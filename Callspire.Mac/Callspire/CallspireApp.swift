import SwiftUI
import AppKit
import Carbon.HIToolbox
import Darwin

@main
struct CallspireApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @StateObject private var state = AppState()

    init() {
        InstanceBroker.claimPrimaryOrForwardAndExit()
    }

    var body: some Scene {
        Window("Callspire", id: "main") {
            MainView()
                .environmentObject(state)
                .frame(minWidth: 900, minHeight: 600)
                .unifiedWindowChrome(lightsCenter: MacTheme.mainLightsCenter)
                .onAppear {
                    appDelegate.attach(state: state)
                }
        }
        .defaultSize(width: 1120, height: 720)
        .windowStyle(.hiddenTitleBar)
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("Settings…") { state.openSettings() }.keyboardShortcut(",", modifiers: .command)
            }
            CommandGroup(after: .windowArrangement) {
                Button("Logs") { state.openLogs() }.keyboardShortcut("l", modifiers: [.command, .shift])
            }
            CommandGroup(replacing: .newItem) { }
        }

        // Active call — a separate small window like WPF CallWindow. Opened/closed from AppState.
        Window("Active Call", id: "call") {
            Group {
                if let call = state.call {
                    CallView(info: call)
                        .environmentObject(state)
                } else {
                    Color.clear.frame(width: 380, height: 560)
                }
            }
            .unifiedWindowChrome(lightsCenter: MacTheme.callLightsCenter) { win in
                state.callWindow = win
                // Single-surface window: backdrop must match the panel, not the chrome gap colour.
                win.isOpaque = false
                win.backgroundColor = .clear
                // Content is flexible so it can fill the title-bar band; keep the window itself fixed-size.
                win.styleMask.remove(.resizable)
            }
        }
        .windowStyle(.hiddenTitleBar)
        .windowResizability(.contentSize)
        .defaultPosition(.topTrailing)

        Window("Settings", id: "settings") {
            SettingsView()
                .environmentObject(state)
                .frame(minWidth: 860, minHeight: 600)
                .unifiedWindowChrome(lightsCenter: MacTheme.sidebarLightsCenter)
        }
        .defaultSize(width: 980, height: 680)
        .windowStyle(.hiddenTitleBar)

        Window("Logs", id: "logs") {
            LogView()
                .environmentObject(state)
                .frame(minWidth: 640, minHeight: 400)
                .unifiedWindowChrome(lightsCenter: MacTheme.singlePanelLightsCenter)
        }
        .defaultSize(width: 860, height: 520)
        .windowStyle(.hiddenTitleBar)
    }
}

/// Gives SwiftUI content access to its hosting NSWindow (used to close the call window programmatically).
struct WindowAccessor: NSViewRepresentable {
    var onWindow: (NSWindow) -> Void
    func makeNSView(context: Context) -> NSView {
        let v = NSView()
        DispatchQueue.main.async { if let w = v.window { onWindow(w) } }
        return v
    }
    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async { if let w = nsView.window { onWindow(w) } }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    weak var state: AppState?
    private var pendingOpenUrls: [URL] = []
    private var sleepToken: NSObjectProtocol?
    private var wakeToken: NSObjectProtocol?

    @MainActor
    func attach(state: AppState) {
        self.state = state
        state.start()
        let queued = pendingOpenUrls
        pendingOpenUrls.removeAll()
        for url in queued { state.handleUrl(url) }
    }

    func applicationWillFinishLaunching(_ notification: Notification) {
        guard !InstanceBroker.isSecondaryForwarder else { return }
        UrlSchemeRegistrar.registerCallspireScheme()
        NSAppleEventManager.shared().setEventHandler(
            self,
            andSelector: #selector(handleGetUrlEvent(_:withReplyEvent:)),
            forEventClass: AEEventClass(kInternetEventClass),
            andEventID: AEEventID(kAEGetURL)
        )
        InstanceBroker.startServer { [weak self] urlString in
            Task { @MainActor in
                guard let url = URL(string: urlString) else { return }
                if let state = self?.state {
                    state.handleUrl(url)
                } else {
                    self?.pendingOpenUrls.append(url)
                }
            }
        }
    }

    @objc private func handleGetUrlEvent(_ event: NSAppleEventDescriptor, withReplyEvent _: NSAppleEventDescriptor) {
        guard let raw = event.paramDescriptor(forKeyword: AEKeyword(keyDirectObject))?.stringValue,
              let url = URL(string: raw) else { return }
        deliverOpenUrls([url])
    }

    private func deliverOpenUrls(_ urls: [URL]) {
        if InstanceBroker.isSecondaryForwarder {
            InstanceBroker.forwardOpenUrlsAndExit(urls)
            return
        }
        Task { @MainActor in
            guard let state else {
                pendingOpenUrls.append(contentsOf: urls)
                return
            }
            for url in urls { state.handleUrl(url) }
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        if InstanceBroker.isSecondaryForwarder {
            NSApp.setActivationPolicy(.prohibited)
            let myPid = ProcessInfo.processInfo.processIdentifier
            for app in NSRunningApplication.runningApplications(withBundleIdentifier: "com.callspire.softphone") {
                if app.processIdentifier != myPid {
                    app.activate(options: [.activateAllWindows, .activateIgnoringOtherApps])
                    break
                }
            }
            exit(0)
            return
        }
        NSApp.setActivationPolicy(.regular)
        let argvUrls = InstanceBroker.protocolUrlsFromLaunch().compactMap { URL(string: $0) }
        if !argvUrls.isEmpty { deliverOpenUrls(argvUrls) }
        let nc = NSWorkspace.shared.notificationCenter
        sleepToken = nc.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.state?.notifySleep() }
        }
        wakeToken = nc.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.state?.notifyWake() }
        }
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        deliverOpenUrls(urls)
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { NSApp.windows.first(where: { $0.identifier?.rawValue.hasPrefix("main") == true })?.makeKeyAndOrderFront(nil) }
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationWillTerminate(_ notification: Notification) {
        Task { @MainActor in state?.stop() }
        InstanceBroker.stopServer()
    }
}

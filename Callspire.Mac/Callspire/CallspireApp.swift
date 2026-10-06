import SwiftUI
import AppKit
import Darwin

@main
struct CallspireApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @StateObject private var state = AppState()

    var body: some Scene {
        Window("Callspire", id: "main") {
            MainView()
                .environmentObject(state)
                .frame(minWidth: 900, minHeight: 600)
                .onAppear {
                    appDelegate.state = state
                    state.start()
                }
        }
        .defaultSize(width: 1120, height: 720)
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
            .background(WindowAccessor { win in
                state.callWindow = win
                win.titlebarAppearsTransparent = true
                win.isMovableByWindowBackground = true
            })
        }
        .windowResizability(.contentSize)
        .defaultPosition(.topTrailing)

        Window("Softphone Settings", id: "settings") {
            SettingsView()
                .environmentObject(state)
                .frame(minWidth: 860, minHeight: 600)
        }
        .defaultSize(width: 980, height: 680)

        Window("Logs", id: "logs") {
            LogView()
                .environmentObject(state)
                .frame(minWidth: 640, minHeight: 400)
        }
        .defaultSize(width: 860, height: 520)
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
    private var sleepToken: NSObjectProtocol?
    private var wakeToken: NSObjectProtocol?
    private var lockPath: String { (NSHomeDirectory() as NSString).appendingPathComponent("Library/Application Support/Callspire/app.lock") }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        acquireSingleInstance()
        let nc = NSWorkspace.shared.notificationCenter
        sleepToken = nc.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.state?.notifySleep() }
        }
        wakeToken = nc.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.state?.notifyWake() }
        }
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls { Task { @MainActor in state?.handleUrl(url) } }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { NSApp.windows.first(where: { $0.identifier?.rawValue.hasPrefix("main") == true })?.makeKeyAndOrderFront(nil) }
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationWillTerminate(_ notification: Notification) {
        Task { @MainActor in state?.stop() }
        try? FileManager.default.removeItem(atPath: lockPath)
    }

    private func acquireSingleInstance() {
        let dir = (lockPath as NSString).deletingLastPathComponent
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let fd = open(lockPath, O_CREAT | O_RDWR, 0o600)
        if fd >= 0 {
            if flock(fd, LOCK_EX | LOCK_NB) != 0 {
                // Another instance is running; macOS already routed the URL/reopen to it via Launch Services.
                NSApp.terminate(nil)
            }
        }
    }
}

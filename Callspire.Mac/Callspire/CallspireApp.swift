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
                .frame(minWidth: 980, minHeight: 640)
                .onAppear {
                    appDelegate.state = state
                    state.start()
                }
        }
        .windowStyle(.automatic)
        .defaultSize(width: 1180, height: 720)
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("Settings…") { state.openSettings() }.keyboardShortcut(",", modifiers: .command)
            }
        }

        Window("Call", id: "call") {
            if let call = state.call {
                CallView(info: call)
                    .environmentObject(state)
                    .frame(minWidth: 320, minHeight: 520)
            } else {
                Color.clear
            }
        }
        .windowResizability(.contentSize)

        Settings {
            SettingsView()
                .environmentObject(state)
                .frame(minWidth: 820, minHeight: 560)
        }
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
        if !flag { NSApp.windows.first?.makeKeyAndOrderFront(nil) }
        return true
    }

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
                // Another instance: activate it via the sidecar socket is not available yet — just abort.
                NSApp.terminate(nil)
            }
        }
    }
}

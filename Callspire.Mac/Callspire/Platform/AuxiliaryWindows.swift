import SwiftUI
import AppKit

/// Settings, Logs and the active call are real windows with traffic lights and the unified chrome.
/// SwiftUI `Window` scenes need Ventura, so on the macOS 12 baseline these are plain AppKit windows
/// hosting SwiftUI content. One instance per kind; re-opening brings the existing window forward.
@MainActor
final class AuxiliaryWindows {
    private var settings: NSWindow?
    private var logs: NSWindow?
    private var call: NSWindow?
    private var closeObservers: [ObjectIdentifier: NSObjectProtocol] = [:]

    func showSettings(state: AppState) {
        if let settings {
            settings.makeKeyAndOrderFront(nil)
            return
        }
        let window = makeWindow(
            title: "Settings",
            contentSize: NSSize(width: 980, height: 680),
            minSize: NSSize(width: 860, height: 600),
            resizable: true,
            autosaveName: "CallspireSettings"
        ) {
            SettingsView()
                .environmentObject(state)
                .unifiedWindowChrome(lightsCenter: MacTheme.sidebarLightsCenter)
        } onClose: { [weak self] in
            self?.settings = nil
        }
        settings = window
        window.makeKeyAndOrderFront(nil)
    }

    func showLogs(state: AppState) {
        if let logs {
            logs.makeKeyAndOrderFront(nil)
            return
        }
        let window = makeWindow(
            title: "Logs",
            contentSize: NSSize(width: 860, height: 520),
            minSize: NSSize(width: 640, height: 400),
            resizable: true,
            autosaveName: "CallspireLogs"
        ) {
            LogView()
                .environmentObject(state)
                .unifiedWindowChrome(lightsCenter: MacTheme.singlePanelLightsCenter)
        } onClose: { [weak self] in
            self?.logs = nil
        }
        logs = window
        window.makeKeyAndOrderFront(nil)
    }

    /// Small fixed-size card like the WPF CallWindow; height follows the in-call keypad.
    func showCall(state: AppState) {
        if let call {
            call.makeKeyAndOrderFront(nil)
            return
        }
        let window = makeWindow(
            title: "Active Call",
            contentSize: CallWindowRoot.size(keypadVisible: state.call?.state.isKeypadVisible ?? false),
            minSize: nil,
            resizable: false,
            autosaveName: nil
        ) {
            CallWindowRoot().environmentObject(state)
        } onClose: { [weak self, weak state] in
            self?.call = nil
            state?.callWindowClosedByUser()
        }
        call = window
        if let visible = NSScreen.main?.visibleFrame {
            window.setFrameTopLeftPoint(NSPoint(x: visible.maxX - window.frame.width - 24, y: visible.maxY - 24))
        }
        window.makeKeyAndOrderFront(nil)
    }

    private func makeWindow<Content: View>(
        title: String,
        contentSize: NSSize,
        minSize: NSSize?,
        resizable: Bool,
        autosaveName: String?,
        @ViewBuilder content: () -> Content,
        onClose: @escaping @MainActor () -> Void
    ) -> NSWindow {
        var style: NSWindow.StyleMask = [.titled, .closable, .miniaturizable, .fullSizeContentView]
        if resizable { style.insert(.resizable) }
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: contentSize), styleMask: style, backing: .buffered, defer: false)
        window.title = title
        window.isReleasedWhenClosed = false
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        if let minSize { window.contentMinSize = minSize }
        window.contentView = NSHostingView(rootView: content())
        window.center()
        if let autosaveName {
            // Restores the last frame when one was saved; otherwise the centred frame stays.
            _ = window.setFrameUsingName(autosaveName)
            _ = window.setFrameAutosaveName(autosaveName)
        }
        let token = NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { [weak self, weak window] _ in
            Task { @MainActor in
                guard let self, let window else { return }
                if let token = self.closeObservers.removeValue(forKey: ObjectIdentifier(window)) {
                    NotificationCenter.default.removeObserver(token)
                }
                // Drop the SwiftUI tree so `onDisappear` / `task` cancellation run like a closed scene.
                window.contentView = nil
                onClose()
            }
        }
        closeObservers[ObjectIdentifier(window)] = token
        return window
    }
}

/// Root of the call window: tracks `AppState.call`, resizes with the keypad and hands the NSWindow to the WebRTC audio host.
private struct CallWindowRoot: View {
    @EnvironmentObject var state: AppState
    @State private var window: NSWindow?

    static func size(keypadVisible: Bool) -> NSSize {
        NSSize(width: 380, height: keypadVisible ? 680 : 560)
    }

    private var keypadVisible: Bool { state.call?.state.isKeypadVisible ?? false }

    var body: some View {
        Group {
            if let call = state.call {
                CallView(info: call)
            } else {
                Color.clear
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        // Call window keeps AppKit's default top-left traffic lights (not centred on the card).
        .unifiedWindowChrome(lightsCenter: nil) { win in
            Task { @MainActor in
                window = win
                state.callWindow = win
                state.webRtc.updateAudioHostWindow(main: state.mainWindow, call: win)
            }
        }
        .onChange(of: keypadVisible) { visible in
            guard let window else { return }
            var frame = window.frame
            let target = Self.size(keypadVisible: visible)
            frame.origin.y = frame.maxY - target.height
            frame.size = target
            window.setFrame(frame, display: true, animate: true)
        }
        .onDisappear {
            Task { @MainActor in
                state.callWindow = nil
                state.webRtc.updateAudioHostWindow(main: state.mainWindow, call: nil)
            }
        }
    }
}

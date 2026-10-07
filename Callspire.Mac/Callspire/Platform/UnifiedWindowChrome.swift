import SwiftUI
import AppKit

/// Telegram-style window chrome: no title bar material, content fills the whole window and the
/// traffic lights float over the first panel. Pair with `.windowStyle(.hiddenTitleBar)` on the scene.
///
/// `lightsCenter` (top-left window coordinates) places the close / minimize / zoom cluster exactly
/// where the layout reserves room for it (see `MacTheme.*LightsCenter`). AppKit's default cluster
/// sits at x≈7…61, which is off-centre for a 76pt rail, so the buttons are measured and moved.
struct UnifiedWindowChrome: ViewModifier {
    var movableByBackground = true
    var lightsCenter: CGPoint? = nil
    var onWindow: ((NSWindow) -> Void)? = nil
    @State private var positioner = TrafficLightsPositioner()

    func body(content: Content) -> some View {
        content
            .background(WindowAccessor { win in
                win.styleMask.insert(.fullSizeContentView)
                win.titlebarAppearsTransparent = true
                win.titleVisibility = .hidden
                win.isMovableByWindowBackground = movableByBackground
                // Clear + vibrancy layers show wallpaper tint; opaque gray windowBackgroundColor blocks it.
                win.isOpaque = false
                win.backgroundColor = .clear
                positioner.attach(win, center: lightsCenter)
                onWindow?(win)
            })
    }
}

/// Keeps the standard window buttons centred on a given point. AppKit re-lays the buttons out on
/// resize, activation and full-screen transitions, so the placement is re-applied on those events.
/// All calls happen on the main thread (WindowAccessor + main-queue notifications).
final class TrafficLightsPositioner {
    private weak var window: NSWindow?
    private var center: CGPoint?
    private var observers: [NSObjectProtocol] = []

    deinit {
        observers.forEach { NotificationCenter.default.removeObserver($0) }
    }

    func attach(_ window: NSWindow, center: CGPoint?) {
        if self.window !== window {
            observers.forEach { NotificationCenter.default.removeObserver($0) }
            observers.removeAll()
            self.window = window
            let names: [Notification.Name] = [
                NSWindow.didResizeNotification,
                NSWindow.didBecomeKeyNotification,
                NSWindow.didResignKeyNotification,
                NSWindow.didExitFullScreenNotification,
                NSWindow.didChangeBackingPropertiesNotification,
            ]
            for name in names {
                observers.append(NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in
                    self?.apply()
                })
            }
        }
        self.center = center
        apply()
        // AppKit finishes its own title bar layout one turn later; apply again to win the race.
        DispatchQueue.main.async { [weak self] in self?.apply() }
    }

    private func apply() {
        guard let window, let center,
              !window.styleMask.contains(.fullScreen),
              let close = window.standardWindowButton(.closeButton),
              let mini = window.standardWindowButton(.miniaturizeButton),
              let zoom = window.standardWindowButton(.zoomButton),
              let titlebar = close.superview,
              let container = titlebar.superview else { return }

        let buttons = [close, mini, zoom]
        let buttonHeight = close.frame.height
        let spacing = max(0, mini.frame.minX - close.frame.maxX)
        let clusterWidth = buttons.reduce(CGFloat(0)) { $0 + $1.frame.width } + spacing * 2

        // Title bar band tall enough that the cluster centre lands on `center.y` from the top edge.
        let bandHeight = max(center.y * 2, buttonHeight)
        let size = window.frame.size
        container.frame = NSRect(x: 0, y: size.height - bandHeight, width: size.width, height: bandHeight)
        titlebar.frame = container.bounds

        var x = center.x - clusterWidth / 2
        let y = (bandHeight - buttonHeight) / 2
        for button in buttons {
            button.setFrameOrigin(NSPoint(x: x, y: y))
            x += button.frame.width + spacing
        }
    }
}

/// Outer layout for a floating-panel window: a uniform inset on all four sides, panels reach the
/// top edge so the traffic lights sit on the first panel, and the gaps show the chrome backdrop.
struct FloatingChromeLayout: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(MacTheme.chromeInset)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background {
                VisualEffectBackground(material: .underWindowBackground)
                    .ignoresSafeArea()
            }
            .ignoresSafeArea()
    }
}

extension View {
    func unifiedWindowChrome(
        movableByBackground: Bool = true,
        lightsCenter: CGPoint? = nil,
        onWindow: ((NSWindow) -> Void)? = nil
    ) -> some View {
        modifier(UnifiedWindowChrome(movableByBackground: movableByBackground, lightsCenter: lightsCenter, onWindow: onWindow))
    }

    func floatingChromeLayout() -> some View {
        modifier(FloatingChromeLayout())
    }
}

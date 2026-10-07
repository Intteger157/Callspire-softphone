import SwiftUI
import AppKit

/// Telegram-style window chrome: no title bar material, content fills the whole window and the
/// traffic lights float over the left panel. Pair with `.windowStyle(.hiddenTitleBar)` on the scene.
/// Content that lives under the buttons reserves `MacTheme.trafficLightsInset` at the top.
struct UnifiedWindowChrome: ViewModifier {
    var movableByBackground = true
    var onWindow: ((NSWindow) -> Void)? = nil

    func body(content: Content) -> some View {
        content
            .background(WindowAccessor { win in
                win.styleMask.insert(.fullSizeContentView)
                win.titlebarAppearsTransparent = true
                win.titleVisibility = .hidden
                win.isMovableByWindowBackground = movableByBackground
                win.backgroundColor = NSColor(MacTheme.chromeFill)
                onWindow?(win)
            })
    }
}

/// Outer layout for a floating-panel window: a uniform inset on all four sides, panels reach the
/// top edge so the traffic lights sit on the first panel, and the gaps show the chrome backdrop.
struct FloatingChromeLayout: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(MacTheme.chromeInset)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(MacTheme.chromeFill)
            .ignoresSafeArea()
    }
}

extension View {
    func unifiedWindowChrome(movableByBackground: Bool = true, onWindow: ((NSWindow) -> Void)? = nil) -> some View {
        modifier(UnifiedWindowChrome(movableByBackground: movableByBackground, onWindow: onWindow))
    }

    func floatingChromeLayout() -> some View {
        modifier(FloatingChromeLayout())
    }
}

import SwiftUI
import AppKit

/// Telegram-style window chrome: the title bar is transparent and content extends under it,
/// so the traffic lights sit on the same backdrop as the floating panels.
/// Views reserve `MacTheme.titleBarInset` at the top so controls do not collide with the buttons.
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
                win.backgroundColor = NSColor.windowBackgroundColor
                onWindow?(win)
            })
    }
}

/// Outer layout for a floating-panel window: band for the traffic lights, gaps between panels,
/// neutral chrome backdrop. Apply once at the root of each window's content.
struct FloatingChromeLayout: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(.horizontal, MacTheme.chromeInset)
            .padding(.bottom, MacTheme.chromeInset)
            .padding(.top, MacTheme.titleBarInset)
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

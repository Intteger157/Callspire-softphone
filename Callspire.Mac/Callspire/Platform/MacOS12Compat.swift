import SwiftUI

// MARK: - View (macOS 13+ modifiers with Monterey fallbacks)

extension View {
    /// Hides the default Form/List scroll background on Ventura+; on Monterey the custom background still shows through in most layouts.
    @ViewBuilder
    func callspireHiddenScrollContentBackground() -> some View {
        if #available(macOS 13.0, *) {
            scrollContentBackground(.hidden)
        } else {
            self
        }
    }
}

// MARK: - Scene (default window size / placement require macOS 13)

private struct CallspireDefaultSizeModifier: SceneModifier {
    var width: CGFloat
    var height: CGFloat

    func body(content: Content) -> some Scene {
        if #available(macOS 13.0, *) {
            content.defaultSize(width: width, height: height)
        } else {
            content
        }
    }
}

private struct CallspireWindowResizabilityModifier: SceneModifier {
    func body(content: Content) -> some Scene {
        if #available(macOS 13.0, *) {
            content.windowResizability(.contentSize)
        } else {
            content
        }
    }
}

private struct CallspireDefaultPositionTopTrailingModifier: SceneModifier {
    func body(content: Content) -> some Scene {
        if #available(macOS 13.0, *) {
            content.defaultPosition(.topTrailing)
        } else {
            content
        }
    }
}

extension Scene {
    func callspireDefaultSize(width: CGFloat, height: CGFloat) -> some Scene {
        modifier(CallspireDefaultSizeModifier(width: width, height: height))
    }

    func callspireWindowResizabilityContentSize() -> some Scene {
        modifier(CallspireWindowResizabilityModifier())
    }

    func callspireDefaultPositionTopTrailing() -> some Scene {
        modifier(CallspireDefaultPositionTopTrailingModifier())
    }
}

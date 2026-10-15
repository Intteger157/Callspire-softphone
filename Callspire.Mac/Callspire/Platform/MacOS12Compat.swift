import SwiftUI

// MARK: - View (Ventura+ modifiers with Monterey fallbacks)

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

    /// `textSelection(.enabled)` is Ventura+; on Monterey text remains copyable via standard AppKit selection where supported.
    @ViewBuilder
    func callspireTextSelectionEnabled() -> some View {
        if #available(macOS 13.0, *) {
            textSelection(.enabled)
        } else {
            self
        }
    }
}

/// `LabeledContent` is Ventura+; simple two-column row on Monterey.
struct CallspireLabeledContent<Content: View>: View {
    var title: String
    @ViewBuilder var content: () -> Content

    init(_ title: String, @ViewBuilder content: @escaping () -> Content) {
        self.title = title
        self.content = content
    }

    var body: some View {
        if #available(macOS 13.0, *) {
            LabeledContent(title, content: content)
        } else {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text(title)
                    .frame(minWidth: 120, alignment: .leading)
                    .foregroundStyle(.secondary)
                content()
                Spacer(minLength: 0)
            }
        }
    }
}

// MARK: - Main window root

@ViewBuilder
func callspireMainRoot(state: AppState, appDelegate: AppDelegate) -> some View {
    MainView()
        .environmentObject(state)
        .frame(minWidth: 900, minHeight: 600)
        .background(WindowAccessor { win in
            state.mainWindow = win
            state.webRtc.updateAudioHostWindow(main: win, call: state.callWindow)
        })
        .unifiedWindowChrome(lightsCenter: MacTheme.mainLightsCenter)
        .onAppear {
            appDelegate.attach(state: state)
        }
}

@CommandsBuilder
func callspireAppCommands(state: AppState) -> some Commands {
    CommandGroup(replacing: .appSettings) {
        Button("Settings…") { state.openSettings() }.keyboardShortcut(",", modifiers: .command)
    }
    CommandGroup(after: .windowArrangement) {
        Button("Logs") { state.openLogs() }.keyboardShortcut("l", modifiers: [.command, .shift])
    }
    CommandGroup(replacing: .newItem) { }
}

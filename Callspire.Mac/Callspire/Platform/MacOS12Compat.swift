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

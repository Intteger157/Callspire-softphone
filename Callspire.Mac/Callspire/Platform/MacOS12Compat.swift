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

// MARK: - Main window root (shared by Window and WindowGroup)

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

// MARK: - Scenes

@SceneBuilder
func callspireMontereyScene(state: AppState, appDelegate: AppDelegate) -> some Scene {
    WindowGroup {
        callspireMainRoot(state: state, appDelegate: appDelegate)
    }
    .commands {
        callspireAppCommands(state: state)
    }
}

@available(macOS 13.0, *)
@SceneBuilder
func callspireVenturaScenes(state: AppState, appDelegate: AppDelegate) -> some Scene {
    Window("Callspire", id: "main") {
        callspireMainRoot(state: state, appDelegate: appDelegate)
    }
    .defaultSize(width: 1120, height: 720)
    .windowStyle(.hiddenTitleBar)
    .commands {
        callspireAppCommands(state: state)
    }

    Window("Active Call", id: "call") {
        Group {
            if let call = state.call {
                CallView(info: call)
                    .environmentObject(state)
            } else {
                Color.clear.frame(width: 380, height: 560)
            }
        }
        .unifiedWindowChrome(lightsCenter: nil) { win in
            state.callWindow = win
            state.webRtc.updateAudioHostWindow(main: state.mainWindow, call: win)
            win.isOpaque = false
            win.backgroundColor = .clear
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

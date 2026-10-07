import SwiftUI
import AppKit
import Combine

/// Settings window — System Settings layout: coloured sidebar icons, grouped form,
/// and a single Save / Revert bar for every editable pane.
struct SettingsView: View {
    @EnvironmentObject var state: AppState

    @State private var panel: AppState.SettingsPanel = .connection
    @State private var draft = SettingsDto()
    @State private var baseline = SettingsFields()
    @State private var loaded = false
    @State private var busy = false
    @State private var message: StatusMessage?
    @State private var reloadTask: Task<Void, Never>?

    struct StatusMessage: Equatable { var text: String; var isError: Bool }

    private var isDirty: Bool { draft.asFields() != baseline }

    var body: some View {
        HStack(alignment: .top, spacing: MacTheme.panelGap) {
            FloatingPanel(padding: 6) {
                List(AppState.SettingsPanel.allCases, selection: Binding(get: { panel }, set: { if let v = $0 { panel = v } })) { p in
                    Label {
                        Text(p.title)
                    } icon: {
                        SettingsIcon(symbol: p.filledSymbol, tint: p.tint, size: 28)
                    }
                    .tag(p)
                }
                .listStyle(.sidebar)
                .scrollContentBackground(.hidden)
                .listRowBackground(Color.clear)
                // Traffic lights float over the top of the sidebar panel.
                .padding(.top, MacTheme.trafficLightsInset - 6)
            }
            .frame(minWidth: 200, idealWidth: 220, maxWidth: 248)

            FloatingPanel {
                VStack(spacing: 0) {
                    Text(panel.title)
                        .font(.title2.weight(.semibold))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 18)
                        .padding(.top, 14)
                        .padding(.bottom, 6)

                    if !loaded {
                        VStack(spacing: 14) {
                            if state.connected {
                                ProgressView("Loading settings…")
                            } else {
                                Image(systemName: "exclamationmark.triangle")
                                    .font(.system(size: 36))
                                    .foregroundStyle(.secondary)
                                Text("Callspire.Service is not running")
                                    .font(.headline)
                                Text(state.statusLine)
                                    .font(.callout)
                                    .foregroundStyle(.secondary)
                                    .multilineTextAlignment(.center)
                                Button("Retry") { scheduleReload(restartSidecar: true) }
                                    .keyboardShortcut(.defaultAction)
                            }
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .padding(24)
                    } else {
                        pane
                            .padding(.top, 2)
                    }
                    if loaded && panel != .about {
                        Divider()
                        saveBar
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
        .floatingChromeLayout()
        .task { scheduleReload(restartSidecar: false) }
        .onChange(of: state.connected) { connected in
            if connected && !loaded { scheduleReload(restartSidecar: false) }
        }
        .onChange(of: state.settingsOpenRequest) { _ in
            panel = state.settingsInitialPanel
            scheduleReload(restartSidecar: false)
        }
        .onChange(of: draft.theme) { theme in
            guard loaded, theme != baseline.theme else { return }
            let previous = baseline.theme
            Task {
                let ok = await state.setTheme(theme)
                await MainActor.run {
                    if ok {
                        baseline.theme = theme
                        message = StatusMessage(text: "Appearance saved.", isError: false)
                    } else {
                        draft.theme = previous
                        message = StatusMessage(text: "Could not save appearance.", isError: true)
                    }
                }
            }
        }
        .onReceive(state.$settings.dropFirst()) { fresh in
            guard loaded else { return }
            if !isDirty {
                draft = fresh
                baseline = fresh.asFields()
            } else {
                draft.gatewayEnabled = fresh.gatewayEnabled
                draft.gatewayUrl = fresh.gatewayUrl
                draft.gatewayToken = fresh.gatewayToken
                draft.gatewayExtension = fresh.gatewayExtension
                draft.kommoOAuth = fresh.kommoOAuth
                baseline.gatewayEnabled = fresh.gatewayEnabled
                baseline.gatewayUrl = fresh.gatewayUrl
                baseline.gatewayToken = fresh.gatewayToken
                baseline.gatewayExtension = fresh.gatewayExtension
            }
        }
        .onAppear {
            panel = state.settingsInitialPanel
            state.start()
        }
    }

    @ViewBuilder
    private var pane: some View {
        switch panel {
        case .connection: ConnectionPanel(draft: $draft, info: state.settings, busy: busy, onTest: test)
        case .audio: AudioPanel(draft: $draft, info: state.settings)
        case .general: GeneralPanel(draft: $draft)
        case .appearance: AppearancePanel(draft: $draft, info: state.settings)
        case .advanced: AdvancedPanel(draft: $draft, info: state.settings)
        case .integrations: IntegrationsPanel(draft: $draft, info: state.settings, onMessage: { message = $0 })
        case .about: AboutPanel(info: state.settings)
        }
    }

    private var saveBar: some View {
        HStack(spacing: 10) {
            if let message {
                Image(systemName: message.isError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                    .foregroundStyle(message.isError ? Color.red : Color.green)
                Text(message.text)
                    .font(.callout)
                    .lineLimit(2)
                    .textSelection(.enabled)
            } else if isDirty {
                Image(systemName: "circle.fill")
                    .font(.system(size: 7))
                    .foregroundStyle(.orange)
                Text("Unsaved changes").font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            if busy { ProgressView().controlSize(.small) }
            Button("Revert") { revert() }
                .disabled(!isDirty || busy)
            Button("Save") { save() }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut("s", modifiers: .command)
                .disabled(!isDirty || busy)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
        .background { VisualEffectBackground(material: .headerView) }
    }

    // MARK: - Actions

    private func scheduleReload(restartSidecar: Bool) {
        reloadTask?.cancel()
        reloadTask = Task { await reload(restartSidecar: restartSidecar) }
    }

    private func reload(restartSidecar: Bool) async {
        loaded = false
        message = nil
        if restartSidecar {
            await state.restartSidecar()
        } else {
            state.start()
        }
        if !state.connected {
            for _ in 0..<40 where !state.connected {
                if Task.isCancelled { return }
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        if Task.isCancelled { return }
        if let s = await state.loadSettings() {
            draft = s
            baseline = s.asFields()
            loaded = true
        }
    }

    private func revert() {
        draft = state.settings
        baseline = state.settings.asFields()
        message = nil
    }

    private func save() {
        busy = true
        message = nil
        Task {
            let result = await state.saveSettings(draft)
            busy = false
            if let err = result.error {
                message = StatusMessage(text: err, isError: true)
            } else {
                let text = result.reconnecting ? "Saved. Reconnecting…" : "Settings saved."
                message = StatusMessage(text: text, isError: false)
                if let s = await state.loadSettings() { draft = s; baseline = s.asFields() }
            }
        }
    }

    private func test(secondary: Bool) {
        busy = true
        message = nil
        Task {
            let r = await state.testConnection(secondary: secondary, draft: draft)
            busy = false
            if let r { message = StatusMessage(text: r.statusText, isError: r.isError) }
        }
    }
}

// MARK: - Sidebar / header chrome

extension AppState.SettingsPanel {
    var filledSymbol: String {
        switch self {
        case .connection: return "network"
        case .audio: return "speaker.wave.2.fill"
        case .general: return "gearshape.fill"
        case .appearance: return "paintbrush.fill"
        case .advanced: return "slider.horizontal.3"
        case .integrations: return "link"
        case .about: return "info.circle.fill"
        }
    }
    var tint: Color {
        switch self {
        case .connection: return .blue
        case .audio: return .pink
        case .general: return .gray
        case .appearance: return .indigo
        case .advanced: return .gray
        case .integrations: return .green
        case .about: return .gray
        }
    }
    var subtitle: String {
        switch self {
        case .connection: return "Your SIP or WebRTC account and an optional second line."
        case .audio: return "Microphone, speaker, call codec and ringtone."
        case .general: return "Call recording and where recordings are stored."
        case .appearance: return "Light, dark or follow the system."
        case .advanced: return "Diagnostics and log files."
        case .integrations: return "Kommo CRM and Callspire PBX Gateway."
        case .about: return "Version and updates."
        }
    }
}

/// System Settings–style white glyph on a coloured rounded square.
struct SettingsIcon: View {
    let symbol: String
    let tint: Color
    var size: CGFloat = 20
    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: size * 0.55, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(tint.gradient, in: RoundedRectangle(cornerRadius: size * 0.25, style: .continuous))
    }
}


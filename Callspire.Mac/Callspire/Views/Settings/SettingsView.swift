import SwiftUI
import AppKit
import Combine

/// Settings window — WPF SettingsWindow parity (Connection / Audio / General / Appearance / Advanced / Integrations / About).
/// The editor works on a local `draft`; snapshots pushed by the sidecar only replace it while nothing is dirty
/// (gateway fields are merged regardless because Authorize / Clear mutate them server-side).
struct SettingsView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss

    @State private var panel: AppState.SettingsPanel = .connection
    @State private var draft = SettingsDto()
    @State private var baseline = SettingsFields()
    @State private var loaded = false
    @State private var busy = false
    @State private var message: StatusMessage?

    struct StatusMessage: Equatable { var text: String; var isError: Bool }

    private var isDirty: Bool { draft.asFields() != baseline }

    var body: some View {
        NavigationSplitView {
            VStack(spacing: 0) {
                List(AppState.SettingsPanel.allCases, selection: Binding(get: { panel }, set: { if let v = $0 { panel = v } })) { p in
                    Label(p.title, systemImage: p.symbol).tag(p)
                }
                .listStyle(.sidebar)
                Divider()
                HStack {
                    Button("Close") { closeWindow() }
                        .buttonStyle(.plain).foregroundStyle(.red)
                    Spacer()
                }
                .padding(12)
            }
            .navigationSplitViewColumnWidth(min: 170, ideal: 190, max: 220)
        } detail: {
            ScrollView {
                Group {
                    if !loaded {
                        HStack { ProgressView().controlSize(.small); Text("Loading settings…").foregroundStyle(.secondary) }
                            .frame(maxWidth: .infinity, minHeight: 200)
                    } else {
                        switch panel {
                        case .connection: ConnectionPanel(draft: $draft, info: state.settings, busy: busy, onSave: save, onTest: test)
                        case .audio: AudioPanel(draft: $draft, info: state.settings, busy: busy, onSave: save)
                        case .general: GeneralPanel(draft: $draft, busy: busy, onSave: save)
                        case .appearance: AppearancePanel(draft: $draft, info: state.settings, busy: busy, onSave: save)
                        case .advanced: AdvancedPanel(draft: $draft, info: state.settings, busy: busy, onSave: save)
                        case .integrations: IntegrationsPanel(draft: $draft, info: state.settings, busy: busy, onSave: save, onMessage: { message = $0 })
                        case .about: AboutPanel(info: state.settings)
                        }
                    }
                }
                .padding(24)
                .frame(maxWidth: 900, alignment: .leading)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .safeAreaInset(edge: .bottom) {
                if let message {
                    HStack(spacing: 8) {
                        Image(systemName: message.isError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                            .foregroundStyle(message.isError ? Color.red : Color.green)
                        Text(message.text).font(.callout).lineLimit(2)
                        Spacer()
                        Button { self.message = nil } label: { Image(systemName: "xmark") }.buttonStyle(.plain)
                    }
                    .padding(10)
                    .background(.bar)
                }
            }
            .navigationTitle(panel.title)
        }
        .navigationTitle("Softphone Settings")
        .task { await reload() }
        .onChange(of: state.settingsOpenRequest) { _ in
            panel = state.settingsInitialPanel
            Task { await reload() }
        }
        .onReceive(state.$settings.dropFirst()) { fresh in
            guard loaded else { return }
            if !isDirty {
                draft = fresh
                baseline = fresh.asFields()
            } else {
                // Authorize / Clear on the gateway change these without going through the editor.
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
        .onAppear { panel = state.settingsInitialPanel }
    }

    // MARK: actions

    private func reload() async {
        if let s = await state.loadSettings() {
            draft = s
            baseline = s.asFields()
            loaded = true
        }
    }

    private func save(_ successText: String) {
        busy = true
        message = nil
        Task {
            let err = await state.saveSettings(draft)
            busy = false
            if let err {
                message = StatusMessage(text: err, isError: true)
            } else {
                message = StatusMessage(text: successText, isError: false)
                if let s = await state.loadSettings() { draft = s; baseline = s.asFields() }
            }
        }
    }

    private func test(secondary: Bool) {
        busy = true
        Task {
            let r = await state.testConnection(secondary: secondary, draft: draft)
            busy = false
            if let r { message = StatusMessage(text: r.statusText, isError: r.isError) }
        }
    }

    private func closeWindow() {
        if isDirty {
            let alert = NSAlert()
            alert.messageText = "Discard unsaved changes?"
            alert.informativeText = "You have edited settings that were not saved."
            alert.addButton(withTitle: "Discard")
            alert.addButton(withTitle: "Cancel")
            if alert.runModal() != .alertFirstButtonReturn { return }
        }
        dismiss()
        NSApp.keyWindow?.close()
    }
}
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var state: AppState
    @State private var panel: Panel = .connection
    @State private var saveError: String?

    enum Panel: String, CaseIterable, Identifiable, Hashable {
        case connection, secondary, audio, kommo, appearance, advanced, about
        var id: String { rawValue }
        var title: String {
            switch self {
            case .connection: return "Connection"
            case .secondary: return "Second line"
            case .audio: return "Audio"
            case .kommo: return "Kommo & Gateway"
            case .appearance: return "Appearance"
            case .advanced: return "Advanced"
            case .about: return "About"
            }
        }
        var symbol: String {
            switch self {
            case .connection: return "cable.connector"
            case .secondary: return "phone.fill"
            case .audio: return "speaker.wave.2.fill"
            case .kommo: return "cloud.fill"
            case .appearance: return "paintbrush.fill"
            case .advanced: return "gearshape.fill"
            case .about: return "info.circle.fill"
            }
        }
    }

    var body: some View {
        NavigationSplitView {
            List(Panel.allCases, selection: Binding(get: { panel }, set: { if let v = $0 { panel = v } })) { p in
                Label(p.title, systemImage: p.symbol).tag(p)
            }
            .navigationSplitViewColumnWidth(200)
        } detail: {
            ScrollView {
                Group {
                    switch panel {
                    case .connection: ConnectionPanel(isSecondary: false)
                    case .secondary: ConnectionPanel(isSecondary: true)
                    case .audio: AudioPanel()
                    case .kommo: KommoPanel()
                    case .appearance: AppearancePanel()
                    case .advanced: AdvancedPanel()
                    case .about: AboutPanel()
                    }
                }
                .padding()
            }
            .safeAreaInset(edge: .bottom) {
                HStack {
                    if let err = saveError ?? (state.settings.statusIsError ? state.settings.statusText : nil) {
                        Text(err).foregroundStyle(.red).font(.caption)
                    } else if !state.settings.statusText.isEmpty {
                        Text(state.settings.statusText).foregroundStyle(.green).font(.caption)
                    }
                    Spacer()
                    Button("Save") {
                        Task { saveError = await state.saveSettings() }
                    }
                    .keyboardShortcut(.defaultAction)
                    .disabled(state.settings.isBusy)
                }
                .padding()
                .background(.bar)
            }
        }
        .onAppear { state.openSettings() }
    }
}

struct FieldLabel: View {
    let title: String
    var body: some View { Text(title).font(.caption.weight(.semibold)).foregroundStyle(.secondary) }
}

struct ConnectionPanel: View {
    @EnvironmentObject var state: AppState
    var isSecondary: Bool
    private var s: Binding<SettingsDto> { $state.settings }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(isSecondary ? "Second line" : "Main connection").font(.title2.bold())
            HStack {
                VStack(alignment: .leading) { FieldLabel(title: "Display name"); TextField("", text: isSecondary ? s.secondaryName : s.mainName) }
                VStack(alignment: .leading) {
                    FieldLabel(title: "Transport")
                    Picker("", selection: isSecondary ? s.secondaryTransport : s.mainTransport) {
                        ForEach(state.settings.transportOptions) { Text($0.label).tag($0.key) }
                    }
                }
            }
            let isWebRtc = (isSecondary ? state.settings.secondaryTransport : state.settings.mainTransport).lowercased() == "webrtc"
            if isWebRtc {
                FieldLabel(title: "WebSocket URI"); TextField("wss://…", text: isSecondary ? s.secondaryWsUri : s.mainWsUri)
                HStack {
                    VStack(alignment: .leading) { FieldLabel(title: "Username"); TextField("", text: isSecondary ? s.secondaryWebRtcUsername : s.mainWebRtcUsername) }
                    VStack(alignment: .leading) { FieldLabel(title: "Password"); SecureField("", text: isSecondary ? s.secondaryWebRtcPassword : s.mainWebRtcPassword) }
                }
                FieldLabel(title: "TURN URI"); TextField("", text: isSecondary ? s.secondaryTurnUri : s.mainTurnUri)
                HStack {
                    VStack(alignment: .leading) { FieldLabel(title: "TURN username"); TextField("", text: isSecondary ? s.secondaryTurnUsername : s.mainTurnUsername) }
                    VStack(alignment: .leading) { FieldLabel(title: "TURN password"); SecureField("", text: isSecondary ? s.secondaryTurnPassword : s.mainTurnPassword) }
                }
            } else {
                HStack {
                    VStack(alignment: .leading) { FieldLabel(title: "Server"); TextField("", text: isSecondary ? s.secondaryServer : s.mainServer) }
                    if isSecondary {
                        VStack(alignment: .leading) { FieldLabel(title: "RTP server"); TextField("", text: s.secondaryRtpServer) }
                    } else {
                        VStack(alignment: .leading) { FieldLabel(title: "Port"); TextField("", text: s.mainPort) }
                    }
                }
                HStack {
                    VStack(alignment: .leading) { FieldLabel(title: "Username"); TextField("", text: isSecondary ? s.secondaryUsername : s.mainUsername) }
                    VStack(alignment: .leading) { FieldLabel(title: "Password"); SecureField("", text: isSecondary ? s.secondaryPassword : s.mainPassword) }
                }
                Toggle("TLS", isOn: isSecondary ? s.secondaryUseTls : s.mainUseTls)
                Toggle("SRTP", isOn: isSecondary ? s.secondaryUseSrtp : s.mainUseSrtp)
            }
            HStack {
                Button("Test reachability") {
                    Task {
                        struct T: Codable { var secondary: Bool; var fields: SettingsFields }
                        try? await state.ipc.requestVoid("testConnection", params: T(secondary: isSecondary, fields: state.settings.asFields()))
                    }
                }
                Spacer()
            }
        }
        .textFieldStyle(.roundedBorder)
    }
}

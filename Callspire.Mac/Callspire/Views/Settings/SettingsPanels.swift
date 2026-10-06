import SwiftUI
import AppKit

// MARK: - Form building blocks

/// Label on the left, visible bordered field on the right (with a placeholder so empty fields are obvious).
struct FormField: View {
    let title: String
    @Binding var text: String
    var prompt: String = ""
    var secure: Bool = false
    var width: CGFloat = 280

    var body: some View {
        LabeledContent(title) {
            Group {
                if secure {
                    SecureField("", text: $text, prompt: Text(prompt))
                } else {
                    TextField("", text: $text, prompt: Text(prompt))
                }
            }
            .textFieldStyle(.roundedBorder)
            .labelsHidden()
            .frame(width: width)
        }
    }
}

/// Section header with an optional status pill on the trailing edge.
struct SectionTitle: View {
    let title: String
    var status: (text: String, tone: StatusDot.Tone)? = nil
    var body: some View {
        HStack {
            Text(title)
            Spacer()
            if let status { StatusPill(text: status.text, tone: status.tone) }
        }
    }
}

// MARK: - Connection

struct ConnectionPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto
    let busy: Bool
    let onTest: (Bool) -> Void
    @State private var showSecondary = false

    var body: some View {
        Form {
            lineSections(isSecondary: false)

            if showSecondary || hasSecondary {
                lineSections(isSecondary: true)
            } else {
                Section {
                    Button { showSecondary = true } label: {
                        Label("Add a Second Line…", systemImage: "plus.circle")
                    }
                    .buttonStyle(.link)
                } footer: {
                    Text("Use a second SIP or WebRTC account, for example a separate trunk.")
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { showSecondary = hasSecondary }
    }

    private var hasSecondary: Bool {
        !draft.secondaryName.isEmpty || !draft.secondaryServer.isEmpty || !draft.secondaryWsUri.isEmpty || !draft.secondaryUsername.isEmpty
    }

    @ViewBuilder
    private func lineSections(isSecondary: Bool) -> some View {
        let status = isSecondary ? state.main.secondary : state.main.main
        let transport = isSecondary ? $draft.secondaryTransport : $draft.mainTransport
        let isWebRtc = transport.wrappedValue.lowercased() == "webrtc"

        Section {
            FormField(title: "Display name", text: isSecondary ? $draft.secondaryName : $draft.mainName,
                      prompt: isSecondary ? "Second line" : "Office")
            LabeledContent("Transport") {
                Picker("", selection: transport) {
                    Text("SIP").tag(transportKey("sip"))
                    Text("WebRTC").tag(transportKey("webrtc"))
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 180)
            }
        } header: {
            SectionTitle(title: isSecondary ? "Second Line" : "Main Line", status: lineStatus(status))
        }

        Section {
            if isWebRtc {
                FormField(title: "WebSocket URI", text: isSecondary ? $draft.secondaryWsUri : $draft.mainWsUri,
                          prompt: "wss://pbx.example.com/webrtc")
                FormField(title: "Username", text: isSecondary ? $draft.secondaryWebRtcUsername : $draft.mainWebRtcUsername,
                          prompt: "Extension or login")
                FormField(title: "Password", text: isSecondary ? $draft.secondaryWebRtcPassword : $draft.mainWebRtcPassword,
                          prompt: "Required", secure: true)
            } else {
                FormField(title: "Server", text: isSecondary ? $draft.secondaryServer : $draft.mainServer,
                          prompt: "pbx.example.com")
                if isSecondary {
                    FormField(title: "RTP server", text: $draft.secondaryRtpServer, prompt: "Optional")
                } else {
                    FormField(title: "Port", text: $draft.mainPort, prompt: "5060", width: 90)
                }
                FormField(title: "Username", text: isSecondary ? $draft.secondaryUsername : $draft.mainUsername,
                          prompt: "Extension or login")
                FormField(title: "Password", text: isSecondary ? $draft.secondaryPassword : $draft.mainPassword,
                          prompt: "Required", secure: true)
            }
            LabeledContent("Reachability") {
                Button("Test Connection") { onTest(isSecondary) }
                    .disabled(busy)
            }
        } header: {
            Text("Account")
        } footer: {
            Text(isWebRtc
                 ? "WebRTC registers through the PBX WebSocket endpoint. Changes take effect after Save."
                 : "Standard SIP registration over UDP/TCP or TLS. Changes take effect after Save.")
        }

        if isWebRtc {
            Section {
                FormField(title: "TURN URI", text: isSecondary ? $draft.secondaryTurnUri : $draft.mainTurnUri,
                          prompt: "turn:turn.example.com:3478")
                FormField(title: "Username", text: isSecondary ? $draft.secondaryTurnUsername : $draft.mainTurnUsername,
                          prompt: "Optional")
                FormField(title: "Password", text: isSecondary ? $draft.secondaryTurnPassword : $draft.mainTurnPassword,
                          prompt: "Optional", secure: true)
            } header: {
                Text("TURN Server (Optional)")
            } footer: {
                Text("Only needed when calls connect but there is no audio behind strict NAT.")
            }
        } else {
            Section("Security") {
                Toggle("Encrypt signalling (TLS)", isOn: isSecondary ? $draft.secondaryUseTls : $draft.mainUseTls)
                Toggle("Encrypt audio (SRTP)", isOn: isSecondary ? $draft.secondaryUseSrtp : $draft.mainUseSrtp)
            }
        }

        if isSecondary {
            Section {
                Button("Remove Second Line", role: .destructive) { clearSecondary() }
            } footer: {
                Text("Removal is applied when you click Save.")
            }
        }
    }

    private func lineStatus(_ c: ConnectionStatus) -> (text: String, tone: StatusDot.Tone)? {
        let text = c.text.isEmpty ? (c.isOnline ? "Connected" : "Not connected") : c.text
        let tone: StatusDot.Tone = c.isError ? .error : (c.isOnline ? .online : .offline)
        return (text, tone)
    }

    /// Server-side keys are "Sip" / "WebRtc"; keep whatever casing the sidecar reports.
    private func transportKey(_ kind: String) -> String {
        info.transportOptions.first(where: { $0.key.lowercased() == kind })?.key ?? (kind == "sip" ? "Sip" : "WebRtc")
    }

    private func clearSecondary() {
        draft.secondaryName = ""; draft.secondaryServer = ""; draft.secondaryRtpServer = ""
        draft.secondaryUsername = ""; draft.secondaryPassword = ""
        draft.secondaryWsUri = ""; draft.secondaryWebRtcUsername = ""; draft.secondaryWebRtcPassword = ""
        draft.secondaryTurnUri = ""; draft.secondaryTurnUsername = ""; draft.secondaryTurnPassword = ""
        draft.secondaryUseTls = false; draft.secondaryUseSrtp = false
        showSecondary = false
    }
}

// MARK: - Audio

struct AudioPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto
    @State private var previewing = false

    var body: some View {
        Form {
            Section {
                Picker("Microphone", selection: $draft.selectedMicrophone) {
                    Text("System Default").tag(-1)
                    ForEach(info.microphones.filter { $0.index >= 0 }) { Text($0.name).tag($0.index) }
                }
                Picker("Speaker", selection: $draft.selectedSpeaker) {
                    Text("System Default").tag(-1)
                    ForEach(info.speakers.filter { $0.index >= 0 }) { Text($0.name).tag($0.index) }
                }
                Toggle("Echo cancellation", isOn: $draft.echoCancellation)
            } header: {
                HStack {
                    Text("Devices")
                    Spacer()
                    Button { Task { _ = await state.refreshAudioDevices() } } label: {
                        Label("Refresh", systemImage: "arrow.clockwise")
                    }
                    .buttonStyle(.borderless)
                    .controlSize(.small)
                }
            } footer: {
                Text(info.audioBackendInfo.isEmpty ? "Echo cancellation applies to SIP calls." : info.audioBackendInfo)
            }

            Section {
                Picker("Codec", selection: $draft.sipCodec) {
                    ForEach(info.sipCodecOptions) { Text($0.label).tag($0.key) }
                }
                Picker("Sample rate", selection: $draft.sipSampleRate) {
                    ForEach(info.sipSampleRateOptions, id: \.self) { Text(sampleRateLabel($0)).tag($0) }
                }
                if draft.sipCodec.lowercased() == "opus" {
                    LabeledContent("Opus bitrate") {
                        TextField("", value: $draft.sipOpusBitrate, format: .number, prompt: Text("64000"))
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 100)
                    }
                }
            } header: {
                Text("Call Quality")
            } footer: {
                Text("G.711 works with every PBX. Choose Opus or G.722 only if your PBX supports it.")
            }

            Section("Ringtone") {
                Picker("Sound", selection: $draft.ringtoneWav) {
                    Text("Ringtone file").tag(true)
                    Text("Classic tone").tag(false)
                }
                LabeledContent("Volume") {
                    HStack {
                        Image(systemName: "speaker.fill").foregroundStyle(.secondary)
                        Slider(value: $draft.ringtoneVolume, in: 0...1, step: 0.05)
                            .frame(width: 180)
                        Image(systemName: "speaker.wave.3.fill").foregroundStyle(.secondary)
                    }
                }
                LabeledContent("Preview") {
                    Button {
                        if previewing { state.stopRingtone() } else { state.previewRingtone(draft: draft) }
                        previewing.toggle()
                    } label: {
                        Label(previewing ? "Stop" : "Play", systemImage: previewing ? "stop.fill" : "play.fill")
                    }
                }
            }
        }
        .formStyle(.grouped)
        .onDisappear { if previewing { state.stopRingtone() } }
    }

    private func sampleRateLabel(_ hz: Int) -> String {
        switch hz {
        case 8000: return "8 kHz (Standard)"
        case 16000: return "16 kHz (Wideband)"
        case 48000: return "48 kHz (Opus)"
        default: return "\(hz) Hz"
        }
    }
}

// MARK: - General

struct GeneralPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto

    var body: some View {
        Form {
            Section {
                Toggle("Record calls automatically", isOn: $draft.callRecording)
                LabeledContent("Recordings") {
                    Button("Show in Finder") { state.openRecordingsFolder() }
                }
            } header: {
                Text("Call Recording")
            } footer: {
                Text("Available for WebRTC calls. Recordings are saved on this Mac.")
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - Appearance

struct AppearancePanel: View {
    @Binding var draft: SettingsDto
    let info: SettingsDto

    var body: some View {
        Form {
            Section {
                LabeledContent("Appearance") {
                    Picker("", selection: $draft.theme) {
                        Text("System").tag("system")
                        Text("Light").tag("light")
                        Text("Dark").tag("dark")
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .frame(width: 240)
                }
            } footer: {
                Text("Applied immediately. System follows the setting in macOS System Settings.")
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - Advanced

struct AdvancedPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto

    var body: some View {
        Form {
            Section {
                Toggle("Detailed WebRTC logging", isOn: $draft.webRtcDebug)
            } header: {
                Text("Diagnostics")
            } footer: {
                Text("Turn on when support asks for logs. Leave off for everyday use.")
            }

            Section("Logs") {
                LabeledContent("Live log") {
                    Button("Open Log Window") { state.openLogs() }
                }
                LabeledContent("Log files") {
                    Button("Show in Finder") { state.openLogsFolder() }
                }
                if !info.settingsFile.isEmpty {
                    LabeledContent("Settings file") {
                        Text(info.settingsFile)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .textSelection(.enabled)
                    }
                }
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - Integrations

struct IntegrationsPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto
    let onMessage: (SettingsView.StatusMessage) -> Void
    @State private var authorizing = false
    @State private var gatewayBusy = false

    private var isGatewaySource: Bool { draft.kommoSource.lowercased() == "gateway" }

    var body: some View {
        Form {
            gatewaySection
            kommoSections
        }
        .formStyle(.grouped)
    }

    // MARK: PBX Gateway

    private var gatewayStatus: (text: String, tone: StatusDot.Tone)? {
        guard draft.gatewayEnabled else { return nil }
        let text = state.main.gatewayStatusText.isEmpty
            ? (state.main.gatewayConnected ? "Connected" : "Not connected")
            : state.main.gatewayStatusText
        return (text, state.main.gatewayConnected ? .online : (draft.gatewayEnabled ? .warning : .offline))
    }

    @ViewBuilder
    private var gatewaySection: some View {
        Section {
            Toggle("Use Callspire PBX Gateway", isOn: $draft.gatewayEnabled)
            if draft.gatewayEnabled {
                FormField(title: "Service URL", text: $draft.gatewayUrl, prompt: "https://pbx.example.com/tool")
                FormField(title: "Extension", text: $draft.gatewayExtension, prompt: "204", width: 120)
                LabeledContent("Sign in") {
                    HStack {
                        Button {
                            gatewayBusy = true
                            Task {
                                let err = await state.gatewayAuthorize(url: draft.gatewayUrl, ext: draft.gatewayExtension)
                                gatewayBusy = false
                                onMessage(.init(text: err ?? "Finish signing in in your browser — Callspire connects automatically.",
                                                isError: err != nil))
                            }
                        } label: {
                            if gatewayBusy { ProgressView().controlSize(.small) }
                            Text(state.main.gatewayConnected ? "Re-authorize…" : "Authorize in Browser…")
                        }
                        .disabled(gatewayBusy || draft.gatewayUrl.trimmingCharacters(in: .whitespaces).isEmpty)

                        Button("Disconnect", role: .destructive) {
                            Task {
                                await state.gatewayClear()
                                onMessage(.init(text: "PBX Gateway disconnected.", isError: false))
                            }
                        }
                        .disabled(gatewayBusy || !state.main.gatewayConnected)
                    }
                }
            }
        } header: {
            SectionTitle(title: "PBX Gateway", status: gatewayStatus)
        } footer: {
            Text("Gives Callspire call history, recordings, outbound Caller ID and click-to-call from your MikoPBX.")
        }
    }

    // MARK: Kommo

    private var kommoStatus: (text: String, tone: StatusDot.Tone)? {
        guard draft.kommoEnabled && state.main.amoCrmConfigured else { return nil }
        let m = state.main
        let text = m.amoCrmStatusText.isEmpty ? (m.amoCrmOnline ? "Connected" : "Offline") : m.amoCrmStatusText
        return (text, m.amoCrmOnline ? .online : .warning)
    }

    @ViewBuilder
    private var kommoSections: some View {
        Section {
            Toggle("Send calls to Kommo", isOn: $draft.kommoEnabled)
            if draft.kommoEnabled {
                LabeledContent("Connect via") {
                    Picker("", selection: $draft.kommoSource) {
                        Text("PBX Gateway").tag("gateway")
                        Text("This Mac").tag("local")
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .frame(width: 220)
                }
            }
        } header: {
            SectionTitle(title: "Kommo CRM", status: kommoStatus)
        } footer: {
            if draft.kommoEnabled {
                Text(isGatewaySource
                     ? "Recommended. The PBX Gateway matches calls and uploads recordings — nothing else to set up here."
                     : "This Mac talks to Kommo directly and uploads its own recordings.")
            } else {
                Text("Attach call notes and recordings to Kommo contacts and leads.")
            }
        }

        if draft.kommoEnabled && !isGatewaySource {
            Section {
                FormField(title: "Account", text: $draft.kommoSubdomain, prompt: "yourcompany")
                LabeledContent("Sign-in method") {
                    Picker("", selection: $draft.kommoAuthMode) {
                        ForEach(info.kommoAuthOptions) { Text($0.label).tag($0.key) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .frame(width: 260)
                }
                if draft.kommoAuthMode == "oauth" {
                    FormField(title: "Client ID", text: $draft.kommoClientId, prompt: "From Kommo integration")
                    FormField(title: "Client secret", text: $draft.kommoClientSecret, prompt: "Required", secure: true)
                    FormField(title: "Redirect URI", text: $draft.kommoRedirectUri, prompt: "https://…")
                    LabeledContent("Authorization") {
                        HStack {
                            StatusPill(text: info.kommoOAuth.statusText,
                                       tone: info.kommoOAuth.isAuthorized ? .online : .offline)
                            Button {
                                authorizing = true
                                Task {
                                    let err = await state.kommoAuthorize(draft: draft)
                                    authorizing = false
                                    onMessage(.init(text: err ?? "Kommo authorized.", isError: err != nil))
                                }
                            } label: {
                                if authorizing { ProgressView().controlSize(.small) }
                                Text("Authorize…")
                            }
                            .disabled(authorizing || draft.kommoClientId.isEmpty || draft.kommoSubdomain.isEmpty)
                        }
                    }
                } else {
                    FormField(title: "Access token", text: $draft.kommoToken, prompt: "Long-lived token", secure: true)
                }
            } header: {
                Text("Kommo Account")
            } footer: {
                Text("Account is the part before .kommo.com in your Kommo address.")
            }
        }

        if draft.kommoEnabled {
            Section("After Each Call") {
                Toggle("Upload call recordings", isOn: $draft.kommoRecordingUpload)
                Toggle("Ask which lead to attach the call to", isOn: $draft.kommoLeadSelection)
            }
        }
    }
}

// MARK: - About

struct AboutPanel: View {
    @EnvironmentObject var state: AppState
    let info: SettingsDto
    @State private var checking = false
    @State private var result: UpdateCheckResult?

    var body: some View {
        Form {
            Section {
                HStack(spacing: 14) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable()
                        .frame(width: 56, height: 56)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Callspire").font(.title3.weight(.semibold))
                        Text(versionText).foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 4)
            }

            Section {
                LabeledContent("Software update") {
                    HStack {
                        if result?.available ?? info.updateAvailable {
                            Button("Download") { state.openUpdateUrl() }
                                .buttonStyle(.borderedProminent)
                        }
                        Button {
                            checking = true
                            Task { result = await state.checkForUpdates(); checking = false }
                        } label: {
                            if checking { ProgressView().controlSize(.small) }
                            Text("Check Now")
                        }
                        .disabled(checking)
                    }
                }
            } footer: {
                let status = result?.status ?? info.updateStatus
                Text(status.isEmpty ? "Callspire checks for updates once a day at launch." : status)
            }
        }
        .formStyle(.grouped)
    }

    private var versionText: String {
        if !info.versionText.isEmpty { return info.versionText }
        return "Version \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")"
    }
}

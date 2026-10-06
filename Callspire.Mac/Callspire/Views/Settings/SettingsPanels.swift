import SwiftUI
import AppKit

// MARK: - Connection

struct ConnectionPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto
    let busy: Bool
    let onSave: (String) -> Void
    let onTest: (Bool) -> Void
    @State private var showSecondary = false
    @State private var turnExpanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 12) {
                Image(systemName: "antenna.radiowaves.left.and.right").font(.title2).foregroundStyle(Color.accentColor)
                SectionHeader(title: "Connections", subtitle: "Manage your SIP and WebRTC connections")
            }

            connectionCard(isSecondary: false)

            if showSecondary || hasSecondary {
                connectionCard(isSecondary: true)
            } else {
                Button { showSecondary = true } label: {
                    Label("Add additional connection", systemImage: "plus.circle")
                }
                .buttonStyle(.link)
            }
        }
        .onAppear { showSecondary = hasSecondary }
    }

    private var hasSecondary: Bool {
        !draft.secondaryName.isEmpty || !draft.secondaryServer.isEmpty || !draft.secondaryWsUri.isEmpty || !draft.secondaryUsername.isEmpty
    }

    private func connectionCard(isSecondary: Bool) -> some View {
        let status = isSecondary ? state.main.secondary : state.main.main
        let isWebRtc = (isSecondary ? draft.secondaryTransport : draft.mainTransport).lowercased() == "webrtc"
        return Card {
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Image(systemName: isSecondary ? "person.2" : "person.crop.circle").foregroundStyle(Color.accentColor)
                    Text(isSecondary ? "Secondary Connection" : "Primary Connection").font(.headline)
                    Spacer()
                    if status.isConfigured || status.isOnline {
                        HStack(spacing: 6) {
                            StatusDot(tone: status.isError ? .error : (status.isOnline ? .online : .offline))
                            Text(status.isOnline ? "Connected" : status.text).font(.caption)
                                .foregroundStyle(status.isOnline ? Color.green : Color.secondary)
                        }
                    }
                    if isSecondary {
                        Button(role: .destructive) { clearSecondary() } label: { Image(systemName: "trash") }
                            .buttonStyle(.plain).foregroundStyle(.red).help("Remove secondary connection")
                    }
                }

                LabeledField(title: "Connection Name") {
                    TextField(isSecondary ? "Second line" : "mikopbx", text: isSecondary ? $draft.secondaryName : $draft.mainName)
                }

                Text("Pick a transport, then enter the credentials below. The same username/password are used for both SIP and WebRTC registration.")
                    .font(.caption).foregroundStyle(.secondary)
                LabeledField(title: "Transport") {
                    Picker("", selection: isSecondary ? $draft.secondaryTransport : $draft.mainTransport) {
                        ForEach(info.transportOptions.isEmpty ? [Choice(key: "Sip", label: "SIP"), Choice(key: "WebRtc", label: "WebRTC")] : info.transportOptions) {
                            Text($0.key.lowercased() == "webrtc" ? "WebRTC" : "SIP").tag($0.key)
                        }
                    }
                    .pickerStyle(.segmented).labelsHidden()
                }

                if isWebRtc {
                    LabeledField(title: "WebSocket URI") {
                        TextField("wss://pbx.example.com/webrtc", text: isSecondary ? $draft.secondaryWsUri : $draft.mainWsUri)
                    }
                    HStack {
                        Text("WebRTC Status: \(status.text.isEmpty ? "Ready to test" : status.text)").font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("Test Connection") { onTest(isSecondary) }.disabled(busy)
                    }
                    HStack(spacing: 12) {
                        LabeledField(title: "Username (WebRTC)") {
                            TextField("", text: isSecondary ? $draft.secondaryWebRtcUsername : $draft.mainWebRtcUsername)
                        }
                        LabeledField(title: "Password (WebRTC)") {
                            SecureField("", text: isSecondary ? $draft.secondaryWebRtcPassword : $draft.mainWebRtcPassword)
                        }
                    }
                } else {
                    HStack(spacing: 12) {
                        LabeledField(title: "SIP Server") {
                            TextField("pbx.example.com", text: isSecondary ? $draft.secondaryServer : $draft.mainServer)
                        }
                        if isSecondary {
                            LabeledField(title: "RTP Server (optional)") { TextField("", text: $draft.secondaryRtpServer) }
                        } else {
                            LabeledField(title: "Port") { TextField("5060", text: $draft.mainPort).frame(width: 90) }
                        }
                    }
                    HStack {
                        Text("SIP Status: \(status.text.isEmpty ? "Ready to test" : status.text)").font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("Test Connection") { onTest(isSecondary) }.disabled(busy)
                    }
                    HStack(spacing: 12) {
                        LabeledField(title: "Username") { TextField("", text: isSecondary ? $draft.secondaryUsername : $draft.mainUsername) }
                        LabeledField(title: "Password") { SecureField("", text: isSecondary ? $draft.secondaryPassword : $draft.mainPassword) }
                    }
                    HStack(spacing: 20) {
                        Toggle("Use TLS", isOn: isSecondary ? $draft.secondaryUseTls : $draft.mainUseTls)
                        Toggle("Use SRTP", isOn: isSecondary ? $draft.secondaryUseSrtp : $draft.mainUseSrtp)
                    }
                }

                Button { onSave("Settings saved. Reconnecting…") } label: {
                    HStack { if busy { ProgressView().controlSize(.small) }; Text("Save and Connect") }
                }
                .buttonStyle(.borderedProminent)
                .disabled(busy)

                Divider()
                DisclosureGroup(isExpanded: $turnExpanded) {
                    VStack(alignment: .leading, spacing: 10) {
                        LabeledField(title: "TURN URI") {
                            TextField("turn:turn.example.com:3478?transport=udp", text: isSecondary ? $draft.secondaryTurnUri : $draft.mainTurnUri)
                        }
                        HStack(spacing: 12) {
                            LabeledField(title: "TURN username") { TextField("", text: isSecondary ? $draft.secondaryTurnUsername : $draft.mainTurnUsername) }
                            LabeledField(title: "TURN password") { SecureField("", text: isSecondary ? $draft.secondaryTurnPassword : $draft.mainTurnPassword) }
                        }
                    }
                    .padding(.top, 8)
                } label: {
                    HStack(spacing: 8) {
                        Text("Turn settings").font(.subheadline.weight(.semibold))
                        let uri = isSecondary ? draft.secondaryTurnUri : draft.mainTurnUri
                        StatusDot(tone: uri.isEmpty ? .offline : .online)
                        Text(turnStatus(uri)).font(.caption).foregroundStyle(uri.isEmpty ? Color.secondary : Color.green)
                    }
                }
            }
        }
        .textFieldStyle(.roundedBorder)
    }

    private func turnStatus(_ uri: String) -> String {
        if uri.isEmpty { return "Not configured" }
        if let q = uri.range(of: "transport=", options: .caseInsensitive) {
            return "Configured (\(uri[q.upperBound...].prefix(3).uppercased()))"
        }
        return "Configured (UDP)"
    }

    private func clearSecondary() {
        draft.secondaryName = ""; draft.secondaryServer = ""; draft.secondaryRtpServer = ""
        draft.secondaryUsername = ""; draft.secondaryPassword = ""
        draft.secondaryWsUri = ""; draft.secondaryWebRtcUsername = ""; draft.secondaryWebRtcPassword = ""
        draft.secondaryTurnUri = ""; draft.secondaryTurnUsername = ""; draft.secondaryTurnPassword = ""
        draft.secondaryUseTls = false; draft.secondaryUseSrtp = false
        showSecondary = false
        onSave("Secondary connection removed.")
    }
}

// MARK: - Audio

struct AudioPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto
    let busy: Bool
    let onSave: (String) -> Void
    @State private var previewing = false

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            SectionHeader(title: "Audio Settings")

            Card {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Devices").font(.subheadline.weight(.semibold))
                    SettingRow(title: "Microphone", detail: "Microphone device") {
                        Picker("", selection: $draft.selectedMicrophone) {
                            Text("System default").tag(-1)
                            ForEach(info.microphones.filter { $0.index >= 0 }) { Text($0.name).tag($0.index) }
                        }.labelsHidden()
                    }
                    SettingRow(title: "Speaker", detail: "Speaker device") {
                        Picker("", selection: $draft.selectedSpeaker) {
                            Text("System default").tag(-1)
                            ForEach(info.speakers.filter { $0.index >= 0 }) { Text($0.name).tag($0.index) }
                        }.labelsHidden()
                    }
                    HStack {
                        if !info.audioBackendInfo.isEmpty {
                            Text(info.audioBackendInfo).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button { Task { _ = await state.refreshAudioDevices() } } label: { Label("Refresh devices", systemImage: "arrow.clockwise") }
                            .controlSize(.small)
                    }
                    Toggle("Acoustic echo cancellation (SIP)", isOn: $draft.echoCancellation).padding(.top, 4)
                }
            }

            Card {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Codec").font(.subheadline.weight(.semibold))
                    SettingRow(title: "Audio codec", detail: "Preferred codec for calls") {
                        Picker("", selection: $draft.sipCodec) {
                            ForEach(info.sipCodecOptions) { Text($0.label).tag($0.key) }
                        }.labelsHidden()
                    }
                    SettingRow(title: "Sample rate", detail: "Audio quality (Hz)") {
                        Picker("", selection: $draft.sipSampleRate) {
                            ForEach(info.sipSampleRateOptions, id: \.self) { Text(sampleRateLabel($0)).tag($0) }
                        }.labelsHidden()
                    }
                    if draft.sipCodec.lowercased() == "opus" {
                        SettingRow(title: "Opus bitrate", detail: "bits per second") {
                            TextField("", value: $draft.sipOpusBitrate, format: .number).textFieldStyle(.roundedBorder).frame(width: 120)
                        }
                    }
                }
            }

            Card {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Ringtone").font(.subheadline.weight(.semibold))
                    SettingRow(title: "Ringtone sound", detail: "Sound played on incoming call") {
                        Picker("", selection: $draft.ringtoneWav) {
                            Text("WAV file (ringtone/incoming_call.wav)").tag(true)
                            Text("Generated tone").tag(false)
                        }.labelsHidden()
                    }
                    HStack {
                        Text("Ringtone volume").font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Text("\(Int((draft.ringtoneVolume * 100).rounded()))%").font(.caption).foregroundStyle(.secondary)
                    }
                    Slider(value: $draft.ringtoneVolume, in: 0...1, step: 0.05)
                    HStack {
                        Button(previewing ? "Stop" : "Preview") {
                            if previewing { state.stopRingtone() } else { state.previewRingtone(draft: draft) }
                            previewing.toggle()
                        }
                        .controlSize(.small)
                    }
                }
            }

            Button { onSave("Audio settings saved.") } label: {
                HStack { if busy { ProgressView().controlSize(.small) }; Text("Save Audio Settings") }
            }
            .buttonStyle(.borderedProminent)
            .disabled(busy)
        }
        .onDisappear { if previewing { state.stopRingtone() } }
    }

    private func sampleRateLabel(_ hz: Int) -> String {
        switch hz {
        case 8000: return "8000 Hz (Standard)"
        case 16000: return "16000 Hz (Wideband)"
        case 48000: return "48000 Hz (Opus)"
        default: return "\(hz) Hz"
        }
    }
}

// MARK: - General

struct GeneralPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let busy: Bool
    let onSave: (String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            SectionHeader(title: "General Settings")
            VStack(alignment: .leading, spacing: 6) {
                SwitchRow(title: "Call Recording", detail: "Record WebRTC calls automatically", isOn: Binding(
                    get: { draft.callRecording },
                    set: { draft.callRecording = $0; onSave($0 ? "Call recording enabled." : "Call recording disabled.") }
                ))
                Text("Recording is available in WebRTC mode only.").font(.caption).foregroundStyle(Color.accentColor)
            }
            Button { state.openRecordingsFolder() } label: { Label("Open Recordings Folder", systemImage: "folder") }
                .buttonStyle(.borderedProminent)
        }
    }
}

// MARK: - Appearance

struct AppearancePanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto
    let busy: Bool
    let onSave: (String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            SectionHeader(title: "Appearance")
            Card {
                SettingRow(title: "Theme", detail: "Choose how the app looks") {
                    Picker("", selection: $draft.theme) {
                        ForEach(info.themeOptions) { Text(themeLabel($0)).tag($0.key) }
                    }.labelsHidden()
                }
            }
            Button("Apply Theme") {
                state.setTheme(draft.theme)
                onSave("Theme applied.")
            }
            .buttonStyle(.borderedProminent)
            .disabled(busy)
        }
    }

    private func themeLabel(_ c: Choice) -> String {
        switch c.key.lowercased() {
        case "system": return "Use system theme"
        case "dark": return "Dark"
        case "light": return "Light"
        default: return c.label
        }
    }
}

// MARK: - Advanced

struct AdvancedPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto
    let busy: Bool
    let onSave: (String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            SectionHeader(title: "Advanced")
            Card {
                VStack(alignment: .leading, spacing: 4) {
                    Text("No advanced settings yet.").font(.body.weight(.semibold))
                    Text("Per-connection options (SIP / WebRTC transport, WebSocket URI, TURN) have moved into the Connection settings. This section is reserved for future advanced features.")
                        .font(.callout).foregroundStyle(.secondary)
                }
            }
            Card {
                VStack(alignment: .leading, spacing: 10) {
                    Text("Diagnostics").font(.subheadline.weight(.semibold))
                    SwitchRow(title: "Verbose WebRTC logging", detail: "Writes phone.js engine events to the application log", isOn: Binding(
                        get: { draft.webRtcDebug },
                        set: { draft.webRtcDebug = $0; onSave("Diagnostics updated.") }
                    ))
                    HStack(spacing: 12) {
                        Button { state.openLogsFolder() } label: { Label("Open Logs Folder", systemImage: "folder") }
                        Button { state.openLogs() } label: { Label("Show Live Log", systemImage: "doc.text.magnifyingglass") }
                    }
                    if !info.settingsFile.isEmpty {
                        KeyValueRow(key: "Settings file", value: info.settingsFile, keyWidth: 100).font(.caption)
                    }
                }
            }
        }
    }
}

// MARK: - Integrations

struct IntegrationsPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto
    let busy: Bool
    let onSave: (String) -> Void
    let onMessage: (SettingsView.StatusMessage) -> Void
    @State private var authorizing = false
    @State private var gatewayBusy = false

    private var isGatewaySource: Bool { draft.kommoSource.lowercased() == "gateway" }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            SectionHeader(title: "Integrations")
            kommoCard
            gatewayCard
        }
        .textFieldStyle(.roundedBorder)
    }

    private var kommoCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 14) {
                SwitchRow(title: "Kommo Integration", detail: "Automatically attach call logs and recordings to leads in Kommo", isOn: $draft.kommoEnabled)

                if draft.kommoEnabled {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Connection & recording upload source:").font(.caption).foregroundStyle(.secondary)
                        Picker("", selection: $draft.kommoSource) {
                            Text("PBX Gateway").tag("gateway")
                            Text("Local (this app)").tag("local")
                        }
                        .pickerStyle(.segmented).labelsHidden().frame(width: 260)
                        Text(isGatewaySource
                             ? "Kommo tokens, CDR matching and recording upload are handled by Callspire PBX Gateway."
                             : "This Mac talks to the Kommo API directly and uploads local recordings.")
                            .font(.caption).foregroundStyle(.secondary)
                    }

                    if !isGatewaySource {
                        LabeledField(title: "Kommo subdomain") {
                            TextField("yourcompany (yourcompany.kommo.com)", text: $draft.kommoSubdomain)
                        }
                        LabeledField(title: "Authentication") {
                            Picker("", selection: $draft.kommoAuthMode) {
                                ForEach(info.kommoAuthOptions) { Text($0.label).tag($0.key) }
                            }
                            .pickerStyle(.segmented).labelsHidden().frame(width: 300)
                        }
                        if draft.kommoAuthMode == "oauth" {
                            LabeledField(title: "Client ID") { TextField("", text: $draft.kommoClientId) }
                            LabeledField(title: "Client Secret") { SecureField("", text: $draft.kommoClientSecret) }
                            LabeledField(title: "Redirect URI") { TextField("https://…", text: $draft.kommoRedirectUri) }
                            HStack(spacing: 10) {
                                Button {
                                    authorizing = true
                                    Task {
                                        let err = await state.kommoAuthorize(draft: draft)
                                        authorizing = false
                                        onMessage(.init(text: err ?? "Kommo authorized.", isError: err != nil))
                                    }
                                } label: {
                                    HStack { if authorizing { ProgressView().controlSize(.small) }; Text("Authorize with Kommo") }
                                }
                                .buttonStyle(.borderedProminent)
                                .disabled(authorizing || draft.kommoClientId.isEmpty || draft.kommoSubdomain.isEmpty)
                                StatusDot(tone: info.kommoOAuth.isAuthorized ? .online : .offline)
                                Text(info.kommoOAuth.statusText).font(.caption)
                                    .foregroundStyle(info.kommoOAuth.isAuthorized ? Color.green : Color.secondary)
                            }
                        } else {
                            LabeledField(title: "Long-lived token") { SecureField("", text: $draft.kommoToken) }
                        }
                    }

                    SwitchRow(title: "Upload call recordings",
                              detail: "When off, contact names and call notes are still synced; audio files are not sent to Kommo",
                              isOn: $draft.kommoRecordingUpload)
                    SwitchRow(title: "Manual selection lead",
                              detail: "After each call, choose which open deal receives the recording (works with PBX Gateway upload too)",
                              isOn: $draft.kommoLeadSelection)
                }

                HStack {
                    Button { onSave("Kommo settings saved.") } label: {
                        HStack { if busy { ProgressView().controlSize(.small) }; Text("Save Kommo Settings") }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(busy)
                    if state.main.amoCrmConfigured {
                        StatusDot(tone: state.main.amoCrmOnline ? .online : .warning)
                        Text(state.main.amoCrmStatusText).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    private var gatewayCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 14) {
                SwitchRow(title: "Callspire PBX Gateway",
                          detail: "Call history, recordings, outbound Caller ID, originate — authenticated API to your MikoPBX through the gateway",
                          isOn: $draft.gatewayEnabled)
                LabeledField(title: "Service URL") {
                    TextField("https://pbx.example.com/tool", text: $draft.gatewayUrl)
                }
                LabeledField(title: "Extension (internal number)") {
                    TextField("204", text: $draft.gatewayExtension).frame(width: 200)
                }
                HStack(spacing: 10) {
                    Button {
                        gatewayBusy = true
                        Task {
                            let err = await state.gatewayAuthorize(url: draft.gatewayUrl, ext: draft.gatewayExtension)
                            gatewayBusy = false
                            onMessage(.init(text: err ?? "Browser opened. Sign in and the token will be returned to Callspire automatically.", isError: err != nil))
                        }
                    } label: {
                        HStack { if gatewayBusy { ProgressView().controlSize(.small) }; Text("Authorize") }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(gatewayBusy || draft.gatewayUrl.trimmingCharacters(in: .whitespaces).isEmpty)

                    Button("Clear settings") {
                        Task {
                            await state.gatewayClear()
                            onMessage(.init(text: "PBX Gateway settings cleared.", isError: false))
                        }
                    }
                    .disabled(gatewayBusy)

                    Button("Save") { onSave("Gateway settings saved.") }
                        .disabled(busy)
                }
                HStack(spacing: 6) {
                    StatusDot(tone: state.main.gatewayConnected ? .online : (draft.gatewayEnabled ? .warning : .offline))
                    Text(state.main.gatewayStatusText.isEmpty ? (state.main.gatewayConnected ? "Connected" : "Not connected") : state.main.gatewayStatusText)
                        .font(.caption)
                        .foregroundStyle(state.main.gatewayConnected ? Color.green : Color.secondary)
                }
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
        VStack(alignment: .leading, spacing: 18) {
            SectionHeader(title: "About")
            Card {
                HStack(spacing: 16) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable().frame(width: 56, height: 56)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Callspire Softphone").font(.title2.weight(.semibold))
                        Text(info.versionText.isEmpty ? "Version \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")" : info.versionText)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            Text("A modern softphone application for macOS.").foregroundStyle(.secondary)
            Card {
                VStack(alignment: .leading, spacing: 10) {
                    Label("Updates", systemImage: "arrow.down.to.line").font(.headline)
                    Text("Updates are checked automatically once per day when the application starts.")
                        .font(.callout).foregroundStyle(.secondary)
                    HStack(spacing: 12) {
                        Button {
                            checking = true
                            Task { result = await state.checkForUpdates(); checking = false }
                        } label: {
                            HStack { if checking { ProgressView().controlSize(.small) }; Text("Check for Updates") }
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(checking)
                        if (result?.available ?? info.updateAvailable) {
                            Button("Download") { state.openUpdateUrl() }
                        }
                    }
                    let status = result?.status ?? info.updateStatus
                    if !status.isEmpty { Text(status).font(.caption).foregroundStyle(.secondary) }
                }
            }
        }
    }
}

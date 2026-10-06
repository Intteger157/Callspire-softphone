import SwiftUI
import AppKit

// MARK: - Connection (Form)

struct ConnectionPanel: View {
    @EnvironmentObject var state: AppState
    @Binding var draft: SettingsDto
    let info: SettingsDto
    let busy: Bool
    let onSave: (String) -> Void
    let onTest: (Bool) -> Void
    @State private var showSecondary = false

    var body: some View {
        Form {
            connectionSection(isSecondary: false)
            if showSecondary || hasSecondary {
                connectionSection(isSecondary: true)
            } else {
                Section {
                    Button { showSecondary = true } label: {
                        Label("Add Additional Connection", systemImage: "plus.circle")
                    }
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
    private func connectionSection(isSecondary: Bool) -> some View {
        let status = isSecondary ? state.main.secondary : state.main.main
        let isWebRtc = (isSecondary ? draft.secondaryTransport : draft.mainTransport).lowercased() == "webrtc"
        let turnUri = isSecondary ? draft.secondaryTurnUri : draft.mainTurnUri

        Section {
            TextField("Name", text: isSecondary ? $draft.secondaryName : $draft.mainName)
            Picker("Transport", selection: isSecondary ? $draft.secondaryTransport : $draft.mainTransport) {
                ForEach(info.transportOptions.isEmpty ? [Choice(key: "Sip", label: "SIP"), Choice(key: "WebRtc", label: "WebRTC")] : info.transportOptions) {
                    Text($0.key.lowercased() == "webrtc" ? "WebRTC" : "SIP").tag($0.key)
                }
            }
            .pickerStyle(.segmented)

            if isWebRtc {
                TextField("WebSocket URI", text: isSecondary ? $draft.secondaryWsUri : $draft.mainWsUri)
                TextField("Username", text: isSecondary ? $draft.secondaryWebRtcUsername : $draft.mainWebRtcUsername)
                SecureField("Password", text: isSecondary ? $draft.secondaryWebRtcPassword : $draft.mainWebRtcPassword)
            } else {
                TextField("SIP Server", text: isSecondary ? $draft.secondaryServer : $draft.mainServer)
                if isSecondary {
                    TextField("RTP Server (optional)", text: $draft.secondaryRtpServer)
                } else {
                    TextField("Port", text: $draft.mainPort)
                }
                TextField("Username", text: isSecondary ? $draft.secondaryUsername : $draft.mainUsername)
                SecureField("Password", text: isSecondary ? $draft.secondaryPassword : $draft.mainPassword)
                Toggle("Use TLS", isOn: isSecondary ? $draft.secondaryUseTls : $draft.mainUseTls)
                Toggle("Use SRTP", isOn: isSecondary ? $draft.secondaryUseSrtp : $draft.mainUseSrtp)
            }

            HStack {
                StatusPill(
                    text: status.text.isEmpty ? (status.isOnline ? "Connected" : "Not connected") : status.text,
                    tone: status.isError ? .error : (status.isOnline ? .online : .offline)
                )
                Spacer()
                Button("Test Connection") { onTest(isSecondary) }.disabled(busy)
            }

            Button {
                onSave("Settings saved. Reconnecting…")
            } label: {
                if busy { ProgressView().controlSize(.small) }
                Text("Save and Connect")
            }
            .disabled(busy)

            if isSecondary {
                Button("Remove Secondary Connection", role: .destructive) { clearSecondary() }
            }
        } header: {
            Text(isSecondary ? "Secondary Connection" : "Primary Connection")
        } footer: {
            Text("Pick a transport, then enter credentials. The same username/password are used for SIP and WebRTC registration.")
        }

        Section("TURN") {
            TextField("TURN URI", text: isSecondary ? $draft.secondaryTurnUri : $draft.mainTurnUri)
            TextField("Username", text: isSecondary ? $draft.secondaryTurnUsername : $draft.mainTurnUsername)
            SecureField("Password", text: isSecondary ? $draft.secondaryTurnPassword : $draft.mainTurnPassword)
            StatusPill(text: turnStatus(turnUri), tone: turnUri.isEmpty ? .offline : .online)
        }
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
                Toggle("Acoustic Echo Cancellation (SIP)", isOn: $draft.echoCancellation)
                Button {
                    Task { _ = await state.refreshAudioDevices() }
                } label: {
                    Label("Refresh Devices", systemImage: "arrow.clockwise")
                }
            } header: {
                Text("Devices")
            } footer: {
                if !info.audioBackendInfo.isEmpty { Text(info.audioBackendInfo) }
            }

            Section("Codec") {
                Picker("Audio Codec", selection: $draft.sipCodec) {
                    ForEach(info.sipCodecOptions) { Text($0.label).tag($0.key) }
                }
                Picker("Sample Rate", selection: $draft.sipSampleRate) {
                    ForEach(info.sipSampleRateOptions, id: \.self) { Text(sampleRateLabel($0)).tag($0) }
                }
                if draft.sipCodec.lowercased() == "opus" {
                    TextField("Opus Bitrate", value: $draft.sipOpusBitrate, format: .number)
                }
            }

            Section("Ringtone") {
                Picker("Sound", selection: $draft.ringtoneWav) {
                    Text("WAV File").tag(true)
                    Text("Generated Tone").tag(false)
                }
                HStack {
                    Text("Volume")
                    Slider(value: $draft.ringtoneVolume, in: 0...1, step: 0.05)
                    Text("\(Int((draft.ringtoneVolume * 100).rounded()))%")
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                        .frame(width: 40, alignment: .trailing)
                }
                HStack {
                    Button(previewing ? "Stop Preview" : "Preview") {
                        if previewing { state.stopRingtone() } else { state.previewRingtone(draft: draft) }
                        previewing.toggle()
                    }
                }
            }

            Section {
                Button {
                    onSave("Audio settings saved.")
                } label: {
                    if busy { ProgressView().controlSize(.small) }
                    Text("Save Audio Settings")
                }
                .disabled(busy)
            }
        }
        .formStyle(.grouped)
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
        Form {
            Section {
                Toggle(isOn: Binding(
                    get: { draft.callRecording },
                    set: { draft.callRecording = $0; onSave($0 ? "Call recording enabled." : "Call recording disabled.") }
                )) {
                    Text("Call Recording")
                }
            } footer: {
                Text("Record WebRTC calls automatically. Available in WebRTC mode only.")
            }
            Section {
                Button {
                    state.openRecordingsFolder()
                } label: {
                    Label("Open Recordings Folder", systemImage: "folder")
                }
            }
        }
        .formStyle(.grouped)
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
        Form {
            Section {
                Picker("Theme", selection: $draft.theme) {
                    ForEach(info.themeOptions) { Text(themeLabel($0)).tag($0.key) }
                }
            } footer: {
                Text("Choose how Callspire looks. System follows Appearance in System Settings.")
            }
            Section {
                Button("Apply Theme") {
                    state.setTheme(draft.theme)
                    onSave("Theme applied.")
                }
                .disabled(busy)
            }
        }
        .formStyle(.grouped)
    }

    private func themeLabel(_ c: Choice) -> String {
        switch c.key.lowercased() {
        case "system": return "Use System"
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
        Form {
            Section {
                Text("No advanced settings yet.")
                    .font(.body.weight(.medium))
                Text("Per-connection options (SIP / WebRTC, WebSocket URI, TURN) live under Connection.")
                    .foregroundStyle(.secondary)
            }

            Section("Diagnostics") {
                Toggle("Verbose WebRTC Logging", isOn: Binding(
                    get: { draft.webRtcDebug },
                    set: { draft.webRtcDebug = $0; onSave("Diagnostics updated.") }
                ))
                Button { state.openLogsFolder() } label: { Label("Open Logs Folder", systemImage: "folder") }
                Button { state.openLogs() } label: { Label("Show Live Log", systemImage: "text.alignleft") }
                if !info.settingsFile.isEmpty {
                    LabeledContent("Settings File", value: info.settingsFile)
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
    let busy: Bool
    let onSave: (String) -> Void
    let onMessage: (SettingsView.StatusMessage) -> Void
    @State private var authorizing = false
    @State private var gatewayBusy = false

    private var isGatewaySource: Bool { draft.kommoSource.lowercased() == "gateway" }

    var body: some View {
        Form {
            Section {
                Toggle("Kommo Integration", isOn: $draft.kommoEnabled)
            } footer: {
                Text("Automatically attach call logs and recordings to leads in Kommo.")
            }

            if draft.kommoEnabled {
                Section("Source") {
                    Picker("Upload Source", selection: $draft.kommoSource) {
                        Text("PBX Gateway").tag("gateway")
                        Text("Local (This Mac)").tag("local")
                    }
                    .pickerStyle(.segmented)
                }

                if !isGatewaySource {
                    Section("Local Kommo") {
                        TextField("Subdomain", text: $draft.kommoSubdomain)
                        Picker("Authentication", selection: $draft.kommoAuthMode) {
                            ForEach(info.kommoAuthOptions) { Text($0.label).tag($0.key) }
                        }
                        .pickerStyle(.segmented)
                        if draft.kommoAuthMode == "oauth" {
                            TextField("Client ID", text: $draft.kommoClientId)
                            SecureField("Client Secret", text: $draft.kommoClientSecret)
                            TextField("Redirect URI", text: $draft.kommoRedirectUri)
                            HStack {
                                Button {
                                    authorizing = true
                                    Task {
                                        let err = await state.kommoAuthorize(draft: draft)
                                        authorizing = false
                                        onMessage(.init(text: err ?? "Kommo authorized.", isError: err != nil))
                                    }
                                } label: {
                                    if authorizing { ProgressView().controlSize(.small) }
                                    Text("Authorize with Kommo")
                                }
                                .disabled(authorizing || draft.kommoClientId.isEmpty || draft.kommoSubdomain.isEmpty)
                                StatusPill(
                                    text: info.kommoOAuth.statusText,
                                    tone: info.kommoOAuth.isAuthorized ? .online : .offline
                                )
                            }
                        } else {
                            SecureField("Long-Lived Token", text: $draft.kommoToken)
                        }
                    }
                }

                Section {
                    Toggle("Upload Call Recordings", isOn: $draft.kommoRecordingUpload)
                    Toggle("Manual Lead Selection", isOn: $draft.kommoLeadSelection)
                }

                Section {
                    Button {
                        onSave("Kommo settings saved.")
                    } label: {
                        if busy { ProgressView().controlSize(.small) }
                        Text("Save Kommo Settings")
                    }
                    .disabled(busy)
                    if state.main.amoCrmConfigured {
                        StatusPill(
                            text: state.main.amoCrmStatusText,
                            tone: state.main.amoCrmOnline ? .online : .warning
                        )
                    }
                }
            }

            Section {
                Toggle("Callspire PBX Gateway", isOn: $draft.gatewayEnabled)
                TextField("Service URL", text: $draft.gatewayUrl)
                TextField("Extension", text: $draft.gatewayExtension)
                HStack {
                    Button {
                        gatewayBusy = true
                        Task {
                            let err = await state.gatewayAuthorize(url: draft.gatewayUrl, ext: draft.gatewayExtension)
                            gatewayBusy = false
                            onMessage(.init(
                                text: err ?? "Browser opened. Sign in and the token returns via callspire://cdr-auth.",
                                isError: err != nil
                            ))
                        }
                    } label: {
                        if gatewayBusy { ProgressView().controlSize(.small) }
                        Text("Authorize")
                    }
                    .disabled(gatewayBusy || draft.gatewayUrl.trimmingCharacters(in: .whitespaces).isEmpty)

                    Button("Clear Settings") {
                        Task {
                            await state.gatewayClear()
                            onMessage(.init(text: "PBX Gateway settings cleared.", isError: false))
                        }
                    }
                    .disabled(gatewayBusy)

                    Button("Save") { onSave("Gateway settings saved.") }
                        .disabled(busy)
                }
                StatusPill(
                    text: state.main.gatewayStatusText.isEmpty
                        ? (state.main.gatewayConnected ? "Connected" : "Not connected")
                        : state.main.gatewayStatusText,
                    tone: state.main.gatewayConnected ? .online : (draft.gatewayEnabled ? .warning : .offline)
                )
            } header: {
                Text("PBX Gateway")
            } footer: {
                Text("Call history, recordings, Caller ID and originate — authenticated API to MikoPBX through the gateway.")
            }
        }
        .formStyle(.grouped)
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
                        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Callspire Softphone").font(.title3.weight(.semibold))
                        Text(info.versionText.isEmpty
                             ? "Version \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")"
                             : info.versionText)
                            .foregroundStyle(.secondary)
                    }
                }
                Text("A modern softphone for macOS.")
                    .foregroundStyle(.secondary)
            }

            Section("Updates") {
                Text("Updates are checked automatically once per day when the application starts.")
                    .foregroundStyle(.secondary)
                HStack {
                    Button {
                        checking = true
                        Task { result = await state.checkForUpdates(); checking = false }
                    } label: {
                        if checking { ProgressView().controlSize(.small) }
                        Text("Check for Updates")
                    }
                    .disabled(checking)
                    if result?.available ?? info.updateAvailable {
                        Button("Download") { state.openUpdateUrl() }
                    }
                }
                let status = result?.status ?? info.updateStatus
                if !status.isEmpty {
                    Text(status).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
    }
}

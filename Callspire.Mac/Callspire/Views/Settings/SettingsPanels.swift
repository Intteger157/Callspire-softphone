import SwiftUI

struct AudioPanel: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Audio").font(.title2.bold())
            Text(state.settings.audioBackendInfo).font(.caption).foregroundStyle(.secondary)
            FieldLabel(title: "Microphone")
            Picker("", selection: $state.settings.selectedMicrophone) {
                ForEach(state.settings.microphones) { Text($0.name).tag($0.index) }
            }
            FieldLabel(title: "Speaker / playback")
            Picker("", selection: $state.settings.selectedSpeaker) {
                ForEach(state.settings.speakers) { Text($0.name).tag($0.index) }
            }
            Button("Refresh devices") { Task { if let s = try? await state.ipc.request("refreshAudioDevices", as: SettingsDto.self) { state.settings = s } } }
            Toggle("Acoustic echo cancellation", isOn: $state.settings.echoCancellation)
            FieldLabel(title: "SIP codec")
            Picker("", selection: $state.settings.sipCodec) {
                ForEach(state.settings.sipCodecOptions) { Text($0.label).tag($0.key) }
            }
            FieldLabel(title: "Sample rate")
            Picker("", selection: $state.settings.sipSampleRate) {
                ForEach(state.settings.sipSampleRateOptions, id: \.self) { Text("\($0) Hz").tag($0) }
            }
            if state.settings.sipCodec.lowercased() == "opus" {
                FieldLabel(title: "Opus bitrate")
                TextField("", value: $state.settings.sipOpusBitrate, format: .number)
            }
            Toggle("WAV ringtone", isOn: $state.settings.ringtoneWav)
            HStack {
                Text("Volume")
                Slider(value: $state.settings.ringtoneVolume, in: 0...1)
                Text("\(Int(state.settings.ringtoneVolume * 100))%").frame(width: 40)
            }
            HStack {
                Button("Preview") { Task { try? await state.ipc.requestVoid("previewRingtone") } }
                Button("Stop") { Task { try? await state.ipc.requestVoid("stopRingtone") } }
            }
        }.textFieldStyle(.roundedBorder)
    }
}

struct KommoPanel: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Kommo & PBX Gateway").font(.title2.bold())
            Toggle("Enable PBX Gateway", isOn: $state.settings.gatewayEnabled)
            FieldLabel(title: "Gateway URL"); TextField("", text: $state.settings.gatewayUrl)
            HStack {
                VStack(alignment: .leading) { FieldLabel(title: "Access token (JWT)"); SecureField("", text: $state.settings.gatewayToken) }
                VStack(alignment: .leading) { FieldLabel(title: "Extension"); TextField("", text: $state.settings.gatewayExtension).frame(width: 120) }
            }
            Divider()
            Toggle("Enable Kommo integration", isOn: $state.settings.kommoEnabled)
            FieldLabel(title: "Source")
            Picker("", selection: $state.settings.kommoSource) {
                ForEach(state.settings.kommoSourceOptions) { Text($0.label).tag($0.key) }
            }
            FieldLabel(title: "Subdomain"); TextField("", text: $state.settings.kommoSubdomain)
            FieldLabel(title: "Authentication")
            Picker("", selection: $state.settings.kommoAuthMode) {
                ForEach(state.settings.kommoAuthOptions) { Text($0.label).tag($0.key) }
            }
            if state.settings.kommoAuthMode == "oauth" {
                HStack {
                    Text(state.settings.kommoOAuth.statusText)
                        .foregroundStyle(state.settings.kommoOAuth.isAuthorized ? Color.green : .secondary)
                    Spacer()
                    Button(state.settings.kommoOAuth.isBusy ? "Authorizing…" : "Authorize with Kommo") {
                        Task { try? await state.ipc.requestVoid("kommoAuthorize", params: state.settings.asFields()) }
                    }.disabled(state.settings.kommoOAuth.isBusy)
                }
                FieldLabel(title: "Client ID"); TextField("", text: $state.settings.kommoClientId)
                FieldLabel(title: "Client Secret"); SecureField("", text: $state.settings.kommoClientSecret)
                FieldLabel(title: "Redirect URI"); TextField("", text: $state.settings.kommoRedirectUri)
            } else {
                FieldLabel(title: "Long-lived token"); SecureField("", text: $state.settings.kommoToken)
            }
            Toggle("Ask which lead to attach the call to", isOn: $state.settings.kommoLeadSelection)
            Toggle("Upload call recordings to Kommo", isOn: $state.settings.kommoRecordingUpload)
        }.textFieldStyle(.roundedBorder)
    }
}

struct AppearancePanel: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Appearance").font(.title2.bold())
            Picker("Theme", selection: $state.settings.theme) {
                ForEach(state.settings.themeOptions) { Text($0.label).tag($0.key) }
            }
            .onChange(of: state.settings.theme) { _, v in
                state.applyTheme(v)
                Task { try? await state.ipc.requestVoid("setTheme", params: ["theme": v]) }
            }
            Text("“System” follows the macOS appearance setting.").font(.caption).foregroundStyle(.secondary)
        }
    }
}

struct AdvancedPanel: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Advanced").font(.title2.bold())
            Toggle("Record calls", isOn: $state.settings.callRecording)
            HStack {
                Text(state.settings.recordingsFolder).lineLimit(1).font(.caption)
                Button("Open folder") { Task { try? await state.ipc.requestVoid("openRecordingsFolder") } }
            }
            Toggle("Verbose WebRTC logging", isOn: $state.settings.webRtcDebug)
            HStack {
                Text(state.settings.logsFolder).lineLimit(1).font(.caption)
                Button("Open logs") { Task { try? await state.ipc.requestVoid("openLogsFolder") } }
            }
            FieldLabel(title: "Settings file")
            Text(state.settings.settingsFile).font(.caption).textSelection(.enabled)
            Text("WebRTC engine test").font(.headline)
            Text("The hidden WKWebView used for calling is created on demand when a WebRTC line connects. Use Logs to inspect phone.js output.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}

struct AboutPanel: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Callspire Softphone").font(.title.bold())
            Text(state.settings.versionText).foregroundStyle(.secondary)
            HStack {
                Button("Check for updates") { Task { try? await state.ipc.requestVoid("checkUpdates") } }
                if state.settings.updateAvailable {
                    Button("Download") { Task { try? await state.ipc.requestVoid("openUpdateUrl") } }
                }
            }
            Text(state.settings.updateStatus).font(.caption).foregroundStyle(.secondary)
        }
    }
}

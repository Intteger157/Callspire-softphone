import SwiftUI
import AppKit

/// Active call — FaceTime-adjacent layout, system SF Symbols.
struct CallView: View {
    @EnvironmentObject var state: AppState
    let info: CallWindowInfo
    @State private var showAudioDevices = false

    private var s: CallState { state.call?.state ?? info.state }
    private let keys = ["1","2","3","4","5","6","7","8","9","*","0","#"]

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 8) {
                Text(s.callerDisplay.isEmpty ? info.phoneNumber : s.callerDisplay)
                    .font(.system(size: 28, weight: .light, design: .rounded))
                    .multilineTextAlignment(.center)
                    .lineLimit(3)
                    .textSelection(.enabled)
                    .padding(.horizontal, 20)

                Text(s.statusText)
                    .font(.callout.weight(.medium))
                    .foregroundStyle(toneColor)

                Text(s.timerText)
                    .font(.title3.monospacedDigit())
                    .foregroundStyle(toneColor)

                HStack(spacing: 8) {
                    TransportBadge(label: info.transportLabel, isWebRtc: info.isWebRtc)
                    if !info.connectionLabel.isEmpty {
                        Text(info.connectionLabel).font(.caption).foregroundStyle(.secondary)
                    }
                    if s.isRecordingIndicatorVisible {
                        Label("REC", systemImage: "record.circle.fill")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.red)
                    }
                }
            }
            .padding(.top, 28)

            Spacer(minLength: 16)

            if s.isKeypadVisible {
                keypad
                    .transition(.opacity.combined(with: .move(edge: .bottom)))
                Spacer(minLength: 16)
            }

            VStack(spacing: 20) {
                if s.showControls {
                    HStack(spacing: 16) {
                        RoundIconButton(
                            symbol: s.isMuted ? "mic.slash.fill" : "mic.fill",
                            active: s.isMuted,
                            help: s.isMuted ? "Unmute" : "Mute"
                        ) { state.callVerb("toggleMute") }

                        RoundIconButton(
                            symbol: s.isOnHold ? "play.fill" : "pause.fill",
                            active: s.isOnHold,
                            help: s.isOnHold ? "Resume" : "Hold"
                        ) { state.callVerb("toggleHold") }

                        RoundIconButton(symbol: "speaker.wave.2.fill", help: "Audio devices") {
                            showAudioDevices = true
                        }

                        RoundIconButton(
                            symbol: "circle.grid.3x3.fill",
                            active: s.isKeypadVisible,
                            help: "Keypad"
                        ) {
                            withAnimation(.easeInOut(duration: 0.15)) { state.callVerb("toggleKeypad") }
                        }
                    }
                }

                if s.showIncomingButtons {
                    HStack(spacing: 48) {
                        RoundIconButton(symbol: "phone.fill", size: 64, fill: .green, foreground: .white, help: "Answer") {
                            state.callVerb("answer")
                        }
                        RoundIconButton(symbol: "phone.down.fill", size: 64, fill: .red, foreground: .white, help: "Reject") {
                            state.callVerb("reject")
                        }
                    }
                } else if s.showHangupButton {
                    RoundIconButton(symbol: "phone.down.fill", size: 64, fill: .red, foreground: .white, help: "Hang up") {
                        state.callVerb("hangup")
                    }
                    .keyboardShortcut(.escape, modifiers: [])
                }
            }
            .padding(.bottom, 28)
        }
        .frame(width: 380, height: s.isKeypadVisible ? 680 : 560)
        .animation(.easeInOut(duration: 0.15), value: s.isKeypadVisible)
        .background(MacTheme.windowFill)
        .sheet(isPresented: $showAudioDevices) {
            CallAudioDevicesSheet().environmentObject(state)
        }
        .onDisappear { state.callWindowClosedByUser() }
    }

    private var toneColor: Color {
        switch s.statusTone.lowercased() {
        case "good", "success": return .green
        case "bad", "error": return .red
        case "warning": return .orange
        default: return .secondary
        }
    }

    private var keypad: some View {
        let rows = stride(from: 0, to: keys.count, by: 3).map { Array(keys[$0..<min($0 + 3, keys.count)]) }
        return VStack(spacing: 10) {
            ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                HStack(spacing: 12) {
                    ForEach(row, id: \.self) { k in
                        KeypadKey(label: k, size: 50) {
                            state.callVerb("sendDtmf", digit: k)
                        }
                    }
                }
            }
        }
    }
}

struct CallAudioDevicesSheet: View {
    @EnvironmentObject var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var devices: CallAudioDevices?
    @State private var input = ""
    @State private var output = ""
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Audio Devices").font(.title3.weight(.semibold))
            if let d = devices {
                Form {
                    Picker("Microphone", selection: $input) {
                        ForEach(d.inputs) { Text($0.label).tag($0.id) }
                    }
                    Picker("Speaker", selection: $output) {
                        ForEach(d.outputs) { Text($0.label).tag($0.id) }
                    }
                }
                .formStyle(.grouped)
                .frame(height: 120)
                Text(d.isWebRtc
                     ? "Changes apply immediately to the current WebRTC call."
                     : "SIP audio re-opens on the selected PortAudio devices.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else if let error {
                Text(error).foregroundStyle(.red)
            } else {
                HStack { ProgressView().controlSize(.small); Text("Loading devices…").foregroundStyle(.secondary) }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Apply") {
                    busy = true
                    Task {
                        await state.switchCallAudioDevices(
                            input: input.isEmpty ? nil : input,
                            output: output.isEmpty ? nil : output
                        )
                        busy = false
                        dismiss()
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(devices == nil || busy)
            }
        }
        .padding(20)
        .frame(width: 420)
        .task {
            if let d = await state.loadCallAudioDevices() {
                devices = d
                input = d.inputs.first?.id ?? ""
                output = d.outputs.first?.id ?? ""
            } else {
                error = "Could not enumerate audio devices."
            }
        }
    }
}

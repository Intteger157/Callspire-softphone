import SwiftUI
import AppKit

/// Active-call window — WPF CallWindow parity: caller, status, timer, optional inline DTMF keypad,
/// mute / hold / speaker / keypad controls, red hang-up, and answer / reject for incoming calls.
struct CallView: View {
    @EnvironmentObject var state: AppState
    let info: CallWindowInfo
    @State private var showAudioDevices = false

    private var s: CallState { state.call?.state ?? info.state }
    private let keys = [["1","2","3"],["4","5","6"],["7","8","9"],["*","0","#"]]

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 6) {
                Text(s.callerDisplay.isEmpty ? info.phoneNumber : s.callerDisplay)
                    .font(.system(size: 26, weight: .light))
                    .multilineTextAlignment(.center)
                    .lineLimit(3)
                    .textSelection(.enabled)
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
                        Label("REC", systemImage: "record.circle").font(.caption.bold()).foregroundStyle(.red)
                    }
                }
                .padding(.top, 2)
            }
            .padding(.top, 28)
            .padding(.horizontal, 24)

            Spacer(minLength: 12)

            if s.isKeypadVisible {
                keypad.transition(.opacity.combined(with: .scale(scale: 0.95)))
                Spacer(minLength: 12)
            }

            VStack(spacing: 18) {
                if s.showControls {
                    HStack(spacing: 18) {
                        RoundIconButton(symbol: s.isMuted ? "mic.slash.fill" : "mic.fill", active: s.isMuted, help: s.isMuted ? "Unmute" : "Mute") { state.callVerb("toggleMute") }
                        RoundIconButton(symbol: s.isOnHold ? "play.fill" : "pause.fill", active: s.isOnHold, help: s.isOnHold ? "Resume" : "Hold") { state.callVerb("toggleHold") }
                        RoundIconButton(symbol: "speaker.wave.2.fill", help: "Audio devices") { showAudioDevices = true }
                        RoundIconButton(symbol: "circle.grid.3x3.fill", active: s.isKeypadVisible, help: "Keypad") {
                            withAnimation(.easeInOut(duration: 0.15)) { state.callVerb("toggleKeypad") }
                        }
                    }
                }
                if s.showIncomingButtons {
                    HStack(spacing: 48) {
                        RoundIconButton(symbol: "phone.fill", size: 64, fill: .green, foreground: .white, help: "Answer") { state.callVerb("answer") }
                        RoundIconButton(symbol: "phone.down.fill", size: 64, fill: .red, foreground: .white, help: "Reject") { state.callVerb("reject") }
                    }
                } else if s.showHangupButton {
                    RoundIconButton(symbol: "phone.down.fill", size: 64, fill: .red, foreground: .white, help: "Hang up") { state.callVerb("hangup") }
                        .keyboardShortcut(.escape, modifiers: [])
                }
            }
            .padding(.bottom, 32)
        }
        .frame(width: 380, height: s.isKeypadVisible ? 680 : 560)
        .animation(.easeInOut(duration: 0.15), value: s.isKeypadVisible)
        .navigationTitle(info.windowTitle.isEmpty ? "Active Call" : info.windowTitle)
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
        VStack(spacing: 10) {
            ForEach(keys, id: \.self) { row in
                HStack(spacing: 14) {
                    ForEach(row, id: \.self) { k in
                        Button { state.callVerb("sendDtmf", digit: k) } label: {
                            Text(k)
                                .font(.system(size: 20, weight: .medium))
                                .frame(width: 54, height: 54)
                                .background(Circle().strokeBorder(Color(nsColor: .separatorColor), lineWidth: 1))
                                .contentShape(Circle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
    }
}

/// Speaker button → pick microphone / speaker for the running call (WPF SpeakerButton_Click parity).
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
            Text("Audio devices").font(.title3.weight(.semibold))
            if let d = devices {
                Picker("Microphone", selection: $input) {
                    ForEach(d.inputs) { Text($0.label).tag($0.id) }
                }
                Picker("Speaker", selection: $output) {
                    ForEach(d.outputs) { Text($0.label).tag($0.id) }
                }
                if d.isWebRtc {
                    Text("Changes apply immediately to the current WebRTC call.").font(.caption).foregroundStyle(.secondary)
                } else {
                    Text("SIP audio is re-opened on the selected PortAudio devices.").font(.caption).foregroundStyle(.secondary)
                }
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
                        await state.switchCallAudioDevices(input: input.isEmpty ? nil : input, output: output.isEmpty ? nil : output)
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

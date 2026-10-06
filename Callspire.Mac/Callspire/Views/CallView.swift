import SwiftUI
import AppKit

/// Active call — FaceTime-adjacent layout, system SF Symbols.
struct CallView: View {
    @EnvironmentObject var state: AppState
    let info: CallWindowInfo
    @State private var showAudioDevices = false

    private var s: CallState { state.call?.state ?? info.state }
    private let keys: [(String, String?)] = [
        ("1", nil), ("2", "ABC"), ("3", "DEF"),
        ("4", "GHI"), ("5", "JKL"), ("6", "MNO"),
        ("7", "PQRS"), ("8", "TUV"), ("9", "WXYZ"),
        ("*", nil), ("0", "+"), ("#", nil),
    ]

    private var title: String { s.callerDisplay.isEmpty ? info.phoneNumber : s.callerDisplay }
    private var showsNumberLine: Bool {
        !info.phoneNumber.isEmpty && !s.callerDisplay.isEmpty && !s.callerDisplay.contains(info.phoneNumber)
    }
    private var isDialing: Bool { !s.wasAnswered && !s.showIncomingButtons && s.showHangupButton }

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(.top, 40)
                .padding(.horizontal, 24)

            Spacer(minLength: 18)

            if s.isKeypadVisible {
                keypad
                    .transition(.opacity.combined(with: .scale(scale: 0.96)))
                Spacer(minLength: 18)
            }

            if s.showControls {
                controls
                Spacer(minLength: 22)
            }

            bottomButtons
                .padding(.bottom, 30)
        }
        .frame(width: 340, height: s.isKeypadVisible ? 660 : 540)
        .animation(.easeInOut(duration: 0.18), value: s.isKeypadVisible)
        .animation(.easeInOut(duration: 0.18), value: s.showControls)
        .background(
            LinearGradient(
                colors: [Color.accentColor.opacity(0.10), MacTheme.windowFill, MacTheme.windowFill],
                startPoint: .top, endPoint: .bottom
            )
        )
        .sheet(isPresented: $showAudioDevices) {
            CallAudioDevicesSheet().environmentObject(state)
        }
        .onDisappear { state.callWindowClosedByUser() }
    }

    // MARK: - Header

    private var header: some View {
        VStack(spacing: 10) {
            CallAvatar(title: title, isIncoming: info.isIncoming, pulsing: isDialing || s.showIncomingButtons)
                .padding(.bottom, 6)

            Text(title)
                .font(.system(size: 26, weight: .semibold, design: .rounded))
                .multilineTextAlignment(.center)
                .lineLimit(2)
                .minimumScaleFactor(0.7)
                .textSelection(.enabled)

            if showsNumberLine {
                Text(info.phoneNumber)
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
            }

            HStack(spacing: 8) {
                if !s.statusText.isEmpty {
                    Text(s.statusText)
                        .font(.callout.weight(.medium))
                        .foregroundStyle(toneColor)
                }
                if !s.timerText.isEmpty {
                    Text(s.timerText)
                        .font(.callout.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
            }
            .frame(minHeight: 20)

            HStack(spacing: 6) {
                TransportBadge(label: info.transportLabel, isWebRtc: info.isWebRtc)
                if !info.connectionLabel.isEmpty {
                    Text(info.connectionLabel)
                        .font(.caption2.weight(.medium))
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(.quaternary.opacity(0.6), in: Capsule())
                }
                if s.isRecordingIndicatorVisible {
                    Label("REC", systemImage: "record.circle.fill")
                        .font(.caption2.weight(.bold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Color.red, in: Capsule())
                }
            }
        }
    }

    // MARK: - Controls

    private var controls: some View {
        HStack(alignment: .top, spacing: 14) {
            CallControlButton(
                symbol: s.isMuted ? "mic.slash.fill" : "mic.fill",
                title: s.isMuted ? "Unmute" : "Mute",
                active: s.isMuted
            ) { state.callVerb("toggleMute") }

            CallControlButton(
                symbol: "circle.grid.3x3.fill",
                title: "Keypad",
                active: s.isKeypadVisible
            ) { withAnimation(.easeInOut(duration: 0.18)) { state.callVerb("toggleKeypad") } }

            CallControlButton(symbol: "speaker.wave.2.fill", title: "Audio", active: false) {
                showAudioDevices = true
            }

            CallControlButton(
                symbol: s.isOnHold ? "play.fill" : "pause.fill",
                title: s.isOnHold ? "Resume" : "Hold",
                active: s.isOnHold
            ) { state.callVerb("toggleHold") }
        }
    }

    @ViewBuilder
    private var bottomButtons: some View {
        if s.showIncomingButtons {
            HStack(spacing: 56) {
                CallEndButton(symbol: "phone.down.fill", title: "Decline", color: .red) { state.callVerb("reject") }
                CallEndButton(symbol: "phone.fill", title: "Answer", color: .green) { state.callVerb("answer") }
                    .keyboardShortcut(.defaultAction)
            }
        } else if s.showHangupButton {
            CallEndButton(symbol: "phone.down.fill", title: "End", color: .red) { state.callVerb("hangup") }
                .keyboardShortcut(.escape, modifiers: [])
        }
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
                HStack(spacing: 16) {
                    ForEach(row, id: \.0) { k in
                        KeypadKey(label: k.0, letters: k.1, size: 54) {
                            state.callVerb("sendDtmf", digit: k.0)
                        }
                    }
                }
            }
        }
    }
}

// MARK: - Call window pieces

private struct CallAvatar: View {
    let title: String
    let isIncoming: Bool
    let pulsing: Bool
    @State private var pulse = false

    private var initials: String? {
        let words = title.split(separator: " ").filter { $0.first?.isLetter == true }
        let letters = words.prefix(2).compactMap { $0.first }.map { String($0).uppercased() }
        return letters.isEmpty ? nil : letters.joined()
    }

    var body: some View {
        ZStack {
            Circle()
                .stroke(Color.accentColor.opacity(0.35), lineWidth: 2)
                .frame(width: 96, height: 96)
                .scaleEffect(pulse ? 1.18 : 1)
                .opacity(pulsing ? (pulse ? 0 : 0.9) : 0)

            Circle()
                .fill(LinearGradient(colors: [Color.accentColor.opacity(0.85), Color.accentColor.opacity(0.55)],
                                     startPoint: .topLeading, endPoint: .bottomTrailing))
                .frame(width: 88, height: 88)
                .shadow(color: Color.accentColor.opacity(0.25), radius: 10, y: 4)

            if let initials {
                Text(initials)
                    .font(.system(size: 32, weight: .semibold, design: .rounded))
                    .foregroundStyle(.white)
            } else {
                Image(systemName: isIncoming ? "phone.arrow.down.left.fill" : "person.fill")
                    .font(.system(size: 34, weight: .medium))
                    .foregroundStyle(.white)
            }
        }
        .frame(width: 110, height: 110)
        .onAppear { startPulse() }
        .onChange(of: pulsing) { _ in startPulse() }
    }

    private func startPulse() {
        pulse = false
        guard pulsing else { return }
        withAnimation(.easeOut(duration: 1.4).repeatForever(autoreverses: false)) { pulse = true }
    }
}

private struct CallControlButton: View {
    let symbol: String
    let title: String
    let active: Bool
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            VStack(spacing: 6) {
                Image(systemName: symbol)
                    .symbolRenderingMode(.hierarchical)
                    .font(.system(size: 19, weight: .medium))
                    .foregroundStyle(active ? Color.black : Color.primary)
                    .frame(width: 58, height: 58)
                    .background(Circle().fill(active ? Color.white : Color.primary.opacity(hover ? 0.16 : 0.10)))
                Text(title)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            .frame(width: 66)
            .contentShape(Rectangle())
        }
        .buttonStyle(PressScaleStyle())
        .focusable(false)
        .onHover { hover = $0 }
        .help(title)
    }
}

private struct CallEndButton: View {
    let symbol: String
    let title: String
    let color: Color
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 8) {
                Image(systemName: symbol)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 68, height: 68)
                    .background(Circle().fill(color))
                    .shadow(color: color.opacity(0.35), radius: 8, y: 3)
                Text(title)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(PressScaleStyle())
        .focusable(false)
        .help(title)
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

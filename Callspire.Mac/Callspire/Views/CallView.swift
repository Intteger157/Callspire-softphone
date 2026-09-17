import SwiftUI

struct CallView: View {
    @EnvironmentObject var state: AppState
    let info: CallWindowInfo
    private var s: CallState { state.call?.state ?? info.state }

    var body: some View {
        VStack(spacing: 16) {
            Text(info.windowTitle.isEmpty ? "Call" : info.windowTitle).font(.headline)
            Text(s.callerDisplay.isEmpty ? info.phoneNumber : s.callerDisplay).font(.largeTitle.bold())
            HStack {
                Text(info.transportLabel).font(.caption).padding(6).background(Color.accentColor, in: Capsule()).foregroundStyle(.white)
                Text(info.connectionLabel).font(.caption).foregroundStyle(.secondary)
                if s.isRecordingIndicatorVisible { Text("REC").font(.caption.bold()).foregroundStyle(.red) }
            }
            Text(s.statusText).foregroundStyle(s.statusTone == "bad" ? Color.red : (s.statusTone == "good" ? Color.green : .secondary))
            Text(s.timerText).font(.title3.monospacedDigit())

            if s.showControls {
                HStack(spacing: 20) {
                    control(s.isMuted ? "mic.slash.fill" : "mic.fill", active: s.isMuted) { state.callVerb("toggleMute") }
                    control(s.isOnHold ? "play.fill" : "pause.fill", active: s.isOnHold) { state.callVerb("toggleHold") }
                    control("circle.grid.3x3.fill", active: s.isKeypadVisible) { state.callVerb("toggleKeypad") }
                }
            }
            if s.isKeypadVisible {
                keypad
            }
            Spacer()
            if s.showIncomingButtons {
                HStack(spacing: 40) {
                    Button { state.callVerb("answer") } label: { Image(systemName: "phone.fill").font(.largeTitle).foregroundStyle(.white).frame(width: 64, height: 64).background(Color.green, in: Circle()) }
                    Button { state.callVerb("reject") } label: { Image(systemName: "phone.down.fill").font(.largeTitle).foregroundStyle(.white).frame(width: 64, height: 64).background(Color.red, in: Circle()) }
                }
            }
            if s.showHangupButton {
                Button { state.callVerb("hangup") } label: {
                    Image(systemName: "phone.down.fill").font(.largeTitle).foregroundStyle(.white)
                        .frame(width: 64, height: 64).background(Color.red, in: Circle())
                }
            }
        }
        .padding()
        .onDisappear { state.closeCall() }
    }

    private var keypad: some View {
        let keys = [["1","2","3"],["4","5","6"],["7","8","9"],["*","0","#"]]
        return VStack {
            ForEach(keys, id: \.self) { row in
                HStack {
                    ForEach(row, id: \.self) { k in
                        Button { state.callVerb("sendDtmf", digit: k) } label: {
                            Text(k).frame(width: 48, height: 48).background(Color.primary.opacity(0.08), in: Circle())
                        }.buttonStyle(.plain)
                    }
                }
            }
        }
    }

    private func control(_ symbol: String, active: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.title2)
                .frame(width: 52, height: 52)
                .background(active ? Color.accentColor : Color.primary.opacity(0.08), in: Circle())
                .foregroundStyle(active ? .white : .primary)
        }.buttonStyle(.plain)
    }
}

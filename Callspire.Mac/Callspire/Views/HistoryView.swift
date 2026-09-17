import SwiftUI

struct HistoryView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("History").font(.title2.bold())
                if let hint = state.main.historyFilterHint, !hint.isEmpty {
                    Text(hint).font(.caption).foregroundStyle(.secondary)
                    Button("Clear filter") { Task { try? await state.ipc.requestVoid("clearHistoryFilter") } }
                }
                Spacer()
                Button("Clear all") {
                    Task { try? await state.ipc.requestVoid("clearHistory") }
                }
            }.padding(.horizontal)

            if state.main.history.isEmpty {
                Spacer()
                Text("No calls yet").foregroundStyle(.secondary).frame(maxWidth: .infinity)
                Spacer()
            } else {
                List(state.main.history) { item in
                    HStack {
                        Image(systemName: item.isMissed ? "phone.down.fill" : (item.isIncoming ? "arrow.down.left" : "arrow.up.right"))
                            .foregroundStyle(item.isMissed ? Color.red : (item.isIncoming ? Color.green : Color.accentColor))
                        VStack(alignment: .leading) {
                            HStack {
                                Text(item.phoneNumberDisplay).font(.headline)
                                Text(item.transportLabel).font(.caption2).padding(.horizontal, 6).padding(.vertical, 2)
                                    .background(Color.accentColor, in: Capsule()).foregroundStyle(.white)
                                Text(item.connectionLabel).font(.caption).foregroundStyle(.secondary)
                            }
                            Text(item.callTimeText).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        VStack(alignment: .trailing) {
                            Text(item.durationText).font(.caption)
                            Text(item.status).font(.caption).foregroundStyle(item.statusKind == "bad" ? Color.red : .secondary)
                        }
                        Button { state.setPhone(item.phoneNumber); state.placeCall() } label: {
                            Image(systemName: "phone.fill").foregroundStyle(.white)
                                .frame(width: 32, height: 32).background(Color.green, in: Circle())
                        }.buttonStyle(.plain)
                    }
                    .contentShape(Rectangle())
                    .onTapGesture { state.openHistoryDetails(item) }
                }
            }
        }
        .padding()
    }
}

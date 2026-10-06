import SwiftUI

/// Call History page — WPF parity: title, optional drill-down filter chip, red "Clear History",
/// one card per call (direction icon, number, transport badge, time, duration, status, call button).
struct HistoryView: View {
    @EnvironmentObject var state: AppState
    @State private var confirmClear = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline) {
                SectionHeader(title: "Call History")
                Spacer()
                Button(role: .destructive) { confirmClear = true } label: {
                    Text("Clear History").foregroundStyle(.red)
                }
                .disabled(state.main.history.isEmpty)
            }
            if let hint = state.main.historyFilterHint, !hint.isEmpty {
                HStack(spacing: 8) {
                    Label(hint, systemImage: "line.3.horizontal.decrease.circle").font(.callout)
                    Button("Show all") { state.clearHistoryFilter() }.controlSize(.small)
                }
                .padding(8)
                .background(Color.accentColor.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
            }

            if state.main.history.isEmpty {
                Spacer()
                VStack(spacing: 8) {
                    Image(systemName: "clock.arrow.circlepath").font(.system(size: 40)).foregroundStyle(.tertiary)
                    Text("No calls yet").foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity)
                Spacer()
            } else {
                ScrollView {
                    LazyVStack(spacing: 8) {
                        ForEach(state.main.history) { item in
                            HistoryRow(item: item)
                        }
                    }
                    .padding(.bottom, 12)
                }
            }
        }
        .padding(20)
        .confirmationDialog("Clear all call history?", isPresented: $confirmClear, titleVisibility: .visible) {
            Button("Clear History", role: .destructive) { state.clearHistory() }
            Button("Cancel", role: .cancel) { }
        } message: {
            Text("This removes every entry from the local call history. Recordings on disk are kept.")
        }
    }
}

private struct HistoryRow: View {
    @EnvironmentObject var state: AppState
    let item: HistoryItem
    @State private var hover = false

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: directionSymbol)
                .font(.title3)
                .foregroundStyle(item.isMissed ? Color.red : (item.isIncoming ? Color.accentColor : Color.green))
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    Text(item.phoneNumberDisplay.isEmpty ? item.phoneNumber : item.phoneNumberDisplay)
                        .font(.body.weight(.semibold))
                    TransportBadge(label: item.transportLabel, isWebRtc: item.isWebRtc)
                    if !item.connectionLabel.isEmpty {
                        Text(item.connectionLabel).font(.caption2).foregroundStyle(.secondary)
                    }
                    if item.hasRecording {
                        Image(systemName: "waveform").font(.caption2).foregroundStyle(.secondary).help("Recording available")
                    }
                }
                HStack(spacing: 8) {
                    Text(item.callTimeText.isEmpty ? item.callTime.historyStamp : item.callTimeText)
                        .font(.caption).foregroundStyle(.secondary)
                    if let cid = item.outboundCallerId, !cid.isEmpty {
                        Text("via \(cid)").font(.caption).foregroundStyle(.secondary)
                    }
                    if !item.crmStatus.isEmpty {
                        Text(item.crmStatus).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 3) {
                Text(item.durationText).font(.caption).foregroundStyle(.secondary)
                Text(item.status).font(.caption).foregroundStyle(statusColor)
            }
            Button {
                state.setPhone(item.phoneNumber)
                state.placeCall(number: item.phoneNumber)
            } label: {
                Image(systemName: "phone.fill")
                    .foregroundStyle(.white)
                    .frame(width: 32, height: 32)
                    .background(state.main.canCall ? Color.green : Color.gray.opacity(0.5), in: Circle())
            }
            .buttonStyle(.plain)
            .disabled(!state.main.canCall)
            .help("Call back")
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(Color(nsColor: .controlBackgroundColor))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(hover ? Color.accentColor.opacity(0.6) : Color(nsColor: .separatorColor), lineWidth: 1))
        )
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture { state.openHistoryDetails(item) }
        .contextMenu {
            Button("Call") { state.placeCall(number: item.phoneNumber) }
            Button("Details…") { state.openHistoryDetails(item) }
            Button("Copy number") { Pasteboard.copy(item.phoneNumber) }
            Button("Use in dialer") { state.setPhone(item.phoneNumber); state.selectedNav = .dialer }
        }
    }

    private var directionSymbol: String {
        if item.isMissed { return "phone.arrow.down.left" }
        return item.isIncoming ? "phone.arrow.down.left" : "phone.arrow.up.right"
    }

    private var statusColor: Color {
        switch item.statusKind.lowercased() {
        case "good", "success": return .green
        case "bad", "error", "missed": return .red
        case "warning": return .orange
        default: return .secondary
        }
    }
}

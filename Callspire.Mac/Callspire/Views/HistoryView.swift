import SwiftUI

/// Call History — native List + SF Symbols.
struct HistoryView: View {
    @EnvironmentObject var state: AppState
    @State private var confirmClear = false

    var body: some View {
        VStack(spacing: 0) {
            historyHeader
            Group {
            if state.main.history.isEmpty {
                VStack(spacing: 10) {
                    Image(systemName: "clock")
                        .font(.system(size: 40, weight: .light))
                        .symbolRenderingMode(.hierarchical)
                        .foregroundStyle(.tertiary)
                    Text("No Calls Yet")
                        .font(.title3.weight(.medium))
                    Text("Completed calls will appear here.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(state.main.history) { item in
                    HistoryRow(item: item)
                        .contentShape(Rectangle())
                        .onTapGesture { state.openHistoryDetails(item) }
                        .contextMenu {
                            Button("Call…") { state.placeCall(number: item.phoneNumber, forceSelection: state.main.showSplitCallButtons) }
                            Button("Details…") { state.openHistoryDetails(item) }
                            Button("Copy Number") { Pasteboard.copy(item.phoneNumber) }
                            Button("Use in Dialer") {
                                state.setPhone(item.phoneNumber)
                                state.selectedNav = .dialer
                            }
                        }
                }
                .listStyle(.inset)
            }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            if let hint = state.main.historyFilterHint, !hint.isEmpty {
                HStack(spacing: 8) {
                    Label(hint, systemImage: "line.3.horizontal.decrease.circle")
                        .font(.callout)
                    Spacer()
                    Button("Show All") { state.clearHistoryFilter() }
                        .controlSize(.small)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(.bar)
            }
        }
        .confirmationDialog("Clear all call history?", isPresented: $confirmClear, titleVisibility: .visible) {
            Button("Clear History", role: .destructive) { state.clearHistory() }
            Button("Cancel", role: .cancel) { }
        } message: {
            Text("This removes every entry from the local call history. Recordings on disk are kept.")
        }
    }

    /// In-content header (not window toolbar) — avoids title-bar jump when switching tabs.
    private var historyHeader: some View {
        HStack {
            Text("Call History")
                .font(.title3.weight(.semibold))
            Spacer()
            Button("Clear History", role: .destructive) { confirmClear = true }
                .disabled(state.main.history.isEmpty)
        }
        .padding(.horizontal, 16)
        .padding(.top, 12)
        .padding(.bottom, 8)
    }
}

private struct HistoryRow: View {
    @EnvironmentObject var state: AppState
    let item: HistoryItem

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: directionSymbol)
                .symbolRenderingMode(.hierarchical)
                .font(.title3)
                .foregroundStyle(item.isMissed ? Color.red : (item.isIncoming ? Color.accentColor : Color.green))
                .frame(width: 24)

            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(item.phoneNumberDisplay.isEmpty ? item.phoneNumber : item.phoneNumberDisplay)
                        .font(.body.weight(.medium))
                    TransportBadge(label: item.transportLabel, isWebRtc: item.isWebRtc)
                    if item.hasRecording {
                        Image(systemName: "waveform")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .help("Recording available")
                    }
                }
                Text(item.callTimeText.isEmpty ? item.callTime.historyStamp : item.callTimeText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Spacer(minLength: 8)

            VStack(alignment: .trailing, spacing: 2) {
                Text(item.durationText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                Text(item.status)
                    .font(.caption)
                    .foregroundStyle(statusColor)
            }

            OutboundCallButton(phoneNumber: item.phoneNumber, compact: true)
        }
        .padding(.vertical, 2)
    }

    private var directionSymbol: String {
        if item.isMissed { return "phone.arrow.down.left.fill" }
        return item.isIncoming ? "phone.arrow.down.left.fill" : "phone.arrow.up.right.fill"
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

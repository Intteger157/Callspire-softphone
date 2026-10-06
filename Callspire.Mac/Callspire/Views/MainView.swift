import SwiftUI
import AppKit

struct MainView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        HStack(spacing: 0) {
            rail
            Divider()
            detail
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        // Keep one window title so macOS traffic-light / toolbar layout does not reflow on tab change.
        .navigationTitle("Callspire")
        .alert(item: $state.alert) { a in
            Alert(title: Text(a.title), message: Text(a.text), dismissButton: .default(Text("OK")))
        }
        .sheet(item: $state.update) { u in UpdateAvailableSheet(info: u).environmentObject(state) }
        .sheet(item: Binding(get: { state.connectionPicker.map { Identified($0) } }, set: { if $0 == nil { state.finishConnection(ConnectionSelectionResult()) } })) { wrap in
            ConnectionSelectionSheet(request: wrap.value).environmentObject(state)
        }
        .sheet(isPresented: Binding(get: { state.gatewayLeadPhone != nil }, set: { if !$0 { state.finishLead(LeadSelectionResult(proceed: false, cancelled: true, leadId: nil)) } })) {
            GatewayLeadSheet(phone: state.gatewayLeadPhone ?? "").environmentObject(state)
        }
        .sheet(item: Binding(get: { state.kommoPicker.map { Identified($0) } }, set: { if $0 == nil { state.finishKommo(nil) } })) { wrap in
            LeadSelectionView(request: wrap.value).environmentObject(state)
        }
        .sheet(item: Binding(get: { state.callDetails.map { Identified($0) } }, set: { if $0 == nil { state.callDetails = nil } })) { wrap in
            CallDetailsView(details: wrap.value).environmentObject(state)
        }
        .onChange(of: state.call?.sessionId) { id in
            if id != nil { openWindow(id: "call") }
        }
        .onChange(of: state.settingsOpenRequest) { _ in openWindow(id: "settings") }
        .onChange(of: state.logsOpenRequest) { _ in openWindow(id: "logs") }
    }

    // MARK: - Icon rail

    private var rail: some View {
        VStack(spacing: 6) {
            avatar
                .padding(.bottom, 10)

            ForEach(AppState.NavItem.allCases) { item in
                RailButton(
                    symbol: item.symbol,
                    selectedSymbol: item.symbolSelected,
                    title: item.title,
                    isSelected: state.selectedNav == item
                ) { state.selectedNav = item }
            }

            Spacer()

            RailButton(symbol: "gearshape", selectedSymbol: "gearshape.fill", title: "Settings", isSelected: false) {
                state.openSettings()
            }
            RailButton(symbol: "text.alignleft", selectedSymbol: "text.alignleft", title: "Logs", isSelected: false) {
                state.openLogs()
            }
        }
        .padding(.vertical, 14)
        .frame(width: 72)
        .frame(maxHeight: .infinity)
        .background(VisualEffectBackground(material: .sidebar))
    }

    private var avatar: some View {
        let line = state.main.main
        return ZStack(alignment: .bottomTrailing) {
            Circle()
                .fill(Color.accentColor.gradient)
                .frame(width: 38, height: 38)
                .overlay(Text(initial).font(.headline).foregroundStyle(.white))
            Circle()
                .fill(line.isError ? Color.red : (line.isOnline ? Color.green : Color.gray))
                .frame(width: 11, height: 11)
                .overlay(Circle().strokeBorder(Color(nsColor: .windowBackgroundColor), lineWidth: 2))
        }
        .help(accountHelp)
    }

    private var accountHelp: String {
        let name = state.main.main.displayName.isEmpty ? "Callspire" : state.main.main.displayName
        let status = state.main.accountText.isEmpty ? state.statusLine : state.main.accountText
        return "\(name) — \(status)"
    }

    private var initial: String {
        let name = state.main.main.displayName.trimmingCharacters(in: .whitespaces)
        return String((name.isEmpty ? "C" : name).prefix(1)).uppercased()
    }

    // MARK: - Detail

    @ViewBuilder
    private var detail: some View {
        switch state.selectedNav {
        case .dialer: DialerView()
        case .history: HistoryView()
        case .statistics: StatisticsView()
        }
    }
}

/// Large icon-only navigation button with tooltip (labels hidden by design).
private struct RailButton: View {
    let symbol: String
    let selectedSymbol: String
    let title: String
    let isSelected: Bool
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Image(systemName: isSelected ? selectedSymbol : symbol)
                .font(.system(size: 21, weight: .regular))
                .frame(width: 46, height: 46)
                .foregroundStyle(isSelected ? Color.accentColor : Color.secondary)
                .background(
                    RoundedRectangle(cornerRadius: 11, style: .continuous)
                        .fill(isSelected ? Color.accentColor.opacity(0.16) : Color.primary.opacity(hover ? 0.07 : 0))
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(title)
        .accessibilityLabel(title)
    }
}

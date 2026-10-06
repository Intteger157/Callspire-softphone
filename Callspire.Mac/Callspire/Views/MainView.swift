import SwiftUI
import AppKit

struct MainView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        NavigationSplitView {
            sidebar
                .navigationSplitViewColumnWidth(min: 180, ideal: 200, max: 240)
        } detail: {
            Group {
                switch state.selectedNav {
                case .dialer: DialerView()
                case .history: HistoryView()
                case .statistics: StatisticsView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .navigationTitle("Softphone")
            .navigationSubtitle(state.main.accountText)
        }
        .toolbar {
            ToolbarItem(placement: .automatic) {
                HStack(spacing: 6) {
                    StatusDot(tone: state.main.anyOnline ? .online : (state.connected ? .offline : .error))
                    Text(state.main.accountText.isEmpty ? state.statusLine : state.main.accountText)
                        .font(.callout).foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                .help(state.main.accountToolTip.isEmpty ? state.statusLine : state.main.accountToolTip)
            }
        }
        // Modals the sidecar can request at any time.
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
        // Window openers: AppState bumps a counter / sets `call`; these observers turn that into openWindow().
        .onChange(of: state.call?.sessionId) { id in
            if id != nil { openWindow(id: "call") }
        }
        .onChange(of: state.settingsOpenRequest) { _ in openWindow(id: "settings") }
        .onChange(of: state.logsOpenRequest) { _ in openWindow(id: "logs") }
    }

    private var sidebar: some View {
        VStack(spacing: 0) {
            List(selection: Binding(get: { state.selectedNav }, set: { if let v = $0 { state.selectedNav = v } })) {
                Section {
                    ForEach(AppState.NavItem.allCases) { item in
                        Label(item.title, systemImage: item.symbol).tag(item)
                    }
                } header: {
                    accountHeader
                }
            }
            .listStyle(.sidebar)

            Divider()
            VStack(alignment: .leading, spacing: 2) {
                sidebarButton("Settings", symbol: "gearshape") { state.openSettings() }
                sidebarButton("Logs", symbol: "doc.text") { state.openLogs() }
            }
            .padding(8)
        }
    }

    private var accountHeader: some View {
        HStack(spacing: 10) {
            ZStack {
                Circle().fill(Color.accentColor).frame(width: 34, height: 34)
                Text(initial).font(.headline).foregroundStyle(.white)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(state.main.main.displayName.isEmpty ? "Callspire" : state.main.main.displayName)
                    .font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                Text(state.main.main.text.isEmpty ? state.statusLine : state.main.main.text)
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .padding(.vertical, 6)
        .textCase(nil)
    }

    private var initial: String {
        let name = state.main.main.displayName.trimmingCharacters(in: .whitespaces)
        return String((name.isEmpty ? "S" : name).prefix(1)).uppercased()
    }

    private func sidebarButton(_ title: String, symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: symbol)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 8).padding(.vertical, 6)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

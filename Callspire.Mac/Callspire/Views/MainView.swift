import SwiftUI
import AppKit

struct MainView: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow

    private enum SidebarItem: Hashable {
        case nav(AppState.NavItem)
        case settings
        case logs
    }

    @State private var selection: SidebarItem = .nav(.dialer)

    var body: some View {
        NavigationSplitView {
            List(selection: $selection) {
                Section {
                    accountRow
                }

                Section {
                    ForEach(AppState.NavItem.allCases) { item in
                        Label {
                            Text(item.title)
                        } icon: {
                            Image(systemName: selection == .nav(item) ? item.symbolSelected : item.symbol)
                                .symbolRenderingMode(.hierarchical)
                        }
                        .tag(SidebarItem.nav(item))
                    }
                }

                Section {
                    Label("Settings", systemImage: "gearshape")
                        .tag(SidebarItem.settings)
                    Label("Logs", systemImage: "text.alignleft")
                        .tag(SidebarItem.logs)
                }
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 190, ideal: 210, max: 250)
        } detail: {
            detail
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .navigationTitle(detailTitle)
        .toolbar {
            ToolbarItem(placement: .automatic) {
                StatusPill(
                    text: state.main.accountText.isEmpty ? state.statusLine : state.main.accountText,
                    tone: state.main.anyOnline ? .online : (state.connected ? .offline : .error)
                )
                .help(state.main.accountToolTip.isEmpty ? state.statusLine : state.main.accountToolTip)
            }
        }
        .onChange(of: selection) { item in
            switch item {
            case .nav(let nav):
                state.selectedNav = nav
            case .settings:
                state.openSettings()
                // Keep main nav selection visually stable after opening Settings window.
                selection = .nav(state.selectedNav)
            case .logs:
                state.openLogs()
                selection = .nav(state.selectedNav)
            }
        }
        .onChange(of: state.selectedNav) { nav in
            if case .nav = selection { selection = .nav(nav) }
        }
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
        .onAppear { selection = .nav(state.selectedNav) }
    }

    @ViewBuilder
    private var detail: some View {
        switch state.selectedNav {
        case .dialer: DialerView()
        case .history: HistoryView()
        case .statistics: StatisticsView()
        }
    }

    private var detailTitle: String {
        switch state.selectedNav {
        case .dialer: return "Callspire"
        case .history: return "Call History"
        case .statistics: return "Call Statistics"
        }
    }

    private var accountRow: some View {
        HStack(spacing: 10) {
            ZStack {
                Circle()
                    .fill(Color.accentColor.opacity(0.85))
                    .frame(width: 28, height: 28)
                Text(initial)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.white)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(state.main.main.displayName.isEmpty ? "Callspire" : state.main.main.displayName)
                    .font(.subheadline.weight(.semibold))
                    .lineLimit(1)
                Text(state.main.main.text.isEmpty ? state.statusLine : state.main.main.text)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }

    private var initial: String {
        let name = state.main.main.displayName.trimmingCharacters(in: .whitespaces)
        return String((name.isEmpty ? "C" : name).prefix(1)).uppercased()
    }
}

import SwiftUI

struct MainView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        NavigationSplitView {
            sidebar
                .navigationSplitViewColumnWidth(min: 72, ideal: 80, max: 96)
        } detail: {
            ZStack {
                switch state.selectedNav {
                case .dialer: DialerView()
                case .history: HistoryView()
                case .statistics: StatisticsView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .toolbar {
            ToolbarItem(placement: .principal) {
                Text("Callspire").font(.headline)
            }
            ToolbarItem(placement: .automatic) {
                HStack(spacing: 8) {
                    Circle().fill(state.main.anyOnline ? Color.green : Color.secondary).frame(width: 8, height: 8)
                    Text(state.main.accountText.isEmpty ? state.statusLine : state.main.accountText)
                        .foregroundStyle(.secondary)
                        .font(.caption)
                }
            }
        }
        .sheet(isPresented: $state.showSettings) { SettingsView().environmentObject(state).frame(width: 920, height: 640) }
        .sheet(isPresented: $state.showLogs) { LogView().environmentObject(state).frame(width: 720, height: 480) }
        .sheet(item: $state.alert) { a in AlertView(alert: a) }
        .sheet(item: $state.update) { u in UpdateAvailableSheet(info: u).environmentObject(state) }
        .sheet(item: Binding(get: { state.connectionPicker.map { Identified($0) } }, set: { if $0 == nil { state.finishConnection(ConnectionSelectionResult()) } })) { wrap in
            ConnectionSelectionSheet(request: wrap.value)
        }
        .sheet(isPresented: Binding(get: { state.gatewayLeadPhone != nil }, set: { if !$0 { state.finishLead(LeadSelectionResult(proceed: false, cancelled: true, leadId: nil)) } })) {
            GatewayLeadSheet(phone: state.gatewayLeadPhone ?? "")
        }
        .sheet(item: Binding(get: { state.kommoPicker.map { Identified($0) } }, set: { if $0 == nil { state.finishKommo(nil) } })) { wrap in
            LeadSelectionView(request: wrap.value)
        }
        .sheet(item: Binding(get: { state.callDetails.map { Identified($0) } }, set: { if $0 == nil { state.callDetails = nil } })) { wrap in
            CallDetailsView(details: wrap.value)
        }
        .background(CallWindowBinder())
    }

    private var sidebar: some View {
        VStack(spacing: 8) {
            ZStack {
                Circle().fill(Color.accentColor).frame(width: 44, height: 44)
                Text("C").foregroundStyle(.white).font(.headline)
            }
            .padding(.top, 12)
            ForEach(AppState.NavItem.allCases) { item in
                Button { state.selectedNav = item } label: {
                    Image(systemName: item.symbol)
                        .font(.title2)
                        .frame(width: 52, height: 52)
                        .foregroundStyle(state.selectedNav == item ? Color.white : Color.primary)
                        .background(state.selectedNav == item ? Color.accentColor : Color.clear, in: RoundedRectangle(cornerRadius: 12))
                }
                .buttonStyle(.plain)
                .help(item.title)
            }
            Spacer()
            Button { state.openSettings() } label: {
                Image(systemName: "gearshape.fill").font(.title2).frame(width: 52, height: 52)
            }.buttonStyle(.plain).help("Settings")
            Button { state.showLogs = true } label: {
                Image(systemName: "doc.text.fill").font(.title2).frame(width: 52, height: 52)
            }.buttonStyle(.plain).help("Logs").padding(.bottom, 12)
        }
        .frame(maxWidth: .infinity)
        .background(.ultraThinMaterial)
    }
}

struct Identified<T>: Identifiable {
    let id = UUID()
    let value: T
    init(_ value: T) { self.value = value }
}

private struct CallWindowBinder: View {
    @EnvironmentObject var state: AppState
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Color.clear.onChange(of: state.call?.sessionId) { _, id in
            if id != nil { openWindow(id: "call") }
        }
    }
}

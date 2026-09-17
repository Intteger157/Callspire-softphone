import SwiftUI

struct StatisticsView: View {
    @EnvironmentObject var state: AppState
    private var st: StatisticsState { state.main.statistics }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack {
                    Text("Statistics").font(.title2.bold())
                    Spacer()
                    Button("Export CSV") {
                        Task { _ = try? await state.ipc.requestRaw("exportStatisticsCsv") }
                    }
                }
                HStack {
                    Picker("Period", selection: bindIndex("periodIndex", st.periodIndex)) {
                        ForEach(Array(st.periodOptions.enumerated()), id: \.offset) { i, n in Text(n).tag(i) }
                    }.frame(width: 180)
                    Picker("Connection", selection: bindIndex("connectionIndex", st.connectionIndex)) {
                        ForEach(Array(st.connectionOptions.enumerated()), id: \.offset) { i, n in Text(n).tag(i) }
                    }.frame(width: 180)
                    Picker("Direction", selection: bindIndex("directionIndex", st.directionIndex)) {
                        ForEach(Array(st.directionOptions.enumerated()), id: \.offset) { i, n in Text(n).tag(i) }
                    }.frame(width: 150)
                }
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 160))], spacing: 12) {
                    kpi("Total", st.kpiTotal, drill: "all")
                    kpi("Answered", st.kpiAnswered, drill: "answered")
                    kpi("Unanswered", st.kpiUnanswered, drill: "unanswered")
                    kpi("Talk time", st.kpiTalkTime, drill: nil)
                    kpi("Contact rate", st.kpiContactRate, drill: nil)
                    kpi("Missed", st.kpiMissed, drill: "missed")
                }
                chart("Calls by day", st.daily.map { ($0.label, $0.barHeight) })
                chart("Activity by hour", st.hourly.map { ($0.label, $0.barHeight) })
                if !st.topNumbers.isEmpty {
                    Text("Top numbers").font(.headline)
                    ForEach(st.topNumbers) { row in
                        Button {
                            Task { try? await state.ipc.requestVoid("setHistoryDrillDown", params: ["drill": "all", "phoneNumber": row.phoneNumber]) }
                            state.selectedNav = .history
                        } label: {
                            HStack { Text(row.name); Spacer(); Text(row.detail).foregroundStyle(.secondary) }
                        }.buttonStyle(.plain)
                    }
                }
            }.padding()
        }
    }

    private func bindIndex(_ key: String, _ current: Int) -> Binding<Int> {
        Binding(get: { current }, set: { v in
            Task {
                try? await state.ipc.requestVoid("setStatisticsFilter", params: [key: v])
            }
        })
    }

    private func kpi(_ title: String, _ value: String, drill: String?) -> some View {
        Button {
            if let drill {
                Task { try? await state.ipc.requestVoid("setHistoryDrillDown", params: ["drill": drill]) }
                state.selectedNav = .history
            }
        } label: {
            VStack(alignment: .leading) {
                Text(title).font(.caption).foregroundStyle(.secondary)
                Text(value.isEmpty ? "—" : value).font(.title3.bold())
            }
            .padding().frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 10))
        }.buttonStyle(.plain)
    }

    private func chart(_ title: String, _ bars: [(String, Double)]) -> some View {
        VStack(alignment: .leading) {
            Text(title).font(.headline)
            HStack(alignment: .bottom, spacing: 4) {
                ForEach(Array(bars.enumerated()), id: \.offset) { _, b in
                    VStack {
                        RoundedRectangle(cornerRadius: 2).fill(Color.accentColor)
                            .frame(width: 10, height: max(4, CGFloat(b.1) * 80))
                    }
                }
            }.frame(height: 90, alignment: .bottom)
        }
    }
}

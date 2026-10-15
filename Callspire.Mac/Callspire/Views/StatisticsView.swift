import SwiftUI

/// Call Statistics — same chrome as Call History / dialer. Charts are neutral; accent only on the Apply CTA.
struct StatisticsView: View {
    @EnvironmentObject var state: AppState
    @State private var customFrom = Calendar.current.date(byAdding: .day, value: -7, to: Date()) ?? Date()
    @State private var customTo = Date()

    private var st: StatisticsState { state.main.statistics }
    private var accent: Color { MacTheme.actionAccent }
    private var chartFill: Color { Color.secondary }

    var body: some View {
        VStack(spacing: 0) {
            header
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    filters
                    if st.hasLegacyUnknownConnection {
                        Label("Some older calls have no connection information and are counted under “Unknown”.", systemImage: "info.circle")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                    kpiGrid
                    chartsRow
                    breakdownRow
                    topNumbers
                }
                .padding(.horizontal, 16)
                .padding(.bottom, 20)
                .frame(maxWidth: 960, alignment: .leading)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear {
            if let f = st.customFrom { customFrom = f }
            if let t = st.customTo { customTo = t }
        }
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 2) {
                Text("Call Statistics")
                    .font(.title3.weight(.semibold))
                if !st.periodHint.isEmpty {
                    Text(st.periodHint)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }
            Spacer()
            Button { state.exportStatisticsCsv() } label: {
                Label("Export CSV", systemImage: "square.and.arrow.up")
            }
            .disabled(!st.hasReport)
        }
        .padding(.horizontal, 16)
        .padding(.top, 12)
        .padding(.bottom, 8)
    }

    // MARK: Filters

    private var filters: some View {
        Card(padding: 12) {
            HStack(spacing: 16) {
                filterPicker("Period", options: st.periodOptions, index: st.periodIndex) { state.setStatisticsFilter(periodIndex: $0) }
                if st.secondaryEnabled || st.connectionOptions.count > 1 {
                    filterPicker("Connection", options: st.connectionOptions, index: st.connectionIndex) { state.setStatisticsFilter(connectionIndex: $0) }
                }
                filterPicker("Direction", options: st.directionOptions, index: st.directionIndex) { state.setStatisticsFilter(directionIndex: $0) }
                if st.isCustomPeriod {
                    DatePicker("From", selection: $customFrom, displayedComponents: .date).labelsHidden()
                    DatePicker("To", selection: $customTo, displayedComponents: .date).labelsHidden()
                    Button("Apply") { applyCustomRange() }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
                        .tint(accent)
                }
                Spacer(minLength: 0)
            }
        }
    }

    private func applyCustomRange() {
        let cal = Calendar.current
        let from = cal.startOfDay(for: customFrom)
        let to = cal.date(byAdding: DateComponents(day: 1, second: -1), to: cal.startOfDay(for: customTo)) ?? customTo
        state.setStatisticsFilter(customFrom: from, customTo: to)
    }

    private func filterPicker(_ title: String, options: [String], index: Int, onChange: @escaping (Int) -> Void) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Picker(title, selection: Binding(get: { index }, set: onChange)) {
                ForEach(Array(options.enumerated()), id: \.offset) { i, label in Text(label).tag(i) }
            }
            .labelsHidden()
            .frame(minWidth: 132)
        }
    }

    // MARK: KPI

    private var kpiGrid: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 148), spacing: 10)], spacing: 10) {
            kpi("Total", st.kpiTotal, st.kpiDirectionHint, drill: { state.drillDownHistory("all") })
            kpi("Answered", st.kpiAnswered, st.kpiSuccessRate, emphasis: .green, drill: { state.drillDownHistory("answered") })
            kpi("Unanswered", st.kpiUnanswered, st.kpiUnansweredHint, emphasis: .orange, drill: { state.drillDownHistory("unanswered") })
            kpi("Talk time", st.kpiTalkTime, st.kpiAvgTalk, drill: nil)
            kpi("Contact rate", st.kpiContactRate, st.kpiRingTime, drill: nil)
            kpi("Missed", st.kpiMissed, st.kpiFailedCancelled, emphasis: .red, drill: { state.drillDownHistory("missed") })
        }
    }

    @ViewBuilder
    private func kpi(_ title: String, _ value: String, _ hint: String, emphasis: Color? = nil, drill: (() -> Void)?) -> some View {
        let valueColor = emphasis ?? Color.primary
        let tile = Card(padding: 12) {
            VStack(alignment: .leading, spacing: 6) {
                Text(title)
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
                Text(value.isEmpty ? "0" : value)
                    .font(.system(size: 24, weight: .semibold, design: .rounded))
                    .foregroundStyle(valueColor)
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
                Text(hint.isEmpty ? " " : hint)
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity, minHeight: 72, alignment: .leading)
        }
        if let drill {
            Button(action: drill) { tile }
                .buttonStyle(.plain)
                .help("Show in Call History")
        } else {
            tile
        }
    }

    // MARK: Charts

    @ViewBuilder
    private var chartsRow: some View {
        if #available(macOS 13.0, *) {
            StatisticsChartsVentura(st: st, chartFill: chartFill)
        } else {
            montereyChartsSummary
        }
    }

    private var montereyChartsSummary: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                Text("Charts").font(.headline)
                Text("Bar charts need macOS 13 (Ventura) or newer. KPI cards, breakdowns, and CSV export work on Monterey.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                if !st.peakHourText.isEmpty {
                    Text(st.peakHourText).font(.caption).foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    // MARK: Breakdowns

    private var breakdownRow: some View {
        HStack(alignment: .top, spacing: 10) {
            statListCard("Outcome", rows: [
                st.outcomeAnswered, st.outcomeCancelled, st.outcomeAgentCancel,
                st.outcomeFailed, st.outcomeMissed
            ])
            statListCard("SIP vs WebRTC", rows: st.connectionCompare.map {
                $0.detail.isEmpty ? $0.name : "\($0.name) · \($0.detail)"
            }, empty: st.connectionCompare.isEmpty)
            statListCard("Kommo", rows: kommoRows, empty: kommoRows.isEmpty, kommoDrill: true)
            statListCard("Outbound Caller ID", rows: st.callerIds.map {
                $0.detail.isEmpty ? $0.name : "\($0.name) · \($0.detail)"
            }, empty: st.callerIds.isEmpty)
        }
    }

    private var kommoRows: [String] {
        if !state.main.amoCrmConfigured { return ["Kommo integration is disabled."] }
        if st.crmLines.isEmpty { return [] }
        return st.crmLines.map(\.name)
    }

    private func statListCard(_ title: String, rows: [String], empty: Bool = false, kommoDrill: Bool = false) -> some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(.headline)
                if empty && rows.isEmpty {
                    Text("No data for this period.").font(.callout).foregroundStyle(.secondary)
                } else {
                    ForEach(Array(rows.enumerated()), id: \.offset) { idx, text in
                        if kommoDrill, idx < st.crmLines.count, state.main.amoCrmConfigured {
                            Button { state.drillDownHistory(st.crmLines[idx].detail) } label: {
                                metricLine(text)
                            }
                            .buttonStyle(.plain)
                        } else {
                            metricLine(text)
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func metricLine(_ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Circle().fill(chartFill.opacity(0.85)).frame(width: 5, height: 5)
            Text(text.isEmpty ? "—" : text)
                .font(.callout)
                .foregroundStyle(.primary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 2)
    }

    private var topNumbers: some View {
        Card {
            VStack(alignment: .leading, spacing: 4) {
                Text("Top numbers").font(.headline)
                Text("Click a row for Call History")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if st.topNumbers.isEmpty {
                    Text("No calls in this period.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .padding(.top, 4)
                } else {
                    ForEach(Array(st.topNumbers.enumerated()), id: \.element.id) { index, row in
                        Button {
                            state.drillDownHistory("all", phoneNumber: row.phoneNumber.isEmpty ? row.name : row.phoneNumber)
                        } label: {
                            HStack {
                                Text("\(index + 1).")
                                    .font(.caption.monospaced())
                                    .foregroundStyle(.secondary)
                                    .frame(width: 22, alignment: .trailing)
                                Text(row.name).font(.callout.weight(.medium))
                                Spacer()
                                Text(row.detail).font(.callout).foregroundStyle(.secondary)
                            }
                            .padding(.vertical, 6)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        if index < st.topNumbers.count - 1 { Divider() }
                    }
                }
            }
        }
    }
}

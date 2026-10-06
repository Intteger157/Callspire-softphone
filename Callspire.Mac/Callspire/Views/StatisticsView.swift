import SwiftUI
import Charts

/// Call Statistics — filters, KPI tiles, charts and breakdowns.
struct StatisticsView: View {
    @EnvironmentObject var state: AppState
    @State private var customFrom = Calendar.current.date(byAdding: .day, value: -7, to: Date()) ?? Date()
    @State private var customTo = Date()

    private var st: StatisticsState { state.main.statistics }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                header
                filters
                if st.hasLegacyUnknownConnection {
                    Label("Some older calls have no connection information and are counted under “Unknown”.", systemImage: "info.circle")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 4)
                }
                kpiCards
                charts
                breakdowns
                topNumbers
            }
            .padding(24)
            .frame(maxWidth: 1080, alignment: .leading)
        }
        .background(MacTheme.windowFill)
        .onAppear {
            if let f = st.customFrom { customFrom = f }
            if let t = st.customTo { customTo = t }
        }
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Call Statistics")
                    .font(.title2.weight(.semibold))
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
    }

    // MARK: filters

    private var filters: some View {
        Card(padding: 12) {
            HStack(spacing: 14) {
                filterPicker("Period", options: st.periodOptions, index: st.periodIndex) { state.setStatisticsFilter(periodIndex: $0) }
                if st.secondaryEnabled || st.connectionOptions.count > 1 {
                    filterPicker("Connection", options: st.connectionOptions, index: st.connectionIndex) { state.setStatisticsFilter(connectionIndex: $0) }
                }
                filterPicker("Direction", options: st.directionOptions, index: st.directionIndex) { state.setStatisticsFilter(directionIndex: $0) }
                if st.isCustomPeriod {
                    DatePicker("From", selection: $customFrom, displayedComponents: .date)
                        .datePickerStyle(.field)
                        .labelsHidden()
                    DatePicker("To", selection: $customTo, displayedComponents: .date)
                        .datePickerStyle(.field)
                        .labelsHidden()
                    Button("Apply") { applyCustomRange() }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
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
            .frame(minWidth: 140)
        }
    }

    // MARK: KPI

    private var kpiCards: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 168), spacing: 12)], spacing: 12) {
            kpi("Total", value: st.kpiTotal, hint: st.kpiDirectionHint, symbol: "phone.fill", tint: .blue) { state.drillDownHistory("all") }
            kpi("Answered", value: st.kpiAnswered, hint: st.kpiSuccessRate, symbol: "phone.arrow.down.left.fill", tint: .green) { state.drillDownHistory("answered") }
            kpi("Unanswered", value: st.kpiUnanswered, hint: st.kpiUnansweredHint, symbol: "phone.down.fill", tint: .orange) { state.drillDownHistory("unanswered") }
            kpi("Talk time", value: st.kpiTalkTime, hint: st.kpiAvgTalk, symbol: "clock.fill", tint: .indigo, action: nil)
            kpi("Contact rate", value: st.kpiContactRate, hint: st.kpiRingTime, symbol: "percent", tint: .teal, action: nil)
            kpi("Missed", value: st.kpiMissed, hint: st.kpiFailedCancelled, symbol: "phone.badge.waveform.fill", tint: .red) { state.drillDownHistory("missed") }
        }
    }

    private func kpi(_ title: String, value: String, hint: String, symbol: String, tint: Color, action: (() -> Void)?) -> some View {
        let tile = VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: symbol)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 24, height: 24)
                    .background(tint.gradient, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                Text(title)
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
                Spacer(minLength: 0)
                if action != nil {
                    Image(systemName: "chevron.right")
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(.tertiary)
                }
            }
            Text(value.isEmpty ? "0" : value)
                .font(.system(size: 26, weight: .semibold, design: .rounded))
                .foregroundStyle(.primary)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
            Text(hint.isEmpty ? " " : hint)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
        .padding(14)
        .frame(maxWidth: .infinity, minHeight: 108, alignment: .leading)
        .background(MacTheme.controlFill, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(Color.primary.opacity(0.06), lineWidth: 1)
        )

        return Group {
            if let action {
                Button(action: action) { tile }
                    .buttonStyle(.plain)
                    .help("Show these calls in Call History")
            } else {
                tile
            }
        }
    }

    // MARK: charts

    private var charts: some View {
        HStack(alignment: .top, spacing: 12) {
            callsByDay
            activityByHour
        }
    }

    private var callsByDay: some View {
        Card {
            VStack(alignment: .leading, spacing: 10) {
                sectionTitle("Calls by day", systemImage: "chart.bar.fill")
                if st.daily.isEmpty {
                    emptyChart
                } else {
                    Chart(st.daily) { day in
                        BarMark(x: .value("Day", day.label), y: .value("Total", day.total))
                            .foregroundStyle(Color.accentColor.opacity(0.35))
                        BarMark(x: .value("Day", day.label), y: .value("Answered", day.answered))
                            .foregroundStyle(Color.green.gradient)
                    }
                    .chartYAxis { AxisMarks(position: .leading) }
                    .chartLegend(.hidden)
                    .frame(height: 168)
                    HStack(spacing: 14) {
                        legendSwatch("Answered", color: .green)
                        legendSwatch("Total", color: Color.accentColor.opacity(0.45))
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }
            }
        }
    }

    private var activityByHour: some View {
        Card {
            VStack(alignment: .leading, spacing: 10) {
                sectionTitle("Activity by hour", systemImage: "clock.fill", caption: st.peakHourText)
                if st.hourly.isEmpty {
                    emptyChart
                } else {
                    let peak = st.hourly.map(\.total).max() ?? 0
                    Chart(st.hourly) { h in
                        BarMark(x: .value("Hour", h.label), y: .value("Calls", h.total))
                            .foregroundStyle(h.total == peak && h.total > 0 ? Color.teal : Color.teal.opacity(0.35))
                            .cornerRadius(3)
                    }
                    .chartYAxis { AxisMarks(position: .leading) }
                    .chartXAxis {
                        AxisMarks(values: .automatic(desiredCount: 8)) { _ in
                            AxisValueLabel().font(.caption2)
                        }
                    }
                    .frame(height: 180)
                }
            }
        }
    }

    private var emptyChart: some View {
        Text("No data for this period.")
            .font(.callout)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, minHeight: 160)
    }

    private func legendSwatch(_ title: String, color: Color) -> some View {
        HStack(spacing: 5) {
            RoundedRectangle(cornerRadius: 2).fill(color).frame(width: 10, height: 10)
            Text(title)
        }
    }

    // MARK: breakdowns

    private var breakdowns: some View {
        VStack(spacing: 12) {
            HStack(alignment: .top, spacing: 12) {
                outcomeBreakdown
                connectionCompare
            }
            HStack(alignment: .top, spacing: 12) {
                amoCrm
                callerIds
            }
        }
    }

    private var outcomeBreakdown: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                sectionTitle("Outcome", systemImage: "list.bullet")
                metricRow(st.outcomeAnswered, tint: .green)
                metricRow(st.outcomeCancelled, tint: .secondary)
                metricRow(st.outcomeAgentCancel, tint: .orange)
                metricRow(st.outcomeFailed, tint: .red)
                metricRow(st.outcomeMissed, tint: .red)
            }
        }
    }

    private var connectionCompare: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                sectionTitle("SIP vs WebRTC", systemImage: "antenna.radiowaves.left.and.right")
                if st.connectionCompare.isEmpty {
                    Text("No data for this period.").font(.callout).foregroundStyle(.secondary)
                }
                ForEach(st.connectionCompare) { row in
                    metricRow(row.detail.isEmpty ? row.name : "\(row.name)  ·  \(row.detail)", tint: .blue)
                }
            }
        }
    }

    private var amoCrm: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                sectionTitle("Kommo", systemImage: "link")
                if !state.main.amoCrmConfigured {
                    Text("Kommo integration is disabled.").font(.callout).foregroundStyle(.secondary)
                } else if st.crmLines.isEmpty {
                    Text("No data for this period.").font(.callout).foregroundStyle(.secondary)
                }
                ForEach(st.crmLines) { row in
                    Button { state.drillDownHistory(row.detail) } label: {
                        metricRow(row.name, tint: .green)
                    }
                    .buttonStyle(.plain)
                    .help("Show these calls in Call History")
                }
            }
        }
    }

    private var callerIds: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                sectionTitle("Outbound Caller ID", systemImage: "person.crop.rectangle")
                if st.callerIds.isEmpty {
                    Text("No outbound calls with Caller ID.").font(.callout).foregroundStyle(.secondary)
                }
                ForEach(st.callerIds) { row in
                    metricRow(row.detail.isEmpty ? row.name : "\(row.name)  ·  \(row.detail)", tint: .indigo)
                }
            }
        }
    }

    private var topNumbers: some View {
        Card {
            VStack(alignment: .leading, spacing: 4) {
                sectionTitle("Top numbers", systemImage: "number", caption: "Click a row to open it in Call History")
                if st.topNumbers.isEmpty {
                    Text("No calls in this period.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .padding(.top, 6)
                }
                ForEach(Array(st.topNumbers.enumerated()), id: \.element.id) { index, row in
                    Button {
                        state.drillDownHistory("all", phoneNumber: row.phoneNumber.isEmpty ? row.name : row.phoneNumber)
                    } label: {
                        HStack(spacing: 12) {
                            Text("\(index + 1)")
                                .font(.caption.weight(.semibold).monospacedDigit())
                                .foregroundStyle(.secondary)
                                .frame(width: 18, alignment: .trailing)
                            Text(row.name)
                                .font(.body.weight(.medium))
                            Spacer()
                            Text(row.detail)
                                .font(.callout)
                                .foregroundStyle(.secondary)
                            Image(systemName: "chevron.right")
                                .font(.caption2.weight(.semibold))
                                .foregroundStyle(.tertiary)
                        }
                        .padding(.vertical, 8)
                        .padding(.horizontal, 6)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    if index < st.topNumbers.count - 1 {
                        Divider().padding(.leading, 36)
                    }
                }
            }
        }
    }

    private func sectionTitle(_ title: String, systemImage: String, caption: String = "") -> some View {
        HStack(spacing: 8) {
            Image(systemName: systemImage)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            Text(title).font(.headline)
            Spacer()
            if !caption.isEmpty {
                Text(caption).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
        }
    }

    private func metricRow(_ text: String, tint: Color) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Circle().fill(tint).frame(width: 7, height: 7).padding(.top, 1)
            Text(text.isEmpty ? "—" : text)
                .font(.callout)
                .foregroundStyle(.primary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 2)
    }
}

import SwiftUI
import Charts

/// Call Statistics page — WPF CallStatisticsPage parity: filters, KPI cards with drill-down,
/// Calls-by-day and Activity-by-hour charts, outcome breakdown, SIP vs WebRTC, AmoCRM, Caller ID, Top numbers, CSV export.
struct StatisticsView: View {
    @EnvironmentObject var state: AppState
    @State private var customFrom = Calendar.current.date(byAdding: .day, value: -7, to: Date()) ?? Date()
    @State private var customTo = Date()

    private var st: StatisticsState { state.main.statistics }
    private let columns = [GridItem(.adaptive(minimum: 150), spacing: 12)]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .top) {
                    SectionHeader(title: "Call Statistics", subtitle: subtitle)
                    Spacer()
                    Button { state.exportStatisticsCsv() } label: { Label("Export CSV", systemImage: "square.and.arrow.up") }
                        .disabled(!st.hasReport)
                }

                filters

                if !st.periodHint.isEmpty {
                    Text(st.periodHint).font(.callout).foregroundStyle(.secondary)
                }
                if st.hasLegacyUnknownConnection {
                    Label("Some older calls have no connection information and are counted under “Unknown”.", systemImage: "info.circle")
                        .font(.caption).foregroundStyle(.secondary)
                }

                kpiCards

                HStack(alignment: .top, spacing: 16) {
                    callsByDay.frame(maxWidth: .infinity)
                    activityByHour.frame(maxWidth: .infinity)
                }
                .frame(minHeight: 200)

                HStack(alignment: .top, spacing: 16) {
                    outcomeBreakdown.frame(maxWidth: .infinity)
                    connectionCompare.frame(maxWidth: .infinity)
                }
                HStack(alignment: .top, spacing: 16) {
                    amoCrm.frame(maxWidth: .infinity)
                    callerIds.frame(maxWidth: .infinity)
                }
                topNumbers
            }
            .padding(20)
        }
        .onAppear {
            if let f = st.customFrom { customFrom = f }
            if let t = st.customTo { customTo = t }
        }
    }

    private var subtitle: String {
        let period = st.periodOptions.indices.contains(st.periodIndex) ? st.periodOptions[st.periodIndex].lowercased() : "selected period"
        return "Operational analytics from the \(period) of call history"
    }

    // MARK: filters

    private var filters: some View {
        HStack(spacing: 16) {
            filterPicker("Period", options: st.periodOptions, index: st.periodIndex) { state.setStatisticsFilter(periodIndex: $0) }
            if st.secondaryEnabled || st.connectionOptions.count > 1 {
                filterPicker("Connection", options: st.connectionOptions, index: st.connectionIndex) { state.setStatisticsFilter(connectionIndex: $0) }
            }
            filterPicker("Direction", options: st.directionOptions, index: st.directionIndex) { state.setStatisticsFilter(directionIndex: $0) }
            if st.isCustomPeriod {
                DatePicker("From", selection: $customFrom, displayedComponents: .date).datePickerStyle(.field)
                DatePicker("To", selection: $customTo, displayedComponents: .date).datePickerStyle(.field)
                Button("Apply") {
                    let cal = Calendar.current
                    let from = cal.startOfDay(for: customFrom)
                    let to = cal.date(byAdding: DateComponents(day: 1, second: -1), to: cal.startOfDay(for: customTo)) ?? customTo
                    state.setStatisticsFilter(customFrom: from, customTo: to)
                }
            }
            Spacer()
        }
    }

    private func filterPicker(_ title: String, options: [String], index: Int, onChange: @escaping (Int) -> Void) -> some View {
        HStack(spacing: 6) {
            Text(title).font(.callout).foregroundStyle(.secondary)
            Picker("", selection: Binding(get: { index }, set: onChange)) {
                ForEach(Array(options.enumerated()), id: \.offset) { i, label in Text(label).tag(i) }
            }
            .labelsHidden()
            .frame(width: 170)
        }
    }

    // MARK: KPI

    private var kpiCards: some View {
        LazyVGrid(columns: columns, spacing: 12) {
            kpi("Total", value: st.kpiTotal, hint: st.kpiDirectionHint, color: .primary) { state.drillDownHistory("all") }
            kpi("Answered", value: st.kpiAnswered, hint: st.kpiSuccessRate, color: .green) { state.drillDownHistory("answered") }
            kpi("Unanswered", value: st.kpiUnanswered, hint: st.kpiUnansweredHint, color: .red) { state.drillDownHistory("unanswered") }
            kpi("Talk time", value: st.kpiTalkTime, hint: st.kpiAvgTalk, color: .primary, action: nil)
            kpi("Contact rate", value: st.kpiContactRate, hint: st.kpiRingTime, color: .primary, action: nil)
            kpi("Missed incoming", value: st.kpiMissed, hint: st.kpiFailedCancelled, color: .red) { state.drillDownHistory("missed") }
        }
    }

    private func kpi(_ title: String, value: String, hint: String, color: Color, action: (() -> Void)?) -> some View {
        Card(padding: 14) {
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(.caption).foregroundStyle(.secondary)
                Text(value.isEmpty ? "0" : value).font(.system(size: 28, weight: .semibold)).foregroundStyle(color)
                Text(hint.isEmpty ? "—" : hint).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { action?() }
        .help(action == nil ? "" : "Show these calls in Call History")
    }

    // MARK: charts

    private var callsByDay: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                Text("Calls by day").font(.headline)
                if st.daily.isEmpty {
                    Text("No data for this period.").font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, minHeight: 120)
                } else {
                    Chart(st.daily) { day in
                        BarMark(x: .value("Day", day.label), y: .value("Calls", day.total))
                            .foregroundStyle(Color.accentColor.gradient)
                            .annotation(position: .top) {
                                if day.total > 0 { Text("\(day.total)").font(.caption2).foregroundStyle(.secondary) }
                            }
                        BarMark(x: .value("Day", day.label), y: .value("Answered", day.answered))
                            .foregroundStyle(Color.green.opacity(0.85))
                    }
                    .chartYAxis(.hidden)
                    .chartLegend(.hidden)
                    .frame(height: 140)
                }
            }
        }
    }

    private var activityByHour: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                Text("Activity by hour").font(.headline)
                if !st.peakHourText.isEmpty {
                    Text(st.peakHourText).font(.caption).foregroundStyle(.secondary)
                }
                if st.hourly.isEmpty {
                    Text("No data for this period.").font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, minHeight: 120)
                } else {
                    Chart(st.hourly) { h in
                        BarMark(x: .value("Hour", h.label), y: .value("Calls", h.total))
                            .foregroundStyle(h.total == (st.hourly.map(\.total).max() ?? -1) && h.total > 0 ? Color.green : Color.green.opacity(0.45))
                    }
                    .chartYAxis(.hidden)
                    .chartXAxis {
                        AxisMarks(values: .automatic(desiredCount: 12)) { _ in AxisValueLabel().font(.caption2) }
                    }
                    .frame(height: 120)
                }
            }
        }
    }

    // MARK: text blocks

    private var outcomeBreakdown: some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                Text("Outcome breakdown").font(.headline)
                bullet(st.outcomeAnswered)
                bullet(st.outcomeCancelled)
                bullet(st.outcomeAgentCancel)
                bullet(st.outcomeFailed)
                bullet(st.outcomeMissed)
            }
        }
    }

    private var connectionCompare: some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                Text("SIP vs WebRTC").font(.headline)
                if st.connectionCompare.isEmpty {
                    Text("No data for this period.").font(.caption).foregroundStyle(.secondary)
                }
                ForEach(st.connectionCompare) { row in
                    Text(row.detail.isEmpty ? row.name : "\(row.name): \(row.detail)").font(.callout)
                }
            }
        }
    }

    private var amoCrm: some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                Text("AmoCRM").font(.headline)
                if !state.main.amoCrmConfigured {
                    Text("Kommo integration is disabled.").font(.caption).foregroundStyle(.secondary)
                } else if st.crmLines.isEmpty {
                    Text("No data for this period.").font(.caption).foregroundStyle(.secondary)
                }
                ForEach(st.crmLines) { row in
                    Button { state.drillDownHistory(row.detail) } label: {
                        bullet(row.name).contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help("Show these calls in Call History")
                }
            }
        }
    }

    private var callerIds: some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                Text("Outbound Caller ID").font(.headline)
                if st.callerIds.isEmpty { Text("No outbound calls with Caller ID.").font(.caption).foregroundStyle(.secondary) }
                ForEach(st.callerIds) { row in
                    Text(row.detail.isEmpty ? row.name : "\(row.name): \(row.detail)").font(.callout)
                }
            }
        }
    }

    private var topNumbers: some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                Text("Top numbers").font(.headline)
                Text("Click a row to view in history").font(.caption).foregroundStyle(.secondary)
                if st.topNumbers.isEmpty { Text("No calls in this period.").font(.caption).foregroundStyle(.secondary) }
                ForEach(st.topNumbers) { row in
                    Button {
                        state.drillDownHistory("all", phoneNumber: row.phoneNumber.isEmpty ? row.name : row.phoneNumber)
                    } label: {
                        HStack {
                            Text(row.name).fontWeight(.medium)
                            Spacer()
                            Text(row.detail).foregroundStyle(.secondary)
                        }
                        .font(.callout)
                        .padding(.vertical, 4)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    Divider()
                }
            }
        }
    }

    private func bullet(_ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text("•").foregroundStyle(.secondary)
            Text(text.isEmpty ? "—" : text)
        }
        .font(.callout)
    }
}

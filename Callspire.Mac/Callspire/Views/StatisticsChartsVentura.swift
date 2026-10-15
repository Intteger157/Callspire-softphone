import SwiftUI
import Charts

/// Swift Charts (macOS 13+). Monterey uses a text summary in `StatisticsView` instead.
@available(macOS 13.0, *)
struct StatisticsChartsVentura: View {
    let st: StatisticsState
    let chartFill: Color

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            chartCard(title: "Calls by day", caption: nil) {
                if st.daily.isEmpty { emptyChart } else {
                    Chart(st.daily) { day in
                        BarMark(x: .value("Day", day.label), y: .value("Total", day.total))
                            .foregroundStyle(chartFill.opacity(0.22))
                        BarMark(x: .value("Day", day.label), y: .value("Answered", day.answered))
                            .foregroundStyle(Color.green.gradient)
                    }
                    .chartYAxis { AxisMarks(position: .leading) { _ in AxisGridLine(stroke: StrokeStyle(lineWidth: 0.5)).foregroundStyle(MacTheme.separator) } }
                    .frame(height: 160)
                }
            }
            chartCard(title: "Activity by hour", caption: st.peakHourText) {
                if st.hourly.isEmpty { emptyChart } else {
                    let peak = st.hourly.map(\.total).max() ?? 0
                    Chart(st.hourly) { h in
                        BarMark(x: .value("Hour", h.label), y: .value("Calls", h.total))
                            .foregroundStyle(h.total == peak && h.total > 0 ? Color.primary : chartFill.opacity(0.35))
                            .cornerRadius(2)
                    }
                    .chartYAxis { AxisMarks(position: .leading) { _ in AxisGridLine(stroke: StrokeStyle(lineWidth: 0.5)).foregroundStyle(MacTheme.separator) } }
                    .chartXAxis { AxisMarks(values: .automatic(desiredCount: 8)) { _ in AxisValueLabel().font(.caption2) } }
                    .frame(height: 160)
                }
            }
        }
    }

    private func chartCard<Content: View>(title: String, caption: String?, @ViewBuilder content: @escaping () -> Content) -> some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text(title).font(.headline)
                    Spacer()
                    if let caption, !caption.isEmpty {
                        Text(caption).font(.caption).foregroundStyle(.secondary)
                    }
                }
                content()
            }
        }
    }

    private var emptyChart: some View {
        Text("No data for this period.")
            .font(.callout)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, minHeight: 140)
    }
}

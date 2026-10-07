import SwiftUI
import AppKit

// MARK: - Logs window (WPF LogWindow parity: live tail, filter, Clear, Copy, Open folder)

struct LogView: View {
    @EnvironmentObject var state: AppState
    @State private var filter = ""
    @State private var autoScroll = true

    private var lines: [String] {
        filter.isEmpty ? state.logs : state.logs.filter { $0.localizedCaseInsensitiveContains(filter) }
    }

    var body: some View {
        FloatingPanel(padding: 0) {
            VStack(spacing: 0) {
                ScrollViewReader { proxy in
                    ScrollView([.vertical, .horizontal]) {
                        LazyVStack(alignment: .leading, spacing: 0) {
                            ForEach(Array(lines.enumerated()), id: \.offset) { i, line in
                                Text(line)
                                    .font(.system(.caption, design: .monospaced))
                                    .foregroundStyle(lineColor(line))
                                    .textSelection(.enabled)
                                    .id(i)
                            }
                        }
                        .padding(8)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .background(Color(nsColor: .textBackgroundColor))
                    .onChange(of: state.logs.count) { _ in
                        if autoScroll, let last = lines.indices.last { proxy.scrollTo(last, anchor: .bottom) }
                    }
                }
                Divider()
                HStack {
                    Text("\(lines.count) lines").font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Toggle("Auto-scroll", isOn: $autoScroll).toggleStyle(.checkbox).font(.caption)
                }
                .padding(.horizontal, 12).padding(.vertical, 6)
            }
        }
        .padding(MacTheme.chromeInset)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MacTheme.chromeFill)
        .toolbar {
            ToolbarItemGroup(placement: .automatic) {
                TextField("Filter", text: $filter).textFieldStyle(.roundedBorder).frame(width: 200)
                Button { Pasteboard.copy(lines.joined(separator: "\n")) } label: { Label("Copy", systemImage: "doc.on.doc") }
                    .help("Copy visible lines")
                Button { state.clearLogs() } label: { Label("Clear", systemImage: "trash") }
                Button { state.openLogsFolder() } label: { Label("Open Folder", systemImage: "folder") }
            }
        }
        .navigationTitle("Logs")
        .task { await state.loadLogSnapshot() }
        .onDisappear { state.stopLogStreaming() }
    }

    private func lineColor(_ line: String) -> Color {
        let l = line.lowercased()
        if l.contains("error") || l.contains("exception") || l.contains("failed") { return .red }
        if l.contains("warn") { return .orange }
        return .primary
    }
}

// MARK: - Update available

struct UpdateAvailableSheet: View {
    @EnvironmentObject var state: AppState
    let info: UpdateInfo
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 12) {
                Image(systemName: "arrow.down.circle.fill").font(.largeTitle).foregroundStyle(Color.accentColor)
                VStack(alignment: .leading) {
                    Text("Update available").font(.title3.weight(.semibold))
                    Text("Callspire \(info.version) is available. You have \(info.currentVersion).").foregroundStyle(.secondary)
                }
            }
            if let notes = info.notes, !notes.isEmpty {
                ScrollView { Text(notes).font(.callout).frame(maxWidth: .infinity, alignment: .leading) }
                    .frame(maxHeight: 160)
            }
            if let sha = info.sha256, !sha.isEmpty {
                Text("SHA-256: \(sha)").font(.system(.caption2, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled)
            }
            HStack {
                Spacer()
                Button("Later") { state.update = nil }.keyboardShortcut(.cancelAction)
                Button("Download from GitHub") {
                    if let u = URL(string: info.url) { NSWorkspace.shared.open(u) } else { state.openUpdateUrl() }
                    state.update = nil
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(20)
        .frame(width: 460)
    }
}

// MARK: - Connection chooser (both lines online, Caller ID)

/// Line + outbound Caller ID chooser. Shown when both lines are configured, or for history calls
/// so the user always sees which number the call goes out from.
struct ConnectionSelectionSheet: View {
    @EnvironmentObject var state: AppState
    let request: ConnectionSelectionRequest
    @State private var callerId: String = ""
    @State private var slot: String = "main"

    private var bothLines: Bool { request.hasMain && request.hasSecondary }
    private var showsCallerIds: Bool { slot == "main" && !request.mainCallerIds.isEmpty }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            header

            if bothLines {
                VStack(alignment: .leading, spacing: 6) {
                    sectionTitle("Line")
                    lineRow(slot: "main", title: request.mainName ?? "Primary", status: request.mainStatus, isWebRtc: request.isMainWebRtc)
                    lineRow(slot: "secondary", title: request.secondaryName ?? "Secondary", status: request.secondaryStatus, isWebRtc: request.isSecondaryWebRtc)
                }
            }

            if showsCallerIds {
                VStack(alignment: .leading, spacing: 6) {
                    sectionTitle("Call from")
                    ScrollView {
                        VStack(spacing: 4) {
                            ForEach(request.mainCallerIds) { item in
                                callerIdRow(item)
                            }
                        }
                    }
                    .frame(maxHeight: 240)
                }
            }

            HStack(spacing: 10) {
                Spacer()
                Button("Cancel", role: .cancel) { state.finishConnection(ConnectionSelectionResult()) }
                    .keyboardShortcut(.cancelAction)
                Button {
                    state.finishConnection(ConnectionSelectionResult(
                        slot: slot,
                        callerId: slot == "main" ? (callerId.isEmpty ? request.selectedCallerId : callerId) : nil
                    ))
                } label: {
                    Label("Call", systemImage: "phone.fill")
                        .padding(.horizontal, 6)
                }
                .buttonStyle(.borderedProminent)
                .tint(.green)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 420)
        .onAppear {
            callerId = request.selectedCallerId ?? request.mainCallerIds.first?.number ?? ""
            slot = request.hasMain ? "main" : "secondary"
        }
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(systemName: "phone.arrow.up.right.fill")
                .font(.system(size: 18, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 40, height: 40)
                .background(Circle().fill(Color.green))
            VStack(alignment: .leading, spacing: 2) {
                Text(request.phoneNumber.map { "Call \($0)" } ?? "Place call")
                    .font(.title3.weight(.semibold))
                    .lineLimit(1)
                Text(bothLines ? "Choose the line and the number to call from." : "Choose the number to call from.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func sectionTitle(_ text: String) -> some View {
        Text(text.uppercased())
            .font(.caption2.weight(.semibold))
            .tracking(0.6)
            .foregroundStyle(.secondary)
    }

    private func lineRow(slot value: String, title: String, status: String?, isWebRtc: Bool) -> some View {
        SelectableRow(isSelected: slot == value) {
            slot = value
        } content: {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(title).font(.callout.weight(.medium))
                    TransportBadge(label: "", isWebRtc: isWebRtc)
                }
                if let status, !status.isEmpty {
                    Text(status).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }

    private func callerIdRow(_ item: CallerIdItem) -> some View {
        let title = !item.name.isEmpty ? item.name : (item.displayText.isEmpty ? item.number : item.displayText)
        return SelectableRow(isSelected: callerId == item.number) {
            callerId = item.number
        } content: {
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.callout.weight(.medium)).lineLimit(1)
                if !item.number.isEmpty, !title.contains(item.number) {
                    Text(item.number).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                }
            }
        }
    }
}

/// Radio-style row used by pickers in sheets.
struct SelectableRow<Content: View>: View {
    let isSelected: Bool
    let action: () -> Void
    @ViewBuilder var content: () -> Content
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: isSelected ? "largecircle.fill.circle" : "circle")
                    .font(.system(size: 15))
                    .foregroundStyle(isSelected ? Color.accentColor : Color.secondary)
                content()
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .background(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(isSelected ? Color.accentColor.opacity(0.12) : Color.primary.opacity(hover ? 0.06 : 0.03))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .strokeBorder(isSelected ? Color.accentColor.opacity(0.5) : MacTheme.separator.opacity(0.5))
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

// MARK: - Gateway lead prompt (WPF LeadSelectionWindow in gateway mode)

struct GatewayLeadSheet: View {
    @EnvironmentObject var state: AppState
    let phone: String
    @State private var leadId = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Send call to Kommo").font(.title3.weight(.semibold))
            Text("Call with \(phone) has ended. Choose where PBX Gateway should attach the call note and recording.")
                .font(.callout).foregroundStyle(.secondary)
            Button {
                state.finishLead(LeadSelectionResult(proceed: true, cancelled: false, leadId: nil))
            } label: {
                Label("Attach to contact (gateway picks the lead)", systemImage: "person.crop.circle.badge.checkmark")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            HStack {
                TextField("Lead ID", text: $leadId).textFieldStyle(.roundedBorder).frame(width: 160)
                Button("Attach to this lead") {
                    state.finishLead(LeadSelectionResult(proceed: true, cancelled: false, leadId: Int64(leadId.trimmingCharacters(in: .whitespaces))))
                }
                .disabled(Int64(leadId.trimmingCharacters(in: .whitespaces)) == nil)
            }
            HStack {
                Spacer()
                Button("Skip", role: .cancel) { state.finishLead(LeadSelectionResult(proceed: false, cancelled: true, leadId: nil)) }
                    .keyboardShortcut(.cancelAction)
            }
        }
        .padding(20)
        .frame(width: 440)
    }
}

// MARK: - Kommo lead picker (local mode, list of open leads)

struct LeadSelectionView: View {
    @EnvironmentObject var state: AppState
    let request: KommoLeadPickerRequest
    @State private var query = ""
    @State private var selected: Int64?

    private var leads: [KommoLead] {
        request.leads.filter { query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) || $0.description.localizedCaseInsensitiveContains(query) || String($0.id).contains(query) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Select Kommo lead").font(.title3.weight(.semibold))
            HStack(spacing: 10) {
                Label(request.phoneNumber ?? "", systemImage: request.isIncoming ? "phone.arrow.down.left" : "phone.arrow.up.right")
                Text(request.wasAnswered ? "Answered · \(request.durationSeconds)s" : "Not answered").foregroundStyle(.secondary)
                if let t = request.callTime { Text(t.historyStamp).foregroundStyle(.secondary) }
            }
            .font(.callout)
            TextField("Search leads", text: $query).textFieldStyle(.roundedBorder)
            List(leads, selection: $selected) { lead in
                VStack(alignment: .leading, spacing: 2) {
                    Text(lead.name).fontWeight(.medium)
                    Text(lead.description).font(.caption).foregroundStyle(.secondary)
                }
                .tag(lead.id)
                .contentShape(Rectangle())
                .onTapGesture(count: 2) { state.finishKommo(lead.id) }
            }
            .frame(minHeight: 240)
            HStack {
                Button("Skip (contact only)") { state.finishKommo(nil) }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Attach to lead") { if let selected { state.finishKommo(selected) } }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
                    .disabled(selected == nil)
            }
        }
        .padding(20)
        .frame(width: 520, height: 460)
    }
}

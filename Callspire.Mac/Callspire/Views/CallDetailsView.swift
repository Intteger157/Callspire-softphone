import SwiftUI
import AVFoundation
import AppKit

/// WPF CallDetailsWindow parity: call summary, call / timing info, Kommo block,
/// "Send result to CRM" (Play / Retry), Technical Details with log filter tabs.
struct CallDetailsView: View {
    @EnvironmentObject var state: AppState
    let details: CallDetails

    @State private var player: AVAudioPlayer?
    @State private var isPlaying = false
    @State private var contactName: String?
    @State private var contactError: String?
    @State private var retryBusy = false
    @State private var retryMessage: String?
    @State private var playError: String?
    @State private var logFilter: LogFilter = .all

    /// Latest copy (retry refreshes `state.callDetails`).
    private var d: CallDetails { state.callDetails ?? details }

    enum LogFilter: String, CaseIterable, Identifiable {
        case all = "All", amo = "Kommo", webrtc = "WebRTC", sip = "SIP"
        var id: String { rawValue }
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    summary
                    HStack(alignment: .top, spacing: 16) {
                        callInformation
                        timingInformation
                    }
                    if d.kommoEnabled { kommo }
                    if d.showCrmSendSection { sendToCrm }
                    technicalDetails
                }
                .padding(24)
            }
            Divider()
            footer
        }
        .frame(minWidth: 760, idealWidth: 820, minHeight: 600, idealHeight: 700)
        .task { await loadContactName() }
        .onDisappear { player?.stop() }
    }

    // MARK: - Summary

    private var summary: some View {
        HStack(alignment: .center, spacing: 16) {
            Image(systemName: directionSymbol)
                .font(.system(size: 22, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 52, height: 52)
                .background(Circle().fill(directionColor.gradient))

            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Text(d.phoneNumber)
                        .font(.system(size: 24, weight: .semibold, design: .rounded))
                        .textSelection(.enabled)
                    Button { Pasteboard.copy(d.phoneNumber) } label: {
                        Image(systemName: "doc.on.doc")
                            .font(.system(size: 11, weight: .medium))
                            .foregroundStyle(.secondary)
                            .frame(width: 26, height: 26)
                            .background(Circle().fill(Color.primary.opacity(0.07)))
                    }
                    .buttonStyle(.plain)
                    .help("Copy number")
                }
                HStack(spacing: 8) {
                    Text(d.callTime.formatted(date: .abbreviated, time: .shortened))
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    TransportBadge(label: d.transportLabel, isWebRtc: d.transportLabel.lowercased().contains("webrtc"))
                    if let name = d.connectionName, !name.isEmpty {
                        Text(name).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }

            Spacer(minLength: 12)

            VStack(alignment: .trailing, spacing: 6) {
                StatusChip(text: d.wasAnswered ? "Answered" : "Not answered", color: d.wasAnswered ? .green : .orange)
                Text(d.durationText.isEmpty ? "—" : d.durationText)
                    .font(.title3.monospacedDigit().weight(.medium))
            }
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.primary.opacity(0.04)))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(MacTheme.separator.opacity(0.6)))
    }

    private var directionSymbol: String {
        if !d.wasAnswered && d.isIncoming { return "phone.down.fill" }
        return d.isIncoming ? "phone.arrow.down.left.fill" : "phone.arrow.up.right.fill"
    }

    private var directionColor: Color {
        if !d.wasAnswered { return d.isIncoming ? .red : .orange }
        return d.isIncoming ? .blue : .green
    }

    // MARK: - Sections

    private var callInformation: some View {
        DetailSection(title: "Call") {
            DetailRow("Direction", d.directionText)
            if let cid = d.outboundCallerId, !cid.isEmpty {
                DetailRow("Caller ID", cid, mono: true)
            }
            DetailRow("Status", d.statusText)
            DetailRow("Ended by", d.endedByText)
        }
        .frame(maxWidth: .infinity)
    }

    private var timingInformation: some View {
        DetailSection(title: "Timing") {
            DetailRow("Started", d.callTime.timeMillis, mono: true)
            DetailRow("Ringback", d.ringbackDurationText.isEmpty ? "—" : d.ringbackDurationText, mono: true)
            DetailRow("Ringing from", d.ringbackStart?.timeMillis ?? "—", mono: true)
            DetailRow("Answered at", d.answerTime?.timeMillis ?? "—", mono: true)
        }
        .frame(maxWidth: .infinity)
    }

    private var kommo: some View {
        DetailSection(title: "Kommo") {
            DetailRow("Lead") {
                if let id = d.kommoLeadId, let url = d.kommoLeadUrl, let link = URL(string: url) {
                    Link(destination: link) {
                        Label("Lead #\(id)", systemImage: "arrow.up.right.square")
                    }
                } else if let id = d.kommoLeadId {
                    Text("Lead #\(id)")
                } else {
                    Text("Not attached to a lead").foregroundStyle(.secondary)
                }
            }
            DetailRow("Contact") {
                if let contactName {
                    Text(contactName)
                } else if let contactError {
                    Text(contactError).foregroundStyle(.secondary)
                } else {
                    HStack(spacing: 6) {
                        ProgressView().controlSize(.small)
                        Text("Looking up…").foregroundStyle(.secondary)
                    }
                }
            }
            DetailRow("Upload") {
                VStack(alignment: .leading, spacing: 2) {
                    Text(uploadStatusText).foregroundStyle(uploadColor)
                    if let reason = d.kommoUploadReason, !reason.isEmpty {
                        Text(reason).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            if let src = d.kommoUploadedRecordingSource, !src.isEmpty {
                DetailRow("Recording", src)
            }
        }
    }

    /// Gateway status strings carry an emoji prefix; colour carries that meaning here instead.
    private var uploadStatusText: String {
        let s = d.kommoUploadStatus.trimmingCharacters(in: .whitespaces)
        guard !s.isEmpty else { return "—" }
        for prefix in ["✅", "❌", "⚠️", "⏳"] where s.hasPrefix(prefix) {
            return String(s.dropFirst(prefix.count)).trimmingCharacters(in: .whitespaces)
        }
        return s
    }

    private var uploadColor: Color {
        let s = d.kommoUploadStatus
        if s.hasPrefix("✅") { return .green }
        if s.hasPrefix("❌") { return .red }
        if s.hasPrefix("⚠️") { return .orange }
        return .primary
    }

    private var sendToCrm: some View {
        DetailSection(title: "Send result to CRM") {
            HStack(spacing: 12) {
                Button { togglePlay() } label: {
                    Image(systemName: isPlaying ? "stop.fill" : "play.fill")
                        .font(.system(size: 13, weight: .semibold))
                        .frame(width: 32, height: 32)
                        .foregroundStyle(d.hasRecording ? Color.white : Color.secondary)
                        .background(Circle().fill(d.hasRecording ? Color.accentColor : Color.primary.opacity(0.08)))
                }
                .buttonStyle(.plain)
                .disabled(!d.hasRecording)
                .help(isPlaying ? "Stop" : "Play recording")

                VStack(alignment: .leading, spacing: 2) {
                    Text(recordingTitle).font(.callout.weight(.medium)).lineLimit(1).truncationMode(.middle)
                    Text(recordingSubtitle).font(.caption).foregroundStyle(.secondary)
                }

                Spacer(minLength: 12)

                if d.canRetryKommo {
                    Button {
                        retryBusy = true; retryMessage = nil
                        Task {
                            let err = await state.retryKommo(d)
                            retryMessage = err ?? (d.kommoGatewayUpload ? "Queued on PBX Gateway" : "Uploaded")
                            retryBusy = false
                        }
                    } label: {
                        HStack(spacing: 6) {
                            if retryBusy { ProgressView().controlSize(.small) }
                            Text(d.kommoGatewayUpload ? "Retry via Gateway" : "Retry upload")
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(retryBusy)
                }
            }
            if let playError {
                Text(playError).font(.caption).foregroundStyle(.red)
            }
            if let retryMessage {
                Text(retryMessage).font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var recordingTitle: String {
        if d.hasRecording { return (d.recordingFilePath as NSString?)?.lastPathComponent ?? "Local recording" }
        return d.kommoGatewayUpload ? "Recording on Miko PBX" : "No local recording"
    }

    private var recordingSubtitle: String {
        if d.hasRecording { return "Local recording" }
        return d.kommoGatewayUpload ? "PBX Gateway fetches it from Miko PBX" : "Nothing to upload"
    }

    private var technicalDetails: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                Text("Technical Details").font(.headline)
                Spacer()
                Picker("", selection: $logFilter) {
                    ForEach(LogFilter.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 260)
                Button { Pasteboard.copy(filteredLogs.joined(separator: "\n")) } label: {
                    Label("Copy", systemImage: "doc.on.doc")
                }
                .disabled(filteredLogs.isEmpty)
            }
            ScrollView([.vertical, .horizontal]) {
                Text(filteredLogs.isEmpty ? "No log entries for this filter." : filteredLogs.joined(separator: "\n"))
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(filteredLogs.isEmpty ? .secondary : .primary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(10)
            }
            .frame(minHeight: 140, maxHeight: 240)
            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(MacTheme.separator.opacity(0.7)))
        }
    }

    private var footer: some View {
        HStack(spacing: 10) {
            OutboundCallButton(phoneNumber: d.phoneNumber, title: "Call back", prominent: true, onBeforeCall: { player?.stop() })

            Spacer()
            Button("Close") { close() }.keyboardShortcut(.cancelAction)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
    }

    /// Same substring rules as WPF CallDetailsWindow.FilterLogs.
    private var filteredLogs: [String] {
        switch logFilter {
        case .all: return d.technicalDetails
        case .amo: return d.technicalDetails.filter { $0.contains("AmoCrm") || $0.contains("AmoCRM") || $0.contains("Kommo") }
        case .webrtc: return d.technicalDetails.filter { $0.contains("WebRtc") || $0.contains("WebRTC") || $0.contains("webrtc") }
        case .sip: return d.technicalDetails.filter { $0.contains("[Sip") || $0.contains("[SIP") || $0.contains("SIP") }
        }
    }

    // MARK: - Actions

    private func loadContactName() async {
        guard d.kommoEnabled else { return }
        let r = await state.lookupKommoContact(d.phoneNumber)
        if let name = r.name, !name.isEmpty { contactName = name; contactError = nil }
        else { contactError = r.error ?? "Not found in Kommo" }
    }

    private func togglePlay() {
        if isPlaying { player?.stop(); isPlaying = false; return }
        Task {
            let r = await state.prepareRecording(d)
            guard let path = r.path else { playError = r.error ?? "Could not open the recording."; return }
            do {
                let p = try AVAudioPlayer(contentsOf: URL(fileURLWithPath: path))
                p.play()
                player = p
                isPlaying = true
                playError = nil
                let duration = p.duration
                Task {
                    try? await Task.sleep(nanoseconds: UInt64(max(0, duration) * 1_000_000_000))
                    if player === p, !p.isPlaying { isPlaying = false }
                }
            } catch {
                playError = error.localizedDescription
            }
        }
    }

    private func close() {
        player?.stop()
        state.callDetails = nil
    }
}

// MARK: - Building blocks

private struct DetailSection<Content: View>: View {
    let title: String
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title.uppercased())
                .font(.caption.weight(.semibold))
                .tracking(0.6)
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 10) {
                content()
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.04)))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(MacTheme.separator.opacity(0.6)))
        }
    }
}

private struct DetailRow<Value: View>: View {
    let key: String
    @ViewBuilder var value: () -> Value

    init(_ key: String, @ViewBuilder value: @escaping () -> Value) {
        self.key = key
        self.value = value
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(key)
                .foregroundStyle(.secondary)
                .frame(width: 96, alignment: .leading)
            value()
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .font(.callout)
    }
}

extension DetailRow where Value == AnyView {
    init(_ key: String, _ text: String, mono: Bool = false) {
        self.init(key) {
            AnyView(
                Text(text.isEmpty ? "—" : text)
                    .font(mono ? .callout.monospacedDigit() : .callout)
                    .textSelection(.enabled)
            )
        }
    }
}

private struct StatusChip: View {
    let text: String
    let color: Color

    var body: some View {
        HStack(spacing: 5) {
            Circle().fill(color).frame(width: 7, height: 7)
            Text(text).font(.caption.weight(.medium))
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 4)
        .background(Capsule().fill(color.opacity(0.15)))
        .foregroundStyle(color)
    }
}

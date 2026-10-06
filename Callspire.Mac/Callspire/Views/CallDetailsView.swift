import SwiftUI
import AVFoundation
import AppKit

/// WPF CallDetailsWindow parity: Call Information, Timing Information, AmoCRM block,
/// "Send result to CRM" (Play / Retry), Technical Details with log filter tabs, Close.
struct CallDetailsView: View {
    @EnvironmentObject var state: AppState
    let details: CallDetails

    @State private var player: AVAudioPlayer?
    @State private var isPlaying = false
    @State private var contactName: String = "Loading…"
    @State private var contactError: String?
    @State private var retryBusy = false
    @State private var retryMessage: String?
    @State private var playError: String?
    @State private var logFilter: LogFilter = .all

    /// Latest copy (retry refreshes `state.callDetails`).
    private var d: CallDetails { state.callDetails ?? details }

    enum LogFilter: String, CaseIterable, Identifiable {
        case all = "All Logs", amo = "AmoCRM Logs", webrtc = "WebRTC Logs", sip = "SIP Logs"
        var id: String { rawValue }
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    HStack(alignment: .top, spacing: 16) {
                        callInformation.frame(maxWidth: .infinity)
                        timingInformation.frame(width: 300)
                    }
                    if d.kommoEnabled { amoCrm }
                    if d.showCrmSendSection { sendToCrm }
                    technicalDetails
                }
                .padding(20)
            }
            Divider()
            HStack {
                Button("Call back") {
                    state.setPhone(d.phoneNumber)
                    state.placeCall(number: d.phoneNumber)
                    close()
                }
                .disabled(!state.main.canCall)
                Spacer()
                Button("Close") { close() }.keyboardShortcut(.cancelAction)
            }
            .padding(12)
        }
        .frame(minWidth: 760, idealWidth: 820, minHeight: 560, idealHeight: 640)
        .navigationTitle("Call Details")
        .task { await loadContactName() }
        .onDisappear { player?.stop() }
    }

    // MARK: sections

    private var callInformation: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Call Information").font(.headline)
            Card {
                VStack(alignment: .leading, spacing: 6) {
                    HStack(alignment: .firstTextBaseline) {
                        Text("Phone Number:").foregroundStyle(.secondary).frame(width: 150, alignment: .leading)
                        Text(d.phoneNumber).fontWeight(.semibold).textSelection(.enabled)
                        Button("Copy") { Pasteboard.copy(d.phoneNumber) }.buttonStyle(.link).font(.caption)
                    }
                    .font(.callout)
                    if let cid = d.outboundCallerId, !cid.isEmpty {
                        KeyValueRow(key: "Outbound CallerID:", value: cid, valueColor: .green)
                    }
                    KeyValueRow(key: "Call Time:", value: d.callTime.historyStamp)
                    HStack(alignment: .firstTextBaseline) {
                        Text("Direction:").foregroundStyle(.secondary).frame(width: 150, alignment: .leading)
                        Text(d.directionText)
                        TransportBadge(label: d.transportLabel, isWebRtc: d.transportLabel.lowercased().contains("webrtc"))
                        if let name = d.connectionName, !name.isEmpty { Text(name).foregroundStyle(.secondary) }
                    }
                    .font(.callout)
                    KeyValueRow(key: "Status:", value: d.statusText, valueColor: d.wasAnswered ? .green : .orange)
                    KeyValueRow(key: "Duration:", value: d.durationText)
                    KeyValueRow(key: "Was Answered:", value: d.wasAnswered ? "Yes" : "No", valueColor: d.wasAnswered ? .green : .red)
                    KeyValueRow(key: "Call Ended By:", value: d.endedByText, valueColor: .accentColor)
                }
            }
        }
    }

    private var timingInformation: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Timing Information").font(.headline)
            Card {
                VStack(alignment: .leading, spacing: 6) {
                    KeyValueRow(key: "Ringback Duration:", value: d.ringbackDurationText, keyWidth: 130)
                    KeyValueRow(key: "Ringback Start:", value: d.ringbackStart?.timeMillis ?? "—", keyWidth: 130)
                    KeyValueRow(key: "Answer Time:", value: d.answerTime?.timeMillis ?? "—", keyWidth: 130)
                }
            }
        }
    }

    private var amoCrm: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("AmoCRM").font(.headline)
            Card {
                VStack(alignment: .leading, spacing: 6) {
                    HStack(alignment: .firstTextBaseline) {
                        Text("Record added to:").foregroundStyle(.secondary).frame(width: 170, alignment: .leading)
                        if let id = d.kommoLeadId, let url = d.kommoLeadUrl, let link = URL(string: url) {
                            Link("Lead #\(id)", destination: link)
                        } else if let id = d.kommoLeadId {
                            Text("Lead #\(id)")
                        } else {
                            Text("Not added to any lead").fontWeight(.medium)
                        }
                    }
                    .font(.callout)
                    HStack(alignment: .firstTextBaseline) {
                        Text("AmoCRM contact name:").foregroundStyle(.secondary).frame(width: 170, alignment: .leading)
                        if let contactError {
                            Text(contactError).foregroundStyle(.secondary)
                        } else {
                            Text(contactName)
                        }
                    }
                    .font(.callout)
                    HStack(alignment: .firstTextBaseline) {
                        Text("Recording upload status:").foregroundStyle(.secondary).frame(width: 170, alignment: .leading)
                        Text(d.kommoUploadStatus.isEmpty ? "—" : d.kommoUploadStatus).foregroundStyle(uploadColor)
                    }
                    .font(.callout)
                    if let reason = d.kommoUploadReason, !reason.isEmpty {
                        Text(reason).font(.caption).foregroundStyle(.secondary).padding(.leading, 170)
                    }
                    if let src = d.kommoUploadedRecordingSource, !src.isEmpty {
                        KeyValueRow(key: "Uploaded recording:", value: src, keyWidth: 170)
                    }
                }
            }
        }
    }

    private var uploadColor: Color {
        let s = d.kommoUploadStatus
        if s.hasPrefix("✅") { return .green }
        if s.hasPrefix("❌") { return .red }
        if s.hasPrefix("⚠️") { return .orange }
        return .primary
    }

    private var sendToCrm: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Send result to CRM").font(.headline)
            VStack(alignment: .leading, spacing: 10) {
                HStack(alignment: .firstTextBaseline) {
                    Text("Recording:").foregroundStyle(.secondary)
                    if d.kommoGatewayUpload && !d.hasRecording {
                        Text("Recording will be fetched from Miko PBX by Gateway")
                    } else if d.hasRecording {
                        Text((d.recordingFilePath as NSString?)?.lastPathComponent ?? "Local recording").lineLimit(1).truncationMode(.middle)
                    } else {
                        Text("No local recording").foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button(isPlaying ? "Stop" : "Play") { togglePlay() }
                        .disabled(!d.hasRecording)
                }
                .font(.callout)
                if let playError { Text(playError).font(.caption).foregroundStyle(.red) }
                HStack(spacing: 12) {
                    if d.canRetryKommo {
                        Button {
                            retryBusy = true; retryMessage = nil
                            Task {
                                let err = await state.retryKommo(d)
                                retryMessage = err ?? (d.kommoGatewayUpload ? "Queued on PBX Gateway" : "Uploaded")
                                retryBusy = false
                            }
                        } label: {
                            HStack {
                                if retryBusy { ProgressView().controlSize(.small) }
                                Text(d.kommoGatewayUpload ? "Retry upload via Gateway" : "Retry upload")
                            }
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(.green)
                        .disabled(retryBusy)
                    }
                    if let retryMessage { Text(retryMessage).font(.caption).foregroundStyle(.secondary) }
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color.green.opacity(0.7), lineWidth: 1))
        }
    }

    private var technicalDetails: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Technical Details").font(.headline)
                Spacer()
                Picker("", selection: $logFilter) {
                    ForEach(LogFilter.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 380)
                Button("Copy") { Pasteboard.copy(filteredLogs.joined(separator: "\n")) }
                    .disabled(filteredLogs.isEmpty)
            }
            ScrollView([.vertical, .horizontal]) {
                Text(filteredLogs.isEmpty ? "No log entries for this filter." : filteredLogs.joined(separator: "\n"))
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(8)
            }
            .frame(minHeight: 120, maxHeight: 220)
            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color(nsColor: .separatorColor)))
        }
    }

    /// Same substring rules as WPF CallDetailsWindow.FilterLogs.
    private var filteredLogs: [String] {
        switch logFilter {
        case .all: return d.technicalDetails
        case .amo: return d.technicalDetails.filter { $0.contains("AmoCrm") || $0.contains("AmoCRM") }
        case .webrtc: return d.technicalDetails.filter { $0.contains("WebRtc") || $0.contains("WebRTC") || $0.contains("webrtc") }
        case .sip: return d.technicalDetails.filter { $0.contains("[Sip") || $0.contains("[SIP") || $0.contains("SIP") }
        }
    }

    // MARK: actions

    private func loadContactName() async {
        guard d.kommoEnabled else { return }
        let r = await state.lookupKommoContact(d.phoneNumber)
        if let name = r.name, !name.isEmpty { contactName = name; contactError = nil }
        else { contactError = r.error ?? "Contact not found in AmoCRM" }
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

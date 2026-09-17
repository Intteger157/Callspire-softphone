import SwiftUI
import AVFoundation
import AppKit

struct CallDetailsView: View {
    @EnvironmentObject var state: AppState
    let details: CallDetails
    @State private var player: AVAudioPlayer?
    @State private var retryMsg: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Call details").font(.title2.bold())
            gridRow("Number", details.phoneNumber)
            gridRow("When", details.callTime.formatted())
            gridRow("Duration", details.durationText)
            gridRow("Transport", details.transportLabel)
            gridRow("Line", details.connectionName ?? "—")
            gridRow("Status", details.status)
            if details.hasRecording {
                HStack {
                    Button("Play") {
                        Task {
                            struct K: Codable { var phoneNumber: String; var callTime: Date }
                            struct R: Codable { var filePath: String?; var error: String? }
                            if let r = try? await state.ipc.request("prepareRecording", params: K(phoneNumber: details.phoneNumber, callTime: details.callTime), as: R.self),
                               let path = r.filePath {
                                player = try? AVAudioPlayer(contentsOf: URL(fileURLWithPath: path))
                                player?.play()
                            }
                        }
                    }
                    Button("Stop") { player?.stop() }
                }
            }
            if details.kommoEnabled {
                Text("Kommo").font(.headline)
                Text(details.kommoUploadStatus)
                if let reason = details.kommoUploadReason { Text(reason).font(.caption).foregroundStyle(.secondary) }
                if details.canRetryKommo {
                    Button("Retry upload") {
                        Task {
                            struct K: Codable { var phoneNumber: String; var callTime: Date }
                            let r = try? await state.ipc.request("retryKommo", params: K(phoneNumber: details.phoneNumber, callTime: details.callTime), as: OkError.self)
                            retryMsg = r?.error ?? (r?.ok == true ? "Queued" : nil)
                        }
                    }
                }
                if let msg = retryMsg { Text(msg).font(.caption) }
            }
            if !details.technicalDetails.isEmpty {
                Text("Technical log").font(.headline)
                ScrollView { Text(details.technicalDetails.joined(separator: "\n")).font(.system(.caption, design: .monospaced)) }
                    .frame(minHeight: 120)
            }
            HStack {
                Button("Call back") { state.setPhone(details.phoneNumber); state.placeCall(); state.callDetails = nil }
                Spacer()
                Button("Close") { state.callDetails = nil }
            }
        }
        .padding()
        .frame(minWidth: 480)
    }

    private func gridRow(_ k: String, _ v: String) -> some View {
        HStack { Text(k).foregroundStyle(.secondary).frame(width: 120, alignment: .leading); Text(v); Spacer() }
    }
}

struct LeadSelectionView: View {
    @EnvironmentObject var state: AppState
    let request: KommoLeadPickerRequest
    @State private var query = ""

    var body: some View {
        VStack(alignment: .leading) {
            Text("Select Kommo lead").font(.title2.bold())
            Text(request.phoneNumber ?? "").foregroundStyle(.secondary)
            TextField("Search", text: $query)
            List(request.leads.filter { query.isEmpty || $0.name.localizedCaseInsensitiveContains(query) || $0.description.localizedCaseInsensitiveContains(query) }) { lead in
                Button {
                    state.finishKommo(lead.id)
                } label: {
                    VStack(alignment: .leading) {
                        Text(lead.name).font(.headline)
                        Text(lead.description).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            HStack {
                Button("Skip") { state.finishKommo(nil) }
                Spacer()
            }
        }.padding().frame(minWidth: 420, minHeight: 360)
    }
}

struct GatewayLeadSheet: View {
    @EnvironmentObject var state: AppState
    let phone: String
    @State private var leadId = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Attach call to Kommo").font(.title2.bold())
            Text(phone)
            Button("Attach to contact") { state.finishLead(LeadSelectionResult(proceed: true, cancelled: false, leadId: nil)) }
            HStack {
                TextField("Lead ID", text: $leadId)
                Button("Use ID") {
                    state.finishLead(LeadSelectionResult(proceed: true, cancelled: false, leadId: Int64(leadId)))
                }
            }
            Button("Skip") { state.finishLead(LeadSelectionResult(proceed: false, cancelled: true, leadId: nil)) }
        }.padding().frame(width: 360)
    }
}

struct ConnectionSelectionSheet: View {
    @EnvironmentObject var state: AppState
    let request: ConnectionSelectionRequest
    @State private var callerId: String = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Choose line").font(.title2.bold())
            if request.hasMain {
                Button {
                    state.finishConnection(ConnectionSelectionResult(slot: "main", callerId: callerId.isEmpty ? request.selectedCallerId : callerId))
                } label: {
                    VStack(alignment: .leading) {
                        Text(request.mainName ?? "Main")
                        Text(request.mainStatus ?? "").font(.caption).foregroundStyle(.secondary)
                    }.frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            if request.hasSecondary {
                Button {
                    state.finishConnection(ConnectionSelectionResult(slot: "secondary", callerId: callerId.isEmpty ? nil : callerId))
                } label: {
                    VStack(alignment: .leading) {
                        Text(request.secondaryName ?? "Secondary")
                        Text(request.secondaryStatus ?? "").font(.caption).foregroundStyle(.secondary)
                    }.frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            if !request.mainCallerIds.isEmpty {
                Picker("Caller ID", selection: $callerId) {
                    ForEach(request.mainCallerIds) { Text($0.displayText).tag($0.number) }
                }
            }
            Button("Cancel", role: .cancel) { state.finishConnection(ConnectionSelectionResult()) }
        }
        .padding()
        .onAppear { callerId = request.selectedCallerId ?? request.mainCallerIds.first?.number ?? "" }
        .frame(width: 360)
    }
}

struct LogView: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        VStack(alignment: .leading) {
            HStack {
                Text("Logs").font(.title2.bold())
                Spacer()
                Button("Clear") { state.logs.removeAll(); Task { try? await state.ipc.requestVoid("clearLog") } }
                Button("Copy") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(state.logs.joined(separator: "\n"), forType: .string) }
            }
            ScrollView {
                LazyVStack(alignment: .leading) {
                    ForEach(Array(state.logs.enumerated()), id: \.offset) { _, line in
                        Text(line).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                    }
                }
            }
        }.padding()
    }
}

struct AlertView: View {
    @Environment(\.dismiss) private var dismiss
    let alert: MessageAlert
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(alert.title).font(.headline)
            Text(alert.text)
            HStack { Spacer(); Button("OK") { dismiss() }.keyboardShortcut(.defaultAction) }
        }.padding().frame(minWidth: 320)
    }
}

struct UpdateAvailableSheet: View {
    @EnvironmentObject var state: AppState
    let info: UpdateInfo
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Update available").font(.title2.bold())
            Text("\(info.currentVersion) → \(info.version)")
            if let notes = info.notes { Text(notes).font(.caption) }
            if let sha = info.sha256 { Text("SHA-256: \(sha)").font(.system(.caption2, design: .monospaced)) }
            HStack {
                Button("Download") { if let u = URL(string: info.url) { NSWorkspace.shared.open(u) } }
                Button("Later") { state.update = nil }
            }
        }.padding().frame(width: 420)
    }
}

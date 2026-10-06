import Foundation
import AppKit
import Combine
import UniformTypeIdentifiers

/// Central observable state for the SwiftUI shell. Owns the sidecar process, the IPC connection and the
/// hidden WebRTC WKWebView; every UI action is a thin IPC call into Callspire.Service, which runs the
/// same `DesktopAppController` as the Windows build.
@MainActor
final class AppState: ObservableObject {
    let ipc = IpcClient()
    let sidecar = SidecarProcess()
    let webRtc = WebRtcEngineHost()
    let socketPath = SidecarProcess.defaultSocketPath

    // Connection / snapshot
    @Published var connected = false
    @Published var main = MainState()
    @Published var statusLine: String = "Starting…"

    // Settings: `settings` is the last server snapshot (status, options, devices, OAuth state).
    // The editor keeps its own draft so pushes from the sidecar never clobber what the user is typing.
    @Published var settings = SettingsDto()
    @Published var settingsLoaded = false

    // Call window
    @Published var call: CallWindowInfo?
    weak var callWindow: NSWindow?

    // Navigation / windows
    @Published var selectedNav: NavItem = .dialer
    @Published var settingsOpenRequest = 0
    @Published var settingsInitialPanel: SettingsPanel = .connection
    @Published var logsOpenRequest = 0

    // Modals
    @Published var alert: MessageAlert?
    @Published var update: UpdateInfo?
    @Published var connectionPicker: ConnectionSelectionRequest?
    @Published var gatewayLeadPhone: String?
    @Published var kommoPicker: KommoLeadPickerRequest?
    @Published var callDetails: CallDetails?

    // Logs
    @Published var logs: [String] = []
    @Published var logStreaming = false

    private var connectTask: Task<Void, Never>?
    private var connectGeneration = 0
    private var sidecarLifecycleStarted = false
    private var pendingProtocolUrls: [String] = []
    private var pendingOutboundCall: (number: String, forceSelection: Bool, callerId: String?)?
    private var connectionReply: ((ConnectionSelectionResult) -> Void)?
    private var leadReply: ((LeadSelectionResult) -> Void)?
    private var kommoReply: ((Int64?) -> Void)?

    enum NavItem: String, CaseIterable, Identifiable, Hashable {
        case dialer, history, statistics
        var id: String { rawValue }
        var title: String {
            switch self {
            case .dialer: return "Dialer"
            case .history: return "Call History"
            case .statistics: return "Call Statistics"
            }
        }
        var symbol: String {
            switch self {
            case .dialer: return "phone"
            case .history: return "clock"
            case .statistics: return "chart.bar"
            }
        }
        var symbolSelected: String {
            switch self {
            case .dialer: return "phone.fill"
            case .history: return "clock.fill"
            case .statistics: return "chart.bar.fill"
            }
        }
    }

    enum SettingsPanel: String, CaseIterable, Identifiable, Hashable {
        case connection, audio, general, appearance, advanced, integrations, about
        var id: String { rawValue }
        var title: String {
            switch self {
            case .connection: return "Connection"
            case .audio: return "Audio"
            case .general: return "General"
            case .appearance: return "Appearance"
            case .advanced: return "Advanced"
            case .integrations: return "Integrations"
            case .about: return "About"
            }
        }
        var symbol: String {
            switch self {
            case .connection: return "network"
            case .audio: return "speaker.wave.2"
            case .general: return "gearshape"
            case .appearance: return "paintbrush"
            case .advanced: return "slider.horizontal.3"
            case .integrations: return "link"
            case .about: return "info.circle"
            }
        }
    }

    // MARK: - Lifecycle

    /// Idempotent: safe from MainView, Settings, and Retry.
    func start() {
        guard !InstanceBroker.isSecondaryForwarder else { return }
        guard !sidecarLifecycleStarted else { return }
        sidecarLifecycleStarted = true
        registerIpcHandlers()
        wireIpcConnectionCallbacks()
        webRtc.attach(ipc: ipc)
        launchSidecarProcess()
    }

    private func wireIpcConnectionCallbacks() {
        ipc.onDisconnected = { [weak self] in
            Task { @MainActor in self?.handleIpcDisconnected() }
        }
    }

    /// Stop IPC, relaunch Callspire.Service, and reconnect (Settings Retry).
    func restartSidecar() async {
        guard !InstanceBroker.isSecondaryForwarder else { return }
        if !sidecarLifecycleStarted {
            start()
            return
        }
        connectTask?.cancel()
        ipc.disconnect()
        sidecar.stop()
        connected = false
        settingsLoaded = false
        statusLine = "Restarting Callspire.Service…"
        try? FileManager.default.removeItem(atPath: socketPath)
        launchSidecarProcess()
    }

    private func launchSidecarProcess() {
        sidecar.onCrash = { [weak self] in self?.statusLine = "Service crashed — restarting…" }
        do { try sidecar.start(socketPath: socketPath) }
        catch {
            statusLine = "Cannot start Callspire.Service: \(error.localizedDescription)"
            return
        }
        beginConnectLoop()
    }

    private func beginConnectLoop() {
        connectGeneration += 1
        connectTask?.cancel()
        let generation = connectGeneration
        connectTask = Task { await connectLoop(generation: generation) }
    }

    private func handleIpcDisconnected() {
        guard sidecarLifecycleStarted else { return }
        connected = false
        statusLine = "Disconnected — reconnecting…"
        beginConnectLoop()
    }

    func stop() {
        connectTask?.cancel()
        ipc.disconnect()
        sidecar.stop()
        webRtc.destroy()
    }

    private func connectLoop(generation: Int) async {
        for _ in 0..<80 {
            if Task.isCancelled || generation != connectGeneration { return }
            if statusLine == "Starting…" || statusLine.hasPrefix("Restarting") || statusLine.hasPrefix("Disconnected") {
                statusLine = "Waiting for Callspire.Service…"
            }
            do {
                try ipc.connect(path: socketPath)
                if Task.isCancelled || generation != connectGeneration {
                    ipc.disconnect(silent: true)
                    return
                }
                connected = true
                statusLine = "Connected"
                ipc.onEvent = { [weak self] name, data in
                    Task { @MainActor in self?.handleEvent(name, data) }
                }
                _ = try? await ipc.request("ping", as: Ping.self)
                if generation != connectGeneration { return }
                await refreshState()
                if logStreaming { await loadLogSnapshot() }
                flushPendingProtocolUrls()
                return
            } catch {
                statusLine = "Waiting for service…"
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        if generation == connectGeneration {
            statusLine = "Could not connect to Callspire.Service"
        }
    }

    private struct Ping: Codable { var pong: Bool?; var version: String?; var pid: Int? }

    func refreshState() async {
        do {
            let state = try await ipc.request("getState", as: MainState.self)
            main = state
            applyTheme(state.themeMode)
        } catch {
            log("getState failed: \(error.localizedDescription)")
        }
    }

    // MARK: - Inbound requests (C# → Swift modals / WebRTC host)

    private func registerIpcHandlers() {
        ipc.register("showConnectionSelection") { [weak self] params, reply in
            Task { @MainActor in
                let req = (try? params?.decode(ConnectionSelectionRequest.self)) ?? ConnectionSelectionRequest()
                self?.connectionPicker = req
                self?.connectionReply = { result in reply(.success(try? encodeJSON(result))) }
                NSApp.activate(ignoringOtherApps: true)
            }
        }
        ipc.register("showLeadSelection") { [weak self] params, reply in
            Task { @MainActor in
                let phone = (try? params?.decode(Phone.self))?.phoneNumber ?? ""
                self?.gatewayLeadPhone = phone
                self?.leadReply = { result in reply(.success(try? encodeJSON(result))) }
                NSApp.activate(ignoringOtherApps: true)
            }
        }
        ipc.register("showKommoLeadPicker") { [weak self] params, reply in
            Task { @MainActor in
                let req = (try? params?.decode(KommoLeadPickerRequest.self)) ?? KommoLeadPickerRequest()
                self?.kommoPicker = req
                self?.kommoReply = { id in
                    struct R: Codable { var leadId: Int64? }
                    reply(.success(try? encodeJSON(R(leadId: id))))
                }
                NSApp.activate(ignoringOtherApps: true)
            }
        }
        ipc.register("webRtcCreateHost") { [weak self] params, reply in
            Task { @MainActor in
                struct HostReq: Codable { var url: String; var enableDevTools: Bool? }
                let req = try? params?.decode(HostReq.self)
                let ok = await self?.webRtc.createHost(url: req?.url ?? "", enableDevTools: req?.enableDevTools ?? false) ?? false
                reply(.success(try? encodeJSON(["success": ok])))
            }
        }
        ipc.register("webRtcInvokeScript") { [weak self] params, reply in
            Task { @MainActor in
                struct Script: Codable { var script: String }
                let js = (try? params?.decode(Script.self))?.script ?? ""
                let result = await self?.webRtc.invokeScript(js) ?? ""
                reply(.success(try? encodeJSON(["result": result])))
            }
        }
    }

    private struct Phone: Codable { var phoneNumber: String }

    // MARK: - Inbound events

    private func handleEvent(_ name: String, _ data: AnyJSON?) {
        switch name {
        case "stateSnapshot":
            guard let data else { return }
            do {
                let s = try data.decode(MainState.self)
                main = s
                applyTheme(s.themeMode)
            } catch {
                log("stateSnapshot decode failed: \(error)")
            }
        case "showCallWindow":
            if let c = try? data?.decode(CallWindowInfo.self) {
                call = c
                NSApp.activate(ignoringOtherApps: true)
            }
        case "callStateChanged":
            if let s = try? data?.decode(CallState.self), var c = call, c.sessionId == s.sessionId {
                c.state = s
                call = c
            }
        case "callClosed":
            call = nil
            callWindow?.close()
            callWindow = nil
        case "bringCallWindowToFront":
            callWindow?.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
        case "showSettings":
            openSettings()
        case "settingsChanged":
            if let s = try? data?.decode(SettingsDto.self) { settings = s; settingsLoaded = true }
        case "showMessage":
            if let m = try? data?.decode(TitleText.self) { alert = MessageAlert(title: m.title, text: m.text) }
        case "showUpdateAvailable":
            if let u = try? data?.decode(UpdateInfo.self) { update = u }
        case "bringToForeground":
            NSApp.activate(ignoringOtherApps: true)
        case "logLines":
            if let lines = try? data?.decode(LogSnapshot.self) { appendLogs(lines.lines) }
        case "webRtcDestroyHost":
            webRtc.destroy()
        case "serviceReady":
            statusLine = "Service ready"
        default:
            break
        }
    }

    private struct TitleText: Codable { var title: String; var text: String }

    func applyTheme(_ mode: String) {
        switch mode.lowercased() {
        case "light": NSApp.appearance = NSAppearance(named: .aqua)
        case "dark": NSApp.appearance = NSAppearance(named: .darkAqua)
        default: NSApp.appearance = nil
        }
    }

    // MARK: - Dialer / connections

    func setPhone(_ value: String) {
        main.phoneNumber = value
        Task { try? await ipc.requestVoid("setPhoneNumber", params: ["value": value]) }
    }

    /// Queue an outbound call after Call Details (or another sheet) finishes dismissing.
    func scheduleOutboundCall(number: String, forceSelection: Bool = true, callerId: String? = nil) {
        let target = number.trimmingCharacters(in: .whitespaces)
        guard !target.isEmpty, main.canPlaceOutbound else { return }
        pendingOutboundCall = (target, forceSelection, callerId)
        callDetails = nil
    }

    func flushPendingOutboundCall() {
        guard callDetails == nil, connectionPicker == nil,
              let pending = pendingOutboundCall else { return }
        pendingOutboundCall = nil
        placeCall(number: pending.number, forceSelection: pending.forceSelection, callerId: pending.callerId)
    }

    /// `forceSelection`: server-side line / Caller ID sheet (both lines or explicit pick).
    /// Pass `callerId` when the user already chose from a local menu (history popover).
    func placeCall(number: String? = nil, slot: String? = nil, forceSelection: Bool = false, callerId: String? = nil) {
        let target = (number ?? main.phoneNumber).trimmingCharacters(in: .whitespaces)
        guard !target.isEmpty else { return }
        Task {
            struct P: Codable { var number: String; var slot: String?; var forceSelection: Bool }
            do {
                if let callerId, !callerId.isEmpty {
                    try await ipc.requestVoid("selectCallerId", params: ["number": callerId])
                }
                try await ipc.requestVoid("placeCall", params: P(number: target, slot: slot, forceSelection: forceSelection), timeout: 120)
            } catch { showError("Call failed", error) }
        }
    }

    func selectCallerId(_ number: String) {
        main.selectedCallerId = number
        Task { try? await ipc.requestVoid("selectCallerId", params: ["number": number]) }
    }

    func reconnect(slot: String) {
        Task { try? await ipc.requestVoid("reconnectSlot", params: ["slot": slot], timeout: 120) }
    }

    // MARK: - Calls

    func callVerb(_ method: String, digit: String? = nil) {
        guard let id = call?.sessionId else { return }
        Task {
            struct P: Codable { var sessionId: String; var digit: String? }
            do { try await ipc.requestVoid(method, params: P(sessionId: id, digit: digit)) }
            catch { log("\(method) failed: \(error.localizedDescription)") }
        }
    }

    /// Window closed by the user (red traffic light) while the call is still running.
    func callWindowClosedByUser() {
        guard call != nil else { return }
        callVerb("callViewClosed")
        call = nil
        callWindow = nil
    }

    func loadCallAudioDevices() async -> CallAudioDevices? {
        guard let id = call?.sessionId else { return nil }
        do { return try await ipc.request("callAudioDevices", params: ["sessionId": id], as: CallAudioDevices.self, timeout: 15) }
        catch { showError("Audio devices", error); return nil }
    }

    func switchCallAudioDevices(input: String?, output: String?) async {
        guard let id = call?.sessionId else { return }
        struct P: Codable { var sessionId: String; var inputId: String?; var outputId: String? }
        do { try await ipc.requestVoid("switchCallAudioDevices", params: P(sessionId: id, inputId: input, outputId: output)) }
        catch { showError("Audio devices", error) }
    }

    // MARK: - History / details

    func clearHistory() {
        Task { try? await ipc.requestVoid("clearHistory") }
    }

    func clearHistoryFilter() {
        Task { try? await ipc.requestVoid("clearHistoryFilter") }
    }

    func drillDownHistory(_ drill: String, phoneNumber: String? = nil) {
        Task {
            struct P: Codable { var drill: String; var phoneNumber: String? }
            try? await ipc.requestVoid("setHistoryDrillDown", params: P(drill: drill, phoneNumber: phoneNumber))
        }
        selectedNav = .history
    }

    func openHistoryDetails(_ item: HistoryItem) {
        Task {
            do {
                callDetails = try await ipc.request("getCallDetails", params: HistoryKey(phoneNumber: item.phoneNumber, callTime: item.callTime), as: CallDetails.self)
            } catch {
                showError("Call details", error)
            }
        }
    }

    func reloadCallDetails(_ d: CallDetails) async {
        if let fresh = try? await ipc.request("getCallDetails", params: HistoryKey(phoneNumber: d.phoneNumber, callTime: d.callTime), as: CallDetails.self) {
            callDetails = fresh
        }
    }

    // MARK: - Statistics

    func setStatisticsFilter(periodIndex: Int? = nil, connectionIndex: Int? = nil, directionIndex: Int? = nil, customFrom: Date? = nil, customTo: Date? = nil) {
        struct P: Codable { var periodIndex: Int?; var connectionIndex: Int?; var directionIndex: Int?; var customFrom: Date?; var customTo: Date? }
        Task { try? await ipc.requestVoid("setStatisticsFilter", params: P(periodIndex: periodIndex, connectionIndex: connectionIndex, directionIndex: directionIndex, customFrom: customFrom, customTo: customTo)) }
    }

    func exportStatisticsCsv() {
        let panel = NSSavePanel()
        panel.title = "Export call statistics"
        panel.nameFieldStringValue = "Callspire-stats-\(Self.fileStamp()).csv"
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task {
            struct R: Codable { var path: String }
            do {
                let r = try await ipc.request("exportStatisticsCsv", params: ["targetPath": url.path], as: R.self)
                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: r.path)])
            } catch {
                showError("Export CSV", error)
            }
        }
    }

    private static func fileStamp() -> String {
        let f = DateFormatter(); f.dateFormat = "yyyyMMdd-HHmm"; return f.string(from: Date())
    }

    // MARK: - Settings

    func openSettings(panel: SettingsPanel? = nil) {
        if let panel { settingsInitialPanel = panel }
        settingsOpenRequest += 1
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Fresh editor snapshot from the sidecar (re-reads settings.json).
    func loadSettings() async -> SettingsDto? {
        guard ipc.isConnected else { return nil }
        do {
            let s = try await ipc.request("getSettings", as: SettingsDto.self)
            settings = s
            settingsLoaded = true
            return s
        } catch {
            if ipc.isConnected { showError("Settings", error) }
            return nil
        }
    }

    /// Validates + saves + reconnects in shared code. Returns an error message or nil.
    func saveSettings(_ draft: SettingsDto) async -> String? {
        struct R: Codable { var error: String? }
        do {
            let r = try await ipc.request("saveSettings", params: draft.asFields(), as: R.self, timeout: 120)
            if r.error == nil { _ = await loadSettings() }
            return r.error
        } catch {
            return error.localizedDescription
        }
    }

    func testConnection(secondary: Bool, draft: SettingsDto) async -> TestConnectionResult? {
        struct T: Codable { var secondary: Bool; var fields: SettingsFields }
        do { return try await ipc.request("testConnection", params: T(secondary: secondary, fields: draft.asFields()), as: TestConnectionResult.self, timeout: 30) }
        catch { return TestConnectionResult(statusText: error.localizedDescription, isError: true) }
    }

    func refreshAudioDevices() async -> SettingsDto? {
        if let s = try? await ipc.request("refreshAudioDevices", as: SettingsDto.self) { settings = s; return s }
        return nil
    }

    func previewRingtone(draft: SettingsDto) {
        struct P: Codable { var fields: SettingsFields }
        Task { try? await ipc.requestVoid("previewRingtone", params: P(fields: draft.asFields())) }
    }

    func stopRingtone() { Task { try? await ipc.requestVoid("stopRingtone") } }

    func setTheme(_ key: String) {
        applyTheme(key)
        Task { try? await ipc.requestVoid("setTheme", params: ["theme": key]) }
    }

    func checkForUpdates() async -> UpdateCheckResult? {
        do { return try await ipc.request("checkUpdates", as: UpdateCheckResult.self, timeout: 60) }
        catch { return UpdateCheckResult(status: "Update check failed: \(error.localizedDescription)") }
    }

    func openUpdateUrl() { Task { try? await ipc.requestVoid("openUpdateUrl") } }
    func openLogsFolder() { Task { try? await ipc.requestVoid("openLogsFolder") } }
    func openRecordingsFolder() { Task { try? await ipc.requestVoid("openRecordingsFolder") } }
    func openExternal(_ target: String) {
        if let url = URL(string: target), url.scheme != nil { NSWorkspace.shared.open(url) }
        else { Task { try? await ipc.requestVoid("openExternal", params: ["target": target]) } }
    }

    // MARK: - Kommo / Gateway

    func kommoAuthorize(draft: SettingsDto) async -> String? {
        struct P: Codable { var clientId: String; var clientSecret: String; var redirectUri: String }
        struct R: Codable { var error: String?; var status: KommoOAuthStatus? }
        do {
            let r = try await ipc.request("kommoAuthorize", params: P(clientId: draft.kommoClientId, clientSecret: draft.kommoClientSecret, redirectUri: draft.kommoRedirectUri), as: R.self, timeout: 600)
            if let st = r.status { settings.kommoOAuth = st }
            return r.error
        } catch {
            return error.localizedDescription
        }
    }

    func gatewayAuthorize(url: String, ext: String) async -> String? {
        struct P: Codable {
            var url: String
            var pbxExtension: String
            enum CodingKeys: String, CodingKey { case url; case pbxExtension = "extension" }
        }
        do { try await ipc.requestVoid("gatewayAuthorize", params: P(url: url, pbxExtension: ext)); return nil }
        catch { return error.localizedDescription }
    }

    func gatewayClear() async {
        do { try await ipc.requestVoid("gatewayClear") }
        catch { showError("PBX Gateway", error) }
        _ = await loadSettings()
    }

    func lookupKommoContact(_ phone: String) async -> KommoContactLookup {
        do { return try await ipc.request("getKommoContactName", params: ["phoneNumber": phone], as: KommoContactLookup.self, timeout: 30) }
        catch { return KommoContactLookup(error: error.localizedDescription) }
    }

    func prepareRecording(_ d: CallDetails) async -> (path: String?, error: String?) {
        struct R: Codable { var filePath: String?; var error: String? }
        do {
            let r = try await ipc.request("prepareRecording", params: HistoryKey(phoneNumber: d.phoneNumber, callTime: d.callTime), as: R.self, timeout: 60)
            return (r.filePath, r.error)
        } catch {
            return (nil, error.localizedDescription)
        }
    }

    func retryKommo(_ d: CallDetails) async -> String? {
        struct R: Codable { var ok: Bool?; var error: String?; var details: CallDetails? }
        do {
            let r = try await ipc.request("retryKommo", params: HistoryKey(phoneNumber: d.phoneNumber, callTime: d.callTime), as: R.self, timeout: 600)
            if let fresh = r.details { callDetails = fresh }
            return r.ok == true ? nil : (r.error ?? "Upload failed")
        } catch {
            return error.localizedDescription
        }
    }

    // MARK: - Logs

    func openLogs() {
        logsOpenRequest += 1
        NSApp.activate(ignoringOtherApps: true)
    }

    func loadLogSnapshot() async {
        logStreaming = true
        if let snap = try? await ipc.request("getLogSnapshot", as: LogSnapshot.self) {
            logs = snap.lines
        }
    }

    func stopLogStreaming() {
        logStreaming = false
        Task { try? await ipc.requestVoid("setLogStreaming", params: ["enabled": false]) }
    }

    func clearLogs() {
        logs.removeAll()
        Task { try? await ipc.requestVoid("clearLog") }
    }

    private func appendLogs(_ lines: [String]) {
        logs.append(contentsOf: lines)
        if logs.count > 4000 { logs.removeFirst(logs.count - 4000) }
    }

    func log(_ text: String) {
        NSLog("[Callspire] %@", text)
        guard ipc.isConnected else { return }
        Task { try? await ipc.requestVoid("log", params: ["text": text]) }
    }

    // MARK: - System

    func handleUrl(_ url: URL) {
        let s = url.absoluteString
        guard ipc.isConnected else {
            pendingProtocolUrls.append(s)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        sendProtocolUrl(s)
    }

    private func sendProtocolUrl(_ url: String) {
        Task { try? await ipc.requestVoid("handleProtocolUrl", params: ["url": url]) }
        NSApp.activate(ignoringOtherApps: true)
    }

    private func flushPendingProtocolUrls() {
        let pending = pendingProtocolUrls
        pendingProtocolUrls.removeAll()
        for url in pending { sendProtocolUrl(url) }
    }

    func notifySleep() { Task { try? await ipc.requestVoid("systemWillSleep") } }
    func notifyWake() { Task { try? await ipc.requestVoid("systemDidWake") } }

    // MARK: - Modal replies

    func finishConnection(_ result: ConnectionSelectionResult) {
        connectionReply?(result)
        connectionReply = nil
        connectionPicker = nil
    }

    func finishLead(_ result: LeadSelectionResult) {
        leadReply?(result)
        leadReply = nil
        gatewayLeadPhone = nil
    }

    func finishKommo(_ id: Int64?) {
        kommoReply?(id)
        kommoReply = nil
        kommoPicker = nil
    }

    func showError(_ title: String, _ error: Error) {
        alert = MessageAlert(title: title, text: error.localizedDescription)
    }
}

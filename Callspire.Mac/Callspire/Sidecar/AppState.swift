import Foundation
import AppKit
import Combine

@MainActor
final class AppState: ObservableObject {
    let ipc = IpcClient()
    let sidecar = SidecarProcess()
    let webRtc = WebRtcEngineHost()
    let socketPath = SidecarProcess.defaultSocketPath

    @Published var connected = false
    @Published var main = MainState()
    @Published var settings = SettingsDto()
    @Published var call: CallWindowInfo?
    @Published var logs: [String] = []
    @Published var selectedNav: NavItem = .dialer
    @Published var showSettings = false
    @Published var showLogs = false
    @Published var alert: MessageAlert?
    @Published var update: UpdateInfo?
    @Published var connectionPicker: ConnectionSelectionRequest?
    @Published var gatewayLeadPhone: String?
    @Published var kommoPicker: KommoLeadPickerRequest?
    @Published var callDetails: CallDetails?
    @Published var statusLine: String = "Starting…"

    private var connectTask: Task<Void, Never>?
    private var connectionReply: ((ConnectionSelectionResult) -> Void)?
    private var leadReply: ((LeadSelectionResult) -> Void)?
    private var kommoReply: ((Int64?) -> Void)?

    enum NavItem: String, CaseIterable, Identifiable {
        case dialer, history, statistics
        var id: String { rawValue }
        var title: String {
            switch self {
            case .dialer: return "Dialer"
            case .history: return "History"
            case .statistics: return "Statistics"
            }
        }
        var symbol: String {
            switch self {
            case .dialer: return "circle.grid.3x3.fill"
            case .history: return "clock.arrow.circlepath"
            case .statistics: return "chart.bar.fill"
            }
        }
    }

    func start() {
        registerIpcHandlers()
        sidecar.onCrash = { [weak self] in self?.statusLine = "Service crashed — restarting…" }
        try? sidecar.start(socketPath: socketPath)
        connectTask?.cancel()
        connectTask = Task { await connectLoop() }
        webRtc.attach(ipc: ipc)
    }

    func stop() {
        connectTask?.cancel()
        ipc.disconnect()
        sidecar.stop()
        webRtc.destroy()
    }

    private func connectLoop() async {
        for _ in 0..<40 {
            if Task.isCancelled { return }
            do {
                try ipc.connect(path: socketPath)
                connected = true
                statusLine = "Connected"
                ipc.onEvent = { [weak self] name, data in
                    Task { @MainActor in self?.handleEvent(name, data) }
                }
                ipc.onDisconnected = { [weak self] in
                    Task { @MainActor in
                        self?.connected = false
                        self?.statusLine = "Disconnected"
                    }
                }
                _ = try? await ipc.request("ping", as: Ping.self)
                if let state = try? await ipc.request("getState", as: MainState.self) {
                    main = state
                    applyTheme(state.themeMode)
                }
                return
            } catch {
                statusLine = "Waiting for service…"
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        statusLine = "Could not connect to Callspire.Service"
    }

    private struct Ping: Codable { var pong: Bool?; var version: String?; var pid: Int? }

    private func registerIpcHandlers() {
        ipc.register("showConnectionSelection") { [weak self] params, reply in
            Task { @MainActor in
                let req = (try? params?.decode(ConnectionSelectionRequest.self)) ?? ConnectionSelectionRequest()
                self?.connectionPicker = req
                self?.connectionReply = { result in
                    reply(.success(try? encodeJSON(result)))
                }
            }
        }
        ipc.register("showLeadSelection") { [weak self] params, reply in
            Task { @MainActor in
                let phone = (try? params?.decode(Phone.self))?.phoneNumber ?? ""
                self?.gatewayLeadPhone = phone
                self?.leadReply = { result in
                    reply(.success(try? encodeJSON(result)))
                }
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

    private func handleEvent(_ name: String, _ data: AnyJSON?) {
        switch name {
        case "stateSnapshot":
            if let s = try? data?.decode(MainState.self) { main = s; applyTheme(s.themeMode) }
        case "showCallWindow":
            if let c = try? data?.decode(CallWindowInfo.self) { call = c; NSApp.activate(ignoringOtherApps: true) }
        case "callStateChanged":
            if let s = try? data?.decode(CallState.self), var c = call, c.sessionId == s.sessionId {
                c.state = s; call = c
            }
        case "callClosed", "bringCallWindowToFront":
            if name == "callClosed" { call = nil }
            else { NSApp.activate(ignoringOtherApps: true) }
        case "showSettings": showSettings = true
        case "settingsChanged":
            if let s = try? data?.decode(SettingsDto.self) { settings = s }
        case "showMessage":
            if let m = try? data?.decode(TitleText.self) { alert = MessageAlert(title: m.title, text: m.text) }
        case "showUpdateAvailable":
            if let u = try? data?.decode(UpdateInfo.self) { update = u }
        case "bringToForeground":
            NSApp.activate(ignoringOtherApps: true)
        case "logLines":
            if let lines = try? data?.decode(LogLines.self) { logs.append(contentsOf: lines.lines); if logs.count > 2000 { logs.removeFirst(logs.count - 2000) } }
        case "webRtcDestroyHost":
            webRtc.destroy()
        case "serviceReady":
            statusLine = "Service ready"
        default: break
        }
    }

    private struct TitleText: Codable { var title: String; var text: String }
    private struct LogLines: Codable { var lines: [String] }

    func applyTheme(_ mode: String) {
        switch mode.lowercased() {
        case "light": NSApp.appearance = NSAppearance(named: .aqua)
        case "dark": NSApp.appearance = NSAppearance(named: .darkAqua)
        default: NSApp.appearance = nil
        }
    }

    // MARK: - Commands

    func setPhone(_ value: String) {
        main.phoneNumber = value
        Task { try? await ipc.requestVoid("setPhoneNumber", params: ["value": value]) }
    }

    func placeCall(slot: String? = nil) {
        Task {
            struct P: Codable { var number: String; var slot: String? }
            try? await ipc.requestVoid("placeCall", params: P(number: main.phoneNumber, slot: slot))
        }
    }

    func reconnect(slot: String) {
        Task { try? await ipc.requestVoid("reconnectSlot", params: ["slot": slot]) }
    }

    func callVerb(_ method: String, digit: String? = nil) {
        guard let id = call?.sessionId else { return }
        Task {
            struct P: Codable { var sessionId: String; var digit: String? }
            try? await ipc.requestVoid(method, params: P(sessionId: id, digit: digit))
        }
    }

    func closeCall() {
        callVerb("callViewClosed")
        call = nil
    }

    func openSettings() {
        showSettings = true
        Task {
            if let s = try? await ipc.request("getSettings", as: SettingsDto.self) { settings = s }
        }
    }

    func saveSettings() async -> String? {
        struct R: Codable { var error: String? }
        let r = try? await ipc.request("saveSettings", params: settings.asFields(), as: R.self)
        return r?.error
    }

    func handleUrl(_ url: URL) {
        Task { try? await ipc.requestVoid("handleProtocolUrl", params: ["url": url.absoluteString]) }
        NSApp.activate(ignoringOtherApps: true)
    }

    func notifySleep() { Task { try? await ipc.requestVoid("systemWillSleep") } }
    func notifyWake() { Task { try? await ipc.requestVoid("systemDidWake") } }

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

    func openHistoryDetails(_ item: HistoryItem) {
        Task {
            struct K: Codable { var phoneNumber: String; var callTime: Date }
            if let d = try? await ipc.request("getCallDetails", params: K(phoneNumber: item.phoneNumber, callTime: item.callTime), as: CallDetails.self) {
                callDetails = d
            }
        }
    }
}

private struct StringMap: Codable {
    // helper unused
}

import Foundation
import WebKit
import AppKit

/// Hidden WKWebView that hosts WebRtcClient/index.html (served by the sidecar asset server).
/// Mirrors AvaloniaWebRtcEngineHost / WPF WebRtcEngineHost: the view must stay in the hierarchy
/// with a non-zero size so getUserMedia works.
@MainActor
final class WebRtcEngineHost: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
    private var webView: WKWebView?
    private weak var ipc: IpcClient?
    private var loadContinuation: CheckedContinuation<Bool, Never>?
    private var loadSettling = false
    private var appNapActivity: NSObjectProtocol?
    private var eventQueue: AsyncStream<String>.Continuation?
    private var eventPump: Task<Void, Never>?

    func attach(ipc: IpcClient) {
        self.ipc = ipc
        guard eventPump == nil else { return }
        // JsSIP events must reach WebRtcService in emission order (new_session → incoming → call_progress …);
        // awaiting each IPC reply keeps the sidecar from processing them concurrently.
        var continuation: AsyncStream<String>.Continuation?
        let stream = AsyncStream<String> { continuation = $0 }
        eventQueue = continuation
        eventPump = Task { [weak self] in
            for await body in stream {
                try? await self?.ipc?.requestVoid("webRtcEngineEvent", params: ["json": body], timeout: 10)
            }
        }
    }

    func createHost(url: String, enableDevTools: Bool) async -> Bool {
        destroy()
        loadSettling = false
        let config = WKWebViewConfiguration()
        config.mediaTypesRequiringUserActionForPlayback = []
        // The page is never visible, so WebKit would otherwise throttle its timers and suspend the
        // WebContent process: JsSIP keep-alives and C# watchdog pongs stop and the UA reconnects in a loop.
        Self.setPrivateFlag(config.preferences, "hiddenPageDOMTimerThrottlingEnabled", false)
        Self.setPrivateFlag(config.preferences, "hiddenPageDOMTimerThrottlingAutoIncreases", false)
        Self.setPrivateFlag(config.preferences, "pageVisibilityBasedProcessSuppressionEnabled", false)
        Self.setPrivateFlag(config, "alwaysRunsAtForegroundPriority", true)
        beginAppNapExemption()
        let uc = config.userContentController
        uc.add(self, name: "callspire")
        // phone.js / index.html post via invokeCSharpAction (Avalonia) or chrome.webview (WPF).
        // Callspire Mac hosts WKWebView in Swift — inject the bridge before any page script runs.
        let bridge = """
        (function () {
            if (typeof window.invokeCSharpAction === 'function') return;
            window.invokeCSharpAction = function (jsonStr) {
                try {
                    window.webkit.messageHandlers.callspire.postMessage(jsonStr);
                } catch (e) { /* swallow */ }
            };
        })();
        """
        uc.addUserScript(WKUserScript(source: bridge, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        let wv = WKWebView(frame: NSRect(x: 0, y: 0, width: 8, height: 8), configuration: config)
        wv.navigationDelegate = self
        Self.setPrivateFlag(wv, "windowOcclusionDetectionEnabled", false)
        if #available(macOS 13.3, *), enableDevTools {
            wv.isInspectable = true
        }
        // Keep a tiny off-screen window so the view stays alive and has a surface.
        let win = NSWindow(contentRect: NSRect(x: -40, y: -40, width: 8, height: 8),
                           styleMask: [.borderless], backing: .buffered, defer: true)
        win.isReleasedWhenClosed = false
        win.contentView = wv
        win.alphaValue = 0.01
        win.orderBack(nil)
        webView = wv
        objc_setAssociatedObject(wv, "hostWindow", win, .OBJC_ASSOCIATION_RETAIN)

        guard let page = URL(string: url) else { return false }
        return await withCheckedContinuation { cont in
            loadContinuation = cont
            wv.load(URLRequest(url: page))
            DispatchQueue.main.asyncAfter(deadline: .now() + 12) { [weak self] in
                self?.finishLoad(false)
            }
        }
    }

    func invokeScript(_ js: String) async -> String {
        guard let webView else { return "" }
        return await withCheckedContinuation { cont in
            webView.evaluateJavaScript(js) { result, _ in
                if let s = result as? String { cont.resume(returning: s) }
                else if let result { cont.resume(returning: String(describing: result)) }
                else { cont.resume(returning: "") }
            }
        }
    }

    func destroy() {
        if let wv = webView {
            wv.configuration.userContentController.removeScriptMessageHandler(forName: "callspire")
            wv.stopLoading()
            if let win = objc_getAssociatedObject(wv, "hostWindow") as? NSWindow { win.close() }
        }
        webView = nil
        finishLoad(false)
        if let activity = appNapActivity {
            ProcessInfo.processInfo.endActivity(activity)
            appNapActivity = nil
        }
    }

    private func beginAppNapExemption() {
        guard appNapActivity == nil else { return }
        appNapActivity = ProcessInfo.processInfo.beginActivity(
            options: [.userInitiatedAllowingIdleSystemSleep, .latencyCritical],
            reason: "Callspire keeps the SIP/WebRTC registration alive"
        )
    }

    /// KVC resolves `key` to WebKit's private `_setKey:` setter; skipped when this WebKit lacks it.
    private static func setPrivateFlag(_ object: NSObject, _ key: String, _ value: Bool) {
        let setter = "_set" + key.prefix(1).uppercased() + key.dropFirst() + ":"
        guard object.responds(to: NSSelectorFromString(setter)) else { return }
        object.setValue(value, forKey: key)
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        let body: String
        if let s = message.body as? String { body = s }
        else if let data = try? JSONSerialization.data(withJSONObject: message.body), let s = String(data: data, encoding: .utf8) { body = s }
        else { return }
        eventQueue?.yield(body)
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        // didFinish fires for iframe navigations too — settle once after index.html + slot iframes.
        guard loadContinuation != nil, !loadSettling else { return }
        loadSettling = true
        Task { @MainActor in
            let slots = await waitForEngineSlots(timeout: 12)
            finishLoad(slots)
        }
    }

    /// Poll until index.html marks WebRtcClient slot iframes ready (same flag C# WaitForSlotReadyAsync uses).
    private func waitForEngineSlots(timeout: TimeInterval) async -> Bool {
        guard webView != nil else { return false }
        let deadline = Date().addingTimeInterval(timeout)
        let probe = """
        (() => { try {
            return !!(window._slotReady && window._slotReady.main);
        } catch (e) { return false; } })()
        """
        while Date() < deadline {
            let ok = await invokeScript(probe)
            if ok == "true" || ok == "1" { return true }
            try? await Task.sleep(nanoseconds: 150_000_000)
        }
        return false
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        finishLoad(false)
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        Task { try? await ipc?.requestVoid("webRtcHostReset") }
    }

    private func finishLoad(_ ok: Bool) {
        loadContinuation?.resume(returning: ok)
        loadContinuation = nil
    }
}

import Foundation
import ObjectiveC
import WebKit
import AppKit

/// Hidden WKWebView that hosts WebRtcClient/index.html (served by the sidecar asset server).
/// Mirrors AvaloniaWebRtcEngineHost / WPF WebRtcEngineHost: the view must stay in the hierarchy
/// with a non-zero size so getUserMedia works.
///
/// Remote audio on macOS is routed through the **app window** that hosts the WKWebView. A separate
/// nearly invisible window (low alpha, off-screen) decodes RTP in JS but often produces no speaker output.
@MainActor
final class WebRtcEngineHost: NSObject, WKScriptMessageHandler, WKNavigationDelegate, WKUIDelegate {
    private var webView: WKWebView?
    private weak var ipc: IpcClient?
    private weak var mainHostWindow: NSWindow?
    private weak var callHostWindow: NSWindow?
    private var loadContinuation: CheckedContinuation<Bool, Never>?
    private var loadSettling = false
    private var appNapActivity: NSObjectProtocol?
    private var eventQueue: AsyncStream<String>.Continuation?
    private var eventPump: Task<Void, Never>?

    private static var ghostWindowAssociationKey: UInt8 = 0
    /// WebKit needs a real backing size; keep the view small but non-zero inside the app window.
    private static let embedSize = NSSize(width: 64, height: 64)

    func attach(ipc: IpcClient) {
        self.ipc = ipc
        guard eventPump == nil else { return }
        var continuation: AsyncStream<String>.Continuation?
        let stream = AsyncStream<String> { continuation = $0 }
        eventQueue = continuation
        eventPump = Task { [weak self] in
            for await body in stream {
                try? await self?.ipc?.requestVoid("webRtcEngineEvent", params: ["json": body], timeout: 10)
            }
        }
    }

    /// Reparent the engine into the active call window when present, otherwise the main window.
    func updateAudioHostWindow(main: NSWindow?, call: NSWindow?) {
        mainHostWindow = main
        callHostWindow = call
        reparentWebViewForAudioOutput()
    }

    func createHost(url: String, enableDevTools: Bool) async -> Bool {
        _ = await MicrophoneAccess.requestIfNeeded()
        destroy()
        loadSettling = false
        let config = WKWebViewConfiguration()
        config.mediaTypesRequiringUserActionForPlayback = []
        Self.setPrivateFlag(config.preferences, "hiddenPageDOMTimerThrottlingEnabled", false)
        Self.setPrivateFlag(config.preferences, "hiddenPageDOMTimerThrottlingAutoIncreases", false)
        Self.setPrivateFlag(config.preferences, "pageVisibilityBasedProcessSuppressionEnabled", false)
        Self.setPrivateFlag(config, "alwaysRunsAtForegroundPriority", true)
        beginAppNapExemption()
        let uc = config.userContentController
        uc.add(self, name: "callspire")
        let bridge = """
        (function () {
            if (typeof window.invokeCSharpAction === 'function') return;
            window.invokeCSharpAction = function (jsonStr) {
                try {
                    window.webkit.messageHandlers.callspire.postMessage(jsonStr);
                } catch (e) { /* swallow */ }
            };
            window.__callspirePlatform = 'mac';
            window.__callspireEnsureRemotePlayback = function () {
                var a = document.getElementById('remoteAudio');
                if (!a || !a.srcObject) return Promise.resolve(false);
                a.muted = false;
                a.volume = 1;
                return a.play().then(function () { return true; }).catch(function () { return false; });
            };
            try {
                localStorage.setItem('callspire.remotePlayback', 'audio');
            } catch (e) { /* swallow */ }
        })();
        """
        uc.addUserScript(WKUserScript(source: bridge, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        let wv = WKWebView(frame: NSRect(origin: .zero, size: Self.embedSize), configuration: config)
        wv.navigationDelegate = self
        wv.uiDelegate = self
        Self.setPrivateFlag(wv, "windowOcclusionDetectionEnabled", false)
        if #available(macOS 13.3, *), enableDevTools {
            wv.isInspectable = true
        }
        webView = wv
        mountInFallbackWindow(wv)
        reparentWebViewForAudioOutput()

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
            wv.removeFromSuperview()
            if let win = objc_getAssociatedObject(wv, &Self.ghostWindowAssociationKey) as? NSWindow {
                win.contentView = nil
                win.close()
            }
            objc_setAssociatedObject(wv, &Self.ghostWindowAssociationKey, nil, .OBJC_ASSOCIATION_RETAIN)
        }
        webView = nil
        finishLoad(false)
        if let activity = appNapActivity {
            ProcessInfo.processInfo.endActivity(activity)
            appNapActivity = nil
        }
    }

    private func mountInFallbackWindow(_ wv: WKWebView) {
        let win = NSWindow(
            contentRect: NSRect(x: -200, y: -200, width: Self.embedSize.width, height: Self.embedSize.height),
            styleMask: [.borderless],
            backing: .buffered,
            defer: true
        )
        win.isReleasedWhenClosed = false
        win.alphaValue = 1
        win.contentView = wv
        win.orderBack(nil)
        objc_setAssociatedObject(wv, &Self.ghostWindowAssociationKey, win, .OBJC_ASSOCIATION_RETAIN)
    }

    private func reparentWebViewForAudioOutput() {
        guard let wv = webView else { return }
        let target = callHostWindow ?? mainHostWindow
        guard let container = target?.contentView else { return }

        if let ghost = objc_getAssociatedObject(wv, &Self.ghostWindowAssociationKey) as? NSWindow {
            wv.removeFromSuperview()
            ghost.contentView = nil
            ghost.orderOut(nil)
            ghost.close()
            objc_setAssociatedObject(wv, &Self.ghostWindowAssociationKey, nil, .OBJC_ASSOCIATION_RETAIN)
        }

        if wv.superview !== container {
            wv.removeFromSuperview()
            container.addSubview(wv, positioned: .below, relativeTo: nil)
        }
        // Still inside the app window (for CoreAudio routing) but off the visible chrome.
        wv.frame = NSRect(x: -Self.embedSize.width - 8, y: -Self.embedSize.height - 8,
                          width: Self.embedSize.width, height: Self.embedSize.height)
        wv.isHidden = false
        wv.alphaValue = 1
        wv.setNeedsDisplay(wv.bounds)
    }

    private func ensureRemotePlaybackFromNative() {
        Task { @MainActor in
            _ = await invokeScript("""
            (function () {
                try {
                    if (typeof window.__callspireEnsureRemotePlayback === 'function') {
                        return window.__callspireEnsureRemotePlayback();
                    }
                } catch (e) {}
                return false;
            })()
            """)
        }
    }

    private func beginAppNapExemption() {
        guard appNapActivity == nil else { return }
        appNapActivity = ProcessInfo.processInfo.beginActivity(
            options: [.userInitiatedAllowingIdleSystemSleep, .latencyCritical],
            reason: "Callspire keeps the SIP/WebRTC registration alive"
        )
    }

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
        if body.contains("\"audio_playing\"") || body.contains("\"audio_connected\"") || body.contains("\"call_confirmed\"") {
            reparentWebViewForAudioOutput()
            ensureRemotePlaybackFromNative()
        }
        eventQueue?.yield(body)
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard loadContinuation != nil, !loadSettling else { return }
        loadSettling = true
        Task { @MainActor in
            let slots = await waitForEngineSlots(timeout: 12)
            reparentWebViewForAudioOutput()
            finishLoad(slots)
        }
    }

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

    func webView(
        _ webView: WKWebView,
        requestMediaCapturePermissionFor origin: WKSecurityOrigin,
        initiatedByFrame frame: WKFrameInfo,
        type: WKMediaCaptureType,
        decisionHandler: @escaping (WKPermissionDecision) -> Void
    ) {
        switch type {
        case .camera:
            decisionHandler(.deny)
        case .microphone, .cameraAndMicrophone:
            if MicrophoneAccess.isAuthorized {
                decisionHandler(.grant)
            } else {
                Task { @MainActor in
                    let ok = await MicrophoneAccess.requestIfNeeded()
                    decisionHandler(ok ? .grant : .deny)
                }
            }
        @unknown default:
            decisionHandler(.prompt)
        }
    }

    private func finishLoad(_ ok: Bool) {
        loadContinuation?.resume(returning: ok)
        loadContinuation = nil
    }
}

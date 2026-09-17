import Foundation
import WebKit
import AppKit

/// Hidden WKWebView that hosts WebRtcClient/index.html (served by the sidecar asset server).
/// Mirrors AvaloniaWebRtcEngineHost / WPF WebRtcEngineHost: the view must stay in the hierarchy
/// with a non-zero size so getUserMedia works.
@MainActor
final class WebRtcEngineHost: NSObject, WKScriptMessageHandler, WKNavigationDelegate, WKUIDelegate {
    private var webView: WKWebView?
    private weak var ipc: IpcClient?
    private var loadContinuation: CheckedContinuation<Bool, Never>?

    func attach(ipc: IpcClient) {
        self.ipc = ipc
    }

    func createHost(url: String, enableDevTools: Bool) async -> Bool {
        destroy()
        let config = WKWebViewConfiguration()
        config.mediaTypesRequiringUserActionForPlayback = []
        let uc = config.userContentController
        uc.add(self, name: "callspire")
        let wv = WKWebView(frame: NSRect(x: 0, y: 0, width: 8, height: 8), configuration: config)
        wv.navigationDelegate = self
        wv.uiDelegate = self
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
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        let body: String
        if let s = message.body as? String { body = s }
        else if let data = try? JSONSerialization.data(withJSONObject: message.body), let s = String(data: data, encoding: .utf8) { body = s }
        else { return }
        Task { try? await ipc?.requestVoid("webRtcEngineEvent", params: ["json": body]) }
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        finishLoad(true)
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

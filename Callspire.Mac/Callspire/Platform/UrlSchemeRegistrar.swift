import Foundation
import CoreServices
import AppKit

/// macOS equivalent of Windows ``ProtocolRegistrar`` — registers ``callspire://`` with Launch Services.
enum UrlSchemeRegistrar {
    static func registerCallspireScheme() {
        let bundleURL = Bundle.main.bundleURL as CFURL
        LSRegisterURL(bundleURL, true)

        guard let bundleId = Bundle.main.bundleIdentifier as CFString? else { return }
        let status = LSSetDefaultHandlerForURLScheme("callspire" as CFString, bundleId)
        if status != noErr {
            NSLog("[Callspire] LSSetDefaultHandlerForURLScheme failed: \(status)")
        }
    }

    /// Works even when `open callspire://…` returns -10814 (use full provision URL).
    static func terminalOpenCommand(for url: String) -> String {
        let bid = Bundle.main.bundleIdentifier ?? "com.callspire.softphone"
        let escaped = url.replacingOccurrences(of: "\"", with: "\\\"")
        return "open -b \(bid) \"\(escaped)\""
    }
}

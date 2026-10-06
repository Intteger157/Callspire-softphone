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

    /// Reliable on unsigned builds: passes the URL in argv (handled at launch) or use when -10814.
    static func terminalOpenCommand(for url: String) -> String {
        let app = "/Applications/Callspire.app"
        let quoted = url.replacingOccurrences(of: "'", with: "'\\''")
        return "open -a \(app) --args '\(quoted)'"
    }
}

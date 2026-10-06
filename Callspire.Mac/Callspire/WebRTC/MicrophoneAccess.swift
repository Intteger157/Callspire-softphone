import AVFoundation

/// macOS TCC for the Callspire bundle (shown as "Callspire" in System Settings).
/// WKWebView getUserMedia uses a separate site prompt for `127.0.0.1`; pre-authorize here
/// so the UI delegate can grant WebKit without repeating that dialog every launch.
enum MicrophoneAccess {
    static var isAuthorized: Bool {
        AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
    }

    @discardableResult
    static func requestIfNeeded() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            return true
        case .notDetermined:
            return await withCheckedContinuation { cont in
                AVCaptureDevice.requestAccess(for: .audio) { cont.resume(returning: $0) }
            }
        default:
            return false
        }
    }
}

import Foundation

/// Locates, launches and restarts Callspire.Service. The Swift app is the single-instance owner;
/// the sidecar is a child that exits when `--parent-pid` disappears.
final class SidecarProcess {
    private var process: Process?
    private var restarting = false
    var onCrash: (() -> Void)?

    static var defaultSocketPath: String {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        let dir = support.appendingPathComponent("Callspire", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let path = dir.appendingPathComponent("service.sock").path
        return path.utf8.count < 100 ? path : NSTemporaryDirectory() + "callspire-service.sock"
    }

    func start(socketPath: String) throws {
        stop()
        let exe = Self.locateServiceExecutable()
        let proc = Process()
        proc.executableURL = exe
        proc.arguments = ["--socket", socketPath, "--parent-pid", String(ProcessInfo.processInfo.processIdentifier)]
        proc.currentDirectoryURL = exe.deletingLastPathComponent()
        proc.terminationHandler = { [weak self] p in
            DispatchQueue.main.async {
                guard let self else { return }
                if p.terminationStatus != 0 { self.onCrash?() }
                self.scheduleRestart(socketPath: socketPath)
            }
        }
        try proc.run()
        process = proc
    }

    func stop() {
        restarting = false
        process?.terminationHandler = nil
        process?.terminate()
        process = nil
    }

    private func scheduleRestart(socketPath: String) {
        guard process?.isRunning != true else { return }
        guard !restarting else { return }
        restarting = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            self?.restarting = false
            try? self?.start(socketPath: socketPath)
        }
    }

    static func locateServiceExecutable() -> URL {
        let bundle = Bundle.main.bundleURL
        let candidates = [
            bundle.appendingPathComponent("Contents/MacOS/Service/Callspire.Service"),
            bundle.appendingPathComponent("Contents/Resources/Service/Callspire.Service"),
            URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
                .appendingPathComponent("Callspire.Service/bin/Debug/net8.0/Callspire.Service"),
        ]
        if let found = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0.path) }) {
            return found
        }
        // Last-resort: PATH lookup for developers running from Xcode without a published sidecar.
        return URL(fileURLWithPath: "/usr/local/bin/Callspire.Service")
    }
}

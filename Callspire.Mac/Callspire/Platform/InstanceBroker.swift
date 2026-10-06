import Foundation
import Darwin

/// Forwards ``callspire://`` / ``tel:`` URLs from a secondary app launch to the running instance.
/// Matches <c>CrossPlatformSingleInstance</c> socket paths in Callspire.Service.
enum InstanceBroker {
    private static var lockFd: Int32 = -1
    private static var listenFd: Int32 = -1
    private static var serverQueue: DispatchQueue?

    /// True when another Callspire is already running; this process only forwards URLs and exits.
    private(set) static var isSecondaryForwarder = false

    private static var supportDir: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("Callspire", isDirectory: true)
    }

    static var lockPath: String { supportDir.appendingPathComponent("instance.lock").path }

    static var socketPath: String {
        let sock = supportDir.appendingPathComponent("instance.sock").path
        return sock.utf8.count < 100 ? sock : (NSTemporaryDirectory() + "callspire-instance.sock")
    }

    /// Call once at process start. Primary returns; secondary waits for ``forwardOpenUrlsAndExit`` or argv URLs.
    static func claimPrimaryOrForwardAndExit() {
        try? FileManager.default.createDirectory(at: supportDir, withIntermediateDirectories: true)
        lockFd = open(lockPath, O_CREAT | O_RDWR, 0o600)
        if lockFd >= 0, flock(lockFd, LOCK_EX | LOCK_NB) == 0 {
            isSecondaryForwarder = false
            return
        }
        isSecondaryForwarder = true
        let argvUrls = protocolUrlsFromLaunch()
        if !argvUrls.isEmpty {
            for url in argvUrls { _ = forward(url) }
            exit(0)
        }
    }

    /// Browser / Launch Services URL delivery on a secondary instance.
    static func forwardOpenUrlsAndExit(_ urls: [URL]) {
        guard isSecondaryForwarder else { return }
        for url in urls { _ = forward(url.absoluteString) }
        exit(0)
    }

    static func protocolUrlsFromLaunch() -> [String] {
        CommandLine.arguments.dropFirst().filter { arg in
            let lower = arg.lowercased()
            return lower.hasPrefix("callspire:") || lower.hasPrefix("tel:")
        }
    }

    static func forward(_ message: String) -> Bool {
        let payload = (message.trimmingCharacters(in: .whitespacesAndNewlines) + "\n").data(using: .utf8)!
        let deadline = Date().addingTimeInterval(3)
        while Date() < deadline {
            var addr = sockaddr_un()
            addr.sun_family = sa_family_t(AF_UNIX)
            let path = socketPath
            let copied = path.withCString { cstr -> Bool in
                let maxLen = MemoryLayout.size(ofValue: addr.sun_path) - 1
                return strncpy(&addr.sun_path.0, cstr, maxLen) != nil
            }
            guard copied else { return false }

            let fd = socket(AF_UNIX, SOCK_STREAM, 0)
            guard fd >= 0 else { return false }
            defer { close(fd) }

            let connectOk = withUnsafePointer(to: &addr) { ptr -> Bool in
                ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                    connect(fd, sa, socklen_t(MemoryLayout<sockaddr_un>.size)) == 0
                }
            }
            if connectOk {
                _ = payload.withUnsafeBytes { write(fd, $0.baseAddress, $0.count) }
                shutdown(fd, SHUT_WR)
                return true
            }
            Thread.sleep(forTimeInterval: 0.15)
        }
        return false
    }

    static func startServer(onMessage: @escaping @MainActor (String) -> Void) {
        guard listenFd < 0 else { return }
        unlink(socketPath)
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        _ = socketPath.withCString { strncpy(&addr.sun_path.0, $0, MemoryLayout.size(ofValue: addr.sun_path) - 1) }

        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return }
        let bindOk = withUnsafePointer(to: &addr) { ptr -> Bool in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                bind(fd, sa, socklen_t(MemoryLayout<sockaddr_un>.size)) == 0
            }
        }
        guard bindOk, listen(fd, 4) == 0 else { close(fd); return }
        listenFd = fd

        let q = DispatchQueue(label: "com.callspire.instance-broker", qos: .userInitiated)
        serverQueue = q
        q.async {
            while listenFd >= 0 {
                var clientAddr = sockaddr_un()
                var len = socklen_t(MemoryLayout<sockaddr_un>.size)
                let client = withUnsafeMutablePointer(to: &clientAddr) { ptr -> Int32 in
                    ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                        accept(listenFd, sa, &len)
                    }
                }
                if client < 0 { break }
                var data = Data()
                var buf = [UInt8](repeating: 0, count: 4096)
                while true {
                    let n = read(client, &buf, buf.count)
                    if n <= 0 { break }
                    data.append(contentsOf: buf[0..<n])
                    if data.count > 16 * 1024 { break }
                }
                close(client)
                guard let text = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
                      !text.isEmpty else { continue }
                Task { @MainActor in onMessage(text) }
            }
        }
    }

    static func stopServer() {
        if listenFd >= 0 { close(listenFd); listenFd = -1 }
        try? FileManager.default.removeItem(atPath: socketPath)
    }
}

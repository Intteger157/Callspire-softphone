import Foundation
import Darwin

/// NDJSON client over a Unix domain socket. One connection to Callspire.Service.
final class IpcClient {
    private var fd: Int32 = -1
    private var readSource: DispatchSourceRead?
    private let queue = DispatchQueue(label: "com.callspire.ipc")
    private var buffer = Data()
    private var nextId: UInt64 = 1
    private var pending: [String: (Result<AnyJSON?, Error>) -> Void] = [:]
    private var requestHandlers: [String: (AnyJSON?, (Result<AnyJSON?, Error>) -> Void) -> Void] = [:]
    var onEvent: ((String, AnyJSON?) -> Void)?
    var onDisconnected: (() -> Void)?
    private(set) var isConnected = false

    func connect(path: String) throws {
        disconnect()
        let sock = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        guard sock >= 0 else { throw IpcError.disconnected }

        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let maxLen = MemoryLayout.size(ofValue: addr.sun_path) - 1
        let utf8 = Array(path.utf8)
        guard utf8.count < maxLen else { Darwin.close(sock); throw IpcError.decode("socket path too long") }
        withUnsafeMutablePointer(to: &addr.sun_path) { ptr in
            ptr.withMemoryRebound(to: UInt8.self, capacity: maxLen) { dst in
                for i in 0..<utf8.count { dst[i] = utf8[i] }
                dst[utf8.count] = 0
            }
        }
        let len = socklen_t(MemoryLayout<sockaddr_un>.size)
        let rc = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(sock, $0, len) }
        }
        guard rc == 0 else {
            Darwin.close(sock)
            throw IpcError.disconnected
        }
        fd = sock
        isConnected = true
        let source = DispatchSource.makeReadSource(fileDescriptor: sock, queue: queue)
        source.setEventHandler { [weak self] in self?.readAvailable() }
        source.setCancelHandler { Darwin.close(sock) }
        source.resume()
        readSource = source
    }

    func disconnect() {
        queue.sync {
            readSource?.cancel()
            readSource = nil
            if fd >= 0 { Darwin.close(fd); fd = -1 }
            isConnected = false
            let leftover = pending
            pending.removeAll()
            leftover.values.forEach { $0(.failure(IpcError.disconnected)) }
        }
    }

    func register(_ method: String, handler: @escaping (AnyJSON?, @escaping (Result<AnyJSON?, Error>) -> Void) -> Void) {
        queue.sync { requestHandlers[method] = handler }
    }

    @discardableResult
    func request<T: Decodable>(_ method: String, params: Encodable? = nil, as type: T.Type, timeout: TimeInterval = 30) async throws -> T {
        let json = try await requestRaw(method, params: params, timeout: timeout)
        guard let json else { throw IpcError.decode("empty result for \(method)") }
        return try json.decode(T.self)
    }

    func requestVoid(_ method: String, params: Encodable? = nil, timeout: TimeInterval = 30) async throws {
        _ = try await requestRaw(method, params: params, timeout: timeout)
    }

    func requestRaw(_ method: String, params: Encodable? = nil, timeout: TimeInterval = 30) async throws -> AnyJSON? {
        try await withCheckedThrowingContinuation { cont in
            queue.async {
                guard self.isConnected else {
                    cont.resume(throwing: IpcError.disconnected)
                    return
                }
                let id = String(self.nextId); self.nextId += 1
                self.pending[id] = { result in
                    switch result {
                    case .success(let v): cont.resume(returning: v)
                    case .failure(let e): cont.resume(throwing: e)
                    }
                }
                do {
                    var env = IpcEnvelope(type: IpcEnvelope.request, id: id, method: method)
                    if let params { env.params = try encodeJSON(AnyEncodable(params)) }
                    try self.write(env)
                } catch {
                    self.pending.removeValue(forKey: id)
                    cont.resume(throwing: error)
                    return
                }
                self.queue.asyncAfter(deadline: .now() + timeout) { [weak self] in
                    guard let self, let cb = self.pending.removeValue(forKey: id) else { return }
                    cb(.failure(IpcError.timeout(method)))
                }
            }
        }
    }

    func sendEvent(_ name: String, data: Encodable? = nil) {
        queue.async {
            do {
                var env = IpcEnvelope(type: IpcEnvelope.eventType, event: name)
                if let data { env.data = try encodeJSON(AnyEncodable(data)) }
                try self.write(env)
            } catch { }
        }
    }

    private func write(_ env: IpcEnvelope) throws {
        var data = try JSONEncoder.ipc.encode(env)
        data.append(0x0A)
        let rc = data.withUnsafeBytes { Darwin.write(self.fd, $0.baseAddress, $0.count) }
        if rc < 0 { throw IpcError.disconnected }
    }

    private func readAvailable() {
        var tmp = [UInt8](repeating: 0, count: 16 * 1024)
        let n = Darwin.read(fd, &tmp, tmp.count)
        if n <= 0 {
            isConnected = false
            DispatchQueue.main.async { self.onDisconnected?() }
            return
        }
        buffer.append(contentsOf: tmp.prefix(n))
        while let range = buffer.firstIndex(of: 0x0A) {
            let line = buffer.subdata(in: buffer.startIndex..<range)
            buffer.removeSubrange(buffer.startIndex...range)
            if line.isEmpty { continue }
            handleLine(line)
        }
    }

    private func handleLine(_ line: Data) {
        guard let env = try? JSONDecoder.ipc.decode(IpcEnvelope.self, from: line) else { return }
        switch env.type {
        case IpcEnvelope.response:
            guard let id = env.id, let cb = pending.removeValue(forKey: id) else { return }
            if let err = env.error {
                cb(.failure(IpcError.remote(err.message, err.code)))
            } else {
                cb(.success(env.result))
            }
        case IpcEnvelope.eventType:
            if let name = env.event {
                DispatchQueue.main.async { self.onEvent?(name, env.data) }
            }
        case IpcEnvelope.request:
            guard let id = env.id, let method = env.method else { return }
            let handler = requestHandlers[method]
            DispatchQueue.main.async {
                guard let handler else {
                    self.queue.async { try? self.write(IpcEnvelope(type: IpcEnvelope.response, id: id, error: IpcErrorBody(message: "unknown method \(method)"))) }
                    return
                }
                handler(env.params) { result in
                    self.queue.async {
                        switch result {
                        case .success(let value):
                            try? self.write(IpcEnvelope(type: IpcEnvelope.response, id: id, result: value))
                        case .failure(let err):
                            try? self.write(IpcEnvelope(type: IpcEnvelope.response, id: id, error: IpcErrorBody(message: err.localizedDescription)))
                        }
                    }
                }
            }
        default: break
        }
    }
}

/// Type-erased Encodable so `params:` can take any Codable payload.
struct AnyEncodable: Encodable {
    private let encodeFunc: (Encoder) throws -> Void
    init(_ value: Encodable) { encodeFunc = { try value.encode(to: $0) } }
    func encode(to encoder: Encoder) throws { try encodeFunc(encoder) }
}

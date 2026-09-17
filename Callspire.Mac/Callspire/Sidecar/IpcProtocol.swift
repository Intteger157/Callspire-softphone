import Foundation

/// Wire envelope matching Callspire.Service.Ipc.IpcMessage (NDJSON, camelCase).
struct IpcEnvelope: Codable {
    var type: String
    var id: String?
    var method: String?
    var params: AnyJSON?
    var result: AnyJSON?
    var error: IpcErrorBody?
    var event: String?
    var data: AnyJSON?

    static let request = "request"
    static let response = "response"
    static let eventType = "event"
}

struct IpcErrorBody: Codable {
    var message: String
    var code: String?
}

enum IpcError: LocalizedError {
    case disconnected
    case timeout(String)
    case remote(String, String?)
    case decode(String)

    var errorDescription: String? {
        switch self {
        case .disconnected: return "Not connected to Callspire.Service"
        case .timeout(let m): return "Timed out: \(m)"
        case .remote(let m, let c): return c.map { "[\($0)] \(m)" } ?? m
        case .decode(let m): return m
        }
    }
}

/// Untyped JSON value so request/response params can round-trip without per-method wrappers.
enum AnyJSON: Codable, Equatable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([AnyJSON])
    case object([String: AnyJSON])

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null; return }
        if let v = try? c.decode(Bool.self) { self = .bool(v); return }
        if let v = try? c.decode(Double.self) { self = .number(v); return }
        if let v = try? c.decode(String.self) { self = .string(v); return }
        if let v = try? c.decode([AnyJSON].self) { self = .array(v); return }
        if let v = try? c.decode([String: AnyJSON].self) { self = .object(v); return }
        throw DecodingError.dataCorruptedError(in: c, debugDescription: "Unsupported JSON")
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let v): try c.encode(v)
        case .number(let v): try c.encode(v)
        case .string(let v): try c.encode(v)
        case .array(let v): try c.encode(v)
        case .object(let v): try c.encode(v)
        }
    }

    func decode<T: Decodable>(_ type: T.Type) throws -> T {
        let data = try JSONEncoder.ipc.encode(self)
        return try JSONDecoder.ipc.decode(T.self, from: data)
    }
}

extension JSONEncoder {
    static let ipc: JSONEncoder = {
        let e = JSONEncoder()
        e.keyEncodingStrategy = .useDefaultKeys
        e.dateEncodingStrategy = .iso8601
        e.outputFormatting = [.sortedKeys]
        return e
    }()
}

extension JSONDecoder {
    static let ipc: JSONDecoder = {
        let d = JSONDecoder()
        d.keyDecodingStrategy = .useDefaultKeys
        d.dateDecodingStrategy = .iso8601
        return d
    }()
}

func encodeJSON<T: Encodable>(_ value: T) throws -> AnyJSON {
    let data = try JSONEncoder.ipc.encode(value)
    return try JSONDecoder.ipc.decode(AnyJSON.self, from: data)
}

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

/// ISO-8601 parsing tolerant to what System.Text.Json emits: optional fractional seconds of any length
/// (C# writes up to 7 digits) and an optional offset (missing offset = local time, matching the sidecar).
enum IpcDate {
    private static let withFraction: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private static let plain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()
    private static let localWithFraction: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = .current
        f.dateFormat = "yyyy-MM-dd'T'HH:mm:ss.SSS"
        return f
    }()
    private static let localPlain: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = .current
        f.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        return f
    }()

    static func parse(_ raw: String) -> Date? {
        var s = raw.trimmingCharacters(in: .whitespaces)
        if s.isEmpty { return nil }
        // Split off the zone designator (Z or ±hh:mm) if present.
        var zone = ""
        if s.hasSuffix("Z") { zone = "Z"; s.removeLast() }
        else if let tIdx = s.firstIndex(of: "T") {
            let timePart = s[s.index(after: tIdx)...]
            if let signIdx = timePart.lastIndex(where: { $0 == "+" || $0 == "-" }) {
                zone = String(s[signIdx...])
                s = String(s[..<signIdx])
            }
        }
        // Normalise fractional seconds to exactly 3 digits (Foundation accepts 1–3 reliably).
        if let dot = s.lastIndex(of: ".") {
            let frac = String(s[s.index(after: dot)...]).filter(\.isNumber)
            let f3 = String((frac + "000").prefix(3))
            s = String(s[..<dot]) + "." + f3
        }
        if zone.isEmpty {
            return localWithFraction.date(from: s) ?? localPlain.date(from: s)
        }
        return withFraction.date(from: s + zone) ?? plain.date(from: s + zone)
    }

    static func format(_ date: Date) -> String { withFraction.string(from: date) }
}

extension JSONEncoder {
    static let ipc: JSONEncoder = {
        let e = JSONEncoder()
        e.keyEncodingStrategy = .useDefaultKeys
        e.dateEncodingStrategy = .custom { date, encoder in
            var c = encoder.singleValueContainer()
            try c.encode(IpcDate.format(date))
        }
        e.outputFormatting = [.sortedKeys]
        return e
    }()
}

extension JSONDecoder {
    static let ipc: JSONDecoder = {
        let d = JSONDecoder()
        d.keyDecodingStrategy = .useDefaultKeys
        d.dateDecodingStrategy = .custom { decoder in
            let c = try decoder.singleValueContainer()
            let s = try c.decode(String.self)
            guard let date = IpcDate.parse(s) else {
                throw DecodingError.dataCorruptedError(in: c, debugDescription: "Unparseable date '\(s)'")
            }
            return date
        }
        return d
    }()
}

func encodeJSON<T: Encodable>(_ value: T) throws -> AnyJSON {
    let data = try JSONEncoder.ipc.encode(value)
    return try JSONDecoder.ipc.decode(AnyJSON.self, from: data)
}

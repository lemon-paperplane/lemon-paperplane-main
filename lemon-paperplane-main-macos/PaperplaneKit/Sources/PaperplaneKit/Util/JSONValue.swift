import Foundation

/// 通用 JSON 值，对应 Go 里的 any / map[string]any / json.RawMessage。
public enum JSONValue: Hashable, Sendable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])
}

extension JSONValue: Codable {
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() {
            self = .null
        } else if let b = try? c.decode(Bool.self) {
            self = .bool(b)
        } else if let n = try? c.decode(Double.self) {
            self = .number(n)
        } else if let s = try? c.decode(String.self) {
            self = .string(s)
        } else if let a = try? c.decode([JSONValue].self) {
            self = .array(a)
        } else if let o = try? c.decode([String: JSONValue].self) {
            self = .object(o)
        } else {
            throw DecodingError.dataCorruptedError(in: c, debugDescription: "不是合法的 JSON 值")
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let b): try c.encode(b)
        case .number(let n):
            // 整数按整数写，避免 1.0 之类的输出
            if n.rounded() == n, abs(n) < 9.007e15 { try c.encode(Int64(n)) } else { try c.encode(n) }
        case .string(let s): try c.encode(s)
        case .array(let a): try c.encode(a)
        case .object(let o): try c.encode(o)
        }
    }
}

extension JSONValue: ExpressibleByNilLiteral, ExpressibleByBooleanLiteral, ExpressibleByIntegerLiteral,
    ExpressibleByFloatLiteral, ExpressibleByStringLiteral, ExpressibleByArrayLiteral, ExpressibleByDictionaryLiteral
{
    public init(nilLiteral: ()) { self = .null }
    public init(booleanLiteral value: Bool) { self = .bool(value) }
    public init(integerLiteral value: Int) { self = .number(Double(value)) }
    public init(floatLiteral value: Double) { self = .number(value) }
    public init(stringLiteral value: String) { self = .string(value) }
    public init(arrayLiteral elements: JSONValue...) { self = .array(elements) }
    public init(dictionaryLiteral elements: (String, JSONValue)...) {
        var o: [String: JSONValue] = [:]
        for (k, v) in elements { o[k] = v }
        self = .object(o)
    }
}

public extension JSONValue {
    init(_ s: String) { self = .string(s) }
    init(_ s: String?) { self = s.map { .string($0) } ?? .null }
    init(_ b: Bool) { self = .bool(b) }
    init(_ n: Int) { self = .number(Double(n)) }
    init(_ n: Double) { self = .number(n) }
    init(_ a: [String]) { self = .array(a.map { .string($0) }) }
    init(_ m: [String: String]) { self = .object(m.mapValues { .string($0) }) }

    /// 把任意 Encodable 转成 JSONValue（用于把类型化参数平铺进调用输入）。
    init<T: Encodable>(encoding value: T) throws {
        let data = try JSONCoding.encoder.encode(value)
        self = try JSONCoding.decoder.decode(JSONValue.self, from: data)
    }

    /// 解码为类型化值。
    func decode<T: Decodable>(as type: T.Type = T.self) throws -> T {
        let data = try JSONCoding.encoder.encode(self)
        return try JSONCoding.decoder.decode(T.self, from: data)
    }

    subscript(key: String) -> JSONValue? {
        if case .object(let o) = self { return o[key] }
        return nil
    }

    subscript(index: Int) -> JSONValue? {
        if case .array(let a) = self, index >= 0, index < a.count { return a[index] }
        return nil
    }

    var isNull: Bool { if case .null = self { return true }; return false }

    var stringValue: String? {
        switch self {
        case .string(let s): return s
        case .number(let n): return n.rounded() == n ? String(Int64(n)) : String(n)
        case .bool(let b): return b ? "true" : "false"
        default: return nil
        }
    }

    var doubleValue: Double? {
        switch self {
        case .number(let n): return n
        case .string(let s): return Double(s)
        case .bool(let b): return b ? 1 : 0
        default: return nil
        }
    }

    var intValue: Int? { doubleValue.map { Int($0) } }

    var boolValue: Bool? {
        switch self {
        case .bool(let b): return b
        case .number(let n): return n != 0
        case .string(let s): return s == "true" || s == "1"
        default: return nil
        }
    }

    var arrayValue: [JSONValue]? { if case .array(let a) = self { return a }; return nil }
    var objectValue: [String: JSONValue]? { if case .object(let o) = self { return o }; return nil }

    /// 字符串数组（multiselect / list 字段）
    var stringArray: [String]? { arrayValue?.compactMap { $0.stringValue } }

    /// 字符串字典（keyvalue 字段）
    var stringMap: [String: String]? { objectValue?.compactMapValues { $0.stringValue } }

    /// 是否「空」：null、空串、空数组、空对象
    var isEmptyValue: Bool {
        switch self {
        case .null: return true
        case .string(let s): return s.isEmpty
        case .array(let a): return a.isEmpty
        case .object(let o): return o.isEmpty
        default: return false
        }
    }

    /// 紧凑 JSON 文本
    var jsonString: String {
        guard let d = try? JSONCoding.encoder.encode(self) else { return "null" }
        return String(decoding: d, as: UTF8.self)
    }
}

/// 构造调用输入：丢掉值为 nil 的键。
public func jsonObject(_ pairs: [String: JSONValue?]) -> JSONValue {
    var o: [String: JSONValue] = [:]
    for (k, v) in pairs { if let v { o[k] = v } }
    return .object(o)
}

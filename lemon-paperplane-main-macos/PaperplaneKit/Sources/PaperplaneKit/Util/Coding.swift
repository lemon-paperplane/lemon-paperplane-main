import Foundation

/// 与 daemon（Go encoding/json）对接的编解码器。
/// Go 的 time.Time 序列化为 RFC 3339（带纳秒、带时区），零值为 0001-01-01T00:00:00Z。
public enum JSONCoding {
    public static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .custom { decoder in
            let c = try decoder.singleValueContainer()
            let s = try c.decode(String.self)
            guard let date = GoTime.parse(s) else {
                throw DecodingError.dataCorruptedError(in: c, debugDescription: "无法解析时间：\(s)")
            }
            return date
        }
        return d
    }()

    public static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .custom { date, encoder in
            var c = encoder.singleValueContainer()
            try c.encode(GoTime.format(date))
        }
        e.outputFormatting = [.sortedKeys]
        return e
    }()
}

public enum GoTime {
    private static let plain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    private static let fractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    /// Go 零值时间（0001-01-01）视为「没有」。
    public static func isZero(_ d: Date?) -> Bool {
        guard let d else { return true }
        return d.timeIntervalSince1970 < -62_000_000_000
    }

    /// 解析 RFC 3339，小数秒最多保留 3 位（ISO8601DateFormatter 不认纳秒）。
    public static func parse(_ raw: String) -> Date? {
        var s = raw
        if let dot = s.firstIndex(of: ".") {
            var end = s.index(after: dot)
            while end < s.endIndex, s[end].isNumber { end = s.index(after: end) }
            let digits = s[s.index(after: dot)..<end]
            let ms = String(digits.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)
            s = String(s[..<dot]) + "." + ms + String(s[end...])
            return fractional.date(from: s)
        }
        return plain.date(from: s)
    }

    public static func format(_ d: Date) -> String { fractional.string(from: d) }
}

// MARK: - 容错默认值包装器
// Go 的 nil 切片 / nil map 序列化为 null，omitempty 字段可能缺失；这些包装器把 null/缺失解码为空值。

@propertyWrapper
public struct DefArray<Element: Codable & Hashable>: Codable, Hashable {
    public var wrappedValue: [Element]
    public init(wrappedValue: [Element] = []) { self.wrappedValue = wrappedValue }
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        wrappedValue = c.decodeNil() ? [] : try c.decode([Element].self)
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(wrappedValue)
    }
}

/// 能表示「空」的值：字典里某一项是 null 时用它（Go 的 map[string][]T 里 nil 切片输出为 null）。
public protocol EmptyDecodable { static var emptyValue: Self { get } }
extension Array: EmptyDecodable { public static var emptyValue: Array { [] } }
extension Dictionary: EmptyDecodable { public static var emptyValue: Dictionary { [:] } }

@propertyWrapper
public struct DefDict<Value: Codable & Hashable>: Codable, Hashable {
    public var wrappedValue: [String: Value]
    public init(wrappedValue: [String: Value] = [:]) { self.wrappedValue = wrappedValue }
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { wrappedValue = [:]; return }
        // 整个字典是 null 用空字典；某一项是 null 时，数组/字典类的值用空值，其他类型丢掉这一项
        let raw = try c.decode([String: Value?].self)
        var out: [String: Value] = [:]
        out.reserveCapacity(raw.count)
        for (k, v) in raw {
            if let v {
                out[k] = v
            } else if let empty = (Value.self as? EmptyDecodable.Type)?.emptyValue as? Value {
                out[k] = empty
            }
        }
        wrappedValue = out
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(wrappedValue)
    }
}

@propertyWrapper
public struct DefString: Codable, Hashable {
    public var wrappedValue: String
    public init(wrappedValue: String = "") { self.wrappedValue = wrappedValue }
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        wrappedValue = c.decodeNil() ? "" : try c.decode(String.self)
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(wrappedValue)
    }
}

@propertyWrapper
public struct DefBool: Codable, Hashable {
    public var wrappedValue: Bool
    public init(wrappedValue: Bool = false) { self.wrappedValue = wrappedValue }
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        wrappedValue = c.decodeNil() ? false : try c.decode(Bool.self)
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(wrappedValue)
    }
}

@propertyWrapper
public struct DefInt: Codable, Hashable {
    public var wrappedValue: Int
    public init(wrappedValue: Int = 0) { self.wrappedValue = wrappedValue }
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { wrappedValue = 0 }
        else if let i = try? c.decode(Int.self) { wrappedValue = i }
        else { wrappedValue = Int(try c.decode(Double.self)) }
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(wrappedValue)
    }
}

@propertyWrapper
public struct DefDouble: Codable, Hashable {
    public var wrappedValue: Double
    public init(wrappedValue: Double = 0) { self.wrappedValue = wrappedValue }
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        wrappedValue = c.decodeNil() ? 0 : try c.decode(Double.self)
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(wrappedValue)
    }
}

// 缺失的键也走默认值（合成的 init(from:) 对非 Optional 属性会调用 decode(_:forKey:)）。
public extension KeyedDecodingContainer {
    func decode<E>(_ type: DefArray<E>.Type, forKey key: Key) throws -> DefArray<E> {
        try decodeIfPresent(type, forKey: key) ?? DefArray()
    }
    func decode<V>(_ type: DefDict<V>.Type, forKey key: Key) throws -> DefDict<V> {
        try decodeIfPresent(type, forKey: key) ?? DefDict()
    }
    func decode(_ type: DefString.Type, forKey key: Key) throws -> DefString {
        try decodeIfPresent(type, forKey: key) ?? DefString()
    }
    func decode(_ type: DefBool.Type, forKey key: Key) throws -> DefBool {
        try decodeIfPresent(type, forKey: key) ?? DefBool()
    }
    func decode(_ type: DefInt.Type, forKey key: Key) throws -> DefInt {
        try decodeIfPresent(type, forKey: key) ?? DefInt()
    }
    func decode(_ type: DefDouble.Type, forKey key: Key) throws -> DefDouble {
        try decodeIfPresent(type, forKey: key) ?? DefDouble()
    }
}

/// 调用结果为 null 时可以直接给出的值（Go 的 nil 切片、无输出的操作）。
public protocol NullDecodable {
    static var nullValue: Self { get }
}

extension Array: NullDecodable { public static var nullValue: [Element] { [] } }
extension Dictionary: NullDecodable { public static var nullValue: [Key: Value] { [:] } }
extension JSONValue: NullDecodable { public static var nullValue: JSONValue { .null } }
extension Optional: NullDecodable { public static var nullValue: Wrapped? { nil } }

/// 没有输出的操作。
public struct EmptyResult: Codable, Hashable, NullDecodable {
    public init() {}
    public init(from decoder: Decoder) throws {}
    public func encode(to encoder: Encoder) throws {}
    public static var nullValue: EmptyResult { EmptyResult() }
}

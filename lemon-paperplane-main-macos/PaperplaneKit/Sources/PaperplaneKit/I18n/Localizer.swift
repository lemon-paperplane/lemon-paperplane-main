import Foundation

/// 界面文案（shared/i18n/<locale>.json，扁平 key → 文本，{0}{1}… 占位）。三端共用同一份文件。
/// 查找顺序：当前语言 → en → key 本身。
public final class Localizer: @unchecked Sendable {
    public static let supported = ["zh-Hans", "en"]

    public private(set) var locale: String
    private var table: [String: String] = [:]
    private var fallback: [String: String] = [:]
    private let directory: URL?
    private let lock = NSLock()

    public init(directory: URL?, locale: String) {
        self.directory = directory
        self.locale = locale
        reload()
    }

    /// 测试用：直接给表
    public init(locale: String, table: [String: String], fallback: [String: String] = [:]) {
        self.directory = nil
        self.locale = locale
        self.table = table
        self.fallback = fallback
    }

    public func setLocale(_ l: String) {
        lock.lock(); locale = l; lock.unlock()
        reload()
    }

    public func reload() {
        guard let directory else { return }
        let t = Self.load(directory.appendingPathComponent("\(locale).json"))
        let f = locale == "en" ? t : Self.load(directory.appendingPathComponent("en.json"))
        lock.lock(); table = t; fallback = f; lock.unlock()
    }

    public func t(_ key: String, _ args: [String] = []) -> String {
        lock.lock()
        let s = table[key] ?? fallback[key]
        lock.unlock()
        return Self.format(s ?? key, args)
    }

    public func has(_ key: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return table[key] != nil || fallback[key] != nil
    }

    public var keys: Set<String> {
        lock.lock(); defer { lock.unlock() }
        return Set(table.keys)
    }

    public static func format(_ s: String, _ args: [String]) -> String {
        guard !args.isEmpty, s.contains("{") else { return s }
        var out = s
        for (i, a) in args.enumerated() { out = out.replacingOccurrences(of: "{\(i)}", with: a) }
        return out
    }

    public static func load(_ url: URL) -> [String: String] {
        guard let d = try? Data(contentsOf: url),
              let m = try? JSONDecoder().decode([String: String].self, from: d) else { return [:] }
        return m
    }

    /// 设置值（system | zh-Hans | en）→ 实际语言。system 时按系统首选语言：中文（任意变体）用 zh-Hans，其余用 en。
    public static func resolve(setting: String?, preferredLanguages: [String] = Locale.preferredLanguages) -> String {
        if let s = setting, supported.contains(s) { return s }
        for lang in preferredLanguages {
            if lang.hasPrefix("zh") { return "zh-Hans" }
            if lang.hasPrefix("en") { return "en" }
        }
        return "en"
    }
}

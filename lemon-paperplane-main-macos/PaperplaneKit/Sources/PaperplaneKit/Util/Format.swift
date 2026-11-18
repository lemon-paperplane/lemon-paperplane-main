import Foundation

/// 图标引用（docs/api.md 第 4 节）：builtin:<name> 或 plugin:<pluginId>/<相对路径>。
public enum IconSource: Hashable, Sendable {
    case builtin(String)
    case plugin(id: String, path: String)

    /// 解析引用。没有前缀时：给了 pluginId 就当作该插件目录里的相对路径，否则当作内置图标名。
    public static func parse(_ raw: String?, pluginId: String? = nil) -> IconSource? {
        guard let raw = raw?.trimmingCharacters(in: .whitespaces), !raw.isEmpty else { return nil }
        if raw.hasPrefix("builtin:") { return .builtin(String(raw.dropFirst(8))) }
        if raw.hasPrefix("plugin:") {
            let rest = raw.dropFirst(7)
            guard let slash = rest.firstIndex(of: "/") else { return nil }
            return .plugin(id: String(rest[..<slash]), path: String(rest[rest.index(after: slash)...]))
        }
        if let pluginId, !pluginId.isEmpty { return .plugin(id: pluginId, path: raw) }
        let name = raw.hasSuffix(".svg") ? String(raw.dropLast(4)) : raw
        return .builtin(name)
    }

    public var key: String {
        switch self {
        case .builtin(let n): return "builtin:\(n)"
        case .plugin(let id, let p): return "plugin:\(id)/\(p)"
        }
    }
}

public enum Format {
    /// 把用户主目录缩写为 ~
    public static func abbreviatePath(_ path: String, home: String = NSHomeDirectory()) -> String {
        guard !home.isEmpty, path.hasPrefix(home) else { return path }
        let rest = path.dropFirst(home.count)
        if rest.isEmpty { return "~" }
        if rest.hasPrefix("/") { return "~" + rest }
        return path
    }

    /// 内存：MB → 「512 MB」「1.2 GB」
    public static func memory(mb: Double) -> String {
        if mb >= 1024 { return String(format: "%.1f GB", mb / 1024) }
        if mb >= 100 { return String(format: "%.0f MB", mb) }
        return String(format: "%.1f MB", mb)
    }

    /// CPU 百分比
    public static func cpu(_ v: Double) -> String {
        v >= 10 ? String(format: "%.0f%%", v) : String(format: "%.1f%%", v)
    }

    public static func bytes(_ n: Int64) -> String {
        let f = ByteCountFormatter()
        f.countStyle = .file
        return f.string(fromByteCount: n)
    }

    /// 持续时长：「3s」「2m 5s」「1h 4m」
    public static func duration(_ seconds: TimeInterval) -> String {
        let s = Int(max(0, seconds))
        if s < 60 { return "\(s)s" }
        if s < 3600 { return "\(s / 60)m \(s % 60)s" }
        return "\(s / 3600)h \((s % 3600) / 60)m"
    }

    /// 地址显示：去掉 http:// 前缀与末尾斜杠
    public static func shortURL(_ url: String) -> String {
        var s = url
        for p in ["http://", "https://"] where s.hasPrefix(p) { s = String(s.dropFirst(p.count)) }
        if s.hasSuffix("/") { s.removeLast() }
        return s
    }
}

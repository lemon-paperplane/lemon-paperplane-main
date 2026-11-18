import Foundation

// 文件操作（fs.*）与编辑器相关的数据结构，契约见 docs/editor.md。
// 路径一律是相对工程根、"/" 分隔；mtime 是 RFC3339Nano 字符串，原样回传给 expectedMtime。

/// fs.list 的一项
public struct FSEntry: Codable, Hashable, Identifiable {
    @DefString public var name: String
    public var path: String
    @DefBool public var dir: Bool
    @DefInt64 public var size: Int64
    public var mtime: String?
    @DefBool public var symlink: Bool
    @DefBool public var hidden: Bool
    /// node_modules / build / .venv 等：界面淡化显示，仍可展开
    @DefBool public var ignored: Bool

    public var id: String { path }

    public init(name: String, path: String, dir: Bool, size: Int64 = 0, hidden: Bool = false, ignored: Bool = false) {
        self.name = name; self.path = path; self.dir = dir; self.size = size; self.hidden = hidden; self.ignored = ignored
    }

    /// 扩展名（小写，不带点）
    public var ext: String {
        guard let dot = name.lastIndex(of: "."), dot != name.startIndex else { return "" }
        return name[name.index(after: dot)...].lowercased()
    }
}

/// fs.list 输出
public struct FSListResult: Codable, Hashable {
    @DefArray public var entries: [FSEntry]
    @DefBool public var truncated: Bool
}

/// fs.read 输出
public struct FSReadResult: Codable, Hashable {
    @DefString public var content: String
    public var encoding: String?
    @DefBool public var bom: Bool
    public var lineEnding: String? // lf | crlf | mixed
    @DefInt64 public var size: Int64
    public var mtime: String?
    @DefBool public var binary: Bool
    @DefBool public var tooLarge: Bool
    @DefBool public var readOnly: Bool
}

/// fs.write 输出
public struct FSWriteResult: Codable, Hashable {
    public var mtime: String?
    @DefInt64 public var size: Int64
}

/// fs.stat 的一项
public struct FSStat: Codable, Hashable {
    public var path: String
    @DefBool public var exists: Bool
    @DefBool public var dir: Bool
    @DefInt64 public var size: Int64
    public var mtime: String?
}

/// fs.delete 输出（移到废纸篓）
public struct FSDeleteResult: Codable, Hashable {
    @DefArray public var trashed: [String]
}

/// fs.search 的一项（按文件名模糊匹配）
public struct FSSearchHit: Codable, Hashable, Identifiable {
    public var path: String
    @DefString public var name: String
    @DefDouble public var score: Double
    public var id: String { path }

    /// 所在目录（相对工程根）
    public var directory: String {
        guard let slash = path.lastIndex(of: "/") else { return "" }
        return String(path[..<slash])
    }
}

/// fs.grep 输出
public struct FSGrepResult: Codable, Hashable {
    @DefArray public var matches: [FSGrepMatch]
    @DefBool public var truncated: Bool
}

public struct FSGrepMatch: Codable, Hashable {
    public var path: String
    @DefInt public var line: Int
    @DefInt public var column: Int
    @DefString public var text: String
}

// MARK: - 编辑器设置与插件贡献

/// settings.editor
public struct EditorSettings: Codable, Hashable {
    /// 空表示按平台默认（eclipse-macos / eclipse-windows）
    @DefString public var keymap: String
    /// 空表示跟随 App 明暗；否则为插件配色主题 id
    @DefString public var theme: String
    @DefInt public var fontSize: Int
    @DefString public var fontFamily: String
    @DefInt public var tabSize: Int
    @DefBool public var insertSpaces: Bool
    @DefBool public var wordWrap: Bool
    /// off | onFocusChange | afterDelay
    @DefString public var autoSave: String
    @DefBool public var trimTrailingWhitespace: Bool
    @DefBool public var insertFinalNewline: Bool
    /// 显示空格与制表符
    @DefBool public var showWhitespace: Bool

    public init() {
        fontSize = 13
        tabSize = 4
        insertSpaces = true
        autoSave = "off"
        insertFinalNewline = true
    }

    /// 本平台的默认快捷键方案
    public static let platformDefaultKeymap = "eclipse-macos"

    public var effectiveKeymap: String { keymap.isEmpty ? Self.platformDefaultKeymap : keymap }
}

/// 语言贡献（着色模块）
public struct LanguageInfo: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefArray public var extensions: [String]
    @DefArray public var filenames: [String]
    @DefArray public var filenamePatterns: [String]
    public var module: String?
    public var lineComment: String?
    @DefArray public var blockComment: [String]
    @DefInt public var priority: Int
    @DefString public var plugin: String

    public init(id: String, name: String, extensions: [String] = [], filenames: [String] = [],
                filenamePatterns: [String] = [], priority: Int = 0, plugin: String) {
        self.id = id; self.name = name; self.extensions = extensions; self.filenames = filenames
        self.filenamePatterns = filenamePatterns; self.priority = priority; self.plugin = plugin
    }
}

/// 快捷键方案贡献
public struct KeymapInfo: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    /// macos | windows | linux | any
    @DefString public var platform: String
    public var file: String?
    public var module: String?
    public var base: String?
    @DefString public var plugin: String
}

/// 编辑器配色主题贡献
public struct EditorThemeInfo: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    /// light | dark
    @DefString public var kind: String
    public var file: String?
    @DefString public var plugin: String
}

/// 按文件名找语言：精确文件名 > 扩展名 > 文件名模式（glob），同等匹配取 priority 高的。
public enum LanguageResolver {
    public static func language(for fileName: String, in languages: [LanguageInfo]) -> LanguageInfo? {
        let lower = fileName.lowercased()
        func best(_ list: [LanguageInfo]) -> LanguageInfo? { list.max { $0.priority < $1.priority } }
        if let l = best(languages.filter { $0.filenames.contains { $0.lowercased() == lower } }) { return l }
        // 扩展名：最长匹配优先（.d.ts 优先于 .ts）
        var extHits: [(Int, LanguageInfo)] = []
        for l in languages {
            for e in l.extensions {
                let ext = (e.hasPrefix(".") ? e : "." + e).lowercased()
                if lower.hasSuffix(ext) && lower.count > ext.count { extHits.append((ext.count, l)) }
            }
        }
        if let maxLen = extHits.map(\.0).max() {
            if let l = best(extHits.filter { $0.0 == maxLen }.map(\.1)) { return l }
        }
        return best(languages.filter { l in l.filenamePatterns.contains { glob($0, matches: lower) } })
    }

    /// 简单 glob：* 与 ?（不区分大小写）
    public static func glob(_ pattern: String, matches name: String) -> Bool {
        let p = Array(pattern.lowercased()), s = Array(name.lowercased())
        var pi = 0, si = 0, star = -1, mark = 0
        while si < s.count {
            if pi < p.count && (p[pi] == "?" || p[pi] == s[si]) { pi += 1; si += 1 }
            else if pi < p.count && p[pi] == "*" { star = pi; mark = si; pi += 1 }
            else if star >= 0 { pi = star + 1; mark += 1; si = mark }
            else { return false }
        }
        while pi < p.count && p[pi] == "*" { pi += 1 }
        return pi == p.count
    }
}

/// 文件类型图标（通用 SVG，shared/icons 里的名字）
public enum FileIcon {
    public static func builtinName(for entry: FSEntry, expanded: Bool = false) -> String {
        if entry.dir {
            if entry.name == ".git" { return "folder-git" }
            return expanded ? "folder-open" : "folder"
        }
        return builtinName(forFileName: entry.name)
    }

    public static func builtinName(forFileName name: String) -> String {
        let lower = name.lowercased()
        switch lower {
        case "dockerfile", "makefile", "procfile", "jenkinsfile", "vagrantfile": return "file-cog"
        case "license", "licence", "license.md", "license.txt", "notice": return "file-text"
        case ".gitignore", ".gitattributes", ".gitmodules", ".dockerignore", ".npmrc", ".nvmrc", ".editorconfig",
             ".env", ".env.local", ".python-version", ".tool-versions": return "file-cog"
        default: break
        }
        if lower.hasPrefix(".env") { return "file-lock" }
        let ext = lower.contains(".") ? String(lower[lower.index(after: lower.lastIndex(of: ".")!)...]) : ""
        switch ext {
        case "json", "jsonc", "json5": return "file-json"
        case "md", "markdown", "txt", "rst", "adoc", "log": return "file-text"
        case "png", "jpg", "jpeg", "gif", "webp", "svg", "ico", "bmp", "tiff", "heic", "avif": return "file-image"
        case "mp4", "mov", "webm", "avi", "mkv": return "file-video"
        case "mp3", "wav", "ogg", "flac", "aac", "m4a": return "file-music"
        case "zip", "tar", "gz", "tgz", "xz", "bz2", "7z", "rar", "jar", "war", "aar", "whl": return "file-archive"
        case "sh", "bash", "zsh", "fish", "bat", "cmd", "ps1": return "file-terminal"
        case "yml", "yaml", "toml", "ini", "cfg", "conf", "properties", "xml", "plist", "gradle", "lock": return "file-cog"
        case "csv", "tsv", "xls", "xlsx", "ods": return "file-spreadsheet"
        case "pem", "key", "crt", "cer", "p12", "jks", "keystore": return "file-key"
        case "ttf", "otf", "woff", "woff2": return "file-type"
        case "db", "sqlite", "sqlite3", "sql": return "database"
        case "pdf": return "file-text"
        case "diff", "patch": return "file-diff"
        case "": return "file"
        default: return "file-code"
        }
    }
}

/// 编辑器窗口地址（docs/editor.md 第 5 节）：/editor/index.html#token=…&projectId=…&file=…&line=…&locale=…&theme=…
public enum EditorURL {
    public static func make(base: URL, token: String, projectId: String, file: String?, line: Int?,
                            locale: String, theme: String) -> URL {
        var pairs: [(String, String)] = [("token", token), ("projectId", projectId)]
        if let file, !file.isEmpty { pairs.append(("file", file)) }
        if let line, line > 0 { pairs.append(("line", String(line))) }
        pairs.append(("locale", locale))
        pairs.append(("theme", theme))
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~/")
        let fragment = pairs.map { k, v in "\(k)=\(v.addingPercentEncoding(withAllowedCharacters: allowed) ?? v)" }
            .joined(separator: "&")
        var c = URLComponents(url: base.appendingPathComponent("editor/index.html"), resolvingAgainstBaseURL: false)!
        c.percentEncodedFragment = fragment
        return c.url!
    }
}

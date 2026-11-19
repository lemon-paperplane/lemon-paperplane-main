import Foundation

/// 服务器上的路径：一律按服务器的分隔符处理（Mac 连 Windows 服务器时是 C:\…），
/// 不用 URL(fileURLWithPath:)、NSString.appendingPathComponent 这些按本机规则的函数。
public struct ServerPath: Hashable, Sendable {
    /// 路径分隔符："/" 或 "\"
    public var sep: String

    public init(sep: String) {
        self.sep = sep.isEmpty ? "/" : sep
    }

    public static let posix = ServerPath(sep: "/")
    public static let windows = ServerPath(sep: "\\")

    public var isWindows: Bool { sep == "\\" }

    /// Windows 上 "/" 也是分隔符
    private func isSep(_ c: Character) -> Bool { String(c) == sep || (isWindows && c == "/") }

    /// 根目录："/"、"C:\"、"\\server\share\"
    public func isRoot(_ path: String) -> Bool {
        if !isWindows { return path == "/" }
        let p = path.replacingOccurrences(of: "/", with: "\\")
        if p.count == 3, p.hasSuffix(":\\") { return true }
        if p.count == 2, p.hasSuffix(":") { return true }
        return false
    }

    public func isAbsolute(_ path: String) -> Bool {
        if !isWindows { return path.hasPrefix("/") }
        let p = path.replacingOccurrences(of: "/", with: "\\")
        if p.hasPrefix("\\\\") { return true }
        let chars = Array(p)
        return chars.count >= 3 && chars[0].isLetter && chars[1] == ":" && chars[2] == "\\"
    }

    /// 去掉末尾多余的分隔符（根目录保留）
    public func normalize(_ path: String) -> String {
        var p = path.trimmingCharacters(in: .whitespaces)
        if isWindows { p = p.replacingOccurrences(of: "/", with: "\\") }
        while p.count > 1, let last = p.last, isSep(last), !isRoot(p) { p.removeLast() }
        if isWindows, p.count == 2, p.hasSuffix(":") { p += "\\" }
        return p
    }

    /// 拼接：base + 分隔符 + name（name 可以含多级，Windows 下 "/" 换成 "\"）
    public func join(_ base: String, _ name: String) -> String {
        var n = name
        if isWindows { n = n.replacingOccurrences(of: "/", with: "\\") }
        while let f = n.first, isSep(f) { n.removeFirst() }
        if base.isEmpty { return n }
        if n.isEmpty { return base }
        if let last = base.last, isSep(last) { return base + n }
        return base + sep + n
    }

    /// 最后一段（文件夹名）；根目录返回自身
    public func lastComponent(_ path: String) -> String {
        let p = normalize(path)
        if isRoot(p) { return p }
        guard let idx = p.lastIndex(where: { isSep($0) }) else { return p }
        return String(p[p.index(after: idx)...])
    }

    /// 上一级；已在根目录返回 nil
    public func parent(_ path: String) -> String? {
        let p = normalize(path)
        if isRoot(p) { return nil }
        guard let idx = p.lastIndex(where: { isSep($0) }) else { return nil }
        let head = String(p[..<idx])
        if head.isEmpty { return sep } // "/foo" → "/"
        if isWindows, head.count == 2, head.hasSuffix(":") { return head + "\\" } // "C:\foo" → "C:\"
        return head
    }

    /// 两个路径是否相同（Windows 不分大小写）
    public func same(_ a: String, _ b: String) -> Bool {
        let x = normalize(a), y = normalize(b)
        return isWindows ? x.lowercased() == y.lowercased() : x == y
    }

    /// path 在 root 之内时返回相对路径（用 "/" 连接，和内核 fs.* 的相对路径一致；相同返回 ""），否则 nil
    public func relative(_ path: String, to root: String) -> String? {
        let p = normalize(path), r = normalize(root)
        if same(p, r) { return "" }
        let prefix = r.hasSuffix(sep) ? r : r + sep
        let match = isWindows ? p.lowercased().hasPrefix(prefix.lowercased()) : p.hasPrefix(prefix)
        guard match else { return nil }
        let rest = String(p.dropFirst(prefix.count))
        return isWindows ? rest.replacingOccurrences(of: "\\", with: "/") : rest
    }

    /// 工程内相对路径（内核用 "/"）→ 绝对路径
    public func absolute(_ rel: String, root: String) -> String {
        rel.isEmpty ? root : join(root, rel)
    }

    /// 主目录缩写为 ~
    public func abbreviate(_ path: String, home: String) -> String {
        guard !home.isEmpty else { return path }
        let h = normalize(home)
        let match = isWindows ? path.lowercased().hasPrefix(h.lowercased()) : path.hasPrefix(h)
        guard match else { return path }
        let rest = path.dropFirst(h.count)
        if rest.isEmpty { return "~" }
        if let f = rest.first, isSep(f) { return "~" + rest }
        return path
    }
}

/// 服务器地址与运行地址的处理
public enum ServerURL {
    /// 拆出 scheme、主机（IPv6 带方括号）、端口、其余部分；解析不了返回 nil
    public struct Parts: Hashable {
        public var scheme: String
        public var userinfo: String
        public var host: String
        public var port: Int?
        public var rest: String

        public var string: String {
            scheme + "://" + userinfo + host + (port.map { ":\($0)" } ?? "") + rest
        }

        /// 端口（没写时按 scheme 的默认值）
        public var effectivePort: Int? {
            if let port { return port }
            switch scheme.lowercased() {
            case "http", "ws": return 80
            case "https", "wss": return 443
            default: return nil
            }
        }
    }

    public static func parse(_ s: String) -> Parts? {
        guard let schemeEnd = s.range(of: "://") else { return nil }
        let scheme = String(s[..<schemeEnd.lowerBound])
        guard !scheme.isEmpty, scheme.allSatisfy({ $0.isLetter || $0.isNumber || $0 == "+" || $0 == "-" || $0 == "." }) else { return nil }
        let afterScheme = s[schemeEnd.upperBound...]
        let authEnd = afterScheme.firstIndex(where: { $0 == "/" || $0 == "?" || $0 == "#" }) ?? afterScheme.endIndex
        var authority = String(afterScheme[..<authEnd])
        let rest = String(afterScheme[authEnd...])
        var userinfo = ""
        if let at = authority.lastIndex(of: "@") {
            userinfo = String(authority[...at])
            authority = String(authority[authority.index(after: at)...])
        }
        var host = authority
        var port: Int?
        if authority.hasPrefix("[") {
            guard let close = authority.firstIndex(of: "]") else { return nil }
            host = String(authority[...close])
            let after = authority[authority.index(after: close)...]
            if after.hasPrefix(":") { port = Int(after.dropFirst()) }
        } else if let colon = authority.lastIndex(of: ":") {
            host = String(authority[..<colon])
            port = Int(authority[authority.index(after: colon)...])
        }
        guard !host.isEmpty else { return nil }
        return Parts(scheme: scheme, userinfo: userinfo, host: host, port: port, rest: rest)
    }

    /// 远程监听默认端口
    public static let defaultPort = 47290

    /// 和内核 remote.NormalizeAddress 一样补全用户填的地址：只写主机名或 IP 时用 https 和 47290；
    /// 写了 https:// 没写端口时用 443；http:// 不支持（返回 nil）
    public static func normalizeAddress(_ raw: String) -> String? {
        var a = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !a.isEmpty, !a.lowercased().hasPrefix("http://") else { return nil }
        let scheme = a.lowercased().hasPrefix("https://")
        if scheme { a = String(a.dropFirst(8)) }
        if let slash = a.firstIndex(of: "/") { a = String(a[..<slash]) }
        var host = a
        var port: Int?
        if a.hasPrefix("[") {
            guard let close = a.firstIndex(of: "]") else { return nil }
            host = String(a[a.index(after: a.startIndex)..<close])
            let after = a[a.index(after: close)...]
            if after.hasPrefix(":") {
                guard let p = Int(after.dropFirst()) else { return nil }
                port = p
            }
        } else if a.filter({ $0 == ":" }).count == 1, let colon = a.firstIndex(of: ":") {
            host = String(a[..<colon])
            guard let p = Int(a[a.index(after: colon)...]) else { return nil }
            port = p
        }
        guard !host.isEmpty else { return nil }
        let p = port ?? (scheme ? 443 : defaultPort)
        guard (1...65535).contains(p) else { return nil }
        let h = host.contains(":") ? "[" + host + "]" : host
        return "https://\(h):\(p)"
    }

    /// 地址里的主机名（IPv6 带方括号）
    public static func host(of url: String) -> String {
        if let p = parse(url) { return p.host }
        // 只有 host[:port] 的写法
        if let p = parse("https://" + url) { return p.host }
        return url
    }

    /// 比较用：小写 scheme/主机、去掉末尾斜杠
    public static func normalize(_ url: String) -> String {
        guard var p = parse(url.trimmingCharacters(in: .whitespaces)) else { return url.lowercased() }
        p.scheme = p.scheme.lowercased()
        p.host = p.host.lowercased()
        while p.rest.hasSuffix("/") { p.rest.removeLast() }
        return p.string
    }

    /// 指向本机回环（或全部网卡）的主机名
    public static func isLoopbackHost(_ host: String) -> Bool {
        switch host.lowercased() {
        case "localhost", "127.0.0.1", "0.0.0.0", "[::1]", "[::]", "::1", "::", "[0:0:0:0:0:0:0:1]": return true
        default: return host.hasPrefix("127.") || host.lowercased().hasSuffix(".localhost")
        }
    }

    /// 运行地址在服务器上的端口（本机回环地址才算，别的主机名返回 nil）
    public static func loopbackPort(_ runURL: String) -> Int? {
        guard let p = parse(runURL), isLoopbackHost(p.host) else { return nil }
        return p.effectivePort
    }

    /// 把运行地址里的 localhost / 127.0.0.1 / 0.0.0.0 换成服务器主机名（其他主机名原样返回）
    public static func direct(_ runURL: String, serverHost: String) -> String {
        guard var p = parse(runURL), isLoopbackHost(p.host), !serverHost.isEmpty else { return runURL }
        var h = serverHost
        if h.contains(":") && !h.hasPrefix("[") { h = "[" + h + "]" }
        p.host = h
        return p.string
    }

    /// 运行地址经端口转发后在本机的地址：换成 localhost:本机端口，保留路径
    public static func forwarded(_ runURL: String, localPort: Int) -> String {
        guard var p = parse(runURL) else { return "http://localhost:\(localPort)" }
        p.host = "localhost"
        p.port = localPort
        return p.string
    }

    /// 显示用：去掉 scheme 与末尾斜杠
    public static func display(_ url: String) -> String { Format.shortURL(url) }
}

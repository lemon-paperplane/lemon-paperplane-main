import Foundation

// 远程服务器（docs/remote.md）：服务器端 remote.*、客户端网关 gateway.*、fs.browse 的输入输出。

/// 连接远程服务器时运行地址的打开方式：转发到本机（forward）和/或用服务器地址直接打开（direct）。
public struct RemoteLinks: Codable, Hashable {
    public var forward: Bool
    public var direct: Bool

    public init(forward: Bool = true, direct: Bool = true) {
        self.forward = forward
        self.direct = direct
    }

    /// 缺省：两种都要
    public static let both = RemoteLinks()

    private enum CodingKeys: String, CodingKey { case forward, direct }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        forward = try c.decodeIfPresent(Bool.self, forKey: .forward) ?? true
        direct = try c.decodeIfPresent(Bool.self, forKey: .direct) ?? true
    }
}

public extension ProjectSummary {
    /// 某个运行配置的远程打开方式（没有记录 = 两种都要；两种都关的脏数据也按两种都要）
    func remoteLinks(for configId: String) -> RemoteLinks {
        guard let l = remoteLinks[configId], l.forward || l.direct else { return .both }
        return l
    }
}

public extension Project {
    func remoteLinks(for configId: String) -> RemoteLinks {
        guard let l = remoteLinks[configId], l.forward || l.direct else { return .both }
        return l
    }
}

// MARK: - 服务器端（remote.*）

/// remote.status / remote.configure
public struct RemoteStatus: Codable, Hashable {
    @DefBool public var enabled: Bool
    @DefBool public var running: Bool
    @DefString public var error: String
    @DefString public var listen: String
    @DefInt public var port: Int
    @DefArray public var addresses: [String]
    @DefString public var fingerprint: String
    @DefString public var fingerprintDisplay: String
    @DefBool public var customCert: Bool
    @DefBool public var passwordLogin: Bool
    @DefBool public var autostart: Bool
    @DefBool public var autostartSupported: Bool
    @DefArray public var allowCIDRs: [String]
    @DefInt public var keys: Int
    @DefInt public var users: Int
    @DefString public var hostname: String

    public init() {}
}

/// 访问密钥（kind = key：手动生成；device：账号密码登录时发的）
public struct RemoteKey: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefString public var kind: String
    @DefString public var user: String
    public var createdAt: Date?
    public var lastUsedAt: Date?
    @DefString public var lastIP: String
    /// 是不是当前请求用的这条（经网关管理远程服务器时 = 本设备）
    @DefBool public var current: Bool

    public var isDevice: Bool { kind == "device" }
}

/// remote.keyCreate：token、code 只返回这一次
public struct RemoteKeyCreated: Codable, Hashable {
    public var key: RemoteKey
    @DefString public var token: String
    @DefString public var code: String
}

public struct RemoteUser: Codable, Hashable, Identifiable {
    public var name: String
    public var createdAt: Date?
    public var updatedAt: Date?
    public var id: String { name }
}

/// remote.audit 的一条
public struct RemoteAuditEntry: Codable, Hashable {
    public var time: Date?
    @DefString public var event: String
    @DefString public var ip: String
    @DefString public var who: String
    @DefString public var detail: String
}

// MARK: - 客户端网关（gateway.*）

/// gateway.decode
public struct GatewayCode: Codable, Hashable {
    @DefArray public var urls: [String]
    @DefString public var fingerprint: String
    @DefString public var token: String
    @DefString public var name: String
}

/// 服务器 GET /api/v1/auth/hello
public struct ServerHello: Codable, Hashable {
    @DefString public var name: String
    @DefString public var hostname: String
    @DefString public var version: String
    @DefInt public var apiVersion: Int
    @DefString public var os: String
    @DefString public var arch: String
    @DefBool public var passwordLogin: Bool
}

/// gateway.probe
public struct GatewayProbe: Codable, Hashable {
    @DefString public var url: String
    @DefString public var fingerprint: String
    @DefString public var fingerprintDisplay: String
    @DefBool public var trusted: Bool
    public var hello: ServerHello?
}

/// gateway.open：port / token 用法和本机 runtime.json 一样；info 是服务器的 system.info
public struct GatewayOpen: Codable, Hashable {
    public var id: String
    @DefInt public var port: Int
    @DefString public var token: String
    public var info: SystemInfo?

    /// 经网关访问服务器的地址（回环端口）
    public var baseURL: URL { URL(string: "http://127.0.0.1:\(port)")! }
}

/// gateway.forward
public struct GatewayForward: Codable, Hashable {
    @DefInt public var localPort: Int
    @DefString public var url: String
}

// MARK: - fs.browse

public struct FSBrowseEntry: Codable, Hashable, Identifiable {
    @DefString public var name: String
    @DefString public var path: String
    @DefBool public var dir: Bool
    @DefBool public var hidden: Bool
    public var size: Int64?
    public var modTime: Date?
    public var id: String { path }
}

public struct FSBrowseResult: Codable, Hashable {
    @DefString public var path: String
    /// 已在根目录时为空
    @DefString public var parent: String
    @DefString public var home: String
    @DefString public var sep: String
    /// Windows 盘符；其他系统为 ["/"]
    @DefArray public var roots: [String]
    @DefArray public var entries: [FSBrowseEntry]
}

// MARK: - 客户端保存的服务器

/// 界面偏好里保存的一台服务器（访问密钥另存钥匙串）
public struct SavedServer: Codable, Hashable, Identifiable {
    public var id: String
    public var name: String
    public var url: String
    @DefString public var fingerprint: String
    @DefBool public var trusted: Bool
    @DefString public var os: String
    @DefString public var hostname: String

    public init(id: String = UUID().uuidString.lowercased(), name: String, url: String, fingerprint: String,
                trusted: Bool, os: String = "", hostname: String = "") {
        self.id = id
        self.name = name
        self.url = url
        self.fingerprint = fingerprint
        self.trusted = trusted
        self.os = os
        self.hostname = hostname
    }

    /// 地址里的主机名（运行地址换成服务器地址时用；IPv6 带方括号）
    public var host: String { ServerURL.host(of: url) }

    /// 地址显示：去掉 https:// 与默认端口以外不动
    public var displayAddress: String {
        var s = url
        if s.hasPrefix("https://") { s = String(s.dropFirst(8)) }
        if s.hasSuffix("/") { s.removeLast() }
        return s
    }

    /// 指纹前 8 位（去掉 sha256: 前缀）
    public var shortFingerprint: String { Self.shortFingerprint(fingerprint) }

    public static func shortFingerprint(_ fp: String) -> String {
        var s = fp
        if let colon = s.firstIndex(of: ":") { s = String(s[s.index(after: colon)...]) }
        return String(s.prefix(8)).uppercased()
    }

    /// 同一台服务器：地址相同且指纹相同（CA 证书的不比指纹）
    public func isSameServer(url other: String, fingerprint fp: String) -> Bool {
        ServerURL.normalize(url) == ServerURL.normalize(other) && (trusted || fp.isEmpty || fingerprint.lowercased() == fp.lowercased())
    }
}

/// 服务器列表的编解码（UserDefaults 里存 JSON）
public enum SavedServerList {
    public static func decode(_ data: Data?) -> [SavedServer] {
        guard let data, let list = try? JSONDecoder().decode([SavedServer].self, from: data) else { return [] }
        return list
    }

    public static func encode(_ list: [SavedServer]) -> Data {
        let e = JSONEncoder()
        e.outputFormatting = [.sortedKeys]
        return (try? e.encode(list)) ?? Data("[]".utf8)
    }
}

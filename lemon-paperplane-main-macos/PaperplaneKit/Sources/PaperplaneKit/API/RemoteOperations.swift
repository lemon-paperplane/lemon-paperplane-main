import Foundation

// docs/remote.md 第 4.2 节：服务器端 remote.*（在哪个客户端上调就管哪台服务器）、
// 客户端网关 gateway.*（只能在本机内核上调）、fs.browse、runconfig.setRemoteLinks。

public extension APIError {
    // gateway.open / probe / login 与经网关调用时的错误码
    static let codeUnreachable = "unreachable"
    static let codeFingerprintMismatch = "fingerprint_mismatch"
    static let codeUnauthorized = "unauthorized"
    static let codeRemoteUnauthorized = "remote_unauthorized"
    static let codeRateLimited = "rate_limited"
    static let codeIncompatible = "incompatible"
    static let codePasswordDisabled = "password_disabled"

    /// 经网关调用时服务器那一侧出了问题（不是本机内核的问题，不能拿它去重启本机内核）
    var isRemoteFailure: Bool {
        switch code {
        case Self.codeUnreachable, Self.codeFingerprintMismatch, Self.codeRemoteUnauthorized, Self.codeRateLimited, Self.codeIncompatible:
            return true
        default:
            return false
        }
    }

    /// data 里的某个字符串字段（fingerprint_mismatch 的 data.fingerprint 等）
    func dataString(_ key: String) -> String? {
        if case .object(let o)? = data, case .string(let s)? = o[key] { return s }
        return nil
    }
}

public extension APIClient {
    /// 网关相关操作要连服务器（TLS 握手、登录哈希），给足时间
    static let gatewayTimeout: TimeInterval = 40

    // MARK: 服务器端

    func remoteStatus() async throws -> RemoteStatus { try await call("remote.status") }

    /// 只传要改的字段
    func remoteConfigure(enabled: Bool? = nil, port: Int? = nil, passwordLogin: Bool? = nil,
                         autostart: Bool? = nil, allowCIDRs: [String]? = nil) async throws -> RemoteStatus {
        try await call("remote.configure", jsonObject([
            "enabled": enabled.map(JSONValue.bool), "port": port.map { .number(Double($0)) },
            "passwordLogin": passwordLogin.map(JSONValue.bool), "autostart": autostart.map(JSONValue.bool),
            "allowCIDRs": allowCIDRs.map { JSONValue($0) },
        ]), timeout: 30)
    }

    func remoteKeys() async throws -> [RemoteKey] { try await call("remote.keys") }

    func remoteKeyCreate(name: String, address: String? = nil) async throws -> RemoteKeyCreated {
        try await call("remote.keyCreate", jsonObject([
            "name": .string(name), "address": address.flatMap { $0.isEmpty ? nil : JSONValue.string($0) },
        ]))
    }

    func remoteKeyRevoke(_ id: String) async throws {
        try await send("remote.keyRevoke", ["id": .string(id)])
    }

    func remoteUsers() async throws -> [RemoteUser] { try await call("remote.users") }

    /// 新建账号或改密码（至少 10 位）
    func remoteUserSet(name: String, password: String) async throws {
        try await send("remote.userSet", ["name": .string(name), "password": .string(password)], timeout: 30)
    }

    func remoteUserRemove(_ name: String) async throws {
        try await send("remote.userRemove", ["name": .string(name)])
    }

    func remoteAudit(limit: Int = 50) async throws -> [RemoteAuditEntry] {
        try await call("remote.audit", ["limit": .number(Double(limit))])
    }

    // MARK: 客户端网关（只能在本机内核上调）

    func gatewayDecode(code: String) async throws -> GatewayCode {
        try await call("gateway.decode", ["code": .string(code.trimmingCharacters(in: .whitespacesAndNewlines))])
    }

    func gatewayProbe(address: String) async throws -> GatewayProbe {
        try await call("gateway.probe", ["address": .string(address)], timeout: Self.gatewayTimeout)
    }

    /// 账号密码换设备专用的访问密钥（密码不保存）
    func gatewayLogin(url: String, fingerprint: String, trusted: Bool, user: String, password: String, device: String) async throws -> String {
        let r: JSONValue = try await call("gateway.login", [
            "url": .string(url), "fingerprint": .string(fingerprint), "trusted": .bool(trusted),
            "user": .string(user), "password": .string(password), "device": .string(device),
        ], timeout: Self.gatewayTimeout)
        guard let t = r["token"]?.stringValue, !t.isEmpty else {
            throw APIError(code: APIError.codeBadResponse, message: "empty token")
        }
        return t
    }

    func gatewayOpen(url: String, fingerprint: String, trusted: Bool, token: String) async throws -> GatewayOpen {
        try await call("gateway.open", [
            "url": .string(url), "fingerprint": .string(fingerprint), "trusted": .bool(trusted), "token": .string(token),
        ], timeout: Self.gatewayTimeout)
    }

    func gatewayClose(_ id: String) async throws {
        try await send("gateway.close", ["id": .string(id)])
    }

    func gatewayForward(id: String, port: Int) async throws -> GatewayForward {
        try await call("gateway.forward", ["id": .string(id), "port": .number(Double(port))])
    }

    func gatewayUnforward(id: String, port: Int) async throws {
        try await send("gateway.unforward", ["id": .string(id), "port": .number(Double(port))])
    }

    // MARK: 其他

    /// 在服务器上新建文件夹（绝对路径，上一级必须存在），返回新路径
    @discardableResult
    func fsMkdir(path: String) async throws -> String {
        let r: JSONValue = try await call("fs.mkdir", ["path": .string(path)])
        return r["path"]?.stringValue ?? path
    }

    /// 列出服务器上任意文件夹（path 为空 = 主目录）
    func fsBrowse(path: String?) async throws -> FSBrowseResult {
        try await call("fs.browse", jsonObject(["path": path.flatMap { $0.isEmpty ? nil : JSONValue.string($0) }]), timeout: 30)
    }

    /// 至少一个为 true；两个都 true 等于默认
    func setRemoteLinks(projectId: String, configId: String, forward: Bool, direct: Bool) async throws {
        try await send("runconfig.setRemoteLinks", [
            "projectId": .string(projectId), "configId": .string(configId), "forward": .bool(forward), "direct": .bool(direct),
        ])
    }

    /// 内核返回的 webview 地址带的是它自己的回环地址与令牌；经网关访问远程服务器时
    /// 换成本客户端的地址和令牌（网关会去掉 token 参数、换成访问密钥转发）。本机时结果不变。
    func rebasedWebviewURL(_ raw: URL) -> URL {
        guard var c = URLComponents(url: raw, resolvingAgainstBaseURL: false),
              let base = URLComponents(url: baseURL, resolvingAgainstBaseURL: false) else { return raw }
        c.scheme = base.scheme
        c.host = base.host
        c.port = base.port
        var items = (c.queryItems ?? []).filter { $0.name != "token" }
        items.insert(URLQueryItem(name: "token", value: token), at: 0)
        c.queryItems = items
        return c.url ?? raw
    }
}

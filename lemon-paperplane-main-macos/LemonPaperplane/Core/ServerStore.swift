import Foundation
import PaperplaneKit

/// 连不上远程服务器的原因（docs/remote.md 4.2 的错误码）
struct ServerFailure: Equatable {
    enum Kind: Equatable {
        case unreachable // 连不上 / 服务器不是柠檬纸飞机
        case fingerprint // 证书指纹和保存的不一样
        case unauthorized // 访问密钥失效（被吊销）或没有保存密钥
        case rateLimited
        case incompatible
        case localDown // 本机后台服务没在运行（网关在本机内核里）
        case other
    }

    var kind: Kind
    var message: String = ""
    /// incompatible 时的两边版本
    var versions: (server: String, client: String)?

    static func == (a: ServerFailure, b: ServerFailure) -> Bool { a.kind == b.kind && a.message == b.message }

    init(kind: Kind, message: String = "") {
        self.kind = kind
        self.message = message
    }

    init(_ error: Error) {
        guard let e = error as? APIError else {
            self.init(kind: .other, message: error.localizedDescription)
            return
        }
        switch e.code {
        case APIError.codeUnreachable, APIError.codeNetwork:
            self.init(kind: .unreachable, message: e.message)
        case APIError.codeFingerprintMismatch:
            self.init(kind: .fingerprint, message: e.message)
        case APIError.codeUnauthorized, APIError.codeRemoteUnauthorized:
            self.init(kind: .unauthorized, message: e.message)
        case APIError.codeRateLimited:
            self.init(kind: .rateLimited, message: e.message)
        case APIError.codeIncompatible:
            self.init(kind: .incompatible, message: e.message)
            versions = (e.dataString("serverVersion") ?? "?", e.dataString("clientVersion") ?? "?")
        default:
            self.init(kind: .other, message: e.message)
        }
    }

    /// 界面上显示的原因
    var text: String {
        switch kind {
        case .unreachable: return tr("server.err.unreachable", message)
        case .fingerprint: return tr("server.err.fingerprint")
        case .unauthorized: return tr("server.err.unauthorized")
        case .rateLimited: return tr("server.err.rateLimited")
        case .incompatible: return tr("server.err.incompatible", versions?.server ?? "?", versions?.client ?? "?")
        case .localDown: return tr("server.localRequired")
        case .other: return message
        }
    }

    /// 需要重新登录（换密钥）才能恢复
    var needsLogin: Bool { kind == .unauthorized || kind == .fingerprint }
}

/// 把 gateway.* 的错误换成界面文案（添加服务器对话框用）
func serverErrorText(_ error: Error) -> String {
    if let e = error as? APIError, e.code == APIError.codePasswordDisabled { return tr("server.err.passwordDisabled") }
    return ServerFailure(error).text
}

/// 服务器列表、当前服务器与连接阶段。列表存在应用偏好里，访问密钥存钥匙串。
@MainActor
final class ServerStore: ObservableObject {
    enum Phase: Equatable {
        case ready
        case connecting
        case failed(ServerFailure)
    }

    @Published private(set) var servers: [SavedServer] = []
    /// nil = 本机
    @Published private(set) var currentId: String?
    @Published var phase: Phase = .ready
    /// 当前远程服务器的网关 id（gateway.forward 用）
    var gatewayId: String?

    private static let listKey = "PaperplaneServers"
    private static let currentKey = "PaperplaneCurrentServer"

    private let keychain = ServerKeychain()

    init() {
        servers = SavedServerList.decode(UserDefaults.standard.data(forKey: Self.listKey))
        let cur = UserDefaults.standard.string(forKey: Self.currentKey)
        currentId = servers.contains { $0.id == cur } ? cur : nil
    }

    var current: SavedServer? { currentId.flatMap { id in servers.first { $0.id == id } } }
    var isRemote: Bool { currentId != nil }
    var currentName: String { current?.name ?? tr("server.local") }

    func server(_ id: String) -> SavedServer? { servers.first { $0.id == id } }

    func setCurrent(_ id: String?) {
        currentId = id
        UserDefaults.standard.set(id, forKey: Self.currentKey)
    }

    /// 已添加过的同一台服务器（地址 + 指纹）
    func existing(url: String, fingerprint: String, excluding: String? = nil) -> SavedServer? {
        servers.first { $0.id != excluding && $0.isSameServer(url: url, fingerprint: fingerprint) }
    }

    func token(_ id: String) -> String? { keychain.token(for: id) }

    /// 新增或更新（同 id 替换），密钥进钥匙串
    @discardableResult
    func save(_ s: SavedServer, token: String?) -> Bool {
        if let token, !keychain.setToken(token, for: s.id, label: "Lemon Paperplane · " + s.name) { return false }
        if let i = servers.firstIndex(where: { $0.id == s.id }) { servers[i] = s } else { servers.append(s) }
        persist()
        return true
    }

    func rename(_ id: String, to name: String) {
        guard let i = servers.firstIndex(where: { $0.id == id }) else { return }
        servers[i].name = name
        persist()
    }

    /// 连上后用服务器的 system.info 校正系统与主机名
    func updateInfo(_ id: String, os: String, hostname: String) {
        guard let i = servers.firstIndex(where: { $0.id == id }) else { return }
        guard servers[i].os != os || servers[i].hostname != hostname else { return }
        if !os.isEmpty { servers[i].os = os }
        if !hostname.isEmpty { servers[i].hostname = hostname }
        persist()
    }

    /// 只删本机记录和钥匙串里的密钥
    func remove(_ id: String) {
        keychain.removeToken(for: id)
        servers.removeAll { $0.id == id }
        if currentId == id { setCurrent(nil) }
        persist()
    }

    private func persist() {
        UserDefaults.standard.set(SavedServerList.encode(servers), forKey: Self.listKey)
    }

    // MARK: 显示

    /// 系统图标（共用图标库里没有系统徽标，用设备类图标：桌面系统 monitor，Linux 服务器 server；与 Windows 版一致）
    static func osIcon(_ os: String) -> String {
        switch os {
        case "darwin", "windows": return "monitor"
        default: return "server"
        }
    }

    static func osName(_ os: String) -> String {
        switch os {
        case "darwin", "windows", "linux": return tr("server.os." + os)
        default: return os
        }
    }
}

import Foundation

/// 负责找到并连上本机 daemon：读 runtime.json → system.info；连不上就执行 `paperplane daemon --detach` 再重试。
public final class DaemonManager: @unchecked Sendable {
    public enum Failure: Error, LocalizedError {
        case binaryNotFound([String])
        case launchFailed(String)
        case timeout

        public var errorDescription: String? {
            switch self {
            case .binaryNotFound(let tried): return "paperplane binary not found: " + tried.joined(separator: ", ")
            case .launchFailed(let m): return "failed to launch daemon: " + m
            case .timeout: return "daemon did not respond in time"
            }
        }
    }

    /// UserDefaults 里开发时指定 daemon 路径的键
    public static let binaryPathDefaultsKey = "PaperplaneBinaryPath"
    /// 开发时指定 daemon 路径的环境变量
    public static let binaryPathEnvKey = "PAPERPLANE_BIN"

    public let dataDir: URL
    public let appBundleURL: URL?
    public var launchTimeout: TimeInterval = 5

    public init(dataDir: URL = DaemonManager.defaultDataDir, appBundleURL: URL? = nil) {
        self.dataDir = dataDir
        self.appBundleURL = appBundleURL
    }

    /// ~/Library/Application Support/LemonPaperplane（开发时可用环境变量 PAPERPLANE_DATA_DIR 指向独立实例，与 daemon 一致）
    public static var defaultDataDir: URL {
        if let d = ProcessInfo.processInfo.environment["PAPERPLANE_DATA_DIR"], !d.isEmpty {
            return URL(fileURLWithPath: (d as NSString).expandingTildeInPath, isDirectory: true)
        }
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("LemonPaperplane", isDirectory: true)
    }

    public var runtimeFile: URL { dataDir.appendingPathComponent("runtime.json") }

    public func readRuntime() -> RuntimeInfo? {
        guard let data = try? Data(contentsOf: runtimeFile) else { return nil }
        return try? JSONCoding.decoder.decode(RuntimeInfo.self, from: data)
    }

    /// 候选 daemon 路径（按优先级）：环境变量 → UserDefaults → App 包内 → 开发目录
    public func candidateBinaries() -> [URL] {
        var out: [URL] = []
        if let env = ProcessInfo.processInfo.environment[Self.binaryPathEnvKey], !env.isEmpty {
            out.append(URL(fileURLWithPath: (env as NSString).expandingTildeInPath))
        }
        if let p = UserDefaults.standard.string(forKey: Self.binaryPathDefaultsKey), !p.isEmpty {
            out.append(URL(fileURLWithPath: (p as NSString).expandingTildeInPath))
        }
        if let app = appBundleURL {
            out.append(app.appendingPathComponent("Contents/MacOS/paperplane"))
        }
        if let rt = readRuntime(), let exe = rt.executable, !exe.isEmpty {
            out.append(URL(fileURLWithPath: exe))
        }
        out.append(URL(fileURLWithPath: "/usr/local/bin/paperplane"))
        out.append(URL(fileURLWithPath: "/opt/homebrew/bin/paperplane"))
        return out
    }

    public func locateBinary() -> URL? {
        candidateBinaries().first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    /// 尝试用现有 runtime.json 连接；成功返回客户端与系统信息。
    public func probe(timeout: TimeInterval = 2) async -> (APIClient, SystemInfo)? {
        guard let rt = readRuntime() else { return nil }
        let client = APIClient(runtime: rt)
        do {
            let info = try await client.systemInfo(timeout: timeout)
            return (client, info)
        } catch {
            return nil
        }
    }

    /// 本 App 自带的 daemon（开发时环境变量 / UserDefaults 指定的优先）。
    public var ownBinary: URL? {
        if let env = ProcessInfo.processInfo.environment[Self.binaryPathEnvKey], !env.isEmpty {
            return URL(fileURLWithPath: (env as NSString).expandingTildeInPath)
        }
        if let p = UserDefaults.standard.string(forKey: Self.binaryPathDefaultsKey), !p.isEmpty {
            return URL(fileURLWithPath: (p as NSString).expandingTildeInPath)
        }
        if let app = appBundleURL {
            let u = app.appendingPathComponent("Contents/MacOS/paperplane")
            if FileManager.default.isExecutableFile(atPath: u.path) { return u }
        }
        return nil
    }

    private static func samePath(_ a: String, _ b: String) -> Bool {
        URL(fileURLWithPath: a).resolvingSymlinksInPath().standardizedFileURL.path
            == URL(fileURLWithPath: b).resolvingSymlinksInPath().standardizedFileURL.path
    }

    /// 正在运行的 daemon 是否应该换成本 App 自带的：
    /// 来自别处的安装（另一份 App、旧的构建目录）且（插件为 0 / 可执行文件已不存在 / 版本不同 / 没有正在运行的工程）。
    /// 别处安装的 daemon 正在跑工程、且版本一致、插件正常时保留它，避免把用户的服务停掉。
    func shouldReplace(_ info: SystemInfo, client: APIClient) async -> Bool {
        guard let own = ownBinary else { return false }
        if info.executable.isEmpty || Self.samePath(info.executable, own.path) {
            return false
        }
        if info.plugins == 0 { return true }
        if !FileManager.default.fileExists(atPath: info.executable) { return true }
        if let v = Bundle(url: appBundleURL ?? own)?.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
           !v.isEmpty, v != info.version { return true }
        let active: [Run] = (try? await client.runs(active: true)) ?? []
        return active.isEmpty
    }

    /// 连接（必要时拉起 daemon；运行中的 daemon 不属于本 App 或不完整时换成自己的）。
    public func connect(locale: String? = nil) async throws -> (APIClient, SystemInfo) {
        var replace = false
        if let (client, info) = await probe() {
            if await shouldReplace(info, client: client) {
                replace = true
            } else {
                return (client, info)
            }
        }
        guard let bin = replace ? ownBinary : (ownBinary ?? locateBinary()) else {
            throw Failure.binaryNotFound(candidateBinaries().map(\.path))
        }
        let before = readRuntime()?.pid
        try launch(binary: bin, locale: locale, replace: replace)
        let deadline = Date().addingTimeInterval(launchTimeout + (replace ? 15 : 0))
        while Date() < deadline {
            try await Task.sleep(nanoseconds: 150_000_000)
            if let ok = await probe(timeout: 1) {
                if replace, ok.1.pid == before { continue }
                return ok
            }
        }
        throw Failure.timeout
    }

    /// 执行 `paperplane daemon --detach`。locale 用来告诉 daemon 系统语言（设置为「跟随系统」时用）。
    public func launch(binary: URL, locale: String?, replace: Bool = false) throws {
        let p = Process()
        p.executableURL = binary
        p.arguments = replace ? ["daemon", "--detach", "--replace"] : ["daemon", "--detach"]
        var env = ProcessInfo.processInfo.environment
        if let locale {
            env["PAPERPLANE_SYSTEM_LOCALE"] = locale
            if env["LANG"] == nil || env["LANG"]?.isEmpty == true {
                env["LANG"] = (locale.hasPrefix("zh") ? "zh_CN" : "en_US") + ".UTF-8"
            }
        }
        p.environment = env
        p.standardInput = FileHandle.nullDevice
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do {
            try p.run()
        } catch {
            throw Failure.launchFailed(error.localizedDescription)
        }
        // --detach 会很快返回；不阻塞等待，交给上层轮询 runtime.json
    }
}

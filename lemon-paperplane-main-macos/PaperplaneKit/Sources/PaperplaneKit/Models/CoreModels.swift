import Foundation

// 对应 lemon-paperplane-main-core/internal/model/model.go。字段名以 Go json tag 为准。
// 改名：Task → TaskInfo（避开 Swift 并发的 Task），Settings → AppSettings（避开 SwiftUI 的 Settings），Event → DaemonEvent。

// MARK: - 工作区与工程

public struct Workspace: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefArray public var projectIds: [String]
    public var createdAt: Date?
    public init(id: String, name: String, projectIds: [String] = []) {
        self.id = id; self.name = name; self.projectIds = projectIds
    }
}

public struct Project: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefString public var path: String
    @DefString public var workspaceId: String
    @DefArray public var modules: [Module]
    public var settings: ProjectSettings?
    @DefArray public var vcsRoots: [VCSRoot]
    public var importedAt: Date?
    public var analyzedAt: Date?
    public var defaultConfigId: String?

    /// 全部运行配置（按模块顺序）
    public var allRunConfigs: [RunConfig] { modules.flatMap { $0.runConfigs } }

    public func module(_ id: String?) -> Module? {
        guard let id else { return nil }
        return modules.first { $0.id == id }
    }

    public func runConfig(_ id: String?) -> RunConfig? {
        guard let id else { return nil }
        return allRunConfigs.first { $0.id == id }
    }

    /// 工程里出现过的全部 facet id
    public var facetIds: Set<String> { Set(modules.flatMap { $0.facets.map(\.id) }) }

    /// 参与过本工程的插件
    public var pluginIds: Set<String> {
        var s = Set<String>()
        for m in modules {
            for f in m.facets { if let p = f.plugin { s.insert(p) } }
            for k in m.analysis.keys { s.insert(k) }
        }
        return s
    }
}

public struct ProjectSettings: Codable, Hashable {
    @DefDict public var env: [String: String]
    @DefDict public var toolchains: [String: ToolchainOverride]
    public var notes: String?
    public init(env: [String: String] = [:], toolchains: [String: ToolchainOverride] = [:]) {
        self.env = env; self.toolchains = toolchains
    }
}

public struct ToolchainOverride: Codable, Hashable {
    public var id: String?
    public var constraint: String?
    public init(id: String? = nil, constraint: String? = nil) { self.id = id; self.constraint = constraint }
}

public struct VCSRoot: Codable, Hashable {
    @DefString public var path: String
    @DefString public var relPath: String
    @DefString public var vcs: String
    @DefString public var plugin: String
}

public struct Module: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefString public var relDir: String
    @DefArray public var facets: [Facet]
    @DefDict public var models: [String: JSONValue]
    @DefDict public var analysis: [String: ModuleAnalysis]
    @DefDict public var toolchains: [String: ToolchainSelection]
    @DefArray public var requirements: [ToolchainRequirement]
    @DefArray public var runConfigs: [RunConfig]
    public var dependencies: DependencyState?
    @DefBool public var synced: Bool
    @DefBool public var needsSync: Bool

    /// 各插件 info 合并
    public var info: [InfoItem] { analysis.keys.sorted().flatMap { analysis[$0]?.info ?? [] } }

    /// 各插件静态问题合并
    public var problems: [Diagnosis] { analysis.keys.sorted().flatMap { analysis[$0]?.problems ?? [] } }

    /// 各插件动态下拉选项合并（FormField.optionsFrom 查这里）
    public var optionValues: [String: [Option]] {
        var out: [String: [Option]] = [:]
        for k in analysis.keys.sorted() {
            for (key, opts) in analysis[k]?.optionValues ?? [:] { out[key, default: []].append(contentsOf: opts) }
        }
        return out
    }

    public func hasAnyFacet(_ ids: [String]) -> Bool {
        if ids.isEmpty { return true }
        return facets.contains { ids.contains($0.id) }
    }
}

public struct ModuleAnalysis: Codable, Hashable {
    @DefArray public var info: [InfoItem]
    @DefArray public var problems: [Diagnosis]
    @DefDict public var optionValues: [String: [Option]]
    public var dependencies: DependencyState?
    public var at: Date?
}

public struct ToolchainSelection: Codable, Hashable {
    @DefString public var kind: String
    public var toolchainId: String?
    public var version: String?
    public var constraint: String?
    public var preferred: String?
    @DefBool public var missing: Bool
    @DefBool public var override: Bool
    public var source: String?
}

/// 持久化的运行配置（Go 里嵌入了 sdk.RunConfiguration，JSON 平铺）。
public struct RunConfig: Codable, Hashable, Identifiable {
    @DefString public var id: String
    @DefString public var name: String
    @DefString public var type: String
    public var plugin: String?
    public var moduleId: String?
    @DefDict public var options: [String: JSONValue]
    @DefDict public var env: [String: String]
    @DefBool public var `default`: Bool
    public var source: String? // auto | user | idea | vscode
    @DefInt public var priority: Int
    @DefBool public var edited: Bool
    @DefBool public var stale: Bool

    public init(id: String, name: String, type: String, plugin: String? = nil, moduleId: String? = nil,
                options: [String: JSONValue] = [:], env: [String: String] = [:]) {
        self.id = id; self.name = name; self.type = type; self.plugin = plugin; self.moduleId = moduleId
        self.options = options; self.env = env
    }
}

// MARK: - 卡片摘要

public struct ProjectSummary: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefString public var path: String
    @DefString public var workspaceId: String
    @DefArray public var facets: [FacetBadge]
    @DefArray public var info: [InfoItem]
    @DefString public var status: String // idle | starting | running | failed | stopping | partial
    @DefArray public var runs: [RunBrief]
    @DefArray public var configs: [ConfigBrief]
    public var defaultConfigId: String?
    /// 加到工作区「启动」面板的运行配置 id（按加入顺序，只含仍存在的）
    @DefArray public var launch: [String]
    public var vcs: VCSBrief?
    @DefInt public var problems: Int
    public var problemSeverity: String?
    @DefArray public var missingToolchains: [String]
    public var dependencies: String?
    public var busy: String?

    public init(id: String, name: String, path: String, workspaceId: String, status: String = "idle") {
        self.id = id; self.name = name; self.path = path; self.workspaceId = workspaceId; self.status = status
    }

    /// 是否有运行中（含启动中、准备中、停止中）的运行
    public var hasActiveRun: Bool { runs.contains { RunState.isActive($0.state) } }
}

public struct FacetBadge: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    public var icon: String?
    public var category: String?
    @DefString public var plugin: String
    public init(id: String, name: String, icon: String? = nil, category: String? = nil, plugin: String) {
        self.id = id; self.name = name; self.icon = icon; self.category = category; self.plugin = plugin
    }
}

public struct ConfigBrief: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefString public var type: String
    public var icon: String?
    @DefString public var moduleId: String
    @DefString public var moduleName: String
}

public struct RunBrief: Codable, Hashable, Identifiable {
    public var id: String
    public var projectId: String?
    @DefString public var configId: String
    @DefString public var name: String
    @DefString public var state: String
    @DefArray public var urls: [String]
    @DefDouble public var cpu: Double
    @DefDouble public var rssMB: Double
    /// 本次运行的内存峰值
    @DefDouble public var peakRSSMB: Double
    /// 进程树里的进程数
    @DefInt public var procs: Int
    /// 采样时间（Unix 毫秒），据此追加趋势点
    @DefInt64 public var t: Int64
}

public struct VCSBrief: Codable, Hashable {
    @DefString public var branch: String
    @DefInt public var ahead: Int
    @DefInt public var behind: Int
    @DefInt public var changes: Int
    @DefInt public var conflicts: Int
    public var operation: String?
    @DefInt public var roots: Int
    public var at: Date?
}

// MARK: - 运行与任务

public enum RunState {
    public static let preparing = "preparing"
    public static let starting = "starting"
    public static let running = "running"
    public static let stopping = "stopping"
    public static let exited = "exited"
    public static let failed = "failed"

    /// 还没结束
    public static func isActive(_ s: String) -> Bool {
        s == preparing || s == starting || s == running || s == stopping
    }
}

public struct Run: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var projectId: String
    @DefString public var moduleId: String
    @DefString public var configId: String
    @DefString public var name: String
    @DefString public var plugin: String
    @DefString public var state: String
    public var pid: Int?
    public var command: String?
    public var cwd: String?
    public var startedAt: Date?
    public var readyAt: Date?
    public var endedAt: Date?
    public var exitCode: Int?
    @DefBool public var stoppedByUser: Bool
    public var error: String?
    @DefArray public var urls: [String]
    @DefArray public var ports: [Int]
    @DefDouble public var cpu: Double
    @DefDouble public var rssMB: Double
    /// 本次运行的内存峰值
    @DefDouble public var peakRSSMB: Double
    /// 进程树里的进程数
    @DefInt public var procs: Int
    @DefArray public var diagnoses: [Diagnosis]
    public var logFile: String?
    @DefInt public var lines: Int

    public init(id: String, projectId: String, configId: String, name: String, state: String) {
        self.id = id; self.projectId = projectId; self.configId = configId; self.name = name; self.state = state
    }

    public var isActive: Bool { RunState.isActive(state) }
}

public enum TaskState {
    public static let running = "running"
    public static let succeeded = "succeeded"
    public static let failed = "failed"
    public static let cancelled = "cancelled"
}

/// model.Task
public struct TaskInfo: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var kind: String // import | reimport | sync | install | toolchain | action | vcs | plugin
    @DefString public var title: String
    public var projectId: String?
    public var moduleId: String?
    @DefString public var state: String
    public var message: String?
    @DefDouble public var percent: Double // <0 不确定
    public var error: String?
    public var startedAt: Date?
    public var endedAt: Date?
    public var result: JSONValue?
    @DefArray public var diagnoses: [Diagnosis]

    public init(id: String, kind: String, title: String, state: String, percent: Double = -1) {
        self.id = id; self.kind = kind; self.title = title; self.state = state; self.percent = percent
    }

    public var isRunning: Bool { state == TaskState.running }
}

/// 控制台的一行。text 保留 ANSI 转义。
public struct LogLine: Codable, Hashable {
    public var seq: Int64
    @DefInt64 public var t: Int64
    @DefString public var s: String // stdout | stderr | system
    @DefString public var text: String
    public init(seq: Int64, t: Int64 = 0, s: String = "stdout", text: String) {
        self.seq = seq; self.t = t; self.s = s; self.text = text
    }
}

@propertyWrapper
public struct DefInt64: Codable, Hashable {
    public var wrappedValue: Int64
    public init(wrappedValue: Int64 = 0) { self.wrappedValue = wrappedValue }
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        wrappedValue = c.decodeNil() ? 0 : try c.decode(Int64.self)
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(wrappedValue)
    }
}

public extension KeyedDecodingContainer {
    func decode(_ type: DefInt64.Type, forKey key: Key) throws -> DefInt64 {
        try decodeIfPresent(type, forKey: key) ?? DefInt64()
    }
}

// MARK: - 工具链与插件

public struct ToolchainKind: Codable, Hashable, Identifiable {
    public var kind: String
    @DefString public var name: String
    public var icon: String?
    @DefBool public var installable: Bool
    @DefString public var plugin: String
    public var id: String { kind }
}

public struct PluginInfo: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefString public var version: String
    public var vendor: String?
    public var description: String?
    public var icon: String?
    public var category: String?
    public var homepage: String?
    @DefString public var source: String // bundled | user | dev
    @DefString public var dir: String
    @DefBool public var enabled: Bool
    @DefBool public var running: Bool
    public var error: String?
    @DefArray public var dependencies: [Dependency]
    public var bundledVersion: String?
    public var contributes: ContribSummary?
}

public struct ContribSummary: Codable, Hashable {
    @DefArray public var facets: [String]
    @DefArray public var toolchains: [String]
    @DefArray public var runConfigTypes: [String]
    @DefInt public var diagnostics: Int
    @DefArray public var views: [String]
    @DefArray public var actions: [String]
    @DefArray public var tools: [String]
    public var vcs: String?
}

public struct StoreEntry: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefString public var version: String
    public var vendor: String?
    public var description: String?
    public var icon: String?
    public var engines: Engines?
    @DefDict public var platforms: [String: StorePlatform]
    public var installed: String?
    @DefBool public var compatible: Bool
}

public struct StorePlatform: Codable, Hashable {
    @DefString public var url: String
    @DefString public var sha256: String
    public var size: Int64?
}

// MARK: - 界面贡献

public struct Contributions: Codable, Hashable {
    @DefArray public var facets: [FacetBadge]
    @DefArray public var toolchainKinds: [ToolchainKind]
    @DefArray public var runConfigTypes: [RunConfigTypeInfo]
    @DefArray public var views: [ViewInfo]
    @DefArray public var actions: [ActionInfo]
    @DefArray public var settings: [PluginSettingsInfo]
    @DefArray public var vcs: [VCSInfo]
    /// 编辑器：语言着色、快捷键方案、配色主题（docs/editor.md 第 4 节）
    @DefArray public var languages: [LanguageInfo]
    @DefArray public var keymaps: [KeymapInfo]
    @DefArray public var editorThemes: [EditorThemeInfo]
    /// 本平台默认的快捷键方案 id（Mac 上是 eclipse-macos）
    public var defaultKeymap: String?
    public init() {}

    public func views(at location: String) -> [ViewInfo] {
        views.filter { $0.location == location }.sorted { ($0.order, $0.title) < ($1.order, $1.title) }
    }

    public func actions(at location: String) -> [ActionInfo] {
        actions.filter { a in
            a.locations.isEmpty ? location == ActionLocation.projectMenu : a.locations.contains(location)
        }
    }

    public func runConfigType(plugin: String?, type: String) -> RunConfigTypeInfo? {
        runConfigTypes.first { $0.id == type && (plugin == nil || $0.plugin == plugin) }
            ?? runConfigTypes.first { $0.id == type }
    }

    public func facet(_ id: String) -> FacetBadge? { facets.first { $0.id == id } }
}

/// RunConfigTypeInfo：sdk.RunConfigTypeDef + plugin（平铺）
public struct RunConfigTypeInfo: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    public var icon: String?
    @DefArray public var facets: [String]
    @DefArray public var options: [FormField]
    @DefString public var plugin: String
}

/// ViewInfo：sdk.ViewDef + plugin
public struct ViewInfo: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var title: String
    public var icon: String?
    @DefString public var location: String
    @DefArray public var facets: [String]
    public var kind: String? // declarative | webview
    public var entry: String?
    @DefInt public var order: Int
    public var runState: String?
    @DefString public var plugin: String

    /// 全局唯一键
    public var key: String { "\(plugin)/\(id)" }
    public var isWebview: Bool { kind == "webview" }
}

/// ActionInfo：sdk.ActionDef + plugin
public struct ActionInfo: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var title: String
    public var icon: String?
    @DefArray public var facets: [String]
    public var confirm: String?
    public var group: String?
    @DefArray public var locations: [String]
    @DefArray public var form: [FormField]
    @DefString public var plugin: String

    public var key: String { "\(plugin)/\(id)" }
}

public struct PluginSettingsInfo: Codable, Hashable {
    @DefString public var plugin: String
    @DefString public var title: String
    @DefArray public var fields: [FormField]
    @DefDict public var values: [String: JSONValue]
}

public struct VCSInfo: Codable, Hashable {
    public var id: String
    @DefString public var name: String
    @DefString public var rootMarker: String
    @DefString public var plugin: String
}

// MARK: - 设置

/// model.Settings
public struct AppSettings: Codable, Hashable {
    @DefString public var locale: String // system | zh-Hans | en
    @DefString public var theme: String // system | light | dark
    @DefString public var mirrorRegion: String // auto | china | global
    @DefBool public var autoInstallToolchains: Bool
    @DefBool public var autoInstallDependencies: Bool
    /// 导入后自动执行构建工具同步（如 Gradle）
    @DefBool public var autoSyncOnImport: Bool
    @DefInt public var pluginIdleSeconds: Int
    @DefInt public var logLinesPerRun: Int
    @DefInt public var logRetentionRuns: Int
    @DefInt public var apiPort: Int
    public var storeIndexUrl: String?
    @DefInt public var gitAutoFetchMinutes: Int
    public var terminal: String?
    @DefDict public var pluginSettings: [String: [String: JSONValue]]
    @DefArray public var disabledPlugins: [String]
    /// settings.editor（缺失时用 EditorSettings() 的默认值）
    public var editor: EditorSettings?
    public init() {}
}

// MARK: - 事件

/// /api/v1/events 的事件类型
public enum EventType {
    public static let hello = "hello"
    public static let workspaces = "workspaces.changed"
    public static let project = "project.changed"
    public static let projectRemoved = "project.removed"
    public static let run = "run.changed"
    public static let task = "task.changed"
    public static let toolchains = "toolchains.changed"
    public static let plugins = "plugins.changed"
    public static let settings = "settings.changed"
    public static let notify = "notify"
    public static let prompt = "prompt"
    public static let promptDone = "prompt.done"
    public static let vcs = "vcs.changed"
    public static let stats = "stats"
}

/// 一条事件，data 先保留为 JSONValue，由使用方按 type 解码。
public struct DaemonEvent: Codable, Hashable {
    public var type: String
    public var data: JSONValue?
    public init(type: String, data: JSONValue? = nil) { self.type = type; self.data = data }

    public func decode<T: Decodable>(_ t: T.Type) throws -> T {
        try (data ?? .null).decode(as: T.self)
    }
}

/// 需要界面回答的对话框（Go 里嵌入了 sdk.PromptParams，JSON 平铺）。
public struct Prompt: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var title: String
    public var message: String?
    @DefArray public var fields: [FormField]
    public var okText: String?
    @DefBool public var danger: Bool
    public var plugin: String?
}

public struct SystemInfo: Codable, Hashable {
    @DefString public var version: String
    @DefString public var os: String
    @DefString public var arch: String
    @DefInt public var pid: Int
    public var startedAt: Date?
    @DefString public var dataDir: String
    @DefString public var apiBase: String
    @DefString public var mcpUrl: String
    @DefString public var executable: String
    @DefDouble public var memoryMB: Double
    @DefInt public var pluginProcs: Int
    @DefDouble public var pluginMemoryMB: Double
    @DefString public var locale: String
    @DefString public var mirrorRegion: String
    /// 可用插件数；0 表示安装不完整，任何工程都识别不了
    @DefInt public var plugins: Int
    public var pluginErrors: [String]?
    public var pluginDirs: [String]?
}

// MARK: - 各操作的专用输出

/// project.scan 的一项
public struct ScanEntry: Codable, Hashable, Identifiable {
    public var path: String
    @DefString public var name: String
    @DefArray public var facets: [FacetBadge]
    @DefBool public var imported: Bool
    public var projectId: String?
    public var id: String { path }
}

/// runconfig.types 的一项
public struct RunConfigTypeChoice: Codable, Hashable {
    public var type: RunConfigTypeInfo
    @DefDict public var optionValues: [String: [Option]]
}

/// run.logs 输出
public struct LogsResult: Codable, Hashable {
    @DefArray public var lines: [LogLine]
    @DefInt64 public var lastSeq: Int64
    @DefInt64 public var total: Int64
    @DefBool public var truncated: Bool
}

/// fix.apply 输出
public struct FixResult: Codable, Hashable {
    public var message: String?
    public var openUrl: String?
    public var rerun: Run?
    /// 修复动作生成的任务（如安装依赖、下载工具链）
    public var task: TaskInfo?
}

/// mirrors.status / mirrors.probe 输出
public struct MirrorStatus: Codable, Hashable {
    @DefString public var configured: String
    @DefString public var effective: String
    @DefArray public var probes: [MirrorProbe]
    public var at: Date?
}

public struct MirrorProbe: Codable, Hashable, Identifiable {
    @DefString public var name: String
    @DefString public var url: String
    public var ms: Double?
    @DefBool public var ok: Bool
    public var id: String { name + "|" + url }
}

/// mcp.config 输出
public struct MCPConfig: Codable, Hashable {
    @DefString public var url: String
    @DefString public var token: String
    @DefArray public var stdioCommand: [String]
    @DefString public var claudeCode: String
    public var claudeDesktop: JSONValue?
}

public struct StopAllResult: Codable, Hashable { @DefInt public var stopped: Int }

/// launch.add / launch.remove 的结果：该工程现在的启动项
public struct LaunchIDs: Codable, Hashable { @DefArray public var launch: [String] }

/// launch.run / launch.stop 的结果
public struct LaunchResult: Codable, Hashable {
    @DefArray public var started: [Run]
    @DefInt public var stopped: Int
    @DefInt public var skipped: Int
    @DefArray public var errors: [LaunchRunError]
}

public struct LaunchRunError: Codable, Hashable {
    @DefString public var projectId: String
    @DefString public var configId: String
    @DefString public var error: String
}
public struct PathResult: Codable, Hashable { @DefString public var path: String }
public struct PortKillResult: Codable, Hashable { @DefArray public var pids: [Int] }

/// 运行时文件 runtime.json
public struct RuntimeInfo: Codable, Hashable {
    public var port: Int
    public var token: String
    public var pid: Int?
    public var version: String?
    public var executable: String?
}

// 值类型，跨线程传递安全
extension DaemonEvent: @unchecked Sendable {}
extension LogLine: @unchecked Sendable {}
extension Run: @unchecked Sendable {}
extension TaskInfo: @unchecked Sendable {}

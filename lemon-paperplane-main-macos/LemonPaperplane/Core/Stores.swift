import Combine
import Foundation
import PaperplaneKit

/// 工作区、工程摘要、设置、插件贡献等「较少变化」的数据。
@MainActor
final class DataStore: ObservableObject {
    @Published var workspaces: [Workspace] = []
    @Published var projects: [String: ProjectSummary] = [:]
    @Published var settings: AppSettings?
    @Published var contributions = Contributions()
    @Published var plugins: [PluginInfo] = []
    @Published var toolchainKinds: [ToolchainKind] = []
    @Published var toolchains: [Toolchain] = []
    @Published var loaded = false

    func workspace(_ id: String?) -> Workspace? { workspaces.first { $0.id == id } }

    /// 以内核的工作区列表为准：同时校正工程摘要里的 workspaceId（移动工程后摘要可能比工作区列表晚到）
    func applyWorkspaces(_ ws: [Workspace]) {
        workspaces = ws
        for w in ws {
            for id in w.projectIds where projects[id] != nil && projects[id]?.workspaceId != w.id {
                projects[id]?.workspaceId = w.id
            }
        }
    }

    /// 按工作区内顺序
    func projects(in workspaceId: String) -> [ProjectSummary] {
        guard let ws = workspace(workspaceId) else { return [] }
        var out = ws.projectIds.compactMap { projects[$0] }
        // 刚导入、工作区列表还没到的工程：只补「不在任何工作区顺序表里」的，避免移走的工程在旧工作区重复出现
        let listed = Set(workspaces.flatMap(\.projectIds))
        out += projects.values.filter { $0.workspaceId == workspaceId && !listed.contains($0.id) }.sorted { $0.name < $1.name }
        return out
    }

    /// 全部工程（按工作区顺序）
    var allProjects: [ProjectSummary] { workspaces.flatMap { projects(in: $0.id) } }

    func toolchains(kind: String) -> [Toolchain] { toolchains.filter { $0.kind == kind } }

    func kindName(_ kind: String) -> String { toolchainKinds.first { $0.kind == kind }?.name ?? kind }

    func plugin(_ id: String) -> PluginInfo? { plugins.first { $0.id == id } }
}

/// 运行与任务（变化频繁：stats 每 2 秒一次）。
@MainActor
final class RunStore: ObservableObject {
    @Published var runs: [String: Run] = [:]
    @Published var tasks: [String: TaskInfo] = [:]
    /// 本界面发起的工具链下载任务：taskId → kind
    var toolchainTaskKind: [String: String] = [:]

    var activeRuns: [Run] {
        runs.values.filter(\.isActive).sorted { ($0.startedAt ?? .distantPast) > ($1.startedAt ?? .distantPast) }
    }

    var activeTasks: [TaskInfo] {
        tasks.values.filter(\.isRunning).sorted { ($0.startedAt ?? .distantPast) > ($1.startedAt ?? .distantPast) }
    }

    /// 最近的任务（运行中在前，其后最近结束的若干个）
    var recentTasks: [TaskInfo] {
        let ended = tasks.values.filter { !$0.isRunning }
            .sorted { ($0.endedAt ?? .distantPast) > ($1.endedAt ?? .distantPast) }
        return activeTasks + ended.prefix(8)
    }

    func latestRun(projectId: String, configId: String) -> Run? {
        runs.values.filter { $0.projectId == projectId && $0.configId == configId }
            .max { ($0.startedAt ?? .distantPast) < ($1.startedAt ?? .distantPast) }
    }

    func activeRuns(projectId: String) -> [Run] { activeRuns.filter { $0.projectId == projectId } }

    func activeRun(projectId: String, configId: String) -> Run? {
        activeRuns.first { $0.projectId == projectId && $0.configId == configId }
    }

    func tasks(projectId: String) -> [TaskInfo] { activeTasks.filter { $0.projectId == projectId } }

    /// stats 事件：CPU/内存走 ResourceStore（按运行各自刷新），这里只在地址或状态变了时才更新，
    /// 避免每 2 秒让所有观察 RunStore 的视图重绘
    func applyStats(_ briefs: [RunBrief]) {
        for b in briefs {
            guard var r = runs[b.id] else { continue }
            let urlsChanged = !b.urls.isEmpty && r.urls != b.urls
            let stateChanged = !b.state.isEmpty && r.state != b.state
            guard urlsChanged || stateChanged else { continue }
            if urlsChanged { r.urls = b.urls }
            if stateChanged { r.state = b.state }
            r.cpu = b.cpu
            r.rssMB = b.rssMB
            runs[b.id] = r
        }
    }

    /// 只保留进行中的和最近 50 个结束的，防止长期运行后无限增长
    func prune() {
        let ended = runs.values.filter { !$0.isActive }.sorted { ($0.startedAt ?? .distantPast) > ($1.startedAt ?? .distantPast) }
        if ended.count > 50 { for r in ended.dropFirst(50) { runs.removeValue(forKey: r.id) } }
        let doneTasks = tasks.values.filter { !$0.isRunning }.sorted { ($0.endedAt ?? .distantPast) > ($1.endedAt ?? .distantPast) }
        if doneTasks.count > 30 { for t in doneTasks.dropFirst(30) { tasks.removeValue(forKey: t.id) } }
    }
}

/// 页面导航与界面状态。
@MainActor
final class NavStore: ObservableObject {
    enum Page: Hashable {
        case workspace(String)
        case running
        case toolchains
        case plugins
        case settings
        case pluginView(String) // ViewInfo.key
    }

    enum ProjectTab: Hashable {
        case overview, run, files, vcs, config
        case plugin(String) // ViewInfo.key
    }

    @Published var page: Page = .running {
        didSet {
            if case .workspace(let id) = page { UserDefaults.standard.set(id, forKey: "PaperplaneLastWorkspace") }
            openProjectId = nil
        }
    }

    @Published var openProjectId: String?
    @Published var projectTab: ProjectTab = .overview
    @Published var search = ""
    @Published var sidebarVisible = UserDefaults.standard.object(forKey: "PaperplaneSidebarVisible") as? Bool ?? true {
        didSet { UserDefaults.standard.set(sidebarVisible, forKey: "PaperplaneSidebarVisible") }
    }
    @Published var sidebarWidth: CGFloat = M.sidebarWidth
    /// 运行页里每个工程选中的运行配置
    @Published var selectedConfig: [String: String] = [:]
    @Published var isFullScreen = false

    /// ⌘F：工具栏搜索框或控制台搜索
    let focusSearch = PassthroughSubject<Void, Never>()
    let consoleFind = PassthroughSubject<Void, Never>()
    /// 当前可见的控制台数量（⌘F 判断去向）
    var visibleConsoles = 0

    var lastWorkspaceId: String? { UserDefaults.standard.string(forKey: "PaperplaneLastWorkspace") }

    var currentWorkspaceId: String? {
        if case .workspace(let id) = page { return id }
        return nil
    }

    func openProject(_ id: String, tab: ProjectTab = .overview) {
        openProjectId = id
        projectTab = tab
    }

    func openRunConsole(projectId: String, configId: String) {
        selectedConfig[projectId] = configId
        openProject(projectId, tab: .run)
    }
}

/// 事件总线：给只关心某类事件的视图（版本控制、工程详情、工具链页）用。
@MainActor
final class EventBus {
    /// project.changed（工程 id）
    let projectChanged = PassthroughSubject<String, Never>()
    /// vcs.changed
    let vcsChanged = PassthroughSubject<VCSChangedEvent, Never>()
    /// toolchains.changed
    let toolchainsChanged = PassthroughSubject<Void, Never>()
    /// plugins.changed
    let pluginsChanged = PassthroughSubject<Void, Never>()
    /// 任何原始事件（调试或扩展用）
    let raw = PassthroughSubject<DaemonEvent, Never>()
}

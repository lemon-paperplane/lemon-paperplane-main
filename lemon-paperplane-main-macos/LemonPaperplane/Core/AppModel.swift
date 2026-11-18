import AppKit
import Combine
import Foundation
import PaperplaneKit

/// 界面的根状态：连接 daemon、订阅事件流、分发到各个 store。
@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()

    enum Connection: Equatable {
        case connecting
        case connected
        case reconnecting
        case failed(String)
    }

    @Published private(set) var connection: Connection = .connecting
    @Published private(set) var systemInfo: SystemInfo?

    private(set) var api: APIClient?

    let data = DataStore()
    let runs = RunStore()
    let nav = NavStore()
    let dialogs = DialogStore()
    let toasts = ToastStore()
    let bus = EventBus()

    private let daemon = DaemonManager(appBundleURL: Bundle.main.bundleURL)
    private let events = EventStream()
    /// 本界面发起的任务：结束时给出提示
    private var trackedTasks: Set<String> = []
    private var started = false

    private init() {}

    // MARK: 连接

    func start() {
        guard !started else { return }
        started = true
        Appearance.apply(UserDefaults.standard.string(forKey: "PaperplaneTheme"))
        connect()
    }

    func connect() {
        connection = .connecting
        Task {
            do {
                let (client, info) = try await daemon.connect(locale: L10n.shared.systemLocale)
                api = client
                systemInfo = info
                connection = .connected
                if info.plugins == 0 {
                    toasts.show("danger", tr("app.noPlugins"), message: info.executable)
                }
                startEvents(client)
                await refreshAll()
            } catch {
                connection = .failed(error.localizedDescription)
            }
        }
    }

    /// 需要已连接的 API；没连上时抛出可展示的错误
    func client() throws -> APIClient {
        if let api { return api }
        throw APIError(code: APIError.codeNetwork, message: tr("connection.notConnected"))
    }

    private func startEvents(_ client: APIClient) {
        events.start(client: client) { [weak self] update in
            Task { @MainActor in self?.handle(update) }
        }
    }

    private func handle(_ update: EventStream.Update) {
        switch update {
        case .connected:
            if connection != .connected { connection = .connected }
        case .disconnected:
            if connection == .connected { connection = .reconnecting }
            scheduleRecovery()
        case .event(let e):
            handle(event: e)
        }
    }

    /// 事件流断开 2 秒后仍没恢复：重新读 runtime.json（daemon 可能换了端口/令牌），必要时重新拉起 daemon
    private var recovering = false

    private func scheduleRecovery() {
        guard !recovering else { return }
        recovering = true
        Task {
            defer { recovering = false }
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            guard connection != .connected else { return }
            guard let (client, info) = try? await daemon.connect(locale: L10n.shared.systemLocale) else { return }
            if client.baseURL != api?.baseURL || client.token != api?.token {
                api = client
                systemInfo = info
                startEvents(client)
            }
        }
    }

    /// 重新拉取全部状态（启动、重连后的 hello）
    func refreshAll() async {
        guard let api else { return }
        async let ws = try? api.workspaces()
        async let ps = try? api.projects()
        async let rs = try? api.runs()
        async let ts = try? api.tasks()
        async let st = try? api.settings()
        async let cs = try? api.contributions()
        async let pl = try? api.plugins()
        async let kinds = try? api.toolchainKinds()
        async let tcs = try? api.toolchains()
        async let prompts = try? api.pendingPrompts()

        if let ws = await ws { data.applyWorkspaces(ws) }
        if let ps = await ps { data.projects = Dictionary(ps.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a }) }
        if let rs = await rs { runs.runs = Dictionary(rs.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a }) }
        if let ts = await ts { runs.tasks = Dictionary(ts.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a }) }
        if let st = await st { applySettings(st) }
        if let cs = await cs { data.contributions = cs }
        if let pl = await pl { setPlugins(pl) }
        if let k = await kinds { data.toolchainKinds = k }
        if let t = await tcs { data.toolchains = t }
        if let prompts = await prompts { for p in prompts { PromptPresenter.present(p) } }
        data.loaded = true
        fixNavigation()
    }

    /// 启动后选一个合理的页面
    private func fixNavigation() {
        switch nav.page {
        case .workspace(let id) where data.workspace(id) != nil: return
        case .toolchains, .plugins, .settings, .pluginView: return
        default: break
        }
        if let last = nav.lastWorkspaceId, data.workspace(last) != nil {
            nav.page = .workspace(last)
        } else if let first = data.workspaces.first {
            nav.page = .workspace(first.id)
        }
    }

    private func setPlugins(_ list: [PluginInfo]) {
        data.plugins = list
        IconStore.shared.pluginVersions = Dictionary(list.map { ($0.id, $0.version) }, uniquingKeysWith: { a, _ in a })
    }

    func applySettings(_ s: AppSettings) {
        data.settings = s
        L10n.shared.apply(setting: s.locale)
        UserDefaults.standard.set(s.theme, forKey: "PaperplaneTheme")
        Appearance.apply(s.theme)
    }

    func updateSystemInfo(_ i: SystemInfo) { systemInfo = i }

    func reloadToolchains() async {
        guard let api else { return }
        if let t = try? await api.toolchains() { data.toolchains = t }
        if let k = try? await api.toolchainKinds() { data.toolchainKinds = k }
    }

    func reloadPlugins() async {
        guard let api else { return }
        if let pl = try? await api.plugins() { setPlugins(pl) }
        if let cs = try? await api.contributions() { data.contributions = cs }
        if let k = try? await api.toolchainKinds() { data.toolchainKinds = k }
    }

    // MARK: 事件分发

    private func handle(event e: DaemonEvent) {
        bus.raw.send(e)
        switch e.type {
        case EventType.hello:
            if let info = try? e.decode(SystemInfo.self) { systemInfo = info }
            connection = .connected
            Task { await refreshAll() }
        case EventType.workspaces:
            if let ws = try? e.decode([Workspace].self) { data.applyWorkspaces(ws) }
        case EventType.project:
            if let p = try? e.decode(ProjectSummary.self) {
                data.projects[p.id] = p
                bus.projectChanged.send(p.id)
            }
        case EventType.projectRemoved:
            if let id = e.data?["id"]?.stringValue {
                data.projects.removeValue(forKey: id)
                if nav.openProjectId == id { nav.openProjectId = nil }
                EditorWindows.shared.requestClose(projectId: id)
            }
        case EventType.run:
            if let r = try? e.decode(Run.self) {
                runs.runs[r.id] = r
                ResourceStore.shared.runChanged(r)
                if !r.isActive {
                    runs.prune()
                    ResourceStore.shared.prune(keep: Set(runs.runs.keys))
                }
            }
        case EventType.task:
            if let t = try? e.decode(TaskInfo.self) { taskChanged(t) }
        case EventType.toolchains:
            bus.toolchainsChanged.send()
            Task { await reloadToolchains() }
        case EventType.plugins:
            IconStore.shared.clearPluginIcons()
            bus.pluginsChanged.send()
            Task { await reloadPlugins() }
        case EventType.settings:
            if let s = try? e.decode(AppSettings.self) { applySettings(s) }
        case EventType.notify:
            if let n = try? e.decode(NotifyParams.self) { toasts.show(n.level, n.title, message: n.message) }
        case EventType.prompt:
            if let p = try? e.decode(Prompt.self) { PromptPresenter.present(p) }
        case EventType.promptDone:
            if let id = e.data?["id"]?.stringValue { PromptPresenter.dismiss(id) }
        case EventType.vcs:
            if let v = try? e.decode(VCSChangedEvent.self) { bus.vcsChanged.send(v) }
        case EventType.stats:
            if let briefs = try? e.decode([RunBrief].self) { applyStats(briefs) }
        default:
            break
        }
    }

    /// stats 每 2 秒一次：数值与趋势进 ResourceStore（每个运行一个可观察对象，只重绘对应的小视图），
    /// 工程摘要只在地址变化时更新，不再每 2 秒重建卡片网格
    private func applyStats(_ briefs: [RunBrief]) {
        ResourceStore.shared.apply(briefs)
        runs.applyStats(briefs)
        let byId = Dictionary(briefs.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        for (pid, var p) in data.projects where p.runs.contains(where: { r in byId[r.id].map { !$0.urls.isEmpty && $0.urls != r.urls } ?? false }) {
            p.runs = p.runs.map { r in
                guard let b = byId[r.id], !b.urls.isEmpty else { return r }
                var n = r
                n.urls = b.urls
                return n
            }
            data.projects[pid] = p
        }
    }

    private func taskChanged(_ t: TaskInfo) {
        runs.tasks[t.id] = t
        guard !t.isRunning, trackedTasks.remove(t.id) != nil else { return }
        switch t.state {
        case TaskState.succeeded:
            let msg = t.result?["message"]?.stringValue
            if let url = t.result?["openUrl"]?.stringValue, !url.isEmpty { Actions.openURL(url) }
            if let msg, !msg.isEmpty { toasts.show("success", t.title, message: msg) }
            if t.result?["reimport"]?.boolValue == true, let pid = t.projectId { Actions.reimport(pid) }
            else if t.result?["sync"]?.boolValue == true, let pid = t.projectId { Actions.sync(pid, moduleId: t.moduleId) }
        case TaskState.failed:
            toasts.show("error", t.title, message: t.error, actionTitle: tr("task.viewLog")) { Actions.showTaskLog(t.id) }
        default:
            break
        }
        runs.prune()
    }

    /// 记下本界面发起的任务，结束时提示
    func track(_ task: TaskInfo) {
        runs.tasks[task.id] = task
        trackedTasks.insert(task.id)
    }

    // MARK: 通用执行

    /// 执行一个 API 调用，失败时弹提示
    func perform(_ title: String? = nil, _ op: @escaping (APIClient) async throws -> Void) {
        Task {
            do {
                try await op(try client())
            } catch {
                toasts.error(error, title: title)
            }
        }
    }

    // MARK: 退出

    /// 退出界面；stopAll 时同时让 daemon 停止全部服务并退出
    func quit(stopServices: Bool) {
        // 编辑窗口有未保存的文件：先让它们提示，不退出也不停服务
        guard EditorWindows.shared.prepareForQuit() else {
            toasts.show("warning", tr("editorWindow.unsaved"))
            return
        }
        events.stop()
        guard stopServices, let api else { NSApp.terminate(nil); return }
        Task {
            try? await api.shutdown(stopRuns: true)
            NSApp.terminate(nil)
        }
    }
}

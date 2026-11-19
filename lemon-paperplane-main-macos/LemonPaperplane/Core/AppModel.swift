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

    /// 本机内核与事件流的连接状态（远程服务器连不上另见 servers.phase）
    @Published private(set) var connection: Connection = .connecting
    /// 当前服务器的 system.info
    @Published private(set) var systemInfo: SystemInfo?

    /// 当前服务器的接口：本机时是本机内核，远程时是本机内核里的网关端口（形式一样）
    private(set) var api: APIClient?
    /// 本机内核（网关在它里面）：连着远程服务器时也保留，gateway.* 与「退出并停止全部服务」用它
    private(set) var localAPI: APIClient?
    private(set) var localInfo: SystemInfo?

    let servers = ServerStore()
    let data = DataStore()
    let runs = RunStore()
    let terminals = TerminalStore()
    let nav = NavStore()
    let dialogs = DialogStore()
    let toasts = ToastStore()
    let bus = EventBus()

    private let daemon = DaemonManager(appBundleURL: Bundle.main.bundleURL)
    private let events = EventStream()
    /// 本界面发起的任务：结束时给出提示
    private var trackedTasks: Set<String> = []
    private var started = false
    /// 每次切换服务器 +1：切换前发出的请求回来时已过期，丢弃结果
    private var generation = 0
    /// 本界面开着的网关（换服务器或换密钥后关掉旧的，连同它的端口转发）
    private var openedGatewayId: String?

    private init() {
        nav.serverKey = servers.currentId ?? ""
    }

    // MARK: 连接

    func start() {
        guard !started else { return }
        started = true
        Appearance.apply(UserDefaults.standard.string(forKey: "PaperplaneTheme"))
        connect()
    }

    /// 连上本机内核（必要时拉起），再按记住的服务器接上本机或远程
    func connect() {
        connection = .connecting
        Task {
            do {
                let (client, info) = try await daemon.connect(locale: L10n.shared.systemLocale)
                localAPI = client
                localInfo = info
                if info.plugins == 0 {
                    toasts.show("danger", tr("app.noPlugins"), message: info.executable)
                }
                await activateCurrent()
            } catch {
                connection = .failed(error.localizedDescription)
            }
        }
    }

    // MARK: 服务器

    /// 当前服务器的路径规则（Mac 连 Windows 服务器时是 C:\…）
    var serverPath: ServerPath { Format.serverPath }
    var isRemote: Bool { servers.isRemote }

    /// 切换到某台服务器（nil = 本机）：所有数据重新加载。没能切换（本机内核没连上、编辑窗口有未保存）时返回 false
    @discardableResult
    func switchServer(to id: String?) -> Bool {
        if id == servers.currentId, servers.phase == .ready, api != nil { return true }
        if id != nil, localAPI == nil {
            toasts.show("warning", tr("server.localRequired"))
            return false
        }
        // 编辑窗口打开的是这台服务器上的文件：有未保存的先让它们提示，不切换
        guard EditorWindows.shared.prepareForQuit() else {
            toasts.show("warning", tr("editorWindow.unsaved"))
            return false
        }
        servers.setCurrent(id)
        resetForServerSwitch()
        Task { await activateCurrent() }
        return true
    }

    /// 重试连接当前服务器（连不上页面的「重试」）
    func reconnectServer() {
        guard localAPI != nil else { connect(); return }
        Task { await activateCurrent() }
    }

    /// 换服务器前清掉上一台的数据与界面状态
    private func resetForServerSwitch() {
        generation += 1
        events.stop()
        api = nil
        servers.gatewayId = nil
        trackedTasks.removeAll()
        PopupController.shared.dismissAll()
        dialogs.dismissAll()
        data.reset()
        runs.reset()
        terminals.reset()
        ResourceStore.shared.reset()
        IconStore.shared.clearPluginIcons()
        nav.serverKey = servers.currentId ?? ""
        nav.resetForServerSwitch()
    }

    /// 按 servers.currentId 接上（本机：直接用本机内核；远程：本机内核 gateway.open）
    private func activateCurrent() async {
        generation += 1
        let gen = generation
        events.stop()
        if let s = servers.current {
            await openRemote(s, gen: gen)
            return
        }
        guard let local = localAPI else { return }
        closeOpenedGateway(except: nil)
        servers.phase = .ready
        servers.gatewayId = nil
        use(local, info: localInfo)
        connection = .connected
        startEvents(local)
        await refreshAll()
    }

    private func openRemote(_ s: SavedServer, gen: Int) async {
        servers.phase = .connecting
        api = nil
        guard let local = localAPI else {
            servers.phase = .failed(ServerFailure(kind: .localDown))
            return
        }
        guard let token = servers.token(s.id), !token.isEmpty else {
            servers.phase = .failed(ServerFailure(kind: .unauthorized))
            return
        }
        do {
            let gw = try await local.gatewayOpen(url: s.url, fingerprint: s.fingerprint, trusted: s.trusted, token: token)
            guard gen == generation else { return }
            closeOpenedGateway(except: gw.id)
            openedGatewayId = gw.id
            servers.gatewayId = gw.id
            let client = APIClient(baseURL: gw.baseURL, token: gw.token)
            use(client, info: gw.info)
            if let i = gw.info { servers.updateInfo(s.id, os: i.os, hostname: i.hostname) }
            servers.phase = .ready
            connection = .connected
            startEvents(client)
            await refreshAll()
        } catch {
            guard gen == generation else { return }
            servers.phase = .failed(ServerFailure(error))
        }
    }

    private func closeOpenedGateway(except keep: String?) {
        guard let old = openedGatewayId, old != keep else { return }
        openedGatewayId = nil
        guard let local = localAPI else { return }
        Task { try? await local.gatewayClose(old) }
    }

    private func use(_ client: APIClient, info: SystemInfo?) {
        api = client
        systemInfo = info
        applyPathRules(info)
    }

    /// 路径缩写与拼接按当前服务器（system.info 的 home、pathSeparator）
    private func applyPathRules(_ info: SystemInfo?) {
        let home = info?.home ?? ""
        Format.serverHome = home.isEmpty ? (isRemote ? "" : NSHomeDirectory()) : home
        let sep = info?.pathSeparator ?? ""
        Format.serverPath = ServerPath(sep: sep.isEmpty ? "/" : sep)
    }

    /// 需要已连接的 API；没连上时抛出可展示的错误
    func client() throws -> APIClient {
        if let api { return api }
        throw APIError(code: APIError.codeNetwork, message: tr("connection.notConnected"))
    }

    private func startEvents(_ client: APIClient) {
        let gen = generation
        events.start(client: client) { [weak self] update in
            Task { @MainActor in
                // 换了服务器后，上一台事件流里还没处理完的消息丢弃
                guard let self, gen == self.generation else { return }
                self.handle(update)
            }
        }
    }

    private func handle(_ update: EventStream.Update) {
        switch update {
        case .connected:
            if connection != .connected { connection = .connected }
            // 远程服务器恢复（事件流经网关重新连上）
            if case .failed = servers.phase, api != nil { servers.phase = .ready }
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
        let gen = generation
        Task {
            defer { recovering = false }
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            guard connection != .connected, gen == generation else { return }
            if servers.isRemote {
                await recoverRemote(gen: gen)
                return
            }
            guard let (client, info) = try? await daemon.connect(locale: L10n.shared.systemLocale) else { return }
            guard gen == generation else { return }
            if client.baseURL != localAPI?.baseURL || client.token != localAPI?.token {
                localAPI = client
                localInfo = info
                use(client, info: info)
                startEvents(client)
            }
        }
    }

    /// 连着远程服务器时事件流断了：先看本机内核（网关在它里面），再经网关问服务器。
    /// 经网关收到 403 remote_unauthorized / 502 是服务器那一侧的问题，不能当成本机令牌失效去重启本机内核。
    private func recoverRemote(gen: Int) async {
        var localRestarted = false
        if let local = localAPI, (try? await local.systemInfo(timeout: 3)) != nil {
            // 本机内核正常
        } else if let (client, info) = try? await daemon.connect(locale: L10n.shared.systemLocale) {
            localRestarted = client.baseURL != localAPI?.baseURL || client.token != localAPI?.token
            if localRestarted { openedGatewayId = nil }
            localAPI = client
            localInfo = info
        } else {
            guard gen == generation else { return }
            servers.phase = .failed(ServerFailure(kind: .localDown))
            return
        }
        guard gen == generation, let s = servers.current else { return }
        // 本机内核重启过：网关跟着没了，重新 open
        if localRestarted {
            await reopenRemote(s)
            return
        }
        guard let api else { return }
        do {
            _ = try await api.systemInfo(timeout: 10)
            // 通了：事件流自己会重连
        } catch let e as APIError {
            guard gen == generation else { return }
            if e.code == APIError.codeNetwork || e.code == APIError.codeUnauthorized {
                // 网关端口没了（被关掉）或本机令牌对不上：重新 open 一个
                await reopenRemote(s)
            } else if e.isRemoteFailure {
                servers.phase = .failed(ServerFailure(e))
                // 密钥被吊销：不再重试，等用户重新登录
                if e.code == APIError.codeRemoteUnauthorized { events.stop() }
            }
        } catch {}
    }

    private func reopenRemote(_ s: SavedServer) async {
        generation += 1
        let gen = generation
        events.stop()
        await openRemote(s, gen: gen)
    }

    /// 重新拉取全部状态（启动、重连后的 hello）
    func refreshAll() async {
        guard let api else { return }
        let gen = generation
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
        async let terms = try? api.terminalList()

        let r = await (ws, ps, rs, ts, st, cs, pl, kinds, tcs, prompts, terms)
        // 等结果期间换了服务器：这批数据属于上一台，丢弃
        guard gen == generation, api === self.api else { return }
        if let ws = r.0 { data.applyWorkspaces(ws) }
        if let ps = r.1 { data.projects = Dictionary(ps.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a }) }
        if let rs = r.2 { runs.runs = Dictionary(rs.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a }) }
        if let ts = r.3 { runs.tasks = Dictionary(ts.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a }) }
        if let st = r.4 { applySettings(st) }
        if let cs = r.5 { data.contributions = cs }
        if let pl = r.6 { setPlugins(pl) }
        if let k = r.7 { data.toolchainKinds = k }
        if let t = r.8 { data.toolchains = t }
        if let prompts = r.9 { for p in prompts { PromptPresenter.present(p) } }
        if let terms = r.10 { terminals.list = terms }
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
        // 界面语言与主题跟着本机走：远程服务器的设置不改本机界面
        guard !isRemote else { return }
        L10n.shared.apply(setting: s.locale)
        UserDefaults.standard.set(s.theme, forKey: "PaperplaneTheme")
        Appearance.apply(s.theme)
    }

    func updateSystemInfo(_ i: SystemInfo) {
        systemInfo = i
        if !isRemote { localInfo = i }
    }

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
            if let info = try? e.decode(SystemInfo.self) {
                systemInfo = info
                applyPathRules(info)
                if !isRemote { localInfo = info }
            }
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
        case EventType.terminals:
            if let list = try? e.decode([TerminalInfo].self) { terminals.list = list }
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
        let gateway = openedGatewayId
        let local = localAPI
        Task {
            // 关掉本界面开的网关（连同端口转发）；本机内核卡住时不等
            if let gateway, let local { try? await local.send("gateway.close", ["id": .string(gateway)], timeout: 2) }
            // 只停本机：远程服务器上的服务不受界面退出影响（远程也不允许调 system.shutdown）
            if stopServices, let local { try? await local.shutdown(stopRuns: true) }
            NSApp.terminate(nil)
        }
    }
}

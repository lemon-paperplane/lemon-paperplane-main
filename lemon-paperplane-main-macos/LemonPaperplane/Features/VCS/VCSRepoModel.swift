import AppKit
import Combine
import PaperplaneKit
import SwiftUI

/// 版本控制标签页的子页
enum VCSPage: Hashable {
    case changes, log, branches, stashes, conflicts
    case plugin(String) // ViewInfo.key
}

/// 一个工程的版本控制会话：仓库列表、当前仓库、当前子页。按工程缓存，切换标签或语言重建视图时状态不丢。
@MainActor
final class VCSSession: ObservableObject {
    private static var sessions: [String: VCSSession] = [:]

    static func of(_ projectId: String) -> VCSSession {
        if let s = sessions[projectId] { return s }
        let s = VCSSession(projectId: projectId)
        sessions[projectId] = s
        return s
    }

    let projectId: String
    @Published private(set) var roots: [VCSRoot] = []
    @Published private(set) var rootsLoaded = false
    @Published var rootsError: String?
    @Published var selectedRoot: String? {
        didSet { if let r = selectedRoot { UserDefaults.standard.set(r, forKey: "PaperplaneVCSRoot:" + projectId) } }
    }
    @Published var page: VCSPage = .changes
    /// 视图是否可见（不可见时不响应外部刷新）
    var visible = false

    private var repos: [String: VCSRepo] = [:]
    private var bag = Set<AnyCancellable>()
    private var lastProjectRefresh = Date.distantPast

    private init(projectId: String) {
        self.projectId = projectId
        let bus = AppModel.shared.bus
        bus.vcsChanged
            .filter { [projectId] in $0.projectId == projectId }
            .sink { [weak self] e in self?.vcsChanged(root: e.root) }
            .store(in: &bag)
        // 卡片摘要变化（内核每 30 秒对一次）：可见时顺带刷新状态，能发现编辑器里做的外部修改
        bus.projectChanged
            .filter { [projectId] in $0 == projectId }
            .sink { [weak self] _ in self?.externalRefresh() }
            .store(in: &bag)
        NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)
            .sink { [weak self] _ in self?.externalRefresh(force: true) }
            .store(in: &bag)
    }

    var repo: VCSRepo? { selectedRoot.map { repo(for: $0) } }

    func repo(for root: String) -> VCSRepo {
        if let r = repos[root] { return r }
        let name = roots.first { $0.path == root }.map { $0.relPath == "." || $0.relPath.isEmpty ? VCSPath.name($0.path) : $0.relPath }
            ?? VCSPath.name(root)
        let r = VCSRepo(session: self, root: root, name: name)
        repos[root] = r
        return r
    }

    /// 重新扫描工程内的仓库（vcs.roots 会更新工程记录）
    func loadRoots() async {
        do {
            let list = try await AppModel.shared.client().vcsRoots(projectId: projectId)
            roots = list
            rootsError = nil
            if selectedRoot == nil || !list.contains(where: { $0.path == selectedRoot }) {
                let saved = UserDefaults.standard.string(forKey: "PaperplaneVCSRoot:" + projectId)
                selectedRoot = list.first { $0.path == saved }?.path ?? list.first?.path
            }
        } catch {
            rootsError = error.localizedDescription
        }
        rootsLoaded = true
    }

    private func vcsChanged(root: String?) {
        if let root, let r = repos[root] {
            r.invalidate()
        } else {
            repos.values.forEach { $0.invalidate() }
        }
    }

    private func externalRefresh(force: Bool = false) {
        guard visible, let repo else { return }
        if !force && Date().timeIntervalSince(lastProjectRefresh) < 5 { return }
        lastProjectRefresh = Date()
        repo.scheduleRefresh()
    }

    /// 打开这个工程的版本控制页并切到指定仓库与子页（从工作区总览跳过来时用）
    func reveal(root: String, page: VCSPage) {
        if roots.contains(where: { $0.path == root }) || !rootsLoaded { selectedRoot = root }
        self.page = page
        if !visible || AppModel.shared.nav.openProjectId != projectId {
            AppModel.shared.nav.openProject(projectId, tab: .vcs)
        }
    }

    /// 在这个工程的仓库里初始化 Git
    func initRepository() {
        AppModel.shared.perform(tr("vcs.init.failed")) { [weak self] api in
            guard let self else { return }
            let p = try await api.project(self.projectId)
            let r: VCSOpResult = try await api.vcsRun(VCSOp.initRepo, projectId: self.projectId, root: p.path, args: VCSInitArgs())
            if !r.ok { throw APIError(code: APIError.codeUserError, message: r.message ?? tr("vcs.init.failed")) }
            await self.loadRoots()
        }
    }
}

/// 页内提示条（操作失败或需要下一步时）
struct VCSNotice: Identifiable {
    struct Action: Identifiable {
        let id = UUID()
        let title: String
        var primary = false
        var danger = false
        let run: () -> Void
    }

    let id = UUID()
    var tone: String
    var title: String
    var message: String?
    var output: String?
    var taskId: String?
    var actions: [Action] = []
}

/// 正在进行的远程/长操作（工具栏显示转圈与「输出」）
struct VCSActivity: Equatable {
    let op: String
    let title: String
    let started = Date()
}

/// 一个仓库：状态、分支、储藏；所有写操作经 run(...) 统一处理结果。
@MainActor
final class VCSRepo: ObservableObject {
    unowned let session: VCSSession
    let root: String
    let name: String
    var projectId: String { session.projectId }

    @Published private(set) var status: VCSStatus?
    @Published private(set) var statusError: String?
    @Published private(set) var branches: VCSBranches?
    @Published private(set) var tags: [VCSTag] = []
    @Published private(set) var stashes: [VCSStash] = []
    @Published private(set) var remotes: [VCSRemote] = []
    @Published var notice: VCSNotice?
    @Published private(set) var activity: VCSActivity?
    /// 每次仓库变化加一：日志、储藏等子页据此重新加载
    @Published private(set) var revision = 0

    let changes: VCSChangesModel
    private(set) lazy var log = VCSLogModel(repo: self)

    private var refreshTask: Task<Void, Never>?
    private var identityChecked = false
    private var cachedModuleId: String??

    init(session: VCSSession, root: String, name: String) {
        self.session = session
        self.root = root
        self.name = name
        self.changes = VCSChangesModel(root: root)
    }

    func api() throws -> APIClient { try AppModel.shared.client() }

    // MARK: 读

    /// 合并短时间内的多次刷新
    func scheduleRefresh() {
        refreshTask?.cancel()
        refreshTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 120_000_000)
            guard !Task.isCancelled else { return }
            await self?.refresh()
        }
    }

    /// 仓库被改动（vcs.changed）：刷新状态，通知子页
    func invalidate() {
        revision += 1
        scheduleRefresh()
    }

    func refresh() async {
        await loadStatus()
        if branches != nil { await loadBranches() }
        if !stashes.isEmpty || (status?.stashCount ?? 0) > 0 { await loadStashes() }
    }

    func loadStatus() async {
        do {
            let s = try await api().vcsStatus(projectId: projectId, root: root)
            if s != status {
                // HEAD 或分支变了（终端里提交、切分支等外部操作）：日志等子页也要刷新
                let moved = status != nil && (status?.headHash != s.headHash || status?.branch != s.branch)
                status = s
                changes.sync(with: s)
                if moved { revision += 1 }
            }
            statusError = nil
        } catch is CancellationError {
        } catch {
            statusError = error.localizedDescription
        }
    }

    /// 用工作区总览（vcs.overview）取到的状态更新：总览不统计增删行数，沿用已有的
    func apply(overviewStatus s: VCSStatus) {
        var s = s
        if let old = status {
            let byPath = Dictionary(old.files.map { ($0.path, $0) }, uniquingKeysWith: { a, _ in a })
            for i in s.files.indices {
                if let o = byPath[s.files[i].path], o.status == s.files[i].status {
                    s.files[i].additions = o.additions
                    s.files[i].deletions = o.deletions
                    s.files[i].binary = o.binary
                }
            }
        }
        guard s != status else { return }
        let moved = status != nil && (status?.headHash != s.headHash || status?.branch != s.branch)
        status = s
        statusError = nil
        changes.sync(with: s)
        if moved { revision += 1 }
    }

    func loadBranches() async {
        do {
            let b = try await api().vcsBranches(projectId: projectId, root: root)
            if b != branches { branches = b }
        } catch {}
    }

    func loadTags() async {
        if let t: VCSTags = try? await api().vcs(VCSOp.tags, projectId: projectId, root: root) { tags = t.tags }
    }

    func loadStashes() async {
        if let s = try? await api().vcsStashList(projectId: projectId, root: root) { stashes = s.stashes }
    }

    func loadRemotes() async {
        if let r: VCSRemotes = try? await api().vcs(VCSOp.remotes, projectId: projectId, root: root) { remotes = r.remotes }
    }

    /// 当前分支名（分离头时为短哈希）
    var headTitle: String {
        guard let s = status else { return "" }
        if !s.branch.isEmpty { return s.branch }
        return VCSStyle.shortHash(s.headHash ?? s.head)
    }

    var currentBranch: String? {
        guard let s = status, !s.branch.isEmpty, !s.detached else { return nil }
        return s.branch
    }

    /// project.action 需要模块：取工程第一个模块（插件动作与模块无关）
    func firstModuleId() async throws -> String {
        if let c = cachedModuleId { return c ?? "" }
        let p = try await api().project(projectId)
        cachedModuleId = .some(p.modules.first?.id)
        return p.modules.first?.id ?? ""
    }

    // MARK: 写

    /// 执行写操作并按结果给出提示或下一步按钮。API 错误弹提示，返回 nil。
    @discardableResult
    func run<A: Encodable>(_ op: String, _ args: A, title: String, long: Bool = false, quietSuccess: Bool = false,
                           extra: ((VCSOpResult) -> [VCSNotice.Action])? = nil) async -> VCSOpResult? {
        do {
            return try await runThrowing(op, args, title: title, long: long, quietSuccess: quietSuccess, extra: extra)
        } catch {
            showError(error, title: title)
            return nil
        }
    }

    /// API 错误：内核带回插件的 reason 时给下一步按钮，否则轻提示
    func showError(_ error: Error, title: String) {
        guard let e = error as? APIError, let reason = e.reason else {
            AppModel.shared.toasts.error(error, title: title)
            return
        }
        var n = VCSNotice(tone: "danger", title: e.message, message: title)
        switch reason {
        case "noRemote":
            n.tone = "warning"
            n.actions = [.init(title: tr("vcs.remote.add"), primary: true) { [weak self] in
                guard let self else { return }
                self.notice = nil
                VCSRemoteActions.addRemote(repo: self)
            }]
        case VCSReason.identityMissing:
            n.actions = [.init(title: tr("vcs.identity.set"), primary: true) { [weak self] in
                guard let self else { return }
                self.notice = nil
                VCSIdentityDialog.present(repo: self, missing: []) {}
            }]
        default:
            AppModel.shared.toasts.error(error, title: title)
            return
        }
        notice = n
    }

    /// 同上，但 API 错误抛出（对话框里调用，错误显示在对话框内）
    @discardableResult
    func runThrowing<A: Encodable>(_ op: String, _ args: A, title: String, long: Bool = false, quietSuccess: Bool = false,
                                   extra: ((VCSOpResult) -> [VCSNotice.Action])? = nil) async throws -> VCSOpResult {
        if long { activity = VCSActivity(op: op, title: title) }
        defer { if long { activity = nil } }
        // 先在主线程编码成 JSONValue（Sendable），再跨到网络调用
        let input = try JSONValue(encoding: args)
        let r: VCSOpResult = try await api().vcsRun(op, projectId: projectId, root: root, args: input)
        present(r, op: op, title: title, quietSuccess: quietSuccess, extra: extra, retry: { [weak self] in
            self?.perform(op, args, title: title, long: long, quietSuccess: quietSuccess, extra: extra)
        })
        scheduleRefresh()
        return r
    }

    /// 后台执行（按钮直接触发的）
    func perform<A: Encodable>(_ op: String, _ args: A, title: String, long: Bool = false, quietSuccess: Bool = false,
                               extra: ((VCSOpResult) -> [VCSNotice.Action])? = nil, then: ((VCSOpResult) -> Void)? = nil) {
        Task {
            if let r = await run(op, args, title: title, long: long, quietSuccess: quietSuccess, extra: extra) { then?(r) }
        }
    }

    /// 结果 → 提示。成功：轻提示；失败或需要下一步：页内提示条。
    func present(_ r: VCSOpResult, op: String, title: String, quietSuccess: Bool = false,
                 extra: ((VCSOpResult) -> [VCSNotice.Action])? = nil, retry: (() -> Void)? = nil) {
        let reason = r.reason ?? (r.needsAuth ? VCSReason.needsAuth : (r.rejected ? VCSReason.rejected : nil))
        if r.ok && r.conflicts.isEmpty {
            notice = nil
            if quietSuccess { return }
            let msg = r.message?.isEmpty == false ? r.message : nil
            if reason == VCSReason.upToDate || reason == VCSReason.noChanges {
                AppModel.shared.toasts.show("info", msg ?? title)
            } else {
                AppModel.shared.toasts.show("success", title, message: msg)
            }
            return
        }
        var n = VCSNotice(tone: r.ok ? "warning" : "danger", title: r.message?.isEmpty == false ? r.message! : title,
                          message: nil, output: r.output, taskId: r.taskId)
        if n.title != title { n.message = title }
        var acts = extra?(r) ?? []
        let goConflicts = VCSNotice.Action(title: tr("vcs.action.resolveConflicts"), primary: true) { [weak self] in
            guard let self else { return }
            self.notice = nil
            self.session.reveal(root: self.root, page: .conflicts)
        }
        switch reason {
        case VCSReason.conflicts, VCSReason.autostashConflict, VCSReason.unresolvedConflicts:
            n.tone = "warning"
            if !r.conflicts.isEmpty { n.message = r.conflicts.prefix(8).joined(separator: "\n") }
            acts.insert(goConflicts, at: 0)
        case VCSReason.rejected:
            n.tone = "warning"
            acts.append(.init(title: tr("vcs.pull"), primary: acts.isEmpty) { [weak self] in
                guard let self else { return }
                self.notice = nil
                VCSPullDialog.present(repo: self)
            })
        case VCSReason.needsAuth, VCSReason.hostKey, VCSReason.network, VCSReason.locked, VCSReason.remoteRejected:
            if let retry {
                acts.append(.init(title: tr("vcs.retry"), primary: acts.isEmpty) { [weak self] in
                    self?.notice = nil
                    retry()
                })
            }
        case VCSReason.noUpstream:
            acts.append(.init(title: tr("vcs.branch.setUpstream"), primary: acts.isEmpty) { [weak self] in
                guard let self else { return }
                self.notice = nil
                VCSBranchActions.setUpstream(repo: self, branch: self.currentBranch ?? "")
            })
        case VCSReason.identityMissing:
            acts.append(.init(title: tr("vcs.identity.set"), primary: true) { [weak self] in
                guard let self else { return }
                self.notice = nil
                VCSIdentityDialog.present(repo: self, missing: []) {}
            })
        case VCSReason.emptyCommit:
            n.tone = "warning"
            acts.append(.init(title: tr("vcs.op.skip"), primary: acts.isEmpty) { [weak self] in
                self?.notice = nil
                self?.perform(VCSOp.skip, VCSNoArgs(), title: tr("vcs.op.skip"))
            })
        case VCSReason.localChanges, VCSReason.untrackedOverwritten:
            if !r.files.isEmpty {
                n.message = r.files.prefix(8).joined(separator: "\n") + (r.files.count > 8 ? "\n…" : "")
            }
        default:
            break
        }
        n.actions = acts
        notice = n
    }

    /// 提交前确认已设置 user.name / user.email（插件会报 identityMissing，但内核 HTTP 错误里没有 reason，这里先查）
    func ensureIdentity(then: @escaping () -> Void) {
        if identityChecked { then(); return }
        Task {
            do {
                let cfg = try await api().vcsConfigGet(projectId: projectId, root: root, keys: ["user.name", "user.email"])
                let missing = ["user.name", "user.email"].filter { (cfg.values[$0] ?? "").trimmingCharacters(in: .whitespaces).isEmpty }
                if missing.isEmpty {
                    identityChecked = true
                    then()
                } else {
                    VCSIdentityDialog.present(repo: self, missing: missing) {
                        self.identityChecked = true
                        then()
                    }
                }
            } catch {
                then() // 读不到配置就交给插件判断
            }
        }
    }

    /// 正在运行的本仓库任务（远程操作的控制台）
    var runningTask: TaskInfo? {
        AppModel.shared.runs.activeTasks.first { $0.kind == "vcs" && $0.projectId == projectId }
    }

    func showOutput(taskId: String?) {
        if let id = taskId ?? runningTask?.id { Actions.showTaskLog(id) }
    }
}

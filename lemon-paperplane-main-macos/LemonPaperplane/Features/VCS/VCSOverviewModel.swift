import AppKit
import Combine
import PaperplaneKit
import SwiftUI

// 工作区 Git 总览（像 VS Code 打开文件夹后的多仓库源代码管理）：工作区里全部工程的全部仓库，挨个 pull / push / commit。
// 每个仓库复用工程版本控制页的 VCSRepo（同一个仓库在两处看到的是同一份状态、勾选与提交信息）。

@MainActor
final class VCSOverviewModel: ObservableObject {
    private static var models: [String: VCSOverviewModel] = [:]

    static func of(_ workspaceId: String) -> VCSOverviewModel {
        if let m = models[workspaceId] { return m }
        let m = VCSOverviewModel(workspaceId: workspaceId)
        models[workspaceId] = m
        return m
    }

    /// 一个仓库（来自 vcs.overview；状态放在对应的 VCSRepo 里）
    struct Entry: Identifiable, Hashable {
        let root: String
        let projectId: String
        let projectName: String
        let relPath: String
        let name: String
        var error: String?
        var id: String { root }

        /// 仓库名之外的说明：工程名（与仓库名不同时）+ 相对路径
        var subtitle: String? {
            var parts: [String] = []
            if projectName != name { parts.append(projectName) }
            if !relPath.isEmpty && relPath != "." && relPath != name { parts.append(relPath) }
            return parts.isEmpty ? nil : parts.joined(separator: " / ")
        }
    }

    let workspaceId: String
    @Published private(set) var entries: [Entry] = []
    @Published private(set) var loading = false
    @Published private(set) var loaded = false
    @Published private(set) var error: String?
    @Published var expanded: Set<String> = []
    @Published var showAllFiles: Set<String> = []
    @Published var query = ""
    @Published var onlyChanged: Bool {
        didSet { UserDefaults.standard.set(onlyChanged, forKey: "PaperplaneVCSOverviewOnlyChanged:" + workspaceId) }
    }
    @Published var batch: VCSBatch?

    /// 页面可见时每 30 秒刷新一次
    var visible = false {
        didSet {
            guard visible != oldValue else { return }
            if visible { startTimer() } else { timer?.invalidate(); timer = nil }
        }
    }

    private var known: Set<String> = []
    private var bag = Set<AnyCancellable>()
    private var repoBag = Set<AnyCancellable>()
    private var timer: Timer?
    private var reloadTask: Task<Void, Never>?

    private init(workspaceId: String) {
        self.workspaceId = workspaceId
        onlyChanged = UserDefaults.standard.bool(forKey: "PaperplaneVCSOverviewOnlyChanged:" + workspaceId)
        AppModel.shared.bus.vcsChanged
            .sink { [weak self] e in self?.vcsChanged(e) }
            .store(in: &bag)
        NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)
            .sink { [weak self] _ in
                guard let self, self.visible else { return }
                self.scheduleReload()
            }
            .store(in: &bag)
    }

    func repo(_ e: Entry) -> VCSRepo {
        let r = VCSSession.of(e.projectId).repo(for: e.root)
        r.changes.autoIncludeUntracked = true
        return r
    }

    static func hasChanges(_ s: VCSStatus?) -> Bool {
        guard let s else { return false }
        return !s.files.isEmpty || s.operation != nil || s.conflicts > 0
    }

    // MARK: 加载与刷新

    func reload() async {
        guard !loading else { return }
        loading = true
        defer { loading = false }
        do {
            let list = try await AppModel.shared.client().vcsOverview(workspaceId: workspaceId)
            var next: [Entry] = []
            for o in list {
                let e = Entry(root: o.root, projectId: o.projectId, projectName: o.projectName, relPath: o.relPath,
                              name: o.name.isEmpty ? VCSPath.name(o.root) : o.name, error: o.status == nil ? (o.error ?? "") : nil)
                next.append(e)
                if let st = o.status { repo(e).apply(overviewStatus: st) }
                // 第一次见到的仓库：有改动 / 冲突 / 进行中操作的默认展开
                if !known.contains(e.root) {
                    known.insert(e.root)
                    if Self.hasChanges(o.status) { expanded.insert(e.root) }
                }
            }
            if next != entries {
                entries = next
                observeRepos()
            }
            error = nil
            loaded = true
        } catch is CancellationError {
        } catch {
            self.error = error.localizedDescription
            loaded = true
        }
    }

    func scheduleReload() {
        reloadTask?.cancel()
        reloadTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 300_000_000)
            guard !Task.isCancelled else { return }
            await self?.reload()
        }
    }

    /// 任何一个仓库的状态变化都让工具栏统计、过滤重新计算
    private func observeRepos() {
        repoBag = []
        for e in entries {
            repo(e).$status.dropFirst()
                .sink { [weak self] _ in DispatchQueue.main.async { self?.objectWillChange.send() } }
                .store(in: &repoBag)
        }
    }

    /// 只刷新对应 root 的那一个仓库；不认识的仓库（新加的嵌套仓库等）整体重取
    private func vcsChanged(_ e: VCSChangedEvent) {
        guard loaded else { return }
        if let root = e.root, let entry = entries.first(where: { $0.root == root }) {
            repo(entry).scheduleRefresh()
        } else if entries.contains(where: { $0.projectId == e.projectId })
                    || AppModel.shared.data.projects[e.projectId]?.workspaceId == workspaceId {
            scheduleReload()
        }
    }

    private func startTimer() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.visible, self.batch?.running != true else { return }
                await self.reload()
            }
        }
    }

    // MARK: 统计与过滤

    struct Stats {
        var repos = 0
        var changed = 0
        var toPush = 0
        var toPull = 0
    }

    var stats: Stats {
        var s = Stats(repos: entries.count)
        for e in entries {
            let st = repo(e).status
            if Self.hasChanges(st) { s.changed += 1 }
            if (st?.ahead ?? 0) > 0 { s.toPush += 1 }
            if (st?.behind ?? 0) > 0 { s.toPull += 1 }
        }
        return s
    }

    func filtered(globalSearch: String) -> [Entry] {
        let q = query.trimmingCharacters(in: .whitespaces)
        let g = globalSearch.trimmingCharacters(in: .whitespaces)
        return entries.filter { e in
            if onlyChanged {
                let st = repo(e).status
                if !(Self.hasChanges(st) || (st?.ahead ?? 0) > 0 || (st?.behind ?? 0) > 0 || e.error != nil) { return false }
            }
            for t in [q, g] where !t.isEmpty {
                if !(e.name.localizedCaseInsensitiveContains(t) || e.projectName.localizedCaseInsensitiveContains(t)
                    || e.relPath.localizedCaseInsensitiveContains(t)) { return false }
            }
            return true
        }
    }

    func toggle(_ root: String) {
        if expanded.contains(root) { expanded.remove(root) } else { expanded.insert(root) }
    }

    // MARK: 批量

    func startBatch(_ kind: VCSBatch.Kind) {
        guard batch?.running != true else { return }
        let targets = entries.filter { e in
            guard e.error == nil, let st = repo(e).status else { return false }
            let tracking = st.upstream != nil && !st.upstreamGone && st.operation == nil
            switch kind {
            case .fetch: return true
            case .pull: return tracking && st.behind > 0 && st.conflicts == 0
            case .push: return tracking && st.ahead > 0
            }
        }
        guard !targets.isEmpty else {
            AppModel.shared.toasts.show("info", kind.emptyMessage)
            return
        }
        let names = targets.map { e in e.subtitle.map { "\(e.name)  (\($0))" } ?? e.name }
        Dialogs.confirm(title: kind.confirmTitle(targets.count),
                        message: names.prefix(20).joined(separator: "\n") + (names.count > 20 ? "\n…" : ""),
                        ok: kind.title) { [weak self] in
            self?.run(kind, targets)
        }
    }

    private func run(_ kind: VCSBatch.Kind, _ targets: [Entry]) {
        batch = VCSBatch(kind: kind, items: targets.map { VCSBatch.Item(root: $0.root, name: $0.name) }, running: true)
        Task {
            for (i, e) in targets.enumerated() {
                guard batch?.kind == kind else { return }
                batch?.items[i].state = .running
                let r = repo(e)
                do {
                    let res: VCSOpResult
                    switch kind {
                    case .fetch:
                        res = try await r.runThrowing(VCSOp.fetch, VCSFetchArgs(), title: tr("vcs.fetch"), long: true, quietSuccess: true)
                    case .pull:
                        res = try await r.runThrowing(VCSOp.pull, VCSPullArgs(mode: VCSPrefs.pullMode, autostash: VCSPrefs.pullAutostash ? true : nil),
                                                      title: tr("vcs.pull"), long: true, quietSuccess: true)
                    case .push:
                        res = try await r.runThrowing(VCSOp.push, VCSPushArgs(), title: tr("vcs.push"), long: true, quietSuccess: true)
                    }
                    let ok = res.ok && res.conflicts.isEmpty
                    batch?.items[i].state = ok ? .ok : .failed
                    batch?.items[i].message = res.message
                    if !ok { expanded.insert(e.root) }
                } catch {
                    // 没配置远程的仓库不算失败
                    let skipped = (error as? APIError)?.reason == "noRemote"
                    batch?.items[i].state = skipped ? .skipped : .failed
                    batch?.items[i].message = error.localizedDescription
                    if !skipped { expanded.insert(e.root) }
                }
            }
            batch?.running = false
            if let b = batch {
                let failed = b.items.filter { $0.state == .failed }.count
                AppModel.shared.toasts.show(failed == 0 ? "success" : "warning", b.kind.title,
                                            message: tr("vcs.overview.batchDone", b.items.count - failed, failed))
            }
        }
    }
}

/// 批量获取 / 拉取 / 推送的进度
struct VCSBatch {
    enum Kind {
        case fetch, pull, push

        var title: String {
            switch self {
            case .fetch: return tr("vcs.overview.fetchAll")
            case .pull: return tr("vcs.overview.pullAll")
            case .push: return tr("vcs.overview.pushAll")
            }
        }

        func confirmTitle(_ n: Int) -> String {
            switch self {
            case .fetch: return tr("vcs.overview.fetchAllTitle", n)
            case .pull: return tr("vcs.overview.pullAllTitle", n)
            case .push: return tr("vcs.overview.pushAllTitle", n)
            }
        }

        var emptyMessage: String {
            switch self {
            case .fetch: return tr("vcs.overview.noRepos")
            case .pull: return tr("vcs.overview.nothingToPull")
            case .push: return tr("vcs.overview.nothingToPush")
            }
        }
    }

    enum State { case pending, running, ok, failed, skipped }

    struct Item: Identifiable {
        let root: String
        let name: String
        var state: State = .pending
        var message: String?
        var id: String { root }
    }

    let kind: Kind
    var items: [Item]
    var running: Bool

    var done: Int { items.filter { $0.state != .pending && $0.state != .running }.count }
}

/// 单个仓库的快捷拉取 / 推送：有上游时直接按默认方式执行，否则弹对话框
@MainActor
enum VCSOverviewActions {
    static func pull(_ repo: VCSRepo) {
        guard let s = repo.status, s.upstream != nil, !s.upstreamGone else {
            VCSPullDialog.present(repo: repo)
            return
        }
        VCSRemoteActions.pull(repo: repo, args: VCSPullArgs(mode: VCSPrefs.pullMode, autostash: VCSPrefs.pullAutostash ? true : nil))
    }

    static func push(_ repo: VCSRepo) {
        guard let s = repo.status, s.upstream != nil, !s.upstreamGone, s.ahead > 0 else {
            VCSPushDialog.present(repo: repo)
            return
        }
        VCSRemoteActions.push(repo: repo, args: VCSPushArgs())
    }

    static func openInTerminal(_ path: String) {
        let terminal = URL(fileURLWithPath: "/System/Applications/Utilities/Terminal.app")
        NSWorkspace.shared.open([URL(fileURLWithPath: path)], withApplicationAt: terminal,
                                configuration: NSWorkspace.OpenConfiguration(), completionHandler: nil)
    }
}

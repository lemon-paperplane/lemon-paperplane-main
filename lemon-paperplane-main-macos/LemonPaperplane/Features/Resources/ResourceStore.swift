import Foundation
import PaperplaneKit

/// 一个运行的资源数据（趋势 + 当前值）。每个运行一个对象，只有显示它的小视图会随 stats 重绘。
@MainActor
final class RunResources: ObservableObject {
    struct Snapshot: Equatable {
        var series = ResourceSeries()
        var cpu: Double = 0
        var rssMB: Double = 0
        var peakRSSMB: Double = 0
        var procs: Int = 0
        var state = ""
        /// 采样时间（毫秒）；0 表示还没有数据
        var t: Int64 = 0
    }

    let runId: String
    @Published private(set) var snap = Snapshot()
    fileprivate var historyLoaded = false
    fileprivate var historyLoading = false

    init(runId: String) { self.runId = runId }

    fileprivate func apply(_ b: RunBrief) {
        var s = snap
        let t = b.t > 0 ? b.t : Int64(Date().timeIntervalSince1970 * 1000)
        s.series.append(ResourceSample(t: t, cpu: b.cpu, rssMB: b.rssMB))
        s.cpu = b.cpu
        s.rssMB = b.rssMB
        s.peakRSSMB = max(b.peakRSSMB, b.rssMB, s.peakRSSMB)
        s.procs = b.procs
        if !b.state.isEmpty { s.state = b.state }
        s.t = t
        if s != snap { snap = s }
    }

    fileprivate func apply(_ stats: RunStats) {
        var s = snap
        s.series.merge(stats.samples)
        s.peakRSSMB = max(stats.peakRSSMB, s.peakRSSMB)
        if !stats.state.isEmpty { s.state = stats.state }
        if let last = stats.samples.last, last.t >= s.t {
            s.cpu = RunState.isActive(stats.state) ? stats.cpu : 0
            s.rssMB = RunState.isActive(stats.state) ? stats.rssMB : 0
            s.t = last.t
        }
        if !stats.processes.isEmpty { s.procs = stats.processes.count }
        if s != snap { snap = s }
    }

    fileprivate func setState(_ state: String) {
        guard snap.state != state else { return }
        var s = snap
        s.state = state
        if !RunState.isActive(state) {
            s.cpu = 0
            s.rssMB = 0
            s.procs = 0
        }
        snap = s
    }

    /// 首次显示时用 run.stats 补历史（每个运行只取一次，之后靠 stats 事件追加）
    func loadHistoryIfNeeded() {
        guard !historyLoaded, !historyLoading, let api = AppModel.shared.api else { return }
        historyLoading = true
        Task {
            defer { historyLoading = false }
            if let st = try? await api.runStats(runId) {
                apply(st)
                historyLoaded = true
            }
        }
    }
}

/// 全部运行的资源数据与合计（「运行中」页顶部用）。
@MainActor
final class ResourceStore: ObservableObject {
    static let shared = ResourceStore()

    struct Totals: Equatable {
        var projects = 0
        var runs = 0
        var rssMB: Double = 0
        var cpu: Double = 0
    }

    @Published private(set) var totals = Totals()
    private var byRun: [String: RunResources] = [:]

    func resources(_ runId: String) -> RunResources {
        if let r = byRun[runId] { return r }
        let r = RunResources(runId: runId)
        byRun[runId] = r
        return r
    }

    /// 已有数据时取（不创建）
    func existing(_ runId: String) -> RunResources? { byRun[runId] }

    /// stats 事件（只含运行中的）
    func apply(_ briefs: [RunBrief]) {
        var t = Totals()
        var projects = Set<String>()
        for b in briefs {
            resources(b.id).apply(b)
            guard RunState.isActive(b.state) else { continue }
            t.runs += 1
            t.rssMB += b.rssMB
            t.cpu += b.cpu
            projects.insert(b.projectId ?? AppModel.shared.runs.runs[b.id]?.projectId ?? b.id)
        }
        t.projects = projects.count
        if t != totals { totals = t }
    }

    /// run.changed：结束后当前值归零，曲线保留
    func runChanged(_ r: Run) {
        byRun[r.id]?.setState(r.state)
        if !RunState.isActive(r.state), totals.runs > 0 {
            // 合计在下一次 stats 时重算；没有运行了就立即清零
            if !AppModel.shared.runs.runs.values.contains(where: \.isActive) { totals = Totals() }
        }
    }

    /// 资源面板轮询到的完整数据
    func apply(_ stats: RunStats) {
        resources(stats.runId).apply(stats)
    }

    /// 某工程运行中的内存合计（排序用）
    func memory(projectId: String) -> Double {
        AppModel.shared.runs.activeRuns(projectId: projectId).reduce(0) { $0 + (byRun[$1.id]?.snap.rssMB ?? 0) }
    }

    /// 只保留仍在运行列表里的运行（结束的保留最近若干个，跟 RunStore 一致）
    func prune(keep: Set<String>) {
        byRun = byRun.filter { keep.contains($0.key) }
    }
}

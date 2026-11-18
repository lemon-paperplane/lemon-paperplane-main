import Combine
import PaperplaneKit
import SwiftUI

/// 运行页控制台上方的「资源」面板（可收起）：内存与 CPU 趋势图、当前值/峰值/进程数/运行时长/端口、进程明细。
/// 展开且可见时每 2 秒取一次 run.stats；运行结束后取最后一次，保留曲线并标「已结束」。
@MainActor
final class RunStatsPoller: ObservableObject {
    @Published private(set) var stats: RunStats?
    private var timer: Timer?
    private var runId: String?
    private var inFlight = false

    func start(runId: String, active: Bool) {
        if self.runId != runId { stats = nil }
        self.runId = runId
        timer?.invalidate()
        timer = nil
        fetch()
        guard active else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.fetch() }
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    private func fetch() {
        guard !inFlight, let id = runId, let api = AppModel.shared.api else { return }
        inFlight = true
        Task {
            defer { inFlight = false }
            guard let s = try? await api.runStats(id), id == runId else { return }
            stats = s
            ResourceStore.shared.apply(s)
        }
    }
}

struct RunResourcePanel: View {
    let run: Run
    @ObservedObject private var res: RunResources
    @StateObject private var poller = RunStatsPoller()
    @AppStorage("PaperplaneResourcePanelOpen") private var open = true
    @AppStorage("PaperplaneProcessTree") private var treeMode = false
    @State private var hoverT: Int64?
    @State private var collapsed: Set<Int> = []
    @Environment(\.theme) private var theme

    init(run: Run) {
        self.run = run
        _res = ObservedObject(wrappedValue: ResourceStore.shared.resources(run.id))
    }

    var body: some View {
        let s = res.snap
        VStack(alignment: .leading, spacing: 0) {
            header(s)
            if open {
                VStack(alignment: .leading, spacing: M.md) {
                    charts(s)
                    facts(s)
                    processes
                }
                .padding(.horizontal, M.lg)
                .padding(.bottom, M.md)
            }
        }
        .background(theme.bgContent)
        .onAppear { restart() }
        .onDisappear { poller.stop() }
        .onChange(of: open) { _ in restart() }
        .onChange(of: run.isActive) { _ in restart() }
    }

    private func restart() {
        if open { poller.start(runId: run.id, active: run.isActive) } else { poller.stop(); res.loadHistoryIfNeeded() }
    }

    private func header(_ s: RunResources.Snapshot) -> some View {
        HStack(spacing: M.sm) {
            Button {
                withAnimation(.easeOut(duration: 0.12)) { open.toggle() }
            } label: {
                HStack(spacing: M.sm) {
                    PPIcon(open ? "chevron-down" : "chevron-right", size: M.iconSmall, color: theme.iconSecondary)
                    PPIcon("activity", size: M.iconSmall, color: theme.iconSecondary)
                    Text(tr("resources.title")).font(.pp(.label))
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            PPInfoButton(text: tr("resources.info"))
            if !run.isActive { PPBadge(text: tr("resources.ended")) }
            Spacer(minLength: M.md)
            if !open {
                Sparkline(samples: s.series.samples, metric: .memory, span: ResourceChart.span)
                    .equatable()
                    .frame(width: 96, height: 14)
            }
            Text(Format.memory(mb: s.rssMB)).font(.pp(.mono)).foregroundColor(ResourceMetric.memory.color(theme))
            Text(Format.cpu(s.cpu)).font(.pp(.mono)).foregroundColor(ResourceMetric.cpu.color(theme))
        }
        .padding(.horizontal, M.lg)
        .frame(height: 32)
    }

    private func charts(_ s: RunResources.Snapshot) -> some View {
        let end = run.isActive ? max(s.series.last?.t ?? 0, Int64(Date().timeIntervalSince1970 * 1000) - 2000)
            : (s.series.last?.t ?? 0)
        return HStack(spacing: M.xl) {
            ResourceChart(title: tr("resources.memory"), metric: .memory, samples: s.series.samples, endT: end, hoverT: $hoverT)
            ResourceChart(title: tr("resources.cpu"), metric: .cpu, samples: s.series.samples, endT: end, hoverT: $hoverT)
        }
        .frame(height: 110)
    }

    private func facts(_ s: RunResources.Snapshot) -> some View {
        let st = poller.stats
        let ports = (st?.ports.isEmpty == false ? st!.ports : run.ports)
        let uptime: String = {
            if let u = st?.uptimeSec, u > 0 { return Format.duration(TimeInterval(u)) }
            guard let start = run.startedAt else { return "-" }
            return Format.duration((run.endedAt ?? Date()).timeIntervalSince(start))
        }()
        return FlowRow(spacing: M.md) {
            fact(tr("resources.peak"), Format.memory(mb: max(s.peakRSSMB, run.peakRSSMB)))
            fact(tr("resources.procs"), "\(max(s.procs, st?.processes.count ?? 0))")
            fact(tr("resources.uptime"), uptime)
            if !ports.isEmpty { fact(tr("resources.ports"), ports.map(String.init).joined(separator: ", ")) }
        }
    }

    private func fact(_ k: String, _ v: String) -> some View {
        HStack(spacing: M.xs) {
            Text(k).foregroundColor(theme.textSecondary)
            Text(v).font(.pp(.mono))
        }
        .font(.pp(.small))
        .padding(.horizontal, M.sm)
        .frame(height: 22)
        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(theme.bgSubtle))
    }

    @ViewBuilder private var processes: some View {
        let list = poller.stats?.processes ?? []
        if !list.isEmpty {
            VStack(spacing: 0) {
                HStack(spacing: M.md) {
                    Text(tr("resources.process")).frame(maxWidth: .infinity, alignment: .leading)
                    Text("PID").frame(width: 64, alignment: .trailing)
                    Text(tr("resources.cpu")).frame(width: 64, alignment: .trailing)
                    Text(tr("resources.memory")).frame(width: 84, alignment: .trailing)
                    PPSegmented(options: [(false, tr("resources.view.list")), (true, tr("resources.view.tree"))],
                                selection: $treeMode, size: .small)
                }
                .font(.pp(.label))
                .foregroundColor(theme.textSecondary)
                .padding(.horizontal, M.md)
                .frame(height: M.rowHeight)
                .background(theme.bgSubtle)
                PPDivider(subtle: true)
                ScrollView {
                    LazyVStack(spacing: 0) {
                        if treeMode {
                            ForEach(ProcessTree.flatten(list, collapsed: collapsed)) { n in
                                processRow(n.process, depth: n.depth, hasChildren: n.hasChildren, subtree: n.subtreeRSSMB)
                            }
                        } else {
                            ForEach(list.sorted { $0.rssMB > $1.rssMB }) { p in
                                processRow(p, depth: 0, hasChildren: false, subtree: nil)
                            }
                        }
                    }
                }
                .frame(maxHeight: 150)
            }
            .clipShape(RoundedRectangle(cornerRadius: M.radiusMD))
            .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(theme.border, lineWidth: 1))
        }
    }

    private func processRow(_ p: RunProcess, depth: Int, hasChildren: Bool, subtree: Double?) -> some View {
        HStack(spacing: M.md) {
            HStack(spacing: M.xs) {
                Spacer().frame(width: CGFloat(depth) * 14)
                if treeMode {
                    if hasChildren {
                        Button {
                            if collapsed.contains(p.pid) { collapsed.remove(p.pid) } else { collapsed.insert(p.pid) }
                        } label: {
                            PPIcon(collapsed.contains(p.pid) ? "chevron-right" : "chevron-down", size: 12, color: theme.iconSecondary)
                                .frame(width: 14, height: 14).contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    } else {
                        Spacer().frame(width: 14)
                    }
                }
                Text(p.name).lineLimit(1).truncationMode(.middle)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Text("\(p.pid)").frame(width: 64, alignment: .trailing).foregroundColor(theme.textTertiary)
            Text(Format.cpu(p.cpu)).frame(width: 64, alignment: .trailing)
            Text(Format.memory(mb: p.rssMB)).frame(width: 84, alignment: .trailing)
                .help(subtree.map { tr("resources.subtree", Format.memory(mb: $0)) } ?? "")
            Spacer().frame(width: 88)
        }
        .font(.pp(.monoSmall))
        .padding(.horizontal, M.md)
        .frame(height: M.rowHeightCompact)
    }
}

/// 「运行中」页顶部汇总：运行中工程数、合计内存、合计 CPU，可按内存排序
struct RunningSummaryBar: View {
    @Binding var sortByMemory: Bool
    @ObservedObject private var store = ResourceStore.shared
    @Environment(\.theme) private var theme

    var body: some View {
        let t = store.totals
        HStack(spacing: M.xl) {
            stat(tr("resources.runningProjects"), "\(t.projects)", color: theme.textPrimary)
            stat(tr("resources.totalMemory"), Format.memory(mb: t.rssMB), color: ResourceMetric.memory.color(theme))
            stat(tr("resources.totalCPU"), Format.cpu(t.cpu), color: ResourceMetric.cpu.color(theme))
            PPInfoButton(text: tr("resources.info"))
            Spacer()
            Text(tr("resources.sort")).foregroundColor(theme.textSecondary).font(.pp(.small))
            PPSegmented(options: [(false, tr("resources.sort.default")), (true, tr("resources.memory"))],
                        selection: $sortByMemory, size: .small)
        }
        .padding(.horizontal, M.xl)
        .padding(.vertical, M.md)
    }

    private func stat(_ title: String, _ value: String, color: Color) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.pp(.caption)).foregroundColor(theme.textSecondary)
            Text(value).font(.pp(.heading).monospacedDigit()).foregroundColor(color)
        }
    }
}

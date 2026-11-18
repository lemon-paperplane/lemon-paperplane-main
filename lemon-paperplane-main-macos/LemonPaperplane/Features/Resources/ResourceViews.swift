import PaperplaneKit
import SwiftUI

// 资源监控的小组件：趋势线、带悬浮详情的资源数字、趋势图。全部用 Canvas 自己画，不引第三方库。
// 配色：内存用强调色（accent），CPU 用 warning 语气色；数字格式沿用 Format.memory / Format.cpu。

enum ResourceMetric {
    case memory, cpu

    func value(_ s: ResourceSample) -> Double { self == .memory ? s.rssMB : s.cpu }

    func color(_ theme: Theme) -> Color { self == .memory ? theme.accent : theme.warning }

    func format(_ v: Double) -> String { self == .memory ? Format.memory(mb: v) : Format.cpu(v) }

    /// 纵轴最小上限：内存 64 MB、CPU 10%，避免几乎不变的数据被放大成锯齿
    var minScale: Double { self == .memory ? 64 : 10 }
}

/// 趋势线（细线 + 浅色面积）。数据不变时 SwiftUI 不会重绘 Canvas。
struct Sparkline: View, Equatable {
    let samples: [ResourceSample]
    let metric: ResourceMetric
    /// 时间轴跨度（毫秒）；0 表示按已有点铺满
    var span: Int64 = 0
    var lineWidth: CGFloat = 1.25
    @Environment(\.theme) private var theme

    static func == (a: Sparkline, b: Sparkline) -> Bool {
        a.samples == b.samples && a.metric == b.metric && a.span == b.span
    }

    var body: some View {
        let color = metric.color(theme)
        let maxV = max(metric.minScale, (samples.map(metric.value).max() ?? 0))
        Canvas { ctx, size in
            let rect = CGRect(origin: .zero, size: size).insetBy(dx: 0, dy: lineWidth)
            let pts = SparklineGeometry.points(samples, value: metric.value, in: rect, span: span, maxValue: maxV)
            guard let first = pts.first else { return }
            var line = Path()
            line.move(to: first)
            for p in pts.dropFirst() { line.addLine(to: p) }
            if pts.count == 1 { line.addLine(to: CGPoint(x: first.x - 1, y: first.y)) }
            var area = line
            area.addLine(to: CGPoint(x: pts.last!.x, y: size.height))
            area.addLine(to: CGPoint(x: first.x, y: size.height))
            area.closeSubpath()
            ctx.fill(area, with: .color(color.opacity(theme.isDark ? 0.22 : 0.14)))
            ctx.stroke(line, with: .color(color), style: StrokeStyle(lineWidth: lineWidth, lineCap: .round, lineJoin: .round))
        }
    }
}

extension ResourceMetric: Equatable {}

/// 某个运行的内存数字（+ 可选 CPU），只观察这个运行自己的数据
struct RunResourceText: View {
    @ObservedObject var res: RunResources
    var showCPU = true
    var fallback: (cpu: Double, rss: Double)?
    @Environment(\.theme) private var theme

    init(runId: String, showCPU: Bool = true, fallback: (cpu: Double, rss: Double)? = nil) {
        _res = ObservedObject(wrappedValue: ResourceStore.shared.resources(runId))
        self.showCPU = showCPU
        self.fallback = fallback
    }

    var body: some View {
        let s = res.snap
        let rss = s.t > 0 ? s.rssMB : (fallback?.rss ?? 0)
        let cpu = s.t > 0 ? s.cpu : (fallback?.cpu ?? 0)
        HStack(spacing: M.sm) {
            if showCPU { Text(Format.cpu(cpu)).foregroundColor(theme.textTertiary) }
            Text(Format.memory(mb: rss))
        }
        .font(.pp(.monoSmall))
        .foregroundColor(theme.textSecondary)
    }
}

/// 卡片 / 弹出框里的资源区：内存趋势线 + 内存 + CPU，悬浮显示详情
struct RunResourceInline: View {
    @ObservedObject var res: RunResources
    var startedAt: Date?
    var sparkWidth: CGFloat = 64
    var showCPU = true
    @State private var hover = false
    @State private var open = false
    @State private var hoverTask: Task<Void, Never>?
    @Environment(\.theme) private var theme

    init(runId: String, startedAt: Date?, sparkWidth: CGFloat = 64, showCPU: Bool = true) {
        _res = ObservedObject(wrappedValue: ResourceStore.shared.resources(runId))
        self.startedAt = startedAt
        self.sparkWidth = sparkWidth
        self.showCPU = showCPU
    }

    var body: some View {
        let s = res.snap
        HStack(spacing: M.sm) {
            Sparkline(samples: s.series.samples, metric: .memory)
                .equatable()
                .frame(width: sparkWidth, height: 14)
            Text(Format.memory(mb: s.rssMB)).foregroundColor(theme.textSecondary)
            if showCPU {
                Text(Format.cpu(s.cpu)).foregroundColor(theme.textTertiary)
            }
        }
        .font(.pp(.monoSmall))
        .contentShape(Rectangle())
        .onHover { inside in
            hoverTask?.cancel()
            if inside {
                hoverTask = Task {
                    try? await Task.sleep(nanoseconds: 350_000_000)
                    if !Task.isCancelled { open = true }
                }
            } else {
                open = false
            }
        }
        .ppPopover(isPresented: $open, alignment: .trailing) {
            ResourceDetailPopover(res: res, startedAt: startedAt)
        }
        .onAppear { res.loadHistoryIfNeeded() }
        .onDisappear {
            hoverTask?.cancel()
            open = false
        }
    }
}

/// 悬浮详情：内存 / 峰值 / CPU / 进程数 / 运行时长 + 两条趋势线
private struct ResourceDetailPopover: View {
    @ObservedObject var res: RunResources
    let startedAt: Date?
    @Environment(\.theme) private var theme

    var body: some View {
        let s = res.snap
        VStack(alignment: .leading, spacing: M.sm) {
            row(tr("resources.memory"), Format.memory(mb: s.rssMB), metric: .memory)
            row(tr("resources.cpu"), Format.cpu(s.cpu), metric: .cpu)
            PPDivider(subtle: true)
            PPKeyValueList(rows: [
                .init(key: tr("resources.peak"), value: Format.memory(mb: s.peakRSSMB), mono: true),
                .init(key: tr("resources.procs"), value: "\(s.procs)", mono: true),
                .init(key: tr("resources.uptime"), value: startedAt.map { Format.duration(Date().timeIntervalSince($0)) } ?? "-", mono: true),
            ], keyWidth: 80)
            .font(.pp(.small))
        }
        .padding(M.md)
        .frame(width: 260)
    }

    private func row(_ title: String, _ value: String, metric: ResourceMetric) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack {
                Text(title).font(.pp(.small)).foregroundColor(theme.textSecondary)
                Spacer()
                Text(value).font(.pp(.mono))
            }
            Sparkline(samples: res.snap.series.samples, metric: metric, span: 300_000)
                .equatable()
                .frame(height: 28)
        }
    }
}

/// 趋势图（运行页资源面板）：固定 5 分钟时间轴，悬浮时显示该时刻的值（与另一张图联动）
struct ResourceChart: View {
    let title: String
    let metric: ResourceMetric
    let samples: [ResourceSample]
    /// 时间轴右端（毫秒）
    let endT: Int64
    @Binding var hoverT: Int64?
    static let span: Int64 = 300_000
    @Environment(\.theme) private var theme

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f
    }()

    var body: some View {
        let color = metric.color(theme)
        let maxV = SparklineGeometry.niceCeiling(max(metric.minScale, samples.map(metric.value).max() ?? 0))
        let hovered = hoverT.flatMap { t in samples.min { abs($0.t - t) < abs($1.t - t) } }
        let shown = hovered ?? samples.last
        VStack(alignment: .leading, spacing: M.xs) {
            HStack(spacing: M.sm) {
                Circle().fill(color).frame(width: 6, height: 6)
                Text(title).font(.pp(.small)).foregroundColor(theme.textSecondary)
                Spacer()
                if let h = hovered {
                    Text(Self.timeFormatter.string(from: Date(timeIntervalSince1970: Double(h.t) / 1000)))
                        .font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                }
                Text(shown.map { metric.format(metric.value($0)) } ?? "-").font(.pp(.mono))
            }
            GeometryReader { g in
                Canvas { ctx, size in
                    let rect = CGRect(origin: .zero, size: size)
                    // 参考线：上限与一半
                    for f in [0.0, 0.5] {
                        var p = Path()
                        let y = rect.minY + rect.height * f
                        p.move(to: CGPoint(x: 0, y: y))
                        p.addLine(to: CGPoint(x: size.width, y: y))
                        ctx.stroke(p, with: .color(theme.borderSubtle), style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
                    }
                    var base = Path()
                    base.move(to: CGPoint(x: 0, y: size.height - 0.5))
                    base.addLine(to: CGPoint(x: size.width, y: size.height - 0.5))
                    ctx.stroke(base, with: .color(theme.border), lineWidth: 1)

                    let pts = SparklineGeometry.points(samples, value: metric.value, in: rect.insetBy(dx: 0, dy: 1),
                                                       span: Self.span, end: endT, maxValue: maxV / 1.1)
                    if let first = pts.first {
                        var line = Path()
                        line.move(to: first)
                        for p in pts.dropFirst() { line.addLine(to: p) }
                        var area = line
                        area.addLine(to: CGPoint(x: pts.last!.x, y: size.height))
                        area.addLine(to: CGPoint(x: first.x, y: size.height))
                        area.closeSubpath()
                        ctx.fill(area, with: .color(color.opacity(theme.isDark ? 0.22 : 0.14)))
                        ctx.stroke(line, with: .color(color), style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
                    }
                    if let h = hovered, let i = samples.firstIndex(of: h), pts.indices.contains(i) {
                        let p = pts[i]
                        var guide = Path()
                        guide.move(to: CGPoint(x: p.x, y: 0))
                        guide.addLine(to: CGPoint(x: p.x, y: size.height))
                        ctx.stroke(guide, with: .color(theme.textTertiary), lineWidth: 1)
                        let dot = CGRect(x: p.x - 3, y: p.y - 3, width: 6, height: 6)
                        ctx.fill(Path(ellipseIn: dot), with: .color(theme.bgSurface))
                        ctx.stroke(Path(ellipseIn: dot), with: .color(color), lineWidth: 1.5)
                    }
                }
                .overlay(alignment: .topLeading) {
                    Text(metric.format(maxV)).font(.pp(.caption)).foregroundColor(theme.textTertiary).padding(.leading, 2)
                }
                .contentShape(Rectangle())
                .onContinuousHover { phase in
                    switch phase {
                    case .active(let loc):
                        let frac = min(max(Double(loc.x / max(g.size.width, 1)), 0), 1)
                        hoverT = endT - Self.span + Int64(frac * Double(Self.span))
                    case .ended:
                        hoverT = nil
                    }
                }
            }
            HStack {
                Text(tr("resources.axis.past")).font(.pp(.caption)).foregroundColor(theme.textTertiary)
                Spacer()
                Text(tr("resources.axis.now")).font(.pp(.caption)).foregroundColor(theme.textTertiary)
            }
        }
    }
}

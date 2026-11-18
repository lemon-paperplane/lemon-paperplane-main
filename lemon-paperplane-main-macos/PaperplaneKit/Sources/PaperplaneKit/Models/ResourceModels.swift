import CoreGraphics
import Foundation

// 运行的资源监控：run.stats 与 stats 事件（RunBrief.t/cpu/rssMB）。
// 内存是整个进程树的常驻内存之和，CPU 是进程树 CPU% 之和（可超过 100%）。

/// 一次资源采样
public struct ResourceSample: Codable, Hashable {
    /// Unix 毫秒
    @DefInt64 public var t: Int64
    @DefDouble public var cpu: Double
    @DefDouble public var rssMB: Double

    public init(t: Int64, cpu: Double, rssMB: Double) {
        self.t = t; self.cpu = cpu; self.rssMB = rssMB
    }
}

/// 进程树里的一个进程（Go 的 model.ProcessInfo；改名避开 Foundation.ProcessInfo）
public struct RunProcess: Codable, Hashable, Identifiable {
    public var pid: Int
    @DefInt public var ppid: Int
    @DefString public var name: String
    @DefDouble public var cpu: Double
    @DefDouble public var rssMB: Double
    public var id: Int { pid }

    public init(pid: Int, ppid: Int, name: String, cpu: Double = 0, rssMB: Double = 0) {
        self.pid = pid; self.ppid = ppid; self.name = name; self.cpu = cpu; self.rssMB = rssMB
    }
}

/// run.stats 输出：最近 5 分钟的趋势（2 秒一点，最多 150 点）与进程明细（按内存降序）
public struct RunStats: Codable, Hashable {
    @DefString public var runId: String
    @DefString public var state: String
    @DefArray public var samples: [ResourceSample]
    @DefArray public var processes: [RunProcess]
    @DefDouble public var cpu: Double
    @DefDouble public var rssMB: Double
    @DefDouble public var peakRSSMB: Double
    @DefArray public var ports: [Int]
    @DefInt64 public var uptimeSec: Int64
    @DefInt public var intervalMs: Int
}

/// 客户端的趋势序列：按时间追加、去重、最多保留 capacity 点（与内核一致 150 点）。
public struct ResourceSeries: Hashable {
    public private(set) var samples: [ResourceSample] = []
    public let capacity: Int

    public init(capacity: Int = 150) { self.capacity = capacity }

    public var last: ResourceSample? { samples.last }
    public var isEmpty: Bool { samples.isEmpty }

    /// 追加一点；时间不晚于最后一点的忽略。返回是否有变化
    @discardableResult
    public mutating func append(_ s: ResourceSample) -> Bool {
        if let l = samples.last, s.t <= l.t { return false }
        samples.append(s)
        if samples.count > capacity { samples.removeFirst(samples.count - capacity) }
        return true
    }

    /// 合并一段历史（run.stats），保留两边的点、按时间排序去重
    @discardableResult
    public mutating func merge(_ history: [ResourceSample]) -> Bool {
        guard !history.isEmpty else { return false }
        var byT: [Int64: ResourceSample] = [:]
        for s in samples { byT[s.t] = s }
        for s in history { byT[s.t] = s }
        let merged = byT.values.sorted { $0.t < $1.t }.suffix(capacity)
        let changed = Array(merged) != samples
        samples = Array(merged)
        return changed
    }

    public var maxRSS: Double { samples.map(\.rssMB).max() ?? 0 }
    public var maxCPU: Double { samples.map(\.cpu).max() ?? 0 }
}

/// 趋势线几何：把采样换算到矩形里（x 按时间，y 按值；0 在底部）。三端画法一致。
public enum SparklineGeometry {
    /// - Parameters:
    ///   - span: 时间轴跨度（毫秒）；为 0 时按点的序号均分
    ///   - end: 时间轴右端（毫秒），默认最后一点
    ///   - maxValue: 纵轴上限（至少为数据最大值，留 10% 余量）
    public static func points(_ samples: [ResourceSample], value: (ResourceSample) -> Double, in rect: CGRect,
                              span: Int64 = 0, end: Int64? = nil, maxValue: Double? = nil) -> [CGPoint] {
        guard !samples.isEmpty, rect.width > 0, rect.height > 0 else { return [] }
        let vmax = max(maxValue ?? 0, (samples.map(value).max() ?? 0) * 1.1, 0.0001)
        let endT = end ?? samples.last!.t
        let startT = span > 0 ? endT - span : samples.first!.t
        let width = Double(max(endT - startT, 1))
        return samples.enumerated().map { i, s in
            let x: Double
            if span > 0 || samples.count > 1 && endT > startT {
                x = Double(s.t - startT) / width
            } else {
                x = samples.count > 1 ? Double(i) / Double(samples.count - 1) : 1
            }
            let y = min(max(value(s) / vmax, 0), 1)
            return CGPoint(x: rect.minX + CGFloat(min(max(x, 0), 1)) * rect.width,
                           y: rect.maxY - CGFloat(y) * rect.height)
        }
    }

    /// 离 x 最近的采样下标（悬浮显示数值用）
    public static func nearestIndex(_ points: [CGPoint], x: CGFloat) -> Int? {
        guard !points.isEmpty else { return nil }
        var best = 0
        var bestD = CGFloat.greatestFiniteMagnitude
        for (i, p) in points.enumerated() {
            let d = abs(p.x - x)
            if d < bestD { bestD = d; best = i }
        }
        return best
    }

    /// 纵轴上限取「好看」的整数：1、2、5 × 10^n
    public static func niceCeiling(_ v: Double) -> Double {
        guard v > 0 else { return 1 }
        let exp = pow(10, floor(log10(v)))
        for m in [1.0, 2.0, 2.5, 5.0, 10.0] where m * exp >= v { return m * exp }
        return 10 * exp
    }
}

/// 进程明细的树形排列（按父子关系，同级按内存降序）
public enum ProcessTree {
    public struct Node: Hashable, Identifiable {
        public let process: RunProcess
        public let depth: Int
        public let hasChildren: Bool
        /// 自己加全部后代的内存
        public let subtreeRSSMB: Double
        public var id: Int { process.pid }
    }

    /// collapsed 里的 pid 不展开其子进程
    public static func flatten(_ list: [RunProcess], collapsed: Set<Int> = []) -> [Node] {
        let pids = Set(list.map(\.pid))
        var children: [Int: [RunProcess]] = [:]
        var roots: [RunProcess] = []
        for p in list {
            if p.ppid != p.pid, pids.contains(p.ppid) { children[p.ppid, default: []].append(p) } else { roots.append(p) }
        }
        var subtree: [Int: Double] = [:]
        func total(_ p: RunProcess, _ seen: inout Set<Int>) -> Double {
            if let v = subtree[p.pid] { return v }
            guard seen.insert(p.pid).inserted else { return 0 }
            let v = p.rssMB + (children[p.pid] ?? []).reduce(0) { $0 + total($1, &seen) }
            subtree[p.pid] = v
            return v
        }
        var seen = Set<Int>()
        for p in list { _ = total(p, &seen) }
        var out: [Node] = []
        var visited = Set<Int>()
        func walk(_ p: RunProcess, _ depth: Int) {
            guard visited.insert(p.pid).inserted else { return }
            let kids = (children[p.pid] ?? []).sorted { (subtree[$0.pid] ?? 0) > (subtree[$1.pid] ?? 0) }
            out.append(Node(process: p, depth: depth, hasChildren: !kids.isEmpty, subtreeRSSMB: subtree[p.pid] ?? p.rssMB))
            if collapsed.contains(p.pid) {
                // 收起的子树不输出，但记为已访问，免得后面被当成孤立进程列出来
                func mark(_ q: RunProcess) {
                    guard visited.insert(q.pid).inserted else { return }
                    for c in children[q.pid] ?? [] { mark(c) }
                }
                for k in kids { mark(k) }
            } else {
                for k in kids { walk(k, depth + 1) }
            }
        }
        for r in roots.sorted(by: { (subtree[$0.pid] ?? 0) > (subtree[$1.pid] ?? 0) }) { walk(r, 0) }
        // 父子关系成环等异常数据：没走到的也要列出来
        for p in list where !visited.contains(p.pid) { walk(p, 0) }
        return out
    }
}

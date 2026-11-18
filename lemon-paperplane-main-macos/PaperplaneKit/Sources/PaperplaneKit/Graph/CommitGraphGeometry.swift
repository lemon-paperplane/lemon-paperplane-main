import CoreGraphics
import Foundation

/// 提交图几何计算：把 VCSGraphRow（内核已算好的列与连线）换算成可直接绘制的线段和节点位置。
///
/// 约定（与 vcs.go 一致）：第 i 行的 edges 描述「本行列号 → 下一行列号」的连线。
/// 因此画第 i 行时：上半段来自第 i-1 行的 edges（从行顶到本行中线），下半段来自本行 edges（从中线到行底）。
/// 斜线在半行内完成转弯，三端画法一致。
public struct CommitGraphGeometry {
    public struct Segment: Hashable {
        public var from: CGPoint
        public var to: CGPoint
        public var color: Int
    }

    public struct Row {
        public var node: CGPoint
        public var nodeColor: Int
        public var segments: [Segment]
        public var width: CGFloat
    }

    public var laneWidth: CGFloat
    public var rowHeight: CGFloat
    public var leftPadding: CGFloat

    public init(laneWidth: CGFloat = 14, rowHeight: CGFloat = 28, leftPadding: CGFloat = 8) {
        self.laneWidth = laneWidth
        self.rowHeight = rowHeight
        self.leftPadding = leftPadding
    }

    public func x(_ lane: Int) -> CGFloat { leftPadding + CGFloat(lane) * laneWidth + laneWidth / 2 }

    /// 计算一行。坐标以该行左上为原点、y 向下。
    public func row(_ current: VCSGraphRow, previous: VCSGraphRow?) -> Row {
        let mid = rowHeight / 2
        var segs: [Segment] = []
        // 上半段：上一行的连线终点落在本行
        if let prev = previous {
            for e in prev.edges {
                segs.append(Segment(from: CGPoint(x: (x(e.from) + x(e.to)) / 2, y: 0),
                                    to: CGPoint(x: x(e.to), y: mid), color: e.color))
            }
        }
        // 下半段：本行出发去下一行
        for e in current.edges {
            segs.append(Segment(from: CGPoint(x: x(e.from), y: mid),
                                to: CGPoint(x: (x(e.from) + x(e.to)) / 2, y: rowHeight), color: e.color))
        }
        let lanes = max(current.lanes, previous?.lanes ?? 0, current.lane + 1)
        return Row(node: CGPoint(x: x(current.lane), y: mid), nodeColor: current.color, segments: segs,
                   width: leftPadding * 2 + CGFloat(lanes) * laneWidth)
    }

    /// 一组提交的整体图宽（取最大列数）
    public func width(for rows: [VCSGraphRow]) -> CGFloat {
        let lanes = rows.map { max($0.lanes, $0.lane + 1) }.max() ?? 1
        return leftPadding * 2 + CGFloat(lanes) * laneWidth
    }

    /// 颜色序号 → 调色板下标（设计变量 graphLane0…graphLane7）
    public static func paletteIndex(_ color: Int, paletteSize: Int = 8) -> Int {
        let m = color % paletteSize
        return m < 0 ? m + paletteSize : m
    }
}

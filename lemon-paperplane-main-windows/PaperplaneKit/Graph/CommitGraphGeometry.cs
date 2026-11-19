using System;
using System.Collections.Generic;
using System.Linq;

namespace PaperplaneKit;

/// <summary>
/// 提交图几何计算：把 VCSGraphRow（内核已算好的列与连线）换算成可直接绘制的线段和节点位置。
/// <para>
/// 约定（与 vcs.go 一致）：第 i 行的 edges 描述「本行列号 → 下一行列号」的连线。
/// 因此画第 i 行时：上半段来自第 i-1 行的 edges（从行顶到本行中线），下半段来自本行 edges（从中线到行底）。
/// 斜线在半行内完成转弯，三端画法一致。
/// </para>
/// </summary>
public sealed class CommitGraphGeometry
{
    public readonly record struct Segment(KitPoint From, KitPoint To, int Color);

    public sealed record Row(KitPoint Node, int NodeColor, List<Segment> Segments, double Width);

    public double LaneWidth { get; }
    public double RowHeight { get; }
    public double LeftPadding { get; }

    public CommitGraphGeometry(double laneWidth = 14, double rowHeight = 28, double leftPadding = 8)
    {
        LaneWidth = laneWidth;
        RowHeight = rowHeight;
        LeftPadding = leftPadding;
    }

    public double X(int lane) => LeftPadding + lane * LaneWidth + LaneWidth / 2;

    /// <summary>计算一行。坐标以该行左上为原点、y 向下。</summary>
    public Row RowFor(VCSGraphRow current, VCSGraphRow? previous)
    {
        var mid = RowHeight / 2;
        var segs = new List<Segment>();
        // 上半段：上一行的连线终点落在本行
        if (previous != null)
            foreach (var e in previous.Edges)
                segs.Add(new Segment(new KitPoint((X(e.From) + X(e.To)) / 2, 0), new KitPoint(X(e.To), mid), e.Color));
        // 下半段：本行出发去下一行
        foreach (var e in current.Edges)
            segs.Add(new Segment(new KitPoint(X(e.From), mid), new KitPoint((X(e.From) + X(e.To)) / 2, RowHeight), e.Color));
        var lanes = Math.Max(Math.Max(current.Lanes, previous?.Lanes ?? 0), current.Lane + 1);
        return new Row(new KitPoint(X(current.Lane), mid), current.Color, segs, LeftPadding * 2 + lanes * LaneWidth);
    }

    /// <summary>一组提交的整体图宽（取最大列数）</summary>
    public double Width(IEnumerable<VCSGraphRow> rows)
    {
        var list = rows.ToList();
        var lanes = list.Count == 0 ? 1 : list.Max(r => Math.Max(r.Lanes, r.Lane + 1));
        return LeftPadding * 2 + lanes * LaneWidth;
    }

    /// <summary>颜色序号 → 调色板下标（设计变量 graphLane0…graphLane7）</summary>
    public static int PaletteIndex(int color, int paletteSize = 8)
    {
        var m = color % paletteSize;
        return m < 0 ? m + paletteSize : m;
    }
}

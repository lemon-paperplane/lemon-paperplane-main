using System;
using System.Collections.Generic;
using System.Linq;

namespace PaperplaneKit;

// 运行的资源监控：run.stats 与 stats 事件（RunBrief.T/Cpu/RssMB）。
// 内存是整个进程树的常驻内存之和，CPU 是进程树 CPU% 之和（可超过 100%）。

/// <summary>一次资源采样</summary>
public sealed record ResourceSample
{
    /// <summary>Unix 毫秒</summary>
    public long T { get; set; }
    public double Cpu { get; set; }
    public double RssMB { get; set; }

    public ResourceSample() { }
    public ResourceSample(long t, double cpu, double rssMB) { T = t; Cpu = cpu; RssMB = rssMB; }
}

/// <summary>进程树里的一个进程（Go 的 model.ProcessInfo）</summary>
public sealed record RunProcess
{
    public int Pid { get; set; }
    public int Ppid { get; set; }
    public string Name { get; set; } = "";
    public double Cpu { get; set; }
    public double RssMB { get; set; }
    public int Id => Pid;

    public RunProcess() { }
    public RunProcess(int pid, int ppid, string name, double cpu = 0, double rssMB = 0)
    {
        Pid = pid; Ppid = ppid; Name = name; Cpu = cpu; RssMB = rssMB;
    }
}

/// <summary>run.stats 输出：最近 5 分钟的趋势（2 秒一点，最多 150 点）与进程明细（按内存降序）</summary>
public sealed record RunStats
{
    public string RunId { get; set; } = "";
    public string State { get; set; } = "";
    public List<ResourceSample> Samples { get; set; } = new();
    public List<RunProcess> Processes { get; set; } = new();
    public double Cpu { get; set; }
    public double RssMB { get; set; }
    public double PeakRSSMB { get; set; }
    public List<int> Ports { get; set; } = new();
    public long UptimeSec { get; set; }
    public int IntervalMs { get; set; }
}

/// <summary>客户端的趋势序列：按时间追加、去重、最多保留 Capacity 点（与内核一致 150 点）。非线程安全，只在 UI 线程用。</summary>
public sealed class ResourceSeries
{
    private List<ResourceSample> _samples = new();

    public ResourceSeries(int capacity = 150) { Capacity = capacity; }

    public int Capacity { get; }
    public IReadOnlyList<ResourceSample> Samples => _samples;
    public ResourceSample? Last => _samples.Count > 0 ? _samples[_samples.Count - 1] : null;
    public bool IsEmpty => _samples.Count == 0;

    /// <summary>追加一点；时间不晚于最后一点的忽略。返回是否有变化</summary>
    public bool Append(ResourceSample s)
    {
        if (_samples.Count > 0 && s.T <= _samples[_samples.Count - 1].T) return false;
        _samples.Add(s);
        if (_samples.Count > Capacity) _samples.RemoveRange(0, _samples.Count - Capacity);
        return true;
    }

    /// <summary>合并一段历史（run.stats），保留两边的点、按时间排序去重（同一时间以历史为准）</summary>
    public bool Merge(IReadOnlyList<ResourceSample> history)
    {
        if (history.Count == 0) return false;
        var byT = new Dictionary<long, ResourceSample>();
        foreach (var s in _samples) byT[s.T] = s;
        foreach (var s in history) byT[s.T] = s;
        var sorted = byT.Values.OrderBy(s => s.T).ToList();
        var merged = sorted.Skip(Math.Max(0, sorted.Count - Capacity)).ToList();
        var changed = !merged.SequenceEqual(_samples);
        _samples = merged;
        return changed;
    }

    public double MaxRSS => _samples.Count == 0 ? 0 : _samples.Max(s => s.RssMB);
    public double MaxCPU => _samples.Count == 0 ? 0 : _samples.Max(s => s.Cpu);
}

/// <summary>趋势线几何：把采样换算到矩形里（x 按时间，y 按值；0 在底部）。三端画法一致。</summary>
public static class SparklineGeometry
{
    /// <param name="span">时间轴跨度（毫秒）；为 0 时按点的时间铺满（只有一点或时间相同时按序号均分）</param>
    /// <param name="end">时间轴右端（毫秒），默认最后一点</param>
    /// <param name="maxValue">纵轴上限（至少为数据最大值，留 10% 余量）</param>
    public static List<KitPoint> Points(IReadOnlyList<ResourceSample> samples, Func<ResourceSample, double> value, KitRect rect,
        long span = 0, long? end = null, double? maxValue = null)
    {
        var o = new List<KitPoint>();
        if (samples.Count == 0 || rect.Width <= 0 || rect.Height <= 0) return o;
        var vmax = Math.Max(Math.Max(maxValue ?? 0, samples.Max(value) * 1.1), 0.0001);
        var endT = end ?? samples[samples.Count - 1].T;
        var startT = span > 0 ? endT - span : samples[0].T;
        var width = (double)Math.Max(endT - startT, 1);
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            double x;
            if (span > 0 || (samples.Count > 1 && endT > startT))
                x = (s.T - startT) / width;
            else
                x = samples.Count > 1 ? (double)i / (samples.Count - 1) : 1;
            var y = Math.Min(Math.Max(value(s) / vmax, 0), 1);
            o.Add(new KitPoint(rect.MinX + Math.Min(Math.Max(x, 0), 1) * rect.Width, rect.MaxY - y * rect.Height));
        }
        return o;
    }

    /// <summary>离 x 最近的采样下标（悬浮显示数值用）</summary>
    public static int? NearestIndex(IReadOnlyList<KitPoint> points, double x)
    {
        if (points.Count == 0) return null;
        var best = 0;
        var bestD = double.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var d = Math.Abs(points[i].X - x);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>纵轴上限取「好看」的数：1、2、2.5、5 × 10^n</summary>
    public static double NiceCeiling(double v)
    {
        if (!(v > 0)) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
            if (m * exp >= v) return m * exp;
        return 10 * exp;
    }
}

/// <summary>进程明细的树形排列（按父子关系，同级按子树内存降序）</summary>
public static class ProcessTree
{
    /// <summary>一行：SubtreeRSSMB 是自己加全部后代的内存。</summary>
    public sealed record Node(RunProcess Process, int Depth, bool HasChildren, double SubtreeRSSMB)
    {
        public int Id => Process.Pid;
    }

    /// <summary>collapsed 里的 pid 不展开其子进程</summary>
    public static List<Node> Flatten(IReadOnlyList<RunProcess> list, ISet<int>? collapsed = null)
    {
        var pids = new HashSet<int>(list.Select(p => p.Pid));
        var children = new Dictionary<int, List<RunProcess>>();
        var roots = new List<RunProcess>();
        foreach (var p in list)
        {
            if (p.Ppid != p.Pid && pids.Contains(p.Ppid))
            {
                if (!children.TryGetValue(p.Ppid, out var c)) children[p.Ppid] = c = new List<RunProcess>();
                c.Add(p);
            }
            else roots.Add(p);
        }
        List<RunProcess> Kids(int pid) => children.TryGetValue(pid, out var c) ? c : new List<RunProcess>();

        var subtree = new Dictionary<int, double>();
        var seen = new HashSet<int>();
        double Total(RunProcess p)
        {
            if (subtree.TryGetValue(p.Pid, out var v)) return v;
            if (!seen.Add(p.Pid)) return 0;
            var t = p.RssMB + Kids(p.Pid).Sum(Total);
            subtree[p.Pid] = t;
            return t;
        }
        foreach (var p in list) Total(p);
        double Sub(RunProcess p) => subtree.TryGetValue(p.Pid, out var v) ? v : 0;

        var o = new List<Node>();
        var visited = new HashSet<int>();
        void Mark(RunProcess q)
        {
            if (!visited.Add(q.Pid)) return;
            foreach (var c in Kids(q.Pid)) Mark(c);
        }
        void Walk(RunProcess p, int depth)
        {
            if (!visited.Add(p.Pid)) return;
            var kids = Kids(p.Pid).OrderByDescending(Sub).ToList();
            o.Add(new Node(p, depth, kids.Count > 0, subtree.TryGetValue(p.Pid, out var v) ? v : p.RssMB));
            if (collapsed != null && collapsed.Contains(p.Pid))
            {
                // 收起的子树不输出，但记为已访问，免得后面被当成孤立进程列出来
                foreach (var k in kids) Mark(k);
            }
            else
            {
                foreach (var k in kids) Walk(k, depth + 1);
            }
        }
        foreach (var r in roots.OrderByDescending(Sub)) Walk(r, 0);
        // 父子关系成环等异常数据：没走到的也要列出来
        foreach (var p in list) if (!visited.Contains(p.Pid)) Walk(p, 0);
        return o;
    }
}

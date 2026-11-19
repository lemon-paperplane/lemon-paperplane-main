using System;
using System.Collections.Generic;
using System.Linq;
using PaperplaneKit;
using RunModel = PaperplaneKit.Run;

namespace LemonPaperplane;

/// <summary>
/// 一个运行的资源数据（趋势 + 当前值）。每个运行一个对象，只有显示它的小视图会随 stats 重绘（规范第 15 节「性能」）。
/// 数据变化时通知 nameof(Snap)。对应 Mac 的 RunResources。只在 UI 线程使用。
/// </summary>
public sealed class RunResources : PPObservable
{
    public string RunId { get; }

    /// <summary>趋势（最近 150 点）。原地追加，读的人只在收到通知后读。</summary>
    public ResourceSeries Series { get; } = new();
    public double Cpu { get; private set; }
    public double RssMB { get; private set; }
    public double PeakRSSMB { get; private set; }
    public int Procs { get; private set; }
    public string State { get; private set; } = "";
    /// <summary>采样时间（毫秒）；0 表示还没有数据。</summary>
    public long T { get; private set; }

    /// <summary>数据版本（每次变化加一），给只需要知道「变了没有」的视图用。</summary>
    public int Version { get; private set; }

    private bool _historyLoaded;
    private bool _historyLoading;

    public RunResources(string runId) { RunId = runId; }

    private void Changed()
    {
        Version++;
        Notify("Snap");
    }

    internal void Apply(RunBrief b)
    {
        var t = b.T > 0 ? b.T : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changed = Series.Append(new ResourceSample(t, b.Cpu, b.RssMB));
        var peak = Math.Max(Math.Max(b.PeakRSSMB, b.RssMB), PeakRSSMB);
        var state = string.IsNullOrEmpty(b.State) ? State : b.State;
        if (!changed && Cpu == b.Cpu && RssMB == b.RssMB && PeakRSSMB == peak && Procs == b.Procs && State == state && T == t) return;
        Cpu = b.Cpu;
        RssMB = b.RssMB;
        PeakRSSMB = peak;
        Procs = b.Procs;
        State = state;
        T = t;
        Changed();
    }

    internal void Apply(RunStats stats)
    {
        var changed = Series.Merge(stats.Samples);
        if (stats.PeakRSSMB > PeakRSSMB) { PeakRSSMB = stats.PeakRSSMB; changed = true; }
        if (!string.IsNullOrEmpty(stats.State) && State != stats.State) { State = stats.State; changed = true; }
        if (stats.Samples.Count > 0 && stats.Samples[stats.Samples.Count - 1] is { } last && last.T >= T)
        {
            var active = RunState.IsActive(stats.State);
            var cpu = active ? stats.Cpu : 0;
            var rss = active ? stats.RssMB : 0;
            if (Cpu != cpu || RssMB != rss || T != last.T) changed = true;
            Cpu = cpu;
            RssMB = rss;
            T = last.T;
        }
        if (stats.Processes.Count > 0 && Procs != stats.Processes.Count) { Procs = stats.Processes.Count; changed = true; }
        if (changed) Changed();
    }

    internal void SetState(string state)
    {
        if (State == state) return;
        State = state;
        if (!RunState.IsActive(state))
        {
            Cpu = 0;
            RssMB = 0;
            Procs = 0;
        }
        Changed();
    }

    /// <summary>首次显示时用 run.stats 补历史（每个运行只取一次，之后靠 stats 事件追加）。</summary>
    public async void LoadHistoryIfNeeded()
    {
        if (_historyLoaded || _historyLoading || AppModel.Shared.Api is not { } api) return;
        _historyLoading = true;
        try
        {
            var st = await api.RunStatsAsync(RunId);
            Apply(st);
            _historyLoaded = true;
        }
        catch
        {
            // 取不到历史不影响后续追加
        }
        finally
        {
            _historyLoading = false;
        }
    }
}

/// <summary>「运行中」页顶部的合计。</summary>
public sealed record ResourceTotals(int Projects, int Runs, double RssMB, double Cpu)
{
    public static readonly ResourceTotals Zero = new(0, 0, 0, 0);
}

/// <summary>
/// 全部运行的资源数据与合计（对应 Mac ResourceStore）。stats 事件每 2 秒经 Apply 进来；
/// 合计变化通知 nameof(Totals)，单个运行的变化只通知它自己的 RunResources。
/// </summary>
public sealed class ResourceStore : PPObservable
{
    public static readonly ResourceStore Shared = new();

    private readonly Dictionary<string, RunResources> _byRun = new();
    private ResourceTotals _totals = ResourceTotals.Zero;

    public ResourceTotals Totals { get => _totals; private set => Set(ref _totals, value); }

    /// <summary>某个运行的资源对象（没有就建）。</summary>
    public RunResources Resources(string runId)
    {
        if (_byRun.TryGetValue(runId, out var r)) return r;
        r = new RunResources(runId);
        _byRun[runId] = r;
        return r;
    }

    /// <summary>已有数据时取（不创建）。</summary>
    public RunResources? Existing(string runId) => _byRun.TryGetValue(runId, out var r) ? r : null;

    /// <summary>stats 事件（只含运行中的）。</summary>
    public void Apply(List<RunBrief> briefs)
    {
        int runs = 0;
        double rss = 0, cpu = 0;
        var projects = new HashSet<string>();
        var store = AppModel.Shared.Runs.Runs;
        foreach (var b in briefs)
        {
            Resources(b.Id).Apply(b);
            if (!RunState.IsActive(b.State)) continue;
            runs++;
            rss += b.RssMB;
            cpu += b.Cpu;
            projects.Add(b.ProjectId ?? (store.TryGetValue(b.Id, out var r) ? r.ProjectId : b.Id));
        }
        Totals = new ResourceTotals(projects.Count, runs, rss, cpu);
    }

    /// <summary>run.changed：结束后当前值归零，曲线保留。</summary>
    public void RunChanged(RunModel r)
    {
        Existing(r.Id)?.SetState(r.State);
        if (!RunState.IsActive(r.State) && Totals.Runs > 0)
        {
            // 合计在下一次 stats 时重算；没有运行了就立即清零
            if (!AppModel.Shared.Runs.Runs.Values.Any(x => x.IsActive)) Totals = ResourceTotals.Zero;
        }
    }

    /// <summary>资源面板轮询到的完整数据。</summary>
    public void Apply(RunStats stats) => Resources(stats.RunId).Apply(stats);

    /// <summary>某工程运行中的内存合计（排序用）。</summary>
    public double Memory(string projectId) =>
        AppModel.Shared.Runs.ActiveRunsOf(projectId).Sum(r => Existing(r.Id)?.RssMB ?? 0);

    /// <summary>只保留仍在运行列表里的运行（结束的保留最近若干个，跟 RunStore 一致）。</summary>
    public void Prune(HashSet<string> keep)
    {
        foreach (var id in _byRun.Keys.Where(k => !keep.Contains(k)).ToList()) _byRun.Remove(id);
    }
}

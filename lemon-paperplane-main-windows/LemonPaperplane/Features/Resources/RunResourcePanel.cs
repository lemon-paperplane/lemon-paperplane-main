using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperplaneKit;
using static LemonPaperplane.UI;
using RunModel = PaperplaneKit.Run;

namespace LemonPaperplane;

/// <summary>
/// 资源面板的轮询：展开且可见时每 2 秒取一次 run.stats；运行结束后只取一次（保留曲线）。对应 Mac 的 RunStatsPoller。
/// </summary>
public sealed class RunStatsPoller : PPObservable
{
    private RunStats? _stats;
    private DispatcherTimer? _timer;
    private string? _runId;
    private bool _inFlight;

    public RunStats? Stats { get => _stats; private set { _stats = value; Notify(); } }

    public void Start(string runId, bool active)
    {
        if (_runId != runId) _stats = null;
        _runId = runId;
        Stop();
        Fetch();
        if (!active) return;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Fetch(), Dispatcher.CurrentDispatcher);
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private async void Fetch()
    {
        if (_inFlight || _runId is not { } id || AppModel.Shared.Api is not { } api) return;
        _inFlight = true;
        try
        {
            var s = await api.RunStatsAsync(id);
            if (id != _runId) return;
            Stats = s;
            ResourceStore.Shared.Apply(s);
        }
        catch
        {
            // 下一轮再取
        }
        finally
        {
            _inFlight = false;
        }
    }
}

/// <summary>
/// 运行页控制台上方的「资源」面板（可收起，状态记住）：内存与 CPU 趋势图、峰值 / 进程数 / 运行时长 / 端口、进程明细（列表 / 树形）。
/// 展开且可见时每 2 秒取一次 run.stats；运行结束后取最后一次，保留曲线并标「已结束」。对应 Mac 的 RunResourcePanel。
/// 运行的最新状态从 RunStore 取（调用方可以一直复用同一个面板实例）。
/// 面板本身只在展开 / 收起、运行开始 / 结束时重建；标题行的数字、两张趋势图原地更新，事实条与进程表是单独的小视图（随轮询重建），
/// 所以悬浮在图上时竖线不会被每 2 秒的数据打断。
/// </summary>
public sealed class RunResourcePanel : PPView
{
    private readonly RunModel _initial;
    private readonly RunResources _res;
    private readonly RunStatsPoller _poller = new();
    private readonly State<bool> _open;
    private readonly State<bool> _tree;
    private readonly State<long?> _hoverT = new(null);
    private bool? _lastActive;
    private bool? _lastEnded;

    public RunResourcePanel(RunModel run)
    {
        _initial = run;
        _res = ResourceStore.Shared.Resources(run.Id);
        _open = UseState(UiPrefs.Get("ResourcePanelOpen") != "0");
        _tree = new State<bool>(UiPrefs.Get("ProcessTree") == "1");
        // 运行列表变化时只在「运行中 / 已结束」切换时重建
        _onRuns = (_, e) =>
        {
            if (e.PropertyName == nameof(RunStore.Runs) && _lastEnded != !Run.IsActive) Invalidate();
        };
    }

    private readonly System.ComponentModel.PropertyChangedEventHandler _onRuns;

    private RunModel Run => AppModel.Shared.Runs.Runs.TryGetValue(_initial.Id, out var r) ? r : _initial;

    protected override void OnAppear()
    {
        AppModel.Shared.Runs.PropertyChanged -= _onRuns;
        AppModel.Shared.Runs.PropertyChanged += _onRuns;
        if (_lastEnded != null && _lastEnded != !Run.IsActive) Invalidate();
        Restart();
    }

    protected override void OnDisappear()
    {
        AppModel.Shared.Runs.PropertyChanged -= _onRuns;
        _poller.Stop();
    }

    private void Restart()
    {
        _lastActive = Run.IsActive;
        if (_open.Value) _poller.Start(_initial.Id, Run.IsActive);
        else
        {
            _poller.Stop();
            _res.LoadHistoryIfNeeded();
        }
    }

    private void Toggle()
    {
        _open.Value = !_open.Value;
        UiPrefs.Set("ResourcePanelOpen", _open.Value ? "1" : "0");
        Restart();
    }

    protected override UIElement Build()
    {
        var run = Run;
        _lastEnded = !run.IsActive;
        if (IsAppeared && _lastActive != run.IsActive)
            Dispatcher.BeginInvoke(new Action(Restart)); // 运行开始 / 结束：轮询跟着开关
        var col = VStack(0, Header(run));
        col.Align = CrossAlign.Stretch;
        if (_open.Value)
        {
            var body = VStack(M.Md, Charts(), Keep("details", () => new ResourceDetails(this)));
            body.Align = CrossAlign.Stretch;
            body.Padding = new Thickness(M.Lg, 0, M.Lg, M.Md);
            col.Children.Add(body);
        }
        return col.Bg(ColorToken.BgContent).Id("resources.panel");
    }

    private UIElement Header(RunModel run)
    {
        var toggle = HStack(M.Sm,
                new PPIcon(_open.Value ? "chevron-down" : "chevron-right", M.IconSmall, ColorToken.IconSecondary),
                new PPIcon("activity", M.IconSmall, ColorToken.IconSecondary),
                Text(L.T("resources.title"), Typo.Label).Shrink(false))
            .Bg(ColorToken.Clear).OnClick(Toggle).A11y(L.T("resources.title")).Id("resources.toggle");
        var row = HStack(M.Sm, toggle, new PPInfoButton(L.T("resources.info")),
            !run.IsActive ? new PPBadge(L.T("resources.ended")) : null,
            Spacer(M.Md),
            Keep("values." + _open.Value, () => new HeaderValues(_initial.Id, !_open.Value)));
        row.Padding = new Thickness(M.Lg, 0, M.Lg, 0);
        row.Height = 32;
        return row;
    }

    /// <summary>标题行右侧：（收起时）趋势线 + 当前内存 + CPU，原地更新。</summary>
    private sealed class HeaderValues : LiveResourceView
    {
        private readonly bool _spark;
        private Sparkline? _line;
        private TextBlock? _mem, _cpu;

        public HeaderValues(string runId, bool spark) : base(runId) { _spark = spark; }

        protected override UIElement Make()
        {
            _line = new Sparkline(() => Res.Series.Samples, ResourceMetric.Memory, ResourceChart.Span) { Width = 96, Height = 14 };
            _mem = Text("", Typo.Mono, ResourceMetric.Memory.ColorKey()).Shrink(false);
            _cpu = Text("", Typo.Mono, ResourceMetric.Cpu.ColorKey()).Shrink(false);
            return HStack(M.Sm, _spark ? _line : null, _mem, _cpu);
        }

        protected override void Update()
        {
            if (_mem == null || _cpu == null) return;
            _mem.Text = Format.Memory(Res.RssMB);
            _cpu.Text = Format.Cpu(Res.Cpu);
            _line?.Refresh();
        }
    }

    /// <summary>时间轴右端：运行中取「最后一点」与「现在 - 2 秒」的较大者，结束后取最后一点。</summary>
    private long EndT()
    {
        var last = _res.Series.Last?.T ?? 0;
        return Run.IsActive ? Math.Max(last, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 2000) : last;
    }

    private UIElement Charts() => Keep("charts", () =>
    {
        // 两张图自己订阅数据、原地重画
        var mem = new ResourceChart(ResourceMetric.Memory, _res, EndT, _hoverT);
        var cpu = new ResourceChart(ResourceMetric.Cpu, _res, EndT, _hoverT);
        var row = HStack(M.Xl, mem.Flex(), cpu.Flex());
        row.Align = CrossAlign.Stretch;
        row.Height = 110;
        return row;
    });

    /// <summary>事实条（峰值、进程数、运行时长、端口）与进程明细表：随轮询结果与资源数据重建。</summary>
    private sealed class ResourceDetails : PPView
    {
        private readonly RunResourcePanel _panel;
        private readonly HashSet<int> _collapsed = new();
        private double _segWidth = 88;

        public ResourceDetails(RunResourcePanel panel)
        {
            _panel = panel;
            Observe(panel._res);
            Observe(panel._poller);
            Observe(panel._tree);
        }

        protected override UIElement Build()
        {
            var col = VStack(M.Md, Facts(_panel.Run), Processes());
            col.Align = CrossAlign.Stretch;
            return col;
        }

        private UIElement Facts(RunModel run)
        {
            var st = _panel._poller.Stats;
            var res = _panel._res;
            var ports = st != null && st.Ports.Count > 0 ? st.Ports : run.Ports;
            string uptime;
            if (st is { UptimeSec: > 0 }) uptime = Format.Duration(st.UptimeSec);
            else if (run.StartedAt is { } start) uptime = Format.Duration((run.EndedAt ?? DateTimeOffset.Now) - start);
            else uptime = "-";
            return Flow(M.Md,
                Fact(L.T("resources.peak"), Format.Memory(Math.Max(res.PeakRSSMB, run.PeakRSSMB))),
                Fact(L.T("resources.procs"), Math.Max(res.Procs, st?.Processes.Count ?? 0).ToString()),
                Fact(L.T("resources.uptime"), uptime),
                ports.Count > 0 ? Fact(L.T("resources.ports"), string.Join(", ", ports)) : null);
        }

        private static UIElement Fact(string k, string v) =>
            Box(HStack(M.Xs, Text(k, Typo.Small, ColorToken.TextSecondary).Shrink(false), Text(v, Typo.Mono).Shrink(false)))
                .Pad(M.Sm, 0).H(22).Rounded(M.RadiusSM).Bg(ColorToken.BgSubtle);

        private UIElement? Processes()
        {
            var list = _panel._poller.Stats?.Processes ?? new List<RunProcess>();
            if (list.Count == 0) return null;
            var tree = _panel._tree;
            var seg = new PPSegmented<bool>(new[] { (false, L.T("resources.view.list")), (true, L.T("resources.view.tree")) },
                Bind.Of(() => tree.Value, v => { tree.Value = v; UiPrefs.Set("ProcessTree", v ? "1" : "0"); }), PPButtonSize.Small).Id("resources.view");
            // 行尾留出和分段控件一样宽的空白，数字列与表头对齐
            seg.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _segWidth = seg.DesiredSize.Width;
            var head = HStack(M.Md,
                Text(L.T("resources.process"), Typo.Label).Flex(),
                Text("PID", Typo.Label).W(64).Align(TextAlignment.Right).Shrink(false),
                Text(L.T("resources.cpu"), Typo.Label).W(64).Align(TextAlignment.Right).Shrink(false),
                Text(L.T("resources.memory"), Typo.Label).W(84).Align(TextAlignment.Right).Shrink(false),
                seg);
            head.Fg(ColorToken.TextSecondary);
            head.Padding = new Thickness(M.Md, 0, M.Md, 0);
            head.Height = M.RowHeight;
            head.Bg(ColorToken.BgSubtle);

            var rows = VStack(0);
            rows.Align = CrossAlign.Stretch;
            if (tree.Value)
                foreach (var n in ProcessTree.Flatten(list, _collapsed)) rows.Children.Add(ProcessRow(n.Process, n.Depth, n.HasChildren, n.SubtreeRSSMB));
            else
                foreach (var p in list.OrderByDescending(p => p.RssMB)) rows.Children.Add(ProcessRow(p, 0, false, null));
            var scroll = Scroll(rows).Key("resources.processes");
            scroll.MaxHeight = 150;

            var col = VStack(0, head, new PPDivider(subtle: true), scroll);
            col.Align = CrossAlign.Stretch;
            var box = Box(col).Rounded(M.RadiusMD).Outline(ColorToken.Border);
            RoundedClip(col, M.RadiusMD - 1);
            return box;
        }

        private UIElement ProcessRow(RunProcess p, int depth, bool hasChildren, double? subtree)
        {
            var name = HStack(M.Xs, depth > 0 ? Gap(depth * 14) : null);
            if (_panel._tree.Value)
            {
                if (hasChildren)
                {
                    var pid = p.Pid;
                    name.Children.Add(Box(new PPIcon(_collapsed.Contains(pid) ? "chevron-right" : "chevron-down", 12, ColorToken.IconSecondary).Center())
                        .Size(14, 14).Bg(ColorToken.Clear).OnClick(() =>
                        {
                            if (!_collapsed.Remove(pid)) _collapsed.Add(pid);
                            Invalidate();
                        }));
                }
                else name.Children.Add(Gap(14));
            }
            name.Children.Add(TextMiddle(p.Name, Typo.MonoSmall));
            var mem = Text(Format.Memory(p.RssMB), Typo.MonoSmall).W(84).Align(TextAlignment.Right).Shrink(false)
                .Tip(subtree is { } s ? L.T("resources.subtree", Format.Memory(s)) : null);
            var row = HStack(M.Md,
                name.Flex(),
                Text(p.Pid.ToString(), Typo.MonoSmall, ColorToken.TextTertiary).W(64).Align(TextAlignment.Right).Shrink(false),
                Text(Format.Cpu(p.Cpu), Typo.MonoSmall).W(64).Align(TextAlignment.Right).Shrink(false),
                mem,
                Gap(_segWidth).H(1));
            row.Padding = new Thickness(M.Md, 0, M.Md, 0);
            row.Height = M.RowHeightCompact;
            return row;
        }
    }

    /// <summary>圆角裁剪（表头底色不超出圆角描边）。</summary>
    internal static void RoundedClip(FrameworkElement el, double radius)
    {
        var clip = new RectangleGeometry { RadiusX = radius, RadiusY = radius };
        el.SizeChanged += (_, e) => clip.Rect = new Rect(e.NewSize);
        el.Clip = clip;
    }
}

/// <summary>「运行中」页顶部汇总：运行中工程数、合计内存、合计 CPU，可按内存排序（对应 Mac RunningSummaryBar）。</summary>
public sealed class RunningSummaryBar : PPView
{
    private readonly Bind<bool> _sortByMemory;
    private readonly ResourceStore _store = ResourceStore.Shared;

    public RunningSummaryBar(Bind<bool> sortByMemory)
    {
        _sortByMemory = sortByMemory;
        Observe(_store, nameof(ResourceStore.Totals));
    }

    protected override UIElement Build()
    {
        var t = _store.Totals;
        var row = HStack(M.Xl,
            Stat(L.T("resources.runningProjects"), t.Projects.ToString(), ColorToken.TextPrimary),
            Stat(L.T("resources.totalMemory"), Format.Memory(t.RssMB), ResourceMetric.Memory.ColorKey()),
            Stat(L.T("resources.totalCPU"), Format.Cpu(t.Cpu), ResourceMetric.Cpu.ColorKey()),
            new PPInfoButton(L.T("resources.info")),
            Spacer(),
            Text(L.T("resources.sort"), Typo.Small, ColorToken.TextSecondary).Shrink(false),
            new PPSegmented<bool>(new[] { (false, L.T("resources.sort.default")), (true, L.T("resources.memory")) }, _sortByMemory, PPButtonSize.Small)
                .Id("running.sort"));
        row.Padding = new Thickness(M.Xl, M.Md, M.Xl, M.Md);
        return row.Id("running.summary");
    }

    private static UIElement Stat(string title, string value, string color) =>
        VStack(2, Text(title, Typo.Caption, ColorToken.TextSecondary), Text(value, Typo.Heading, color).TabularDigits().Shrink(false));
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 资源监控的小组件：趋势线、带悬浮详情的资源数字、趋势图。全部自绘（OnRender），不引第三方库。
// 配色：内存用强调色（accent），CPU 用 warning 语气色；数字格式沿用 Format.Memory / Format.Cpu。
// 这些小视图都「原地更新」：订阅自己那个运行的 RunResources，数据变了只改文字、重画趋势线，不重建元素树
// （卡片网格、侧栏、运行列表不因 stats 重绘；悬浮详情的锚点也不会因为重建被换掉）。对应 Mac 的 ResourceViews.swift。

public enum ResourceMetric { Memory, Cpu }

public static class ResourceMetricExt
{
    public static double Value(this ResourceMetric m, ResourceSample s) => m == ResourceMetric.Memory ? s.RssMB : s.Cpu;

    /// <summary>颜色键：内存 accent、CPU warning。</summary>
    public static string ColorKey(this ResourceMetric m) => m == ResourceMetric.Memory ? ColorToken.Accent : ColorToken.Warning;

    public static string Format(this ResourceMetric m, double v) => m == ResourceMetric.Memory ? PaperplaneKit.Format.Memory(v) : PaperplaneKit.Format.Cpu(v);

    /// <summary>纵轴最小上限：内存 64 MB、CPU 10%，避免几乎不变的数据被放大成锯齿。</summary>
    public static double MinScale(this ResourceMetric m) => m == ResourceMetric.Memory ? 64 : 10;

    /// <summary>面积填充的不透明度：亮色 14%、暗色 22%。</summary>
    public static double AreaOpacity => Theme.IsDark ? 0.22 : 0.14;

    internal static Brush Solid(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    internal static Point P(KitPoint p) => new(p.X, p.Y);

    /// <summary>线与下方面积（线宽 lineWidth，圆头圆角）。</summary>
    internal static void DrawSeries(DrawingContext dc, List<KitPoint> pts, double bottom, Color color, double lineWidth)
    {
        if (pts.Count == 0) return;
        var first = pts[0];
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(first), false, false);
            if (pts.Count == 1) g.LineTo(new Point(first.X - 1, first.Y), true, true);
            else g.PolyLineTo(pts.Skip(1).Select(P).ToList(), true, true);
        }
        line.Freeze();
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(P(first), true, true);
            if (pts.Count == 1) g.LineTo(new Point(first.X - 1, first.Y), false, false);
            else g.PolyLineTo(pts.Skip(1).Select(P).ToList(), false, false);
            var last = pts.Count == 1 ? new KitPoint(first.X - 1, first.Y) : pts[pts.Count - 1];
            g.LineTo(new Point(last.X, bottom), false, false);
            g.LineTo(new Point(first.X, bottom), false, false);
        }
        area.Freeze();
        dc.DrawGeometry(Solid(color, AreaOpacity), null, area);
        var pen = new Pen(Solid(color), lineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>
/// 趋势线（细线 + 浅色面积）。数据由 source 提供（读 RunResources.Series），数据变了调用 Refresh() 重画。对应 Mac 的 Sparkline。
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly Func<IReadOnlyList<ResourceSample>> _source;
    private readonly ResourceMetric _metric;
    private readonly long _span;
    private readonly double _lineWidth;

    /// <param name="span">时间轴跨度（毫秒）；0 表示按已有点铺满</param>
    public Sparkline(Func<IReadOnlyList<ResourceSample>> source, ResourceMetric metric, long span = 0, double lineWidth = 1.25)
    {
        _source = source;
        _metric = metric;
        _span = span;
        _lineWidth = lineWidth;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        EventHandler onTheme = (_, _) => InvalidateVisual();
        Loaded += (_, _) => { Theme.Changed -= onTheme; Theme.Changed += onTheme; InvalidateVisual(); };
        Unloaded += (_, _) => Theme.Changed -= onTheme;
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var samples = _source();
        double w = ActualWidth, h = ActualHeight;
        if (samples.Count == 0 || w <= 0 || h <= 0) return;
        var maxV = Math.Max(_metric.MinScale(), samples.Max(s => _metric.Value(s)));
        var rect = new KitRect(0, _lineWidth, w, Math.Max(0, h - 2 * _lineWidth));
        var pts = SparklineGeometry.Points(samples, s => _metric.Value(s), rect, _span, maxValue: maxV);
        ResourceMetricExt.DrawSeries(dc, pts, h, Theme.Color(_metric.ColorKey()), _lineWidth);
    }
}

/// <summary>
/// 订阅一个 RunResources、原地更新的小视图基类：元素在构造时建好，Build 每次返回同一棵树；数据变化只调用 Update。
/// </summary>
public abstract class LiveResourceView : PPView
{
    protected readonly RunResources Res;
    private readonly PropertyChangedEventHandler _onChange;
    private UIElement? _root;

    protected LiveResourceView(string runId)
    {
        Res = ResourceStore.Shared.Resources(runId);
        _onChange = (_, _) => Update();
    }

    /// <summary>建一次元素树。</summary>
    protected abstract UIElement Make();

    /// <summary>按当前数据刷新文字与趋势线。</summary>
    protected abstract void Update();

    protected override UIElement Build()
    {
        _root ??= Make();
        Update();
        return _root;
    }

    protected override void OnAppear()
    {
        Res.PropertyChanged -= _onChange;
        Res.PropertyChanged += _onChange;
        Update();
    }

    protected override void OnDisappear() => Res.PropertyChanged -= _onChange;
}

/// <summary>某个运行的内存数字（+ 可选 CPU），只观察这个运行自己的数据（对应 Mac RunResourceText）。</summary>
public sealed class RunResourceText : LiveResourceView
{
    private readonly bool _showCpu;
    private readonly double _fallbackCpu, _fallbackRss;
    private TextBlock? _cpu, _mem;

    public RunResourceText(string runId, bool showCPU = true, double? fallbackCpu = null, double? fallbackRss = null) : base(runId)
    {
        _showCpu = showCPU;
        _fallbackCpu = fallbackCpu ?? 0;
        _fallbackRss = fallbackRss ?? 0;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override UIElement Make()
    {
        _cpu = Text("", Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false);
        _mem = Text("", Typo.MonoSmall, ColorToken.TextSecondary).Shrink(false);
        return HStack(M.Sm, _showCpu ? _cpu : null, _mem);
    }

    protected override void Update()
    {
        if (_mem == null || _cpu == null) return;
        var has = Res.T > 0;
        _mem.Text = Format.Memory(has ? Res.RssMB : _fallbackRss);
        _cpu.Text = Format.Cpu(has ? Res.Cpu : _fallbackCpu);
    }
}

/// <summary>卡片 / 弹出框里的资源区：内存趋势线 + 内存 + CPU，悬浮 350ms 显示详情（对应 Mac RunResourceInline）。</summary>
public sealed class RunResourceInline : LiveResourceView, IVisualStateful
{
    private readonly DateTimeOffset? _startedAt;
    private readonly double _sparkWidth;
    private readonly bool _showCpu;
    private Sparkline? _spark;
    private TextBlock? _mem, _cpu;
    private DispatcherTimer? _hoverTimer;
    private PPPopup? _popup;

    public RunResourceInline(string runId, DateTimeOffset? startedAt, double sparkWidth = 64, bool showCPU = true) : base(runId)
    {
        _startedAt = startedAt;
        _sparkWidth = sparkWidth;
        _showCpu = showCPU;
        VerticalAlignment = VerticalAlignment.Center;
        this.Id("resource.inline");
        MouseEnter += (_, _) => HoverChanged(true);
        MouseLeave += (_, _) => HoverChanged(false);
    }

    protected override UIElement Make()
    {
        _spark = new Sparkline(() => Res.Series.Samples, ResourceMetric.Memory) { Width = _sparkWidth, Height = 14 };
        _mem = Text("", Typo.MonoSmall, ColorToken.TextSecondary).Shrink(false);
        _cpu = Text("", Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false);
        return HStack(M.Sm, _spark, _mem, _showCpu ? _cpu : null).Bg(ColorToken.Clear);
    }

    protected override void Update()
    {
        if (_mem == null || _cpu == null || _spark == null) return;
        _mem.Text = Format.Memory(Res.RssMB);
        _cpu.Text = Format.Cpu(Res.Cpu);
        _spark.Refresh();
    }

    protected override void OnAppear()
    {
        base.OnAppear();
        Res.LoadHistoryIfNeeded();
    }

    protected override void OnDisappear()
    {
        base.OnDisappear();
        _hoverTimer?.Stop();
        _popup?.Close();
    }

    private void HoverChanged(bool inside)
    {
        _hoverTimer?.Stop();
        if (!inside)
        {
            _popup?.Close();
            return;
        }
        _hoverTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Normal, (_, _) =>
        {
            _hoverTimer!.Stop();
            if (IsMouseOver) Open();
        }, Dispatcher);
        _hoverTimer.Start();
    }

    /// <summary>调试桥 force open：不移动鼠标也能打开悬浮详情（截图用）。</summary>
    public void UpdateVisual()
    {
        if (PPDebug.Is(this, "open")) Open();
        else if (!IsMouseOver) _popup?.Close();
    }

    /// <summary>打开悬浮详情。</summary>
    public void Open()
    {
        if (_popup != null || !IsLoaded) return;
        _popup = Popover.Show(this, new PopoverContent(new ResourceDetailPopover(Res, _startedAt), 260), PopupAlignment.Trailing,
            onClose: () => _popup = null);
    }
}

/// <summary>悬浮详情：内存 / CPU（各带趋势线）、峰值 / 进程数 / 运行时长。</summary>
internal sealed class ResourceDetailPopover : PPView
{
    private readonly RunResources _res;
    private readonly DateTimeOffset? _startedAt;

    public ResourceDetailPopover(RunResources res, DateTimeOffset? startedAt)
    {
        _res = res;
        _startedAt = startedAt;
        Observe(res);
    }

    protected override UIElement Build()
    {
        var uptime = _startedAt is { } s ? Format.Duration(DateTimeOffset.Now - s) : "-";
        var kv = VStack(M.Xs,
            KeyValue(L.T("resources.peak"), Format.Memory(_res.PeakRSSMB)),
            KeyValue(L.T("resources.procs"), _res.Procs.ToString()),
            KeyValue(L.T("resources.uptime"), uptime));
        kv.Align = CrossAlign.Stretch;
        var col = VStack(M.Sm, Row(L.T("resources.memory"), Format.Memory(_res.RssMB), ResourceMetric.Memory),
            Row(L.T("resources.cpu"), Format.Cpu(_res.Cpu), ResourceMetric.Cpu), new PPDivider(subtle: true), kv);
        col.Align = CrossAlign.Stretch;
        return col.Id("resource.detail");
    }

    /// <summary>键值行（键 small secondary、值 mono；同 Mac 在 PPKeyValueList 外面套 .font(.small)）。</summary>
    private static UIElement KeyValue(string k, string v) =>
        HStack(M.Md, Text(k, Typo.Small, ColorToken.TextSecondary).W(80).Shrink(false), Text(v, Typo.Mono, ColorToken.TextPrimary));

    private UIElement Row(string title, string value, ResourceMetric metric)
    {
        var head = HStack(0, Text(title, Typo.Small, ColorToken.TextSecondary), Spacer(), Text(value, Typo.Mono).Shrink(false));
        var spark = new Sparkline(() => _res.Series.Samples, metric, 300_000) { Height = 28 };
        var col = VStack(3, head, spark);
        col.Align = CrossAlign.Stretch;
        return col;
    }
}

/// <summary>
/// 趋势图（运行页资源面板）：固定 5 分钟时间轴，纵轴取整齐上限、上限与一半处虚线参考线；
/// 悬浮时显示该时刻的值（hoverT 与另一张图共享，两张图联动竖线）。对应 Mac 的 ResourceChart。
/// </summary>
public sealed class ResourceChart : Grid, IVisualStateful
{
    public const long Span = 300_000;

    private readonly ResourceMetric _metric;
    private readonly RunResources _res;
    private readonly Func<long> _endT;
    private readonly State<long?> _hoverT;
    private readonly TextBlock _time;
    private readonly TextBlock _value;
    private readonly TextBlock _maxLabel;
    private readonly ChartCanvas _canvas;
    private readonly TextBlock _title;
    private readonly TextBlock _past;
    private readonly TextBlock _now;

    public ResourceChart(ResourceMetric metric, RunResources res, Func<long> endT, State<long?> hoverT)
    {
        _metric = metric;
        _res = res;
        _endT = endT;
        _hoverT = hoverT;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _title = Text("", Typo.Small, ColorToken.TextSecondary);
        _time = Text("", Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false);
        _value = Text("-", Typo.Mono).Shrink(false);
        var dot = Box().Size(6, 6).Rounded(3).Bg(metric.ColorKey());
        var head = HStack(M.Sm, dot, _title, Spacer(), _time, _value).Margins(0, 0, 0, M.Xs);

        _canvas = new ChartCanvas(this);
        _maxLabel = Text("", Typo.Caption, ColorToken.TextTertiary).Shrink(false);
        _maxLabel.Margin = new Thickness(2, 0, 0, 0);
        _maxLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _maxLabel.VerticalAlignment = VerticalAlignment.Top;
        _maxLabel.IsHitTestVisible = false;
        var plot = new Grid();
        plot.Children.Add(_canvas);
        plot.Children.Add(_maxLabel);

        _past = Text("", Typo.Caption, ColorToken.TextTertiary);
        _now = Text("", Typo.Caption, ColorToken.TextTertiary).Shrink(false);
        var axis = HStack(0, _past, Spacer(), _now).Margins(0, M.Xs, 0, 0);

        Children.Add(head.At(0, 0));
        Children.Add(plot.At(1, 0));
        Children.Add(axis.At(2, 0));
        this.Id(metric == ResourceMetric.Memory ? "resources.chart.memory" : "resources.chart.cpu");

        PropertyChangedEventHandler onData = (_, _) => Refresh();
        EventHandler onTheme = (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            _res.PropertyChanged -= onData; _res.PropertyChanged += onData;
            _hoverT.PropertyChanged -= onData; _hoverT.PropertyChanged += onData;
            Theme.Changed -= onTheme; Theme.Changed += onTheme;
            L10n.Shared.Changed -= onTheme; L10n.Shared.Changed += onTheme;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _res.PropertyChanged -= onData;
            _hoverT.PropertyChanged -= onData;
            Theme.Changed -= onTheme;
            L10n.Shared.Changed -= onTheme;
        };
    }

    private IReadOnlyList<ResourceSample> Samples => _res.Series.Samples;

    internal long EndT => _endT();

    internal double MaxValue => SparklineGeometry.NiceCeiling(Math.Max(_metric.MinScale(), Samples.Count == 0 ? 0 : Samples.Max(s => _metric.Value(s))));

    /// <summary>离悬浮时刻最近的采样。</summary>
    internal ResourceSample? Hovered
    {
        get
        {
            if (_hoverT.Value is not long t || Samples.Count == 0) return null;
            ResourceSample best = Samples[0];
            foreach (var s in Samples) if (Math.Abs(s.T - t) < Math.Abs(best.T - t)) best = s;
            return best;
        }
    }

    /// <summary>数据、悬浮或语言变化：刷新文字并重画。</summary>
    public void Refresh()
    {
        var hovered = Hovered;
        var shown = hovered ?? (Samples.Count > 0 ? Samples[Samples.Count - 1] : null);
        _time.Text = hovered != null ? DateTimeOffset.FromUnixTimeMilliseconds(hovered.T).ToLocalTime().ToString("HH:mm:ss") : "";
        _time.Visibility = hovered != null ? Visibility.Visible : Visibility.Collapsed;
        _value.Text = shown != null ? _metric.Format(_metric.Value(shown)) : "-";
        _maxLabel.Text = _metric.Format(MaxValue);
        _title.Text = L.T(_metric == ResourceMetric.Memory ? "resources.memory" : "resources.cpu");
        _past.Text = L.T("resources.axis.past");
        _now.Text = L.T("resources.axis.now");
        _canvas.InvalidateVisual();
    }

    internal void SetHover(long? t) => _hoverT.Value = t;

    /// <summary>调试桥 force hover：不移动鼠标也能显示悬浮竖线（悬浮在时间轴三分之二处，截图用）。</summary>
    public void UpdateVisual()
    {
        if (PPDebug.Is(this, "hover")) SetHover(EndT - Span / 3);
        else if (!IsMouseOver) SetHover(null);
    }

    /// <summary>图区：参考线、基线、曲线与面积、悬浮竖线与圆点。</summary>
    private sealed class ChartCanvas : FrameworkElement
    {
        private readonly ResourceChart _chart;

        public ChartCanvas(ResourceChart chart)
        {
            _chart = chart;
            SnapsToDevicePixels = true;
            Focusable = false;
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var w = Math.Max(ActualWidth, 1);
            var frac = Math.Min(Math.Max(e.GetPosition(this).X / w, 0), 1);
            var end = _chart.EndT;
            _chart.SetHover(end - Span + (long)(frac * Span));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _chart.SetHover(null);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            // 参考线：上限与一半
            var dash = new Pen(ResourceMetricExt.Solid(Theme.Color(ColorToken.BorderSubtle)), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dash.Freeze();
            foreach (var f in new[] { 0.0, 0.5 })
            {
                var y = Math.Round(h * f) + 0.5;
                dc.DrawLine(dash, new Point(0, y), new Point(w, y));
            }
            var basePen = new Pen(ResourceMetricExt.Solid(Theme.Color(ColorToken.Border)), 1);
            basePen.Freeze();
            dc.DrawLine(basePen, new Point(0, h - 0.5), new Point(w, h - 0.5));

            var samples = _chart.Samples;
            if (samples.Count == 0) return;
            var metric = _chart._metric;
            var color = Theme.Color(metric.ColorKey());
            var pts = SparklineGeometry.Points(samples, s => metric.Value(s), new KitRect(0, 1, w, Math.Max(0, h - 2)),
                Span, _chart.EndT, _chart.MaxValue / 1.1);
            ResourceMetricExt.DrawSeries(dc, pts, h, color, 1.5);

            if (_chart.Hovered is { } hv)
            {
                int i = -1;
                for (int k = 0; k < samples.Count; k++) if (ReferenceEquals(samples[k], hv)) { i = k; break; }
                if (i >= 0 && i < pts.Count)
                {
                    var p = ResourceMetricExt.P(pts[i]);
                    var guide = new Pen(ResourceMetricExt.Solid(Theme.Color(ColorToken.TextTertiary)), 1);
                    guide.Freeze();
                    var gx = Math.Round(p.X) + 0.5;
                    dc.DrawLine(guide, new Point(gx, 0), new Point(gx, h));
                    var ring = new Pen(ResourceMetricExt.Solid(color), 1.5);
                    ring.Freeze();
                    dc.DrawEllipse(ResourceMetricExt.Solid(Theme.Color(ColorToken.BgSurface)), ring, p, 3, 3);
                }
            }
        }
    }
}

// 插件界面描述语言（sdk.ViewNode）渲染器（对应 Mac Renderers/ViewNodeRenderer.swift）。全部节点类型见 lemon-paperplane-main-plugin-sdk/view.go。
// 事件走 view.event（带 selection / selected）；处理 refresh / state / message / openUrl / reimport；refreshMs 定时刷新（不可见时暂停）。
// kind: webview 先显示「打开」空状态，用户点了才取地址并创建 WebviewHost。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;
using DocRun = System.Windows.Documents.Run;

namespace LemonPaperplane;

/// <summary>插件视图的数据：view.render、事件、定时刷新，以及界面上的勾选、表单值、标签页、展开状态。对应 Mac PluginViewModel。</summary>
public sealed class PluginViewModel : PPObservable
{
    private ViewNode? _root;
    private string? _error;
    private bool _loading;
    private string? _busyAction;

    public ViewNode? Root { get => _root; private set { _root = value; Notify(); } }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }
    public string? BusyAction { get => _busyAction; private set => Set(ref _busyAction, value); }

    /// <summary>表格勾选：表格 id → 行 id</summary>
    public Dictionary<string, HashSet<string>> Selections { get; } = new();
    /// <summary>表单值：表单 id（或 action）→ 值</summary>
    public Dictionary<string, Dictionary<string, JToken>> FormValues { get; } = new();
    /// <summary>标签页节点选中的序号、树节点展开状态、区块折叠状态</summary>
    public Dictionary<string, int> TabIndex { get; } = new();
    public Dictionary<string, bool> Expanded { get; } = new();
    public Dictionary<string, bool> Collapsed { get; } = new();

    public ViewContext? Context { get; private set; }
    private Dictionary<string, JToken>? _state;
    private DispatcherTimer? _timer;
    private int _refreshMs;
    private bool _visible;
    private int _renderGen;

    /// <summary>界面状态变了（勾选、标签、展开）：只重建节点树。</summary>
    public void Touch() => Notify(nameof(Selections));

    public void Start(ViewContext ctx)
    {
        bool changed = Context == null || !Same(ctx, Context);
        Context = ctx;
        _visible = true;
        if (changed || _root == null)
        {
            if (changed)
            {
                _state = ctx.InitialState?.ToDictionary(kv => kv.Key, kv => kv.Value);
                FormValues.Clear();
                Selections.Clear();
            }
            _ = Render();
        }
        else
        {
            ScheduleTimer();
        }
    }

    private static bool Same(ViewContext a, ViewContext b) =>
        a.Plugin == b.Plugin && a.View == b.View && a.ProjectId == b.ProjectId && a.ModuleId == b.ModuleId && a.RunId == b.RunId;

    public void Stop()
    {
        _visible = false;
        _timer?.Stop();
        _timer = null;
    }

    public async Task Render()
    {
        if (Context is not { } ctx || AppModel.Shared.Api is not { } api) return;
        int gen = ++_renderGen;
        Loading = _root == null;
        try
        {
            var r = await api.RenderViewAsync(ctx, _state);
            if (!ReferenceEquals(ctx, Context) || gen != _renderGen) return;
            if (r.State != null) _state = r.State;
            _refreshMs = r.RefreshMs > 0 ? Math.Max(1000, r.RefreshMs) : 0;
            Error = null;
            Root = r.Root;
        }
        catch (Exception e)
        {
            if (_root == null) Error = e.Message;
        }
        Loading = false;
        ScheduleTimer();
    }

    private void ScheduleTimer()
    {
        _timer?.Stop();
        _timer = null;
        if (!_visible || _refreshMs <= 0) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_refreshMs) };
        t.Tick += async (_, _) =>
        {
            t.Stop();
            if (!_visible || _timer != t) return;
            await Render();
        };
        _timer = t;
        t.Start();
    }

    /// <summary>触发事件；有 confirm 时先确认。</summary>
    public void Fire(string? action, Dictionary<string, JToken>? values = null, string? confirm = null, string? title = null)
    {
        if (string.IsNullOrEmpty(action)) return;
        values ??= new Dictionary<string, JToken>();
        if (!string.IsNullOrEmpty(confirm))
        {
            Dialogs.Confirm(title ?? confirm!, title == null ? null : confirm, () => Send(action!, values), ok: title ?? L.T("common.ok"));
            return;
        }
        _ = SendReporting(action!, values);
    }

    private async Task SendReporting(string action, Dictionary<string, JToken> values)
    {
        try { await Send(action, values); }
        catch (Exception e) { AppModel.Shared.Toasts.Error(e); }
    }

    private async Task Send(string action, Dictionary<string, JToken> values)
    {
        if (Context is not { } ctx) return;
        var v = new Dictionary<string, JToken>(values);
        // 带上勾选状态
        var sel = Selections.Where(kv => kv.Value.Count > 0).ToList();
        if (sel.Count > 0)
        {
            var o = new JObject();
            foreach (var kv in sel) o[kv.Key] = JSONValue.Of(kv.Value.OrderBy(x => x, StringComparer.Ordinal));
            v["selection"] = o;
            if (!v.ContainsKey("selected")) v["selected"] = JSONValue.Of(sel.SelectMany(kv => kv.Value).OrderBy(x => x, StringComparer.Ordinal));
        }
        BusyAction = action;
        try
        {
            var r = await AppModel.Shared.Client().ViewEventAsync(ctx, action, v, _state);
            if (r.State != null) _state = r.State;
            if (!string.IsNullOrEmpty(r.Message)) AppModel.Shared.Toasts.Show("info", r.Message!);
            if (!string.IsNullOrEmpty(r.OpenUrl)) Actions.OpenURL(r.OpenUrl!);
            if (r.Reimport && ctx.ProjectId is { } pid) Actions.Reimport(pid);
            if (r.Refresh) await Render();
        }
        finally
        {
            BusyAction = null;
        }
    }

    /// <summary>节点的 data 作为事件 values。</summary>
    public static Dictionary<string, JToken> DataValues(ViewNode n) =>
        (n.Data ?? new Dictionary<string, string>()).ToDictionary(kv => kv.Key, kv => JSONValue.Of(kv.Value));
}

/// <summary>插件视图宿主（标签页、区块、运行侧栏、全局页、设置页通用）。对应 Mac PluginViewHost。</summary>
public sealed class PluginViewHost : PPView
{
    private readonly ViewInfo _view;
    private readonly ViewContext _context;
    private readonly bool _scrolls;
    private readonly PluginViewModel _model = new();
    private readonly State<Uri?> _webviewUrl;
    private bool _webviewLoading;

    /// <param name="scrolls">false 时不自带滚动（嵌在已经能滚动的页面里，如概览区块、设置页）</param>
    public PluginViewHost(ViewInfo view, ViewContext context, bool scrolls = true)
    {
        _view = view;
        _context = context;
        _scrolls = scrolls;
        _webviewUrl = UseState<Uri?>(null);
        Observe(_model, nameof(PluginViewModel.Error), nameof(PluginViewModel.Loading));
        Observe(_model, "Root");
    }

    protected override void OnAppear()
    {
        if (!_view.IsWebview) _model.Start(_context);
    }

    protected override void OnDisappear() => _model.Stop();

    protected override UIElement Build()
    {
        if (_view.IsWebview) return Webview();
        if (_model.Root != null)
        {
            // 节点树单独一个视图：勾选、标签切换只重建它，不动外面的滚动区域
            var tree = Keep("tree", () => new PluginNodeTree(_model));
            if (!_scrolls) return Box(tree);
            var inner = Box(tree).Pad(M.Xl);
            return Box(Keep("scroll", () => Scroll(Gap(0)).Key("plugin.scroll"), sv => sv.Content = inner));
        }
        if (_model.Error is { } e)
        {
            var alert = new PPAlert("danger", L.T("plugin.viewFailed"), e,
                new PPButton(L.T("connection.retry"), () => _ = _model.Render(), size: PPButtonSize.Small));
            alert.Margin = new Thickness(M.Xl);
            alert.VerticalAlignment = VerticalAlignment.Top;
            return alert;
        }
        var spinner = Box(new PPSpinner().Center()).Pad(M.Xl);
        if (_scrolls) spinner.VerticalAlignment = VerticalAlignment.Stretch;
        return spinner;
    }

    private UIElement Webview()
    {
        if (_webviewUrl.Value is { } url) return Box(Keep("webview:" + url, () => new WebviewHost(url, _context)));
        // 只有用户点了「打开」才创建 WebView
        return new PPEmptyState("app-window", _view.Title, L.T("plugin.openWebview"), OpenWebview, "external-link");
    }

    private async void OpenWebview()
    {
        if (_webviewLoading) return;
        _webviewLoading = true;
        try
        {
            var u = await AppModel.Shared.Client().WebviewURLAsync(_context);
            if (u == null) AppModel.Shared.Toasts.Show("error", L.T("plugin.viewFailed"));
            _webviewUrl.Value = u;
        }
        catch (Exception e)
        {
            AppModel.Shared.Toasts.Error(e, L.T("plugin.viewFailed"));
        }
        _webviewLoading = false;
    }
}

/// <summary>递归渲染节点树（对应 Mac NodeView）。表单与内嵌控制台跨重建复用，输入不被打断。</summary>
internal sealed class PluginNodeTree : PPView
{
    private readonly PluginViewModel _model;

    public PluginNodeTree(PluginViewModel model)
    {
        _model = model;
        Observe(model, "Root", nameof(PluginViewModel.BusyAction), nameof(PluginViewModel.Selections));
    }

    private string? Plugin => _model.Context?.Plugin;

    protected override UIElement Build()
    {
        if (_model.Root is not { } root) return Gap(0);
        var el = Node(root, "r");
        if (el is FrameworkElement fe && root.Type == "column") fe.HorizontalAlignment = HorizontalAlignment.Stretch;
        return el;
    }

    private IconKey? Icon(string? raw) => IconKey.Parse(raw, Plugin);

    private static string KeyOf(ViewNode n, string path) => n.Id ?? path;

    private List<UIElement> Children(ViewNode n, string path) =>
        n.Children.Select((c, i) => Node(c, path + "." + i)).ToList();

    private UIElement Node(ViewNode n, string path)
    {
        var key = KeyOf(n, path);
        switch (n.Type)
        {
            case "column":
            {
                var col = VStack(M.Md, Children(n, path));
                col.HorizontalAlignment = HorizontalAlignment.Stretch;
                return col;
            }
            case "row":
                return HStack(M.Md, Children(n, path));
            case "section":
            {
                var body = VStack(M.Md, Children(n, path));
                body.Align = CrossAlign.Stretch;
                var bind = Bind.Of(() => _model.Collapsed.TryGetValue(key, out var c) ? c : n.Collapsed, v => _model.Collapsed[key] = v);
                var sec = new PPSection(n.Title ?? "", body, info: n.Info, collapsible: true, collapsedBind: bind);
                sec.Margin = new Thickness(0, M.Xs, 0, 0);
                sec.HorizontalAlignment = HorizontalAlignment.Stretch;
                return sec;
            }
            case "divider":
                return new PPDivider(subtle: true);
            case "spacer":
                return Spacer(M.Md);
            case "tabs":
                return Tabs(n, path, key);
            case "heading":
                return HStack(M.Sm, Text(n.Text ?? n.Title ?? "", Typo.Heading), n.Info != null ? new PPInfoButton(n.Info) : null);
            case "text":
            {
                var t = Selectable(n.Text ?? "", n.Mono ? Typo.Mono : Typo.Body, n.Tone == null ? ColorToken.TextPrimary : Theme.Tone(n.Tone).Fg);
                var row = HStack(M.Sm, t, n.Info != null ? new PPInfoButton(n.Info).Top() : null);
                row.Align = CrossAlign.Start;
                return row;
            }
            case "code":
            {
                var t = Selectable(n.Text ?? "", Typo.Mono, ColorToken.TextPrimary, wrap: false);
                var sv = Scroll(Box(t).Pad(M.Md), horizontal: true, vertical: false);
                var box = Box(sv).Rounded(M.RadiusMD).Bg(ColorToken.BgCode).Outline(ColorToken.BorderSubtle);
                box.HorizontalAlignment = HorizontalAlignment.Stretch;
                return box;
            }
            case "markdown":
                return MarkdownText.Make(n.Text ?? "");
            case "kv":
                return new PPKeyValueList(n.Items.Select(i => new PPKeyValueRow(i.Key, i.Value, i.Mono, i.Tone, i.Href))).FillW();
            case "badge":
                return new PPBadge(n.Text ?? "", n.Tone, iconRef: Icon(n.Icon));
            case "alert":
            {
                var actions = n.Children.Count > 0 ? HStack(M.Sm, Children(n, path)) : null;
                return new PPAlert(n.Tone ?? "info", n.Title ?? n.Text ?? "", n.Title == null ? null : n.Text, actions).FillW();
            }
            case "stat":
            {
                var col = VStack(2,
                    HStack(M.Xs, Text(n.Title ?? "", Typo.Small, ColorToken.TextSecondary), n.Info != null ? new PPInfoButton(n.Info) : null),
                    Text(n.Text ?? "", Typo.Stat, n.Tone == null ? ColorToken.TextPrimary : Theme.Tone(n.Tone).Fg));
                return Box(col).Pad(M.Md).MinW(120).Rounded(M.RadiusMD).Bg(ColorToken.BgSubtle);
            }
            case "progress":
            {
                var col = VStack(M.Xs);
                col.Align = CrossAlign.Stretch;
                if (n.Title != null || n.Text != null)
                    col.Children.Add(HStack(0, Text(n.Title ?? "", Typo.Small), Spacer(),
                        Text(n.Text ?? (n.Value >= 0 ? ((int)n.Value) + "%" : ""), Typo.Small, ColorToken.TextSecondary)));
                col.Children.Add(new PPProgressBar(n.Value, tone: n.Tone));
                col.HorizontalAlignment = HorizontalAlignment.Stretch;
                return col;
            }
            case "empty":
            {
                var col = VStack(M.Md,
                    new PPIcon(Icon(n.Icon ?? "builtin:inbox"), M.IconEmpty, ColorToken.IconSecondary).HCenter(),
                    n.Title != null ? Text(n.Title, Typo.Body, ColorToken.TextSecondary).HCenter() : null,
                    n.Text != null ? Text(n.Text, Typo.Small, ColorToken.TextTertiary).HCenter() : null);
                col.Align = CrossAlign.Center;
                var box = Box(col).Pad(M.Xxl);
                box.HorizontalAlignment = HorizontalAlignment.Stretch;
                return box;
            }
            case "icon":
                return new PPIcon(Icon(n.Icon), M.Icon, n.Tone == null ? null : Theme.Tone(n.Tone).Fg);
            case "table":
                return Table(n, key);
            case "list":
                return List(n);
            case "tree":
            {
                var col = VStack(0);
                col.Align = CrossAlign.Stretch;
                foreach (var r in n.Rows) AddTreeRow(col, r, 0);
                col.HorizontalAlignment = HorizontalAlignment.Stretch;
                return col;
            }
            case "button":
                return Button(n);
            case "link":
                return new PPLink(n.Text ?? n.Href ?? "", () =>
                {
                    if (!string.IsNullOrEmpty(n.Href)) Actions.OpenURL(n.Href!);
                    else _model.Fire(n.Action, PluginViewModel.DataValues(n));
                }, icon: n.Icon == null ? null : "external-link");
            case "form":
                return Form(n, key);
            case "toolbar":
                return HStack(M.Sm, Children(n, path));
            case "console":
            {
                if (string.IsNullOrEmpty(n.Console)) return Gap(0);
                var console = Keep("console:" + key + ":" + n.Console, () => new ConsolePanel(n.Console!, null, null, compact: true));
                var box = Box(console).H(320).Rounded(M.RadiusMD).Outline(ColorToken.Border);
                box.HorizontalAlignment = HorizontalAlignment.Stretch;
                var clip = new RectangleGeometry { RadiusX = M.RadiusMD, RadiusY = M.RadiusMD };
                box.SizeChanged += (_, e) => clip.Rect = new Rect(e.NewSize);
                console.Clip = clip;
                return box;
            }
            default:
                return Gap(0);
        }
    }

    private UIElement Tabs(ViewNode n, string path, string key)
    {
        int idx = _model.TabIndex.TryGetValue(key, out var i) ? i : 0;
        var tabs = n.Children.Select((c, j) => new PPTab<int>(j, c.Title ?? (j + 1).ToString(), iconRef: Icon(c.Icon))).ToList();
        var bar = new PPTabBar<int>(tabs, Bind.Of(() => idx, v => { _model.TabIndex[key] = v; _model.Touch(); }));
        var col = VStack(M.Md, bar, new PPDivider(subtle: true));
        col.Align = CrossAlign.Stretch;
        if (idx >= 0 && idx < n.Children.Count)
        {
            var c = n.Children[idx];
            var page = VStack(M.Md, c.Children.Select((x, j) => Node(x, path + "." + idx + "." + j)));
            col.Children.Add(page);
        }
        col.HorizontalAlignment = HorizontalAlignment.Stretch;
        return col;
    }

    private UIElement Button(ViewNode n)
    {
        var kind = n.Style switch
        {
            "primary" => PPButtonKind.Primary,
            "danger" => PPButtonKind.Danger,
            "link" => PPButtonKind.Link,
            _ => PPButtonKind.Secondary,
        };
        var b = new PPButton(n.Text ?? n.Title ?? "", () => _model.Fire(n.Action, PluginViewModel.DataValues(n), n.Confirm, n.Text),
            kind: kind, size: PPButtonSize.Small, busy: n.Action != null && _model.BusyAction == n.Action, iconRef: Icon(n.Icon));
        if (n.Disabled) b.IsEnabled = false;
        if (!string.IsNullOrEmpty(n.Info)) b.Tip(n.Info);
        return b;
    }

    private static PPButtonKind ActionKind(string? style, bool ghostDefault) => style switch
    {
        "primary" => PPButtonKind.Primary,
        "danger" => PPButtonKind.Danger,
        "link" => PPButtonKind.Link,
        _ => ghostDefault ? PPButtonKind.Ghost : PPButtonKind.Secondary,
    };

    // MARK: table

    private static FrameworkElement Cell(FrameworkElement content, ViewTableColumn c)
    {
        content.HorizontalAlignment = c.Align == "right" ? HorizontalAlignment.Right : (c.Align == "center" ? HorizontalAlignment.Center : HorizontalAlignment.Left);
        var box = Box(content);
        if (c.Width > 0) box.W(c.Width); else box.Flex();
        return box;
    }

    private UIElement Table(ViewNode n, string key)
    {
        var sel = _model.Selections.TryGetValue(key, out var s) ? s : new HashSet<string>();
        bool hasActions = n.Rows.Any(r => r.Actions.Count > 0);
        var header = HStack(M.Md);
        if (n.Selectable)
            header.Children.Add(new PPCheckbox(Bind.Of(() => n.Rows.Count > 0 && sel.Count == n.Rows.Count, on =>
            {
                _model.Selections[key] = on ? new HashSet<string>(n.Rows.Select(r => r.Id)) : new HashSet<string>();
                _model.Touch();
            })));
        foreach (var c in n.Columns) header.Children.Add(Cell(Text(c.Title, Typo.Label, ColorToken.TextSecondary), c));
        if (hasActions) header.Children.Add(Gap(80).H(1));
        var headerBox = Box(header).Pad(M.Md, 0).H(M.RowHeight).Bg(ColorToken.BgSubtle);
        var col = VStack(0, headerBox, new PPDivider(subtle: true));
        col.Align = CrossAlign.Stretch;
        foreach (var r in n.Rows)
        {
            col.Children.Add(TableRow(r, n, key, sel, hasActions));
            col.Children.Add(new PPDivider(subtle: true));
        }
        var box = Box(col).Rounded(M.RadiusMD).Outline(ColorToken.Border);
        var clip = new RectangleGeometry { RadiusX = M.RadiusMD, RadiusY = M.RadiusMD };
        col.SizeChanged += (_, e) => clip.Rect = new Rect(e.NewSize);
        col.Clip = clip;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        return box;
    }

    private UIElement TableRow(ViewTableRow row, ViewNode n, string key, HashSet<string> sel, bool hasActions)
    {
        bool selected = sel.Contains(row.Id);
        var h = HStack(M.Md);
        if (n.Selectable)
            h.Children.Add(new PPCheckbox(Bind.Of(() => selected, on =>
            {
                var set = _model.Selections.TryGetValue(key, out var x) ? x : new HashSet<string>();
                if (on) set.Add(row.Id); else set.Remove(row.Id);
                _model.Selections[key] = set;
                _model.Touch();
            })));
        for (int i = 0; i < n.Columns.Count; i++)
        {
            var c = n.Columns[i];
            var text = row.Cells.TryGetValue(c.Key, out var t) ? t : "";
            var content = HStack(M.Xs,
                i == 0 && row.Icon != null ? new PPIcon(Icon(row.Icon), M.IconSmall) : null,
                TextMiddle(text, c.Mono ? Typo.Mono : Typo.Body, row.Tone == null ? ColorToken.TextPrimary : Theme.Tone(row.Tone).Fg));
            h.Children.Add(Cell(content, c));
        }
        if (hasActions)
        {
            var acts = HStack(M.Xs);
            foreach (var a in row.Actions)
            {
                var act = a;
                var b = new PPButton(act.Text ?? "", () =>
                {
                    var v = PluginViewModel.DataValues(act);
                    v["id"] = row.Id;
                    _model.Fire(act.Action, v, act.Confirm, act.Text);
                }, kind: ActionKind(act.Style, ghostDefault: true), size: PPButtonSize.Small);
                if (act.Disabled) b.IsEnabled = false;
                acts.Children.Add(b);
            }
            acts.HorizontalAlignment = HorizontalAlignment.Right;
            h.Children.Add(Box(acts).MinW(80));
        }
        var box = Box(h).Pad(M.Md, 0).MinH(M.RowHeight);
        h.VerticalAlignment = VerticalAlignment.Center;
        void Paint(bool hover) => box.Bg(selected ? ColorToken.BgSelected : (hover && row.Action != null ? ColorToken.BgHover : ColorToken.Clear));
        Paint(false);
        box.OnHover(Paint);
        if (row.Action != null)
        {
            box.OnClick(() =>
            {
                var v = new Dictionary<string, JToken> { ["id"] = row.Id };
                foreach (var kv in row.Cells) v[kv.Key] = kv.Value;
                _model.Fire(row.Action, v);
            });
        }
        return box;
    }

    // MARK: list / tree

    private UIElement List(ViewNode n)
    {
        var col = VStack(0);
        col.Align = CrossAlign.Stretch;
        foreach (var r in n.Rows)
        {
            var row = r;
            var text = VStack(2, Text(row.Cells.TryGetValue("title", out var t) ? t : "", Typo.Body, row.Tone == null ? ColorToken.TextPrimary : Theme.Tone(row.Tone).Fg),
                row.Cells.TryGetValue("subtitle", out var sub) && !string.IsNullOrEmpty(sub) ? Text(sub, Typo.Small, ColorToken.TextSecondary) : null).Shrink();
            var h = HStack(M.Md, row.Icon != null ? new PPIcon(Icon(row.Icon), M.Icon) : null, text, Spacer(M.Sm));
            foreach (var a in row.Actions)
            {
                var act = a;
                h.Children.Add(new PPButton(act.Text ?? "", () =>
                {
                    var v = PluginViewModel.DataValues(act);
                    v["id"] = row.Id;
                    _model.Fire(act.Action, v, act.Confirm, act.Text);
                }, kind: act.Style == "primary" ? PPButtonKind.Primary : PPButtonKind.Ghost, size: PPButtonSize.Small));
            }
            h.Margin = new Thickness(0, M.Sm, 0, M.Sm);
            Action? click = row.Action == null ? null : () =>
            {
                var v = new Dictionary<string, JToken> { ["id"] = row.Id };
                foreach (var kv in row.Cells) v[kv.Key] = kv.Value;
                _model.Fire(row.Action, v);
            };
            col.Children.Add(new PPRow(h, action: click, height: 0));
        }
        col.HorizontalAlignment = HorizontalAlignment.Stretch;
        return col;
    }

    private void AddTreeRow(Stack col, ViewTableRow row, int depth)
    {
        bool open = _model.Expanded.TryGetValue(row.Id, out var e) ? e : row.Expanded;
        var title = row.Cells.TryGetValue("title", out var t) ? t : (row.Cells.Values.FirstOrDefault() ?? row.Id);
        var h = HStack(M.Sm,
            Gap(depth * 16).H(1),
            row.Children.Count == 0 ? Gap(M.IconSmall) : new PPIcon(open ? "chevron-down" : "chevron-right", M.IconSmall, ColorToken.IconSecondary),
            row.Icon != null ? new PPIcon(Icon(row.Icon), M.Icon) : null,
            Text(title, Typo.Body, row.Tone == null ? ColorToken.TextPrimary : Theme.Tone(row.Tone).Fg),
            row.Cells.TryGetValue("subtitle", out var sub) ? Text(sub, Typo.Small, ColorToken.TextTertiary) : null,
            Spacer());
        col.Children.Add(new PPRow(h, action: () =>
        {
            if (row.Children.Count > 0) { _model.Expanded[row.Id] = !open; _model.Touch(); }
            if (row.Action != null)
            {
                var v = new Dictionary<string, JToken> { ["id"] = row.Id };
                foreach (var kv in row.Cells) v[kv.Key] = kv.Value;
                _model.Fire(row.Action, v);
            }
        }));
        if (open) foreach (var c in row.Children) AddTreeRow(col, c, depth + 1);
    }

    // MARK: form

    private UIElement Form(ViewNode n, string key)
    {
        if (!_model.FormValues.TryGetValue(key, out var values))
        {
            values = FormView.InitialValues(n.Fields, n.Values);
            _model.FormValues[key] = values;
        }
        // 字段不变就复用同一个表单（输入中、定时刷新时不被打断）
        var sig = key + "|" + string.Join(",", n.Fields.Select(f => f.Key + ":" + f.Type + ":" + string.Join("/", f.Options.Select(o => o.Value))));
        var form = Keep("form:" + sig, () => new FormView(n.Fields, values, labelWidth: 120));
        var submit = new PPButton(n.SubmitText ?? L.T("common.submit"), () =>
        {
            if (FormView.MissingRequired(n.Fields, values).Count > 0) { form.ShowErrors = true; return; }
            _model.Fire(n.Action, FormView.Normalized(n.Fields, values), n.Confirm);
        }, kind: PPButtonKind.Primary, size: PPButtonSize.Small, busy: n.Action != null && _model.BusyAction == n.Action);
        if (n.Disabled) submit.IsEnabled = false;
        Detach(form);
        var col = VStack(M.Md,
            n.Title != null ? Text(n.Title, Typo.Title) : null,
            form,
            HStack(0, Gap(120 + M.Md).H(1), submit));
        col.Align = CrossAlign.Stretch;
        col.HorizontalAlignment = HorizontalAlignment.Stretch;
        return col;
    }
}

/// <summary>Markdown（只支持粗体、斜体、链接、行内代码、- / * 列表）。对应 Mac MarkdownText。</summary>
internal static class MarkdownText
{
    private static readonly Regex Inline = new(@"\*\*(.+?)\*\*|__(.+?)__|\[([^\]]+)\]\(([^)\s]+)\)|`([^`]+)`|\*([^*\s][^*]*?)\*|_([^_\s][^_]*?)_", RegexOptions.Compiled);

    public static UIElement Make(string text)
    {
        var col = VStack(M.Xs);
        col.Align = CrossAlign.Stretch;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                var row = HStack(M.Sm, Text("•", Typo.Body, ColorToken.TextSecondary).Top(), InlineText(trimmed.Substring(2)));
                row.Align = CrossAlign.Start;
                col.Children.Add(row);
            }
            else if (trimmed.Length > 0) col.Children.Add(InlineText(line));
            else col.Children.Add(Gap(M.Xs));
        }
        col.HorizontalAlignment = HorizontalAlignment.Stretch;
        return col;
    }

    private static TextBlock InlineText(string s)
    {
        var tb = Paragraph("", Typo.Body);
        int pos = 0;
        foreach (Match m in Inline.Matches(s))
        {
            if (m.Index > pos) tb.Inlines.Add(new DocRun(s.Substring(pos, m.Index - pos)));
            if (m.Groups[1].Success || m.Groups[2].Success)
                tb.Inlines.Add(new Bold(new DocRun(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)));
            else if (m.Groups[3].Success)
            {
                var url = m.Groups[4].Value;
                var link = new Hyperlink(new DocRun(m.Groups[3].Value)) { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
                link.SetResourceReference(TextElement.ForegroundProperty, ColorToken.TextLink);
                link.Click += (_, _) => Actions.OpenURL(url);
                link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
                link.MouseLeave += (_, _) => link.TextDecorations = null;
                tb.Inlines.Add(link);
            }
            else if (m.Groups[5].Success)
                tb.Inlines.Add(new DocRun(m.Groups[5].Value) { FontFamily = UIFonts.Mono, FontSize = Typo.Mono.Size });
            else
                tb.Inlines.Add(new Italic(new DocRun(m.Groups[6].Success ? m.Groups[6].Value : m.Groups[7].Value)));
            pos = m.Index + m.Length;
        }
        if (pos < s.Length) tb.Inlines.Add(new DocRun(s.Substring(pos)));
        return tb;
    }
}

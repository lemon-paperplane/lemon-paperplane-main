using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PaperplaneKit;
using static LemonPaperplane.UI;
using RunModel = PaperplaneKit.Run;

namespace LemonPaperplane;

/// <summary>工具栏左侧：侧栏开关、服务器切换器（docs/remote.md 5.1）、重新连接中（对应 Mac MainToolbar 左半部分）。</summary>
public sealed class MainToolbarLeft : PPView
{
    private readonly MainWindow _window;
    private readonly AppModel _model = AppModel.Shared;

    public MainToolbarLeft(MainWindow window)
    {
        _window = window;
        Observe(_model, nameof(AppModel.Connection));
        Observe(h => window.SidebarVisibleChanged += h, h => window.SidebarVisibleChanged -= h);
    }

    protected override UIElement Build()
    {
        var toggle = new PPIconButton(_window.SidebarVisible ? "panel-left-close" : "panel-left-open", L.T("toolbar.toggleSidebar"),
            () => _window.SidebarVisible = !_window.SidebarVisible).Id("toolbar.sidebar");
        var row = HStack(M.Xs, toggle, Keep("server", () => new ServerSwitcher()));
        if (_model.Connection.Kind == ConnectionState.Reconnecting.Kind)
            row.Children.Add(HStack(M.Xs, new PPSpinner(M.IconSmall, ColorToken.Warning),
                Text(L.T("connection.reconnecting"), Typo.Small, ColorToken.Warning)).Margins(M.Md, 0, 0, 0));
        return row;
    }
}

/// <summary>工具栏右侧：搜索、导入、运行中、后台任务（对应 Mac MainToolbar 右半部分）。</summary>
public sealed class MainToolbarRight : PPView
{
    private readonly NavStore _nav = AppModel.Shared.Nav;
    private readonly PPTextField _search;

    public MainToolbarRight()
    {
        Observe(_nav, nameof(NavStore.Page), nameof(NavStore.OpenProjectId));
        // 搜索框跨重建复用同一个实例（输入中不被换掉）
        _search = new PPTextField(L.T("toolbar.search"), Bind.Of(() => _nav.Search, v => _nav.Search = v), icon: "search", clearable: true,
            height: M.ControlHeightSmall + 2, onEscape: () => { _nav.Search = ""; _search?.Sync(); Keyboard.ClearFocus(); });
        _search.W(220).Margins(0, 0, M.Xs, 0).Id("toolbar.search");
    }

    protected override void OnAppear() => _nav.FocusSearch += OnFocusSearch;

    protected override void OnDisappear() => _nav.FocusSearch -= OnFocusSearch;

    private void OnFocusSearch(object? sender, EventArgs e)
    {
        if (_search.IsVisible) _search.Focus();
    }

    private bool ShowsSearch => _nav.OpenProjectId == null && _nav.Page.Kind is NavPage.KindWorkspace or NavPage.KindRunning;

    protected override UIElement Build()
    {
        UI.Detach(_search);
        return HStack(M.Xs,
            ShowsSearch ? _search : null,
            ImportMenu(),
            Keep("runs", () => new RunsIndicator()),
            Keep("tasks", () => new TasksIndicator()));
    }

    private static UIElement ImportMenu()
    {
        var label = Box(HStack(M.Xs, new PPIcon("plus"), Text(L.T("toolbar.import"), Typo.Label).Shrink(false), new PPIcon("chevron-down", 12)))
            .Pad(M.Md, 0).H(M.ControlHeightSmall + 2).Rounded(M.RadiusMD).Bg(ColorToken.Accent).Fg(ColorToken.TextOnAccent);
        return new PPMenuButton(() =>
        {
            var e = new System.Collections.Generic.List<MenuEntry>
            {
                MenuEntry.Item(L.T("import.folder"), ImportFlow.ImportFolder, icon: "folder-input", shortcut: "Ctrl+N"),
                MenuEntry.Item(L.T("import.scan"), ImportFlow.ScanFolder, icon: "scan-search", shortcut: "Ctrl+Shift+N"),
            };
            if (VCSHooks.CloneAction is { } clone)
            {
                e.Add(MenuEntry.Divider);
                e.Add(MenuEntry.Item(L.T("import.clone"), clone, icon: "git-branch"));
            }
            return e;
        }, label, PopupAlignment.Trailing, L.T("toolbar.import")).Id("toolbar.import");
    }
}

/// <summary>运行中数量：点开列出全部运行，可停止、看日志、打开地址（对应 Mac RunsIndicator）。</summary>
public sealed class RunsIndicator : PPView
{
    private readonly RunStore _runs = AppModel.Shared.Runs;
    private readonly State<bool> _open;

    public RunsIndicator()
    {
        Observe(_runs, nameof(RunStore.Runs));
        // 不订阅：弹出框的锚点跨重建复用（锚点被换掉会把刚打开的弹出框关掉）
        _open = new State<bool>(false);
    }

    protected override UIElement Build()
    {
        var count = _runs.ActiveRuns.Count;
        var content = HStack(M.Xs, new PPIcon("activity", M.Icon, count == 0 ? ColorToken.IconSecondary : ColorToken.StatusRunning),
            Text(count.ToString(), Typo.Label).TabularDigits());
        return Keep("label", () => PopoverAnchor.Make(_open, L.T("toolbar.runs"), "toolbar.runs",
                () => new PopoverContent(new RunsPopover(() => _open.Value = false), 380)),
            b => { b.Child = content; b.ToolTip = L.T("toolbar.runs"); });
    }
}

/// <summary>工具栏里点开弹出框的小按钮：锚点跨重建复用，按下态跟着弹出框开关。</summary>
internal static class PopoverAnchor
{
    public static System.Windows.Controls.Border Make(State<bool> open, string tip, string id, Func<UIElement> content)
    {
        var label = Box().Pad(M.Sm, 0).H(M.IconButton).Rounded(M.RadiusMD);
        void Paint() => label.Bg(open.Value ? ColorToken.BgPressed : ColorToken.Clear);
        open.PropertyChanged += (_, _) => Paint();
        Paint();
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Tip(tip).Id(id);
        label.OnClick(() =>
        {
            if (!open.Value && PopupController.WasJustDismissed(label)) return;
            open.Value = !open.Value;
        });
        return label.PPPopover(open, content, PopupAlignment.Trailing);
    }
}

/// <summary>全部运行列表（运行中弹出框、托盘共用行样式）。</summary>
public sealed class RunsPopover : PPView
{
    private readonly Action _close;
    private readonly RunStore _runs = AppModel.Shared.Runs;
    private readonly DataStore _data = AppModel.Shared.Data;

    public RunsPopover(Action close)
    {
        _close = close;
        Observe(_runs, nameof(RunStore.Runs));
        Observe(_data, nameof(DataStore.Projects));
    }

    protected override UIElement Build()
    {
        var active = _runs.ActiveRuns;
        var head = HStack(0, Text(L.T("toolbar.runs"), Typo.Title), Spacer(),
                active.Count > 0 ? new PPButton(L.T("menu.stopAll"), Actions.StopAll, icon: "square", kind: PPButtonKind.Ghost, size: PPButtonSize.Small) : null)
            .Pad(M.Md, 0).H(36);
        UIElement body;
        if (active.Count == 0)
            body = Text(L.T("runs.none"), Typo.Body, ColorToken.TextSecondary).Pad(M.Lg);
        else
        {
            var list = VStack(0, active.Select(r => new RunRow(r, _data.Project(r.ProjectId)?.Name, _close)));
            list.Align = CrossAlign.Stretch;
            body = Scroll(list.Pad(M.Xs)).Do(s => s.MaxHeight = 360);
        }
        var col = VStack(0, head, new PPDivider(subtle: true), body);
        col.Align = CrossAlign.Stretch;
        return col;
    }
}

/// <summary>一个运行：状态、名称、工程、地址、资源、看日志 / 停止（对应 Mac RunRow）。</summary>
public sealed class RunRow : PPView
{
    private readonly RunModel _run;
    private readonly string? _projectName;
    private readonly Action? _close;

    public RunRow(RunModel run, string? projectName, Action? close)
    {
        _run = run;
        _projectName = projectName;
        _close = close;
    }

    protected override UIElement Build()
    {
        var r = _run;
        var title = HStack(M.Xs, Text(r.Name, Typo.BodyStrong),
            _projectName != null ? Text(_projectName, Typo.Body, ColorToken.TextSecondary) : null);
        UIElement address = r.Urls.FirstOrDefault() is { } url
            ? RunAddress.Links(url, r.ProjectId, r.ConfigId)
            : Text(RunStatusStyle.Title(r.State), Typo.Small, ColorToken.TextTertiary);
        var sub = HStack(M.Md, address, RunState.IsActive(r.State) ? new RunResourceInline(r.Id, r.StartedAt, 48) : null);
        var content = HStack(M.Md,
            new StatusDot(r.State),
            VStack(2, title, sub).Flex(),
            new PPIconButton("scroll-text", L.T("runs.openLog"), () =>
            {
                _close?.Invoke();
                MainWindow.Current?.ShowMainWindow();
                AppModel.Shared.Nav.OpenRunConsole(r.ProjectId, r.ConfigId);
            }, size: M.IconButtonSmall),
            new PPIconButton("square", L.T("action.stop"), () => Actions.Stop(r.Id), size: M.IconButtonSmall, tint: ColorToken.Danger))
            .Margins(0, M.Sm, 0, M.Sm);
        return new PPRow(content, height: 0);
    }
}

/// <summary>后台任务：转圈 + 数量，点开看进度、取消、看日志（对应 Mac TasksIndicator）。</summary>
public sealed class TasksIndicator : PPView
{
    private readonly RunStore _runs = AppModel.Shared.Runs;
    private readonly State<bool> _open;

    public TasksIndicator()
    {
        Observe(_runs, nameof(RunStore.Tasks));
        // 不订阅：弹出框的锚点跨重建复用（任务进度变化时弹出框不被关掉）
        _open = new State<bool>(false);
    }

    protected override UIElement Build()
    {
        if (_runs.RecentTasks.Count == 0) return Gap(0);
        var active = _runs.ActiveTasks.Count;
        UIElement inner = active == 0
            ? new PPIcon("circle-check", M.Icon, ColorToken.IconSecondary)
            : HStack(M.Xs, new PPSpinner(M.Icon, ColorToken.Accent), Text(active.ToString(), Typo.Label).TabularDigits());
        return Keep("label", () => PopoverAnchor.Make(_open, L.T("toolbar.tasks"), "toolbar.tasks",
                () => new PopoverContent(new TasksPopover(() => _open.Value = false), 380)),
            b => { b.Child = inner; b.ToolTip = L.T("toolbar.tasks"); });
    }
}

public sealed class TasksPopover : PPView
{
    private readonly Action _close;
    private readonly RunStore _runs = AppModel.Shared.Runs;

    public TasksPopover(Action close)
    {
        _close = close;
        Observe(_runs, nameof(RunStore.Tasks));
    }

    protected override UIElement Build()
    {
        var list = VStack(M.Xs, _runs.RecentTasks.Select(t => new TaskRow(t, _close)));
        list.Align = CrossAlign.Stretch;
        var col = VStack(0,
            Text(L.T("toolbar.tasks"), Typo.Title).Pad(M.Md, 0).H(36).Do(t => t.VerticalAlignment = VerticalAlignment.Center),
            new PPDivider(subtle: true),
            Scroll(list.Pad(M.Xs)).Do(s => s.MaxHeight = 400));
        col.Align = CrossAlign.Stretch;
        return col;
    }
}

/// <summary>一个任务：状态图标、标题、进度、消息、看日志 / 取消（对应 Mac TaskRow）。</summary>
public sealed class TaskRow : PPView
{
    private readonly TaskInfo _task;
    private readonly Action? _close;

    public TaskRow(TaskInfo task, Action? close)
    {
        _task = task;
        _close = close;
    }

    protected override UIElement Build()
    {
        var t = _task;
        UIElement icon = t.State switch
        {
            TaskState.Running => new PPSpinner(M.IconSmall, ColorToken.Accent),
            TaskState.Succeeded => new PPIcon("circle-check", M.IconSmall, ColorToken.Success),
            TaskState.Failed => new PPIcon("circle-x", M.IconSmall, ColorToken.Danger),
            _ => new PPIcon("circle-minus", M.IconSmall, ColorToken.IconSecondary),
        };
        var head = HStack(M.Sm, icon, Text(t.Title, Typo.BodyStrong).Flex(),
            new PPIconButton("scroll-text", L.T("task.viewLog"), () => { _close?.Invoke(); Actions.ShowTaskLog(t.Id); }, size: M.IconButtonSmall),
            t.IsRunning ? new PPIconButton("x", L.T("common.cancel"), () => Actions.CancelTask(t.Id), size: M.IconButtonSmall) : null);
        var col = VStack(M.Xs, head);
        col.Align = CrossAlign.Stretch;
        if (t.IsRunning) col.Children.Add(new PPProgressBar(t.Percent));
        var msg = t.IsRunning ? t.Message : t.Error ?? t.Message;
        if (!string.IsNullOrEmpty(msg))
            col.Children.Add(Paragraph(msg, Typo.Small, t.State == TaskState.Failed ? ColorToken.Danger : ColorToken.TextSecondary)
                .Do(p => { p.TextTrimming = TextTrimming.CharacterEllipsis; p.MaxHeight = 2 * 16; }));
        return new PPRow(col.Margins(0, M.Sm, 0, M.Sm), height: 0);
    }
}

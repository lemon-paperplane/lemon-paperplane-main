using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 工作区「终端」：像 VS Code 一样开若干个终端，右侧是终端列表，左侧显示当前终端。对应 Mac TerminalPage.swift。
// 终端在内核里（ConPTY 里的 PowerShell，terminal.* 接口），切走或关掉界面不影响，回来按偏移补齐输出。
// 终端区域是编辑器工程的 terminal.html（xterm.js），一个网页装下这个工作区的全部终端；离开这个分段即释放网页（WebView2 Dispose）。

/// <summary>终端操作（对应 Mac extension Actions 里的终端部分）。</summary>
public static class TerminalActions
{
    private static AppModel Model => AppModel.Shared;

    /// <summary>在工程根目录新建终端，并切换过去。</summary>
    public static void NewTerminal(string workspaceId, ProjectSummary project, (int Cols, int Rows) size) =>
        Model.Perform(L.T("terminal.createFailed"), async api =>
        {
            var t = await api.TerminalCreateAsync(project.Id, size.Cols, size.Rows);
            Model.Terminals.Upsert(t);
            Model.Terminals.Select(workspaceId, t.Id);
        });

    public static void Rename(TerminalInfo t)
    {
        var field = new FormField { Key = "name", Title = L.T("terminal.name"), Type = "string", Placeholder = t.Name, Required = true };
        var id = t.Id;
        Dialogs.Form(L.T("terminal.rename"), new List<FormField> { field }, async v =>
        {
            var name = v.TryGetValue("name", out var n) ? n.StringValue() ?? "" : "";
            await Model.Client().TerminalRenameAsync(id, name);
        }, values: new Dictionary<string, JToken> { ["name"] = t.Name }, ok: L.T("common.save"));
    }

    public static void Restart(TerminalInfo t) =>
        Model.Perform(L.T("terminal.restartFailed"), async api => await api.TerminalRestartAsync(t.Id));

    /// <summary>关闭终端；里面还有命令在跑（开发服务器等）时先确认。</summary>
    public static void Close(TerminalInfo t) =>
        Model.Perform(async api =>
        {
            TerminalBusy? b = null;
            try { b = await api.TerminalBusyAsync(t.Id); }
            catch { /* 查不到按不忙处理 */ }
            if (b is { Busy: true })
            {
                var name = string.IsNullOrEmpty(b.Process) ? L.T("terminal.aProcess") : b.Process;
                Dialogs.Confirm(L.T("terminal.close.title", t.Name), L.T("terminal.close.busy", name), () =>
                    Model.Perform(async a => await a.TerminalCloseAsync(t.Id)), ok: L.T("terminal.close"), danger: true);
                return;
            }
            await api.TerminalCloseAsync(t.Id);
        });

    /// <summary>「新建终端」菜单：选工作区里的一个工程，终端从它的根目录开始。</summary>
    public static IEnumerable<MenuEntry> ProjectMenu(string workspaceId, Func<Task<(int, int)>> size)
    {
        var projects = Model.Data.ProjectsIn(workspaceId);
        if (projects.Count == 0)
            return new[] { MenuEntry.Item(L.T("terminal.noProjects"), () => { }, disabled: true) };
        var output = new List<MenuEntry> { MenuEntry.Header(L.T("terminal.pickProject")) };
        foreach (var p in projects)
        {
            var f = p.Facets.FirstOrDefault();
            var icon = IconKey.Parse(f?.Icon, f?.Plugin) ?? IconKey.Builtin("folder");
            output.Add(MenuEntry.Item(new PPMenuItem(p.Name, async () =>
            {
                var s = await size();
                NewTerminal(workspaceId, p, s);
            }, iconRef: icon)));
        }
        return output;
    }
}

/// <summary>终端页（工作区「终端」分段；对应 Mac TerminalPage）。</summary>
public sealed class TerminalPage : PPView
{
    private readonly string _ws;
    private readonly TerminalStore _store = AppModel.Shared.Terminals;
    private readonly PPWebView _web;
    private readonly Stack _root;
    private readonly FrameworkElement _empty;
    private readonly PPDivider _listDivider = new(vertical: true, subtle: true);
    private readonly FrameworkElement _list;
    private List<string> _syncedIds = new();
    private string? _shown;

    public TerminalPage(string workspaceId)
    {
        _ws = workspaceId;
        Observe(_store);
        _web = new PPWebView("terminal", Url, ColorToken.ConsoleBg)
        {
            KeysToPage = true, // 和 Mac 一样 Ctrl 组合键都给 shell（Ctrl+R、Ctrl+C…）
            ErrorIcon = "square-terminal",
        };
        _web.Ready += OnWebReady;
        _web.Message += OnMessage;
        _web.ThemeChanged += dark => _web.Exec("window.paperplaneTerm.setTheme(" + (dark ? "'dark'" : "'light'") + ")");

        // 网页一直在（没有终端时藏起来、换成空状态），新建第一个终端时也能量出行列数
        _empty = EmptyState();
        var area = ZStack(_web, _empty).Flex();
        _list = new TerminalList(workspaceId, Select).W(220);
        var body = HStack(0, area, _listDivider, _list);
        body.Align = CrossAlign.Stretch;
        _root = VStack(0, new TerminalToolbar(workspaceId, Size), new PPDivider(subtle: true), body.Flex());
        _root.Align = CrossAlign.Stretch;
        _root.Bg(ColorToken.ConsoleBg);
        _root.Id("terminal.page");
    }

    /// <summary>调试桥用。</summary>
    internal PPWebView Web => _web;

    protected override UIElement Build()
    {
        var list = _store.In(_ws);
        var current = _store.Current(_ws);
        bool empty = list.Count == 0;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _web.Covered = empty;
        _listDivider.Visibility = _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        SyncWeb(list.Select(t => t.Id).ToList(), current?.Id);
        return _root;
    }

    /// <summary>没有终端时的空状态（单独的小视图：切换语言时跟着重建）。</summary>
    private FrameworkElement EmptyState() => new ViewOf(() =>
    {
        var col = VStack(M.Lg,
            new PPIcon("square-terminal", M.IconEmpty, ColorToken.IconSecondary).HCenter(),
            Text(L.T("terminal.empty"), Typo.Body, ColorToken.TextSecondary).HCenter(),
            new PPButtonMenu(L.T("terminal.new"), () => TerminalActions.ProjectMenu(_ws, Size), icon: "plus", kind: PPButtonKind.Primary)
                .HCenter().Id("terminal.newEmpty"));
        col.Align = CrossAlign.Center;
        col.HorizontalAlignment = HorizontalAlignment.Center;
        col.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Child = col }.Bg(ColorToken.BgContent);
    });

    // MARK: 网页

    private string? Url()
    {
        var api = AppModel.Shared.Api;
        if (api == null) return null;
        var b = api.BaseUrl.AbsoluteUri.TrimEnd('/');
        return b + "/editor/terminal.html#token=" + Uri.EscapeDataString(api.Token) + "&theme=" + (Theme.IsDark ? "dark" : "light");
    }

    private void OnWebReady()
    {
        var list = _store.In(_ws).Select(t => t.Id).ToList();
        _syncedIds = list;
        _web.Exec("window.paperplaneTerm.sync(" + WebView2Env.Js(list) + ")");
        _shown = null;
        if (_store.Current(_ws) is { } t) Show(t.Id);
    }

    /// <summary>终端列表或当前终端变了：关掉网页里不在列表里的，显示当前的（对应 Mac 的两个 onChange）。</summary>
    private void SyncWeb(List<string> ids, string? current)
    {
        if (!_web.IsReady) return;
        if (!ids.SequenceEqual(_syncedIds))
        {
            _syncedIds = ids;
            _web.Exec("window.paperplaneTerm.sync(" + WebView2Env.Js(ids) + ")");
            if (current != null) Show(current);
            return;
        }
        if (current != null && current != _shown) Show(current);
    }

    private void Show(string id)
    {
        _shown = id;
        _web.FocusPage();
        _web.Exec("window.paperplaneTerm.show(" + WebView2Env.Js(id) + ")");
    }

    private void Select(TerminalInfo t)
    {
        _store.Select(_ws, t.Id);
        Show(t.Id);
    }

    /// <summary>当前区域能放下的行列数（新建终端按它启动 shell）。</summary>
    private async Task<(int, int)> Size()
    {
        if (!_web.IsReady) return (100, 30);
        var r = await _web.EvalAsync("window.paperplaneTerm.size()");
        try
        {
            if (r != null && JToken.Parse(r) is JObject o)
                return (Math.Max(20, (int?)o["cols"] ?? 100), Math.Max(5, (int?)o["rows"] ?? 30));
        }
        catch { /* 网页出错时按默认 */ }
        return (100, 30);
    }

    private void OnMessage(JObject m)
    {
        switch ((string?)m["type"])
        {
            case "closed":
                if ((string?)m["id"] is { } id) _store.List = _store.List.Where(t => t.Id != id).ToList();
                break;
            case "openExternal":
                if ((string?)m["url"] is { } s && Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                    Actions.OpenURL(u.AbsoluteUri);
                break;
        }
    }
}

/// <summary>终端页工具栏：当前终端名、目录、退出状态与重启；i；重命名、关闭；新建终端。</summary>
internal sealed class TerminalToolbar : PPView
{
    private readonly string _ws;
    private readonly Func<Task<(int, int)>> _size;
    private readonly TerminalStore _store = AppModel.Shared.Terminals;

    public TerminalToolbar(string workspaceId, Func<Task<(int, int)>> size)
    {
        _ws = workspaceId;
        _size = size;
        Observe(_store);
    }

    protected override UIElement Build()
    {
        var t = _store.Current(_ws);
        var row = HStack(M.Sm);
        if (t != null)
        {
            row.Children.Add(new PPIcon("square-terminal", M.IconSmall, ColorToken.IconSecondary));
            row.Children.Add(Text(t.Name, Typo.BodyStrong));
            row.Children.Add(TextMiddle(Format.AbbreviatePath(t.Cwd), Typo.Small, ColorToken.TextTertiary));
            if (t.Exited)
            {
                row.Children.Add(new PPBadge(L.T("terminal.exited", t.ExitCode), tone: "danger"));
                row.Children.Add(new PPButton(L.T("terminal.restart"), () => TerminalActions.Restart(t), icon: "rotate-ccw",
                    kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("terminal.restart"));
            }
        }
        else
        {
            row.Children.Add(Text(L.T("terminal.none"), Typo.Small, ColorToken.TextSecondary));
        }
        row.Children.Add(new PPInfoButton(L.T("terminal.info")));
        row.Children.Add(Spacer(M.Md));
        if (t != null)
        {
            row.Children.Add(new PPIconButton("pencil", L.T("terminal.rename"), () => TerminalActions.Rename(t), M.IconButtonSmall, M.IconSmall).Id("terminal.rename"));
            row.Children.Add(new PPIconButton("trash-2", L.T("terminal.close"), () => TerminalActions.Close(t), M.IconButtonSmall, M.IconSmall).Id("terminal.close"));
        }
        row.Children.Add(new PPButtonMenu(L.T("terminal.new"), () => TerminalActions.ProjectMenu(_ws, _size), icon: "plus",
            kind: PPButtonKind.Primary, size: PPButtonSize.Small, alignment: PopupAlignment.Trailing).Id("terminal.new"));
        row.Margin = new Thickness(M.Xl, 0, M.Xl, 0);
        return new Border { Child = row, Height = M.ToolbarHeight }.Bg(ColorToken.BgContent);
    }
}

/// <summary>右侧终端列表（宽 220）。</summary>
internal sealed class TerminalList : PPView
{
    private readonly string _ws;
    private readonly Action<TerminalInfo> _select;
    private readonly TerminalStore _store = AppModel.Shared.Terminals;
    private readonly DataStore _data = AppModel.Shared.Data;

    public TerminalList(string workspaceId, Action<TerminalInfo> select)
    {
        _ws = workspaceId;
        _select = select;
        Observe(_store);
        Observe(_data, nameof(DataStore.Projects));
    }

    protected override UIElement Build()
    {
        var current = _store.Current(_ws)?.Id;
        var col = VStack(1, _store.In(_ws).Select(t => new TerminalRow(t, _data.Project(t.ProjectId), t.Id == current, () => _select(t))));
        col.Align = CrossAlign.Stretch;
        col.Padding = new Thickness(M.Sm);
        return Scroll(col).Bg(ColorToken.BgContent).Id("terminal.list");
    }
}

/// <summary>终端列表的一行：图标、名字、副标题（所在工程 / 目录 / 退出码）、悬停时的关闭按钮。高 36。</summary>
internal sealed class TerminalRow : Border
{
    private readonly TerminalInfo _t;
    private readonly bool _selected;
    private readonly PPIconButton _close;

    public TerminalRow(TerminalInfo t, ProjectSummary? project, bool selected, Action select)
    {
        _t = t;
        _selected = selected;
        var subtitle = t.Exited ? L.T("terminal.exited", t.ExitCode)
            : project != null && project.Name != t.Name ? project.Name
            : Format.AbbreviatePath(t.Cwd);
        _close = new PPIconButton("x", L.T("terminal.close"), () => TerminalActions.Close(t), M.IconButtonSmall, M.IconSmall);
        _close.Visibility = Visibility.Collapsed;
        var texts = VStack(1,
            TextMiddle(t.Name, Typo.Body, ColorToken.TextPrimary),
            new HeadTrimText(subtitle, Typo.Caption, t.Exited ? ColorToken.Danger : ColorToken.TextTertiary));
        texts.Shrink();
        Child = HStack(M.Sm,
            new PPIcon("square-terminal", M.Icon, t.Exited ? ColorToken.Danger : selected ? ColorToken.IconPrimary : ColorToken.IconSecondary),
            texts,
            Spacer(0),
            _close);
        Height = 36;
        Padding = new Thickness(M.Sm, 0, M.Sm, 0);
        CornerRadius = new CornerRadius(M.RadiusSM);
        SnapsToDevicePixels = true;
        ToolTip = t.Cwd;
        MouseEnter += (_, _) => UpdateVisual();
        MouseLeave += (_, _) => UpdateVisual();
        this.OnClick(select);
        this.PPContextMenu(Menu);
        this.A11y(t.Name);
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        bool hover = IsMouseOver;
        this.Bg(_selected ? ColorToken.BgSelected : hover ? ColorToken.BgHover : ColorToken.Clear);
        _close.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<MenuEntry> Menu()
    {
        var t = _t;
        var output = new List<MenuEntry> { MenuEntry.Item(L.T("terminal.rename"), () => TerminalActions.Rename(t), icon: "pencil") };
        if (t.Exited) output.Add(MenuEntry.Item(L.T("terminal.restart"), () => TerminalActions.Restart(t), icon: "rotate-ccw"));
        output.Add(MenuEntry.Item(L.T("terminal.reveal"), () => Actions.RevealInExplorer(t.Cwd), icon: "folder-open"));
        output.Add(MenuEntry.Divider);
        output.Add(MenuEntry.Item(L.T("terminal.close"), () => TerminalActions.Close(t), icon: "trash-2", destructive: true));
        return output;
    }
}

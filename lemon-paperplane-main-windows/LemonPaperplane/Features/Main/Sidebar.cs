using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>左侧栏：工作区（拖动排序、右键重命名 / 全部停止 / 删除）、运行中、工具链、插件、设置、插件 sidebar 视图（对应 Mac Sidebar）。</summary>
public sealed class Sidebar : PPView
{
    private readonly DataStore _data = AppModel.Shared.Data;
    private readonly RunStore _runs = AppModel.Shared.Runs;
    private readonly NavStore _nav = AppModel.Shared.Nav;

    // 拖动排序：按下的工作区、按下位置、拖动中实时移动的顺序
    private string? _pressId;
    private Point _pressAt;
    private bool _dragging;
    private readonly List<(string Id, FrameworkElement Row)> _rows = new();

    public Sidebar()
    {
        Observe(_data, nameof(DataStore.Workspaces), nameof(DataStore.Projects), nameof(DataStore.Contributions));
        Observe(_runs, nameof(RunStore.Runs));
        Observe(_nav, nameof(NavStore.Page), nameof(NavStore.OpenProjectId));
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragEnd;
        // LostMouseCapture 会从子元素冒泡上来（开始拖动时行把捕获让给侧栏），只认侧栏自己失去捕获
        LostMouseCapture += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) EndDrag(commit: true); };
    }

    protected override UIElement Build()
    {
        _rows.Clear();
        var col = VStack(1);
        col.Align = CrossAlign.Stretch;

        col.Children.Add(HStack(0,
            Text(L.T("sidebar.workspaces"), Typo.Caption, ColorToken.TextTertiary), Spacer(),
            new PPIconButton("plus", L.T("workspace.new"), Actions.CreateWorkspace, size: M.IconButtonSmall, iconSize: M.IconSmall).Id("sidebar.newWorkspace"))
            .Margins(M.Md, 0, 0, M.Xxs));

        foreach (var ws in _data.Workspaces) col.Children.Add(WorkspaceRow(ws));

        var active = _runs.ActiveRuns.Count;
        bool onPage(NavPage p) => _nav.Page == p && _nav.OpenProjectId == null;
        col.Children.Add(SidebarItem.Make("activity", L.T("sidebar.running"), onPage(NavPage.Running), () => _nav.Page = NavPage.Running,
            count: active, countTone: active == 0 ? null : "success").Margins(0, M.Md, 0, 0).Id("sidebar.running"));

        col.Children.Add(new PPDivider(subtle: true).Margins(M.Md, M.Md, M.Md, M.Md));
        col.Children.Add(SidebarItem.Make("boxes", L.T("sidebar.toolchains"), _nav.Page == NavPage.ToolchainsPage, () => _nav.Page = NavPage.ToolchainsPage).Id("sidebar.toolchains"));
        col.Children.Add(SidebarItem.Make("puzzle", L.T("sidebar.plugins"), _nav.Page == NavPage.PluginsPage, () => _nav.Page = NavPage.PluginsPage).Id("sidebar.plugins"));
        col.Children.Add(SidebarItem.Make("settings", L.T("sidebar.settings"), _nav.Page == NavPage.SettingsPage, () => _nav.Page = NavPage.SettingsPage).Id("sidebar.settings"));

        var views = _data.Contributions.ViewsAt(ViewLocation.Sidebar);
        if (views.Count > 0)
        {
            col.Children.Add(new PPDivider(subtle: true).Margins(M.Md, M.Md, M.Md, M.Md));
            foreach (var v in views)
            {
                var page = NavPage.PluginView(v.Key);
                col.Children.Add(SidebarItem.Make(IconKey.Parse(v.Icon, v.Plugin) ?? IconKey.Builtin("puzzle"), v.Title, _nav.Page == page, () => _nav.Page = page));
            }
        }

        col.Margin = new Thickness(M.Sm);
        return Scroll(col).Key("sidebar.scroll").Bg(ColorToken.BgChrome);
    }

    private FrameworkElement WorkspaceRow(Workspace ws)
    {
        var projects = _data.ProjectsIn(ws.Id);
        var running = projects.Count(p => p.HasActiveRun);
        var page = NavPage.WorkspacePage(ws.Id);
        var row = SidebarItem.Make("layers", ws.Name, _nav.Page == page && _nav.OpenProjectId == null, () => { if (!_dragging) _nav.Page = page; },
            count: projects.Count, runningDot: running > 0).Id("sidebar.workspace." + ws.Id);
        row.PPContextMenu(() => new[]
        {
            MenuEntry.Item(L.T("workspace.rename"), () => Actions.RenameWorkspace(ws), icon: "pencil"),
            MenuEntry.Item(L.T("menu.stopAll"), () => AppModel.Shared.Perform(async api => await api.StopAllAsync(workspaceId: ws.Id)),
                icon: "square", disabled: running == 0),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("workspace.delete"), () => Actions.DeleteWorkspace(ws), icon: "trash-2", destructive: true),
        });
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressId = ws.Id;
            _pressAt = e.GetPosition(this);
        };
        _rows.Add((ws.Id, row));
        if (_dragging && _pressId == ws.Id) row.Alpha(0.6);
        return row;
    }

    // MARK: 拖动排序（拖进别的工作区行时立即移动，松开时提交新顺序）

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_pressId == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.Y - _pressAt.Y) < 4) return;
            _dragging = true;
            CaptureMouse();
            Invalidate();
        }
        foreach (var (id, row) in _rows)
        {
            if (id == _pressId || !row.IsVisible) continue;
            var top = row.TranslatePoint(new Point(0, 0), this).Y;
            if (p.Y < top || p.Y > top + row.ActualHeight) continue;
            var list = _data.Workspaces.ToList();
            var from = list.FindIndex(w => w.Id == _pressId);
            var to = list.FindIndex(w => w.Id == id);
            if (from < 0 || to < 0 || from == to) break;
            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            _data.Workspaces = list;
            break;
        }
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            e.Handled = true;
            ReleaseMouseCapture(); // 会触发 LostMouseCapture → EndDrag
        }
        _pressId = null;
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging) return;
        _dragging = false;
        _pressId = null;
        if (commit)
        {
            var ids = _data.Workspaces.Select(w => w.Id).ToList();
            AppModel.Shared.Perform(async api => await api.ReorderWorkspacesAsync(ids));
        }
        Invalidate();
    }
}

/// <summary>侧栏的一行：图标、名称、运行中绿点、数量（对应 Mac SidebarItem）。</summary>
public static class SidebarItem
{
    public static PPRow Make(string icon, string title, bool selected, Action action, int? count = null, string? countTone = null, bool runningDot = false) =>
        Make(IconKey.Builtin(icon), title, selected, action, count, countTone, runningDot);

    public static PPRow Make(IconKey icon, string title, bool selected, Action action, int? count = null, string? countTone = null, bool runningDot = false)
    {
        var content = HStack(M.Md,
            new PPIcon(icon, M.Icon, selected ? ColorToken.Accent : ColorToken.IconPrimary),
            Text(title, selected ? Typo.BodyStrong : Typo.Body).Flex(),
            runningDot ? Box().Size(6, 6).Rounded(3).Bg(ColorToken.StatusRunning) : null,
            count is { } n ? Text(n.ToString(), Typo.Small, countTone == null ? ColorToken.TextTertiary : Theme.Tone(countTone).Fg).TabularDigits() : null);
        return new PPRow(content, selected, action, M.RowHeight);
    }
}

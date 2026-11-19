using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 工作区页：工程卡片网格（自适应列宽，可拖动排序，按工具栏搜索词过滤）。workspaceId 为 null 时是「运行中」聚合页
/// （顶部汇总 + 排序，按内存时每 10 秒重排一次）。对应 Mac 的 WorkspacePage。
/// 工作区页顶部的分段（启动 | 工程 | 版本控制 | 终端）由 VCSWorkspaceSwitcher 提供，这里只给「工程」分段的网格。
/// </summary>
public sealed class WorkspacePage : PPView
{
    private readonly string? _workspaceId;
    private readonly State<bool> _sortByMemory;
    private ProjectGrid? _grid;

    public WorkspacePage(string? workspaceId)
    {
        _workspaceId = workspaceId;
        _sortByMemory = new State<bool>(UiPrefs.Get("RunningSortByMemory") == "1");
    }

    /// <summary>网格只建一个实例：分段切走再切回、切换器重建时都复用它（滚动位置、卡片状态都在）。</summary>
    private UIElement GridView()
    {
        _grid ??= new ProjectGrid(_workspaceId, _sortByMemory);
        Detach(_grid);
        return _grid;
    }

    protected override UIElement Build()
    {
        if (_workspaceId is { } ws) return Keep("switcher", () => new VCSWorkspaceSwitcher(ws, GridView));
        var sort = Bind.Of(() => _sortByMemory.Value, v =>
        {
            _sortByMemory.Value = v;
            UiPrefs.Set("RunningSortByMemory", v ? "1" : "0");
        });
        var col = VStack(0, Keep("summary", () => new RunningSummaryBar(sort)), new PPDivider(subtle: true), GridView().Flex());
        col.Align = CrossAlign.Stretch;
        return col;
    }
}

/// <summary>工程卡片网格（「工程」分段的内容，或「运行中」页的主体）。</summary>
public sealed class ProjectGrid : PPView
{
    private readonly string? _workspaceId;
    private readonly State<bool> _sortByMemory;
    private readonly DataStore _data = AppModel.Shared.Data;
    private readonly NavStore _nav = AppModel.Shared.Nav;
    private readonly RunStore _runs = AppModel.Shared.Runs;
    private DispatcherTimer? _sortTimer;

    // 拖动排序：按下的卡片、按下位置、拖动中（拖进别的卡片时立即移动，松开时提交新顺序）
    private string? _pressId;
    private Point _pressAt;
    private bool _dragging;
    private readonly List<(string Id, FrameworkElement Card)> _cards = new();

    public ProjectGrid(string? workspaceId, State<bool> sortByMemory)
    {
        _workspaceId = workspaceId;
        _sortByMemory = sortByMemory;
        Observe(_data, nameof(DataStore.Projects), nameof(DataStore.Workspaces));
        Observe(_nav, nameof(NavStore.Search));
        if (workspaceId == null)
        {
            Observe(_runs, nameof(RunStore.Runs));
            Observe(sortByMemory);
        }
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragEnd;
        // LostMouseCapture 会从子元素冒泡上来（开始拖动时卡片把捕获让给网格），只认网格自己失去捕获
        LostMouseCapture += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) EndDrag(commit: true); };
    }

    private bool Reorderable => _workspaceId != null && string.IsNullOrWhiteSpace(_nav.Search);

    protected override void OnAppear()
    {
        if (_workspaceId != null) return;
        // 「运行中」页按内存排序：每 10 秒按最新数值重排一次，避免网格频繁跳动
        _sortTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) =>
        {
            if (_sortByMemory.Value && !_dragging) Invalidate();
        }, Dispatcher);
        _sortTimer.Start();
    }

    protected override void OnDisappear() => _sortTimer?.Stop();

    private List<ProjectSummary> Projects()
    {
        List<ProjectSummary> list;
        if (_workspaceId is { } ws) list = _data.ProjectsIn(ws);
        else
        {
            var activeIds = new HashSet<string>(_runs.ActiveRuns.Select(r => r.ProjectId));
            list = _data.AllProjects.Where(p => p.HasActiveRun || activeIds.Contains(p.Id) || (p.Status == RunState.Failed && p.Runs.Count > 0)).ToList();
            if (_sortByMemory.Value)
            {
                var mem = list.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => ResourceStore.Shared.Memory(g.Key));
                list = list.OrderByDescending(p => mem[p.Id]).ToList();
            }
        }
        var q = _nav.Search.Trim();
        if (q.Length == 0) return list;
        bool Has(string s) => s.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        return list.Where(p => Has(p.Name) || Has(p.Path) || p.Facets.Any(f => Has(f.Name))).ToList();
    }

    protected override UIElement Build()
    {
        _cards.Clear();
        var list = Projects();
        if (list.Count == 0)
        {
            if (_nav.Search.Trim().Length > 0) return new PPEmptyState("search", L.T("workspace.noMatch"));
            if (_workspaceId == null) return new PPEmptyState("activity", L.T("running.empty"));
            return new PPEmptyState("folder-open", L.T("workspace.empty"), L.T("toolbar.import"), ImportFlow.ImportFolder, "folder-input");
        }
        // 同一排的卡片一样高（和 Mac 一样每排按最高的那张）
        var grid = new AdaptiveGrid(M.CardMinWidth, M.CardMaxWidth, M.Lg, stretch: true);
        foreach (var p in list)
        {
            var card = Keep("card:" + p.Id, () => MakeCard(p), c => c.Update(p));
            card.Opacity = _dragging && _pressId == p.Id ? 0.6 : 1;
            grid.Children.Add(card);
            _cards.Add((p.Id, card));
        }
        var content = Box(grid).Pad(M.Xl);
        return Scroll(content).Key("workspace.grid").Id("workspace.grid");
    }

    private ProjectCard MakeCard(ProjectSummary p)
    {
        var card = new ProjectCard(p, showWorkspace: _workspaceId == null);
        var id = p.Id;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!Reorderable) return;
            _pressId = id;
            _pressAt = e.GetPosition(this);
        };
        return card;
    }

    // MARK: 拖动排序（同 Sidebar 里工作区的做法：拖进别的卡片时立即移动，松开时提交 project.reorder）

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_pressId == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressAt.X) < 4 && Math.Abs(p.Y - _pressAt.Y) < 4) return;
            if (!Reorderable) { _pressId = null; return; }
            PopupController.DismissAll();
            _dragging = true;
            CaptureMouse();
            Invalidate();
        }
        foreach (var (id, card) in _cards)
        {
            if (id == _pressId || !card.IsVisible) continue;
            var topLeft = card.TranslatePoint(new Point(0, 0), this);
            if (!new Rect(topLeft, new Size(card.ActualWidth, card.ActualHeight)).Contains(p)) continue;
            Move(_pressId, id);
            break;
        }
    }

    /// <summary>在本地顺序里把 from 移到 to 的位置（网格随 Workspaces 变化重排）。</summary>
    private void Move(string from, string to)
    {
        if (_workspaceId is not { } wsId) return;
        var list = _data.Workspaces.ToList();
        var i = list.FindIndex(w => w.Id == wsId);
        if (i < 0) return;
        var ids = list[i].ProjectIds.ToList();
        int a = ids.IndexOf(from), b = ids.IndexOf(to);
        if (a < 0 || b < 0 || a == b) return;
        ids.RemoveAt(a);
        ids.Insert(b, from);
        list[i] = list[i] with { ProjectIds = ids };
        _data.Workspaces = list;
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
        if (commit && _workspaceId is { } wsId && _data.Workspace(wsId) is { } ws)
        {
            var ids = ws.ProjectIds.ToList();
            AppModel.Shared.Perform(async api => await api.ReorderProjectsAsync(wsId, ids));
        }
        Invalidate();
    }
}

/// <summary>
/// 自适应列宽网格（对应 SwiftUI 的 LazyVGrid + GridItem(.adaptive(minimum:maximum:))）：
/// 列数 = ⌊(宽 + 间距) / (最小宽 + 间距)⌋，列宽平分且不超过最大宽（超过时靠左）；每行高取该行最高的，格子在行内垂直居中。
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    private readonly double _min;
    private readonly double _max;
    private readonly double _spacing;
    private readonly bool _center;
    private readonly bool _stretch;
    private int _cols = 1;
    private double _colW;
    private readonly List<double> _rowHeights = new();

    /// <param name="center">列宽到了上限、一行放不满时整体居中（LazyVGrid 默认对齐）；否则靠左（alignment: .leading）。</param>
    /// <param name="stretch">同一排的元素拉成一样高（按这一排最高的那个），内容靠上；否则各自高度、在排内垂直居中。</param>
    public AdaptiveGrid(double minItemWidth, double maxItemWidth, double spacing, bool center = false, bool stretch = false)
    {
        _min = minItemWidth;
        _max = maxItemWidth;
        _spacing = spacing;
        _center = center;
        _stretch = stretch;
    }

    private void Columns(double width)
    {
        if (double.IsInfinity(width) || double.IsNaN(width)) width = _min;
        _cols = Math.Max(1, (int)Math.Floor((width + _spacing) / (_min + _spacing)));
        _colW = Math.Min(_max, Math.Max(0, (width - _spacing * (_cols - 1)) / _cols));
    }

    private IEnumerable<UIElement> Visible => InternalChildren.Cast<UIElement>().Where(c => c != null && c.Visibility != Visibility.Collapsed);

    protected override Size MeasureOverride(Size avail)
    {
        Columns(avail.Width);
        _rowHeights.Clear();
        int i = 0;
        double rowH = 0;
        foreach (var c in Visible)
        {
            c.Measure(new Size(_colW, double.PositiveInfinity));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            if (++i % _cols == 0) { _rowHeights.Add(rowH); rowH = 0; }
        }
        if (i % _cols != 0) _rowHeights.Add(rowH);
        double h = _rowHeights.Sum() + _spacing * Math.Max(0, _rowHeights.Count - 1);
        double w = double.IsInfinity(avail.Width) ? _cols * _colW + _spacing * (_cols - 1) : avail.Width;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        Columns(final.Width);
        double used = _cols * _colW + _spacing * (_cols - 1);
        double x0 = _center ? Math.Max(0, (final.Width - used) / 2) : 0;
        int i = 0;
        double y = 0;
        foreach (var c in Visible)
        {
            int row = i / _cols, col = i % _cols;
            if (col == 0 && row > 0) y += _rowHeights[row - 1] + _spacing;
            double rh = row < _rowHeights.Count ? _rowHeights[row] : c.DesiredSize.Height;
            double h = _stretch ? rh : Math.Min(c.DesiredSize.Height, rh);
            c.Arrange(new Rect(x0 + col * (_colW + _spacing), y + (rh - h) / 2, _colW, h));
            i++;
        }
        return final;
    }
}

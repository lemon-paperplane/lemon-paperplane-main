using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 日志：提交图、引用徽标、按分支 / 作者 / 关键词 / 路径过滤、分页、右侧提交详情（对应 Mac VCSLogView.swift）。
// 提交列表自绘（VCSLogList）：只画可见行，图、徽标、文字都在 OnRender 里画。

public sealed class VCSLogModel : PPObservable
{
    public VCSRepo Repo { get; }

    private List<VCSCommit> _commits = new();
    private bool _hasMore;
    private bool _hasGraph = true;
    private bool _loading;
    private bool _loadingMore;
    private string? _error;
    private string _branch = VCSPrefs.LogAllBranches ? "--all" : "";
    private HashSet<string> _selected = new();
    private string? _primary;

    public List<VCSCommit> Commits { get => _commits; private set { _commits = value; Notify(); } }
    public bool HasMore { get => _hasMore; private set => Set(ref _hasMore, value); }
    public bool HasGraph { get => _hasGraph; private set => Set(ref _hasGraph, value); }
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }
    public bool LoadingMore { get => _loadingMore; private set => Set(ref _loadingMore, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>"--all" 全部分支、"" 当前分支、其它为分支名</summary>
    public string Branch { get => _branch; set => Set(ref _branch, value); }
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public string Path { get; set; } = "";

    public HashSet<string> Selected { get => _selected; set { _selected = value; Notify(); } }
    public string? Primary { get => _primary; set => Set(ref _primary, value); }
    public double DetailWidth { get; set; } = 400;

    private int _loadedRevision = -1;
    private string? _anchor;
    private int _gen;
    public const int PageSize = 200;

    public VCSLogModel(VCSRepo repo) { Repo = repo; }

    private VCSLogArgs Args(int skip)
    {
        var a = new VCSLogArgs { Ref = _branch.Length == 0 ? null : _branch, Skip = skip > 0 ? skip : null, Limit = PageSize };
        var t = Text.Trim();
        if (t.Length > 0) a.Grep = t;
        var au = Author.Trim();
        if (au.Length > 0) a.Author = au;
        var p = Path.Trim();
        if (p.Length > 0) a.Path = p;
        return a;
    }

    /// <summary>仓库变化后（或首次）重新加载第一页</summary>
    public void EnsureLoaded()
    {
        if (_loadedRevision != Repo.Revision) Reload();
    }

    public async void Reload()
    {
        _loadedRevision = Repo.Revision;
        var gen = ++_gen;
        Loading = _commits.Count == 0;
        try
        {
            var sw = Stopwatch.StartNew();
            var log = await Repo.Api().VcsLogAsync(Repo.ProjectId, Args(0), Repo.Root);
            VCSPerf.Record("log.fetch", sw.Elapsed.TotalMilliseconds);
            if (gen != _gen) return;
            Commits = log.Commits;
            HasMore = log.HasMore;
            HasGraph = (log.Graph ?? true) && log.Commits.Any(c => c.Graph != null);
            Error = null;
            // 关键词像提交哈希且没匹配到信息：直接显示那个提交
            var t = Text.Trim();
            if (log.Commits.Count == 0 && t.Length >= 7 && t.Length <= 40 && t.All(Uri.IsHexDigit)) Primary = t;
            if (_primary is { } p && log.Commits.All(c => c.Hash != p))
            {
                // 选中的提交不在第一页（或已不存在）时保持详情，但列表里不再高亮
                var hashes = new HashSet<string>(log.Commits.Select(c => c.Hash));
                Selected = new HashSet<string>(_selected.Where(hashes.Contains));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (gen != _gen) return;
            Error = e.Message;
        }
        if (gen == _gen) Loading = false;
    }

    public async void LoadMore()
    {
        if (!_hasMore || _loadingMore || _loading) return;
        LoadingMore = true;
        var skip = _commits.Count;
        var gen = _gen;
        try
        {
            var log = await Repo.Api().VcsLogAsync(Repo.ProjectId, Args(skip), Repo.Root);
            if (gen == _gen && _commits.Count == skip)
            {
                var all = new List<VCSCommit>(_commits.Count + log.Commits.Count);
                all.AddRange(_commits);
                all.AddRange(log.Commits);
                Commits = all;
                HasMore = log.HasMore;
            }
        }
        catch { }
        LoadingMore = false;
    }

    /// <summary>过滤条件变化：稍等再查</summary>
    public async void FiltersChanged()
    {
        var gen = ++_gen;
        await Task.Delay(350);
        if (gen != _gen) return;
        Reload();
    }

    public void Click(VCSCommit c)
    {
        if (VCSModifiers.Command)
        {
            var s = new HashSet<string>(_selected);
            if (!s.Remove(c.Hash)) s.Add(c.Hash);
            Selected = s;
            Primary = c.Hash;
        }
        else if (VCSModifiers.Shift && _anchor is { } a && _commits.FindIndex(x => x.Hash == a) is var i && i >= 0
                 && _commits.FindIndex(x => x.Hash == c.Hash) is var j && j >= 0)
        {
            Selected = new HashSet<string>(_commits.Skip(Math.Min(i, j)).Take(Math.Abs(i - j) + 1).Select(x => x.Hash));
            Primary = c.Hash;
        }
        else
        {
            Selected = new HashSet<string> { c.Hash };
            Primary = c.Hash;
            _anchor = c.Hash;
        }
    }

    /// <summary>选中的提交（列表顺序：新的在前）</summary>
    public List<VCSCommit> SelectedCommits => _commits.Where(c => _selected.Contains(c.Hash)).ToList();

    public void Focus(string hash)
    {
        if (_commits.Any(c => c.Hash == hash))
        {
            Selected = new HashSet<string> { hash };
            _anchor = hash;
        }
        Primary = hash;
    }
}

/// <summary>日志子页。对应 Mac VCSLogPage。</summary>
public sealed class VCSLogPage : PPView
{
    private readonly VCSRepo _repo;
    private readonly VCSLogModel _log;

    public VCSLogPage(VCSRepo repo)
    {
        _repo = repo;
        _log = repo.Log;
    }

    protected override async void OnAppear()
    {
        _repo.PropertyChanged += OnRepo;
        _log.EnsureLoaded();
        if (_repo.Branches == null) await _repo.LoadBranches();
    }

    protected override void OnDisappear() => _repo.PropertyChanged -= OnRepo;

    private void OnRepo(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VCSRepo.Revision)) _log.EnsureLoaded();
    }

    protected override UIElement Build()
    {
        var body = new Grid();
        var listCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        var detailCol = new ColumnDefinition { Width = new GridLength(_log.DetailWidth) };
        body.ColumnDefinitions.Add(listCol);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        body.ColumnDefinitions.Add(detailCol);
        body.Children.Add(new VCSLogListArea(_repo).At(0, 0));
        body.Children.Add(new VCSResizeHandle(() => _log.DetailWidth, w => { _log.DetailWidth = w; detailCol.Width = new GridLength(w); }, 280, 720).At(0, 1));
        body.Children.Add(new VCSLogDetailHost(_repo).At(0, 2));
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new VCSLogFilterBar(_repo).At(0, 0));
        g.Children.Add(new PPDivider(subtle: true).At(1, 0));
        g.Children.Add(body.At(2, 0));
        return g;
    }
}

/// <summary>过滤栏（输入框跨重建复用）</summary>
internal sealed class VCSLogFilterBar : PPView
{
    private readonly VCSRepo _repo;
    private readonly VCSLogModel _log;

    public VCSLogFilterBar(VCSRepo repo)
    {
        _repo = repo;
        _log = repo.Log;
        Observe(_log, nameof(VCSLogModel.Branch), nameof(VCSLogModel.Loading), nameof(VCSLogModel.LoadingMore), nameof(VCSLogModel.Commits), nameof(VCSLogModel.HasMore));
        Observe(repo, nameof(VCSRepo.Branches));
    }

    private List<DropdownOption<string>> BranchOptions()
    {
        var o = new List<DropdownOption<string>>
        {
            new("--all", L.T("vcs.log.allBranches"), icon: "git-fork"),
            new("", L.T("vcs.log.currentBranch"), icon: "circle-dot"),
        };
        if (_repo.Branches is { } b)
        {
            o.AddRange(b.Local.Select(x => new DropdownOption<string>(x.Name, x.Name, icon: "git-branch")));
            o.AddRange(b.Remote.Select(x => new DropdownOption<string>(x.Name, x.Name, icon: "cloud")));
        }
        if (o.All(x => x.Value != _log.Branch)) o.Add(new DropdownOption<string>(_log.Branch, _log.Branch));
        return o;
    }

    private PPTextField Field(string key, string placeholder, string icon, Func<string> get, Action<string> set, double width, double minWidth = 0) =>
        Keep(key, () =>
        {
            var tf = new PPTextField(placeholder, Bind.Of(get, v => { set(v); _log.FiltersChanged(); }), icon: icon, clearable: true, height: M.ControlHeightSmall);
            tf.W(width).Shrink().Id("vcs.log." + key);
            if (minWidth > 0) tf.MinWidth = minWidth;
            return tf;
        });

    protected override UIElement Build()
    {
        var dd = new PPDropdown<string>(BranchOptions(), Bind.Of(() => _log.Branch, v =>
        {
            _log.Branch = v;
            if (v == "--all" || v.Length == 0) VCSPrefs.LogAllBranches = v == "--all";
            _log.Reload();
        }), size: PPButtonSize.Small, minWidth: 150, maxWidth: 220).Id("vcs.log.branch");
        var text = Field("text", L.T("vcs.log.searchText"), "search", () => _log.Text, v => _log.Text = v, 300, 160);
        var author = Field("author", L.T("vcs.log.author"), "user", () => _log.Author, v => _log.Author = v, 150);
        var path = Field("path", L.T("vcs.log.path"), "folder", () => _log.Path, v => _log.Path = v, 170);
        foreach (var f in new UIElement[] { text, author, path }) UI.Detach(f);
        var row = HStack(M.Sm, dd, text, author, path, Spacer(),
            _log.Loading || _log.LoadingMore ? new PPSpinner(M.IconSmall) : null,
            Text(L.T("vcs.log.count", _log.Commits.Count) + (_log.HasMore ? "+" : ""), Typo.Small, ColorToken.TextTertiary).Shrink(false),
            new PPIconButton("refresh-cw", L.T("vcs.refresh"), _log.Reload, size: M.IconButtonSmall, iconSize: M.IconSmall));
        row.Padding = new Thickness(M.Md, 0, M.Md, 0);
        row.Height = M.ToolbarHeight;
        return row;
    }
}

/// <summary>列表区：表头 + 自绘提交列表 / 错误 / 空</summary>
internal sealed class VCSLogListArea : PPView
{
    public const double RowH = 24;
    private readonly VCSRepo _repo;
    private readonly VCSLogModel _log;
    private readonly CommitGraphGeometry _geo = new(12, RowH, 6);
    private VCSLogList? _list;
    private ScrollViewer? _scroll;

    public VCSLogListArea(VCSRepo repo)
    {
        _repo = repo;
        _log = repo.Log;
        Observe(_log, nameof(VCSLogModel.Commits), nameof(VCSLogModel.HasGraph), nameof(VCSLogModel.Error), nameof(VCSLogModel.Loading));
    }

    protected override UIElement Build()
    {
        if (_log.Error is { } e && _log.Commits.Count == 0)
            return new PPAlert("danger", L.T("vcs.log.failed"), e, new PPButton(L.T("connection.retry"), _log.Reload, size: PPButtonSize.Small)).Margins(M.Md).Top();
        if (_log.Commits.Count == 0 && !_log.Loading) return new PPEmptyState("git-commit-horizontal", L.T("vcs.log.empty"));
        double graphWidth = 0;
        if (_log.HasGraph)
        {
            var graphs = _log.Commits.Where(c => c.Graph != null).Select(c => c.Graph!);
            graphWidth = Math.Min(_geo.Width(graphs), 220);
        }
        if (_list == null)
        {
            _list = new VCSLogList(_repo, _geo, RowH);
            _scroll = _list.InScroll();
            _list.Id("vcs.log.list");
            _scroll.Id("vcs.log.scroll");
        }
        _list.SetData(_log.Commits, graphWidth);
        UI.Detach(_scroll!);
        var header = HStack(0,
            Gap(0).W(graphWidth),
            Text(L.T("vcs.log.subject"), Typo.Label, ColorToken.TextSecondary).Margins(M.Sm, 0, 0, 0).Flex(),
            Text(L.T("vcs.log.authorColumn"), Typo.Label, ColorToken.TextSecondary).W(130).Shrink(false),
            Text(L.T("vcs.log.date"), Typo.Label, ColorToken.TextSecondary).W(118).Shrink(false),
            Text(L.T("vcs.log.hash"), Typo.Label, ColorToken.TextSecondary).W(70).Shrink(false));
        header.Height = M.RowHeightCompact;
        header.Bg(ColorToken.BgSubtle);
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(header.At(0, 0));
        g.Children.Add(new PPDivider(subtle: true).At(1, 0));
        g.Children.Add(_scroll!.At(2, 0));
        return g;
    }
}

/// <summary>
/// 提交列表（自绘，只画可见行）：提交图（车道颜色 graphLane0…7）、引用徽标、主题、作者、日期、短哈希；
/// 单击 / Ctrl 多选 / Shift 连选，右键菜单，悬停提示，滚到末尾自动加载下一页。
/// </summary>
public sealed class VCSLogList : VCSScrollPanel
{
    private readonly VCSRepo _repo;
    private readonly VCSLogModel _log;
    private readonly CommitGraphGeometry _geo;
    private readonly double _rowH;
    private List<VCSCommit> _commits = new();
    private double _graphWidth;
    private int _hover = -1;
    private int _menuIndex = -1;
    private PPSpinner? _spinner;
    private readonly Dictionary<long, FormattedText> _cache = new();
    private readonly Typeface _ui = new(UIFonts.UI, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _uiStrong = new(UIFonts.UI, FontStyles.Normal, Typo.BodyStrong.Weight, FontStretches.Normal);
    private readonly Typeface _mono = new(UIFonts.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private string? _tip;
    // 每行的徽标 / 「+N」 / 日期位置（悬停提示用）
    private readonly Dictionary<int, List<(Rect Rect, string Text)>> _hotspots = new();

    public VCSLogList(VCSRepo repo, CommitGraphGeometry geo, double rowH)
    {
        _repo = repo;
        _log = repo.Log;
        _geo = geo;
        _rowH = rowH;
        Loaded += (_, _) =>
        {
            _log.PropertyChanged -= OnLog;
            _log.PropertyChanged += OnLog;
            Theme.Changed -= OnTheme;
            Theme.Changed += OnTheme;
            L10n.Shared.Changed -= OnTheme;
            L10n.Shared.Changed += OnTheme;
        };
        Unloaded += (_, _) =>
        {
            _log.PropertyChanged -= OnLog;
            Theme.Changed -= OnTheme;
            L10n.Shared.Changed -= OnTheme;
        };
        ToolTipService.SetInitialShowDelay(this, 500);
        this.PPContextMenu(MenuEntries);
        Focusable = false;
        // 自动化（调试桥按 id 点击）：鼠标在列表上时点悬停的行，否则选中当前行的下一行
        SetValue(UI.ClickActionProperty, new Action(() =>
        {
            if (_hover >= 0) { ClickRow(_hover); return; }
            var cur = _log.Primary is { } p ? _commits.FindIndex(c => c.Hash == p) : -1;
            ClickRow(Math.Min(cur + 1, _commits.Count - 1));
        }));
    }

    protected override double LineStep => _rowH;

    private void OnTheme(object? sender, EventArgs e)
    {
        _cache.Clear();
        _pens.Clear();
        InvalidateVisual();
    }

    // 车道画笔（按颜色序号缓存，换主题时清掉）
    private readonly Dictionary<int, Pen> _pens = new();

    private Pen LanePen(int color)
    {
        int i = CommitGraphGeometry.PaletteIndex(color);
        if (_pens.TryGetValue(i, out var p)) return p;
        p = new Pen(B(Theme.GraphLane(i)), 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        _pens[i] = p;
        return p;
    }

    private void OnLog(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VCSLogModel.Selected):
            case nameof(VCSLogModel.Primary):
                InvalidateVisual();
                break;
            case nameof(VCSLogModel.LoadingMore):
                InvalidateMeasure();
                InvalidateVisual();
                break;
        }
    }

    public void SetData(List<VCSCommit> commits, double graphWidth)
    {
        if (ReferenceEquals(commits, _commits) && Math.Abs(graphWidth - _graphWidth) < 0.1) return;
        bool append = commits.Count >= _commits.Count && _commits.Count > 0 && commits.Count > 0 && ReferenceEquals(commits[0], _commits[0]);
        if (!append) _cache.Clear();
        _commits = commits;
        _graphWidth = graphWidth;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double ExtraBottom => _log.LoadingMore ? 40 : 0;

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? 800 : available.Width;
        var h = double.IsInfinity(available.Height) ? 600 : available.Height;
        UpdateScrollInfo(new Size(w, h), new Size(w, _commits.Count * _rowH + ExtraBottom));
        // 末尾的转圈
        if (_log.LoadingMore)
        {
            if (_spinner == null)
            {
                _spinner = new PPSpinner(M.IconSmall);
                Children.Add(_spinner);
            }
            _spinner.Measure(new Size(w, 40));
        }
        else if (_spinner != null)
        {
            Children.Remove(_spinner);
            _spinner = null;
        }
        // 接近末尾：加载下一页（对应 Mac 里倒数第 20 行出现时 loadMore）
        int last = (int)Math.Floor((OffsetY + h) / _rowH);
        if (_commits.Count > 0 && last >= _commits.Count - 20 && _log.HasMore && !_log.LoadingMore)
            Dispatcher.BeginInvoke(new Action(_log.LoadMore));
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _spinner?.Arrange(new Rect(0, _commits.Count * _rowH - OffsetY, final.Width, 40));
        return final;
    }

    private Brush B(string key) => Theme.Brush(key);

    private double Pdip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText Fmt(string s, Typeface tf, double size, Brush b, double maxWidth = 0)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, b, null, TextOptions.GetTextFormattingMode(this), Pdip);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    private FormattedText Cached(int row, int part, Func<FormattedText> make)
    {
        long key = row * 16L + part;
        if (_cache.TryGetValue(key, out var ft)) return ft;
        ft = make();
        _cache[key] = ft;
        return ft;
    }

    // 列宽（从右往左）：哈希 70、日期 118、作者 130
    private const double HashW = 70, DateW = 118, AuthorW = 130;

    protected override void OnRender(DrawingContext dc)
    {
        var sw = Stopwatch.StartNew();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_commits.Count == 0) return;
        if (_cache.Count > 8000) _cache.Clear();
        _hotspots.Clear();
        int first = Math.Max(0, (int)Math.Floor(OffsetY / _rowH));
        int last = Math.Min(_commits.Count - 1, (int)Math.Floor((OffsetY + h) / _rowH));
        var selected = _log.Selected;
        double subjectRight = Math.Max(_graphWidth + 40, w - HashW - DateW - AuthorW);
        for (int i = first; i <= last; i++)
        {
            var c = _commits[i];
            double y = i * _rowH - OffsetY;
            var rowRect = new Rect(0, y, w, _rowH);
            if (selected.Contains(c.Hash)) dc.DrawRectangle(B(ColorToken.BgSelected), null, rowRect);
            else if (i == _hover) dc.DrawRectangle(B(ColorToken.BgHover), null, rowRect);
            bool isHead = c.Refs.Any(r => r.Kind == "head");
            if (_graphWidth > 0 && c.Graph != null)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, y, _graphWidth, _rowH)));
                DrawGraph(dc, c.Graph, i > 0 ? _commits[i - 1].Graph : null, y, isHead, c.Parents.Count > 1);
                dc.Pop();
            }
            // 主题列：徽标 + 主题
            double x = _graphWidth + M.Sm;
            var refs = c.Refs.Where(r => r.Kind != "head").ToList();
            var spots = new List<(Rect, string)>();
            dc.PushClip(new RectangleGeometry(new Rect(x, y, Math.Max(0, subjectRight - x), _rowH)));
            for (int k = 0; k < Math.Min(3, refs.Count); k++)
            {
                var r = refs[k];
                double bw = DrawBadge(dc, i, k, r, isHead && r.Kind == "local", x, y);
                spots.Add((new Rect(x, y, bw, _rowH), r.Name));
                x += bw + M.Xs;
            }
            if (refs.Count > 3)
            {
                var more = Cached(i, 4, () => Fmt("+" + (refs.Count - 3), _ui, Typo.Caption.Size, B(ColorToken.TextTertiary)));
                dc.DrawText(more, new Point(x, y + (_rowH - more.Height) / 2));
                spots.Add((new Rect(x, y, more.WidthIncludingTrailingWhitespace, _rowH), string.Join("\n", refs.Skip(3).Select(r => r.Name))));
                x += more.WidthIncludingTrailingWhitespace + M.Xs;
            }
            double subjW = subjectRight - x - M.Sm;
            if (subjW > 8)
            {
                var subj = Cached(i, 0, () => Fmt(c.Subject, isHead ? _uiStrong : _ui, Typo.Body.Size, B(ColorToken.TextPrimary), subjW));
                if (Math.Abs(subj.MaxTextWidth - subjW) > 0.5) { _cache.Remove(i * 16L); subj = Cached(i, 0, () => Fmt(c.Subject, isHead ? _uiStrong : _ui, Typo.Body.Size, B(ColorToken.TextPrimary), subjW)); }
                dc.DrawText(subj, new Point(x, y + (_rowH - subj.Height) / 2));
            }
            dc.Pop();
            // 作者、日期、哈希
            double ax = subjectRight;
            var author = Cached(i, 1, () => Fmt(c.Author, _ui, Typo.Body.Size, B(ColorToken.TextSecondary), AuthorW - 4));
            dc.DrawText(author, new Point(ax, y + (_rowH - author.Height) / 2));
            var date = Cached(i, 2, () => Fmt(VCSDate.Short(c.ParsedDate), _ui, Typo.Body.Size, B(ColorToken.TextSecondary), DateW - 4));
            dc.DrawText(date, new Point(ax + AuthorW, y + (_rowH - date.Height) / 2));
            spots.Add((new Rect(ax + AuthorW, y, DateW, _rowH), VCSDate.Full(c.ParsedDate)));
            var hash = Cached(i, 3, () => Fmt(c.Short, _mono, Typo.MonoSmall.Size, B(ColorToken.TextTertiary), HashW - 4));
            dc.DrawText(hash, new Point(ax + AuthorW + DateW, y + (_rowH - hash.Height) / 2));
            _hotspots[i] = spots;
        }
        VCSPerf.Record("log.render", sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>提交图的一格（内核算好的车道与连线，CommitGraphGeometry 换算坐标）</summary>
    private void DrawGraph(DrawingContext dc, VCSGraphRow row, VCSGraphRow? previous, double y, bool isHead, bool merge)
    {
        var r = _geo.RowFor(row, previous);
        foreach (var s in r.Segments)
        {
            dc.DrawLine(LanePen(s.Color), new Point(s.From.X, y + s.From.Y), new Point(s.To.X, y + s.To.Y));
        }
        double radius = merge ? 3.5 : 4;
        var center = new Point(r.Node.X, y + r.Node.Y);
        var color = B(Theme.GraphLane(r.NodeColor));
        if (isHead) dc.DrawEllipse(B(ColorToken.BgContent), new Pen(color, 1.8), center, radius, radius);
        else dc.DrawEllipse(color, null, center, radius, radius);
    }

    /// <summary>引用徽标（与 VCSViews.RefBadge 同样的外观）；返回宽度</summary>
    private double DrawBadge(DrawingContext dc, int row, int k, VCSRef r, bool current, double x, double y)
    {
        var (icon, fg, bg) = VCSRefStyle.Of(r, current);
        var text = Cached(row, 5 + k, () => Fmt(r.Name, _ui, Typo.Caption.Size, B(fg), 160));
        double w = 5 + 11 + 3 + text.WidthIncludingTrailingWhitespace + 5;
        double top = y + (_rowH - 16) / 2;
        dc.DrawRoundedRectangle(B(bg), null, new Rect(x, top, w, 16), M.RadiusSM, M.RadiusSM);
        if (IconStore.Shared.Icon(icon) is { } ic) IconRenderer.Draw(dc, ic, new Rect(x + 5, top + 2.5, 11, 11), B(fg), M.StrokeScale(11));
        dc.DrawText(text, new Point(x + 5 + 11 + 3, top + (16 - text.Height) / 2));
        return w;
    }

    // MARK: 鼠标

    private int RowAt(Point p)
    {
        var i = (int)Math.Floor((p.Y + OffsetY) / _rowH);
        return i >= 0 && i < _commits.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        var i = RowAt(p);
        if (i != _hover)
        {
            _hover = i;
            InvalidateVisual();
        }
        string? tip = null;
        if (i >= 0 && _hotspots.TryGetValue(i, out var spots))
            foreach (var (rect, text) in spots)
                if (rect.Contains(p)) { tip = text; break; }
        if (tip != _tip)
        {
            _tip = tip;
            ToolTip = tip;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var i = RowAt(e.GetPosition(this));
        if (i < 0) return;
        _log.Click(_commits[i]);
        e.Handled = true;
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonDown(e);
        _menuIndex = RowAt(e.GetPosition(this));
    }

    private IEnumerable<MenuEntry> MenuEntries()
    {
        // 键盘菜单键 / 自动化打开时没有鼠标位置：用当前选中的提交
        if ((_menuIndex < 0 || _menuIndex >= _commits.Count) && _log.Primary is { } p) _menuIndex = _commits.FindIndex(x => x.Hash == p);
        if (_menuIndex < 0 || _menuIndex >= _commits.Count) return Array.Empty<MenuEntry>();
        var c = _commits[_menuIndex];
        _menuIndex = -1;
        if (!_log.Selected.Contains(c.Hash)) _log.Click(c);
        var sel = _log.SelectedCommits;
        return VCSCommitMenu.Entries(_repo, sel.Count == 0 ? new List<VCSCommit> { c } : sel);
    }

    /// <summary>调试桥 / 测试用：选中第 index 行</summary>
    public void ClickRow(int index)
    {
        if (index >= 0 && index < _commits.Count) _log.Click(_commits[index]);
    }
}

/// <summary>右侧面板的拖动宽度把手：1px 分隔线 + 7px 拖动热区。对应 Mac VCSResizeHandle。</summary>
public sealed class VCSResizeHandle : Grid
{
    public VCSResizeHandle(Func<double> width, Action<double> setWidth, double min, double max)
    {
        Children.Add(new PPDivider(vertical: true));
        var hit = new Border { Width = 7, Margin = new Thickness(-3, 0, -3, 0), Background = Brushes.Transparent, Cursor = Cursors.SizeWE };
        Children.Add(hit);
        ClipToBounds = false;
        Panel.SetZIndex(this, 10);
        double? startX = null, startW = null;
        hit.MouseLeftButtonDown += (_, e) =>
        {
            startX = e.GetPosition(Parent as IInputElement ?? this).X;
            startW = width();
            hit.CaptureMouse();
            e.Handled = true;
        };
        hit.MouseMove += (_, e) =>
        {
            if (startX == null || !hit.IsMouseCaptured) return;
            var dx = e.GetPosition(Parent as IInputElement ?? this).X - startX.Value;
            setWidth(Math.Min(Math.Max(startW!.Value - dx, min), max));
        };
        hit.MouseLeftButtonUp += (_, _) =>
        {
            startX = null;
            hit.ReleaseMouseCapture();
        };
    }
}

/// <summary>右侧：选中提交的详情或空状态</summary>
internal sealed class VCSLogDetailHost : PPView
{
    private readonly VCSRepo _repo;
    private readonly VCSLogModel _log;

    public VCSLogDetailHost(VCSRepo repo)
    {
        _repo = repo;
        _log = repo.Log;
        Observe(_log, nameof(VCSLogModel.Primary));
    }

    protected override UIElement Build()
    {
        if (_log.Primary is { } h) return Keep("detail:" + h, () => new VCSCommitDetailView(_repo, h, _log.Focus));
        return new PPEmptyState("git-commit-horizontal", L.T("vcs.log.selectCommit"));
    }
}

// MARK: - 提交详情

/// <summary>提交详情：信息、引用、作者 / 日期 / 哈希 / 父提交 / 所在分支、文件列表。对应 Mac VCSCommitDetailView。</summary>
public sealed class VCSCommitDetailView : PPView
{
    private readonly VCSRepo _repo;
    private readonly string _hash;
    private readonly Action<string>? _onSelectParent;
    private readonly State<VCSCommitDetail?> _detail;
    private readonly State<string?> _error;
    private readonly State<int> _parent;
    private readonly State<VCSContainingBranches?> _containing;
    private bool _loadingContaining;
    private int _loadedParent = -1;

    public VCSCommitDetailView(VCSRepo repo, string hash, Action<string>? onSelectParent = null)
    {
        _repo = repo;
        _hash = hash;
        _onSelectParent = onSelectParent;
        _detail = UseState<VCSCommitDetail?>(null);
        _error = UseState<string?>(null);
        _parent = UseState(1);
        _containing = UseState<VCSContainingBranches?>(null);
    }

    protected override void OnAppear() => Load();

    private async void Load()
    {
        if (_loadedParent == _parent.Value) return;
        var parent = _parent.Value;
        _loadedParent = parent;
        try
        {
            var d = await _repo.Api().VcsShowAsync(_repo.ProjectId, _hash, parent > 1 ? parent : null, _repo.Root);
            if (parent != _parent.Value) return;
            _detail.Value = d;
            _error.Value = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _error.Value = e.Message;
        }
        if (_containing.Value == null && !_loadingContaining)
        {
            _loadingContaining = true;
            _containing.Value = await VCSAsync.Try(() => _repo.Api().VcsContainingBranchesAsync(_repo.ProjectId, _hash, _repo.Root));
            _loadingContaining = false;
        }
    }

    protected override UIElement Build()
    {
        if (_loadedParent != _parent.Value && IsAppeared) Dispatcher.BeginInvoke(new Action(Load));
        if (_detail.Value is { } d) return Content(d);
        if (_error.Value is { } e) return new PPAlert("danger", L.T("vcs.log.detailFailed"), e).Margins(M.Md).Top();
        return new PPSpinner().Center();
    }

    private UIElement Content(VCSCommitDetail d)
    {
        var c = d.Commit;
        var message = VCSViews.FullMessage(d);
        var nl = message.IndexOf('\n');
        var subject = nl < 0 ? message : message.Substring(0, nl);
        var body = nl < 0 ? "" : message.Substring(nl + 1).Trim();
        var col = VStack(M.Md,
            Selectable(subject, Typo.Title),
            body.Length > 0 ? Selectable(body, Typo.Body, ColorToken.TextSecondary) : null,
            c.Refs.Count > 0 ? Flow(M.Xs, c.Refs.Select(r => VCSViews.RefBadge(r))) : null,
            new PPDivider(subtle: true),
            Meta(d),
            new PPDivider(subtle: true),
            Files(d));
        col.Align = CrossAlign.Stretch;
        col.Padding = new Thickness(M.Lg);
        return Scroll(col).Key("vcs.detail.scroll");
    }

    private UIElement Meta(VCSCommitDetail d)
    {
        var c = d.Commit;
        var col = VStack(M.Sm,
            VCSViews.MetaRow(L.T("vcs.log.authorColumn"), Selectable(c.Author + (c.Email != null ? $" <{c.Email}>" : ""), Typo.Small).MaxH(Typo.Small.LineHeight * 2 + 2)),
            VCSViews.MetaRow(L.T("vcs.log.date"), Text(VCSDate.Full(c.ParsedDate), Typo.Small)));
        if (d.Committer is { } cm && (cm != c.Author || d.CommitterDate != c.Date))
            col.Children.Add(VCSViews.MetaRow(L.T("vcs.log.committer"), Text(cm + "  " + VCSDate.Full(d.CommitterDate), Typo.Small, ColorToken.TextSecondary).Lines(2)));
        col.Children.Add(VCSViews.MetaRow(L.T("vcs.log.hash"), HStack(M.Xs,
            Selectable(c.Hash, Typo.MonoSmall, wrap: false),
            new PPIconButton("copy", L.T("vcs.copyHash"), () => Actions.Copy(c.Hash), size: 20, iconSize: 12))));
        if (c.Parents.Count > 0)
            col.Children.Add(VCSViews.MetaRow(L.T("vcs.log.parents"), HStack(M.Sm,
                c.Parents.Select(p => (UIElement)new PPLink(p.Length > 8 ? p.Substring(0, 8) : p, () => _onSelectParent?.Invoke(p), mono: true)))));
        UIElement contained;
        if (_containing.Value is { } cb)
        {
            if (cb.Refs.Count == 0) contained = Text(L.T("vcs.log.noBranches"), Typo.Small, ColorToken.TextTertiary);
            else
                contained = Flow(M.Xs, cb.Refs.Take(30).Select(r => (UIElement)VCSViews.RefBadge(r)),
                    cb.Refs.Count > 30 || cb.Truncated ? Text("…", Typo.Small, ColorToken.TextTertiary) : null);
        }
        else contained = new PPSpinner(12).Left();
        col.Children.Add(VCSViews.MetaRow(L.T("vcs.log.containedIn"), contained));
        col.Align = CrossAlign.Stretch;
        return col;
    }

    private UIElement Files(VCSCommitDetail d)
    {
        var col = VStack(2);
        col.Align = CrossAlign.Stretch;
        var head = HStack(M.Sm, Text(L.T("vcs.log.files", d.Files.Count), Typo.Label, ColorToken.TextSecondary), Spacer(),
            d.Commit.Parents.Count > 1
                ? new PPSegmented<int>(Enumerable.Range(0, d.Commit.Parents.Count).Select(i => (i + 1, L.T("vcs.log.parentN", i + 1))), _parent, PPButtonSize.Small)
                : null);
        col.Children.Add(head);
        int parent = _parent.Value;
        foreach (var f in d.Files)
        {
            var line = new VCSFileLine(f, false, () => VCSDiffDialog.PresentCommit(_repo, d, f, parent));
            line.PPContextMenu(() => new[]
            {
                MenuEntry.Item(L.T("vcs.showDiff"), () => VCSDiffDialog.PresentCommit(_repo, d, f, parent), icon: "file-diff"),
                MenuEntry.Item(L.T("vcs.fileHistory"), () => VCSFileHistoryDialog.Present(_repo, f.Path), icon: "history"),
                MenuEntry.Item(L.T("vcs.checkoutFileVersion"), () => VCSHistoryActions.RestoreFile(_repo, f.Path, d.Commit.Hash, d.Commit.Short), icon: "rotate-ccw"),
                MenuEntry.Divider,
                MenuEntry.Item(L.T("vcs.copyPath"), () => Actions.Copy(f.Path), icon: "copy"),
            });
            col.Children.Add(line);
        }
        return col;
    }
}

/// <summary>文件行（提交详情、储藏、比较里用）：状态字母、文件名、目录、增删行数。对应 Mac VCSFileLine。</summary>
public sealed class VCSFileLine : Border
{
    private readonly bool _selected;

    public VCSFileLine(VCSFileChange file, bool selected, Action action)
    {
        _selected = selected;
        var row = HStack(M.Sm,
            VCSViews.StatusLetter(file.Status),
            Text(VCSPath.Name(file.Path), Typo.Body, VCSStyle.Color(file.Status)),
            TextMiddle(file.OldPath != null ? "← " + file.OldPath : VCSPath.Dir(file.Path), Typo.Small, ColorToken.TextTertiary),
            Spacer(M.Xs),
            file.Binary ? new PPIcon("binary", 12, ColorToken.TextTertiary) : VCSViews.LineStats(file.Additions, file.Deletions));
        row.VerticalAlignment = VerticalAlignment.Center;
        Child = row;
        Padding = new Thickness(M.Sm, 0, M.Sm, 0);
        Height = M.RowHeightCompact;
        CornerRadius = new CornerRadius(M.RadiusSM);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        SnapsToDevicePixels = true;
        MouseEnter += (_, _) => Update();
        MouseLeave += (_, _) => Update();
        this.OnClick(action);
        this.Tip(file.Path).Id("vcs.fileLine." + file.Path);
        Update();
    }

    private void Update() => this.Bg(_selected ? ColorToken.BgSelected : IsMouseOver ? ColorToken.BgHover : ColorToken.Clear);
}

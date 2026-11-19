using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>工程详情「文件」标签页（对应 Mac FilesTab）：40px 工具栏 + 按需展开的文件树 / 文件名搜索结果。</summary>
public sealed class FilesTab : PPView
{
    private readonly Project _project;
    private readonly FileTreeModel _tree;

    public FilesTab(Project project)
    {
        _project = project;
        _tree = new FileTreeModel(project);
    }

    /// <summary>调试桥用。</summary>
    internal FileTreeModel Tree => _tree;

    protected override void OnAppear()
    {
        _tree.Attach();
        _ = _tree.Start();
    }

    protected override void OnDisappear() => _tree.Detach();

    protected override UIElement Build()
    {
        var col = VStack(0,
            Keep("toolbar", () => new FilesToolbar(_project, _tree)),
            new PPDivider(subtle: true),
            Keep("content", () => new FilesContent(_project, _tree)).Flex());
        col.Align = CrossAlign.Stretch;
        return col.Id("files.tab");
    }
}

/// <summary>
/// 工具栏：搜索框（宽 ≤ 320）、新建文件、新建文件夹、全部折叠、刷新、打开编辑窗口。
/// 工具栏本身不订阅任何状态（输入时不重建，输入法组字不被打断）；搜索中的转圈是单独的小视图。
/// </summary>
internal sealed class FilesToolbar : PPView
{
    private readonly Project _project;
    private readonly FileTreeModel _tree;

    public FilesToolbar(Project project, FileTreeModel tree)
    {
        _project = project;
        _tree = tree;
    }

    protected override UIElement Build()
    {
        // 按语言区分键：切换语言后换成新文案的搜索框（同一语言下跨重建复用，不打断输入）
        var field = Keep("filter:" + L10n.Shared.Locale, () => new PPTextField(L.T("files.filter"), Bind.Of(() => _tree.Query, v => _tree.Query = v),
            icon: "search", clearable: true, height: M.ControlHeightSmall + 2).Id("files.filter"));
        field.MaxWidth = 320;
        field.HorizontalAlignment = HorizontalAlignment.Left;
        // 和 SwiftUI 一样：搜索框与弹性空白平分剩余宽度，搜索框最多 320
        var fieldSlot = new Border { Child = field }.Flex();
        var row = HStack(M.Xs,
            fieldSlot,
            new SearchingSpinner(_tree),
            Spacer(M.Md),
            new PPIconButton("file-plus", L.T("files.newFile"), () => FileActions.NewItem(_project.Id, _tree.SelectedDir, false, _tree)).Id("files.newFile"),
            new PPIconButton("folder-plus", L.T("files.newFolder"), () => FileActions.NewItem(_project.Id, _tree.SelectedDir, true, _tree)).Id("files.newFolder"),
            new PPIconButton("chevrons-down-up", L.T("files.collapseAll"), _tree.CollapseAll).Id("files.collapseAll"),
            new PPIconButton("refresh-cw", L.T("files.refresh"), () => _ = _tree.Refresh()).Id("files.refresh"),
            new PPButton(L.T("editorWindow.open"), () => EditorWindows.Shared.Open(_project.Id, SelectedFile), icon: "file-pen", size: PPButtonSize.Small)
                .Margins(M.Xs, 0, 0, 0).Id("files.openEditor"));
        row.Margin = new Thickness(M.Lg, 0, M.Lg, 0);
        return new Border { Child = row, Height = M.ToolbarHeight };
    }

    /// <summary>选中的是文件时随编辑窗口一起打开。</summary>
    private string? SelectedFile
    {
        get
        {
            if (_tree.Selection is not { } s) return null;
            var e = _tree.Rows.FirstOrDefault(r => r.Entry.Path == s)?.Entry;
            if (e != null) return e.Dir ? null : e.Path;
            return _tree.Results?.Any(h => h.Path == s) == true ? s : null;
        }
    }
}

/// <summary>按文件名搜索进行中时的转圈（不搜索时不占位）。</summary>
internal sealed class SearchingSpinner : PPView
{
    private readonly FileTreeModel _tree;

    public SearchingSpinner(FileTreeModel tree)
    {
        _tree = tree;
        Observe(tree, nameof(FileTreeModel.Searching));
    }

    protected override UIElement Build()
    {
        Visibility = _tree.Searching ? Visibility.Visible : Visibility.Collapsed;
        return new PPSpinner(M.IconSmall).Margins(M.Xs, 0, 0, 0);
    }
}

/// <summary>
/// 树 / 搜索结果 / 空状态。树和结果都虚拟化（只为看得见的行建元素）。
/// 列表实例常驻在固定的容器里（不经 Keep 摘下再挂回）：选中、展开这类变化只增删 / 重绑行，滚动位置不动。
/// </summary>
internal sealed class FilesContent : PPView
{
    private readonly Project _project;
    private readonly FileTreeModel _tree;
    private readonly Border _host = new();
    private VirtualRows<FileTreeModel.Row>? _treeList;
    private VirtualRows<FSSearchHit>? _resultList;

    public FilesContent(Project project, FileTreeModel tree)
    {
        _project = project;
        _tree = tree;
        Observe(tree, nameof(FileTreeModel.Children), nameof(FileTreeModel.Expanded), nameof(FileTreeModel.Selection),
            nameof(FileTreeModel.Git), nameof(FileTreeModel.Results), nameof(FileTreeModel.Searching));
        Observe(AppModel.Shared.Data, nameof(DataStore.Contributions));
    }

    protected override UIElement Build()
    {
        var el = Current();
        if (!ReferenceEquals(_host.Child, el)) _host.Child = el;
        return _host;
    }

    private UIElement Current()
    {
        if (_tree.Results is { } results)
        {
            _treeList = null;
            if (results.Count == 0 && !_tree.Searching)
            {
                _resultList = null;
                return new PPEmptyState("file-search", L.T("files.noMatch"));
            }
            _resultList ??= new VirtualRows<FSSearchHit>(() => new SearchHitRowView(_project, _tree),
                (el, hit) => ((SearchHitRowView)el).Bind(hit), h => h.Path, new Thickness(M.Sm)).Id("files.results");
            _resultList.SetItems(results);
            _resultList.Refresh();
            return _resultList;
        }
        _resultList = null;
        var rootErr = _tree.Error("");
        if (rootErr != null && !_tree.Children.ContainsKey(""))
        {
            _treeList = null;
            return new PPEmptyState("circle-alert", L.T("files.loadFailed") + "\n" + rootErr, L.T("connection.retry"), () => _ = _tree.Reload(""));
        }
        if (!_tree.Children.TryGetValue("", out var top))
        {
            _treeList = null;
            return new PPSpinner();
        }
        if (top.Count == 0)
        {
            _treeList = null;
            return new PPEmptyState("folder-open", L.T("files.empty"), L.T("files.newFile"),
                () => FileActions.NewItem(_project.Id, "", false, _tree), "file-plus");
        }
        var rows = _tree.Rows;
        if (_tree.IsTruncated("")) rows.Add(new FileTreeModel.Row(new FSEntry(), 0, Footer: true));
        _treeList ??= new VirtualRows<FileTreeModel.Row>(() => new FileTreeRowView(_project, _tree),
            (el, r) => ((FileTreeRowView)el).Bind(r), r => r.Id, new Thickness(M.Sm)).Id("files.tree");
        _treeList.SetItems(rows);
        _treeList.Refresh();
        return _treeList;
    }
}

/// <summary>树的一行：缩进 16/级、展开箭头、图标、名字（Git 着色、中间截断）、符号链接、加载中、行尾 Git 字母。高 24。</summary>
internal sealed class FileTreeRowView : Border
{
    private readonly Project _project;
    private readonly FileTreeModel _tree;
    private FileTreeModel.Row? _row;

    public FileTreeRowView(Project project, FileTreeModel tree)
    {
        _project = project;
        _tree = tree;
        Height = M.RowHeightCompact;
        CornerRadius = new CornerRadius(M.RadiusSM);
        Padding = new Thickness(M.Sm, 0, M.Sm, 0);
        SnapsToDevicePixels = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        this.Bg(ColorToken.Clear);
        MouseEnter += (_, _) => UpdateBackground();
        MouseLeave += (_, _) => UpdateBackground();
        MouseLeftButtonDown += OnMouseDown;
        this.PPContextMenu(() =>
        {
            if (_row == null || _row.Footer) return Array.Empty<MenuEntry>();
            _tree.Selection = _row.Entry.Path;
            return FileActions.Menu(_project.Id, _project.Path, _row.Entry, _tree);
        });
    }

    public void Bind(FileTreeModel.Row row)
    {
        _row = row;
        if (row.Footer)
        {
            Child = Text(L.T("files.truncated"), Typo.Small, ColorToken.TextTertiary).Margins(M.Md - M.Sm, 0, 0, 0);
            Height = M.RowHeightCompact + M.Md;
            ToolTip = null;
            UpdateBackground();
            return;
        }
        Height = M.RowHeightCompact;
        var e = row.Entry;
        bool open = _tree.IsExpanded(e.Path);
        var git = _tree.GitStatus(e.Path);
        UIElement chevron;
        if (e.Dir)
        {
            var c = Box(new PPIcon(open ? "chevron-down" : "chevron-right", M.IconSmall, ColorToken.IconSecondary).Center())
                .Size(M.Icon, M.Icon).Bg(ColorToken.Clear);
            void Toggle()
            {
                _tree.Selection = e.Path;
                _tree.Toggle(e);
            }
            c.MouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                if (ev.ClickCount == 1) Toggle();
            };
            c.SetValue(UI.ClickActionProperty, (Action)Toggle); // 调试桥按 ID 点击
            chevron = c.Id("files.chevron:" + e.Path);
        }
        else chevron = Gap(M.Icon);
        var name = TextMiddle(e.Name, Typo.Body, NameColor(e, git));
        var content = HStack(M.Xs,
            new FrameworkElement { Width = row.Depth * 16, IsHitTestVisible = false },
            chevron,
            FileIconView.Make(e, open, AppModel.Shared.Data),
            name,
            e.Symlink ? new PPIcon("folder-symlink", 12, ColorToken.IconSecondary).Tip(L.T("files.symlink")) : null,
            _tree.IsLoading(e.Path) ? new PPSpinner(12) : null,
            Spacer(M.Sm),
            git != null ? Text(git, Typo.MonoSmall, FileGitStyle.Color(git)).Shrink(false).Tip(FileGitStyle.Title(git)) : null);
        content.Opacity = e.Ignored ? 0.5 : e.Hidden ? 0.75 : 1;
        Child = content;
        this.Id("files.row:" + e.Path);
        SetValue(UI.ClickActionProperty, (Action)(() => _tree.Selection = e.Path)); // 调试桥按 ID 点击 = 单击选中
        ToolTip = e.Ignored ? L.T("files.ignored") : null;
        UpdateBackground();
    }

    private string NameColor(FSEntry e, string? git)
    {
        if (git != null) return FileGitStyle.Color(git);
        if (e.Dir && _tree.IsChangedDir(e.Path)) return ColorToken.Info;
        return ColorToken.TextPrimary;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs ev)
    {
        if (_row == null || _row.Footer) return;
        var e = _row.Entry;
        _tree.Selection = e.Path;
        if (ev.ClickCount == 2)
        {
            if (e.Dir) _tree.Toggle(e);
            else FileActions.Edit(_project.Id, e.Path);
        }
        ev.Handled = true;
    }

    public void UpdateBackground()
    {
        bool selected = _row != null && !_row.Footer && _tree.Selection == _row.Entry.Path;
        bool hover = _row != null && !_row.Footer && IsMouseOver;
        this.Bg(selected ? ColorToken.BgSelected : hover ? ColorToken.BgHover : ColorToken.Clear);
    }
}

/// <summary>搜索结果的一行：图标、文件名（Git 着色）、所在目录（小字、开头截断）。</summary>
internal sealed class SearchHitRowView : Border
{
    private readonly Project _project;
    private readonly FileTreeModel _tree;
    private FSSearchHit? _hit;

    public SearchHitRowView(Project project, FileTreeModel tree)
    {
        _project = project;
        _tree = tree;
        Height = M.RowHeightCompact;
        CornerRadius = new CornerRadius(M.RadiusSM);
        Padding = new Thickness(M.Sm, 0, M.Sm, 0);
        SnapsToDevicePixels = true;
        this.Bg(ColorToken.Clear);
        MouseEnter += (_, _) => UpdateBackground();
        MouseLeave += (_, _) => UpdateBackground();
        MouseLeftButtonDown += (_, ev) =>
        {
            if (_hit == null) return;
            _tree.Selection = _hit.Path;
            if (ev.ClickCount == 2) FileActions.Edit(_project.Id, _hit.Path);
            ev.Handled = true;
        };
        this.PPContextMenu(() =>
        {
            if (_hit == null) return Array.Empty<MenuEntry>();
            _tree.Selection = _hit.Path;
            return FileActions.Menu(_project.Id, _project.Path, Entry(_hit), _tree);
        });
    }

    private static FSEntry Entry(FSSearchHit hit) => new() { Name = hit.Name, Path = hit.Path, Dir = false };

    public void Bind(FSSearchHit hit)
    {
        _hit = hit;
        this.Id("files.hit:" + hit.Path);
        SetValue(UI.ClickActionProperty, (Action)(() => _tree.Selection = hit.Path));
        var git = _tree.GitStatus(hit.Path);
        Child = HStack(M.Sm,
            FileIconView.Make(Entry(hit), false, AppModel.Shared.Data),
            Text(hit.Name, Typo.Body, git != null ? FileGitStyle.Color(git) : ColorToken.TextPrimary),
            new HeadTrimText(hit.Directory, Typo.Small, ColorToken.TextTertiary),
            Spacer());
        UpdateBackground();
    }

    private void UpdateBackground() =>
        this.Bg(_hit != null && _tree.Selection == _hit.Path ? ColorToken.BgSelected : IsMouseOver ? ColorToken.BgHover : ColorToken.Clear);
}

/// <summary>
/// 文件图标（对应 Mac FileIconView）：有语言贡献且该语言对应某个技术徽标时用它的图标，否则按扩展名用通用 SVG。
/// 启动页的「添加脚本或程序」列表也用它。
/// </summary>
public sealed class FileIconView : PPView
{
    private readonly FSEntry _entry;
    private readonly bool _expanded;

    public FileIconView(FSEntry entry, bool expanded)
    {
        _entry = entry;
        _expanded = expanded;
        Observe(AppModel.Shared.Data, nameof(DataStore.Contributions));
    }

    protected override UIElement Build() => Make(_entry, _expanded, AppModel.Shared.Data);

    /// <summary>不订阅变化的一次性图标（虚拟化列表的行重绑时用）。</summary>
    public static PPIcon Make(FSEntry entry, bool expanded, DataStore data)
    {
        var c = data.Contributions;
        if (!entry.Dir && LanguageResolver.Language(entry.Name, c.Languages) is { } lang && c.Facet(lang.Id) is { Icon: { Length: > 0 } icon } facet)
            return PPIcon.Raw(icon, facet.Plugin, FileIcon.BuiltinName(entry), M.Icon);
        return new PPIcon(FileIcon.BuiltinName(entry, expanded), M.Icon, entry.Dir ? ColorToken.IconSecondary : ColorToken.IconPrimary);
    }
}

/// <summary>Git 状态的颜色与说明（与 IDEA 一致：修改蓝、新增绿、未跟踪橙、冲突红、删除灰）。</summary>
public static class FileGitStyle
{
    public static string Color(string s) => s switch
    {
        "M" or "R" or "C" or "T" => ColorToken.Info,
        "A" => ColorToken.Success,
        "?" => ColorToken.Warning,
        "U" => ColorToken.Danger,
        "D" => ColorToken.TextTertiary,
        _ => ColorToken.TextPrimary,
    };

    public static string Title(string s) => s switch
    {
        "M" or "T" => L.T("files.git.modified"),
        "A" => L.T("files.git.added"),
        "?" => L.T("files.git.untracked"),
        "U" => L.T("files.git.conflict"),
        "D" => L.T("files.git.deleted"),
        "R" or "C" => L.T("files.git.renamed"),
        _ => s,
    };
}

/// <summary>
/// 虚拟化的行列表（对应 SwiftUI 的 ScrollView + LazyVStack）：只为看得见的行建元素，滚动时复用（Recycling）。
/// make 建一个行元素，bind 把数据填进去；SetItems 按键做最小增删（展开 / 折叠时滚动位置不跳）。
/// </summary>
public sealed class VirtualRows<T> : ItemsControl where T : class
{
    private readonly Func<FrameworkElement> _make;
    private readonly Action<FrameworkElement, T> _bind;
    private readonly Func<T, string> _key;
    private ObservableCollection<T> _items = new();

    public VirtualRows(Func<FrameworkElement> make, Action<FrameworkElement, T> bind, Func<T, string> key, Thickness padding)
    {
        _make = make;
        _bind = bind;
        _key = key;
        Focusable = false;
        ItemsSource = _items;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(this, true);
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer));
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        sv.SetValue(ScrollViewer.CanContentScrollProperty, true);
        sv.SetValue(FocusableProperty, false);
        var ip = new FrameworkElementFactory(typeof(ItemsPresenter));
        ip.SetValue(MarginProperty, padding);
        sv.AppendChild(ip);
        Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = sv };
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => false;

    protected override DependencyObject GetContainerForItemOverride()
    {
        var el = _make();
        el.HorizontalAlignment = HorizontalAlignment.Stretch;
        return el;
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        var el = (FrameworkElement)element;
        el.Tag = item;
        _bind(el, (T)item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item) => ((FrameworkElement)element).Tag = null;

    /// <summary>换成新的行：首尾相同的保留，只增删中间变了的部分；变化很大时整体替换。</summary>
    public void SetItems(IReadOnlyList<T> next)
    {
        var old = _items;
        int prefix = 0;
        while (prefix < old.Count && prefix < next.Count && _key(old[prefix]) == _key(next[prefix])) prefix++;
        int suffix = 0;
        while (suffix < old.Count - prefix && suffix < next.Count - prefix
               && _key(old[old.Count - 1 - suffix]) == _key(next[next.Count - 1 - suffix])) suffix++;
        int removeCount = old.Count - prefix - suffix, insertCount = next.Count - prefix - suffix;
        if (removeCount + insertCount > 400)
        {
            _items = new ObservableCollection<T>(next);
            ItemsSource = _items;
            return;
        }
        for (int i = 0; i < removeCount; i++) old.RemoveAt(prefix);
        for (int i = 0; i < insertCount; i++) old.Insert(prefix + i, next[prefix + i]);
        // 键相同但内容变了（条目信息更新）：替换这一项（看得见的行会重新生成）
        for (int i = 0; i < old.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(old[i], next[i])) old[i] = next[i];
    }

    /// <summary>按当前数据重绑看得见的行（选中、Git 状态、加载中这些只影响外观的变化）。</summary>
    public void Refresh()
    {
        var panel = PPView.Descendants<VirtualizingStackPanel>(this).FirstOrDefault();
        if (panel == null) return;
        foreach (UIElement c in panel.Children)
            if (c is FrameworkElement el && el.Tag is T item) _bind(el, item);
    }
}

/// <summary>开头截断的单行文字（所在目录、终端工作目录：放不下时保留末尾，前面用「…」）。对应 SwiftUI 的 .truncationMode(.head)。</summary>
public sealed class HeadTrimText : Decorator
{
    private readonly string _full;
    private readonly TextBlock _inner = new() { TextWrapping = TextWrapping.NoWrap };

    public HeadTrimText(string text, Typo font, string color)
    {
        _full = text ?? "";
        font.Apply(_inner);
        _inner.Text = _full;
        Child = _inner;
        this.Fg(color);
        Stack.SetShrink(this, true);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var text = _full;
        double w = constraint.Width;
        if (!double.IsInfinity(w) && _full.Length > 1 && !Fits(_full, w))
        {
            int lo = 0, hi = _full.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Fits("…" + _full.Substring(_full.Length - mid), w)) lo = mid;
                else hi = mid - 1;
            }
            text = "…" + _full.Substring(_full.Length - lo);
        }
        if (_inner.Text != text) _inner.Text = text;
        ToolTip = text == _full ? null : _full;
        return base.MeasureOverride(constraint);
    }

    private bool Fits(string s, double width)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection, new Typeface(_inner.FontFamily, _inner.FontStyle, _inner.FontWeight, _inner.FontStretch),
            _inner.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(this), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace <= width + 0.01;
    }
}

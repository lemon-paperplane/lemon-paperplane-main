using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>「提交」子页：左侧变更列表 + 提交信息，右侧差异。对应 Mac VCSChangesPage。</summary>
public sealed class VCSChangesPage : PPView
{
    private readonly VCSRepo _repo;

    public VCSChangesPage(VCSRepo repo) { _repo = repo; }

    protected override UIElement Build()
    {
        var changes = _repo.Changes;
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.Children.Add(new VCSChangesToolbar(_repo).At(0, 0));
        left.Children.Add(new PPDivider(subtle: true).At(1, 0));
        left.Children.Add(new VCSChangeList(_repo).At(2, 0));
        left.Children.Add(new PPDivider().At(3, 0));
        left.Children.Add(new VCSCommitPanel(_repo).At(4, 0));
        var right = new VCSDiffView(changes.Diff, changes, () => changes.HunkMode);
        return new PPSplitView(left, right, UIState.Bind("vcs.changes.left", 380.0), 260, 680);
    }
}

// MARK: - 列表工具栏

internal sealed class VCSChangesToolbar : PPView
{
    private readonly VCSRepo _repo;
    private VCSChangesModel Changes => _repo.Changes;

    public VCSChangesToolbar(VCSRepo repo)
    {
        _repo = repo;
        Observe(repo, nameof(VCSRepo.Status));
        Observe(repo.Changes, nameof(VCSChangesModel.Selection), nameof(VCSChangesModel.Multi), nameof(VCSChangesModel.GroupByDir), nameof(VCSChangesModel.StagingArea));
    }

    protected override UIElement Build()
    {
        var c = Changes;
        PPIconButton B(string icon, string help, Action a, bool active = false) =>
            new(icon, help, a, size: M.IconButtonSmall, iconSize: M.IconSmall, active: active);
        var row = HStack(M.Xxs,
            B("refresh-cw", L.T("vcs.refresh"), async () => { await _repo.Refresh(); c.Diff.Reload(); }).Id("vcs.changes.refresh"),
            B("undo-2", L.T("vcs.discard.selected"), DiscardSelected).Disabled(c.Selection == null && c.Multi.Count == 0),
            B("archive", L.T("vcs.stash.new"), () => VCSStashDialogs.PresentNew(_repo, null)).Disabled(_repo.Status?.Files.Count is null or 0),
            new PPDivider(vertical: true).H(14).Margins(M.Xs, 0, M.Xs, 0),
            B("folder-tree", L.T("vcs.changes.groupByDir"), () => c.GroupByDir = !c.GroupByDir, active: c.GroupByDir).Id("vcs.changes.groupByDir"),
            B("chevrons-down-up", L.T("vcs.changes.collapseAll"), CollapseAll).Disabled(!c.GroupByDir),
            B("chevrons-up-down", L.T("vcs.changes.expandAll"), () => c.Collapsed = new HashSet<string>()).Disabled(!c.GroupByDir),
            B("layers", L.T("vcs.changes.stagingArea"), () => c.StagingArea = !c.StagingArea, active: c.StagingArea).Id("vcs.changes.stagingArea"),
            Spacer());
        row.Padding = new Thickness(M.Sm, 0, M.Sm, 0);
        row.Height = M.RowHeight + 4;
        return row;
    }

    private void CollapseAll()
    {
        if (_repo.Status is not { } s) return;
        var keys = new HashSet<string>();
        foreach (var f in s.Files)
        {
            var d = VCSPath.Dir(f.Path);
            while (d.Length > 0)
            {
                foreach (var g in new[] { "conflict", "changes", "untracked", "staged", "unstaged" }) keys.Add($"d:{g}:{d}");
                d = VCSPath.Dir(d);
            }
        }
        Changes.Collapsed = keys;
    }

    /// <summary>丢弃列表里选中的文件（多选或当前选中，与 IDEA 的 Rollback 一致；不按勾选，避免误丢全部）</summary>
    private void DiscardSelected()
    {
        if (_repo.Status is not { } s) return;
        var c = Changes;
        var sels = c.Multi.Count == 0 ? (c.Selection is { } one ? new List<VCSChangeSelection> { one } : new List<VCSChangeSelection>()) : c.Multi.ToList();
        var set = new HashSet<string>(sels.Select(x => x.Path));
        var files = s.Files.Where(f => set.Contains(f.Path) && !f.Conflicted).ToList();
        if (files.Count == 0) return;
        var onlyUnstaged = c.StagingArea && sels.All(x => x.Side == VCSChangeSide.Unstaged);
        VCSDiscardDialog.Present(_repo, files, onlyUnstaged ? VCSDiffMode.Unstaged : null);
    }
}

// MARK: - 变更列表

/// <summary>列表的一行。对应 Mac VCSChangeRowItem。</summary>
internal sealed class VCSChangeRowItem
{
    public enum RowKind { Group, Dir, File, Truncated }

    public string Id = "";
    public RowKind Kind;
    public string Key = "";
    public string Title = "";
    public List<VCSFileChange> Files = new();
    public string Side = "";
    public VCSFileChange? File;
    public int Count;
    public int Depth;
}

internal static class VCSChangeRows
{
    public static List<VCSChangeRowItem> Build(VCSStatus s, VCSChangesModel changes)
    {
        var g = changes.GroupsOf(s.Files);
        var rows = new List<VCSChangeRowItem>();
        void Add(string key, string title, List<VCSFileChange> files, string side)
        {
            if (files.Count == 0) return;
            rows.Add(new VCSChangeRowItem { Id = "g:" + key, Kind = VCSChangeRowItem.RowKind.Group, Key = key, Title = title, Files = files, Side = side });
            if (changes.Collapsed.Contains("g:" + key)) return;
            if (changes.GroupByDir)
                rows.AddRange(Tree(files, key, side, changes.Collapsed));
            else
                rows.AddRange(files.OrderBy(f => f.Path, VCSNaturalComparer.Shared)
                    .Select(f => new VCSChangeRowItem { Id = $"f:{key}:{f.Path}", Kind = VCSChangeRowItem.RowKind.File, File = f, Side = side, Depth = 1 }));
        }
        Add("conflict", L.T("vcs.changes.conflicts"), g.Conflicts, VCSChangeSide.Conflict);
        if (changes.StagingArea)
        {
            Add("staged", L.T("vcs.changes.staged"), g.Staged, VCSChangeSide.Staged);
            Add("unstaged", L.T("vcs.changes.unstaged"), g.Unstaged, VCSChangeSide.Unstaged);
        }
        else
        {
            Add("changes", L.T("vcs.changes.changes"), g.Tracked, VCSChangeSide.Head);
        }
        Add("untracked", L.T("vcs.changes.untracked"), g.Untracked, VCSChangeSide.Untracked);
        if (s.UntrackedTruncated && !changes.Collapsed.Contains("g:untracked"))
            rows.Add(new VCSChangeRowItem { Id = "trunc", Kind = VCSChangeRowItem.RowKind.Truncated, Count = s.UntrackedCount, Depth = 1 });
        return rows;
    }

    private sealed class Node
    {
        public readonly Dictionary<string, Node> Dirs = new();
        public readonly List<VCSFileChange> Files = new();
        public readonly List<VCSFileChange> All = new();
    }

    public static List<VCSChangeRowItem> Tree(List<VCSFileChange> files, string group, string side, HashSet<string> collapsed)
    {
        var root = new Node();
        foreach (var f in files)
        {
            var node = root;
            node.All.Add(f);
            var dir = VCSPath.Dir(f.Path);
            if (dir.Length > 0)
            {
                foreach (var comp in dir.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!node.Dirs.TryGetValue(comp, out var next)) node.Dirs[comp] = next = new Node();
                    node = next;
                    node.All.Add(f);
                }
            }
            node.Files.Add(f);
        }
        var output = new List<VCSChangeRowItem>();
        void Walk(Node n, string prefix, int depth)
        {
            foreach (var name in n.Dirs.Keys.OrderBy(k => k, VCSNaturalComparer.Shared))
            {
                var child = n.Dirs[name];
                var label = name;
                var path = prefix.Length == 0 ? name : prefix + "/" + name;
                // 只有一个子目录且没有文件的目录链压缩成一行
                while (child.Files.Count == 0 && child.Dirs.Count == 1)
                {
                    var only = child.Dirs.First();
                    label += "/" + only.Key;
                    path += "/" + only.Key;
                    child = only.Value;
                }
                var key = $"d:{group}:{path}";
                output.Add(new VCSChangeRowItem { Id = key, Kind = VCSChangeRowItem.RowKind.Dir, Key = key, Title = label, Files = child.All, Side = side, Depth = depth });
                if (!collapsed.Contains(key)) Walk(child, path, depth + 1);
            }
            foreach (var f in n.Files.OrderBy(f => VCSPath.Name(f.Path), VCSNaturalComparer.Shared))
                output.Add(new VCSChangeRowItem { Id = $"f:{group}:{f.Path}", Kind = VCSChangeRowItem.RowKind.File, File = f, Side = side, Depth = depth });
        }
        Walk(root, "", 1);
        return output;
    }
}

/// <summary>变更列表（虚拟化：只为可见行建元素）。对应 Mac VCSChangeList。</summary>
internal sealed class VCSChangeList : PPView
{
    private readonly VCSRepo _repo;
    private readonly VCSVirtualList _list = new(new Thickness(M.Xs, M.Xs, M.Xs, M.Xs));
    private readonly ScrollViewer _scroll;

    public VCSChangeList(VCSRepo repo)
    {
        _repo = repo;
        _scroll = _list.InScroll();
        _scroll.Key("vcs.changes.list");
        Observe(repo, nameof(VCSRepo.Status), nameof(VCSRepo.StatusError));
        Observe(repo.Changes, nameof(VCSChangesModel.Collapsed), nameof(VCSChangesModel.GroupByDir), nameof(VCSChangesModel.StagingArea),
            nameof(VCSChangesModel.Selection), nameof(VCSChangesModel.Multi), VCSChangesModel.ChecksProperty);
    }

    private VCSChangesModel Changes => _repo.Changes;

    protected override UIElement Build()
    {
        if (_repo.Status is { } s)
        {
            if (s.Files.Count == 0) return new PPEmptyState("circle-check", L.T("vcs.changes.clean"));
            var items = VCSChangeRows.Build(s, Changes);
            _list.SetRows(items.Select(r => new VRow(r.Id, r.Kind == VCSChangeRowItem.RowKind.Group ? M.RowHeightCompact + M.Xs : M.RowHeightCompact, () => RowView(r))).ToList());
            UI.Detach(_scroll);
            return _scroll;
        }
        if (_repo.StatusError is { } e)
            return new PPAlert("danger", L.T("vcs.status.failed"), e, new PPButton(L.T("connection.retry"), async () => await _repo.LoadStatus(), size: PPButtonSize.Small))
                .Margins(M.Md).Top();
        return new PPSpinner().Center();
    }

    private FrameworkElement RowView(VCSChangeRowItem r) => r.Kind switch
    {
        VCSChangeRowItem.RowKind.Group => GroupRow(r),
        VCSChangeRowItem.RowKind.Dir => DirRow(r),
        VCSChangeRowItem.RowKind.File => new VCSChangeFileRow(_repo, r.File!, r.Side, r.Depth),
        _ => TruncatedRow(r),
    };

    private FrameworkElement TruncatedRow(VCSChangeRowItem r)
    {
        var row = HStack(M.Sm, new PPIcon("triangle-alert", M.IconSmall, ColorToken.Warning),
            Text(L.T("vcs.changes.untrackedTruncated", r.Count), Typo.Small, ColorToken.TextSecondary),
            new PPInfoButton(L.T("vcs.changes.untrackedTruncated.info")), Spacer());
        row.Padding = new Thickness(r.Depth * 14 + M.Sm, 0, 0, 0);
        row.Height = M.RowHeightCompact;
        return row;
    }

    private bool Checkable(string side) => !Changes.StagingArea && (side == VCSChangeSide.Head || side == VCSChangeSide.Untracked);

    private FrameworkElement GroupRow(VCSChangeRowItem r)
    {
        var c = Changes;
        var collapsedKey = "g:" + r.Key;
        var files = r.Files;
        var header = VCSViews.GroupHeader(r.Title, files.Count, c.Collapsed.Contains(collapsedKey), () => c.ToggleCollapsed(collapsedKey),
            Checkable(r.Side) ? new VCSTriCheckbox(c.GroupState(files), () => c.ToggleGroup(files)) : null);
        header.Id("vcs.changes.group." + r.Key);
        var row = HStack(M.Xs, header.Flex(), GroupActions(r.Side, files));
        row.Margin = new Thickness(0, M.Xs, M.Xs, 0);
        return row;
    }

    private UIElement? GroupActions(string side, List<VCSFileChange> files)
    {
        switch (side)
        {
            case VCSChangeSide.Conflict:
                return new PPLink(L.T("vcs.conflicts.resolve"), () => _repo.Session.Page = VCSPage.Conflicts);
            case VCSChangeSide.Staged:
                return new PPIconButton("minus", L.T("vcs.unstageAll"), () =>
                    _repo.Perform(VCSOp.Unstage, new VCSPathsArgs { Paths = files.Select(f => f.Path).ToList() }, L.T("vcs.unstage"), quietSuccess: true),
                    size: M.IconButtonSmall, iconSize: M.IconSmall);
            case VCSChangeSide.Unstaged:
            case VCSChangeSide.Untracked:
                if (!Changes.StagingArea) return null;
                return new PPIconButton("plus", L.T("vcs.stageAll"), () =>
                    _repo.Perform(VCSOp.Stage, new VCSPathsArgs { Paths = files.Select(f => f.Path).ToList() }, L.T("vcs.stage"), quietSuccess: true),
                    size: M.IconButtonSmall, iconSize: M.IconSmall);
            default:
                return null;
        }
    }

    private FrameworkElement DirRow(VCSChangeRowItem r)
    {
        var c = Changes;
        var collapsed = c.Collapsed.Contains(r.Key);
        var files = r.Files;
        var row = HStack(M.Xs,
            new PPIcon(collapsed ? "chevron-right" : "chevron-down", 12, ColorToken.IconSecondary),
            Checkable(r.Side) ? new VCSTriCheckbox(c.GroupState(files), () => c.ToggleGroup(files)) : null,
            new PPIcon("folder", M.IconSmall, ColorToken.IconSecondary),
            TextMiddle(r.Title),
            Text(files.Count.ToString(CultureInfo.InvariantCulture), Typo.Caption, ColorToken.TextTertiary).Shrink(false),
            Spacer());
        row.Padding = new Thickness(r.Depth * 14 + M.Xxs, 0, M.Sm, 0);
        row.Height = M.RowHeightCompact;
        row.Bg(ColorToken.Clear);
        return row.OnClick(() => c.ToggleCollapsed(r.Key));
    }
}

/// <summary>一个文件行：勾选、状态着色的文件名、目录、增删行数、悬停时暂存 / 取消暂存。对应 Mac VCSChangeFileRow。</summary>
internal sealed class VCSChangeFileRow : Border
{
    private readonly VCSRepo _repo;
    private readonly VCSFileChange _file;
    private readonly string _side;
    private readonly bool _selected;
    private readonly UIElement? _hoverActions;
    private readonly UIElement? _stats;

    private VCSChangesModel Changes => _repo.Changes;

    public VCSChangeFileRow(VCSRepo repo, VCSFileChange file, string side, int depth)
    {
        _repo = repo;
        _file = file;
        _side = side;
        var c = repo.Changes;
        var sel = new VCSChangeSelection(file.Path, side);
        _selected = Nullable.Equals(c.Selection, sel) || c.Multi.Contains(sel);
        var st = side switch
        {
            VCSChangeSide.Staged => file.IndexStatus ?? file.Status,
            VCSChangeSide.Unstaged => file.WorktreeStatus ?? file.Status,
            _ => file.Status,
        };
        var name = Text(VCSPath.Name(file.Path), Typo.Body, VCSStyle.Color(st));
        if (st == "D") name.TextDecorations = TextDecorations.Strikethrough;
        var row = HStack(M.Sm);
        if (!c.StagingArea && (side == VCSChangeSide.Head || side == VCSChangeSide.Untracked))
            row.Children.Add(new VCSTriCheckbox(c.IsIncluded(file), () => c.SetIncluded(file, c.IsIncluded(file) != true)));
        row.Children.Add(VCSViews.StatusLetter(st));
        row.Children.Add(name);
        if (file.OldPath is { } old && old != file.Path)
            row.Children.Add(TextMiddle("← " + (VCSPath.Dir(old) == VCSPath.Dir(file.Path) ? VCSPath.Name(old) : old), Typo.Small, ColorToken.TextTertiary));
        if (!c.GroupByDir)
            row.Children.Add(TextMiddle(VCSPath.Dir(file.Path), Typo.Small, ColorToken.TextTertiary));
        if (side == VCSChangeSide.Conflict)
            row.Children.Add(Text(VCSStyle.ConflictTitle(file.ConflictType), Typo.Caption, ColorToken.Danger));
        row.Children.Add(Spacer(M.Xs));
        if (c.StagingArea)
        {
            _hoverActions = HoverActions();
            if (_hoverActions != null)
            {
                _hoverActions.Visibility = Visibility.Collapsed;
                row.Children.Add(_hoverActions);
            }
        }
        if (file.Binary) _stats = new PPIcon("binary", 12, ColorToken.TextTertiary);
        else if (side is VCSChangeSide.Head or VCSChangeSide.Untracked or VCSChangeSide.Staged) _stats = VCSViews.LineStats(file.Additions, file.Deletions);
        if (_stats != null) row.Children.Add(_stats);
        Child = row;
        Padding = new Thickness(depth * 14 + M.Xxs, 0, M.Sm, 0);
        Height = M.RowHeightCompact;
        CornerRadius = new CornerRadius(M.RadiusSM);
        SnapsToDevicePixels = true;
        this.Tip(file.Path).Id("vcs.changes.file." + file.Path);
        MouseEnter += (_, _) => Update();
        MouseLeave += (_, _) => Update();
        this.OnClick(() => c.Select(sel, file, repo));
        this.PPContextMenu(() => VCSFileMenu.Entries(repo, c.Scope(sel)));
        Update();
    }

    private UIElement? HoverActions()
    {
        switch (_side)
        {
            case VCSChangeSide.Staged:
                return new PPIconButton("minus", L.T("vcs.unstage"), () =>
                    _repo.Perform(VCSOp.Unstage, new VCSPathsArgs { Paths = new List<string> { _file.Path } }, L.T("vcs.unstage"), quietSuccess: true), size: 20, iconSize: 12);
            case VCSChangeSide.Unstaged:
            case VCSChangeSide.Untracked:
                return new PPIconButton("plus", L.T("vcs.stage"), () =>
                    _repo.Perform(VCSOp.Stage, new VCSPathsArgs { Paths = new List<string> { _file.Path } }, L.T("vcs.stage"), quietSuccess: true), size: 20, iconSize: 12);
            default:
                return null;
        }
    }

    private void Update()
    {
        bool hover = IsMouseOver;
        this.Bg(_selected ? ColorToken.BgSelected : hover ? ColorToken.BgHover : ColorToken.Clear);
        bool showActions = hover && Changes.StagingArea && _hoverActions != null;
        if (_hoverActions != null) _hoverActions.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        if (_stats != null) _stats.Visibility = showActions ? Visibility.Collapsed : Visibility.Visible;
    }
}

// MARK: - 文件右键菜单

public static class VCSFileMenu
{
    public static List<MenuEntry> Entries(VCSRepo repo, List<VCSChangeSelection> scope)
    {
        if (repo.Status is not { } s) return new List<MenuEntry>();
        var byPath = new Dictionary<string, VCSFileChange>();
        foreach (var f in s.Files) if (!byPath.ContainsKey(f.Path)) byPath[f.Path] = f;
        var files = scope.Where(x => byPath.ContainsKey(x.Path)).Select(x => byPath[x.Path]).ToList();
        if (files.Count == 0 || scope.Count == 0) return new List<MenuEntry>();
        var first = files[0];
        var firstSel = scope[0];
        var paths = files.Select(f => f.Path).ToList();
        bool single = files.Count == 1;
        var conflicts = files.Where(f => f.Conflicted).ToList();
        var untracked = files.Where(f => f.IsUntracked).ToList();
        bool staging = repo.Changes.StagingArea;
        var e = new List<MenuEntry>
        {
            MenuEntry.Item(L.T("vcs.showDiff"), () => VCSDiffDialog.PresentWorking(repo, first, firstSel.Side), icon: "file-diff", disabled: !single),
            MenuEntry.Item(L.T("vcs.fileHistory"), () => VCSFileHistoryDialog.Present(repo, first.Path), icon: "history",
                disabled: !single || first.IsUntracked || first.Status == "A"),
            MenuEntry.Divider,
        };
        if (staging)
        {
            var stageable = files.Where(f => !f.Conflicted && (f.IsUntracked || !string.IsNullOrEmpty(f.WorktreeStatus))).ToList();
            var unstageable = files.Where(f => !f.Conflicted && !string.IsNullOrEmpty(f.IndexStatus)).ToList();
            e.Add(MenuEntry.Item(L.T("vcs.stage"), () =>
                repo.Perform(VCSOp.Stage, new VCSPathsArgs { Paths = stageable.Select(f => f.Path).ToList() }, L.T("vcs.stage"), quietSuccess: true),
                icon: "plus", disabled: stageable.Count == 0));
            e.Add(MenuEntry.Item(L.T("vcs.unstage"), () =>
                repo.Perform(VCSOp.Unstage, new VCSPathsArgs { Paths = unstageable.Select(f => f.Path).ToList() }, L.T("vcs.unstage"), quietSuccess: true),
                icon: "minus", disabled: unstageable.Count == 0));
        }
        if (conflicts.Count > 0)
        {
            var cp = conflicts.Select(f => f.Path).ToList();
            e.Add(MenuEntry.Item(L.T("vcs.conflicts.useOurs"), () => VCSConflictActions.Resolve(repo, cp, "ours"), icon: "arrow-left"));
            e.Add(MenuEntry.Item(L.T("vcs.conflicts.useTheirs"), () => VCSConflictActions.Resolve(repo, cp, "theirs"), icon: "arrow-right"));
            e.Add(MenuEntry.Item(L.T("vcs.conflicts.mergeTool"), () => VCSConflictActions.MergeTool(repo, cp), icon: "git-merge"));
            e.Add(MenuEntry.Item(L.T("vcs.conflicts.markResolved"), () => VCSConflictActions.Resolve(repo, cp, "resolved"), icon: "check"));
        }
        var discardable = files.Where(f => !f.Conflicted).ToList();
        if (discardable.Count > 0)
        {
            var mode = staging && firstSel.Side == VCSChangeSide.Unstaged ? VCSDiffMode.Unstaged : null;
            e.Add(MenuEntry.Item(L.T("vcs.discard.ellipsis"), () => VCSDiscardDialog.Present(repo, discardable, mode), icon: "undo-2", destructive: true));
        }
        if (untracked.Count > 0)
        {
            e.Add(MenuEntry.Submenu(L.T("vcs.ignore"), IgnoreEntries(repo, untracked), icon: "eye-off"));
            e.Add(MenuEntry.Item(L.T("vcs.clean.ellipsis"), () =>
            {
                var up = untracked.Select(f => f.Path).ToList();
                Dialogs.Confirm(L.T("vcs.clean.title", up.Count), string.Join("\n", up.Take(10)),
                    async () => await repo.RunThrowing(VCSOp.Clean, new VCSCleanArgs { Paths = up }, L.T("vcs.clean")),
                    ok: L.T("common.delete"), danger: true);
            }, icon: "trash-2", destructive: true));
        }
        e.Add(MenuEntry.Item(L.T("vcs.stash.selected"), () => VCSStashDialogs.PresentNew(repo, paths), icon: "archive", disabled: files.All(f => f.Conflicted)));
        e.Add(MenuEntry.Divider);
        e.Add(MenuEntry.Item(L.T("vcs.revealInFinder"), () =>
        {
            var abs = VCSPath.Join(repo.Root, first.Path);
            Actions.RevealInExplorer(File.Exists(abs) || Directory.Exists(abs) ? abs : VCSPath.Join(repo.Root, VCSPath.Dir(first.Path)));
        }, icon: "folder-open"));
        e.Add(MenuEntry.Item(L.T("vcs.copyPath"), () => Actions.Copy(string.Join("\n", paths)), icon: "copy"));
        e.Add(MenuEntry.Item(L.T("vcs.copyAbsolutePath"), () => Actions.Copy(string.Join("\n", paths.Select(p => VCSPath.Join(repo.Root, p)))), icon: "copy"));
        var plugin = VCSPluginActions.Entries(ActionLocation.VcsFileMenu, repo,
            new Dictionary<string, JToken> { ["path"] = first.Path, ["paths"] = new JArray(paths) });
        if (plugin.Count > 0)
        {
            e.Add(MenuEntry.Divider);
            e.AddRange(plugin);
        }
        return e;
    }

    public static List<MenuEntry> IgnoreEntries(VCSRepo repo, List<VCSFileChange> files)
    {
        var e = new List<MenuEntry>();
        var exact = files.Select(f => "/" + (f.Path.EndsWith("/", StringComparison.Ordinal) ? f.Path.Substring(0, f.Path.Length - 1) + "/" : f.Path)).ToList();
        e.Add(MenuEntry.Item(exact.Count == 1 ? exact[0] : L.T("vcs.ignore.files", exact.Count), () => Ignore(repo, exact, null)));
        var exts = new HashSet<string>(files.Select(f => VCSPath.Ext(f.Path)).Where(x => x.Length > 0));
        if (exts.Count == 1)
        {
            var x = exts.First();
            e.Add(MenuEntry.Item("*." + x, () => Ignore(repo, new List<string> { "*." + x }, null)));
        }
        var dirs = new HashSet<string>(files.Select(f => VCSPath.Dir(f.Path)).Where(d => d.Length > 0));
        if (dirs.Count == 1)
        {
            var d = dirs.First();
            e.Add(MenuEntry.Item("/" + d + "/", () => Ignore(repo, new List<string> { "/" + d + "/" }, null)));
        }
        e.Add(MenuEntry.Divider);
        e.Add(MenuEntry.Item(L.T("vcs.ignore.exclude"), () => Ignore(repo, exact, "exclude")));
        return e;
    }

    private static void Ignore(VCSRepo repo, List<string> patterns, string? file) =>
        repo.Perform(VCSOp.Ignore, new VCSIgnoreArgs { Patterns = patterns, File = file }, L.T("vcs.ignore"));
}

// MARK: - 丢弃确认

public static class VCSDiscardDialog
{
    /// <summary>mode：null 恢复到 HEAD；unstaged 只丢弃未暂存部分</summary>
    public static void Present(VCSRepo repo, List<VCSFileChange> files, string? mode)
    {
        const string id = "vcs-discard";
        Dialogs.Present(id, new Body(id, repo, files, mode));
    }

    private sealed class Body : PPView
    {
        private readonly string _id;
        private readonly VCSRepo _repo;
        private readonly List<VCSFileChange> _files;
        private readonly string? _mode;
        private readonly State<bool> _keepNew;
        private readonly State<bool> _busy;
        private readonly State<string?> _error;

        public Body(string id, VCSRepo repo, List<VCSFileChange> files, string? mode)
        {
            _id = id;
            _repo = repo;
            _files = files;
            _mode = mode;
            _keepNew = UseState(false);
            _busy = UseState(false);
            _error = UseState<string?>(null);
        }

        private List<VCSFileChange> NewFiles => _files.Where(f => f.IsUntracked || f.Status == "A").ToList();

        protected override UIElement Build()
        {
            var list = VStack(2);
            foreach (var f in _files.Take(12))
                list.Children.Add(HStack(M.Sm, VCSViews.StatusLetter(f.Status), TextMiddle(f.Path, Typo.Small)));
            if (_files.Count > 12) list.Children.Add(Text("…", Typo.Body, ColorToken.TextTertiary));
            var col = VStack(M.Md, list);
            col.Align = CrossAlign.Stretch;
            var nf = NewFiles;
            if (nf.Count > 0 && _mode != VCSDiffMode.Unstaged)
            {
                col.Children.Add(new PPAlert(_keepNew.Value ? "info" : "warning",
                    _keepNew.Value ? L.T("vcs.discard.keepNewNote", nf.Count) : L.T("vcs.discard.deleteNew", nf.Count)));
                col.Children.Add(new PPCheckbox(_keepNew, L.T("vcs.discard.keepNew")));
            }
            if (_error.Value is { } err) col.Children.Add(new PPAlert("danger", err));
            var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
            var ok = new PPButton(L.T("vcs.discard"), Submit, kind: PPButtonKind.Danger, busy: _busy.Value).Id("dialog.ok");
            return new PPDialog(L.T("vcs.discard.title", _files.Count), col, new UIElement[] { cancel, ok }, 480, Close) { DefaultButton = ok, CancelButton = cancel };
        }

        private void Close() => Dialogs.Dismiss(_id);

        private async void Submit()
        {
            if (_busy.Value) return;
            _busy.Value = true;
            try
            {
                var args = new VCSDiscardArgs { Paths = _files.Select(f => f.Path).ToList(), Mode = _mode, KeepNew = _keepNew.Value ? true : null };
                await _repo.RunThrowing(VCSOp.Discard, args, L.T("vcs.discard"), quietSuccess: true);
                Close();
            }
            catch (Exception e)
            {
                _error.Value = e.Message;
            }
            _busy.Value = false;
        }
    }
}

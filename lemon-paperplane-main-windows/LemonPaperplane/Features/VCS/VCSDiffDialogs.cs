using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 大对话框：差异浏览（文件列表 + 差异）、文件历史、比较两个引用（对应 Mac VCSDiffDialogs.swift）。

/// <summary>文件列表 + 差异。对应 Mac VCSDiffBrowser。</summary>
public sealed class VCSDiffBrowser : PPView
{
    private readonly VCSRepo _repo;
    private readonly List<VCSFileChange> _files;
    private readonly Func<VCSFileChange, VCSDiffTarget> _target;
    private readonly State<string?> _selected;
    private readonly VCSDiffModel _diff = new();
    private readonly VCSVirtualList _list = new(new Thickness(M.Xs));
    private readonly ScrollViewer _scroll;
    private VCSDiffView? _view;

    public VCSDiffBrowser(VCSRepo repo, List<VCSFileChange> files, Func<VCSFileChange, VCSDiffTarget> target, string? selected)
    {
        _repo = repo;
        _files = files;
        _target = target;
        _selected = UseState(selected);
        _scroll = _list.InScroll();
        _scroll.Key("vcs.browser.list");
        _scroll.Bg(ColorToken.BgContent);
    }

    protected override void OnAppear()
    {
        if (_diff.Target != null) return;
        if ((_files.FirstOrDefault(f => f.Path == _selected.Value) ?? _files.FirstOrDefault()) is { } f) Select(f);
    }

    private void Select(VCSFileChange f)
    {
        _selected.Value = f.Path;
        _diff.Set(_target(f), _repo);
    }

    protected override UIElement Build()
    {
        _view ??= new VCSDiffView(_diff, _repo.Changes);
        UI.Detach(_view);
        if (_files.Count == 0) return new PPEmptyState("file-diff", L.T("vcs.diff.noFiles"));
        if (_files.Count == 1) return _view;
        _list.SetRows(_files.Select(f => new VRow(f.Path, M.RowHeightCompact + 1, () => new VCSFileLine(f, f.Path == _selected.Value, () => Select(f)))).ToList());
        UI.Detach(_scroll);
        return new PPSplitView(_scroll, _view, UIState.Bind("vcs.browser.left", 300.0), 200, 520);
    }
}

public static class VCSDiffDialog
{
    public static void Present(VCSRepo repo, string title, List<VCSFileChange> files, string? selected, Func<VCSFileChange, VCSDiffTarget> target)
    {
        const string id = "vcs-diff";
        Dialogs.Present(id, new VCSLargeDialog(id, title, new VCSDiffBrowser(repo, files, target, selected)));
    }

    /// <summary>工作区文件（变更列表里「显示差异」）</summary>
    public static void PresentWorking(VCSRepo repo, VCSFileChange file, string side)
    {
        var t = repo.Changes.Target(new VCSChangeSelection(file.Path, side), file);
        Present(repo, file.Path, new List<VCSFileChange> { file }, file.Path, _ => t);
    }

    public static void PresentCommit(VCSRepo repo, VCSCommitDetail detail, VCSFileChange file, int parent)
    {
        var c = detail.Commit;
        Present(repo, $"{c.Short}  {c.Subject}", detail.Files, file.Path, f =>
            new VCSDiffTarget(f.Path, f.OldPath, VCSDiffKind.Commit(c.Hash, parent), f.Status));
    }

    public static void PresentStash(VCSRepo repo, VCSStash stash, VCSStashShow show, VCSFileChange file) =>
        Present(repo, $"stash@{{{stash.Index}}}  {stash.Message}", show.Files, file.Path, f =>
            new VCSDiffTarget(f.Path, f.OldPath, VCSDiffKind.Commit(show.Hash, 1), f.Status));
}

// MARK: - 文件历史

public static class VCSFileHistoryDialog
{
    public static void Present(VCSRepo repo, string path)
    {
        const string id = "vcs-file-history";
        Dialogs.Present(id, new VCSLargeDialog(id, L.T("vcs.fileHistory.title", path), new Body(repo, path, id)));
    }

    private sealed class Body : PPView
    {
        private readonly VCSRepo _repo;
        private readonly string _path;
        private readonly string _id;
        private readonly State<List<VCSCommit>> _commits = new(new List<VCSCommit>());
        private bool _hasMore;
        private readonly State<string?> _error;
        private readonly State<bool> _loading;
        private readonly State<string?> _selected;
        private readonly VCSDiffModel _diff = new();
        private readonly VCSVirtualList _list = new(new Thickness(M.Xs));
        private readonly ScrollViewer _scroll;
        private VCSDiffView? _view;
        private bool _started;

        public Body(VCSRepo repo, string path, string id)
        {
            _repo = repo;
            _path = path;
            _id = id;
            Observe(_commits);
            _error = UseState<string?>(null);
            _loading = UseState(true);
            _selected = UseState<string?>(null);
            _scroll = _list.InScroll();
            _scroll.Key("vcs.history.list");
            _list.EndThreshold = 1;
            _list.OnEndReached = LoadMore;
        }

        protected override async void OnAppear()
        {
            if (_started) return;
            _started = true;
            await Load(0);
        }

        private async System.Threading.Tasks.Task Load(int skip)
        {
            _loading.Value = true;
            try
            {
                var log = await _repo.Api().VcsFileHistoryAsync(_repo.ProjectId, new VCSFileHistoryArgs { Path = _path, Limit = 100, Skip = skip > 0 ? skip : null }, _repo.Root);
                _commits.Value = skip == 0 ? log.Commits : _commits.Value.Concat(log.Commits).ToList();
                _hasMore = log.HasMore;
                if (_selected.Value == null && _commits.Value.FirstOrDefault() is { } f) Select(f);
            }
            catch (Exception e)
            {
                _error.Value = e.Message;
            }
            _loading.Value = false;
        }

        private async void LoadMore()
        {
            if (!_hasMore || _loading.Value) return;
            await Load(_commits.Value.Count);
        }

        private void Select(VCSCommit c)
        {
            _selected.Value = c.Hash;
            _diff.Set(new VCSDiffTarget(c.Path ?? _path, null, VCSDiffKind.Commit(c.Hash, 1), c.PathStatus), _repo);
        }

        protected override UIElement Build()
        {
            UIElement left;
            if (_error.Value is { } e) left = new PPAlert("danger", e).Margins(M.Md).Top();
            else if (_commits.Value.Count == 0 && !_loading.Value) left = new PPEmptyState("history", L.T("vcs.fileHistory.empty"));
            else
            {
                var rows = _commits.Value.Select(c => new VRow(c.Hash, 38, () => Row(c))).ToList();
                if (_loading.Value) rows.Add(new VRow("loading", 40, () => new PPSpinner(M.IconSmall).Center()));
                _list.SetRows(rows);
                UI.Detach(_scroll);
                left = _scroll;
            }
            _view ??= new VCSDiffView(_diff, _repo.Changes);
            UI.Detach(_view);
            return new PPSplitView(Box(left).Bg(ColorToken.BgContent), _view, UIState.Bind("vcs.history.left", 420.0), 280, 640);
        }

        private FrameworkElement Row(VCSCommit c)
        {
            var meta = HStack(M.Sm, Text(c.Short, Typo.MonoSmall).Shrink(false), Text(c.Author, Typo.Caption), Text(VCSDate.Short(c.ParsedDate), Typo.Caption).Shrink(false),
                c.Path is { } p && p != _path ? TextMiddle(p, Typo.Caption) : null).Fg(ColorToken.TextTertiary);
            var content = HStack(M.Sm, c.PathStatus is { } s ? VCSViews.StatusLetter(s) : null, VStack(0, Text(c.Subject), meta).Flex());
            content.Margin = new Thickness(M.Sm, 0, M.Sm, 0);
            var row = VCSViews.TableRow(content, c.Hash == _selected.Value, () => Select(c), height: 38);
            row.PPContextMenu(() => new[]
            {
                MenuEntry.Item(L.T("vcs.copyHash"), () => Actions.Copy(c.Hash), icon: "copy"),
                MenuEntry.Item(L.T("vcs.fileHistory.compareWithCurrent"), () =>
                    _diff.Set(new VCSDiffTarget(_path, c.Path != _path ? c.Path : null, VCSDiffKind.Range(c.Hash, null)), _repo), icon: "git-compare"),
                MenuEntry.Item(L.T("vcs.showInLog"), () =>
                {
                    Dialogs.Dismiss(_id);
                    _repo.Session.Page = VCSPage.Log;
                    _repo.Log.Focus(c.Hash);
                }, icon: "git-graph"),
                MenuEntry.Divider,
                MenuEntry.Item(L.T("vcs.checkoutFileVersion"), () => VCSHistoryActions.RestoreFile(_repo, c.Path ?? _path, c.Hash, c.Short), icon: "rotate-ccw"),
            });
            return row;
        }
    }
}

// MARK: - 比较

public static class VCSCompareDialog
{
    /// <summary>base 与 head（null 为当前分支）之间：双方独有的提交 + merge-base 到 head 的文件变化</summary>
    public static void Present(VCSRepo repo, string @base, string? head, string title)
    {
        const string id = "vcs-compare";
        Dialogs.Present(id, new VCSLargeDialog(id, title, new Body(repo, @base, head)));
    }

    private sealed class Body : PPView
    {
        private readonly VCSRepo _repo;
        private readonly string _base;
        private readonly string? _head;
        private readonly State<VCSCompare?> _result;
        private readonly State<string?> _error;
        private readonly State<string> _tab;
        private readonly State<string?> _selected;
        private readonly VCSVirtualList _list = new(new Thickness(M.Xs));
        private readonly ScrollViewer _scroll;
        private bool _started;

        public Body(VCSRepo repo, string @base, string? head)
        {
            _repo = repo;
            _base = @base;
            _head = head;
            _result = UseState<VCSCompare?>(null);
            _error = UseState<string?>(null);
            _tab = UseState("commits");
            _selected = UseState<string?>(null);
            _scroll = _list.InScroll();
            _scroll.Key("vcs.compare.list");
        }

        private string HeadName => _head ?? _repo.HeadTitle;

        protected override async void OnAppear()
        {
            if (_started) return;
            _started = true;
            try { _result.Value = await _repo.Api().VcsCompareAsync(_repo.ProjectId, new VCSCompareArgs { Base = _base, Head = _head }, _repo.Root); }
            catch (Exception e) { _error.Value = e.Message; }
        }

        protected override UIElement Build()
        {
            var r = _result.Value;
            var top = HStack(0,
                new PPSegmented<string>(new[]
                {
                    ("commits", L.T("vcs.compare.commits", (r?.Ahead.Count ?? 0) + (r?.Behind.Count ?? 0))),
                    ("files", L.T("vcs.compare.files", r?.Files.Count ?? 0)),
                }, _tab, PPButtonSize.Small).Id("vcs.compare.tab"),
                Spacer(),
                r?.Truncated == true ? new PPBadge(L.T("vcs.compare.truncated"), tone: "warning") : null);
            top.Padding = new Thickness(M.Lg, 0, M.Lg, 0);
            top.Height = M.ToolbarHeight;
            UIElement body;
            if (_error.Value is { } e) body = new PPAlert("danger", e).Margins(M.Md).Top();
            else if (r != null)
            {
                body = _tab.Value == "files"
                    ? Keep("files", () => new VCSDiffBrowser(_repo, r.Files, f =>
                        new VCSDiffTarget(f.Path, f.OldPath, VCSDiffKind.Range(r.MergeBase ?? _base, _head ?? "HEAD"), f.Status), null))
                    : Commits(r);
            }
            else body = new PPSpinner().Center();
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(top.At(0, 0));
            g.Children.Add(new PPDivider(subtle: true).At(1, 0));
            g.Children.Add(body.At(2, 0));
            return g;
        }

        private UIElement Commits(VCSCompare r)
        {
            var rows = new List<VRow>
            {
                new("h:ahead", M.RowHeightCompact, () => VCSViews.GroupHeader(L.T("vcs.compare.onlyIn", HeadName), r.Ahead.Count)),
            };
            rows.AddRange(r.Ahead.Select(c => new VRow("a:" + c.Hash, M.RowHeight, () => CommitRow(c))));
            var baseName = _base.Length == 40 ? _base.Substring(0, 8) : _base;
            rows.Add(new VRow("h:behind", M.RowHeightCompact + M.Md, () => VCSViews.GroupHeader(L.T("vcs.compare.onlyIn", baseName), r.Behind.Count).Margins(0, M.Md, 0, 0)));
            rows.AddRange(r.Behind.Select(c => new VRow("b:" + c.Hash, M.RowHeight, () => CommitRow(c))));
            _list.SetRows(rows);
            UI.Detach(_scroll);
            UIElement detail = _selected.Value is { } h
                ? Keep("detail:" + h, () => new VCSCommitDetailView(_repo, h))
                : new PPEmptyState("git-commit-horizontal", L.T("vcs.log.selectCommit"));
            return Grid("*,1,400", null, _scroll.At(0, 0), new PPDivider(vertical: true).At(0, 1), detail.At(0, 2));
        }

        private FrameworkElement CommitRow(VCSCommit c)
        {
            var content = HStack(M.Md,
                Text(c.Short, Typo.MonoSmall, ColorToken.TextTertiary).W(64).Shrink(false),
                Text(c.Subject),
                Spacer(M.Sm),
                Text(c.Author, Typo.Body, ColorToken.TextSecondary),
                Text(VCSDate.Short(c.ParsedDate), Typo.Body, ColorToken.TextTertiary).W(110).Shrink(false).Align(TextAlignment.Right));
            content.Margin = new Thickness(M.Sm, 0, M.Sm, 0);
            var row = VCSViews.TableRow(content, c.Hash == _selected.Value, () => _selected.Value = c.Hash);
            row.PPContextMenu(() => VCSCommitMenu.Entries(_repo, new List<VCSCommit> { c }));
            return row;
        }
    }
}

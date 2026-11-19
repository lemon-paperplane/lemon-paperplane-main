using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 工程详情「版本控制」标签页：仓库切换、工具栏（分支、获取、拉取、推送、插件动作）、进行中操作横幅、
/// 子页（提交、日志、分支、储藏、冲突、插件 vcs.tab）。键盘：Ctrl+K 提交、Ctrl+Shift+K 推送、Ctrl+T 拉取。对应 Mac VCSTabView。
/// </summary>
public sealed class VCSTabView : PPView
{
    private readonly string _projectId;
    private readonly VCSSession _session;

    public VCSTabView(string projectId)
    {
        _projectId = projectId;
        _session = VCSSession.Of(projectId);
        Observe(_session, nameof(VCSSession.RootsLoaded), nameof(VCSSession.Roots), nameof(VCSSession.SelectedRoot), nameof(VCSSession.RootsError));
    }

    protected override async void OnAppear()
    {
        _session.Visible = true;
        if (!_session.RootsLoaded || _session.Roots.Count == 0) await _session.LoadRoots();
    }

    protected override void OnDisappear() => _session.Visible = false;

    protected override UIElement Build()
    {
        if (!_session.RootsLoaded) return new PPSpinner().Center();
        if (_session.Roots.Count == 0)
        {
            var col = VStack(M.Md,
                new PPEmptyState("folder-git", L.T("vcs.notRepo"), L.T("vcs.init"), _session.InitRepository, "plus"),
                _session.RootsError is { } e ? new PPAlert("danger", e).Margins(M.Xl) : null);
            col.Align = CrossAlign.Stretch;
            col.VerticalAlignment = VerticalAlignment.Center;
            return col;
        }
        if (_session.Repo is { } repo) return Keep("repo:" + repo.Root, () => new VCSRepoView(_session, repo));
        return new PPSpinner().Center();
    }
}

internal sealed class VCSRepoView : PPView
{
    private readonly VCSSession _session;
    private readonly VCSRepo _repo;
    private readonly DataStore _data = AppModel.Shared.Data;
    private int _lastConflicts;

    public VCSRepoView(VCSSession session, VCSRepo repo)
    {
        _session = session;
        _repo = repo;
        Observe(repo, nameof(VCSRepo.Status), nameof(VCSRepo.Notice));
        Observe(session, nameof(VCSSession.Page));
        Observe(_data, nameof(DataStore.Contributions));
    }

    private List<ViewInfo> PluginViews => _data.Contributions.ViewsAt(ViewLocation.VcsTab);

    protected override async void OnAppear()
    {
        _repo.PropertyChanged += OnRepoChanged;
        _lastConflicts = _repo.Status?.Conflicts ?? 0;
        Shortcuts.Register("Ctrl+K", FocusCommit);
        Shortcuts.Register("Ctrl+Shift+K", () => VCSPushDialog.Present(_repo));
        Shortcuts.Register("Ctrl+T", () => VCSPullDialog.Present(_repo));
        await _repo.Refresh();
        await _repo.LoadRemotes();
        await _repo.LoadStashes();
    }

    protected override void OnDisappear()
    {
        _repo.PropertyChanged -= OnRepoChanged;
        Shortcuts.Unregister("Ctrl+K");
        Shortcuts.Unregister("Ctrl+Shift+K");
        Shortcuts.Unregister("Ctrl+T");
    }

    private void FocusCommit()
    {
        _session.Page = VCSPage.Changes;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(_repo.Changes.RequestFocusMessage));
    }

    /// <summary>冲突解决完后冲突页消失：回到提交页</summary>
    private void OnRepoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VCSRepo.Status)) return;
        var n = _repo.Status?.Conflicts ?? 0;
        if (n != _lastConflicts)
        {
            _lastConflicts = n;
            if (n == 0 && _session.Page == VCSPage.Conflicts && _repo.Status?.Operation == null) _session.Page = VCSPage.Changes;
        }
    }

    protected override UIElement Build()
    {
        var g = new Grid();
        void Row(UIElement? el, bool fill = false)
        {
            if (el == null) return;
            g.RowDefinitions.Add(new RowDefinition { Height = fill ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            g.Children.Add(el.At(g.RowDefinitions.Count - 1, 0));
        }
        Row(Keep("toolbar", () => new VCSToolbar(_session, _repo)));
        Row(new PPDivider(subtle: true));
        if (_repo.Status is { } s && s.Operation != null) Row(new VCSOperationBanner(_repo).Margins(M.Md, M.Sm, M.Md, 0));
        if (_repo.Notice is { } n) Row(Keep("notice:" + n.Id, () => new VCSNoticeBar(_repo, n)).Margins(M.Md, M.Sm, M.Md, 0));
        Row(HStack(0, new PPTabBar<string>(Tabs(), Bind.Of(() => _session.Page, v => _session.Page = v)).Id("vcs.tabs"), Spacer()).Margins(M.Md, 0, M.Md, 0));
        Row(new PPDivider(subtle: true));
        Row(Page(), fill: true);
        return g;
    }

    private List<PPTab<string>> Tabs()
    {
        var s = _repo.Status;
        var t = new List<PPTab<string>>
        {
            new(VCSPage.Changes, L.T("vcs.page.changes"), "git-commit-horizontal", s?.Files.Count),
            new(VCSPage.Log, L.T("vcs.page.log"), "git-graph"),
            new(VCSPage.Branches, L.T("vcs.page.branches"), "git-branch"),
            new(VCSPage.Stashes, L.T("vcs.page.stashes"), "archive", s?.StashCount),
        };
        if ((s?.Conflicts ?? 0) > 0 || s?.Operation != null || _session.Page == VCSPage.Conflicts)
            t.Add(new PPTab<string>(VCSPage.Conflicts, L.T("vcs.page.conflicts"), "triangle-alert", s?.Conflicts));
        foreach (var v in PluginViews)
            t.Add(new PPTab<string>(VCSPage.Plugin(v.Key), v.Title, iconRef: IconKey.Parse(v.Icon, v.Plugin)));
        return t;
    }

    private UIElement Page()
    {
        var page = _session.Page;
        switch (page)
        {
            case VCSPage.Changes: return Keep("page:changes", () => new VCSChangesPage(_repo));
            case VCSPage.Log: return Keep("page:log", () => new VCSLogPage(_repo));
            case VCSPage.Branches: return Keep("page:branches", () => new VCSBranchesPage(_repo));
            case VCSPage.Stashes: return Keep("page:stashes", () => new VCSStashPage(_repo));
            case VCSPage.Conflicts: return Keep("page:conflicts", () => new VCSConflictsPage(_repo));
        }
        if (VCSPage.IsPlugin(page))
        {
            var key = VCSPage.PluginKey(page);
            if (PluginViews.FirstOrDefault(v => v.Key == key) is { } v)
                return Keep("page:" + key + "|" + _repo.Root, () => new PluginViewHost(v, new ViewContext
                {
                    Plugin = v.Plugin,
                    View = v.Id,
                    ProjectId = _repo.ProjectId,
                    InitialState = new Dictionary<string, JToken> { ["root"] = _repo.Root },
                }));
            return new PPEmptyState("puzzle", L.T("vcs.page.pluginMissing"));
        }
        return Keep("page:changes", () => new VCSChangesPage(_repo));
    }
}

// MARK: - 工具栏

internal sealed class VCSToolbar : PPView
{
    private readonly VCSSession _session;
    private readonly VCSRepo _repo;

    public VCSToolbar(VCSSession session, VCSRepo repo)
    {
        _session = session;
        _repo = repo;
        Observe(session, nameof(VCSSession.Roots), nameof(VCSSession.SelectedRoot));
        Observe(repo, nameof(VCSRepo.Activity));
    }

    private static UIElement VDivider() => new PPDivider(vertical: true).H(16).Margins(M.Xs, 0, M.Xs, 0);

    protected override UIElement Build()
    {
        var row = HStack(M.Xs);
        if (_session.Roots.Count > 1)
        {
            row.Children.Add(new PPDropdown<string>(_session.Roots.Select(r => new DropdownOption<string>(r.Path,
                    r.RelPath == "." || r.RelPath.Length == 0 ? VCSPath.LocalName(r.Path) : r.RelPath, icon: "folder-git")),
                Bind.Of(() => _session.SelectedRoot ?? "", v => _session.SelectedRoot = v), size: PPButtonSize.Small, minWidth: 120, maxWidth: 220)
                .Tip(L.T("vcs.repository")).Id("vcs.root"));
            row.Children.Add(VDivider());
        }
        row.Children.Add(Keep("branch", () => new VCSBranchButton(_repo)));
        row.Children.Add(VDivider());
        var a = _repo.Activity;
        row.Children.Add(new PPIconButton("cloud-download", L.T("vcs.fetch"), () => VCSRemoteActions.Fetch(_repo), busy: a?.Op == VCSOp.Fetch).Disabled(a != null).Id("vcs.fetch"));
        row.Children.Add(new PPIconButton("arrow-down-to-line", L.T("vcs.pull.ellipsis") + "  Ctrl+T", () => VCSPullDialog.Present(_repo), busy: a?.Op == VCSOp.Pull)
            .Disabled(a != null).Id("vcs.pull"));
        row.Children.Add(new PPIconButton("arrow-up", L.T("vcs.push.ellipsis") + "  Ctrl+Shift+K", () => VCSPushDialog.Present(_repo), busy: a?.Op == VCSOp.Push)
            .Disabled(a != null).Id("vcs.push"));
        row.Children.Add(new PPIconButton("check", L.T("vcs.commit") + "  Ctrl+K", () =>
        {
            _session.Page = VCSPage.Changes;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(_repo.Changes.RequestFocusMessage));
        }).Id("vcs.commitFocus"));
        row.Children.Add(new PPIconButton("history", L.T("vcs.page.log"), () => _session.Page = VCSPage.Log));
        if (a != null) row.Children.Add(Keep("activity", () => new VCSActivityIndicator(_repo)).Margins(M.Md, 0, 0, 0));
        row.Children.Add(Spacer(M.Md));
        row.Children.Add(new PPIconButton("refresh-cw", L.T("vcs.refresh"), async () =>
        {
            await _session.LoadRoots();
            await _repo.Refresh();
            _repo.Changes.Diff.Reload();
        }).Id("vcs.refresh"));
        row.Children.Add(new PPMenuButton(MoreEntries, VCSViews.MenuGlyph("ellipsis", M.IconButton), PopupAlignment.Trailing, L.T("common.more")).Id("vcs.more"));
        row.Padding = new Thickness(M.Md, 0, M.Md, 0);
        row.Height = M.ToolbarHeight;
        return row;
    }

    private IEnumerable<MenuEntry> MoreEntries()
    {
        var e = new List<MenuEntry>
        {
            MenuEntry.Item(L.T("vcs.stash.new"), () => VCSStashDialogs.PresentNew(_repo, null), icon: "archive"),
            MenuEntry.Item(L.T("vcs.branch.new"), () => VCSBranchActions.NewBranch(_repo, null, null), icon: "git-branch"),
            MenuEntry.Item(L.T("vcs.checkoutRef"), () => VCSBranchActions.CheckoutRef(_repo), icon: "circle-dot"),
            MenuEntry.Item(L.T("vcs.remote.add"), () => VCSRemoteActions.AddRemote(_repo), icon: "server"),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("vcs.changes.stagingArea"), () => _repo.Changes.StagingArea = !_repo.Changes.StagingArea, icon: "layers", @checked: _repo.Changes.StagingArea),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("vcs.copyRepoPath"), () => Actions.Copy(_repo.Root), icon: "copy"),
            MenuEntry.Item(L.T("vcs.clone.ellipsis"), VCSCloneDialog.Present, icon: "download"),
        };
        if (Actions.LocalOnlyAvailable) e.Insert(e.Count - 2, MenuEntry.Item(L.T("vcs.revealRepo"), () => Actions.RevealInExplorer(_repo.Root), icon: "folder-open"));
        var plugin = VCSPluginActions.Entries(ActionLocation.VcsToolbar, _repo, new Dictionary<string, JToken>());
        if (plugin.Count > 0)
        {
            e.Add(MenuEntry.Divider);
            e.AddRange(plugin);
        }
        return e;
    }
}

// MARK: - 进行中的操作

internal sealed class VCSOperationBanner : PPView
{
    private readonly VCSRepo _repo;

    public VCSOperationBanner(VCSRepo repo)
    {
        _repo = repo;
        Observe(repo, nameof(VCSRepo.Status), nameof(VCSRepo.Activity));
    }

    protected override UIElement Build()
    {
        if (_repo.Status is not { } status) return new FrameworkElement();
        var op = status.Operation ?? "";
        bool canSkip = op is "rebase" or "cherry-pick" or "revert" or "am";
        var buttons = HStack(M.Sm,
            status.Conflicts > 0
                ? new PPButton(L.T("vcs.banner.conflicts", status.Conflicts), () => _repo.Session.Page = VCSPage.Conflicts, icon: "triangle-alert",
                    kind: PPButtonKind.Primary, size: PPButtonSize.Small).Id("vcs.banner.conflicts")
                : null,
            new PPButton(L.T("vcs.op.continue"), () => VCSConflictActions.ContinueOp(_repo), kind: status.Conflicts > 0 ? PPButtonKind.Secondary : PPButtonKind.Primary,
                size: PPButtonSize.Small, busy: _repo.Activity?.Op == VCSOp.Continue).Disabled(status.Conflicts > 0).Id("vcs.banner.continue"),
            canSkip ? new PPButton(L.T("vcs.op.skip"), () => VCSConflictActions.Skip(_repo), size: PPButtonSize.Small) : null,
            new PPButton(L.T("vcs.op.abort"), () => VCSConflictActions.Abort(_repo), kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("vcs.banner.abort"));
        return new PPAlert(status.Conflicts > 0 ? "warning" : "info", Title(status), Detail(status), buttons);
    }

    private static string Title(VCSStatus status)
    {
        var d = status.OperationDetail;
        switch (status.Operation)
        {
            case "rebase":
            {
                var t = L.T("vcs.banner.rebase", d?.HeadName ?? status.Branch, VCSStyle.ShortHash(d?.Onto));
                if (d?.Step is int step && d.Total is int total && total > 0) t += "  " + L.T("vcs.banner.step", step, total);
                return t;
            }
            case "merge":
                return L.T("vcs.banner.merge", status.Branch.Length == 0 ? VCSStyle.ShortHash(status.HeadHash) : status.Branch);
            case "cherry-pick":
                return L.T("vcs.banner.cherryPick", VCSStyle.ShortHash(d?.Current));
            case "revert":
                return L.T("vcs.banner.revert", VCSStyle.ShortHash(d?.Current));
            case "am":
                return L.T("vcs.banner.am");
            default:
                return VCSStyle.OperationTitle(status.Operation);
        }
    }

    private static string? Detail(VCSStatus status)
    {
        if (status.OperationDetail is not { } d) return null;
        if (!string.IsNullOrEmpty(d.Message)) return d.Message;
        if (status.Operation == "rebase" && !string.IsNullOrEmpty(d.Current)) return L.T("vcs.banner.applying", VCSStyle.ShortHash(d.Current));
        return null;
    }
}

/// <summary>失败 / 需要下一步的提示条：结论 + 下一步按钮 + 查看输出。对应 Mac VCSNoticeBar。</summary>
public sealed class VCSNoticeBar : PPView
{
    private readonly VCSRepo _repo;
    private readonly VCSNotice _notice;
    private readonly State<bool> _showOutput;

    public VCSNoticeBar(VCSRepo repo, VCSNotice notice)
    {
        _repo = repo;
        _notice = notice;
        _showOutput = UseState(false);
    }

    protected override UIElement Build()
    {
        var n = _notice;
        var buttons = HStack(M.Sm);
        foreach (var a in n.Actions)
            buttons.Children.Add(new PPButton(a.Title, a.Run, kind: a.Danger ? PPButtonKind.Danger : a.Primary ? PPButtonKind.Primary : PPButtonKind.Secondary, size: PPButtonSize.Small));
        if (n.TaskId is { } t)
            buttons.Children.Add(new PPButton(L.T("vcs.output"), () => Actions.ShowTaskLog(t), kind: PPButtonKind.Ghost, size: PPButtonSize.Small));
        else if (!string.IsNullOrEmpty(n.Output))
            buttons.Children.Add(new PPButton(_showOutput.Value ? L.T("vcs.hideOutput") : L.T("vcs.output"), () => _showOutput.Value = !_showOutput.Value,
                kind: PPButtonKind.Ghost, size: PPButtonSize.Small));
        buttons.Children.Add(new PPIconButton("x", L.T("common.close"), () => _repo.Notice = null, size: M.IconButtonSmall, iconSize: M.IconSmall).Id("vcs.notice.close"));
        var col = VStack(M.Sm, new PPAlert(n.Tone, n.Title, n.Message, buttons).Id("vcs.notice"));
        col.Align = CrossAlign.Stretch;
        if (_showOutput.Value && n.Output is { } o)
        {
            var text = Selectable(o, Typo.MonoSmall, wrap: false);
            var box = Box(text).Pad(M.Md);
            col.Children.Add(Box(Scroll(box, horizontal: true).MaxH(180)).Rounded(M.RadiusMD).Bg(ColorToken.BgCode));
        }
        return col;
    }
}

/// <summary>
/// 进行中的远程 / 长操作：转圈 + 标题 + 「输出」（有任务时）。单独一个视图：任务进度事件只重建它，
/// 不重建整条工具栏（工具栏里打开着的菜单、弹出框不会被关掉）。
/// </summary>
internal sealed class VCSActivityIndicator : PPView
{
    private readonly VCSRepo _repo;

    public VCSActivityIndicator(VCSRepo repo)
    {
        _repo = repo;
        Observe(repo, nameof(VCSRepo.Activity));
        Observe(AppModel.Shared.Runs, nameof(RunStore.Tasks));
    }

    protected override UIElement Build()
    {
        if (_repo.Activity is not { } a) return new FrameworkElement();
        return HStack(M.Sm, new PPSpinner(M.IconSmall), Text(a.Title, Typo.Small, ColorToken.TextSecondary),
            _repo.RunningTask != null ? new PPLink(L.T("vcs.output"), () => _repo.ShowOutput(null)) : null);
    }
}

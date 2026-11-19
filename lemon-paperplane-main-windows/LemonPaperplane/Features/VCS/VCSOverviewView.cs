using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 工作区「版本控制」视图：顶部统计 / 过滤 / 批量操作，下面每个仓库一段（可折叠），段内提交信息、提交、改动文件（对应 Mac VCSOverviewView.swift）。

/// <summary>
/// 工作区页顶部分段：启动 | 工程 | 版本控制 | 终端（按工作区记住），下面显示对应内容（对应 Mac VCSWorkspaceSwitcher）。
/// projects 是「工程」分段的内容（工程卡片网格，由工作区页提供）。
/// </summary>
public sealed class VCSWorkspaceSwitcher : PPView
{
    private readonly string _workspaceId;
    private readonly Func<UIElement> _projects;
    private readonly State<string> _mode;

    public VCSWorkspaceSwitcher(string workspaceId, Func<UIElement> projects)
    {
        _workspaceId = workspaceId;
        _projects = projects;
        _mode = UseState(VCSPrefs.String("WorkspaceMode:" + workspaceId) ?? "projects");
    }

    protected override UIElement Build()
    {
        var mode = Bind.Of(() => _mode.Value, v =>
        {
            _mode.Value = v;
            VCSPrefs.SetString("WorkspaceMode:" + _workspaceId, v);
        });
        var top = HStack(0, Keep("picker", () => new VCSWorkspaceModePicker(_workspaceId, mode)), Spacer());
        top.Margin = new Thickness(M.Xl, M.Lg, M.Xl, _mode.Value == "vcs" ? M.Sm : 0);
        UIElement content = _mode.Value switch
        {
            "vcs" => Keep("vcs", () => new VCSOverviewPage(_workspaceId)),
            "launch" => Keep("launch", () => new LaunchBoardPage(_workspaceId)),
            "terminal" => Keep("terminal", () => new TerminalPage(_workspaceId)),
            _ => _projects(),
        };
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(top.At(0, 0));
        UI.Detach(content);
        g.Children.Add(content.At(1, 0));
        return g;
    }
}

/// <summary>分段控件：启动（带运行中的启动项数）| 工程 | 版本控制（带有改动的仓库数）| 终端（带终端个数）</summary>
internal sealed class VCSWorkspaceModePicker : PPView
{
    private readonly string _workspaceId;
    private readonly Bind<string> _mode;
    private readonly VCSOverviewModel _model;
    private readonly AppModel _app = AppModel.Shared;

    public VCSWorkspaceModePicker(string workspaceId, Bind<string> mode)
    {
        _workspaceId = workspaceId;
        _mode = mode;
        _model = VCSOverviewModel.Of(workspaceId);
        Observe(_model, nameof(VCSOverviewModel.Loaded), nameof(VCSOverviewModel.Entries), VCSOverviewModel.StatsProperty);
        Observe(_app.Data, nameof(DataStore.Projects), nameof(DataStore.Workspaces));
        Observe(_app.Runs, nameof(RunStore.Runs));
        Observe(_app.Terminals);
    }

    /// <summary>总览取过就用它（含嵌套仓库），否则用工程卡片的 Git 摘要（主仓库）</summary>
    private int ChangedCount
    {
        get
        {
            if (_model.Loaded) return _model.Stats.Changed;
            return _app.Data.ProjectsIn(_workspaceId).Count(p => p.Vcs is { } v && (v.Changes > 0 || v.Conflicts > 0 || !string.IsNullOrEmpty(v.Operation)));
        }
    }

    protected override UIElement Build()
    {
        var row = HStack(2,
            Segment("launch", L.T("launch.mode"), "rocket", LaunchGroupData.ActiveCount(_workspaceId, _app.Data, _app.Runs), "success"),
            Segment("projects", L.T("vcs.overview.modeProjects"), "layout-grid", 0),
            Segment("vcs", L.T("vcs.overview.modeVCS"), "git-branch", ChangedCount),
            Segment("terminal", L.T("terminal.mode"), "square-terminal", _app.Terminals.In(_workspaceId).Count, null));
        return Box(row).Pad(2).Rounded(M.RadiusMD).Bg(ColorToken.NeutralSubtle).Do(b => b.HorizontalAlignment = HorizontalAlignment.Left);
    }

    private UIElement Segment(string value, string title, string icon, int count, string? tone = "info") =>
        new ModeSegment(title, icon, count, tone, _mode.Value == value) { Action = () => { _mode.Value = value; Invalidate(); } }.Id("workspace.mode." + value);

    private sealed class ModeSegment : PPButtonBase
    {
        private readonly Border _bg;
        private readonly Border _shadow;
        private readonly bool _on;

        public ModeSegment(string title, string icon, int count, string? tone, bool on)
        {
            _on = on;
            var row = HStack(M.Sm, new PPIcon(icon, M.IconSmall), Text(title, Typo.Label).Shrink(false), count > 0 ? new PPCountBadge(count, tone) : null);
            row.VerticalAlignment = VerticalAlignment.Center;
            _shadow = new Border { CornerRadius = new CornerRadius(M.RadiusSM), Visibility = on ? Visibility.Visible : Visibility.Collapsed }
                .Bg(ColorToken.BgSurface).Shadow(ShadowToken.Hairline);
            _bg = new Border
            {
                Child = row,
                Padding = new Thickness(M.Lg, 0, M.Lg, 0),
                Height = M.ControlHeight - 4,
                CornerRadius = new CornerRadius(M.RadiusSM),
                SnapsToDevicePixels = true,
            };
            _bg.Fg(on ? ColorToken.TextPrimary : ColorToken.TextSecondary);
            var g = new Grid();
            g.Children.Add(_shadow);
            g.Children.Add(_bg);
            Content = g;
            this.A11y(title);
            UpdateVisual();
        }

        public override void UpdateVisual()
        {
            if (_bg == null) return;
            _bg.Bg(_on ? ColorToken.BgSurface : ColorToken.Clear);
        }
    }
}

// MARK: - 页面

public sealed class VCSOverviewPage : PPView
{
    private readonly string _workspaceId;
    private readonly VCSOverviewModel _model;

    public VCSOverviewPage(string workspaceId)
    {
        _workspaceId = workspaceId;
        _model = VCSOverviewModel.Of(workspaceId);
    }

    protected override async void OnAppear()
    {
        _model.Visible = true;
        await _model.Reload();
    }

    protected override void OnDisappear() => _model.Visible = false;

    protected override UIElement Build()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new VCSOverviewToolbar(_model).At(0, 0));
        g.Children.Add(new VCSBatchPanel(_model).At(1, 0));
        g.Children.Add(new PPDivider(subtle: true).At(2, 0));
        g.Children.Add(new VCSOverviewContent(_model).At(3, 0));
        return g;
    }
}

internal sealed class VCSOverviewToolbar : PPView
{
    private readonly VCSOverviewModel _model;

    public VCSOverviewToolbar(VCSOverviewModel model)
    {
        _model = model;
        Observe(model, nameof(VCSOverviewModel.Entries), nameof(VCSOverviewModel.Loading), nameof(VCSOverviewModel.Batch),
            nameof(VCSOverviewModel.OnlyChanged), VCSOverviewModel.StatsProperty);
    }

    protected override UIElement Build()
    {
        var s = _model.Stats;
        bool running = _model.Batch?.Running == true;
        bool none = _model.Entries.Count == 0;
        var stats = HStack(M.Md, Text(L.T("vcs.overview.stats", s.Repos, s.Changed), Typo.BodyStrong).Shrink(false),
            s.ToPush > 0 ? HStack(2, new PPIcon("arrow-up", 12), Text(L.T("vcs.overview.toPush", s.ToPush), Typo.Small).Shrink(false)).Fg(ColorToken.Success) : null,
            s.ToPull > 0 ? HStack(2, new PPIcon("arrow-down", 12), Text(L.T("vcs.overview.toPull", s.ToPull), Typo.Small).Shrink(false)).Fg(ColorToken.Info) : null,
            _model.Loading ? new PPSpinner(M.IconSmall) : null,
            Spacer(M.Md),
            new PPButton(L.T("vcs.overview.fetchAll"), () => _model.StartBatch(VCSBatch.BatchKind.Fetch), icon: "cloud-download", size: PPButtonSize.Small)
                .Disabled(running || none).Id("vcs.overview.fetchAll"),
            new PPButton(L.T("vcs.overview.pullAll"), () => _model.StartBatch(VCSBatch.BatchKind.Pull), icon: "arrow-down-to-line", size: PPButtonSize.Small)
                .Disabled(running || none).Id("vcs.overview.pullAll"),
            new PPButton(L.T("vcs.overview.pushAll"), () => _model.StartBatch(VCSBatch.BatchKind.Push), icon: "arrow-up", size: PPButtonSize.Small)
                .Disabled(running || none).Id("vcs.overview.pushAll"),
            new PPInfoButton(L.T("vcs.overview.batchInfo")),
            new PPIconButton("refresh-cw", L.T("vcs.refresh"), async () => await _model.Reload(), size: M.IconButtonSmall, iconSize: M.IconSmall).Id("vcs.overview.refresh"));
        var search = Keep("search", () => new PPTextField(L.T("vcs.overview.search"), Bind.Of(() => _model.Query, v => _model.Query = v), icon: "search", clearable: true,
            height: M.ControlHeightSmall).W(260).Shrink().Id("vcs.overview.search"));
        UI.Detach(search);
        var filters = HStack(M.Md, search,
            VCSViews.Small(new PPCheckbox(Bind.Of(() => _model.OnlyChanged, v => _model.OnlyChanged = v), L.T("vcs.overview.onlyChanged"))).Id("vcs.overview.onlyChanged"),
            Spacer(),
            new PPIconButton("chevrons-up-down", L.T("vcs.changes.expandAll"), () => _model.Expanded = new HashSet<string>(_model.Entries.Select(e => e.Root)),
                size: M.IconButtonSmall, iconSize: M.IconSmall).Id("vcs.overview.expandAll"),
            new PPIconButton("chevrons-down-up", L.T("vcs.changes.collapseAll"), () => _model.Expanded = new HashSet<string>(), size: M.IconButtonSmall, iconSize: M.IconSmall));
        var col = VStack(M.Sm, stats, filters);
        col.Align = CrossAlign.Stretch;
        col.Padding = new Thickness(M.Xl, M.Sm, M.Xl, M.Sm);
        return col;
    }
}

// MARK: - 批量进度

internal sealed class VCSBatchPanel : PPView
{
    private readonly VCSOverviewModel _model;

    public VCSBatchPanel(VCSOverviewModel model)
    {
        _model = model;
        Observe(model, nameof(VCSOverviewModel.Batch));
    }

    protected override UIElement Build()
    {
        if (_model.Batch is not { } batch) return new FrameworkElement();
        var head = HStack(M.Sm,
            batch.Running ? new PPSpinner(M.IconSmall) : null,
            Text(VCSBatch.Title(batch.Kind), Typo.BodyStrong).Shrink(false),
            Text($"{batch.Done}/{batch.Items.Count}", Typo.MonoSmall, ColorToken.TextSecondary).Shrink(false),
            new PPProgressBar(batch.Items.Count == 0 ? 0 : batch.Done * 100.0 / batch.Items.Count).W(220),
            Spacer(),
            !batch.Running ? new PPIconButton("x", L.T("common.close"), () => _model.Batch = null, size: M.IconButtonSmall, iconSize: M.IconSmall).Id("vcs.batch.close") : null);
        var items = VStack(2);
        items.Align = CrossAlign.Stretch;
        foreach (var item in batch.Items)
        {
            var r = HStack(M.Sm, StateIcon(item.State), Text(item.Name),
                !string.IsNullOrEmpty(item.Message) ? Text(item.Message, Typo.Small, item.State == VCSBatch.ItemState.Failed ? ColorToken.Danger : ColorToken.TextTertiary) : null,
                Spacer());
            r.Height = 20;
            r.Bg(ColorToken.Clear);
            var root = item.Root;
            items.Children.Add(r.OnClick(() => _model.Expand(root)));
        }
        var col = VStack(M.Sm, head, items);
        col.Align = CrossAlign.Stretch;
        return Box(col).Pad(M.Md).Rounded(M.RadiusLG).Bg(ColorToken.BgSubtle).Outline(ColorToken.Border).Margins(M.Xl, 0, M.Xl, M.Sm);
    }

    private static UIElement StateIcon(VCSBatch.ItemState s) => s switch
    {
        VCSBatch.ItemState.Pending => new PPIcon("circle", M.IconSmall, ColorToken.IconSecondary),
        VCSBatch.ItemState.Running => new PPSpinner(M.IconSmall),
        VCSBatch.ItemState.Ok => new PPIcon("circle-check", M.IconSmall, ColorToken.Success),
        VCSBatch.ItemState.Failed => new PPIcon("circle-x", M.IconSmall, ColorToken.Danger),
        _ => new PPIcon("circle-minus", M.IconSmall, ColorToken.IconSecondary),
    };
}

// MARK: - 内容

internal sealed class VCSOverviewContent : PPView
{
    private readonly VCSOverviewModel _model;
    private readonly NavStore _nav = AppModel.Shared.Nav;

    public VCSOverviewContent(VCSOverviewModel model)
    {
        _model = model;
        Observe(model, nameof(VCSOverviewModel.Entries), nameof(VCSOverviewModel.Loaded), nameof(VCSOverviewModel.Error), nameof(VCSOverviewModel.Query),
            nameof(VCSOverviewModel.OnlyChanged), VCSOverviewModel.StatsProperty);
        Observe(_nav, nameof(NavStore.Search));
    }

    protected override UIElement Build()
    {
        if (!_model.Loaded) return new PPSpinner().Center();
        if (_model.Error is { } e && _model.Entries.Count == 0)
            return new PPAlert("danger", L.T("vcs.overview.failed"), e, new PPButton(L.T("connection.retry"), async () => await _model.Reload(), size: PPButtonSize.Small))
                .Margins(M.Xl).Top();
        if (_model.Entries.Count == 0) return new PPEmptyState("folder-git", L.T("vcs.overview.empty"));
        var list = _model.Filtered(_nav.Search);
        if (list.Count == 0)
            return new PPEmptyState("circle-check", _model.OnlyChanged ? L.T("vcs.overview.allClean") : L.T("vcs.overview.noMatch"));
        var col = VStack(M.Md);
        col.Align = CrossAlign.Stretch;
        foreach (var entry in list)
        {
            var en = entry;
            var section = Keep("sec:" + en.Root + "|" + en.Error, () => new VCSOverviewSection(_model, en));
            UI.Detach(section);
            col.Children.Add(section);
        }
        col.Padding = new Thickness(M.Xl, M.Lg, M.Xl, M.Lg);
        return Scroll(col).Key("vcs.overview.scroll");
    }
}

// MARK: - 一个仓库

internal sealed class VCSOverviewSection : PPView
{
    private readonly VCSOverviewModel _model;
    private readonly VCSOverviewModel.Entry _entry;
    private readonly VCSRepo _repo;

    public VCSOverviewSection(VCSOverviewModel model, VCSOverviewModel.Entry entry)
    {
        _model = model;
        _entry = entry;
        _repo = model.Repo(entry);
        Observe(_repo, nameof(VCSRepo.Status), nameof(VCSRepo.Notice), nameof(VCSRepo.Activity));
        Observe(model, nameof(VCSOverviewModel.Expanded));
    }

    private bool Expanded => _model.Expanded.Contains(_entry.Root);

    protected override UIElement Build()
    {
        var col = VStack(0, Header());
        col.Align = CrossAlign.Stretch;
        if (_repo.Notice is { } n) col.Children.Add(Keep("notice:" + n.Id, () => new VCSNoticeBar(_repo, n)).Margins(M.Md, 0, M.Md, M.Sm));
        if (Expanded)
        {
            col.Children.Add(new PPDivider(subtle: true));
            if (_repo.Status != null) col.Children.Add(Keep("body", () => new VCSOverviewBody(_model, _entry, _repo)));
            else if (_entry.Error is { } e) col.Children.Add(new PPAlert("danger", L.T("vcs.status.failed"), e.Length == 0 ? null : e).Margins(M.Md));
            else col.Children.Add(new PPSpinner(M.IconSmall).Margins(M.Md));
        }
        return Box(col).Rounded(M.RadiusLG).Bg(ColorToken.BgSurface).Outline(ColorToken.Border).Id("vcs.overview.section." + _entry.Name);
    }

    private UIElement Header()
    {
        var s = _repo.Status;
        bool busy = _repo.Activity != null;
        var row = HStack(M.Sm,
            new PPIcon(Expanded ? "chevron-down" : "chevron-right", 12, ColorToken.IconSecondary),
            new PPIcon("folder-git", M.Icon, ColorToken.IconSecondary),
            Text(_entry.Name, Typo.BodyStrong).Shrink(false),
            _entry.Subtitle is { } sub ? TextMiddle(sub, Typo.Small, ColorToken.TextTertiary) : null);
        if (s != null)
        {
            var bb = Keep("branch", () => new VCSBranchButton(_repo, showCounts: false, compact: true));
            UI.Detach(bb);
            row.Children.Add(bb);
            if (s.Ahead > 0)
                row.Children.Add(HStack(2, new PPIcon("arrow-up", 12), Text(s.Ahead.ToString(CultureInfo.InvariantCulture), Typo.MonoSmall).Shrink(false))
                    .Fg(ColorToken.Success).Bg(ColorToken.Clear).Tip(L.T("vcs.overview.aheadHelp", s.Ahead)));
            if (s.Behind > 0)
                row.Children.Add(HStack(2, new PPIcon("arrow-down", 12), Text(s.Behind.ToString(CultureInfo.InvariantCulture), Typo.MonoSmall).Shrink(false))
                    .Fg(ColorToken.Info).Bg(ColorToken.Clear).Tip(L.T("vcs.overview.behindHelp", s.Behind)));
            if (s.UpstreamGone) row.Children.Add(new PPIcon("triangle-alert", 12, ColorToken.Warning).Tip(L.T("vcs.branch.upstreamGone")));
            else if (s.Upstream == null && !s.Detached && !s.Unborn) row.Children.Add(new PPIcon("cloud-off", 12, ColorToken.TextTertiary).Tip(L.T("vcs.overview.noUpstream")));
            if (s.Operation is { } op) row.Children.Add(new PPBadge(L.T("vcs.overview.inProgress", VCSStyle.OperationTitle(op)), tone: "warning"));
            if (s.Conflicts > 0) row.Children.Add(new PPBadge(L.T("vcs.overview.conflicts", s.Conflicts), tone: "danger"));
            var changed = s.Files.Count(f => !f.Conflicted);
            if (changed > 0) row.Children.Add(new PPBadge(L.T("vcs.overview.changes", changed), tone: "info"));
        }
        else if (_entry.Error is { } e)
        {
            row.Children.Add(new PPIcon("circle-alert", M.IconSmall, ColorToken.Danger).Tip(e));
        }
        row.Children.Add(Spacer(M.Md));
        if (_repo.Activity != null) row.Children.Add(Keep("activity", () => new VCSActivityIndicator(_repo)));
        PPIconButton B(string icon, string help, Action act, bool isBusy = false) => new(icon, help, act, size: M.IconButtonSmall, iconSize: M.IconSmall, busy: isBusy);
        row.Children.Add(B("cloud-download", L.T("vcs.fetch"), () => VCSRemoteActions.Fetch(_repo), _repo.Activity?.Op == VCSOp.Fetch).Disabled(busy || s == null));
        row.Children.Add(B("arrow-down-to-line", L.T("vcs.pull"), () => VCSOverviewActions.Pull(_repo), _repo.Activity?.Op == VCSOp.Pull).Disabled(busy || s == null)
            .Id("vcs.overview.pull." + _entry.Name));
        row.Children.Add(B("arrow-up", L.T("vcs.push"), () => VCSOverviewActions.Push(_repo), _repo.Activity?.Op == VCSOp.Push).Disabled(busy || s == null)
            .Id("vcs.overview.push." + _entry.Name));
        row.Children.Add(B("refresh-cw", L.T("vcs.refresh"), async () => await _repo.LoadStatus()));
        row.Children.Add(new PPMenuButton(Menu, VCSViews.MenuGlyph("ellipsis"), PopupAlignment.Trailing, L.T("common.more")).Id("vcs.overview.more." + _entry.Name));
        row.Padding = new Thickness(M.Md, 0, M.Md, 0);
        row.Height = 40;
        row.Bg(ColorToken.Clear);
        return row.OnClick(() => _model.Toggle(_entry.Root)).Id("vcs.overview.header." + _entry.Name);
    }

    private IEnumerable<MenuEntry> Menu()
    {
        var files = (_repo.Status?.Files ?? new List<VCSFileChange>()).Where(f => !f.Conflicted).ToList();
        var e = new List<MenuEntry>
        {
            MenuEntry.Item(L.T("vcs.overview.openInProject"), () => VCSSession.Of(_entry.ProjectId).Reveal(_entry.Root, VCSPage.Changes), icon: "git-branch"),
            MenuEntry.Item(L.T("vcs.copyRepoPath"), () => Actions.Copy(_entry.Root), icon: "copy"),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("vcs.pull.ellipsis"), () => VCSPullDialog.Present(_repo), icon: "arrow-down-to-line"),
            MenuEntry.Item(L.T("vcs.push.ellipsis"), () => VCSPushDialog.Present(_repo), icon: "arrow-up"),
            MenuEntry.Item(L.T("vcs.branch.new"), () => VCSBranchActions.NewBranch(_repo, null, null), icon: "git-branch"),
            MenuEntry.Item(L.T("vcs.stash.new"), () => VCSStashDialogs.PresentNew(_repo, null), icon: "archive", disabled: files.Count == 0),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("vcs.overview.discardAll"), () => VCSDiscardDialog.Present(_repo, files, null), icon: "undo-2", destructive: true, disabled: files.Count == 0),
        };
        // 在资源管理器 / 终端中打开是本机操作：连着远程服务器时隐藏
        if (Actions.LocalOnlyAvailable)
            e.InsertRange(1, new[]
            {
                MenuEntry.Item(L.T("vcs.revealInFinder"), () => Actions.RevealInExplorer(_entry.Root), icon: "folder-open"),
                MenuEntry.Item(L.T("vcs.overview.openInTerminal"), () => VCSOverviewActions.OpenInTerminal(_entry.Root), icon: "square-terminal"),
            });
        var plugin = VCSPluginActions.Entries(ActionLocation.VcsToolbar, _repo, new Dictionary<string, JToken>());
        if (plugin.Count > 0)
        {
            e.Add(MenuEntry.Divider);
            e.AddRange(plugin);
        }
        return e;
    }
}

/// <summary>展开后：提交信息、提交按钮、改动文件（冲突 / 改动 / 未跟踪）</summary>
internal sealed class VCSOverviewBody : PPView
{
    private const int FileLimit = 200;
    private readonly VCSOverviewModel _model;
    private readonly VCSOverviewModel.Entry _entry;
    private readonly VCSRepo _repo;
    private PPButton? _commit;
    private PPButton? _commitPush;

    private VCSChangesModel Changes => _repo.Changes;
    private VCSCommitDraft Draft => _repo.Changes.Draft;

    public VCSOverviewBody(VCSOverviewModel model, VCSOverviewModel.Entry entry, VCSRepo repo)
    {
        _model = model;
        _entry = entry;
        _repo = repo;
        Observe(repo, nameof(VCSRepo.Status));
        Observe(repo.Changes, VCSChangesModel.ChecksProperty);
        Observe(repo.Changes.Draft, nameof(VCSCommitDraft.Committing));
        Observe(model, nameof(VCSOverviewModel.ShowAllFiles));
    }

    protected override void OnAppear()
    {
        Draft.MessageReplaced += SyncEditor;
        // 同一份草稿也可能在工程版本控制页里改过
        if (_editor != null) SyncEditor();
    }

    protected override void OnDisappear() => Draft.MessageReplaced -= SyncEditor;

    private void SyncEditor() => Editor().Sync();

    private PPTextArea? _editor;

    private PPTextArea Editor() => _editor ??= MakeEditor();

    private PPTextArea MakeEditor()
    {
        var a = VCSMessageEditor.Make(Draft, L.T("vcs.commit.placeholder"), UpdateButtons).H(60).Id("vcs.overview.message." + _entry.Name);
        // Ctrl+Enter 只给正在编辑信息的这一段
        a.TextBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && KeyInput.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (CanCommit(Selected())) Commit(false);
            }
        };
        return a;
    }

    private int Selected()
    {
        if (_repo.Status is not { } st) return 0;
        var plan = Changes.CommitPlan(st.Files);
        return plan.Paths.Count + plan.Hunks.Count;
    }

    private bool CanCommit(int selected)
    {
        if (_repo.Status is not { } st || Draft.Committing || st.Conflicts != 0) return false;
        bool hasMessage = Draft.Message.Trim().Length > 0;
        bool merging = st.Operation == "merge";
        return (hasMessage || merging) && (selected > 0 || merging);
    }

    private void UpdateButtons()
    {
        bool can = CanCommit(Selected());
        if (_commit != null && !Draft.Committing) _commit.IsEnabled = can;
        if (_commitPush != null) _commitPush.IsEnabled = can && !Draft.Committing;
    }

    private void Commit(bool push) => _repo.Commit(push, byPaths: true, push: push ? () => VCSOverviewActions.Push(_repo) : null);

    protected override UIElement Build()
    {
        var status = _repo.Status ?? new VCSStatus();
        var col = VStack(M.Sm);
        col.Align = CrossAlign.Stretch;
        if (status.Files.Count == 0)
        {
            col.Children.Add(HStack(M.Sm, new PPIcon("circle-check", M.IconSmall, ColorToken.Success), Text(L.T("vcs.changes.clean"), Typo.Body, ColorToken.TextSecondary))
                .Margins(0, M.Xs, 0, M.Xs));
        }
        else
        {
            var g = Changes.GroupsOf(status.Files);
            int selected = Selected();
            var editor = Editor();
            UI.Detach(editor);
            col.Children.Add(editor);
            _commit = new PPButton(L.T("vcs.commit"), () => Commit(false), kind: PPButtonKind.Primary, size: PPButtonSize.Small, busy: Draft.Committing)
                .Tip(L.T("vcs.commit") + "  Ctrl+Enter").Id("vcs.overview.commit." + _entry.Name);
            _commitPush = new PPButton(L.T("vcs.commitAndPush"), () => Commit(true), size: PPButtonSize.Small);
            col.Children.Add(HStack(M.Sm, _commit, _commitPush, Text(L.T("vcs.commit.selectedCount", selected), Typo.Small, ColorToken.TextTertiary), Spacer()));
            UpdateButtons();
            col.Children.Add(Files(g, status));
        }
        col.Padding = new Thickness(M.Md);
        return col;
    }

    private UIElement Files(VCSChangesModel.Groups g, VCSStatus status)
    {
        bool showAll = _model.ShowAllFiles.Contains(_entry.Root);
        int limit = showAll ? int.MaxValue : FileLimit;
        int total = g.Conflicts.Count + g.Tracked.Count + g.Untracked.Count;
        var col = VStack(0);
        col.Align = CrossAlign.Stretch;
        Group(col, L.T("vcs.changes.conflicts"), g.Conflicts, VCSChangeSide.Conflict, false, limit);
        Group(col, L.T("vcs.changes.changes"), g.Tracked, VCSChangeSide.Head, true, Math.Max(0, limit - g.Conflicts.Count));
        Group(col, L.T("vcs.changes.untracked"), g.Untracked, VCSChangeSide.Untracked, true, Math.Max(0, limit - g.Conflicts.Count - g.Tracked.Count));
        if (status.UntrackedTruncated)
        {
            var r = HStack(M.Sm, new PPIcon("triangle-alert", M.IconSmall, ColorToken.Warning),
                Text(L.T("vcs.changes.untrackedTruncated", status.UntrackedCount), Typo.Small, ColorToken.TextSecondary),
                new PPInfoButton(L.T("vcs.changes.untrackedTruncated.info")));
            r.Padding = new Thickness(M.Sm, 0, 0, 0);
            r.Height = M.RowHeightCompact;
            col.Children.Add(r);
        }
        if (total > limit)
            col.Children.Add(new PPLink(L.T("vcs.overview.showAllFiles", total), () => _model.ShowAllFiles = new HashSet<string>(_model.ShowAllFiles) { _entry.Root })
                .Margins(M.Sm, M.Xs, 0, 0).Left());
        return col;
    }

    private void Group(Stack col, string title, List<VCSFileChange> files, string side, bool checkable, int budget)
    {
        if (files.Count == 0) return;
        col.Children.Add(VCSViews.GroupHeader(title, files.Count, accessory: checkable ? new VCSTriCheckbox(Changes.GroupState(files), () => Changes.ToggleGroup(files)) : null));
        foreach (var f in files.Take(Math.Max(budget, 0)))
            col.Children.Add(new VCSOverviewFileRow(_repo, f, side, checkable));
    }
}

internal sealed class VCSOverviewFileRow : Border
{
    public VCSOverviewFileRow(VCSRepo repo, VCSFileChange file, string side, bool checkable)
    {
        var c = repo.Changes;
        var name = Text(VCSPath.Name(file.Path), Typo.Body, VCSStyle.Color(file.Status)).Shrink(false);
        if (file.Status == "D") name.TextDecorations = TextDecorations.Strikethrough;
        var row = HStack(M.Sm,
            checkable ? new VCSTriCheckbox(c.IsIncluded(file), () => c.SetIncluded(file, c.IsIncluded(file) != true)) : (UIElement)Gap(14),
            VCSViews.StatusLetter(file.Status),
            name,
            file.OldPath is { } old && old != file.Path ? TextMiddle("← " + old, Typo.Small, ColorToken.TextTertiary) : null,
            TextMiddle(VCSPath.Dir(file.Path), Typo.Small, ColorToken.TextTertiary),
            side == VCSChangeSide.Conflict ? Text(VCSStyle.ConflictTitle(file.ConflictType), Typo.Caption, ColorToken.Danger) : null,
            Spacer());
        row.VerticalAlignment = VerticalAlignment.Center;
        Child = row;
        Padding = new Thickness(M.Sm, 0, M.Sm, 0);
        Height = M.RowHeightCompact;
        CornerRadius = new CornerRadius(M.RadiusSM);
        SnapsToDevicePixels = true;
        MouseEnter += (_, _) => this.Bg(ColorToken.BgHover);
        MouseLeave += (_, _) => this.Bg(ColorToken.Clear);
        this.Bg(ColorToken.Clear);
        this.OnClick(() => VCSDiffDialog.PresentWorking(repo, file, side));
        this.Tip(file.Path);
        this.PPContextMenu(() => VCSFileMenu.Entries(repo, new List<VCSChangeSelection> { new(file.Path, side) }));
    }
}

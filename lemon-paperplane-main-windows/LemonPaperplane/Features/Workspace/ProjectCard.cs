using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 工程卡片（规范第 6 节）：状态点、名称、问题数、更多菜单；路径；facet 徽标；info；运行配置下拉 + 运行 / 停止 / 重启；
/// 运行地址与资源；Git 摘要；忙碌状态。悬停描边 borderStrong + card 阴影；点卡片进入工程详情，右键与「⋯」同一个菜单。
/// 网格重建时卡片实例按工程 id 复用（Update 换新摘要），打开着的菜单、问题弹出框、资源悬浮详情不会被打断。对应 Mac 的 ProjectCard。
/// </summary>
public sealed class ProjectCard : PPView
{
    private readonly NavStore _nav = AppModel.Shared.Nav;
    private readonly DataStore _data = AppModel.Shared.Data;
    private readonly RunStore _runs = AppModel.Shared.Runs;
    private readonly bool _showWorkspace;
    private ProjectSummary _p;

    public ProjectCard(ProjectSummary project, bool showWorkspace = false)
    {
        _p = project;
        _showWorkspace = showWorkspace;
        Observe(_nav, nameof(NavStore.SelectedConfig));
        if (showWorkspace) Observe(_data, nameof(DataStore.Workspaces));
    }

    public ProjectSummary Project => _p;

    /// <summary>换成新的工程摘要（摘要是不可变记录，引用变了才重建）。</summary>
    public void Update(ProjectSummary p)
    {
        if (ReferenceEquals(_p, p)) return;
        _p = p;
        Invalidate();
    }

    private string? SelectedConfigId
    {
        get
        {
            if (_nav.SelectedConfig.TryGetValue(_p.Id, out var c) && _p.Configs.Any(x => x.Id == c)) return c;
            return _p.DefaultConfigId ?? _p.Configs.FirstOrDefault()?.Id;
        }
    }

    private RunBrief? CurrentRun
    {
        get
        {
            if (SelectedConfigId is { } c && _p.Runs.FirstOrDefault(r => r.ConfigId == c) is { } r) return r;
            return _p.Runs.FirstOrDefault();
        }
    }

    protected override UIElement Build()
    {
        var p = _p;
        var col = VStack(M.Md, Header());
        col.Align = CrossAlign.Stretch;
        if (p.Facets.Count > 0)
            col.Children.Add(Flow(M.Xs, p.Facets.Take(6).Select(f => new FacetBadgeView(f.Name, f.Icon, f.Plugin))));
        if (p.Info.Count > 0)
            col.Children.Add(HStack(M.Md, p.Info.Take(3).Select(i =>
                HStack(M.Xs, Text(i.Label, Typo.Small, ColorToken.TextTertiary), Text(i.Value, Typo.Small, ColorToken.TextSecondary)).Shrink())));
        col.Children.Add(new PPDivider(subtle: true));
        col.Children.Add(RunArea());
        if (p.Vcs is { } vcs && !string.IsNullOrEmpty(vcs.Branch)) col.Children.Add(GitLine(vcs));
        if (!string.IsNullOrEmpty(p.Busy))
            col.Children.Add(HStack(M.Sm, new PPSpinner(M.IconSmall, ColorToken.Accent), Text(p.Busy, Typo.Small, ColorToken.TextSecondary)));

        var card = new PPCard(col, hoverable: true);
        card.OnClick(() => _nav.OpenProject(_p.Id));
        card.PPContextMenu(() => CardMenu.Entries(_p));
        card.A11y(p.Name);
        return card.Id("project.card." + p.Id);
    }

    private UIElement Header()
    {
        var p = _p;
        var sub = HStack(M.Xs);
        if (_showWorkspace && _data.Workspace(p.WorkspaceId) is { } ws)
        {
            sub.Children.Add(Text(ws.Name, Typo.Small, ColorToken.TextSecondary));
            sub.Children.Add(Text("·", Typo.Small, ColorToken.TextTertiary).Shrink(false));
        }
        sub.Children.Add(TextMiddle(Format.AbbreviatePath(p.Path), Typo.Small, ColorToken.TextTertiary));
        var titles = VStack(2, Text(p.Name, Typo.Title), sub);
        titles.Align = CrossAlign.Stretch;

        var problems = Keep("problems", () => new ProblemsIndicator(p), i => i.Update(_p));
        var more = Keep("more", () => new PPMenuButton(() => CardMenu.Entries(_p),
            Box(new PPIcon("ellipsis", M.Icon, ColorToken.IconSecondary).Center()).Size(M.IconButtonSmall, M.IconButtonSmall).Bg(ColorToken.Clear),
            PopupAlignment.Trailing, L.T("common.more")).Id("card.more"), m => m.ToolTip = L.T("common.more"));
        var row = HStack(M.Sm,
            new StatusDot(p.Status).Margins(0, 5, 0, 0).Top(),
            titles.Shrink(),
            Spacer(M.Xs),
            problems.Top(),
            more.Top());
        row.Align = CrossAlign.Start;
        return row;
    }

    private UIElement RunArea()
    {
        var p = _p;
        if (p.Configs.Count == 0)
            return Box(Text(L.T("card.noConfig"), Typo.Small, ColorToken.TextTertiary).VCenter()).H(M.ControlHeightSmall);
        var cid = SelectedConfigId;
        var run = cid != null ? p.Runs.FirstOrDefault(r => r.ConfigId == cid && RunState.IsActive(r.State)) : null;
        var options = p.Configs.Select(c => new DropdownOption<string>(c.Id, c.Name, iconRef: IconKey.Parse(c.Icon)));
        var dropdown = new PPDropdown<string>(options, Bind.Of(() => cid ?? "", v => _nav.SelectConfig(p.Id, v)),
            size: PPButtonSize.Small, minWidth: 100, maxWidth: 220).Id("card.config");
        var controls = HStack(M.Xs, dropdown, Spacer(M.Xs));
        if (run != null)
        {
            var runId = run.Id;
            controls.Children.Add(new PPIconButton("rotate-cw", L.T("action.restart"), () => Actions.Restart(runId), size: M.IconButtonSmall).Id("card.restart"));
            controls.Children.Add(new PPIconButton("square", L.T("action.stop"), () => Actions.Stop(runId), size: M.IconButtonSmall, tint: ColorToken.Danger).Id("card.stop"));
        }
        else
        {
            controls.Children.Add(new PPIconButton("play", L.T("action.run"), () => Actions.Run(p.Id, cid), size: M.IconButtonSmall, tint: ColorToken.Success).Id("card.run"));
        }
        var col = VStack(M.Sm, controls);
        col.Align = CrossAlign.Stretch;
        // 运行中：地址 + 资源；没在运行：「未运行 · 上次运行 …」占住同一行，卡片高度不随启停跳动
        if (CurrentRun is { } r && RunState.IsActive(r.State))
        {
            UIElement address = r.Urls.FirstOrDefault() is { } url
                ? new PPLink(Format.ShortURL(url), () => Actions.OpenURL(url), mono: true, icon: "external-link").Shrink()
                : Text(RunStatusStyle.Title(r.State), Typo.Small, ColorToken.TextSecondary);
            // 只有这一小块随 stats 重绘（每个运行一个可观察对象），卡片本身不动
            var started = _runs.Runs.TryGetValue(r.Id, out var full) ? full.StartedAt : null;
            var res = Keep("res:" + r.Id, () => new RunResourceInline(r.Id, started));
            col.Children.Add(HStack(M.Md, address, Spacer(M.Xs), res).H(StatusLineHeight));
        }
        else
        {
            col.Children.Add(Keep("idle", () => new IdleRunLine(), l => l.LastRun = _p.LastRun).H(StatusLineHeight));
        }
        return col;
    }

    /// <summary>运行状态行（地址 + 资源 / 未运行）的固定高度，和 Mac 一致。</summary>
    public const double StatusLineHeight = 18;

    private static UIElement GitLine(VCSBrief v)
    {
        var row = HStack(M.Sm,
            new PPIcon("git-branch", 12, ColorToken.IconSecondary),
            TextMiddle(v.Branch, Typo.Small),
            v.Ahead > 0 ? HStack(1, new PPIcon("arrow-up", 11), Text(v.Ahead.ToString(), Typo.Small).Shrink(false)) : null,
            v.Behind > 0 ? HStack(1, new PPIcon("arrow-down", 11), Text(v.Behind.ToString(), Typo.Small).Shrink(false)) : null,
            v.Changes > 0 ? Text(L.T("card.changes", v.Changes), Typo.Small, ColorToken.TextTertiary).Shrink(false) : null,
            v.Conflicts > 0 ? new PPBadge(L.T("card.conflicts", v.Conflicts), tone: "danger") : null,
            Spacer());
        return row.Fg(ColorToken.TextSecondary).Id("card.git");
    }
}

/// <summary>卡片右键 / 更多菜单（对应 Mac CardMenu）。</summary>
public static class CardMenu
{
    public static IEnumerable<MenuEntry> Entries(ProjectSummary p)
    {
        var model = AppModel.Shared;
        var facetIds = new HashSet<string>(p.Facets.Select(f => f.Id));
        var plugins = new HashSet<string>(p.Facets.Select(f => f.Plugin));
        var actions = Actions.Applicable(model.Data.Contributions.ActionsAt(ActionLocation.CardMenu), facetIds, plugins);
        var others = model.Data.Workspaces.Where(w => w.Id != p.WorkspaceId).ToList();
        var e = new List<MenuEntry>
        {
            MenuEntry.Item(L.T("card.open"), () => model.Nav.OpenProject(p.Id), icon: "arrow-right"),
            MenuEntry.Item(L.T("open.finder"), () => Actions.Open(p.Id, "finder"), icon: "folder-open"),
            MenuEntry.Item(L.T("open.terminal"), () => Actions.Open(p.Id, "terminal"), icon: "square-terminal"),
            MenuEntry.Item(L.T("open.idea"), () => Actions.Open(p.Id, "idea"), icon: "code-xml"),
            MenuEntry.Item(L.T("open.vscode"), () => Actions.Open(p.Id, "vscode"), icon: "code-xml"),
            MenuEntry.Item(L.T("editorWindow.open"), () => EditorWindows.Shared.Open(p.Id), icon: "file-pen"),
            MenuEntry.Divider,
        };
        if (p.HasActiveRun) e.Add(MenuEntry.Item(L.T("card.stopAll"), () => Actions.StopProject(p.Id), icon: "square"));
        e.Add(MenuEntry.Item(L.T("project.reimport"), () => Actions.Reimport(p.Id), icon: "refresh-cw"));
        if (actions.Count > 0)
        {
            e.Add(MenuEntry.Divider);
            e.AddRange(Actions.MenuEntries(actions, p.Id));
        }
        e.Add(MenuEntry.Divider);
        e.Add(MenuEntry.Submenu(L.T("card.moveTo"), others.Select(ws => MenuEntry.Item(ws.Name, () => Actions.Move(p.Id, ws.Id), icon: "layers")), icon: "folder-symlink"));
        e.Add(MenuEntry.Item(L.T("project.rename"), () => Actions.Rename(p), icon: "pencil"));
        e.Add(MenuEntry.Item(L.T("project.remove"), () => Actions.Remove(p), icon: "trash-2", destructive: true));
        return e;
    }
}

/// <summary>问题提示：缺工具链 / 依赖未装 / 诊断的数量，点开列出并可一键修复（对应 Mac ProblemsIndicator）。</summary>
public sealed class ProblemsIndicator : PPView
{
    private ProjectSummary _p;
    private readonly State<bool> _open;

    public ProblemsIndicator(ProjectSummary project)
    {
        _p = project;
        _open = new State<bool>(false);
        this.PPPopover(_open, () => new ProblemsPopover(_p.Id, () => _open.Value = false), PopupAlignment.Trailing);
    }

    public void Update(ProjectSummary p)
    {
        if (ReferenceEquals(_p, p)) return;
        _p = p;
        Invalidate();
    }

    private int Count => _p.Problems + _p.MissingToolchains.Count + (_p.Dependencies is "missing" or "outdated" ? 1 : 0);

    protected override UIElement Build()
    {
        var count = Count;
        if (count <= 0) return Gap(0);
        var sev = _p.ProblemSeverity ?? (_p.MissingToolchains.Count == 0 ? Severity.Warning : Severity.Error);
        var c = Theme.Severity(sev);
        var label = Box(HStack(3,
                new PPIcon(sev == Severity.Error ? "circle-alert" : "triangle-alert", M.IconSmall, c.Fg),
                Text(Math.Max(count, _p.Problems).ToString(), Typo.Small, c.Fg).TabularDigits().Shrink(false)))
            .Pad(M.Sm, 0).H(M.IconButtonSmall).Rounded(M.RadiusSM).Bg(c.Bg)
            .Tip(L.T("problems.title")).A11y(L.T("problems.title")).Id("card.problems");
        label.OnClick(() =>
        {
            if (!_open.Value && PopupController.WasJustDismissed(this)) return;
            _open.Value = !_open.Value;
        });
        return label;
    }
}

/// <summary>问题列表（project.problems），每条可修复；修复后关闭（对应 Mac ProblemsPopover）。</summary>
public sealed class ProblemsPopover : PPView
{
    private readonly string _projectId;
    private readonly Action _close;
    private readonly State<List<Diagnosis>?> _problems;
    private readonly State<string?> _error;

    public ProblemsPopover(string projectId, Action close)
    {
        _projectId = projectId;
        _close = close;
        _problems = UseState<List<Diagnosis>?>(null);
        _error = UseState<string?>(null);
        Width = 420;
    }

    protected override async void OnAppear()
    {
        if (_problems.Value != null) return;
        try { _problems.Value = await AppModel.Shared.Client().ProblemsAsync(_projectId); }
        catch (Exception e) { _error.Value = e.Message; }
    }

    protected override UIElement Build()
    {
        UIElement body;
        if (_problems.Value is { } list)
        {
            if (list.Count == 0) body = Text(L.T("problems.none"), Typo.Body, ColorToken.TextSecondary).Pad(M.Lg);
            else
            {
                var rows = VStack(M.Xs, list.Select(d => new DiagnosisRow(d, null, _projectId, _close, compact: true)));
                rows.Align = CrossAlign.Stretch;
                rows.Padding = new Thickness(M.Sm);
                body = Scroll(rows).Key("problems.scroll").MaxH(420);
            }
        }
        else if (_error.Value is { } err) body = Paragraph(err, Typo.Body, ColorToken.Danger).Pad(M.Lg);
        else body = Box(new PPSpinner().Left()).Pad(M.Lg);
        var col = VStack(0,
            Box(Text(L.T("problems.title"), Typo.Title).VCenter()).Pad(M.Md, 0).H(36),
            new PPDivider(subtle: true),
            body);
        col.Align = CrossAlign.Stretch;
        return col.Id("problems.popover");
    }
}

/// <summary>没在运行时的状态行：未运行 · 上次运行 3 小时前；上次异常退出用红色写退出码。每分钟刷新相对时间（对应 Mac IdleRunLine）。</summary>
public sealed class IdleRunLine : PPView
{
    private LastRun? _lastRun;
    private System.Windows.Threading.DispatcherTimer? _timer;

    public LastRun? LastRun
    {
        get => _lastRun;
        set { if (!Equals(_lastRun, value)) { _lastRun = value; Invalidate(); } }
    }

    protected override void OnAppear()
    {
        _timer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDisappear() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e) => Invalidate();

    protected override UIElement Build()
    {
        var row = HStack(M.Xs, new PPIcon("power", 12, ColorToken.IconSecondary), Text(L.T("card.idle"), Typo.Small, ColorToken.TextTertiary).Shrink(false));
        if (_lastRun is { EndedAt: { } at } lr)
        {
            row.Children.Add(Text("·", Typo.Small, ColorToken.TextTertiary).Shrink(false));
            var when = VCSDate.Short(at);
            row.Children.Add(lr.Failed
                ? Text(lr.ExitCode != 0 ? L.T("card.lastFailed", when, lr.ExitCode.ToString()) : L.T("card.lastStartFailed", when), Typo.Small, ColorToken.Danger)
                : Text(L.T("card.lastRun", when), Typo.Small, ColorToken.TextTertiary));
            if (!string.IsNullOrEmpty(lr.Name)) row.Tip(lr.Name);
        }
        row.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }
}

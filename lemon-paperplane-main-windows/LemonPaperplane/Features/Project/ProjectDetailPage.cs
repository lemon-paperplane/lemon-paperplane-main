// 工程详情（对应 Mac Features/Project/ProjectDetailPage.swift）：头部（返回、状态、名称、路径、技术标签、运行控件、插件动作、更多菜单）、
// 标签栏（概览、运行、文件、版本控制、配置 + 插件 project.tab）、标签内容。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>工程详情的数据：project.get，收到 project.changed 时合并刷新（300ms）。对应 Mac ProjectDetailModel。</summary>
public sealed class ProjectDetailModel : PPObservable
{
    private Project? _project;
    private string? _error;
    private readonly DispatcherTimer _debounce;
    private bool _subscribed;

    public string ProjectId { get; }
    public Project? Project { get => _project; set { _project = value; Notify(); } }
    public string? Error { get => _error; set => Set(ref _error, value); }

    public ProjectDetailModel(string projectId)
    {
        ProjectId = projectId;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Reload(); };
    }

    public void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        AppModel.Shared.Bus.ProjectChanged += OnProjectChanged;
    }

    public void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        AppModel.Shared.Bus.ProjectChanged -= OnProjectChanged;
        _debounce.Stop();
    }

    private void OnProjectChanged(string id)
    {
        if (id != ProjectId) return;
        _debounce.Stop();
        _debounce.Start();
    }

    public async Task Reload()
    {
        try
        {
            Project = await AppModel.Shared.Client().ProjectAsync(ProjectId);
            Error = null;
        }
        catch (Exception e)
        {
            if (_project == null) Error = e.Message;
        }
    }

    /// <summary>修改工具链覆盖：null 表示改回自动。</summary>
    public void SetToolchainOverride(string kind, ToolchainOverride? o) =>
        AppModel.Shared.Perform(async api =>
        {
            Project = await api.UpdateProjectSettingsAsync(ProjectId, toolchains: new Dictionary<string, ToolchainOverride?> { [kind] = o });
        });

    /// <summary>插件 project.tab 视图（按 facet / 插件过滤）。</summary>
    public List<ViewInfo> PluginTabs()
    {
        if (_project is not { } p) return new List<ViewInfo>();
        var facets = p.FacetIds;
        var plugins = p.PluginIds;
        return AppModel.Shared.Data.Contributions.ViewsAt(ViewLocation.ProjectTab)
            .Where(v => v.Facets.Count == 0 ? plugins.Contains(v.Plugin) : v.Facets.Any(facets.Contains)).ToList();
    }
}

/// <summary>工程详情页。</summary>
public sealed class ProjectDetailPage : PPView
{
    private readonly string _projectId;
    private readonly ProjectDetailModel _model;
    private readonly DataStore _data = AppModel.Shared.Data;
    private bool _exists;

    public ProjectDetailPage(string projectId)
    {
        _projectId = projectId;
        _model = new ProjectDetailModel(projectId);
        _exists = _data.Project(projectId) != null;
        // 只在工程出现 / 消失时重建；头部、标签栏、内容各自订阅需要的数据
        Observe(h =>
        {
            _projectsHandler = (_, e) =>
            {
                if (e.PropertyName is not (null or nameof(DataStore.Projects))) return;
                bool now = _data.Project(_projectId) != null;
                if (now == _exists) return;
                _exists = now;
                h(this, EventArgs.Empty);
            };
            _data.PropertyChanged += _projectsHandler;
        }, _ =>
        {
            if (_projectsHandler != null) _data.PropertyChanged -= _projectsHandler;
            _projectsHandler = null;
        });
    }

    private System.ComponentModel.PropertyChangedEventHandler? _projectsHandler;

    protected override void OnAppear()
    {
        _model.Subscribe();
        _ = _model.Reload();
    }

    protected override void OnDisappear() => _model.Unsubscribe();

    protected override UIElement Build()
    {
        if (_data.Project(_projectId) == null)
            return new PPEmptyState("folder-open", L.T("project.missing"), L.T("common.back"), () => AppModel.Shared.Nav.OpenProjectId = null);
        var col = VStack(0,
            Keep("header", () => new ProjectHeader(_projectId, _model)),
            Keep("tabs", () => new ProjectTabBarView(_projectId, _model)),
            new PPDivider(subtle: true),
            Keep("content", () => new ProjectTabContent(_projectId, _model)).Flex());
        col.Align = CrossAlign.Stretch;
        return col;
    }
}

/// <summary>标签栏：概览（问题数）、运行（运行中数）、文件、版本控制（改动数）、配置 + 插件标签。</summary>
internal sealed class ProjectTabBarView : PPView
{
    private readonly string _projectId;
    private readonly ProjectDetailModel _model;
    private readonly AppModel _app = AppModel.Shared;

    public ProjectTabBarView(string projectId, ProjectDetailModel model)
    {
        _projectId = projectId;
        _model = model;
        Observe(_app.Data, nameof(DataStore.Projects), nameof(DataStore.Contributions));
        Observe(_app.Nav, nameof(NavStore.ProjectTabSelection));
        Observe(model, nameof(ProjectDetailModel.Project));
    }

    protected override UIElement Build()
    {
        var s = _app.Data.Project(_projectId);
        if (s == null) return Gap(0);
        var tabs = new List<PPTab<string>>
        {
            new(ProjectTab.Overview, L.T("project.tab.overview"), icon: "layout-grid", badge: s.Problems > 0 ? s.Problems : null),
            new(ProjectTab.Run, L.T("project.tab.run"), icon: "play", badge: s.Runs.Count(r => RunState.IsActive(r.State))),
            new(ProjectTab.Files, L.T("files.tab"), icon: "folder-tree"),
            new(ProjectTab.Vcs, L.T("project.tab.vcs"), icon: "git-branch", badge: s.Vcs?.Changes),
            new(ProjectTab.Config, L.T("project.tab.config"), icon: "sliders-horizontal"),
        };
        foreach (var v in _model.PluginTabs())
            tabs.Add(new PPTab<string>(ProjectTab.Plugin(v.Key), v.Title, iconRef: IconKey.Parse(v.Icon, v.Plugin)));
        var nav = _app.Nav;
        var bar = new PPTabBar<string>(tabs, Bind.Of(() => nav.ProjectTabSelection, v => nav.ProjectTabSelection = v));
        return Box(HStack(0, bar, Spacer())).Pad(M.Lg, 0);
    }
}

/// <summary>标签内容：按选中的标签显示（每个标签一个视图，切换标签时上一个丢弃，和 Mac 一样）。</summary>
internal sealed class ProjectTabContent : PPView
{
    private readonly string _projectId;
    private readonly ProjectDetailModel _model;
    private readonly AppModel _app = AppModel.Shared;

    public ProjectTabContent(string projectId, ProjectDetailModel model)
    {
        _projectId = projectId;
        _model = model;
        Observe(_app.Nav, nameof(NavStore.ProjectTabSelection));
        Observe(model, nameof(ProjectDetailModel.Project), nameof(ProjectDetailModel.Error));
        Observe(_app.Data, nameof(DataStore.Contributions));
    }

    protected override UIElement Build()
    {
        if (_model.Project is { } p)
        {
            var tab = _app.Nav.ProjectTabSelection;
            switch (tab)
            {
                case ProjectTab.Overview:
                    return Box(Keep("overview", () => new OverviewTab(_model)));
                case ProjectTab.Run:
                    return Box(Keep("run", () => new RunTab(_model)));
                case ProjectTab.Files:
                    return Box(Keep("files", () => new FilesTab(p)));
                case ProjectTab.Vcs:
                    return Box(Keep("vcs", () => new VCSTabView(p.Id)));
                case ProjectTab.Config:
                    return Box(Keep("config", () => new ConfigTab(_model)));
                default:
                    if (tab.StartsWith("plugin:", StringComparison.Ordinal))
                    {
                        var key = tab.Substring(7);
                        var v = _model.PluginTabs().FirstOrDefault(x => x.Key == key);
                        if (v != null) return Box(Keep("plugin:" + key, () => new PluginTabContent(v, _model)));
                        return new PPEmptyState("puzzle", L.T("plugin.viewMissing"));
                    }
                    return Box(Keep("overview", () => new OverviewTab(_model)));
            }
        }
        if (_model.Error is { } e)
            return new PPEmptyState("circle-alert", e, L.T("connection.retry"), () => _ = _model.Reload());
        return Box(new PPSpinner().Center());
    }
}

/// <summary>工程标签页插件视图：多个模块符合时可切换模块。对应 Mac PluginTabContent。</summary>
internal sealed class PluginTabContent : PPView
{
    private readonly ViewInfo _view;
    private readonly ProjectDetailModel _model;
    private readonly State<string> _moduleId;

    public PluginTabContent(ViewInfo view, ProjectDetailModel model)
    {
        _view = view;
        _model = model;
        _moduleId = UseState("");
        Observe(model, nameof(ProjectDetailModel.Project));
    }

    protected override UIElement Build()
    {
        if (_model.Project is not { } project) return Gap(0);
        var v = _view;
        var modules = project.Modules.Where(m => v.Facets.Count == 0
            ? (m.Facets.Any(f => f.Plugin == v.Plugin) || m.Analysis.ContainsKey(v.Plugin))
            : m.HasAnyFacet(v.Facets)).ToList();
        var mid = modules.Any(m => m.Id == _moduleId.Value) ? _moduleId.Value : (modules.FirstOrDefault()?.Id ?? "");
        var col = VStack(0);
        col.Align = CrossAlign.Stretch;
        if (modules.Count > 1)
        {
            var dd = new PPDropdown<string>(modules.Select(m => new DropdownOption<string>(m.Id, m.Name, detail: string.IsNullOrEmpty(m.RelDir) ? null : m.RelDir)),
                Bind.Of(() => mid, x => _moduleId.Value = x), size: PPButtonSize.Small, minWidth: 200).Id("plugin.module");
            col.Children.Add(Box(HStack(0, dd, Spacer())).Pad(M.Xl, M.Md, M.Xl, 0));
        }
        var ctx = new ViewContext { Plugin = v.Plugin, View = v.Id, ProjectId = project.Id, ModuleId = mid.Length == 0 ? null : mid };
        col.Children.Add(Keep("view:" + mid, () => new PluginViewHost(v, ctx)).Flex());
        return col;
    }
}

/// <summary>详情页头部：返回、状态点、名称、路径、技术标签、运行控件、插件工具栏动作、更多菜单。对应 Mac ProjectHeader。</summary>
internal sealed class ProjectHeader : PPView
{
    private readonly string _projectId;
    private readonly ProjectDetailModel _model;
    private readonly AppModel _app = AppModel.Shared;
    private const double DotSize = 10;

    public ProjectHeader(string projectId, ProjectDetailModel model)
    {
        _projectId = projectId;
        _model = model;
        Observe(_app.Data, nameof(DataStore.Projects), nameof(DataStore.Contributions));
        Observe(_app.Nav, nameof(NavStore.SelectedConfig));
        Observe(model, nameof(ProjectDetailModel.Project));
    }

    private string? ConfigId(ProjectSummary s)
    {
        if (_app.Nav.SelectedConfig.TryGetValue(s.Id, out var c) && s.Configs.Any(x => x.Id == c)) return c;
        return s.DefaultConfigId ?? s.Configs.FirstOrDefault()?.Id;
    }

    private ISet<string> Facets(ProjectSummary s) => _model.Project?.FacetIds ?? new HashSet<string>(s.Facets.Select(f => f.Id));
    private ISet<string> Plugins(ProjectSummary s) => _model.Project?.PluginIds ?? new HashSet<string>(s.Facets.Select(f => f.Plugin));

    protected override UIElement Build()
    {
        var s = _app.Data.Project(_projectId);
        if (s == null) return Gap(0);
        var nav = _app.Nav;
        var path = new PPLink(Format.AbbreviatePath(s.Path), () => Actions.RevealInExplorer(s.Path), mono: true).Tip(L.T("open.finder")).Id("project.path");
        path.HorizontalAlignment = HorizontalAlignment.Left;
        var titleCol = VStack(3, Text(s.Name, Typo.Display).Id("project.name"), path).Shrink().MinW(120);
        var more = new PPMenuButton(() => MoreMenu(s),
            Box(new PPIcon("ellipsis", M.Icon, ColorToken.IconPrimary).Center()).Size(M.IconButton, M.IconButton).Bg(ColorToken.Clear),
            PopupAlignment.Trailing, L.T("common.more")).Id("project.more");
        var top = HStack(M.Md,
            new PPIconButton("arrow-left", L.T("common.back"), () => nav.OpenProjectId = null).Id("project.back"),
            new StatusDot(s.Status, DotSize),
            titleCol,
            Spacer(M.Lg),
            UrlButton(s),
            RunControls(s),
            ToolbarActions(s),
            more);
        var col = VStack(M.Sm, top);
        col.Align = CrossAlign.Stretch;
        // 技术标签单独占一行，跟工程名左对齐，放不下时换行
        if (s.Facets.Count > 0)
        {
            var flow = Flow(M.Xs, s.Facets.Select(f => new FacetBadgeView(f.Name, f.Icon, f.Plugin)));
            flow.Margin = new Thickness(M.IconButton + M.Md + DotSize + M.Md, 0, 0, 0);
            col.Children.Add(flow);
        }
        return Box(col).Pad(M.Lg, M.Md);
    }

    private RunBrief? ActiveRun(ProjectSummary s, string? cid) => s.Runs.FirstOrDefault(r => r.ConfigId == cid && RunState.IsActive(r.State));

    /// <summary>
    /// 运行中的地址（Mac 在运行控件里）。单独作为一个可压缩项：和工程名一起分剩余宽度，
    /// 工程名还能有 150 以上时地址完整显示，再窄就整个让出来（不裁成半截）。
    /// </summary>
    private UIElement? UrlButton(ProjectSummary s)
    {
        if (s.Configs.Count == 0 || ActiveRun(s, ConfigId(s))?.Urls.FirstOrDefault() is not { } url) return null;
        var b = new PPButton(Format.ShortURL(url), () => Actions.OpenURL(url), icon: "external-link", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("project.url");
        var h = new HideWhenTight(b).Shrink();
        h.Margin = new Thickness(0, 0, M.Xs - M.Md, 0); // 和下拉框之间用运行控件内部的间距
        return h;
    }

    /// <summary>
    /// 配置下拉 + 运行 / 重启 / 停止。直接放进头部这一行（组内间距 xs），下拉框可以压缩到最小 140，
    /// 窗口窄时先压它和地址，工程名留得住。
    /// </summary>
    private IEnumerable<UIElement> RunControls(ProjectSummary s)
    {
        if (s.Configs.Count == 0) yield break;
        var cid = ConfigId(s);
        var active = ActiveRun(s, cid);
        var nav = _app.Nav;
        var tight = new Thickness(M.Xs - M.Md, 0, 0, 0);
        yield return new PPDropdown<string>(s.Configs.Select(c => new DropdownOption<string>(c.Id, c.Name, iconRef: IconKey.Parse(c.Icon))),
            Bind.Of(() => cid ?? "", v => nav.SelectConfig(s.Id, v)), minWidth: 140, maxWidth: 240).Id("project.config").Shrink();
        if (active != null)
        {
            yield return new PPIconButton("rotate-cw", L.T("action.restart"), () => Actions.Restart(active.Id)) { Margin = tight }.Id("project.restart");
            yield return new PPIconButton("square", L.T("action.stop") + "  Ctrl+.", () => Actions.Stop(active.Id), tint: ColorToken.Danger) { Margin = tight }.Id("project.stop");
        }
        else
        {
            yield return new PPIconButton("play", L.T("action.run") + "  Ctrl+R", () => Actions.Run(s.Id, cid), tint: ColorToken.Success) { Margin = tight }.Id("project.run");
        }
    }

    private UIElement? ToolbarActions(ProjectSummary s)
    {
        var acts = Actions.Applicable(_app.Data.Contributions.ActionsAt(ActionLocation.ProjectToolbar), Facets(s), Plugins(s));
        if (acts.Count == 0) return null;
        var row = HStack(M.Md, new PPDivider(vertical: true).H(20));
        foreach (var a in acts)
        {
            var act = a;
            row.Children.Add(new PPIconButton(IconKey.Parse(act.Icon, act.Plugin) ?? IconKey.Builtin("zap"), act.Title, () => Actions.Invoke(act, s.Id)));
        }
        return row;
    }

    private IEnumerable<MenuEntry> MoreMenu(ProjectSummary s)
    {
        var acts = Actions.Applicable(_app.Data.Contributions.ActionsAt(ActionLocation.ProjectMenu), Facets(s), Plugins(s));
        var e = new List<MenuEntry>
        {
            MenuEntry.Item(L.T("project.reimport"), () => Actions.Reimport(s.Id), icon: "refresh-cw"),
            MenuEntry.Item(L.T("project.sync"), () => Actions.Sync(s.Id), icon: "refresh-cw"),
            MenuEntry.Item(L.T("editorWindow.open"), () => EditorWindows.Shared.Open(s.Id), icon: "file-pen"),
            MenuEntry.Submenu(L.T("project.openWith"), Actions.OpenWithEntries(s.Id), icon: "external-link"),
        };
        if (s.HasActiveRun) e.Add(MenuEntry.Item(L.T("card.stopAll"), () => Actions.StopProject(s.Id), icon: "square"));
        if (acts.Count > 0)
        {
            e.Add(MenuEntry.Divider);
            e.AddRange(Actions.MenuEntries(acts, s.Id));
        }
        e.Add(MenuEntry.Divider);
        e.Add(MenuEntry.Item(L.T("project.rename"), () => Actions.Rename(s), icon: "pencil"));
        e.Add(MenuEntry.Item(L.T("project.remove"), () => Actions.Remove(s), icon: "trash-2", destructive: true));
        return e;
    }
}

/// <summary>放不下就整个不显示（不裁成半截）：可用宽度小于内容的自然宽度时量成 0。</summary>
internal sealed class HideWhenTight : Decorator
{
    private bool _hidden;

    public HideWhenTight(UIElement child)
    {
        Child = child;
        ClipToBounds = true; // 让出空间时宽度为 0，内容被裁掉
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null) return new Size();
        Child.Measure(new Size(double.PositiveInfinity, constraint.Height));
        var d = Child.DesiredSize;
        _hidden = d.Width > constraint.Width + 0.5;
        return _hidden ? new Size(0, d.Height) : d;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child == null) return arrangeSize;
        var d = Child.DesiredSize;
        Child.Arrange(new Rect(0, 0, d.Width, arrangeSize.Height));
        bool hide = _hidden || arrangeSize.Width + 0.5 < d.Width;
        return new Size(hide ? 0 : d.Width, arrangeSize.Height);
    }
}

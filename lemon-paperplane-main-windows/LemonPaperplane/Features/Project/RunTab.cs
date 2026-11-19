// 运行（对应 Mac Features/Project/RunTab.swift）：左侧运行配置列表，右侧控制台（诊断条、资源面板、工具栏、ANSI 日志、可切换的插件 run.panel 侧栏）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperplaneKit;
using static LemonPaperplane.UI;
using RunModel = PaperplaneKit.Run;

namespace LemonPaperplane;

/// <summary>运行标签。</summary>
public sealed class RunTab : PPView
{
    private readonly ProjectDetailModel _model;
    private readonly AppModel _app = AppModel.Shared;
    private readonly State<double> _listWidth = new(240);

    public RunTab(ProjectDetailModel model)
    {
        _model = model;
        Observe(model, nameof(ProjectDetailModel.Project));
        Observe(_app.Nav, nameof(NavStore.SelectedConfig), nameof(NavStore.ProjectTabSelection));
    }

    /// <summary>选中的运行配置：记住的那个还在就用它，否则默认配置、第一个。</summary>
    public static string? SelectedId(Project project)
    {
        var configs = project.AllRunConfigs;
        if (AppModel.Shared.Nav.SelectedConfig.TryGetValue(project.Id, out var c) && configs.Any(x => x.Id == c)) return c;
        return project.DefaultConfigId ?? configs.FirstOrDefault()?.Id;
    }

    protected override UIElement Build()
    {
        if (_model.Project is not { } project) return Gap(0);
        var left = Keep("list", () => new RunConfigList(_model));
        UIElement right;
        if (SelectedId(project) is { } cid && project.RunConfig(cid) != null)
            right = Keep("area:" + cid, () => new RunConsoleArea(_model, cid));
        else
            right = new PPEmptyState("play", L.T("run.noConfig"), L.T("runconfig.new"), () => RunConfigDialog.PresentNew(project), "plus").Bg(ColorToken.BgContent);
        // 分栏宽度跨重建保留（_listWidth），左右两边的视图跨重建复用
        return new PPSplitView(left, right, _listWidth, 180, 420);
    }
}

/// <summary>左侧运行配置列表：按模块分组；状态点、默认标记、过期标记、加入启动、运行 / 停止；右键菜单。</summary>
internal sealed class RunConfigList : PPView
{
    private readonly ProjectDetailModel _model;
    private readonly AppModel _app = AppModel.Shared;

    public RunConfigList(ProjectDetailModel model)
    {
        _model = model;
        Observe(model, nameof(ProjectDetailModel.Project));
        Observe(_app.Nav, nameof(NavStore.SelectedConfig));
        Observe(_app.Runs, nameof(RunStore.Runs));
        Observe(_app.Data, nameof(DataStore.Projects), nameof(DataStore.Contributions));
    }

    protected override UIElement Build()
    {
        if (_model.Project is not { } project) return Gap(0);
        var header = HStack(0,
            Text(L.T("runconfig.title"), Typo.Label, ColorToken.TextSecondary), Spacer(),
            new PPIconButton("plus", L.T("runconfig.new"), () => RunConfigDialog.PresentNew(project), size: M.IconButtonSmall, iconSize: M.IconSmall).Id("runconfig.new"));
        var headerBox = Box(header).Pad(M.Lg, 0, M.Sm, 0).H(M.ToolbarHeight);
        var list = VStack(1);
        list.Align = CrossAlign.Stretch;
        var selected = RunTab.SelectedId(project);
        var summary = _app.Data.Project(project.Id);
        foreach (var m in project.Modules)
        {
            if (project.Modules.Count > 1 && m.RunConfigs.Count > 0)
                list.Children.Add(Text(m.Name, Typo.Caption, ColorToken.TextTertiary).Pad(M.Md, M.Md, M.Md, M.Xxs));
            foreach (var c in m.RunConfigs) list.Children.Add(ConfigRow(project, summary, c, selected));
        }
        var scroll = Keep("scroll", () => Scroll(Gap(0)).Key("runconfig.scroll"), sv => sv.Content = Box(list).Pad(M.Sm));
        var col = new Grid();
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        col.Children.Add(headerBox.At(0, 0));
        col.Children.Add(new PPDivider(subtle: true).At(1, 0));
        col.Children.Add(scroll.At(2, 0));
        col.Bg(ColorToken.BgContent);
        return col;
    }

    private UIElement ConfigRow(Project project, ProjectSummary? summary, RunConfig c, string? selected)
    {
        var runs = _app.Runs;
        var nav = _app.Nav;
        var active = runs.ActiveRun(project.Id, c.Id);
        var latest = active ?? runs.LatestRun(project.Id, c.Id);
        bool isDefault = c.Id == project.DefaultConfigId;
        bool inLaunch = summary?.Launch.Contains(c.Id) == true;
        var type = _app.Data.Contributions.RunConfigType(c.Plugin, c.Type);
        var icon = ZStack(PPIcon.Raw(type?.Icon, c.Plugin, "play", M.Icon));
        if (latest != null)
        {
            var dot = new StatusDot(latest.State, 6) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            dot.Margin = new Thickness(0, 0, -2, -2);
            icon.Children.Add(dot);
        }
        var row = HStack(M.Sm,
            icon,
            TextMiddle(c.Name),
            isDefault ? new PPIcon("pin", 12, ColorToken.IconSecondary).Tip(L.T("runconfig.default")) : null,
            c.Stale ? new PPIcon("triangle-alert", 12, ColorToken.Warning).Tip(L.T("runconfig.stale")) : null,
            Spacer(M.Xs),
            // 加入 / 移出工作区「启动」面板（已加入的常亮）
            new PPIconButton("rocket", inLaunch ? L.T("launch.remove") : L.T("launch.add"),
                () => Actions.SetLaunch(project.Id, new[] { c.Id }, !inLaunch), size: M.IconButtonSmall, iconSize: M.IconSmall,
                tint: inLaunch ? ColorToken.Accent : ColorToken.IconSecondary, active: inLaunch).Id("runconfig.launch." + c.Id),
            active != null
                ? new PPIconButton("square", L.T("action.stop"), () => Actions.Stop(active.Id), size: M.IconButtonSmall, iconSize: M.IconSmall, tint: ColorToken.Danger).Id("runconfig.stop." + c.Id)
                : new PPIconButton("play", L.T("action.run"), () =>
                {
                    nav.SelectConfig(project.Id, c.Id);
                    Actions.Run(project.Id, c.Id);
                }, size: M.IconButtonSmall, iconSize: M.IconSmall, tint: ColorToken.Success).Id("runconfig.run." + c.Id));
        var r = new PPRow(row, selected: c.Id == selected, action: () => nav.SelectConfig(project.Id, c.Id)).Id("runconfig." + c.Id);
        r.A11y(c.Name);
        r.PPContextMenu(() => Menu(project, c, isDefault, inLaunch));
        return r;
    }

    private IEnumerable<MenuEntry> Menu(Project project, RunConfig c, bool isDefault, bool inLaunch)
    {
        var pid = project.Id;
        var e = new List<MenuEntry>
        {
            MenuEntry.Item(L.T("action.run"), () => Actions.Run(pid, c.Id), icon: "play"),
            MenuEntry.Item(L.T("runconfig.edit"), () => RunConfigDialog.PresentEdit(project, c), icon: "pencil"),
            MenuEntry.Item(L.T("runconfig.duplicate"), () => RunConfigDialog.PresentDuplicate(project, c), icon: "copy"),
            MenuEntry.Item(L.T("runconfig.setDefault"), () => AppModel.Shared.Perform(async api =>
            {
                await api.SetDefaultRunConfigAsync(pid, c.Id);
                await _model.Reload();
            }), icon: "pin", disabled: isDefault),
            MenuEntry.Item(inLaunch ? L.T("launch.remove") : L.T("launch.add"), () => Actions.SetLaunch(pid, new[] { c.Id }, !inLaunch), icon: "rocket"),
        };
        if (c.Edited && c.Source != "user")
        {
            e.Add(MenuEntry.Item(L.T("runconfig.reset"), () => AppModel.Shared.Perform(async api =>
            {
                await api.ResetRunConfigAsync(pid, c.Id);
                await _model.Reload();
            }), icon: "rotate-ccw"));
        }
        e.Add(MenuEntry.Divider);
        e.Add(MenuEntry.Item(L.T("common.delete"), () => Dialogs.Confirm(L.T("runconfig.delete.title", c.Name), null, async () =>
        {
            await AppModel.Shared.Client().DeleteRunConfigAsync(pid, c.Id);
            await _model.Reload();
        }, ok: L.T("common.delete"), danger: true), icon: "trash-2", destructive: true));
        return e;
    }
}

/// <summary>控制台区域：诊断条 + 资源面板 + 控制台 + 插件侧栏。对应 Mac RunConsoleArea（每个运行配置一个实例）。</summary>
internal sealed class RunConsoleArea : PPView
{
    private readonly ProjectDetailModel _model;
    private readonly string _configId;
    private readonly AppModel _app = AppModel.Shared;
    private readonly State<bool> _panelOpen;
    private readonly State<string?> _panelKey;
    private readonly State<double> _panelWidth = new(320);
    private readonly HashSet<string> _dismissed = new();
    private readonly RunControls? _controls;
    private ConsolePanel? _console;

    public RunConsoleArea(ProjectDetailModel model, string configId)
    {
        _model = model;
        _configId = configId;
        _panelOpen = UseState(UiPrefs.Get("RunPanelOpen") == "1");
        _panelKey = UseState<string?>(null);
        _controls = new RunControls(model, configId, () => _panelOpen.Value, () =>
        {
            _panelOpen.Value = !_panelOpen.Value;
            UiPrefs.Set("RunPanelOpen", _panelOpen.Value ? "1" : "0");
            _controls?.Invalidate();
        });
        Observe(model, nameof(ProjectDetailModel.Project));
        Observe(_app.Runs, nameof(RunStore.Runs));
        Observe(_app.Data, nameof(DataStore.Contributions));
    }

    private RunConfig? Config => _model.Project?.RunConfig(_configId);
    private RunModel? Run => _model.Project is { } p ? _app.Runs.LatestRun(p.Id, _configId) : null;

    /// <summary>当前运行状态下可显示的插件侧栏。</summary>
    public static List<ViewInfo> Panels(Project project, RunConfig config, RunModel? run)
    {
        if (run == null || project.Module(config.ModuleId) is not { } m) return new List<ViewInfo>();
        return AppModel.Shared.Data.Contributions.ViewsAt(ViewLocation.RunPanel).Where(v =>
        {
            bool facetOK = v.Facets.Count == 0 ? (v.Plugin == config.Plugin || m.Facets.Any(f => f.Plugin == v.Plugin)) : m.HasAnyFacet(v.Facets);
            bool stateOK = string.IsNullOrEmpty(v.RunState) || v.RunState == run.State;
            return facetOK && stateOK;
        }).ToList();
    }

    protected override UIElement Build()
    {
        if (_model.Project is not { } project || Config is not { } config) return Gap(0);
        var run = Run;
        var panels = Panels(project, config, run);
        var controls = _controls!;

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (Diagnoses(run, project) is { } diag) main.Children.Add(diag.At(0, 0));
        if (run != null)
        {
            if (_console == null)
            {
                Detach(controls);
                _console = new ConsolePanel(run.Id, run.Id, run.LogFile, leading: controls, inputEnabled: run.IsActive);
            }
            else
            {
                _console.Update(run.Id, run.Id, run.LogFile, run.IsActive);
                if (!ReferenceEquals(_console.Leading, controls)) _console.Leading = controls;
                else if (System.Windows.Media.VisualTreeHelper.GetParent(controls) == null) _console.Invalidate();
            }
            Detach(_console);
            var resources = Keep("res:" + run.Id, () => new RunResourcePanel(run));
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(resources.At(0, 0));
            body.Children.Add(new PPDivider(subtle: true).At(1, 0));
            body.Children.Add(_console.At(2, 0));
            main.Children.Add(body.At(1, 0));
        }
        else
        {
            if (_console != null) _console.Leading = null;
            Detach(controls);
            var bar = Box(HStack(0, controls, Spacer())).Pad(M.Sm, 0).H(M.ToolbarHeight).Bg(ColorToken.BgContent);
            var empty = new PPEmptyState("terminal", L.T("run.notStarted"), L.T("action.run"), () => Actions.Run(project.Id, config.Id), "play").Bg(ColorToken.ConsoleBg);
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(bar.At(0, 0));
            g.Children.Add(new PPDivider(subtle: true).At(1, 0));
            g.Children.Add(empty.At(2, 0));
            main.Children.Add(g.At(1, 0));
        }

        if (!(_panelOpen.Value && run != null && panels.Count > 0)) return main;

        // 插件侧栏
        var current = panels.FirstOrDefault(p => p.Key == _panelKey.Value) ?? panels[0];
        var side = new Grid();
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (panels.Count > 1)
        {
            var bar = new PPTabBar<string>(panels.Select(p => new PPTab<string>(p.Key, p.Title, iconRef: IconKey.Parse(p.Icon, p.Plugin))),
                Bind.Of(() => current.Key, v => _panelKey.Value = v));
            side.Children.Add(Box(bar).Pad(M.Sm, 0).At(0, 0));
        }
        else
        {
            side.Children.Add(Box(HStack(0, Text(current.Title, Typo.Label), Spacer())).Pad(M.Md, 0).H(M.ToolbarHeight).At(0, 0));
        }
        side.Children.Add(new PPDivider(subtle: true).At(1, 0));
        var host = Keep("panel:" + current.Key + run!.Id, () => new PluginViewHost(current,
            new ViewContext { Plugin = current.Plugin, View = current.Id, ProjectId = project.Id, ModuleId = config.ModuleId, RunId = run.Id }));
        side.Children.Add(host.At(2, 0));
        side.Width = _panelWidth.Value;
        side.Bg(ColorToken.BgContent);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(main.At(0, 0));
        root.Children.Add(new PPDivider(vertical: true).At(0, 1));
        root.Children.Add(side.At(0, 2));
        return root;
    }

    private UIElement? Diagnoses(RunModel? run, Project project)
    {
        if (run == null) return null;
        var list = run.Diagnoses.Where(d => !_dismissed.Contains(d.StableKey)).ToList();
        bool failed = run.State == RunState.Failed && !string.IsNullOrEmpty(run.Error);
        if (list.Count == 0 && !failed) return null;
        var col = VStack(M.Sm);
        col.Align = CrossAlign.Stretch;
        if (failed && list.Count == 0) col.Children.Add(new PPAlert("danger", run.Error!));
        foreach (var d in list)
        {
            var diag = d;
            var row = Keep("diag:" + run.Id + ":" + d.StableKey, () => new DiagnosisRow(diag, run.Id, project.Id));
            var close = new PPIconButton("x", L.T("common.close"), () => { _dismissed.Add(diag.StableKey); Invalidate(); },
                size: M.IconButtonSmall, iconSize: M.IconSmall);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Margin = new Thickness(M.Xs);
            col.Children.Add(ZStack(row, close));
        }
        var scroll = Scroll(Box(col).Pad(M.Md));
        scroll.MaxHeight = 260;
        var wrap = VStack(0, scroll, new PPDivider(subtle: true));
        wrap.Align = CrossAlign.Stretch;
        return wrap;
    }
}

/// <summary>
/// 控制台工具栏左侧的运行控件：运行 / 重启 / 停止、状态与时长、地址、内存 / CPU、插件 run.toolbar 动作、侧栏开关。
/// 单独一个视图（运行状态变化只重建这一小块，控制台不动）。
/// </summary>
internal sealed class RunControls : PPView
{
    private readonly ProjectDetailModel _model;
    private readonly string _configId;
    private readonly Func<bool> _panelOpen;
    private readonly Action _togglePanel;
    private readonly AppModel _app = AppModel.Shared;
    private readonly DispatcherTimer _tick;
    private TextBlock? _status;

    public RunControls(ProjectDetailModel model, string configId, Func<bool> panelOpen, Action togglePanel)
    {
        _model = model;
        _configId = configId;
        _panelOpen = panelOpen;
        _togglePanel = togglePanel;
        Observe(model, nameof(ProjectDetailModel.Project));
        Observe(_app.Runs, nameof(RunStore.Runs));
        Observe(_app.Data, nameof(DataStore.Contributions));
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => { if (_status != null && Run is { } r) _status.Text = StatusText(r); };
    }

    private RunModel? Run => _model.Project is { } p ? _app.Runs.LatestRun(p.Id, _configId) : null;

    protected override void OnAppear() => _tick.Start();
    protected override void OnDisappear() => _tick.Stop();

    public static string StatusText(RunModel r)
    {
        // 用户主动停止的不显示退出码（被信号结束时退出码没有意义）
        if (!r.IsActive && r.StoppedByUser) return L.T("status.stopped");
        var s = RunStatusStyle.Title(r.State);
        if (!r.IsActive && r.ExitCode is int code) s += " · " + L.T("run.exitCode", code);
        if (r.IsActive && r.StartedAt is { } start) s += " · " + Format.Duration(DateTimeOffset.Now - start);
        return s;
    }

    protected override UIElement Build()
    {
        if (_model.Project is not { } project || project.RunConfig(_configId) is not { } config) return Gap(0);
        var run = Run;
        bool active = run?.IsActive == true;
        var row = HStack(M.Xxs);
        if (active && run != null)
        {
            row.Children.Add(new PPIconButton("rotate-cw", L.T("action.restart"), () => Actions.Restart(run.Id)).Id("run.restart"));
            row.Children.Add(new PPIconButton("square", L.T("action.stop") + "  Ctrl+.", () => Actions.Stop(run.Id), tint: ColorToken.Danger).Id("run.stop"));
        }
        else
        {
            row.Children.Add(new PPIconButton("play", L.T("action.run") + "  Ctrl+R", () =>
            {
                _app.Nav.SelectConfig(project.Id, config.Id);
                Actions.Run(project.Id, config.Id);
            }, tint: ColorToken.Success).Id("run.run"));
        }
        _status = null;
        if (run != null)
        {
            _status = Text(StatusText(run), Typo.Small, ColorToken.TextSecondary).Id("run.status");
            row.Children.Add(HStack(M.Sm, new StatusDot(run.State), _status).Margins(M.Sm, 0, M.Sm, 0).Shrink());
            foreach (var u in run.Urls.Take(2))
            {
                var url = u;
                row.Children.Add(new PPLink(Format.ShortURL(url), () => Actions.OpenURL(url), mono: true, icon: "external-link").Margins(0, 0, M.Sm, 0).Shrink());
            }
            if (active) row.Children.Add(new RunResourceText(run.Id, fallbackCpu: run.Cpu, fallbackRss: run.RssMB));
        }
        var module = project.Module(config.ModuleId);
        var acts = Actions.Applicable(_app.Data.Contributions.ActionsAt(ActionLocation.RunToolbar),
            new HashSet<string>(module?.Facets.Select(f => f.Id) ?? Enumerable.Empty<string>()), new HashSet<string> { config.Plugin ?? "" });
        foreach (var a in acts)
        {
            var act = a;
            row.Children.Add(new PPIconButton(IconKey.Parse(act.Icon, act.Plugin) ?? IconKey.Builtin("zap"), act.Title,
                () => Actions.Invoke(act, project.Id, config.ModuleId, run?.Id)));
        }
        if (RunConsoleArea.Panels(project, config, run).Count > 0)
        {
            bool open = _panelOpen();
            row.Children.Add(new PPIconButton(open ? "panel-right-close" : "panel-right", L.T("run.panel"), _togglePanel, active: open).Id("run.panelToggle"));
        }
        return row;
    }
}

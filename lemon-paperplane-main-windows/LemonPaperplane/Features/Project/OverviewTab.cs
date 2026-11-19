// 概览（对应 Mac Features/Project/OverviewTab.swift）：问题（可一键修复）、模块（facet、关键信息、工具链、依赖）、插件 project.section 区块。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>概览标签。</summary>
public sealed class OverviewTab : PPView
{
    private readonly ProjectDetailModel _model;
    private readonly AppModel _app = AppModel.Shared;
    private readonly State<List<Diagnosis>> _problems;
    private DateTimeOffset? _loadedFor;
    private bool _everLoaded;

    public OverviewTab(ProjectDetailModel model)
    {
        _model = model;
        _problems = UseState(new List<Diagnosis>());
        Observe(model, nameof(ProjectDetailModel.Project));
        Observe(_app.Data, nameof(DataStore.Contributions), nameof(DataStore.Toolchains), nameof(DataStore.ToolchainKinds));
    }

    protected override void OnAppear() => LoadProblems();

    private async void LoadProblems()
    {
        if (_model.Project is not { } p) return;
        _loadedFor = p.AnalyzedAt;
        _everLoaded = true;
        try { _problems.Value = await _app.Client().ProblemsAsync(p.Id); }
        catch { /* 和 Mac 一样：取不到就保持原样 */ }
    }

    private sealed record SectionItem(ViewInfo View, Module Module)
    {
        public string Id => View.Key + "|" + Module.Id;
    }

    private List<SectionItem> Sections(Project project)
    {
        var output = new List<SectionItem>();
        foreach (var v in _app.Data.Contributions.ViewsAt(ViewLocation.ProjectSection))
            foreach (var m in project.Modules)
            {
                bool ok = v.Facets.Count == 0 ? (m.Facets.Any(f => f.Plugin == v.Plugin) || m.Analysis.ContainsKey(v.Plugin)) : m.HasAnyFacet(v.Facets);
                if (ok) output.Add(new SectionItem(v, m));
            }
        return output;
    }

    protected override UIElement Build()
    {
        if (_model.Project is not { } project) return Gap(0);
        // 分析时间变了重新取问题（对应 .task(id: project.analyzedAt)）
        if (_everLoaded && project.AnalyzedAt != _loadedFor) Dispatcher.BeginInvoke(new Action(LoadProblems));

        var col = VStack(M.Xxl);
        col.Align = CrossAlign.Stretch;
        var problems = _problems.Value;
        if (problems.Count > 0)
        {
            var list = VStack(M.Sm);
            list.Align = CrossAlign.Stretch;
            foreach (var d in problems)
                list.Children.Add(Keep("diag:" + d.StableKey, () => new DiagnosisRow(d, null, project.Id, LoadProblems)));
            col.Children.Add(new PPSection(L.T("problems.title"), list).Id("overview.problems"));
        }

        var modules = VStack(M.Lg);
        modules.Align = CrossAlign.Stretch;
        foreach (var m in project.Modules) modules.Children.Add(ModuleCard(project, m));
        col.Children.Add(new PPSection(L.T("project.modules"), modules,
            accessory: new PPButton(L.T("project.reimport"), () => Actions.Reimport(project.Id), icon: "refresh-cw", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("overview.reimport"))
            .Id("overview.modules"));

        foreach (var sec in Sections(project))
        {
            var host = Keep("sec:" + sec.Id, () => new PluginViewHost(sec.View,
                new ViewContext { Plugin = sec.View.Plugin, View = sec.View.Id, ProjectId = project.Id, ModuleId = sec.Module.Id }, scrolls: false));
            var box = Box(host).Pad(M.Md).Rounded(M.RadiusLG).Bg(ColorToken.BgSurface).Outline(ColorToken.Border);
            var title = sec.View.Title + (project.Modules.Count > 1 ? "  ·  " + sec.Module.Name : "");
            col.Children.Add(new PPSection(title, box));
        }

        var content = Box(col).Pad(M.Xl);
        return Box(Keep("scroll", () => Scroll(Gap(0)).Key("overview.scroll"), sv => sv.Content = content));
    }

    // MARK: 模块卡

    private UIElement ModuleCard(Project project, Module module)
    {
        var data = _app.Data;
        var header = HStack(M.Md,
            new PPIcon("box", M.Icon, ColorToken.IconSecondary),
            Text(module.Name, Typo.Title).Shrink(false),
            module.RelDir.Length > 0 ? Text(module.RelDir, Typo.MonoSmall, ColorToken.TextTertiary) : null,
            Spacer(),
            module.NeedsSync && !module.Synced ? new PPButton(L.T("project.sync"), () => Actions.Sync(project.Id, module.Id), icon: "refresh-cw", size: PPButtonSize.Small) : null,
            Actions.LocalOnlyAvailable
                ? new PPMenuButton(() => Actions.OpenWithEntries(project.Id, module.Id),
                    Box(new PPIcon("ellipsis", M.Icon, ColorToken.IconSecondary).Center()).Size(M.IconButtonSmall, M.IconButtonSmall).Bg(ColorToken.Clear),
                    PopupAlignment.Trailing, L.T("common.more"))
                : null);
        var col = VStack(M.Md, header);
        col.Align = CrossAlign.Stretch;
        if (module.Facets.Count > 0)
        {
            col.Children.Add(Flow(M.Xs, module.Facets.Select(f =>
            {
                var fb = data.Contributions.Facet(f.Id);
                return fb != null ? new FacetBadgeView(fb.Name, fb.Icon, fb.Plugin) : new FacetBadgeView(f.Id, null, f.Plugin ?? "");
            })));
        }
        var info = module.Info;
        if (info.Count > 0) col.Children.Add(new PPKeyValueList(info.Select(i => new PPKeyValueRow(i.Label, i.Value)), keyWidth: 120));
        if (module.Toolchains.Count > 0)
        {
            col.Children.Add(new PPDivider(subtle: true));
            var tcs = VStack(M.Sm);
            tcs.Align = CrossAlign.Stretch;
            foreach (var kind in module.Toolchains.Keys.OrderBy(k => k, StringComparer.Ordinal))
                tcs.Children.Add(ToolchainSelectionRow(project, module.Toolchains[kind]));
            col.Children.Add(tcs);
        }
        if (module.Dependencies is { } deps && deps.State != "none")
        {
            col.Children.Add(new PPDivider(subtle: true));
            col.Children.Add(new DependencyRow(project.Id, module.Id, deps));
        }
        return new PPCard(col).Id("module." + module.Id);
    }

    /// <summary>一类工具链：当前选择 + 修改（自动 / 已安装 / 自定义约束）。对应 Mac ToolchainSelectionRow。</summary>
    private UIElement ToolchainSelectionRow(Project project, ToolchainSelection sel)
    {
        var data = _app.Data;
        var row = HStack(M.Md, Text(data.KindName(sel.Kind), Typo.Body, ColorToken.TextSecondary).W(120).Shrink(false));
        if (sel.Missing)
        {
            row.Children.Add(new PPBadge(L.T("toolchain.missing"), tone: "danger"));
            if (!string.IsNullOrEmpty(sel.Constraint)) row.Children.Add(Text(sel.Constraint, Typo.Mono, ColorToken.TextSecondary));
        }
        else
        {
            row.Children.Add(Text(sel.Version ?? "", Typo.Mono));
            if (data.Toolchains.FirstOrDefault(t => t.Id == sel.ToolchainId) is { Vendor: { } v }) row.Children.Add(Text(v, Typo.Body, ColorToken.TextTertiary));
        }
        if (sel.Override) row.Children.Add(new PPBadge(L.T("toolchain.override"), tone: "info"));
        if (!string.IsNullOrEmpty(sel.Source)) row.Children.Add(new PPInfoButton(L.T("toolchain.source", sel.Source!, sel.Constraint ?? "-")));
        row.Children.Add(Spacer(M.Md));
        row.Children.Add(ToolchainOverridePicker.Make(project, sel.Kind, _model));
        row.MinHeight = M.RowHeight;
        return row;
    }
}

/// <summary>工具链覆盖选择（project.settings 的 toolchains）。对应 Mac ToolchainOverridePicker。</summary>
internal static class ToolchainOverridePicker
{
    private const string Auto = "__auto";
    private const string Custom = "__custom";

    public static UIElement Make(Project project, string kind, ProjectDetailModel model)
    {
        var data = AppModel.Shared.Data;
        ToolchainOverride? current = project.Settings != null && project.Settings.Toolchains.TryGetValue(kind, out var c) ? c : null;
        var opts = new List<DropdownOption<string>> { new(Auto, L.T("toolchain.auto")) };
        opts.AddRange(data.ToolchainsOf(kind).Select(t => new DropdownOption<string>(t.Id, t.DisplayName, detail: t.Source)));
        if (!string.IsNullOrEmpty(current?.Constraint)) opts.Add(new DropdownOption<string>("c:" + current!.Constraint, L.T("toolchain.constraintValue", current.Constraint!)));
        opts.Add(new DropdownOption<string>(Custom, L.T("toolchain.customConstraint")));
        string sel = !string.IsNullOrEmpty(current?.Id) ? current!.Id! : !string.IsNullOrEmpty(current?.Constraint) ? "c:" + current!.Constraint : Auto;
        return new PPDropdown<string>(opts, Bind.Of(() => sel, v =>
        {
            switch (v)
            {
                case Auto:
                    model.SetToolchainOverride(kind, null);
                    break;
                case Custom:
                    Dialogs.Input(L.T("toolchain.customConstraint"), L.T("toolchain.constraintLabel"), async con =>
                    {
                        model.Project = await AppModel.Shared.Client().UpdateProjectSettingsAsync(project.Id,
                            toolchains: new Dictionary<string, ToolchainOverride?> { [kind] = new ToolchainOverride { Constraint = con } });
                    }, initial: current?.Constraint ?? "", ok: L.T("common.save"));
                    break;
                default:
                    if (v.StartsWith("c:", StringComparison.Ordinal)) break;
                    model.SetToolchainOverride(kind, new ToolchainOverride { Id = v });
                    break;
            }
        }), size: PPButtonSize.Small, minWidth: 160, maxWidth: 260).Id("toolchain." + kind);
    }
}

/// <summary>依赖状态 + 安装（安装中按钮转圈）。对应 Mac DependencyRow。</summary>
internal sealed class DependencyRow : PPView
{
    private readonly string _projectId;
    private readonly string _moduleId;
    private readonly DependencyState _deps;
    private readonly RunStore _runs = AppModel.Shared.Runs;

    public DependencyRow(string projectId, string moduleId, DependencyState deps)
    {
        _projectId = projectId;
        _moduleId = moduleId;
        _deps = deps;
        Observe(_runs, nameof(RunStore.Tasks));
    }

    public static string Title(string s) => s switch
    {
        "ok" => L.T("deps.ok"),
        "missing" => L.T("deps.missing"),
        "outdated" => L.T("deps.outdated"),
        _ => L.T("deps.unknown"),
    };

    public static string Tone(string s) => s switch
    {
        "ok" => "success",
        "missing" => "danger",
        "outdated" => "warning",
        _ => "neutral",
    };

    protected override UIElement Build()
    {
        bool installing = _runs.ActiveTasks.Any(t => t.ProjectId == _projectId && t.ModuleId == _moduleId && t.Kind == "install");
        var d = _deps;
        var pid = _projectId;
        var mid = _moduleId;
        var menu = new PPMenuButton(() => new[]
        {
            MenuEntry.Item(L.T("deps.cleanInstall"), () =>
                Dialogs.Confirm(L.T("deps.cleanInstall"), L.T("deps.cleanInstall.confirm"), () => Actions.InstallDeps(pid, mid, clean: true), ok: L.T("deps.cleanInstall")),
                icon: "eraser"),
        }, Box(new PPIcon("chevron-down", M.IconSmall).Center()).Size(M.IconButtonSmall, M.IconButtonSmall).Bg(ColorToken.Clear), PopupAlignment.Trailing);
        return HStack(M.Md,
            Text(L.T("deps.title"), Typo.Body, ColorToken.TextSecondary).W(120).Shrink(false),
            new PPBadge(Title(d.State), tone: Tone(d.State)),
            d.Manager != null ? Text(d.Manager, Typo.Mono, ColorToken.TextSecondary) : null,
            !string.IsNullOrEmpty(d.Message) ? new PPInfoButton(d.Message!) : null,
            Spacer(M.Md),
            new PPButton(d.State == "ok" ? L.T("deps.reinstall") : L.T("deps.install"), () => Actions.InstallDeps(pid, mid), icon: "download",
                kind: d.State == "ok" ? PPButtonKind.Ghost : PPButtonKind.Primary, size: PPButtonSize.Small, busy: installing).Id("deps.install." + mid),
            menu);
    }
}

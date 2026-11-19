// 配置（对应 Mac Features/Project/ConfigTab.swift）：工程环境变量、工具链覆盖、运行配置列表（点开编辑）、相关插件设置。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>配置标签。</summary>
public sealed class ConfigTab : PPView
{
    private readonly ProjectDetailModel _model;
    private readonly AppModel _app = AppModel.Shared;

    public ConfigTab(ProjectDetailModel model)
    {
        _model = model;
        Observe(model, nameof(ProjectDetailModel.Project));
        Observe(_app.Data, nameof(DataStore.Contributions), nameof(DataStore.Toolchains), nameof(DataStore.ToolchainKinds), nameof(DataStore.Plugins));
    }

    /// <summary>模块需要的工具链种类。</summary>
    private static List<string> Kinds(Project project)
    {
        var s = new HashSet<string>();
        foreach (var m in project.Modules)
        {
            foreach (var k in m.Toolchains.Keys) s.Add(k);
            foreach (var r in m.Requirements) s.Add(r.Kind);
        }
        if (project.Settings != null) foreach (var k in project.Settings.Toolchains.Keys) s.Add(k);
        return s.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static string SelectedText(Project project, string kind)
    {
        var sels = project.Modules.Select(m => m.Toolchains.TryGetValue(kind, out var t) ? t : null).Where(t => t != null).Select(t => t!).ToList();
        if (sels.Any(x => x.Missing)) return L.T("toolchain.missing");
        return string.Join(", ", sels.Select(x => x.Version).Where(v => v != null).Distinct().OrderBy(v => v, StringComparer.Ordinal));
    }

    protected override UIElement Build()
    {
        if (_model.Project is not { } project) return Gap(0);
        var data = _app.Data;
        var col = VStack(M.Xxl);
        col.Align = CrossAlign.Stretch;
        col.Children.Add(Keep("env", () => new EnvSection(_model)));

        var kinds = Kinds(project);
        if (kinds.Count > 0)
        {
            var rows = VStack(M.Sm);
            rows.Align = CrossAlign.Stretch;
            foreach (var k in kinds)
            {
                rows.Children.Add(HStack(M.Md,
                    PPIcon.Raw(data.ToolchainKinds.FirstOrDefault(x => x.Kind == k)?.Icon, null, "boxes", M.Icon),
                    Text(data.KindName(k)).W(140).Shrink(false),
                    Text(SelectedText(project, k), Typo.Mono, ColorToken.TextSecondary),
                    Spacer(),
                    ToolchainOverridePicker.Make(project, k, _model)));
            }
            col.Children.Add(new PPSection(L.T("config.toolchains"), new PPCard(rows), info: L.T("config.toolchains.info")).Id("config.toolchains"));
        }

        col.Children.Add(RunConfigsSection(project));

        var relevant = data.Contributions.Settings.Where(s => project.PluginIds.Contains(s.Plugin) && s.Fields.Count > 0).ToList();
        if (relevant.Count > 0)
        {
            var cards = VStack(M.Lg);
            cards.Align = CrossAlign.Stretch;
            foreach (var s in relevant)
            {
                var info = s;
                cards.Children.Add(Keep("plugin:" + s.Plugin, () => new PluginSettingsCard(info), card => card.Update(info)));
            }
            col.Children.Add(new PPSection(L.T("config.pluginSettings"), cards, info: L.T("config.pluginSettings.info")).Id("config.pluginSettings"));
        }

        col.MaxWidth = 900;
        col.HorizontalAlignment = HorizontalAlignment.Left;
        var content = Box(col).Pad(M.Xl);
        return Box(Keep("scroll", () => Scroll(Gap(0)).Key("config.scroll"), sv => sv.Content = content));
    }

    /// <summary>运行配置（点开编辑）。</summary>
    private UIElement RunConfigsSection(Project project)
    {
        var data = _app.Data;
        var add = new PPButton(L.T("runconfig.new"), () => RunConfigDialog.PresentNew(project), icon: "plus", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("config.newRunConfig");
        UIElement body;
        if (project.AllRunConfigs.Count == 0)
        {
            body = Text(L.T("run.noConfig"), Typo.Body, ColorToken.TextSecondary);
        }
        else
        {
            var list = VStack(0);
            list.Align = CrossAlign.Stretch;
            foreach (var c in project.AllRunConfigs)
            {
                var cfg = c;
                var type = data.Contributions.RunConfigType(c.Plugin, c.Type);
                var row = HStack(M.Md,
                    PPIcon.Raw(type?.Icon, c.Plugin, "play", M.Icon),
                    Text(c.Name).Shrink(false),
                    Text(type?.Name ?? c.Type, Typo.Body, ColorToken.TextTertiary),
                    c.Id == project.DefaultConfigId ? new PPBadge(L.T("runconfig.default"), tone: "info") : null,
                    c.Edited ? new PPBadge(L.T("runconfig.edited")) : null,
                    Spacer(),
                    new PPIcon("pencil", M.IconSmall, ColorToken.IconSecondary));
                list.Children.Add(new PPRow(row, action: () => RunConfigDialog.PresentEdit(project, cfg)).Id("config.runconfig." + c.Id).A11y(c.Name));
            }
            body = new PPCard(list, padding: M.Xs);
        }
        return new PPSection(L.T("runconfig.title"), body, accessory: add).Id("config.runconfigs");
    }
}

/// <summary>工程环境变量（改了出现「还原」「保存」）。对应 Mac EnvSection。</summary>
internal sealed class EnvSection : PPView
{
    private readonly ProjectDetailModel _model;
    private List<KeyValuePair<string, string>> _pairs = new();
    private readonly State<bool> _dirty;
    private readonly State<bool> _busy;
    private readonly State<int> _editorId;

    public EnvSection(ProjectDetailModel model)
    {
        _model = model;
        _dirty = UseState(false);
        _busy = UseState(false);
        _editorId = UseState(0);
    }

    protected override void OnAppear() => Load();

    private void Load()
    {
        _pairs = (_model.Project?.Settings?.Env ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        _dirty.Value = false;
        _editorId.Value++;
    }

    private async void Save()
    {
        if (_model.Project is not { } p) return;
        _busy.Value = true;
        var env = new Dictionary<string, string>();
        foreach (var kv in _pairs) if (kv.Key.Length > 0) env[kv.Key] = kv.Value;
        try
        {
            _model.Project = await AppModel.Shared.Client().UpdateProjectSettingsAsync(p.Id, env: env);
            _dirty.Value = false;
        }
        catch (Exception e)
        {
            AppModel.Shared.Toasts.Error(e);
        }
        _busy.Value = false;
    }

    protected override UIElement Build()
    {
        var editor = Keep("editor:" + _editorId.Value, () => new KeyValueEditor(_pairs, pairs =>
        {
            _pairs = pairs;
            _dirty.Value = true;
        }));
        UIElement? accessory = null;
        if (_dirty.Value)
            accessory = HStack(M.Sm,
                new PPButton(L.T("common.revert"), Load, kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("env.revert"),
                new PPButton(L.T("common.save"), Save, kind: PPButtonKind.Primary, size: PPButtonSize.Small, busy: _busy.Value).Id("env.save"));
        return new PPSection(L.T("config.env"), new PPCard(editor), info: L.T("config.env.info"), accessory: accessory).Id("config.env");
    }
}

/// <summary>一个插件的设置表单（plugin.settings）。对应 Mac PluginSettingsCard（插件页也用它）。</summary>
public sealed class PluginSettingsCard : PPView
{
    private PluginSettingsInfo _info;
    private Dictionary<string, JToken> _values;
    private readonly State<bool> _dirty;
    private readonly State<bool> _busy;
    private int _version;

    public PluginSettingsCard(PluginSettingsInfo info)
    {
        _info = info;
        _values = FormView.InitialValues(info.Fields, info.Values);
        _dirty = UseState(false);
        _busy = UseState(false);
        Observe(AppModel.Shared.Data, nameof(DataStore.Plugins));
    }

    /// <summary>插件设置有了新值：没改过时直接换成新值。</summary>
    public void Update(PluginSettingsInfo info)
    {
        var changed = !Json.DeepEquals(info.Values, _info.Values) || !Json.DeepEquals(info.Fields, _info.Fields);
        _info = info;
        if (!changed || _dirty.Value) return;
        _values = FormView.InitialValues(info.Fields, info.Values);
        _version++;
        Invalidate();
    }

    private async void Save()
    {
        _busy.Value = true;
        try
        {
            await AppModel.Shared.Client().UpdatePluginSettingsAsync(_info.Plugin, FormView.Normalized(_info.Fields, _values));
            _dirty.Value = false;
            AppModel.Shared.Toasts.Show("success", L.T("common.saved"));
        }
        catch (Exception e)
        {
            AppModel.Shared.Toasts.Error(e);
        }
        _busy.Value = false;
    }

    protected override UIElement Build()
    {
        var info = _info;
        var form = Keep("form:" + _version, () => new FormView(info.Fields, _values, onChange: () => _dirty.Value = true));
        var header = HStack(M.Sm,
            PPIcon.Raw(AppModel.Shared.Data.Plugin(info.Plugin)?.Icon, info.Plugin, "puzzle", M.Icon),
            Text(info.Title, Typo.Title),
            Spacer(),
            _dirty.Value ? new PPButton(L.T("common.save"), Save, kind: PPButtonKind.Primary, size: PPButtonSize.Small, busy: _busy.Value).Id("pluginSettings.save." + info.Plugin) : null);
        var col = VStack(M.Md, header, form);
        col.Align = CrossAlign.Stretch;
        return new PPCard(col).Id("pluginSettings." + info.Plugin);
    }
}

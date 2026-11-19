using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 设置：语言、主题、终端、镜像（测速）、自动下载 / 安装、插件闲置、日志、Git、编辑器、API 与 MCP、关于、插件 settings 视图。
/// 对应 Mac 的 SettingsPage。Windows 差异：「终端」是 Windows 的终端程序（自动 / Windows Terminal / PowerShell 7 / Windows PowerShell / 命令提示符）；
/// 「命令行工具」是把安装目录加到用户 PATH（内核 cli.install 在 Windows 上就是这么做的）。
/// </summary>
public sealed class SettingsPage : PPView
{
    private readonly DataStore _data = AppModel.Shared.Data;
    private readonly AppModel _model = AppModel.Shared;
    private readonly State<MirrorStatus?> _mirrors;
    private readonly State<bool> _probing;
    private readonly State<MCPConfig?> _mcp;
    private bool _loaded;

    public SettingsPage()
    {
        _mirrors = UseState<MirrorStatus?>(null);
        _probing = UseState(false);
        _mcp = UseState<MCPConfig?>(null);
        Observe(_data, nameof(DataStore.Settings), nameof(DataStore.Contributions));
        Observe(_model, nameof(AppModel.SystemInfo));
    }

    private AppSettings S => _data.Settings ?? new AppSettings();

    protected override async void OnAppear()
    {
        if (_model.Api is not { } api) return;
        // 关于：每次打开时刷新一次内存数据
        try { _model.UpdateSystemInfo(await api.SystemInfoAsync()); } catch { }
        if (_loaded) return;
        _loaded = true;
        try { _mirrors.Value = await api.MirrorsStatusAsync(); } catch { }
        try { _mcp.Value = await api.McpConfigAsync(); } catch { }
    }

    protected override UIElement Build()
    {
        var col = VStack(M.Xxl, General(), MirrorSection(), Automation(), Keep("editor", () => new EditorSettingsSection()), ApiSection(), About());
        foreach (var v in _data.Contributions.ViewsAt(ViewLocation.Settings))
        {
            var view = v;
            col.Children.Add(new PPSection(v.Title, new PPCard(Keep("view:" + v.Key, () => new PluginViewHost(view, new ViewContext { Plugin = view.Plugin, View = view.Id }, scrolls: false)))));
        }
        col.Align = CrossAlign.Stretch;
        col.Padding = new Thickness(M.Xl);
        col.MaxWidth = 820;
        col.HorizontalAlignment = HorizontalAlignment.Left;
        var page = VStack(0, new PPPageHeader(L.T("sidebar.settings")), new PPDivider(subtle: true), Scroll(col).Key("settings.scroll").Id("settings.scroll").Flex());
        page.Align = CrossAlign.Stretch;
        return page;
    }

    // MARK: 通用

    /// <summary>Windows 的终端程序（值与内核 openwith_windows.go 一致；空 = 自动：Windows Terminal → PowerShell）。</summary>
    private static readonly string[] Terminals = { "", "wt", "pwsh", "powershell", "cmd" };

    private static string TerminalTitle(string v) => v switch
    {
        "wt" => "Windows Terminal",
        "pwsh" => "PowerShell 7",
        "powershell" => "Windows PowerShell",
        "cmd" => L.T("settings.terminal.cmd"),
        _ => L.T("settings.terminal.auto"),
    };

    private UIElement General()
    {
        var s = S;
        var language = new PPDropdown<string>(new[]
        {
            new DropdownOption<string>("system", L.T("settings.followSystem")),
            new DropdownOption<string>("zh-Hans", "中文"),
            new DropdownOption<string>("en", "English"),
        }, Bind.Of(() => string.IsNullOrEmpty(S.Locale) ? "system" : S.Locale, v =>
        {
            L10n.Shared.Apply(v);
            Update(x => x with { Locale = v }, ("locale", v));
        }), minWidth: 180).Id("settings.language");
        var theme = new PPSegmented<string>(new[] { ("system", L.T("settings.followSystem")), ("light", L.T("settings.light")), ("dark", L.T("settings.dark")) },
            Bind.Of(() => string.IsNullOrEmpty(S.Theme) ? "system" : S.Theme, v =>
            {
                Theme.Apply(v);
                UiPrefs.Set("Theme", v);
                Update(x => x with { Theme = v }, ("theme", v));
            })).Id("settings.theme");
        var terminal = new PPDropdown<string>(Terminals.Select(t => new DropdownOption<string>(t, TerminalTitle(t))),
            Bind.Of(() => Terminals.Contains(S.Terminal ?? "") ? S.Terminal ?? "" : "", v => Update(x => x with { Terminal = v }, ("terminal", v))),
            minWidth: 180).Id("settings.terminal");
        return Section(L.T("settings.general"), null,
            SettingRow(L.T("settings.language"), language),
            SettingRow(L.T("settings.theme"), theme),
            SettingRow(L.T("settings.terminal"), terminal));
    }

    // MARK: 镜像

    private UIElement MirrorSection()
    {
        var s = S;
        var region = new PPSegmented<string>(new[] { ("auto", L.T("settings.mirror.auto")), ("china", L.T("settings.mirror.china")), ("global", L.T("settings.mirror.global")) },
            Bind.Of(() => string.IsNullOrEmpty(S.MirrorRegion) ? "auto" : S.MirrorRegion, v => Update(x => x with { MirrorRegion = v }, ("mirrorRegion", v))))
            .Id("settings.mirrorRegion");
        var m = _mirrors.Value;
        var regionRow = HStack(M.Md, region,
            m != null && !string.IsNullOrEmpty(m.Effective) ? Text(L.T("settings.mirror.effective", RegionTitle(m.Effective)), Typo.Small, ColorToken.TextSecondary) : null);
        UIElement? probes = null;
        if (m?.Probes is { Count: > 0 } list)
        {
            var rows = VStack(0, list.Select(p => HStack(M.Md,
                Box().Size(6, 6).Rounded(3).Bg(p.Ok ? ColorToken.StatusRunning : ColorToken.StatusFailed),
                Text(p.Name).W(160).Shrink(false),
                TextMiddle(p.Url, Typo.MonoSmall, ColorToken.TextTertiary),
                Spacer(),
                Text(p.Ok ? $"{(int)(p.Ms ?? 0)} ms" : L.T("settings.mirror.unreachable"), Typo.MonoSmall, p.Ok ? ColorToken.TextSecondary : ColorToken.Danger).Shrink(false))
                .H(M.RowHeightCompact)));
            rows.Align = CrossAlign.Stretch;
            rows.Margin = new Thickness(M.FormLabelWidth + M.Md, 0, 0, 0);
            probes = rows;
        }
        var probe = HStack(0, Gap(M.FormLabelWidth + M.Md).H(1),
            new PPButton(L.T("settings.mirror.probe"), Probe, icon: "gauge", size: PPButtonSize.Small, busy: _probing.Value).Id("settings.probe"));
        return Section(L.T("settings.mirrors"), L.T("settings.mirrors.info"),
            SettingRow(L.T("settings.mirrorRegion"), regionRow), probes, probe);
    }

    private async void Probe()
    {
        _probing.Value = true;
        try { _mirrors.Value = await AppModel.Shared.Client().MirrorsProbeAsync(); }
        catch (Exception e) { AppModel.Shared.Toasts.Error(e); }
        _probing.Value = false;
    }

    private static string RegionTitle(string r) => r == "china" ? L.T("settings.mirror.china") : r == "global" ? L.T("settings.mirror.global") : r;

    // MARK: 自动化、插件、日志、Git

    private UIElement Automation()
    {
        return Section(L.T("settings.behavior"), null,
            SettingRow(L.T("settings.autoToolchains"), new PPSwitch(Bind.Of(() => S.AutoInstallToolchains,
                v => Update(x => x with { AutoInstallToolchains = v }, ("autoInstallToolchains", v)))).Id("settings.autoToolchains"), L.T("settings.autoToolchains.info")),
            SettingRow(L.T("settings.autoDeps"), new PPSwitch(Bind.Of(() => S.AutoInstallDependencies,
                v => Update(x => x with { AutoInstallDependencies = v }, ("autoInstallDependencies", v)))).Id("settings.autoDeps"), L.T("settings.autoDeps.info")),
            SettingRow(L.T("settings.autoSync"), new PPSwitch(Bind.Of(() => S.AutoSyncOnImport,
                v => Update(x => x with { AutoSyncOnImport = v }, ("autoSyncOnImport", v)))).Id("settings.autoSync"), L.T("settings.autoSync.info")),
            new PPDivider(subtle: true),
            SettingRow(L.T("settings.pluginIdle"), Number("pluginIdle", S.PluginIdleSeconds, L.T("unit.seconds"),
                v => Update(x => x with { PluginIdleSeconds = v }, ("pluginIdleSeconds", v))), L.T("settings.pluginIdle.info")),
            SettingRow(L.T("settings.logLines"), Number("logLines", S.LogLinesPerRun, L.T("unit.lines"),
                v => Update(x => x with { LogLinesPerRun = v }, ("logLinesPerRun", v))), L.T("settings.logLines.info")),
            SettingRow(L.T("settings.logRetention"), Number("logRetention", S.LogRetentionRuns, L.T("unit.runs"),
                v => Update(x => x with { LogRetentionRuns = v }, ("logRetentionRuns", v)))),
            SettingRow(L.T("settings.gitFetch"), Number("gitFetch", S.GitAutoFetchMinutes, L.T("unit.minutes"),
                v => Update(x => x with { GitAutoFetchMinutes = v }, ("gitAutoFetchMinutes", v))), L.T("settings.gitFetch.info")));
    }

    /// <summary>数字设置跨重建复用（输入中、提交后焦点都在）。</summary>
    private NumberSetting Number(string key, int value, string unit, Action<int> commit) =>
        Keep("num." + key + "." + L10n.Shared.Locale, () => new NumberSetting(value, unit, commit).Id("settings." + key), n => n.SetValue(value));

    // MARK: API 与 MCP

    private UIElement ApiSection()
    {
        var info = _model.SystemInfo;
        var mcp = _mcp.Value;
        var token = HStack(M.Sm, Text(new string('•', 16), Typo.Mono, ColorToken.TextSecondary).Shrink(false),
            new PPButton(L.T("settings.copyToken"), () =>
            {
                if ((mcp?.Token ?? _model.Api?.Token) is { Length: > 0 } t) Actions.Copy(t);
            }, icon: "key-round", size: PPButtonSize.Small).Id("settings.copyToken"));
        var claudeCode = HStack(M.Sm, TextMiddle(mcp?.ClaudeCode ?? "", Typo.MonoSmall, ColorToken.TextSecondary),
            new PPButton(L.T("common.copy"), () => { if (mcp?.ClaudeCode is { } c) Actions.Copy(c); }, icon: "copy", size: PPButtonSize.Small).Disabled(mcp == null));
        var desktop = new PPButton(L.T("settings.copyConfig"), () => { if (mcp?.ClaudeDesktop is { } cfg) Actions.Copy(PrettyJson(cfg)); },
            icon: "copy", size: PPButtonSize.Small).Disabled(mcp?.ClaudeDesktop == null);
        var cli = new PPButton(L.T("settings.installCli"), () => AppModel.Shared.Perform(L.T("settings.installCli"), async api =>
        {
            var r = await api.InstallCLIAsync();
            AppModel.Shared.Toasts.Show("success", L.T("settings.cliInstalled"), r.Path);
        }), icon: "terminal", size: PPButtonSize.Small).Id("settings.installCli");
        return Section(L.T("settings.api"), L.T("settings.api.info"),
            SettingRow(L.T("settings.apiBase"), Copyable(info?.ApiBase ?? "")),
            SettingRow(L.T("settings.mcpUrl"), Copyable(mcp?.Url is { Length: > 0 } u ? u : info?.McpUrl ?? "")),
            SettingRow(L.T("settings.token"), token),
            SettingRow(L.T("settings.claudeCode"), claudeCode),
            SettingRow(L.T("settings.claudeDesktop"), desktop),
            SettingRow(L.T("settings.cli"), cli, L.T("settings.cli.info")));
    }

    private static UIElement Copyable(string text) =>
        HStack(M.Sm, Selectable(text, Typo.Mono, wrap: false), new PPIconButton("copy", L.T("common.copy"), () => Actions.Copy(text), size: M.IconButtonSmall));

    /// <summary>格式化 JSON（键排序，同 Mac 的 .prettyPrinted + .sortedKeys）。</summary>
    private static string PrettyJson(JToken v)
    {
        static JToken Sort(JToken t) => t switch
        {
            JObject o => new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Sort(p.Value)))),
            JArray a => new JArray(a.Select(Sort)),
            _ => t.DeepClone(),
        };
        return Sort(v).ToString(Formatting.Indented);
    }

    // MARK: 关于

    private UIElement About()
    {
        var info = _model.SystemInfo;
        var ui = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                 ?? typeof(SettingsPage).Assembly.GetName().Version?.ToString() ?? "";
        var list = new PPKeyValueList(new[]
        {
            new PPKeyValueRow(L.T("settings.about.ui"), ui, mono: true),
            new PPKeyValueRow(L.T("settings.about.daemon"), info?.Version ?? "-", mono: true),
            new PPKeyValueRow(L.T("settings.about.daemonMemory"), Format.Memory(info?.MemoryMB ?? 0), mono: true),
            new PPKeyValueRow(L.T("settings.about.pluginProcs"), (info?.PluginProcs ?? 0).ToString(), mono: true),
            new PPKeyValueRow(L.T("settings.about.pluginMemory"), Format.Memory(info?.PluginMemoryMB ?? 0), mono: true),
            new PPKeyValueRow(L.T("settings.about.dataDir"), Format.AbbreviatePath(info?.DataDir ?? ""), mono: true),
        }, M.FormLabelWidth);
        return new PPSection(L.T("settings.about"), new PPCard(list)).Id("settings.about");
    }

    // MARK: 保存

    /// <summary>改设置：先本地生效（避免控件回跳），再 settings.update，结果以内核为准。</summary>
    private void Update(Func<AppSettings, AppSettings> local, (string Key, object? Value) patch)
    {
        if (_data.Settings is { } cur) _data.Settings = local(cur);
        var body = Json.Object(patch);
        AppModel.Shared.Perform(L.T("settings.saveFailed"), async api =>
        {
            var s = await api.UpdateSettingsAsync(body);
            AppModel.Shared.ApplySettings(s);
            if (patch.Key == "mirrorRegion")
            {
                try { _mirrors.Value = await api.MirrorsStatusAsync(); } catch { }
            }
        });
    }

    // MARK: 布局小工具

    /// <summary>区块：标题（可带 i）+ 卡片里一列设置行。</summary>
    internal static UIElement Section(string title, string? info, params object?[] rows)
    {
        var col = VStack(M.Md, rows);
        col.Align = CrossAlign.Stretch;
        return new PPSection(title, new PPCard(col), info: info);
    }

    /// <summary>设置行：标题（可带 i，固定 160 宽）+ 控件（对应 Mac SettingRow）。</summary>
    internal static UIElement SettingRow(string title, UIElement content, string? info = null)
    {
        var label = HStack(M.Xs, Text(title), info != null ? new PPInfoButton(info) : null).W(M.FormLabelWidth).Shrink(false);
        var row = HStack(M.Md, label, content.Shrink(), Spacer(0));
        row.MinHeight = M.RowHeight;
        return row;
    }
}

/// <summary>数字设置：回车或失焦时提交（非负整数，不合法时恢复原值）。对应 Mac NumberSetting。</summary>
public sealed class NumberSetting : Stack
{
    private readonly State<string> _text;
    private readonly PPTextField _field;
    private readonly Action<int> _commit;
    private int _value;

    public NumberSetting(int value, string unit, Action<int> commit) : base(Orientation.Horizontal, M.Sm)
    {
        _value = value;
        _commit = commit;
        _text = new State<string>(value.ToString());
        _field = new PPTextField("", _text, mono: true, onSubmit: Apply).W(110);
        _field.TextBox!.LostKeyboardFocus += (_, _) => Apply();
        Children.Add(_field);
        if (unit.Length > 0) Children.Add(UI.Text(unit, Typo.Body, ColorToken.TextSecondary).Shrink(false));
    }

    /// <summary>外部值变了：没在输入时同步显示。</summary>
    public void SetValue(int v)
    {
        _value = v;
        if (_field.IsKeyboardFocusWithin) return;
        _text.Value = v.ToString();
        _field.Sync();
    }

    private void Apply()
    {
        if (!int.TryParse(_text.Value.Trim(), out var v) || v < 0)
        {
            _text.Value = _value.ToString();
            _field.Sync();
            return;
        }
        if (v != _value) _commit(v);
    }
}

/// <summary>文本设置：回车或失焦时提交。对应 Mac TextSetting。</summary>
public sealed class TextSetting : Decorator
{
    private readonly State<string> _text;
    private readonly PPTextField _field;
    private readonly Action<string> _commit;
    private string _value;

    public TextSetting(string value, string placeholder, Action<string> commit)
    {
        _value = value;
        _commit = commit;
        _text = new State<string>(value);
        _field = new PPTextField(placeholder, _text, onSubmit: Apply).W(240);
        _field.TextBox!.LostKeyboardFocus += (_, _) => Apply();
        Child = _field;
    }

    public void SetValue(string v)
    {
        _value = v;
        if (_field.IsKeyboardFocusWithin) return;
        _text.Value = v;
        _field.Sync();
    }

    private void Apply()
    {
        var v = _text.Value.Trim();
        if (v != _value) _commit(v);
    }
}

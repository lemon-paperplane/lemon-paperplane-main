using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>设置页「编辑器」分组：写入 settings.editor（docs/editor.md 第 3 节）。对应 Mac 的 EditorSettingsSection。</summary>
public sealed class EditorSettingsSection : PPView
{
    private readonly DataStore _data = AppModel.Shared.Data;

    public EditorSettingsSection()
    {
        Observe(_data, nameof(DataStore.Settings), nameof(DataStore.Contributions));
    }

    private EditorSettings E => _data.Settings?.Editor ?? new EditorSettings();

    /// <summary>本平台默认的方案 id（Windows 上 eclipse-windows）。</summary>
    private string PlatformDefault => _data.Contributions.DefaultKeymap ?? EditorSettings.PlatformDefaultKeymap;

    protected override UIElement Build()
    {
        var e = E;
        var keymap = new PPDropdown<string>(KeymapOptions(),
            Bind.Of(() => string.IsNullOrEmpty(E.Keymap) ? PlatformDefault : E.Keymap, v => Update(x => x with { Keymap = v == PlatformDefault ? "" : v })),
            minWidth: 240).Id("editor.keymap");
        var theme = new PPDropdown<string>(ThemeOptions(), Bind.Of(() => E.Theme, v => Update(x => x with { Theme = v })), minWidth: 240).Id("editor.theme");
        var fontSize = Keep("fontSize." + L10n.Shared.Locale, () => new NumberSetting(e.FontSize, "pt", v => Update(x => x with { FontSize = Math.Min(Math.Max(v, 8), 40) })),
            n => n.SetValue(E.FontSize));
        var fontFamily = Keep("fontFamily." + L10n.Shared.Locale, () => new TextSetting(e.FontFamily, L.T("settings.editor.fontFamily.default"), v => Update(x => x with { FontFamily = v })),
            t => t.SetValue(E.FontFamily));
        var tabSize = Keep("tabSize." + L10n.Shared.Locale, () => new NumberSetting(e.TabSize, "", v => Update(x => x with { TabSize = Math.Min(Math.Max(v, 1), 16) })),
            n => n.SetValue(E.TabSize));
        var indent = new PPSegmented<bool>(new[] { (true, L.T("settings.editor.spaces")), (false, L.T("settings.editor.tabs")) },
            Bind.Of(() => E.InsertSpaces, v => Update(x => x with { InsertSpaces = v })));
        var autoSave = new PPDropdown<string>(new[]
        {
            new DropdownOption<string>("off", L.T("settings.editor.autoSave.off")),
            new DropdownOption<string>("onFocusChange", L.T("settings.editor.autoSave.onFocusChange")),
            new DropdownOption<string>("afterDelay", L.T("settings.editor.autoSave.afterDelay")),
        }, Bind.Of(() => string.IsNullOrEmpty(E.AutoSave) ? "off" : E.AutoSave, v => Update(x => x with { AutoSave = v })), minWidth: 240);

        return SettingsPage.Section(L.T("settings.editor"), L.T("settings.editor.info"),
            SettingsPage.SettingRow(L.T("settings.editor.keymap"), keymap),
            SettingsPage.SettingRow(L.T("settings.editor.theme"), theme),
            SettingsPage.SettingRow(L.T("settings.editor.fontSize"), fontSize),
            SettingsPage.SettingRow(L.T("settings.editor.fontFamily"), fontFamily),
            SettingsPage.SettingRow(L.T("settings.editor.tabSize"), tabSize),
            SettingsPage.SettingRow(L.T("settings.editor.indent"), indent),
            SettingsPage.SettingRow(L.T("settings.editor.wordWrap"), Switch(() => E.WordWrap, v => x => x with { WordWrap = v })),
            SettingsPage.SettingRow(L.T("settings.editor.autoSave"), autoSave),
            SettingsPage.SettingRow(L.T("settings.editor.trim"), Switch(() => E.TrimTrailingWhitespace, v => x => x with { TrimTrailingWhitespace = v })),
            SettingsPage.SettingRow(L.T("settings.editor.finalNewline"), Switch(() => E.InsertFinalNewline, v => x => x with { InsertFinalNewline = v })),
            SettingsPage.SettingRow(L.T("settings.editor.showWhitespace"), Switch(() => E.ShowWhitespace, v => x => x with { ShowWhitespace = v })));
    }

    private PPSwitch Switch(Func<bool> get, Func<bool, Func<EditorSettings, EditorSettings>> change) =>
        new(Bind.Of(get, v => Update(change(v))));

    /// <summary>快捷键方案：本平台与通用的在前；没有插件时也显示默认方案名。</summary>
    private IEnumerable<DropdownOption<string>> KeymapOptions()
    {
        var def = PlatformDefault;
        var list = _data.Contributions.Keymaps.ToList();
        if (list.All(k => k.Id != def))
            list.Insert(0, new KeymapInfo { Id = def, Name = "Eclipse (Windows)", Platform = "windows" });
        var order = new Dictionary<string, int> { ["windows"] = 0, ["any"] = 1 };
        int Rank(KeymapInfo k) => order.TryGetValue(k.Platform, out var r) ? r : 2;
        return list.OrderBy(Rank).ThenBy(k => k.Name, StringComparer.Ordinal).Select(k =>
        {
            var detail = PlatformTitle(k.Platform);
            if (k.Id == def) detail += " · " + L.T("settings.editor.keymap.default");
            return new DropdownOption<string>(k.Id, string.IsNullOrEmpty(k.Name) ? k.Id : k.Name, detail: detail);
        });
    }

    private IEnumerable<DropdownOption<string>> ThemeOptions() =>
        new[] { new DropdownOption<string>("", L.T("settings.editor.theme.followApp")) }
            .Concat(_data.Contributions.EditorThemes.Select(t => new DropdownOption<string>(t.Id, string.IsNullOrEmpty(t.Name) ? t.Id : t.Name,
                detail: t.Kind == "dark" ? L.T("settings.dark") : L.T("settings.light"))));

    private static string PlatformTitle(string p) => p switch
    {
        "macos" => "macOS",
        "windows" => "Windows",
        "linux" => "Linux",
        _ => L.T("settings.editor.platform.any"),
    };

    /// <summary>改一项：本地先生效，再把整个 editor 对象写回（settings.update 做 JSON merge）。</summary>
    private void Update(Func<EditorSettings, EditorSettings> change)
    {
        var cur = E;
        var next = change(cur);
        if (next == cur) return;
        if (_data.Settings is { } s) _data.Settings = s with { Editor = next };
        var body = Json.Object(("editor", Json.ToToken(next)));
        AppModel.Shared.Perform(L.T("settings.saveFailed"), async api =>
        {
            var res = await api.UpdateSettingsAsync(body);
            AppModel.Shared.ApplySettings(res);
        });
    }
}

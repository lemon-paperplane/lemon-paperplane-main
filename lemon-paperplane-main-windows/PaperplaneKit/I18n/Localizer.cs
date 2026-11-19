using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PaperplaneKit;

/// <summary>
/// 界面文案（shared/i18n/&lt;locale&gt;.json，扁平 key → 文本，{0}{1}… 占位）。三端共用同一份文件。
/// 查找顺序：当前语言 → en → key 本身。对应 Mac 的 Localizer.swift。
/// </summary>
public sealed class Localizer
{
    public static readonly string[] Supported = { "zh-Hans", "en" };

    private readonly Func<string, Dictionary<string, string>>? _loader;
    private readonly object _lock = new();
    private Dictionary<string, string> _table = new();
    private Dictionary<string, string> _fallback = new();

    public string Locale { get; private set; }

    /// <summary>用加载函数（locale → 表）创建，例如 Localizer.Embedded(locale)。</summary>
    public Localizer(Func<string, Dictionary<string, string>> loader, string locale)
    {
        _loader = loader;
        Locale = locale;
        Reload();
    }

    /// <summary>测试用：直接给表。</summary>
    public Localizer(string locale, IDictionary<string, string> table, IDictionary<string, string>? fallback = null)
    {
        Locale = locale;
        _table = new Dictionary<string, string>(table);
        _fallback = fallback == null ? new Dictionary<string, string>() : new Dictionary<string, string>(fallback);
    }

    /// <summary>从 PaperplaneKit.dll 里的嵌入资源 shared/i18n/*.json 读取。</summary>
    public static Localizer Embedded(string locale) => new(LoadEmbedded, locale);

    /// <summary>从目录读取（目录里是 zh-Hans.json、en.json）。</summary>
    public static Localizer FromDirectory(string directory, string locale) =>
        new(l => Load(Path.Combine(directory, l + ".json")), locale);

    public void SetLocale(string locale)
    {
        lock (_lock) Locale = locale;
        Reload();
    }

    public void Reload()
    {
        if (_loader == null) return;
        var l = Locale;
        var t = _loader(l);
        var f = l == "en" ? t : _loader("en");
        lock (_lock)
        {
            _table = t;
            _fallback = f;
        }
    }

    /// <summary>取文案并替换 {0}{1}…；找不到时返回 key 本身。</summary>
    public string T(string key, IReadOnlyList<string>? args)
    {
        string? s;
        lock (_lock)
        {
            if (!_table.TryGetValue(key, out s)) _fallback.TryGetValue(key, out s);
        }
        return Format(s ?? key, args);
    }

    public string T(string key, params string[] args) => T(key, (IReadOnlyList<string>)args);

    public bool Has(string key)
    {
        lock (_lock) return _table.ContainsKey(key) || _fallback.ContainsKey(key);
    }

    public IReadOnlyCollection<string> Keys
    {
        get { lock (_lock) return _table.Keys.ToArray(); }
    }

    public static string Format(string s, IReadOnlyList<string>? args)
    {
        if (args == null || args.Count == 0 || s.IndexOf('{') < 0) return s;
        var output = s;
        for (int i = 0; i < args.Count; i++) output = output.Replace("{" + i + "}", args[i]);
        return output;
    }

    public static Dictionary<string, string> Parse(string json)
    {
        try { return JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(); }
        catch { return new Dictionary<string, string>(); }
    }

    public static Dictionary<string, string> Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch { return new Dictionary<string, string>(); }
    }

    /// <summary>基础文案再盖上 Windows 的说法（shared/i18n/windows/&lt;locale&gt;.json：访达 → 资源管理器、⌘ → Ctrl 等）。</summary>
    public static Dictionary<string, string> LoadEmbedded(string locale)
    {
        var text = SharedAssets.ReadText("i18n/" + locale + ".json");
        var table = text == null ? new Dictionary<string, string>() : Parse(text);
        var overlay = SharedAssets.ReadText("i18n/windows/" + locale + ".json");
        if (overlay != null)
            foreach (var kv in Parse(overlay)) table[kv.Key] = kv.Value;
        return table;
    }

    /// <summary>
    /// 设置值（system | zh-Hans | en）→ 实际语言。system 时按系统首选语言：中文（任意变体）用 zh-Hans，其余用 en。
    /// preferredLanguages 不给时用 Windows 的界面语言（CurrentUICulture、InstalledUICulture）。
    /// </summary>
    public static string Resolve(string? setting, IEnumerable<string>? preferredLanguages = null)
    {
        if (setting != null && Supported.Contains(setting)) return setting;
        foreach (var lang in preferredLanguages ?? SystemLanguages())
        {
            if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
        }
        return "en";
    }

    public static IEnumerable<string> SystemLanguages()
    {
        yield return CultureInfo.CurrentUICulture.Name;
        yield return CultureInfo.InstalledUICulture.Name;
    }
}

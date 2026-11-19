using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using PaperplaneKit;
using MColor = System.Windows.Media.Color;

namespace LemonPaperplane;

/// <summary>语气色的三个资源键：前景、浅底、描边。</summary>
public readonly struct ToneKeys
{
    public readonly string Fg;
    public readonly string Bg;
    public readonly string Border;
    public ToneKeys(string fg, string bg, string border) { Fg = fg; Bg = bg; Border = border; }
}

/// <summary>
/// 主题：亮 / 暗两套资源字典（每个颜色变量一个 SolidColorBrush，键名同 JSON；另有阴影 Effect），
/// 放在 Application.Resources.MergedDictionaries 里。切换只换字典，界面里一律用动态资源引用（el.Bg("bgContent")），不用重建。
/// 设置值 theme = system | light | dark；system 跟随 Windows「应用模式」（注册表 AppsUseLightTheme，系统设置变化时自动切换）。
/// 对应 Mac 的 Theme.swift + Appearance。
/// </summary>
public static class Theme
{
    private static ResourceDictionary? _light;
    private static ResourceDictionary? _dark;
    private static ResourceDictionary? _current;
    private static Application? _app;
    private static HashSet<string>? _keys;

    /// <summary>设置值：system | light | dark</summary>
    public static string Setting { get; private set; } = "system";

    /// <summary>当前生效的是否暗色。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>生效主题变化（UI 线程）。只有自绘（OnRender）或读 Theme.Color 的代码需要订阅；用资源引用的元素自动更新。</summary>
    public static event EventHandler? Changed;

    private const string PrefName = "Theme";

    /// <summary>启动时调用一次：建两套字典，按保存的设置（或系统）选一套，开始监听系统主题。</summary>
    public static void Install(Application app)
    {
        _app = app;
        _light = Build(false);
        _dark = Build(true);
        Setting = Normalize(UiPrefs.Get(PrefName));
        IsDark = Resolve(Setting);
        _current = IsDark ? _dark : _light;
        app.Resources.MergedDictionaries.Add(_current);
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
    }

    /// <summary>应用设置值（来自服务的 settings.theme，或设置页切换）。persist=false 时不写注册表（调试桥用）。</summary>
    public static void Apply(string? setting, bool persist = true)
    {
        Setting = Normalize(setting);
        if (persist) UiPrefs.Set(PrefName, Setting);
        Refresh();
    }

    private static string Normalize(string? s) => s is "light" or "dark" ? s : "system";

    private static bool Resolve(string setting) => setting switch
    {
        "dark" => true,
        "light" => false,
        _ => !SystemAppsUseLightTheme,
    };

    private static void Refresh()
    {
        if (_app == null || _light == null || _dark == null) return;
        var dark = Resolve(Setting);
        if (_current != null && dark == IsDark) return;
        IsDark = dark;
        var next = dark ? _dark : _light;
        var md = _app.Resources.MergedDictionaries;
        int idx = _current == null ? -1 : md.IndexOf(_current);
        if (idx >= 0) md[idx] = next;
        else md.Add(next);
        _current = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color && e.Category != UserPreferenceCategory.VisualStyle) return;
        _app?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Setting == "system") Refresh();
            SystemThemeChanged?.Invoke(null, EventArgs.Empty);
        }));
    }

    /// <summary>系统主题（应用模式或任务栏模式）变化，托盘图标据此换色。</summary>
    public static event EventHandler? SystemThemeChanged;

    /// <summary>Windows「默认应用模式」是否为浅色（HKCU\…\Themes\Personalize\AppsUseLightTheme，读不到按浅色）。</summary>
    public static bool SystemAppsUseLightTheme => ReadPersonalize("AppsUseLightTheme") != 0;

    /// <summary>Windows「默认 Windows 模式」（任务栏、托盘）是否为浅色。</summary>
    public static bool SystemUsesLightTheme => ReadPersonalize("SystemUsesLightTheme") != 0;

    private static int ReadPersonalize(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue(name) is int v ? v : 1;
        }
        catch { return 1; }
    }

    // MARK: 字典

    private static ResourceDictionary Build(bool dark)
    {
        var t = DesignTokens.Shared;
        var theme = dark ? "dark" : "light";
        var d = new ResourceDictionary();
        foreach (var key in ColorToken.All)
        {
            var b = new SolidColorBrush(ParseColor(t.ColorHex(theme, key) ?? "#FF00FF"));
            b.Freeze();
            d[key] = b;
        }
        d[ColorToken.Clear] = Brushes.Transparent;
        var shadowColor = ParseColor(t.ColorHex(theme, "shadow") ?? "#000000");
        double factor = dark ? 2.0 : 1.0; // 暗色主题阴影不透明度加大
        foreach (var (key, name) in new[] { (ShadowToken.Popup, "popup"), (ShadowToken.Dialog, "dialog"), (ShadowToken.Card, "card"), (ShadowToken.Toast, "toast") })
        {
            t.Shadow.TryGetValue(name, out var s);
            d[key] = Shadow(shadowColor, s?.Y ?? 4, s?.Blur ?? 16, Math.Min(1, (s?.Opacity ?? 0.14) * factor));
        }
        d[ShadowToken.Hairline] = Shadow(shadowColor, 0.5, 2, dark ? 0.3 : 0.1);
        return d;
    }

    private static DropShadowEffect Shadow(Color c, double y, double blur, double opacity)
    {
        var e = new DropShadowEffect
        {
            Color = MColor.FromRgb(c.R, c.G, c.B),
            Direction = 270,
            ShadowDepth = y,
            BlurRadius = blur,
            Opacity = opacity,
            RenderingBias = RenderingBias.Performance,
        };
        e.Freeze();
        return e;
    }

    /// <summary>#rgb / #rrggbb / #rrggbbaa → Color（解析失败返回洋红，一眼能看出来）。</summary>
    public static Color ParseColor(string hex)
    {
        var c = SvgParser.ParseColor(hex);
        if (c == null) return Colors.Magenta;
        var v = c.Value;
        return MColor.FromArgb(ToByte(v.A), ToByte(v.R), ToByte(v.G), ToByte(v.B));
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);

    // MARK: 取值

    /// <summary>当前主题下的画刷（自绘时用；元素属性请用 SetResourceReference / el.Bg()，切换主题才会跟着变）。</summary>
    public static Brush Brush(string key)
    {
        CheckKey(key);
        if (_current != null && _current[key] is Brush b) return b;
        return Brushes.Magenta;
    }

    /// <summary>当前主题下的颜色值。</summary>
    public static Color Color(string key) => Brush(key) is SolidColorBrush sb ? sb.Color : Colors.Magenta;

    /// <summary>某套主题下的颜色值（不管当前生效哪套）。</summary>
    public static Color Color(string key, bool dark) => ParseColor(DesignTokens.Shared.ColorHex(dark ? "dark" : "light", key) ?? "#FF00FF");

    /// <summary>语气（tone）→ 资源键：neutral / info / success / warning / danger（或 error）。</summary>
    public static ToneKeys Tone(string? tone) => tone switch
    {
        "success" => new ToneKeys(ColorToken.Success, ColorToken.SuccessSubtle, ColorToken.SuccessBorder),
        "warning" => new ToneKeys(ColorToken.Warning, ColorToken.WarningSubtle, ColorToken.WarningBorder),
        "danger" or "error" => new ToneKeys(ColorToken.Danger, ColorToken.DangerSubtle, ColorToken.DangerBorder),
        "info" => new ToneKeys(ColorToken.Info, ColorToken.InfoSubtle, ColorToken.InfoBorder),
        _ => new ToneKeys(ColorToken.TextSecondary, ColorToken.NeutralSubtle, ColorToken.NeutralBorder),
    };

    /// <summary>诊断严重度：error → danger，warning → warning，其余 → info。</summary>
    public static ToneKeys Severity(string? severity) => severity switch
    {
        "error" => Tone("danger"),
        "warning" => Tone("warning"),
        _ => Tone("info"),
    };

    /// <summary>提交图车道颜色键（color % 8，负数从末尾取）。</summary>
    public static string GraphLane(int index)
    {
        int i = ((index % 8) + 8) % 8;
        return "graphLane" + i;
    }

    /// <summary>16 个 ANSI 基础色（当前主题）。</summary>
    public static Color Ansi(int index)
    {
        var list = DesignTokens.Shared.Ansi.TryGetValue(IsDark ? "dark" : "light", out var l) ? l : null;
        if (list == null || index < 0 || index >= list.Count) return Colors.Magenta;
        return ParseColor(list[index]);
    }

    public static bool IsKnownKey(string key)
    {
        _keys ??= new HashSet<string>(ColorToken.All.Concat(new[] { ColorToken.Clear, ShadowToken.Popup, ShadowToken.Dialog, ShadowToken.Card, ShadowToken.Toast, ShadowToken.Hairline }));
        return _keys.Contains(key);
    }

    /// <summary>Debug 编译时检查资源键（写错的键名直接抛异常，Release 不检查）。</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void CheckKey(string key)
    {
        if (!IsKnownKey(key)) throw new ArgumentException("未知的设计变量：" + key);
    }
}

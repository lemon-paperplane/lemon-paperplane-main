using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PaperplaneKit;

/// <summary>shared/design-tokens.json 的结构（三端共用的设计变量）。对应 Mac 的 DesignTokens.swift。</summary>
public sealed class DesignTokens
{
    public sealed class TextStyle
    {
        public double Size { get; set; } = 13;
        public int Weight { get; set; } = 400;
        public double LineHeight { get; set; } = 18;

        public TextStyle() { }
        public TextStyle(double size, int weight, double lineHeight) { Size = size; Weight = weight; LineHeight = lineHeight; }
    }

    public sealed class StrokeStep
    {
        public double MaxSize { get; set; }
        public double Scale { get; set; } = 1;
    }

    public sealed class IconTokens
    {
        public double Size { get; set; } = 16;
        public double SizeSmall { get; set; } = 14;
        public double SizeLarge { get; set; } = 20;
        public double SizeEmpty { get; set; } = 32;
        public List<StrokeStep> StrokeScale { get; set; } = new();

        /// <summary>按显示尺寸取线宽系数（≤16 → 0.75，≤20 → 0.875，更大 → 1）。</summary>
        public double StrokeScaleForSize(double size)
        {
            foreach (var s in StrokeScale.OrderBy(s => s.MaxSize))
                if (size <= s.MaxSize) return s.Scale;
            return 1;
        }
    }

    public sealed class ShadowStyle
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Blur { get; set; }
        public double Opacity { get; set; }
    }

    public int Version { get; set; }
    /// <summary>font.ui / font.mono → 平台（macos / windows / linux）→ 逗号分隔的字体族列表</summary>
    public Dictionary<string, Dictionary<string, string>> Font { get; set; } = new();
    public Dictionary<string, TextStyle> Typography { get; set; } = new();
    public Dictionary<string, double> Spacing { get; set; } = new();
    public Dictionary<string, double> Radius { get; set; } = new();
    public Dictionary<string, double> Size { get; set; } = new();
    public IconTokens Icon { get; set; } = new();
    /// <summary>阴影（popup / dialog / card / toast）</summary>
    public Dictionary<string, ShadowStyle> Shadow { get; set; } = new();
    public Dictionary<string, double> Motion { get; set; } = new();
    /// <summary>theme（light/dark）→ 颜色名 → #RRGGBB(AA)</summary>
    public Dictionary<string, Dictionary<string, string>> Color { get; set; } = new();
    /// <summary>theme → 16 个 ANSI 颜色</summary>
    public Dictionary<string, List<string>> Ansi { get; set; } = new();

    public static DesignTokens Parse(string json)
    {
        var t = JsonConvert.DeserializeObject<DesignTokens>(json, new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
        });
        return t ?? throw new InvalidDataException("design-tokens.json 为空");
    }

    public static DesignTokens Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>从 PaperplaneKit.dll 里的嵌入资源 shared/design-tokens.json 读取。</summary>
    public static DesignTokens LoadEmbedded()
    {
        var text = SharedAssets.ReadText("design-tokens.json") ?? throw new FileNotFoundException("缺少嵌入资源 shared/design-tokens.json");
        return Parse(text);
    }

    private static DesignTokens? _shared;
    private static readonly object SharedLock = new();

    /// <summary>全局一份（第一次用到时从嵌入资源读取；读取失败时为空表，取值处各自有默认值）。</summary>
    public static DesignTokens Shared
    {
        get
        {
            if (_shared != null) return _shared;
            lock (SharedLock)
            {
                if (_shared == null)
                {
                    try { _shared = LoadEmbedded(); }
                    catch { _shared = new DesignTokens(); }
                }
                return _shared;
            }
        }
    }

    /// <summary>界面代码要求每个主题都提供的颜色名（与 Mac DesignTokens.requiredColors 一致）。</summary>
    public static readonly string[] RequiredColors =
    {
        "bgChrome", "bgContent", "bgSurface", "bgSubtle", "bgElevated", "bgInput", "bgInputDisabled", "bgHover",
        "bgPressed", "bgSelected", "bgSelectedInactive", "bgCode", "border", "borderSubtle", "borderStrong",
        "borderFocus", "focusRing", "textPrimary", "textSecondary", "textTertiary", "textDisabled", "textOnAccent",
        "textLink", "iconPrimary", "iconSecondary", "accent", "accentHover", "accentPressed", "accentSubtle",
        "accentSubtleBorder", "success", "successSubtle", "successBorder", "warning", "warningSubtle",
        "warningBorder", "danger", "dangerHover", "dangerSubtle", "dangerBorder", "info", "infoSubtle", "infoBorder",
        "neutralSubtle", "neutralBorder", "statusIdle", "statusStarting", "statusRunning", "statusFailed",
        "statusStopping", "buttonSecondaryBg", "buttonSecondaryHover", "buttonSecondaryBorder", "switchOff", "scrim",
        "shadow", "consoleBg", "consoleText", "consoleSystem", "consoleStderr", "consoleSelection", "searchMatch",
        "searchMatchCurrent", "scrollbar", "graphLane0", "graphLane1", "graphLane2", "graphLane3", "graphLane4",
        "graphLane5", "graphLane6", "graphLane7", "diffAddedBg", "diffAddedText", "diffRemovedBg",
        "diffRemovedText", "diffHunkBg", "brandLemon",
    };

    /// <summary>校验：返回缺失项（主题.颜色名）。</summary>
    public List<string> MissingColors()
    {
        var output = new List<string>();
        foreach (var theme in new[] { "light", "dark" })
        {
            Color.TryGetValue(theme, out var m);
            m ??= new Dictionary<string, string>();
            foreach (var k in RequiredColors)
                if (!m.ContainsKey(k)) output.Add(theme + "." + k);
            if (!Ansi.TryGetValue(theme, out var a) || a.Count != 16) output.Add("ansi." + theme);
        }
        return output;
    }

    // MARK: 取值（带默认值）

    public double SizeOf(string key, double fallback) => Size.TryGetValue(key, out var v) ? v : fallback;
    public double SpacingOf(string key, double fallback) => Spacing.TryGetValue(key, out var v) ? v : fallback;
    public double RadiusOf(string key, double fallback) => Radius.TryGetValue(key, out var v) ? v : fallback;
    public double MotionOf(string key, double fallback) => Motion.TryGetValue(key, out var v) ? v : fallback;

    public TextStyle TextStyleOf(string key) =>
        Typography.TryGetValue(key, out var s) ? s : new TextStyle(13, 400, 18);

    /// <summary>某个平台的字体族列表（例如 FontFamilies("ui", "windows") → ["Segoe UI Variable Text", "Segoe UI", …]）。</summary>
    public string[] FontFamilies(string kind, string platform)
    {
        if (Font.TryGetValue(kind, out var m) && m.TryGetValue(platform, out var list) && !string.IsNullOrWhiteSpace(list))
            return list.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        return Array.Empty<string>();
    }

    /// <summary>颜色十六进制值（theme = light / dark）。</summary>
    public string? ColorHex(string theme, string key) =>
        Color.TryGetValue(theme, out var m) && m.TryGetValue(key, out var v) ? v : null;
}

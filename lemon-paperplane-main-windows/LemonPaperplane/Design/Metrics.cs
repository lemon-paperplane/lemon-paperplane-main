using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>尺寸与间距（来自 design-tokens.json，名字与 Mac 的 M 一致，改成 C# 大小写）。</summary>
public static class M
{
    private static readonly DesignTokens T = DesignTokens.Shared;

    public static readonly double ToolbarHeight = T.SizeOf("toolbarHeight", 40);
    public static readonly double TabBarHeight = T.SizeOf("tabBarHeight", 36);
    public static readonly double RowHeight = T.SizeOf("rowHeight", 28);
    public static readonly double RowHeightCompact = T.SizeOf("rowHeightCompact", 24);
    public static readonly double ControlHeight = T.SizeOf("controlHeight", 28);
    public static readonly double ControlHeightSmall = T.SizeOf("controlHeightSmall", 24);
    public static readonly double IconButton = T.SizeOf("iconButton", 28);
    public static readonly double IconButtonSmall = T.SizeOf("iconButtonSmall", 22);
    public static readonly double SidebarWidth = T.SizeOf("sidebarWidth", 232);
    public static readonly double SidebarMinWidth = T.SizeOf("sidebarMinWidth", 180);
    public static readonly double SidebarMaxWidth = T.SizeOf("sidebarMaxWidth", 360);
    public static readonly double CardMinWidth = T.SizeOf("cardMinWidth", 300);
    public static readonly double CardMaxWidth = T.SizeOf("cardMaxWidth", 440);
    public static readonly double FormLabelWidth = T.SizeOf("formLabelWidth", 160);
    public static readonly double DialogWidth = T.SizeOf("dialogWidth", 480);
    public static readonly double DialogWidthLarge = T.SizeOf("dialogWidthLarge", 640);
    public static readonly double PopoverMaxWidth = T.SizeOf("popoverMaxWidth", 420);
    public static readonly double StatusDot = T.SizeOf("statusDot", 8);
    public static readonly double BorderWidth = T.SizeOf("borderWidth", 1);
    public static readonly double FocusRingWidth = T.SizeOf("focusRingWidth", 2);
    public static readonly int ConsoleMaxLines = (int)T.SizeOf("consoleMaxLines", 5000);

    public static readonly double Xxs = T.SpacingOf("xxs", 2);
    public static readonly double Xs = T.SpacingOf("xs", 4);
    public static readonly double Sm = T.SpacingOf("sm", 6);
    public static readonly double Md = T.SpacingOf("md", 8);
    public static readonly double Lg = T.SpacingOf("lg", 12);
    public static readonly double Xl = T.SpacingOf("xl", 16);
    public static readonly double Xxl = T.SpacingOf("xxl", 20);
    public static readonly double Xxxl = T.SpacingOf("xxxl", 24);
    public static readonly double Huge = T.SpacingOf("huge", 32);

    public static readonly double RadiusXS = T.RadiusOf("xs", 3);
    public static readonly double RadiusSM = T.RadiusOf("sm", 4);
    public static readonly double RadiusMD = T.RadiusOf("md", 6);
    public static readonly double RadiusLG = T.RadiusOf("lg", 8);
    public static readonly double RadiusXL = T.RadiusOf("xl", 12);

    public static readonly double Icon = T.Icon.Size > 0 ? T.Icon.Size : 16;
    public static readonly double IconSmall = T.Icon.SizeSmall > 0 ? T.Icon.SizeSmall : 14;
    public static readonly double IconLarge = T.Icon.SizeLarge > 0 ? T.Icon.SizeLarge : 20;
    public static readonly double IconEmpty = T.Icon.SizeEmpty > 0 ? T.Icon.SizeEmpty : 32;

    /// <summary>动效时长（毫秒）：fast 100、normal 160、slow 240，一律 ease-out。</summary>
    public static readonly double MotionFast = T.MotionOf("fast", 100);
    public static readonly double MotionNormal = T.MotionOf("normal", 160);
    public static readonly double MotionSlow = T.MotionOf("slow", 240);
    public static readonly double ToastSeconds = T.MotionOf("toastSeconds", 4);
    public static readonly double ToastErrorSeconds = T.MotionOf("toastErrorSeconds", 8);

    /// <summary>按显示尺寸取图标线宽系数（≤16 → 0.75，≤20 → 0.875，更大 → 1）。</summary>
    public static double StrokeScale(double iconSize) => T.Icon.StrokeScale.Count > 0 ? T.Icon.StrokeScaleForSize(iconSize) : (iconSize <= 16 ? 0.75 : 1);
}

/// <summary>
/// 字体层级（design-tokens.json 的 typography，规范第 3 节）。对应 Mac 的 PPText / .font(.pp(.title))。
/// 用法：text.Font(Typo.Title)；UI.Text("…", Typo.Small)。
/// </summary>
public sealed class Typo
{
    public string Name { get; }
    public double Size { get; }
    public FontWeight Weight { get; }
    public double LineHeight { get; }
    public bool IsMono { get; }
    public FontFamily Family => IsMono ? UIFonts.Mono : UIFonts.UI;

    private Typo(string name)
    {
        Name = name;
        var s = DesignTokens.Shared.TextStyleOf(name);
        Size = s.Size;
        Weight = ToWeight(s.Weight);
        LineHeight = s.LineHeight;
        IsMono = name is "mono" or "monoSmall";
    }

    public static readonly Typo Caption = new("caption");
    public static readonly Typo Small = new("small");
    public static readonly Typo Body = new("body");
    public static readonly Typo BodyStrong = new("bodyStrong");
    public static readonly Typo Label = new("label");
    public static readonly Typo Title = new("title");
    public static readonly Typo Heading = new("heading");
    public static readonly Typo Display = new("display");
    public static readonly Typo Stat = new("stat");
    public static readonly Typo Mono = new("mono");
    public static readonly Typo MonoSmall = new("monoSmall");

    public static FontWeight ToWeight(int w) => w switch
    {
        < 350 => FontWeights.Light,
        < 450 => FontWeights.Normal,
        < 550 => FontWeights.Medium,
        < 650 => FontWeights.SemiBold,
        _ => FontWeights.Bold,
    };

    /// <summary>应用到 TextBlock：字号、字重、字体、固定行高（与 Mac 行高一致，单行文字在控件里垂直居中）。</summary>
    public void Apply(TextBlock t)
    {
        t.FontFamily = Family;
        t.FontSize = Size;
        t.FontWeight = Weight;
        t.LineHeight = LineHeight;
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    /// <summary>应用到控件（TextBox 等）：字号、字重、字体。</summary>
    public void Apply(Control c)
    {
        c.FontFamily = Family;
        c.FontSize = Size;
        c.FontWeight = Weight;
        if (c is System.Windows.Controls.Primitives.TextBoxBase)
        {
            // 文本框的行高与 TextBlock 一致（可选中文字和普通文字并排时基线对齐）
            TextBlock.SetLineHeight(c, LineHeight);
            TextBlock.SetLineStackingStrategy(c, LineStackingStrategy.BlockLineHeight);
        }
    }

    /// <summary>作为继承属性设在容器上（子元素里没单独设字体的文字都用它）。</summary>
    public void ApplyInherited(DependencyObject d)
    {
        d.SetValue(TextElement.FontFamilyProperty, Family);
        d.SetValue(TextElement.FontSizeProperty, Size);
        d.SetValue(TextElement.FontWeightProperty, Weight);
    }

    public override string ToString() => Name;
}

/// <summary>
/// 字体族：界面字体用 tokens 里的 windows 值（Segoe UI Variable Text → Segoe UI → Microsoft YaHei UI，逐字回退）；
/// 等宽字体按已安装字体选 JetBrains Mono → Cascadia Mono → Consolas，再回退到中文字体。
/// </summary>
public static class UIFonts
{
    private static FontFamily? _ui;
    private static FontFamily? _mono;
    private static HashSet<string>? _installed;

    public static FontFamily UI => _ui ??= new FontFamily(Pick(DesignTokens.Shared.FontFamilies("ui", "windows"), new[] { "Segoe UI", "Microsoft YaHei UI" }, keepAll: true));

    public static FontFamily Mono => _mono ??= new FontFamily(Pick(DesignTokens.Shared.FontFamilies("mono", "windows"), new[] { "Consolas" }, keepAll: false) + ", Microsoft YaHei UI");

    /// <summary>界面字体实际用的第一个已安装字体名（调试桥、陈列页显示用）。</summary>
    public static string UIName => FirstInstalled(DesignTokens.Shared.FontFamilies("ui", "windows")) ?? "Segoe UI";

    public static string MonoName => FirstInstalled(DesignTokens.Shared.FontFamilies("mono", "windows")) ?? "Consolas";

    private static string? FirstInstalled(IEnumerable<string> names) => names.FirstOrDefault(IsInstalled);

    public static bool IsInstalled(string name)
    {
        if (_installed == null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
                {
                    set.Add(f.Source);
                    foreach (var n in f.FamilyNames.Values) set.Add(n);
                }
            }
            catch
            {
                // 枚举字体失败时按都装了处理
            }
            _installed = set;
        }
        return _installed.Count == 0 || _installed.Contains(name);
    }

    /// <summary>keepAll：按 tokens 顺序保留已安装的全部字体（WPF 逐字回退）；否则只取第一个已安装的。</summary>
    private static string Pick(string[] fromTokens, string[] fallback, bool keepAll)
    {
        var list = fromTokens.Length > 0 ? fromTokens : fallback;
        var installed = list.Where(IsInstalled).ToList();
        if (installed.Count == 0) installed = fallback.ToList();
        return keepAll ? string.Join(", ", installed) : installed[0];
    }
}

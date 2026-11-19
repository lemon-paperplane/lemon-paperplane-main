// 颜色名：与 shared/design-tokens.json 的 color.light / color.dark 键名一致（由 JSON 生成，与 Mac ColorTokens.swift 对应）。
// 代码里写 el.Bg(ColorToken.BgContent) 或 el.Bg("bgContent") 都可以；Debug 编译时写错的键名会直接抛异常。

namespace LemonPaperplane;

public static class ColorToken
{
    public const string BgChrome = "bgChrome";
    public const string BgContent = "bgContent";
    public const string BgSurface = "bgSurface";
    public const string BgSubtle = "bgSubtle";
    public const string BgElevated = "bgElevated";
    public const string BgInput = "bgInput";
    public const string BgInputDisabled = "bgInputDisabled";
    public const string BgHover = "bgHover";
    public const string BgPressed = "bgPressed";
    public const string BgSelected = "bgSelected";
    public const string BgSelectedInactive = "bgSelectedInactive";
    public const string BgCode = "bgCode";
    public const string Border = "border";
    public const string BorderSubtle = "borderSubtle";
    public const string BorderStrong = "borderStrong";
    public const string BorderFocus = "borderFocus";
    public const string FocusRing = "focusRing";
    public const string TextPrimary = "textPrimary";
    public const string TextSecondary = "textSecondary";
    public const string TextTertiary = "textTertiary";
    public const string TextDisabled = "textDisabled";
    public const string TextOnAccent = "textOnAccent";
    public const string TextLink = "textLink";
    public const string IconPrimary = "iconPrimary";
    public const string IconSecondary = "iconSecondary";
    public const string Accent = "accent";
    public const string AccentHover = "accentHover";
    public const string AccentPressed = "accentPressed";
    public const string AccentSubtle = "accentSubtle";
    public const string AccentSubtleBorder = "accentSubtleBorder";
    public const string Success = "success";
    public const string SuccessSubtle = "successSubtle";
    public const string SuccessBorder = "successBorder";
    public const string Warning = "warning";
    public const string WarningSubtle = "warningSubtle";
    public const string WarningBorder = "warningBorder";
    public const string Danger = "danger";
    public const string DangerHover = "dangerHover";
    public const string DangerSubtle = "dangerSubtle";
    public const string DangerBorder = "dangerBorder";
    public const string Info = "info";
    public const string InfoSubtle = "infoSubtle";
    public const string InfoBorder = "infoBorder";
    public const string NeutralSubtle = "neutralSubtle";
    public const string NeutralBorder = "neutralBorder";
    public const string StatusIdle = "statusIdle";
    public const string StatusStarting = "statusStarting";
    public const string StatusRunning = "statusRunning";
    public const string StatusFailed = "statusFailed";
    public const string StatusStopping = "statusStopping";
    public const string ButtonSecondaryBg = "buttonSecondaryBg";
    public const string ButtonSecondaryHover = "buttonSecondaryHover";
    public const string ButtonSecondaryBorder = "buttonSecondaryBorder";
    public const string SwitchOff = "switchOff";
    public const string Scrim = "scrim";
    public const string Shadow = "shadow";
    public const string ConsoleBg = "consoleBg";
    public const string ConsoleText = "consoleText";
    public const string ConsoleSystem = "consoleSystem";
    public const string ConsoleStderr = "consoleStderr";
    public const string ConsoleSelection = "consoleSelection";
    public const string SearchMatch = "searchMatch";
    public const string SearchMatchCurrent = "searchMatchCurrent";
    public const string Scrollbar = "scrollbar";
    public const string GraphLane0 = "graphLane0";
    public const string GraphLane1 = "graphLane1";
    public const string GraphLane2 = "graphLane2";
    public const string GraphLane3 = "graphLane3";
    public const string GraphLane4 = "graphLane4";
    public const string GraphLane5 = "graphLane5";
    public const string GraphLane6 = "graphLane6";
    public const string GraphLane7 = "graphLane7";
    public const string DiffAddedBg = "diffAddedBg";
    public const string DiffAddedText = "diffAddedText";
    public const string DiffRemovedBg = "diffRemovedBg";
    public const string DiffRemovedText = "diffRemovedText";
    public const string DiffHunkBg = "diffHunkBg";
    public const string BrandLemon = "brandLemon";
    /// <summary>透明但能接收鼠标（可点击区域的底色）。不是设计变量，各主题都是 Transparent。</summary>
    public const string Clear = "clear";

    /// <summary>全部设计变量颜色名（不含 Clear）。</summary>
    public static readonly string[] All = PaperplaneKit.DesignTokens.RequiredColors;
}

/// <summary>阴影资源键（DropShadowEffect，来自 design-tokens.json 的 shadow；暗色主题不透明度加大）。</summary>
public static class ShadowToken
{
    public const string Popup = "shadow.popup";
    public const string Dialog = "shadow.dialog";
    public const string Card = "shadow.card";
    public const string Toast = "shadow.toast";
    /// <summary>分段控件选中段、开关圆钮的细阴影（Mac 写死的 radius 1 y 0.5）。</summary>
    public const string Hairline = "shadow.hairline";
}

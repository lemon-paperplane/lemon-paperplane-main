using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LemonPaperplane;

/// <summary>按钮种类（design-system.md「按钮」）。</summary>
public enum PPButtonKind
{
    /// <summary>强调色实心</summary>
    Primary,
    /// <summary>描边</summary>
    Secondary,
    /// <summary>无底，悬停出底色（工具栏）</summary>
    Ghost,
    /// <summary>危险操作实心红</summary>
    Danger,
    /// <summary>文本链接</summary>
    Link,
}

public enum PPButtonSize
{
    /// <summary>高 28，水平内边距 12</summary>
    Regular,
    /// <summary>高 24，水平内边距 8</summary>
    Small,
}

public static class PPButtonSizeExt
{
    public static double Height(this PPButtonSize s) => s == PPButtonSize.Regular ? M.ControlHeight : M.ControlHeightSmall;
    public static double HPadding(this PPButtonSize s) => s == PPButtonSize.Regular ? M.Lg : M.Md;
}

/// <summary>
/// 陈列页 / 截图用：强制显示某个交互状态（hover / pressed / focus / open），不用真的移动鼠标。
/// el.SetValue(PPDebug.ForceStateProperty, "hover")。
/// </summary>
public static class PPDebug
{
    public static readonly DependencyProperty ForceStateProperty = DependencyProperty.RegisterAttached(
        "ForceState", typeof(string), typeof(PPDebug), new PropertyMetadata(null, (d, _) => (d as IVisualStateful)?.UpdateVisual()));

    public static string? GetForceState(DependencyObject d) => d.GetValue(ForceStateProperty) as string;

    public static T Force<T>(this T el, string? state) where T : DependencyObject
    {
        el.SetValue(ForceStateProperty, state);
        return el;
    }

    internal static bool Is(DependencyObject d, string state) => GetForceState(d) == state;
}

/// <summary>按状态（悬停、按下、聚焦、禁用）更新外观的元素。</summary>
public interface IVisualStateful
{
    void UpdateVisual();
}

/// <summary>
/// 自绘按钮的基类：保留 WPF Button 的语义（空格 / 回车触发、点击、自动化 Invoke），外观完全自绘（空模板 + 内容）。
/// 状态变化时调用 UpdateVisual；颜色一律资源引用。键盘聚焦时画 2px focusRing（鼠标点击不画）。
/// </summary>
public abstract class PPButtonBase : Button, IVisualStateful
{
    private static readonly ControlTemplate BareTemplate = MakeBareTemplate();
    private Action? _action;

    private static ControlTemplate MakeBareTemplate()
    {
        var t = new ControlTemplate(typeof(ButtonBase)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        t.Seal();
        return t;
    }

    protected PPButtonBase()
    {
        Template = BareTemplate;
        FocusVisualStyle = null;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        SnapsToDevicePixels = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        // 系统主题的 Button 样式把 Foreground 设成 ControlText（黑色），没单独设颜色的文字（复选框、单选框的标签）在暗色下看不见
        this.Fg(ColorToken.TextPrimary);
    }

    /// <summary>点击动作（也可以用 Click 事件）。</summary>
    public Action? Action
    {
        get => _action;
        set => _action = value;
    }

    protected override void OnClick()
    {
        base.OnClick();
        _action?.Invoke();
    }

    protected bool Hover => (IsMouseOver && IsEnabled) || PPDebug.Is(this, "hover");
    protected bool Pressed => IsPressed || PPDebug.Is(this, "pressed");
    protected bool FocusVisible => (IsKeyboardFocused && InputManager.Current.MostRecentInputDevice is KeyboardDevice) || PPDebug.Is(this, "focus");

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsEnabledProperty || e.Property == IsKeyboardFocusedProperty)
            UpdateVisual();
    }

    public abstract void UpdateVisual();

    /// <summary>键盘聚焦光圈：2px focusRing，比控件大 2px，圆角 +2。</summary>
    protected static Border MakeFocusRing(double radius) => new Border
    {
        BorderThickness = new Thickness(M.FocusRingWidth),
        CornerRadius = new CornerRadius(radius + 2),
        Margin = new Thickness(-2),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        SnapsToDevicePixels = true,
    }.Outline(ColorToken.FocusRing, M.FocusRingWidth);
}

/// <summary>文本按钮（可带图标）。对应 Mac 的 PPButton。</summary>
public class PPButton : PPButtonBase
{
    private readonly Border _chrome;
    private readonly Border _ring;
    private readonly Stack _row;
    private readonly TextBlock _text;
    private UIElement? _iconEl;
    private IconKey? _icon;
    private readonly string? _trailingIcon;
    private bool _busy;

    public PPButtonKind Kind { get; }
    public PPButtonSize Size { get; }

    /// <summary>new PPButton(L.T("action.run"), () =&gt; …, icon: "play", kind: PPButtonKind.Primary, size: PPButtonSize.Small)</summary>
    public PPButton(string title, Action? onClick = null, string? icon = null, PPButtonKind kind = PPButtonKind.Secondary,
                    PPButtonSize size = PPButtonSize.Regular, bool fullWidth = false, bool busy = false, IconKey? iconRef = null,
                    string? trailingIcon = null)
    {
        Kind = kind;
        _trailingIcon = trailingIcon;
        Size = size;
        Action = onClick;
        _icon = iconRef ?? (icon != null ? IconKey.Builtin(icon) : null);
        _text = UI.Text(title, kind == PPButtonKind.Link ? Typo.Body : Typo.Label);
        _text.Shrink(false);
        _row = UI.HStack(M.Sm);
        _row.HorizontalAlignment = HorizontalAlignment.Center;
        _row.VerticalAlignment = VerticalAlignment.Center;
        _chrome = new Border
        {
            CornerRadius = new CornerRadius(M.RadiusMD),
            Child = _row,
            SnapsToDevicePixels = true,
        };
        if (kind != PPButtonKind.Link)
        {
            _chrome.Height = size.Height();
            _chrome.Padding = new Thickness(size.HPadding(), 0, size.HPadding(), 0);
        }
        else
        {
            Cursor = Cursors.Hand;
        }
        _ring = MakeFocusRing(M.RadiusMD);
        var root = new Grid();
        root.Children.Add(_ring);
        root.Children.Add(_chrome);
        Content = root;
        if (fullWidth) HorizontalAlignment = HorizontalAlignment.Stretch;
        this.A11y(title);
        _busy = busy;
        RebuildRow();
        IsEnabled = !busy;
        UpdateVisual();
    }

    public string Title
    {
        get => _text.Text;
        set { _text.Text = value; this.A11y(value); }
    }

    /// <summary>忙碌：显示转圈并禁用。</summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            IsEnabled = !value;
            RebuildRow();
        }
    }

    private void RebuildRow()
    {
        _row.Children.Clear();
        _iconEl = null;
        double iconSize = Size == PPButtonSize.Small ? M.IconSmall : M.Icon;
        if (_busy) _iconEl = new PPSpinner(M.IconSmall);
        else if (_icon != null) _iconEl = new PPIcon(_icon, iconSize);
        if (_iconEl != null) _row.Children.Add(_iconEl);
        _row.Children.Add(_text);
        if (_trailingIcon != null) _row.Children.Add(new PPIcon(_trailingIcon, M.IconSmall) { Opacity = 0.8 });
    }

    public override void UpdateVisual()
    {
        if (_chrome == null) return;
        bool p = Pressed, h = Hover;
        string? bg = Kind switch
        {
            PPButtonKind.Primary => p ? ColorToken.AccentPressed : h ? ColorToken.AccentHover : ColorToken.Accent,
            PPButtonKind.Danger => p || h ? ColorToken.DangerHover : ColorToken.Danger,
            PPButtonKind.Secondary => p ? ColorToken.BgPressed : h ? ColorToken.ButtonSecondaryHover : ColorToken.ButtonSecondaryBg,
            PPButtonKind.Ghost => p ? ColorToken.BgPressed : h ? ColorToken.BgHover : ColorToken.Clear,
            _ => ColorToken.Clear,
        };
        _chrome.Bg(bg);
        _chrome.Outline(Kind == PPButtonKind.Secondary ? ColorToken.ButtonSecondaryBorder : null);
        _chrome.Fg(Kind switch
        {
            PPButtonKind.Primary or PPButtonKind.Danger => ColorToken.TextOnAccent,
            PPButtonKind.Link => h ? ColorToken.AccentHover : ColorToken.TextLink,
            _ => ColorToken.TextPrimary,
        });
        _chrome.Opacity = IsEnabled ? 1 : 0.5;
        _ring.Visibility = FocusVisible && Kind != PPButtonKind.Link ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>只有图标的方形按钮（工具栏、卡片运行区），必须有提示文字。对应 Mac 的 PPIconButton。</summary>
public sealed class PPIconButton : PPButtonBase
{
    private readonly Border _chrome;
    private readonly Border _ring;
    private readonly double _iconSize;
    private IconKey? _icon;
    private string? _tint;
    private bool _active;
    private bool _busy;

    /// <summary>new PPIconButton("square", L.T("action.stop"), () =&gt; …, tint: ColorToken.Danger)</summary>
    public PPIconButton(string icon, string? help = null, Action? onClick = null, double size = 0, double iconSize = 0,
                        string? tint = null, bool active = false, bool busy = false)
        : this(IconKey.Builtin(icon), help, onClick, size, iconSize, tint, active, busy) { }

    public PPIconButton(IconKey? icon, string? help = null, Action? onClick = null, double size = 0, double iconSize = 0,
                        string? tint = null, bool active = false, bool busy = false)
    {
        _icon = icon;
        _tint = tint;
        _active = active;
        _busy = busy;
        Action = onClick;
        double s = size > 0 ? size : M.IconButton;
        _iconSize = iconSize > 0 ? iconSize : M.Icon;
        _chrome = new Border
        {
            Width = s,
            Height = s,
            CornerRadius = new CornerRadius(M.RadiusMD),
            SnapsToDevicePixels = true,
        };
        _ring = MakeFocusRing(M.RadiusMD);
        var root = new Grid();
        root.Children.Add(_ring);
        root.Children.Add(_chrome);
        Content = root;
        Help = help;
        SetGlyph();
        UpdateVisual();
    }

    public string? Help
    {
        get => ToolTip as string;
        set
        {
            ToolTip = string.IsNullOrEmpty(value) ? null : value;
            this.A11y(value);
        }
    }

    public IconKey? Icon
    {
        get => _icon;
        set { _icon = value; SetGlyph(); }
    }

    /// <summary>选中态（侧栏开关、跟随滚动…）：accentSubtle 底 + 强调色图标。</summary>
    public bool Active
    {
        get => _active;
        set { _active = value; UpdateVisual(); }
    }

    public bool Busy
    {
        get => _busy;
        set { _busy = value; SetGlyph(); }
    }

    public string? Tint
    {
        get => _tint;
        set { _tint = value; UpdateVisual(); }
    }

    private void SetGlyph()
    {
        UIElement? g = _busy ? new PPSpinner(M.IconSmall) : (_icon != null ? new PPIcon(_icon, _iconSize) : null);
        if (g is FrameworkElement fe) fe.Center();
        _chrome.Child = g;
    }

    public override void UpdateVisual()
    {
        if (_chrome == null) return;
        _chrome.Bg(Pressed ? ColorToken.BgPressed : _active ? ColorToken.AccentSubtle : Hover ? ColorToken.BgHover : ColorToken.Clear);
        _chrome.Fg(IsEnabled ? (_tint ?? (_active ? ColorToken.Accent : ColorToken.IconPrimary)) : ColorToken.TextDisabled);
        _ring.Visibility = FocusVisible ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>可点击的文本链接（URL、路径）：textLink，悬停下划线，长文本中间截断。对应 Mac 的 PPLink。</summary>
public sealed class PPLink : PPButtonBase
{
    private readonly MiddleTrimText _text;

    /// <summary>new PPLink("localhost:8080", () =&gt; …, mono: true)</summary>
    public PPLink(string title, Action? onClick = null, bool mono = false, string? icon = null)
    {
        Action = onClick;
        Cursor = Cursors.Hand;
        _text = UI.TextMiddle(title, mono ? Typo.MonoSmall : Typo.Small);
        var row = UI.HStack(M.Xs, icon != null ? new PPIcon(icon, M.IconSmall) : null, _text);
        row.Bg(ColorToken.Clear).Fg(ColorToken.TextLink);
        Content = row;
        this.A11y(title);
        UpdateVisual();
    }

    public override void UpdateVisual()
    {
        if (_text != null) _text.Inner.TextDecorations = Hover ? TextDecorations.Underline : null;
    }
}

/// <summary>一组紧挨的工具栏控件（间距 2）。对应 Mac 的 PPToolbarGroup。</summary>
public static class PPToolbarGroup
{
    public static Stack Of(params object?[] children) => UI.HStack(M.Xxs, children);
}

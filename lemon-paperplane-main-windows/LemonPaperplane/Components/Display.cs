using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LemonPaperplane;

/// <summary>界面层的系统动作入口（打开链接、复制）。AppModel 可以替换成带日志 / 提示的实现。</summary>
public static class UIHooks
{
    public static Action<string> OpenUrl { get; set; } = url =>
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { ToastStore.Shared.Error(e); }
    };

    public static Action<string> CopyText { get; set; } = text =>
    {
        try { Clipboard.SetText(text); }
        catch (Exception e) { ToastStore.Shared.Error(e); }
    };
}

/// <summary>徽标（语气色浅底 + 前景，描边 60%）。对应 Mac 的 PPBadge。</summary>
public sealed class PPBadge : Grid
{
    /// <summary>new PPBadge("LTS", tone: "success")</summary>
    public PPBadge(string text, string? tone = null, string? icon = null, bool mono = false, IconKey? iconRef = null)
    {
        var c = Theme.Tone(tone);
        Height = 18;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
        Children.Add(new Border { CornerRadius = new CornerRadius(M.RadiusSM) }.Bg(c.Bg));
        Children.Add(new Border { CornerRadius = new CornerRadius(M.RadiusSM), Opacity = 0.6, SnapsToDevicePixels = true }.Outline(c.Border));
        var key = iconRef ?? (icon != null ? IconKey.Builtin(icon) : null);
        var row = UI.HStack(M.Xs, key != null ? new PPIcon(key, 12) : null, UI.Text(text, mono ? Typo.MonoSmall : Typo.Caption).Shrink(false));
        row.Margin = new Thickness(M.Sm, 0, M.Sm, 0);
        Children.Add(row);
        this.Fg(tone == null || tone == "neutral" ? ColorToken.TextSecondary : c.Fg);
    }
}

/// <summary>数量胶囊（标签页、指示器）。对应 Mac 的 PPCountBadge。</summary>
public sealed class PPCountBadge : Border
{
    private readonly TextBlock _text;

    public PPCountBadge(int count, string? tone = null)
    {
        _text = UI.Text(count.ToString(), Typo.Caption).TabularDigits().Shrink(false);
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        _text.VerticalAlignment = VerticalAlignment.Center;
        Child = _text;
        Padding = new Thickness(5, 0, 5, 0);
        MinWidth = 16;
        MinHeight = 16;
        CornerRadius = new CornerRadius(8);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        SnapsToDevicePixels = true;
        this.Bg(tone == null ? ColorToken.NeutralSubtle : Theme.Tone(tone).Bg);
        this.Fg(tone == null ? ColorToken.TextSecondary : Theme.Tone(tone).Fg);
    }

    public int Count
    {
        set => _text.Text = value.ToString();
    }
}

/// <summary>技术徽标（facet）：图标 12 + 名称（caption），neutralSubtle 底。对应 Mac 的 FacetBadgeView。</summary>
public sealed class FacetBadgeView : Border
{
    /// <summary>name：显示名；icon：图标引用（builtin:/plugin:/插件相对路径）；plugin：图标所属插件。</summary>
    public FacetBadgeView(string name, string? icon, string? plugin = null)
    {
        Child = UI.HStack(M.Xs, PPIcon.Raw(icon, plugin, "box", 12), UI.Text(name, Typo.Caption).Shrink(false));
        Padding = new Thickness(M.Sm, 0, M.Sm, 0);
        Height = 20;
        CornerRadius = new CornerRadius(M.RadiusSM);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        this.Bg(ColorToken.NeutralSubtle).Fg(ColorToken.TextSecondary);
    }
}

/// <summary>运行状态：idle | preparing | starting | running | stopping | failed | exited | partial。</summary>
public static class RunStatusStyle
{
    public static string Color(string? state) => state switch
    {
        "running" or "partial" => ColorToken.StatusRunning,
        "starting" or "preparing" => ColorToken.StatusStarting,
        "failed" => ColorToken.StatusFailed,
        "stopping" => ColorToken.StatusStopping,
        _ => ColorToken.StatusIdle,
    };

    public static string Title(string? state) => state switch
    {
        "running" => L.T("status.running"),
        "partial" => L.T("status.partial"),
        "starting" => L.T("status.starting"),
        "preparing" => L.T("status.preparing"),
        "failed" => L.T("status.failed"),
        "stopping" => L.T("status.stopping"),
        "exited" => L.T("status.exited"),
        _ => L.T("status.idle"),
    };

    public static bool IsBusy(string? state) => state is "starting" or "preparing" or "stopping";
}

/// <summary>状态圆点（8px；启动中 / 准备中 / 停止中呼吸闪烁；提示为状态名）。对应 Mac 的 StatusDot。</summary>
public sealed class StatusDot : Border
{
    private string _state = "";

    public StatusDot(string state, double size = 0)
    {
        double s = size > 0 ? size : M.StatusDot;
        Width = s;
        Height = s;
        CornerRadius = new CornerRadius(s / 2);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        Loaded += (_, _) => Pulse();
        Unloaded += (_, _) => BeginAnimation(OpacityProperty, null);
        State = state;
    }

    public string State
    {
        get => _state;
        set
        {
            _state = value ?? "";
            this.Bg(RunStatusStyle.Color(_state));
            ToolTip = RunStatusStyle.Title(_state);
            if (IsLoaded) Pulse();
        }
    }

    private void Pulse()
    {
        if (!RunStatusStyle.IsBusy(_state))
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            return;
        }
        var a = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        BeginAnimation(OpacityProperty, a);
    }
}

/// <summary>转圈（loader-circle 旋转，0.9 秒一圈）。对应 Mac 的 PPSpinner。</summary>
public sealed class PPSpinner : Decorator
{
    private readonly RotateTransform _rotate = new();

    public PPSpinner(double size = 0, string? color = null)
    {
        var icon = new PPIcon("loader-circle", size > 0 ? size : M.Icon, color)
        {
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rotate,
        };
        Child = icon;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        IsHitTestVisible = false;
        Loaded += (_, _) => _rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        Unloaded += (_, _) => _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }
}

/// <summary>进度条：4px 胶囊；value 0–100，value &lt; 0 为不确定（来回移动）。对应 Mac 的 PPProgressBar。</summary>
public sealed class PPProgressBar : Grid
{
    private readonly Border _fill;
    private readonly TranslateTransform _move = new();
    private double _value;

    public PPProgressBar(double value, double height = 4, string? tone = null)
    {
        Height = height;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Children.Add(new Border { CornerRadius = new CornerRadius(height / 2) }.Bg(ColorToken.NeutralSubtle));
        _fill = new Border { CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _move };
        _fill.Bg(tone == null ? ColorToken.Accent : Theme.Tone(tone).Fg);
        Children.Add(_fill);
        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => Layout();
        Unloaded += (_, _) => _move.BeginAnimation(TranslateTransform.XProperty, null);
        _value = value;
    }

    public double Value
    {
        get => _value;
        set
        {
            bool modeChanged = (_value < 0) != (value < 0);
            _value = value;
            if (modeChanged) _move.BeginAnimation(TranslateTransform.XProperty, null);
            Layout();
        }
    }

    private void Layout()
    {
        double w = ActualWidth;
        if (w <= 0) return;
        if (_value < 0)
        {
            _fill.Width = w * 0.3;
            var a = new DoubleAnimation(0, w * 0.7, TimeSpan.FromSeconds(1))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _move.BeginAnimation(TranslateTransform.XProperty, a);
        }
        else
        {
            _move.BeginAnimation(TranslateTransform.XProperty, null);
            _move.X = 0;
            _fill.Width = w * Math.Min(Math.Max(_value, 0), 100) / 100;
        }
    }
}

/// <summary>分隔线（1px border；subtle 用 borderSubtle）。对应 Mac 的 PPDivider。</summary>
public sealed class PPDivider : Border
{
    public PPDivider(bool vertical = false, bool subtle = false)
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        if (vertical) { Width = 1; VerticalAlignment = VerticalAlignment.Stretch; }
        else { Height = 1; HorizontalAlignment = HorizontalAlignment.Stretch; }
        this.Bg(subtle ? ColorToken.BorderSubtle : ColorToken.Border);
    }
}

/// <summary>
/// 区块：标题（title）+ 可选 i + 右侧附件，可折叠。对应 Mac 的 PPSection。
/// 折叠状态：给 collapsed（Bind）或 stateKey（UIState 里跨重建保留），都不给时只在本实例内有效。
/// </summary>
public sealed class PPSection : Stack
{
    private readonly UIElement _content;
    private readonly PPIcon? _chevron;
    private readonly Bind<bool> _collapsed;

    public PPSection(string title, UIElement content, string? info = null, UIElement? accessory = null, bool collapsible = false,
                     bool collapsed = false, Bind<bool>? collapsedBind = null, string? stateKey = null)
        : base(Orientation.Vertical, M.Md)
    {
        _content = content;
        _collapsed = collapsedBind ?? (stateKey != null ? UIState.Bind(stateKey, collapsed) : new State<bool>(collapsed));
        Align = CrossAlign.Stretch;
        var header = UI.HStack(M.Sm);
        if (collapsible)
        {
            _chevron = new PPIcon("chevron-down", M.IconSmall, ColorToken.IconSecondary);
            header.Children.Add(_chevron);
        }
        header.Children.Add(UI.Text(title, Typo.Title));
        if (info != null) header.Children.Add(new PPInfoButton(info));
        header.Children.Add(UI.Spacer(M.Md));
        if (accessory != null) header.Children.Add(accessory);
        header.Bg(ColorToken.Clear);
        if (collapsible)
        {
            header.OnClick(() => { _collapsed.Value = !_collapsed.Value; Apply(); });
            header.A11y(title);
        }
        Children.Add(header);
        Children.Add(content);
        Apply();
    }

    private void Apply()
    {
        bool c = _collapsed.Value;
        _content.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        if (_chevron != null) _chevron.Key = IconKey.Builtin(c ? "chevron-right" : "chevron-down");
    }
}

/// <summary>卡片：bgSurface、1px border、圆角 8、内边距 12；hoverable 时悬停描边 borderStrong + card 阴影；selected 强调色描边。对应 Mac 的 PPCard。</summary>
public sealed class PPCard : Grid, IVisualStateful
{
    private readonly Border _shadow;
    private readonly Border _box;
    private readonly bool _hoverable;
    private bool _selected;

    public PPCard(UIElement content, double padding = -1, bool hoverable = false, bool selected = false)
    {
        _hoverable = hoverable;
        _selected = selected;
        _shadow = new Border { CornerRadius = new CornerRadius(M.RadiusLG), Visibility = Visibility.Collapsed }.Bg(ColorToken.BgSurface).Shadow(ShadowToken.Card);
        _box = new Border
        {
            CornerRadius = new CornerRadius(M.RadiusLG),
            Padding = new Thickness(padding >= 0 ? padding : M.Lg),
            Child = content,
            SnapsToDevicePixels = true,
        }.Bg(ColorToken.BgSurface);
        Children.Add(_shadow);
        Children.Add(_box);
        if (hoverable)
        {
            MouseEnter += (_, _) => UpdateVisual();
            MouseLeave += (_, _) => UpdateVisual();
        }
        UpdateVisual();
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; UpdateVisual(); }
    }

    public void UpdateVisual()
    {
        bool hover = _hoverable && (IsMouseOver || PPDebug.Is(this, "hover"));
        _shadow.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
        _box.Outline(_selected ? ColorToken.Accent : hover ? ColorToken.BorderStrong : ColorToken.Border);
    }
}

/// <summary>空状态：一个图标（32）+ 一句话 + 一个主按钮，居中。对应 Mac 的 PPEmptyState。</summary>
public sealed class PPEmptyState : Border
{
    public PPEmptyState(string icon, string title, string? actionTitle = null, Action? action = null, string? actionIcon = null)
    {
        var col = UI.VStack(M.Lg,
            new PPIcon(icon, M.IconEmpty, ColorToken.IconSecondary).HCenter(),
            UI.Paragraph(title, Typo.Body, ColorToken.TextSecondary).Align(TextAlignment.Center).HCenter(),
            actionTitle != null && action != null ? new PPButton(actionTitle, action, icon: actionIcon, kind: PPButtonKind.Primary).HCenter() : null);
        col.Align = CrossAlign.Center;
        col.HorizontalAlignment = HorizontalAlignment.Center;
        col.VerticalAlignment = VerticalAlignment.Center;
        Child = col;
        Padding = new Thickness(M.Huge);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }
}

/// <summary>i 图标：点开 280 宽弹出框显示说明（界面上不铺解释性小字）。对应 Mac 的 PPInfoButton。</summary>
public sealed class PPInfoButton : PPButtonBase
{
    private readonly string _text;
    private readonly PPIcon _icon;
    private PPPopup? _popup;

    public PPInfoButton(string text)
    {
        _text = text;
        _icon = new PPIcon("info", M.IconSmall);
        Content = new Border { Child = _icon }.Bg(ColorToken.Clear);
        Focusable = true;
        Action = Toggle;
        this.A11y(L.T("common.more"));
        UpdateVisual();
    }

    private void Toggle()
    {
        if (_popup != null) { _popup.Close(); return; }
        if (PopupController.WasJustDismissed(this)) return;
        var body = UI.Paragraph(_text, Typo.Small, ColorToken.TextPrimary).W(280);
        _popup = Popover.Show(this, new PopoverContent(body), onClose: () => { _popup = null; UpdateVisual(); });
        UpdateVisual();
    }

    public override void UpdateVisual()
    {
        if (_icon == null) return;
        _icon.Fg(_popup != null || PPDebug.Is(this, "open") ? ColorToken.Accent : ColorToken.IconSecondary);
    }
}

/// <summary>键值列表的一行。</summary>
public sealed class PPKeyValueRow
{
    public string Key { get; }
    public string Value { get; }
    public bool Mono { get; }
    public string? Tone { get; }
    public string? Href { get; }

    public PPKeyValueRow(string key, string value, bool mono = false, string? tone = null, string? href = null)
    {
        Key = key;
        Value = value;
        Mono = mono;
        Tone = tone;
        Href = href;
    }
}

/// <summary>键值列表：键列 secondary 固定宽，值可等宽 / 语气色 / 链接 / 可选中。对应 Mac 的 PPKeyValueList。</summary>
public sealed class PPKeyValueList : Stack
{
    public PPKeyValueList(IEnumerable<PPKeyValueRow> rows, double keyWidth = 120) : base(Orientation.Vertical, M.Xs)
    {
        Align = CrossAlign.Stretch;
        foreach (var r in rows)
        {
            UIElement value;
            if (r.Href != null)
            {
                var href = r.Href;
                value = new PPLink(r.Value, () => UIHooks.OpenUrl(href), mono: r.Mono);
            }
            else
            {
                var t = UI.Selectable(r.Value, r.Mono ? Typo.Mono : Typo.Body, r.Tone == null ? ColorToken.TextPrimary : Theme.Tone(r.Tone).Fg);
                t.MaxHeight = (r.Mono ? Typo.Mono : Typo.Body).LineHeight * 3 + 2;
                value = t;
            }
            var key = UI.Text(r.Key, Typo.Body, ColorToken.TextSecondary).W(keyWidth).Shrink(false);
            key.VerticalAlignment = VerticalAlignment.Top;
            var row = UI.HStack(M.Md, key, value.Flex());
            row.Align = CrossAlign.Start;
            Children.Add(row);
        }
    }
}

/// <summary>列表行：高 28，圆角 6，悬停 bgHover，选中 bgSelected；点击 action。对应 Mac 的 PPRow。</summary>
public sealed class PPRow : Border, IVisualStateful
{
    private bool _selected;

    public PPRow(UIElement content, bool selected = false, Action? action = null, double height = -1)
    {
        _selected = selected;
        Child = content;
        Padding = new Thickness(M.Md, 0, M.Md, 0);
        if (height != 0) Height = height > 0 ? height : M.RowHeight;
        CornerRadius = new CornerRadius(M.RadiusMD);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        SnapsToDevicePixels = true;
        if (content is FrameworkElement fe && fe.ReadLocalValue(VerticalAlignmentProperty) == DependencyProperty.UnsetValue) fe.VerticalAlignment = VerticalAlignment.Center;
        MouseEnter += (_, _) => UpdateVisual();
        MouseLeave += (_, _) => UpdateVisual();
        if (action != null) this.OnClick(action);
        UpdateVisual();
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; UpdateVisual(); }
    }

    public void UpdateVisual() =>
        this.Bg(_selected ? ColorToken.BgSelected : (IsMouseOver || PPDebug.Is(this, "hover")) ? ColorToken.BgHover : ColorToken.Clear);
}

/// <summary>页面标题栏（页内，高 52，heading 字号 + 右侧操作）。对应 Mac 的 PPPageHeader。</summary>
public sealed class PPPageHeader : Border
{
    public PPPageHeader(string title, string? subtitle = null, params object?[] accessory)
    {
        var row = UI.HStack(M.Md, UI.Text(title, Typo.Heading).Shrink(false), subtitle != null ? UI.Text(subtitle, Typo.Body, ColorToken.TextSecondary) : null, UI.Spacer());
        foreach (var a in UI.Flatten(accessory)) row.Children.Add(a);
        Child = row;
        Padding = new Thickness(M.Xxl, 0, M.Xxl, 0);
        Height = 52;
    }
}

/// <summary>提示条：语气浅底 + 描边，图标 + 粗体标题 + 说明 + 右侧按钮。对应 Mac 的 PPAlert。</summary>
public sealed class PPAlert : Border
{
    public PPAlert(string tone, string title, string? message = null, params object?[] actions)
    {
        var c = Theme.Tone(tone);
        string icon = tone switch { "danger" or "error" => "circle-alert", "warning" => "triangle-alert", "success" => "circle-check", _ => "info" };
        var text = UI.VStack(M.Xxs, UI.Paragraph(title, Typo.BodyStrong),
            !string.IsNullOrEmpty(message) ? UI.Paragraph(message, Typo.Small, ColorToken.TextSecondary) : null);
        text.Shrink();
        var row = UI.HStack(M.Md, new PPIcon(icon, M.Icon, c.Fg).Top().Margins(0, 1, 0, 0), text, UI.Spacer(M.Md));
        foreach (var a in UI.Flatten(actions)) row.Children.Add(a);
        row.Align = CrossAlign.Start;
        Child = row;
        Padding = new Thickness(M.Md);
        CornerRadius = new CornerRadius(M.RadiusMD);
        SnapsToDevicePixels = true;
        this.Bg(c.Bg).Outline(c.Border);
    }
}

/// <summary>可调宽度的左右分栏：1px 分隔线，7px 拖动热区。对应 Mac 的 PPSplitView。</summary>
public sealed class PPSplitView : Grid
{
    private readonly Bind<double> _width;
    private readonly ColumnDefinition _left;
    private readonly double _min;
    private readonly double _max;

    public PPSplitView(UIElement left, UIElement right, Bind<double> leftWidth, double minLeft = 180, double maxLeft = 480)
    {
        _width = leftWidth;
        _min = minLeft;
        _max = maxLeft;
        _left = new ColumnDefinition { Width = new GridLength(Clamp(leftWidth.Value)) };
        ColumnDefinitions.Add(_left);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Children.Add(left.At(0, 0));
        Children.Add(new PPDivider(vertical: true).At(0, 1));
        Children.Add(right.At(0, 2));
        var handle = new Border { Width = 7, Margin = new Thickness(-3, 0, -3, 0), Cursor = Cursors.SizeWE, Background = Brushes.Transparent };
        handle.At(0, 1).ZIndex(10);
        Children.Add(handle);
        double? startX = null, startW = null;
        handle.MouseLeftButtonDown += (_, e) =>
        {
            startX = e.GetPosition(this).X;
            startW = _left.Width.Value;
            handle.CaptureMouse();
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (startX == null || !handle.IsMouseCaptured) return;
            var w = Clamp(startW!.Value + e.GetPosition(this).X - startX.Value);
            _left.Width = new GridLength(w);
            _width.Value = w;
        };
        handle.MouseLeftButtonUp += (_, _) =>
        {
            startX = null;
            handle.ReleaseMouseCapture();
        };
    }

    private double Clamp(double w) => Math.Min(Math.Max(w, _min), _max);
}

/// <summary>弹出框里的标准内容外边距（8）与最大宽度（420）。对应 Mac 的 PopoverContent。</summary>
public sealed class PopoverContent : Border
{
    public PopoverContent(UIElement content, double width = 0)
    {
        Child = content;
        Padding = new Thickness(M.Md);
        if (width > 0) Width = width;
        MaxWidth = M.PopoverMaxWidth;
    }
}

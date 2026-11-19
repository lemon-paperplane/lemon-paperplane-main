using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LemonPaperplane;

/// <summary>
/// 链式修饰（对应 SwiftUI 的 modifier）。颜色一律用资源引用（切换主题自动更新），键名见 ColorToken。
/// 名字避开了元素已有的属性（Margin → Margins、Opacity → Alpha、Width → W …），否则 C# 会把调用当成属性。
/// </summary>
public static class UIExt
{
    // MARK: 颜色

    /// <summary>背景（Border / Panel / Control / TextBlock；Shape 为填充）。key 为 null 时清除。</summary>
    public static T Bg<T>(this T el, string? key) where T : FrameworkElement
    {
        DependencyProperty dp = el switch
        {
            Border => Border.BackgroundProperty,
            Panel => Panel.BackgroundProperty,
            Control => Control.BackgroundProperty,
            TextBlock => TextBlock.BackgroundProperty,
            Shape => Shape.FillProperty,
            _ => throw new ArgumentException(el.GetType().Name + " 没有背景，先用 UI.Box 包一层"),
        };
        if (key == null) el.ClearValue(dp);
        else
        {
            Theme.CheckKey(key);
            el.SetResourceReference(dp, key);
        }
        return el;
    }

    /// <summary>前景色（文字、PPIcon 的 currentColor）。设在容器上时子元素继承。</summary>
    public static T Fg<T>(this T el, string? key) where T : FrameworkElement
    {
        if (key == null) el.ClearValue(TextElement.ForegroundProperty);
        else
        {
            Theme.CheckKey(key);
            el.SetResourceReference(TextElement.ForegroundProperty, key);
        }
        return el;
    }

    /// <summary>描边（Border / Control 的 BorderBrush + 粗细；Shape 的 Stroke）。</summary>
    public static T Outline<T>(this T el, string? key, double thickness = 1) where T : FrameworkElement
    {
        switch (el)
        {
            case Border b:
                if (key == null) b.ClearValue(Border.BorderBrushProperty); else b.SetResourceReference(Border.BorderBrushProperty, key);
                b.BorderThickness = new Thickness(key == null ? 0 : thickness);
                break;
            case Control c:
                if (key == null) c.ClearValue(Control.BorderBrushProperty); else c.SetResourceReference(Control.BorderBrushProperty, key);
                c.BorderThickness = new Thickness(key == null ? 0 : thickness);
                break;
            case Shape s:
                if (key == null) s.ClearValue(Shape.StrokeProperty); else s.SetResourceReference(Shape.StrokeProperty, key);
                s.StrokeThickness = thickness;
                break;
            default:
                throw new ArgumentException(el.GetType().Name + " 没有描边，先用 UI.Box 包一层");
        }
        if (key != null) Theme.CheckKey(key);
        return el;
    }

    /// <summary>阴影（ShadowToken.Card / Popup / Dialog / Toast），null 清除。只加在背景形状上，别加在含文字的容器上（文字会变糊）。</summary>
    public static T Shadow<T>(this T el, string? key) where T : FrameworkElement
    {
        if (key == null) el.ClearValue(UIElement.EffectProperty);
        else el.SetResourceReference(UIElement.EffectProperty, key);
        return el;
    }

    // MARK: 尺寸与边距

    public static T Margins<T>(this T el, double all) where T : FrameworkElement { el.Margin = new Thickness(all); return el; }
    public static T Margins<T>(this T el, double horizontal, double vertical) where T : FrameworkElement { el.Margin = new Thickness(horizontal, vertical, horizontal, vertical); return el; }
    public static T Margins<T>(this T el, double left, double top, double right, double bottom) where T : FrameworkElement { el.Margin = new Thickness(left, top, right, bottom); return el; }

    /// <summary>内边距（Border / Control / TextBlock）。其它元素先用 UI.Box(el).Pad(…)。</summary>
    public static T Pad<T>(this T el, double all) where T : FrameworkElement => el.Pad(new Thickness(all));
    public static T Pad<T>(this T el, double horizontal, double vertical) where T : FrameworkElement => el.Pad(new Thickness(horizontal, vertical, horizontal, vertical));
    public static T Pad<T>(this T el, double left, double top, double right, double bottom) where T : FrameworkElement => el.Pad(new Thickness(left, top, right, bottom));

    public static T Pad<T>(this T el, Thickness t) where T : FrameworkElement
    {
        switch (el)
        {
            case Border b: b.Padding = t; break;
            case Control c: c.Padding = t; break;
            case TextBlock tb: tb.Padding = t; break;
            case Stack st: st.Padding = t; break;
            default: throw new ArgumentException(el.GetType().Name + " 没有内边距，先用 UI.Box 包一层");
        }
        return el;
    }

    public static T W<T>(this T el, double w) where T : FrameworkElement { el.Width = w; return el; }
    public static T H<T>(this T el, double h) where T : FrameworkElement { el.Height = h; return el; }
    public static T Size<T>(this T el, double w, double h) where T : FrameworkElement { el.Width = w; el.Height = h; return el; }
    public static T MinW<T>(this T el, double w) where T : FrameworkElement { el.MinWidth = w; return el; }
    public static T MaxW<T>(this T el, double w) where T : FrameworkElement { el.MaxWidth = w; return el; }
    public static T MinH<T>(this T el, double h) where T : FrameworkElement { el.MinHeight = h; return el; }
    public static T MaxH<T>(this T el, double h) where T : FrameworkElement { el.MaxHeight = h; return el; }

    /// <summary>圆角（Border）。</summary>
    public static Border Rounded(this Border b, double radius) { b.CornerRadius = new CornerRadius(radius); return b; }

    // MARK: 对齐与弹性

    public static T HAlign<T>(this T el, HorizontalAlignment a) where T : FrameworkElement { el.HorizontalAlignment = a; return el; }
    public static T VAlign<T>(this T el, VerticalAlignment a) where T : FrameworkElement { el.VerticalAlignment = a; return el; }
    public static T Left<T>(this T el) where T : FrameworkElement => el.HAlign(HorizontalAlignment.Left);
    public static T Right<T>(this T el) where T : FrameworkElement => el.HAlign(HorizontalAlignment.Right);
    public static T HCenter<T>(this T el) where T : FrameworkElement => el.HAlign(HorizontalAlignment.Center);
    public static T Top<T>(this T el) where T : FrameworkElement => el.VAlign(VerticalAlignment.Top);
    public static T Bottom<T>(this T el) where T : FrameworkElement => el.VAlign(VerticalAlignment.Bottom);
    public static T VCenter<T>(this T el) where T : FrameworkElement => el.VAlign(VerticalAlignment.Center);
    public static T Center<T>(this T el) where T : FrameworkElement => el.HCenter().VCenter();
    /// <summary>横向撑满（等同 SwiftUI 的 .frame(maxWidth: .infinity)，在 VStack 里生效）。</summary>
    public static T FillW<T>(this T el) where T : FrameworkElement => el.HAlign(HorizontalAlignment.Stretch);
    public static T FillH<T>(this T el) where T : FrameworkElement => el.VAlign(VerticalAlignment.Stretch);

    /// <summary>在 HStack / VStack 里分剩余空间（weight 为比例）。</summary>
    public static T Flex<T>(this T el, double weight = 1) where T : UIElement { Stack.SetFlex(el, weight); return el; }

    /// <summary>空间不够时可被压缩（UI.Text 默认开启）；false 时保持本来大小。</summary>
    public static T Shrink<T>(this T el, bool on = true) where T : UIElement { Stack.SetShrink(el, on); return el; }

    /// <summary>Grid 里的位置。</summary>
    public static T At<T>(this T el, int row, int column, int rowSpan = 1, int columnSpan = 1) where T : UIElement
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, column);
        if (rowSpan > 1) Grid.SetRowSpan(el, rowSpan);
        if (columnSpan > 1) Grid.SetColumnSpan(el, columnSpan);
        return el;
    }

    // MARK: 显示

    /// <summary>visible=false 时不占位（Collapsed）。</summary>
    public static T Visible<T>(this T el, bool visible) where T : UIElement { el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; return el; }

    /// <summary>隐藏但保留位置。</summary>
    public static T Hidden<T>(this T el, bool hidden = true) where T : UIElement { el.Visibility = hidden ? Visibility.Hidden : Visibility.Visible; return el; }

    public static T Alpha<T>(this T el, double opacity) where T : UIElement { el.Opacity = opacity; return el; }

    public static T Disabled<T>(this T el, bool disabled = true) where T : UIElement { el.IsEnabled = !disabled; return el; }

    public static T ZIndex<T>(this T el, int z) where T : UIElement { Panel.SetZIndex(el, z); return el; }

    // MARK: 文字

    public static T Font<T>(this T el, Typo typo) where T : FrameworkElement
    {
        switch (el)
        {
            case TextBlock t: typo.Apply(t); break;
            case Control c: typo.Apply(c); break;
            default: typo.ApplyInherited(el); break;
        }
        return el;
    }

    /// <summary>自动换行（不再截断）。</summary>
    public static TextBlock Wrap(this TextBlock t) { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; return t; }

    /// <summary>最多 n 行（多余的末尾省略号）。</summary>
    public static TextBlock Lines(this TextBlock t, int n)
    {
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        double lh = double.IsNaN(t.LineHeight) ? t.FontSize * 1.35 : t.LineHeight;
        t.MaxHeight = Math.Ceiling(lh * n) + t.Padding.Top + t.Padding.Bottom;
        return t;
    }

    public static TextBlock Align(this TextBlock t, TextAlignment a) { t.TextAlignment = a; return t; }

    /// <summary>等宽数字（计数、统计）。</summary>
    public static TextBlock TabularDigits(this TextBlock t) { Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular); return t; }

    public static TextBlock Underline(this TextBlock t, bool on = true) { t.TextDecorations = on ? TextDecorations.Underline : null; return t; }

    // MARK: 交互

    /// <summary>
    /// 点击（非按钮元素：卡片、行）。左键按下捕获、在元素上松开才算一次点击。
    /// 能用 PPButton / PPRow 的地方用它们（有键盘与自动化支持）。
    /// </summary>
    public static T OnClick<T>(this T el, Action? action) where T : FrameworkElement
    {
        el.SetValue(UI.ClickActionProperty, action);
        if (action == null) return el;
        if (el is Panel p && p.Background == null) p.Background = Brushes.Transparent;
        if (el is Border b && b.Background == null) b.Background = Brushes.Transparent;
        el.MouseLeftButtonDown += (s, e) =>
        {
            if (e.Handled) return;
            el.CaptureMouse();
            e.Handled = true;
        };
        el.MouseLeftButtonUp += (s, e) =>
        {
            if (!el.IsMouseCaptured) return;
            el.ReleaseMouseCapture();
            var pt = e.GetPosition(el);
            if (pt.X >= 0 && pt.Y >= 0 && pt.X <= el.ActualWidth && pt.Y <= el.ActualHeight)
            {
                e.Handled = true;
                UI.GetClickAction(el)?.Invoke();
            }
        };
        return el;
    }

    /// <summary>双击。</summary>
    public static T OnDoubleClick<T>(this T el, Action action) where T : FrameworkElement
    {
        el.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount == 2) { e.Handled = true; action(); }
        };
        return el;
    }

    /// <summary>鼠标进出。</summary>
    public static T OnHover<T>(this T el, Action<bool> changed) where T : FrameworkElement
    {
        el.MouseEnter += (_, _) => changed(true);
        el.MouseLeave += (_, _) => changed(false);
        return el;
    }

    /// <summary>悬停提示（自绘外观，见 Templates/Controls.xaml）。</summary>
    public static T Tip<T>(this T el, string? text) where T : FrameworkElement
    {
        el.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        return el;
    }

    /// <summary>自动化 ID（调试桥 click / tree 用，也是重建时恢复焦点与滚动位置的键）。</summary>
    public static T Id<T>(this T el, string id) where T : UIElement { AutomationProperties.SetAutomationId(el, id); return el; }

    /// <summary>无障碍名字（读屏、调试桥按文字查找）。</summary>
    public static T A11y<T>(this T el, string? name) where T : UIElement { AutomationProperties.SetName(el, name ?? ""); return el; }

    /// <summary>重建时认出同一个元素的键（见 PPView）。</summary>
    public static T Key<T>(this T el, string key) where T : UIElement { el.SetValue(UI.KeyProperty, key); return el; }

    public static T Hand<T>(this T el) where T : FrameworkElement { el.Cursor = Cursors.Hand; return el; }

    /// <summary>直接改元素（逃生口）。</summary>
    public static T Do<T>(this T el, Action<T> action) where T : DependencyObject { action(el); return el; }
}

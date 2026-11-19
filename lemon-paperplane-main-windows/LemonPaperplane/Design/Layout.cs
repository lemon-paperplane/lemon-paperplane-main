using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LemonPaperplane;

// 布局辅助：像写 SwiftUI 一样写 WPF 元素树。
//   UI.VStack(M.Md,
//       UI.Text(L.T("project.title"), Typo.Title),
//       UI.HStack(M.Sm, new PPIcon("folder"), UI.Text(path, Typo.Small, ColorToken.TextTertiary), UI.Spacer()),
//       ok ? new PPBadge("LTS", tone: "success") : null)
//     .Pad(M.Lg).Bg(ColorToken.BgSurface)
// 子元素可以是 UIElement、字符串（变成 UI.Text）、IEnumerable（展开）或 null（跳过）。

#region 绑定与状态

/// <summary>可读写的值（对应 SwiftUI 的 Binding）。组件的输入值都用它：State&lt;T&gt; 或 Bind.Of(() =&gt; x, v =&gt; x = v)。</summary>
public abstract class Bind<T>
{
    public abstract T Value { get; set; }
}

public static class Bind
{
    private sealed class FuncBind<T> : Bind<T>
    {
        private readonly Func<T> _get;
        private readonly Action<T> _set;
        public FuncBind(Func<T> get, Action<T> set) { _get = get; _set = set; }
        public override T Value { get => _get(); set => _set(value); }
    }

    public static Bind<T> Of<T>(Func<T> get, Action<T> set) => new FuncBind<T>(get, set);

    /// <summary>只读常量（写入被忽略）。</summary>
    public static Bind<T> Const<T>(T value) => new FuncBind<T>(() => value, _ => { });
}

/// <summary>
/// 视图里的可观察局部状态（对应 SwiftUI 的 @State）。在 PPView 里用 UseState 创建，值变了自动重建该视图。
/// 也可以单独 new，传给组件做 Bind，再用 PPView.Observe 订阅。
/// </summary>
public sealed class State<T> : Bind<T>, INotifyPropertyChanged
{
    private T _value;
    public State(T initial) { _value = initial; }

    public override T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static implicit operator T(State<T> s) => s.Value;
}

/// <summary>INotifyPropertyChanged 小基类（store、视图模型用）：属性 setter 里 Set(ref _x, value)。</summary>
public abstract class PPObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    /// <summary>通知属性变化（name 为 null 时表示整个对象都变了）。</summary>
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 跨重建保留的小状态（区块折叠、分栏宽度等，对应 SwiftUI 按身份保留的 @State / @SceneStorage）：
/// UIState.Get("overview.modules.collapsed", false)。只在本次运行内有效。
/// </summary>
public static class UIState
{
    private static readonly Dictionary<string, object?> Values = new();

    public static T Get<T>(string key, T fallback) => Values.TryGetValue(key, out var v) && v is T t ? t : fallback;

    public static void Set<T>(string key, T value) => Values[key] = value;

    /// <summary>以 key 存储的 Bind（读写 UIState）。</summary>
    public static Bind<T> Bind<T>(string key, T fallback) => LemonPaperplane.Bind.Of(() => Get(key, fallback), v => Set(key, v));
}

#endregion

#region 布局容器

public enum CrossAlign { Start, Center, End, Stretch }

/// <summary>
/// 横排 / 竖排容器（HStack / VStack）：子元素之间固定间距；Flex 子元素（UI.Spacer、.Flex()）分剩余空间；
/// Shrink 子元素（UI.Text 默认是）在空间不够时先被压缩（文字截断），其余子元素保持本来大小。
/// 交叉轴对齐：子元素自己设了 HorizontalAlignment / VerticalAlignment 时以它为准，否则用 Align。
/// HStack 默认垂直居中，VStack 默认左对齐（等同 SwiftUI 的 VStack(alignment: .leading)）。
/// </summary>
public class Stack : Panel
{
    public static readonly DependencyProperty FlexProperty = DependencyProperty.RegisterAttached(
        "Flex", typeof(double), typeof(Stack), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty ShrinkProperty = DependencyProperty.RegisterAttached(
        "Shrink", typeof(bool), typeof(Stack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetFlex(DependencyObject d) => (double)d.GetValue(FlexProperty);
    public static void SetFlex(DependencyObject d, double v) => d.SetValue(FlexProperty, v);
    public static bool GetShrink(DependencyObject d) => (bool)d.GetValue(ShrinkProperty);
    public static void SetShrink(DependencyObject d, bool v) => d.SetValue(ShrinkProperty, v);

    private Orientation _orientation;
    private double _spacing;
    private CrossAlign _align;
    private Thickness _padding;

    public Stack(Orientation orientation, double spacing = 0)
    {
        _orientation = orientation;
        _spacing = spacing;
        _align = orientation == Orientation.Horizontal ? CrossAlign.Center : CrossAlign.Start;
    }

    public Orientation Orientation { get => _orientation; set { _orientation = value; InvalidateMeasure(); } }
    public double Spacing { get => _spacing; set { _spacing = value; InvalidateMeasure(); } }
    public CrossAlign Align { get => _align; set { _align = value; InvalidateArrange(); } }

    /// <summary>内边距（背景画到边，内容往里收）。</summary>
    public Thickness Padding { get => _padding; set { _padding = value; InvalidateMeasure(); } }

    private bool H => _orientation == Orientation.Horizontal;
    private double Main(Size s) => H ? s.Width : s.Height;
    private double Cross(Size s) => H ? s.Height : s.Width;
    private Size Make(double main, double cross) => H ? new Size(main, cross) : new Size(cross, main);

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c != null && c.Visibility != Visibility.Collapsed).ToList();

    private static double MinMain(UIElement c, bool h) => c is SpacerElement sp ? sp.MinLength : 0;

    protected override Size MeasureOverride(Size outer)
    {
        double padW = _padding.Left + _padding.Right, padH = _padding.Top + _padding.Bottom;
        var avail = new Size(Math.Max(0, outer.Width - padW), Math.Max(0, outer.Height - padH));
        var inner = MeasureInner(avail);
        return new Size(inner.Width + padW, inner.Height + padH);
    }

    private Size MeasureInner(Size avail)
    {
        foreach (UIElement c in InternalChildren)
            if (c != null && c.Visibility == Visibility.Collapsed) c.Measure(new Size(0, 0));
        var kids = Visible();
        double availMain = Main(avail), availCross = Cross(avail);
        double used = _spacing * Math.Max(0, kids.Count - 1);
        double cross = 0, flexTotal = 0;
        var shrinkers = new List<UIElement>();
        var flexers = new List<UIElement>();
        foreach (var c in kids)
        {
            double flex = GetFlex(c);
            if (flex > 0) { flexers.Add(c); flexTotal += flex; used += MinMain(c, H); continue; }
            if (GetShrink(c)) { shrinkers.Add(c); continue; }
            c.Measure(Make(double.PositiveInfinity, availCross));
            used += Main(c.DesiredSize);
            cross = Math.Max(cross, Cross(c.DesiredSize));
        }
        // 可压缩的：先按本来大小量，放不下时从小到大平分剩余空间
        if (shrinkers.Count > 0)
        {
            foreach (var c in shrinkers) c.Measure(Make(double.PositiveInfinity, availCross));
            double want = shrinkers.Sum(c => Main(c.DesiredSize));
            double remaining = Math.Max(0, availMain - used);
            if (!double.IsInfinity(availMain) && want > remaining)
            {
                int left = shrinkers.Count;
                foreach (var c in shrinkers.OrderBy(c => Main(c.DesiredSize)).ToList())
                {
                    double share = remaining / left;
                    double give = Math.Min(Main(c.DesiredSize), share);
                    c.Measure(Make(give, availCross));
                    remaining -= Main(c.DesiredSize);
                    left--;
                }
            }
            foreach (var c in shrinkers)
            {
                used += Main(c.DesiredSize);
                cross = Math.Max(cross, Cross(c.DesiredSize));
            }
        }
        // 弹性的：分剩余空间（可用空间无限时按本来大小）
        if (flexers.Count > 0)
        {
            double rest = double.IsInfinity(availMain) ? double.PositiveInfinity : Math.Max(0, availMain - used);
            foreach (var c in flexers)
            {
                double give = double.IsInfinity(rest) ? double.PositiveInfinity : MinMain(c, H) + rest * GetFlex(c) / flexTotal;
                c.Measure(Make(give, availCross));
                cross = Math.Max(cross, Cross(c.DesiredSize));
                if (double.IsInfinity(rest)) used += Math.Max(0, Main(c.DesiredSize) - MinMain(c, H));
            }
            if (!double.IsInfinity(availMain)) used = Math.Max(used, availMain); // 有 Spacer 时撑满
        }
        return Make(used, cross);
    }

    protected override Size ArrangeOverride(Size outer)
    {
        var final = new Size(Math.Max(0, outer.Width - _padding.Left - _padding.Right), Math.Max(0, outer.Height - _padding.Top - _padding.Bottom));
        var kids = Visible();
        double finalMain = Main(final), finalCross = Cross(final);
        double fixedSum = _spacing * Math.Max(0, kids.Count - 1);
        double flexTotal = 0;
        foreach (var c in kids)
        {
            double f = GetFlex(c);
            if (f > 0) { flexTotal += f; fixedSum += MinMain(c, H); }
            else fixedSum += Main(c.DesiredSize);
        }
        double rest = Math.Max(0, finalMain - fixedSum);
        double pos = 0;
        foreach (var c in kids)
        {
            double f = GetFlex(c);
            double main = f > 0 ? MinMain(c, H) + rest * f / flexTotal : Main(c.DesiredSize);
            double cd = Math.Min(Cross(c.DesiredSize), finalCross);
            var align = EffectiveAlign(c);
            double cs, co;
            switch (align)
            {
                case CrossAlign.Stretch: cs = finalCross; co = 0; break;
                case CrossAlign.Center: cs = cd; co = (finalCross - cd) / 2; break;
                case CrossAlign.End: cs = cd; co = finalCross - cd; break;
                default: cs = cd; co = 0; break;
            }
            var r = H ? new Rect(pos, co, main, cs) : new Rect(co, pos, cs, main);
            r.Offset(_padding.Left, _padding.Top);
            c.Arrange(r);
            pos += main + _spacing;
        }
        return outer;
    }

    private CrossAlign EffectiveAlign(UIElement c)
    {
        if (c is FrameworkElement fe)
        {
            var dp = H ? FrameworkElement.VerticalAlignmentProperty : FrameworkElement.HorizontalAlignmentProperty;
            if (fe.ReadLocalValue(dp) != DependencyProperty.UnsetValue)
            {
                if (H)
                    return fe.VerticalAlignment switch { VerticalAlignment.Top => CrossAlign.Start, VerticalAlignment.Center => CrossAlign.Center, VerticalAlignment.Bottom => CrossAlign.End, _ => CrossAlign.Stretch };
                return fe.HorizontalAlignment switch { HorizontalAlignment.Left => CrossAlign.Start, HorizontalAlignment.Center => CrossAlign.Center, HorizontalAlignment.Right => CrossAlign.End, _ => CrossAlign.Stretch };
            }
        }
        return _align;
    }
}

/// <summary>
/// 按内容定宽（SwiftUI 的 .fixedSize(horizontal: true)）：先按无限宽量出内容的自然宽度，再夹在 [MinFit, MaxFit] 之间重新量。
/// 菜单、下拉列表用它：各行等宽、取最宽的一行，行里的 Spacer 不会把面板撑到最大宽度。
/// </summary>
public sealed class FitWidth : Decorator
{
    public double MinFit { get; set; }
    public double MaxFit { get; set; } = double.PositiveInfinity;

    public FitWidth(UIElement child, double min = 0, double max = double.PositiveInfinity)
    {
        Child = child;
        MinFit = min;
        MaxFit = max;
    }

    protected override Size MeasureOverride(Size c)
    {
        if (Child == null) return new Size();
        Child.Measure(new Size(double.PositiveInfinity, c.Height));
        double w = Math.Max(MinFit, Math.Min(Child.DesiredSize.Width, Math.Min(MaxFit, c.Width)));
        Child.Measure(new Size(w, c.Height));
        return new Size(w, Child.DesiredSize.Height);
    }
}

/// <summary>弹性空白（UI.Spacer）。</summary>
public sealed class SpacerElement : FrameworkElement
{
    public double MinLength { get; }
    public SpacerElement(double minLength) { MinLength = minLength; Stack.SetFlex(this, 1); IsHitTestVisible = false; }
}

/// <summary>自动换行排列（徽标、按钮组）。对应 Mac 的 FlowRow。</summary>
public sealed class FlowRow : Panel
{
    private double _spacing;
    private double _lineSpacing;

    public FlowRow(double spacing = 4, double? lineSpacing = null)
    {
        _spacing = spacing;
        _lineSpacing = lineSpacing ?? spacing;
    }

    public double Spacing { get => _spacing; set { _spacing = value; InvalidateMeasure(); } }
    public double LineSpacing { get => _lineSpacing; set { _lineSpacing = value; InvalidateMeasure(); } }

    protected override Size MeasureOverride(Size avail)
    {
        double maxW = avail.Width;
        double x = 0, y = 0, lineH = 0, width = 0;
        foreach (UIElement c in InternalChildren)
        {
            if (c == null) continue;
            c.Measure(new Size(maxW, double.PositiveInfinity));
            if (c.Visibility == Visibility.Collapsed) continue;
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > maxW) { y += lineH + _lineSpacing; x = 0; lineH = 0; }
            x += d.Width + _spacing;
            lineH = Math.Max(lineH, d.Height);
            width = Math.Max(width, x - _spacing);
        }
        return new Size(width, y + lineH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, lineH = 0;
        var line = new List<UIElement>();
        void Flush()
        {
            double lx = 0;
            foreach (var e in line)
            {
                var d = e.DesiredSize;
                e.Arrange(new Rect(lx, y + (lineH - d.Height) / 2, d.Width, d.Height));
                lx += d.Width + _spacing;
            }
            line.Clear();
        }
        foreach (UIElement c in InternalChildren)
        {
            if (c == null || c.Visibility == Visibility.Collapsed) continue;
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > final.Width)
            {
                Flush();
                y += lineH + _lineSpacing;
                x = 0;
                lineH = 0;
            }
            line.Add(c);
            x += d.Width + _spacing;
            lineH = Math.Max(lineH, d.Height);
        }
        Flush();
        return final;
    }
}

/// <summary>中间截断的单行文字（路径、地址）：放不下时保留首尾、中间用「…」。完整文字在 FullText，截断时悬停提示显示全文。</summary>
public sealed class MiddleTrimText : Decorator
{
    private string _full = "";

    /// <summary>实际显示文字的 TextBlock（字体、行高设在它上面）。</summary>
    public TextBlock Inner { get; } = new() { TextWrapping = TextWrapping.NoWrap };

    public MiddleTrimText(string text)
    {
        Child = Inner;
        FullText = text;
    }

    public string FullText
    {
        get => _full;
        set
        {
            _full = value ?? "";
            Inner.Text = _full;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var text = _full;
        double w = constraint.Width - Inner.Padding.Left - Inner.Padding.Right;
        if (!double.IsInfinity(w) && _full.Length > 1 && !Fits(_full, w))
        {
            int lo = 0, hi = _full.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Fits(Make(mid), w)) lo = mid;
                else hi = mid - 1;
            }
            text = Make(lo);
        }
        if (Inner.Text != text) Inner.Text = text;
        ToolTip = text == _full ? null : _full;
        return base.MeasureOverride(constraint);
    }

    private string Make(int keep)
    {
        int head = (keep + 1) / 2, tail = keep / 2;
        return _full.Substring(0, head) + "\u2026" + _full.Substring(_full.Length - tail);
    }

    private bool Fits(string s, double width)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection, new Typeface(Inner.FontFamily, Inner.FontStyle, Inner.FontWeight, Inner.FontStretch),
            Inner.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(this), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace <= width + 0.01;
    }
}

#endregion

/// <summary>元素工厂（HStack、VStack、Text、Spacer…）。写界面时可以 using static LemonPaperplane.UI;</summary>
public static class UI
{
    public static Stack HStack(double spacing, params object?[] children) => Fill(new Stack(Orientation.Horizontal, spacing), children);

    public static Stack VStack(double spacing, params object?[] children) => Fill(new Stack(Orientation.Vertical, spacing), children);

    /// <summary>叠放（后面的在上面），等同一个单元格的 Grid。</summary>
    public static Grid ZStack(params object?[] children) => Fill(new Grid(), children);

    public static FlowRow Flow(double spacing, params object?[] children) => Fill(new FlowRow(spacing), children);

    public static T Fill<T>(T panel, IEnumerable<object?> children) where T : Panel
    {
        foreach (var c in Flatten(children)) panel.Children.Add(c);
        return panel;
    }

    /// <summary>展开子元素：UIElement 原样、字符串变成 UI.Text、IEnumerable 展开、null 跳过。</summary>
    public static IEnumerable<UIElement> Flatten(IEnumerable<object?> items)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case null: break;
                case UIElement e: yield return e; break;
                case string s: yield return Text(s); break;
                case IEnumerable seq:
                    foreach (var e in Flatten(seq.Cast<object?>())) yield return e;
                    break;
                default: throw new ArgumentException("不支持的子元素：" + item.GetType().Name);
            }
        }
    }

    /// <summary>弹性空白（撑开，把后面的元素推到另一头）；minLength 为最小长度。</summary>
    public static SpacerElement Spacer(double minLength = 0) => new(minLength);

    /// <summary>固定长度的空白（两个方向都是 length）。</summary>
    public static FrameworkElement Gap(double length) => new FrameworkElement { Width = length, Height = length, IsHitTestVisible = false };

    /// <summary>单行文字（放不下时末尾省略号，在 HStack 里先被压缩）。默认正文字号、textPrimary 由外层继承。</summary>
    public static TextBlock Text(string? text, Typo? font = null, string? color = null)
    {
        var t = new TextBlock { Text = text ?? "", TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        (font ?? Typo.Body).Apply(t);
        if (color != null) t.Fg(color);
        Stack.SetShrink(t, true);
        return t;
    }

    /// <summary>多行文字（自动换行）。</summary>
    public static TextBlock Paragraph(string? text, Typo? font = null, string? color = null)
    {
        var t = Text(text, font, color);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        return t;
    }

    /// <summary>中间截断的单行文字（路径、地址）。</summary>
    public static MiddleTrimText TextMiddle(string? text, Typo? font = null, string? color = null)
    {
        var t = new MiddleTrimText(text ?? "");
        (font ?? Typo.Body).Apply(t.Inner);
        if (color != null) t.Fg(color);
        Stack.SetShrink(t, true);
        return t;
    }

    /// <summary>可选中复制的文字（只读、无边框，对应 SwiftUI 的 .textSelection(.enabled)）。</summary>
    public static TextBox Selectable(string? text, Typo? font = null, string? color = null, bool wrap = true)
    {
        var t = new TextBox
        {
            Text = text ?? "",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(-2, 0, -2, 0), // 抵消 TextBox 文字区自带的左右 2px，和普通文字对齐
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Focusable = true,
            IsTabStop = false,
        };
        (font ?? Typo.Body).Apply(t);
        t.Fg(color ?? ColorToken.TextPrimary);
        Stack.SetShrink(t, true);
        return t;
    }

    public static PPIcon Icon(string name, double size = 0, string? color = null) => new(name, size, color);

    /// <summary>容器（Border）：背景、描边、圆角、内边距都设在它上面。</summary>
    public static Border Box(UIElement? child = null) => new() { Child = child, SnapsToDevicePixels = true };

    /// <summary>纵向（或横向）滚动区域（自绘细滚动条）。</summary>
    public static ScrollViewer Scroll(UIElement content, bool horizontal = false, bool vertical = true) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        Focusable = false,
        Padding = new Thickness(0),
    };

    /// <summary>分隔线（1px border；subtle 用 borderSubtle）。</summary>
    public static Border Divider(bool vertical = false, bool subtle = false)
    {
        var b = new Border { SnapsToDevicePixels = true, IsHitTestVisible = false };
        if (vertical) { b.Width = 1; b.VerticalAlignment = VerticalAlignment.Stretch; }
        else { b.Height = 1; b.HorizontalAlignment = HorizontalAlignment.Stretch; }
        return b.Bg(subtle ? ColorToken.BorderSubtle : ColorToken.Border);
    }

    /// <summary>网格：columns / rows 用 "Auto,*,120,2*" 这样的写法；子元素用 .At(row, col) 摆放。</summary>
    public static Grid Grid(string? columns, string? rows, params object?[] children)
    {
        var g = new Grid();
        var conv = new GridLengthConverter();
        if (!string.IsNullOrEmpty(columns))
            foreach (var c in columns!.Split(',')) g.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)conv.ConvertFromInvariantString(c.Trim())! });
        if (!string.IsNullOrEmpty(rows))
            foreach (var r in rows!.Split(',')) g.RowDefinitions.Add(new RowDefinition { Height = (GridLength)conv.ConvertFromInvariantString(r.Trim())! });
        return Fill(g, children);
    }

    /// <summary>把元素从原来的父元素上摘下来（复用元素前用）。</summary>
    public static void Detach(UIElement el)
    {
        var parent = VisualTreeHelper.GetParent(el) ?? LogicalTreeHelper.GetParent(el);
        switch (parent)
        {
            case Panel p: p.Children.Remove(el); break;
            case Decorator d when d.Child == el: d.Child = null; break;
            case ContentPresenter cp when cp.Content == el: cp.Content = null; break;
            case ContentControl cc when cc.Content == el: cc.Content = null; break;
        }
        if (LogicalTreeHelper.GetParent(el) is ContentControl lc && lc.Content == el) lc.Content = null;
        if (LogicalTreeHelper.GetParent(el) is Decorator ld && ld.Child == el) ld.Child = null;
    }

    // MARK: 点击（非按钮元素）

    /// <summary>.OnClick 登记的动作（调试桥按文字/ID 点击时调用）。</summary>
    public static readonly DependencyProperty ClickActionProperty = DependencyProperty.RegisterAttached(
        "ClickAction", typeof(Action), typeof(UI), new PropertyMetadata(null));

    public static Action? GetClickAction(DependencyObject d) => d.GetValue(ClickActionProperty) as Action;

    /// <summary>重建时用来认出「同一个」元素的键（恢复焦点、滚动位置）；没设时用 AutomationId。</summary>
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(UI), new PropertyMetadata(null));

    public static string? GetKey(DependencyObject d)
    {
        if (d.GetValue(KeyProperty) is string k && k.Length > 0) return k;
        var id = AutomationProperties.GetAutomationId(d);
        return string.IsNullOrEmpty(id) ? null : id;
    }
}

/// <summary>键盘：修饰键（调试桥可以覆盖）、快捷键解析与显示。</summary>
public static class KeyInput
{
    /// <summary>调试桥模拟按键时覆盖修饰键状态（WPF 的 Keyboard.Modifiers 读的是真实键盘）。</summary>
    public static ModifierKeys? Override { get; set; }

    /// <summary>当前修饰键：组件和快捷键处理一律用它，不直接读 Keyboard.Modifiers。</summary>
    public static ModifierKeys Modifiers => Override ?? Keyboard.Modifiers;

    /// <summary>解析 "Ctrl+Shift+N"、"Escape"、"Down"、"F5"、"Ctrl+,"。</summary>
    public static bool TryParse(string text, out Key key, out ModifierKeys mods)
    {
        key = Key.None;
        mods = ModifierKeys.None;
        var parts = text.Split('+').Select(p => p.Trim()).ToList();
        if (text.EndsWith("++", StringComparison.Ordinal)) { parts.RemoveAll(p => p.Length == 0); parts.Add("+"); }
        foreach (var p in parts.Take(parts.Count - 1))
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default: return false;
            }
        }
        var k = parts.LastOrDefault() ?? "";
        key = k.ToLowerInvariant() switch
        {
            "esc" or "escape" => Key.Escape,
            "enter" or "return" => Key.Enter,
            "," => Key.OemComma,
            "." => Key.OemPeriod,
            "+" => Key.OemPlus,
            "-" => Key.OemMinus,
            "space" => Key.Space,
            "del" => Key.Delete,
            "up" => Key.Up,
            "down" => Key.Down,
            "left" => Key.Left,
            "right" => Key.Right,
            _ => Enum.TryParse<Key>(k.Length == 1 && char.IsDigit(k[0]) ? "D" + k : k, true, out var parsed) ? parsed : Key.None,
        };
        return key != Key.None;
    }

    /// <summary>菜单里显示的快捷键文字（Ctrl+Shift+N）。</summary>
    public static string Format(Key key, ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key switch
        {
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }
}

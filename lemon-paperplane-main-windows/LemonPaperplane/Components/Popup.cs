using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LemonPaperplane;

// 弹出层：WPF Popup + 自绘外壳（bgElevated、1px border、圆角 8、popup 阴影），下拉、菜单、弹出框、右键菜单共用。
// 贴锚点下方（空间不够翻到上方），点外面 / Esc / 切到别的应用 / 窗口移动时关闭。对应 Mac 的 Popup.swift。

public enum PopupAlignment { Leading, Trailing, Center }

/// <summary>一个打开着的弹出层。</summary>
public sealed class PPPopup
{
    internal PPPopup(Popup popup, FrameworkElement chrome, FrameworkElement panel, FrameworkElement? anchor)
    {
        Popup = popup;
        Chrome = chrome;
        Panel = panel;
        Anchor = anchor;
    }

    public Popup Popup { get; }
    /// <summary>含阴影留白的根。</summary>
    internal FrameworkElement Chrome { get; }
    /// <summary>可见的面板（圆角框）。</summary>
    public FrameworkElement Panel { get; }
    public FrameworkElement? Anchor { get; }
    public Action? OnClose { get; set; }
    /// <summary>键盘处理（菜单的上下 / 回车 / 左右）：返回 true 表示已处理。</summary>
    public Func<KeyEventArgs, bool>? KeyHandler { get; set; }
    public bool IsOpen => Popup.IsOpen;

    public void Close() => PopupController.Dismiss(this);
}

/// <summary>管理全部弹出层（先开的在下，子菜单在上）。</summary>
public static class PopupController
{
    private static readonly List<PPPopup> OpenList = new();
    private static readonly HashSet<Window> Hooked = new();
    private static readonly Dictionary<FrameworkElement, DateTime> RecentlyDismissed = new();
    private static bool _appHooked;

    /// <summary>阴影留白（弹出窗口比可见面板大这么多，阴影画在里面）。</summary>
    public static readonly Thickness ShadowPad = new(12, 6, 12, 18);
    private const double Gap = 4;

    public static IReadOnlyList<PPPopup> Open => OpenList;
    public static bool IsShowingAny => OpenList.Count > 0;
    public static PPPopup? Top => OpenList.Count > 0 ? OpenList[OpenList.Count - 1] : null;

    /// <summary>有弹出层打开或关闭时触发（调试桥、截图用）。</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// 在锚点下方弹出 content。alignment：左对齐 / 右对齐 / 居中；preferRight：贴在锚点右侧（子菜单）；
    /// at：相对锚点的位置（右键菜单在鼠标处弹出）；wantsKey：内容需要键盘焦点（筛选框）。
    /// </summary>
    public static PPPopup Present(FrameworkElement anchor, UIElement content, PopupAlignment alignment = PopupAlignment.Leading,
                                  bool preferRight = false, bool wantsKey = false, Action? onClose = null, Point? at = null)
    {
        HookWindow(Window.GetWindow(anchor));
        var panel = new Border
        {
            CornerRadius = new CornerRadius(M.RadiusLG),
            BorderThickness = new Thickness(1),
            Child = content,
            SnapsToDevicePixels = true,
        }.Bg(ColorToken.BgElevated).Outline(ColorToken.Border);
        var clip = new RectangleGeometry { RadiusX = M.RadiusLG, RadiusY = M.RadiusLG };
        panel.SizeChanged += (_, e) => clip.Rect = new Rect(e.NewSize);
        panel.Clip = clip;
        var shadow = new Border { CornerRadius = new CornerRadius(M.RadiusLG) }.Bg(ColorToken.BgElevated).Shadow(ShadowToken.Popup);
        var root = new Grid { Margin = ShadowPad };
        root.Children.Add(shadow);
        root.Children.Add(panel);
        var outer = new Border { Child = root, Background = Brushes.Transparent };
        Templates.ApplyRootDefaults(outer);
        outer.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Cycle);
        outer.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Contained);

        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Placement = PlacementMode.Relative,
            PlacementTarget = anchor,
            Child = outer,
            PopupAnimation = PopupAnimation.None,
            Focusable = wantsKey,
        };
        var p = new PPPopup(popup, outer, panel, anchor);
        outer.PreviewMouseDown += (_, _) =>
        {
            // 在父菜单里点击时关掉它上面的子菜单
            int idx = OpenList.IndexOf(p);
            if (idx >= 0 && idx + 1 < OpenList.Count) Dismiss(OpenList[idx + 1]);
        };
        outer.PreviewKeyDown += (_, e) => HandleKey(e);
        OpenList.Add(p);
        // 锚点从界面上移除（所在视图重建、页面切换）时跟着关闭，对应 Mac 的 onDisappear
        RoutedEventHandler? unloaded = null;
        unloaded = (_, _) => { anchor.Unloaded -= unloaded; Dismiss(p); };
        anchor.Unloaded += unloaded;
        var userClose = onClose;
        p.OnClose = () => { anchor.Unloaded -= unloaded; userClose?.Invoke(); };
        Position(p, alignment, preferRight, at);
        popup.IsOpen = true;
        // 内容尺寸变化（异步加载、筛选）后重新定位
        outer.SizeChanged += (_, e) =>
        {
            if (e.PreviousSize.Width > 0 && popup.IsOpen) Position(p, alignment, preferRight, at);
        };
        if (wantsKey)
        {
            popup.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                var first = PPView.Descendants<UIElement>(outer).FirstOrDefault(e => e is TextBox || e is PasswordBox);
                if (first != null) Keyboard.Focus(first);
            }));
        }
        Changed?.Invoke(null, EventArgs.Empty);
        return p;
    }

    /// <summary>按内容尺寸、锚点在屏幕上的位置算出相对锚点的偏移。</summary>
    private static void Position(PPPopup p, PopupAlignment alignment, bool preferRight, Point? at)
    {
        var anchor = p.Anchor;
        if (anchor == null) return;
        p.Chrome.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var full = p.Chrome.DesiredSize;
        double w = full.Width - ShadowPad.Left - ShadowPad.Right, h = full.Height - ShadowPad.Top - ShadowPad.Bottom;
        var rects = PresentationSource.FromVisual(anchor) != null ? Native.ScreenRects(anchor) : null;
        // 锚点矩形（相对锚点左上角）
        var a = at is Point pt ? new Rect(pt, new Size(0, 0)) : new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight);
        double x, y;
        if (preferRight)
        {
            x = a.Right - 2;
            y = a.Top - 4; // 子菜单首项与父项对齐（菜单有 4px 内边距）
        }
        else
        {
            x = alignment switch
            {
                PopupAlignment.Trailing => a.Right - w,
                PopupAlignment.Center => a.Left + a.Width / 2 - w / 2,
                _ => a.Left,
            };
            y = a.Bottom + Gap;
        }
        if (rects.HasValue)
        {
            var (el, work) = rects.Value;
            // 换算到屏幕坐标判断空间
            double sx = el.Left + x, sy = el.Top + y;
            if (preferRight)
            {
                if (sx + w > work.Right) sx = el.Left + a.Left - w + 2;
            }
            else if (sy + h > work.Bottom && el.Top + a.Top - Gap - h >= work.Top)
            {
                sy = el.Top + a.Top - Gap - h; // 下方放不下翻到上方
            }
            sx = Math.Min(Math.Max(sx, work.Left + 4), Math.Max(work.Left + 4, work.Right - w - 4));
            sy = Math.Min(Math.Max(sy, work.Top + 4), Math.Max(work.Top + 4, work.Bottom - h - 4));
            x = sx - el.Left;
            y = sy - el.Top;
        }
        p.Popup.HorizontalOffset = x - ShadowPad.Left;
        p.Popup.VerticalOffset = y - ShadowPad.Top;
    }

    /// <summary>关闭它和它上面的（子菜单）。</summary>
    public static void Dismiss(PPPopup p)
    {
        int idx = OpenList.IndexOf(p);
        if (idx < 0) return;
        var closing = OpenList.Skip(idx).ToList();
        OpenList.RemoveRange(idx, OpenList.Count - idx);
        closing.Reverse();
        foreach (var c in closing) CloseOne(c);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void DismissAll()
    {
        if (OpenList.Count == 0) return;
        var all = OpenList.ToList();
        OpenList.Clear();
        all.Reverse();
        foreach (var c in all) CloseOne(c);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>关闭某个弹出层上面的全部（父菜单里移动时关子菜单）。</summary>
    public static void DismissAbove(PPPopup p)
    {
        int idx = OpenList.IndexOf(p);
        if (idx >= 0 && idx + 1 < OpenList.Count) Dismiss(OpenList[idx + 1]);
    }

    private static void CloseOne(PPPopup p)
    {
        p.Popup.IsOpen = false;
        p.Popup.Child = null;
        var cb = p.OnClose;
        p.OnClose = null;
        cb?.Invoke();
    }

    /// <summary>
    /// 锚点按钮在「按下时关掉了自己的弹出层」后，紧接着的点击不应该再打开（否则点按钮关不掉）。按钮的点击处理里先查这个。
    /// </summary>
    public static bool WasJustDismissed(FrameworkElement anchor)
    {
        if (!RecentlyDismissed.TryGetValue(anchor, out var t)) return false;
        RecentlyDismissed.Remove(anchor);
        return (DateTime.UtcNow - t).TotalMilliseconds < 600;
    }

    /// <summary>窗口与弹出层里的按键：先给最上层的处理函数（菜单导航），Esc 关最上层。</summary>
    public static void HandleKey(KeyEventArgs e)
    {
        if (e.Handled) return;
        var top = Top;
        if (top == null) return;
        if (top.KeyHandler != null && top.KeyHandler(e)) { e.Handled = true; return; }
        if (e.Key == Key.Escape)
        {
            Dismiss(top);
            e.Handled = true;
        }
    }

    /// <summary>窗口上点击（弹出层外）：全部关闭，并记下点到的锚点。</summary>
    private static void OnWindowMouseDown(object sender, MouseButtonEventArgs e) => SimulateOutsidePress(e.OriginalSource as DependencyObject);

    /// <summary>弹出层外的一次按下（窗口鼠标事件；调试桥点击前也调用）。</summary>
    public static void SimulateOutsidePress(DependencyObject? src)
    {
        if (OpenList.Count == 0) return;
        foreach (var p in OpenList)
            if (p.Anchor != null && src != null && IsWithin(src, p.Anchor)) RecentlyDismissed[p.Anchor] = DateTime.UtcNow;
        DismissAll();
    }

    private static bool IsWithin(DependencyObject d, DependencyObject ancestor)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (ReferenceEquals(x, ancestor)) return true;
        return false;
    }

    /// <summary>每个窗口装一次：点击、按键、移动 / 缩放 / 最小化时关闭；切到别的应用时关闭。</summary>
    public static void HookWindow(Window? w)
    {
        if (!_appHooked && Application.Current != null)
        {
            _appHooked = true;
            Application.Current.Deactivated += (_, _) => DismissAll();
        }
        if (w == null || !Hooked.Add(w)) return;
        w.PreviewMouseDown += OnWindowMouseDown;
        w.PreviewKeyDown += (_, e) => HandleKey(e);
        w.LocationChanged += (_, _) => DismissAll();
        w.SizeChanged += (_, _) => DismissAll();
        w.StateChanged += (_, _) => DismissAll();
        w.Closed += (_, _) => { Hooked.Remove(w); DismissAll(); };
    }
}

/// <summary>弹出框（自绘无边框面板，贴锚点下方，点外面 / Esc 关闭）。对应 Mac 的 .ppPopover。</summary>
public static class Popover
{
    /// <summary>直接弹出：Popover.Show(button, new PopoverContent(…), PopupAlignment.Trailing)。</summary>
    public static PPPopup Show(FrameworkElement anchor, UIElement content, PopupAlignment alignment = PopupAlignment.Leading,
                               bool wantsKey = false, Action? onClose = null) =>
        PopupController.Present(anchor, content, alignment, wantsKey: wantsKey, onClose: onClose);

    /// <summary>
    /// 绑定到 State&lt;bool&gt;（对应 SwiftUI 的 .ppPopover(isPresented:)）：值变 true 时弹出，关闭时写回 false。
    /// 锚点从界面移除时自动关闭。
    /// </summary>
    public static T PPPopover<T>(this T anchor, State<bool> isPresented, Func<UIElement> content, PopupAlignment alignment = PopupAlignment.Leading,
                                 bool wantsKey = false) where T : FrameworkElement
    {
        PPPopup? open = null;
        void Sync()
        {
            if (isPresented.Value && open == null && anchor.IsLoaded)
            {
                open = Show(anchor, content(), alignment, wantsKey, () =>
                {
                    open = null;
                    if (isPresented.Value) isPresented.Value = false;
                });
            }
            else if (!isPresented.Value && open != null)
            {
                var o = open;
                open = null;
                o.Close();
            }
        }
        PropertyChangedEventHandler h = (_, _) => Sync();
        anchor.Loaded += (_, _) => { isPresented.PropertyChanged -= h; isPresented.PropertyChanged += h; Sync(); };
        anchor.Unloaded += (_, _) =>
        {
            isPresented.PropertyChanged -= h;
            if (open != null) { var o = open; open = null; o.Close(); }
        };
        return anchor;
    }
}

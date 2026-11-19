using System;
using System.Linq;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace LemonPaperplane;

/// <summary>
/// 主窗口外壳：自绘标题栏（40px 工具栏就是标题栏，窗口按钮在右侧，Windows 11 样式），下方「侧栏 + 内容岛」，再上面是提示层与对话框层。
/// 只做外壳：工具栏左 / 中 / 右插槽、侧栏、页面内容由功能页设置（ToolbarLeft、ToolbarCenter、ToolbarRight、Sidebar、Page）。
/// 对应 Mac 的 AppDelegate（窗口）+ MainView（布局）。
/// </summary>
public sealed class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }

    private readonly Grid _root;
    private readonly Grid _layout;
    private readonly Decorator _left = new();
    private readonly Decorator _center = new();
    private readonly Decorator _right = new();
    private readonly CaptionButton _min = new(CaptionKind.Minimize);
    private readonly CaptionButton _max = new(CaptionKind.Maximize);
    private readonly CaptionButton _close = new(CaptionKind.Close);
    private readonly ColumnDefinition _sidebarCol;
    private readonly Decorator _sidebar = new();
    private readonly Border _resizer;
    private readonly Border _island;
    private readonly Decorator _page = new();
    private bool _sidebarVisible = true;
    private double _sidebarWidth;

    public DialogHost DialogLayer { get; }
    public ToastHost ToastLayer { get; }

    /// <summary>关闭按钮的行为：返回 true 表示只隐藏（收到托盘，服务继续运行）；没设置时关闭即退出界面。由托盘接上。</summary>
    public Func<bool>? HideOnClose { get; set; }

    public MainWindow()
    {
        Current = this;
        Title = L.T("app.name");
        MinWidth = 760;
        MinHeight = 480;
        Width = 1180;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Bg(ColorToken.BgChrome);
        UseLayoutRounding = true;
        RestoreBounds_();

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = M.ToolbarHeight - SystemParameters.WindowResizeBorderThickness.Top,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
        });

        // 工具栏：左插槽 | 中间（拖动区）| 右插槽；窗口按钮在最上层（对话框遮罩之上）
        // 空间不够时左侧（位置标题）先截断；右侧保持原宽，超出部分在窗口按钮左边裁掉
        foreach (var slot in new[] { _left, _center, _right })
        {
            WindowChrome.SetIsHitTestVisibleInChrome(slot, true);
            slot.VerticalAlignment = VerticalAlignment.Center;
        }
        _left.Margin = new Thickness(M.Md, 0, M.Md, 0);
        _right.Margin = new Thickness(M.Md, 0, M.Md, 0);
        var row = UI.HStack(0, _left.Shrink(), _center.Flex(), _right);
        var toolbar = new Border
        {
            Height = M.ToolbarHeight,
            Child = row,
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 46 * 3, 0), // 给右上角的窗口按钮让位
        };
        var caption = UI.HStack(0, _min, _max, _close);
        caption.Align = CrossAlign.Start;
        caption.VerticalAlignment = VerticalAlignment.Top;
        _min.Action = () => WindowState = WindowState.Minimized;
        _max.Action = ToggleMaximize;
        _close.Action = Close;

        // 侧栏 + 拖动条 + 内容岛
        _sidebarWidth = Clamp(UiPrefs.GetDouble("SidebarWidth") ?? M.SidebarWidth);
        _sidebarCol = new ColumnDefinition { Width = new GridLength(_sidebarWidth) };
        var body = new Grid();
        body.ColumnDefinitions.Add(_sidebarCol);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(M.Sm) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _resizer = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeWE }.Id("sidebar.resizer");
        InstallResizer(_resizer);
        var islandClip = new RectangleGeometry { RadiusX = M.RadiusLG, RadiusY = M.RadiusLG };
        _page.SizeChanged += (_, e) => islandClip.Rect = new Rect(e.NewSize);
        _page.Clip = islandClip;
        _island = new Border
        {
            CornerRadius = new CornerRadius(M.RadiusLG),
            BorderThickness = new Thickness(1),
            Child = _page,
            Margin = new Thickness(0, 0, M.Sm, M.Sm),
            SnapsToDevicePixels = true,
        }.Bg(ColorToken.BgContent).Outline(ColorToken.Border).Id("content.island");
        body.Children.Add(_sidebar.At(0, 0));
        body.Children.Add(_resizer.At(0, 1));
        body.Children.Add(_island.At(0, 2));

        _layout = new Grid();
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(M.ToolbarHeight) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _layout.Children.Add(toolbar.At(0, 0));
        _layout.Children.Add(body.At(1, 0));

        ToastLayer = new ToastHost(ToastStore.Shared);
        DialogLayer = new DialogHost(DialogStore.Shared);
        DialogStore.Shared.ShowMainWindow = ShowMainWindow;
        // 窗口按钮在对话框遮罩之上（对话框打开时也能最小化 / 关闭）
        var captionLayer = new Grid { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        captionLayer.Children.Add(caption);

        _root = new Grid();
        _root.Children.Add(_layout);
        _root.Children.Add(ToastLayer);
        _root.Children.Add(DialogLayer);
        _root.Children.Add(captionLayer);
        _root.Bg(ColorToken.BgChrome);
        Templates.ApplyRootDefaults(_root);
        Content = _root;

        StateChanged += (_, _) => OnStateChanged();
        Activated += (_, _) => SetActiveLook(true);
        Deactivated += (_, _) => SetActiveLook(false);
        PreviewKeyDown += OnPreviewKey;
        Theme.Changed += OnThemeChanged;
        L10n.Shared.Changed += (_, _) => { Title = L.T("app.name"); LocaleChanged?.Invoke(this, EventArgs.Empty); };
        PopupController.HookWindow(this);
        SidebarVisible = UiPrefs.Get("SidebarVisible") != "0";
    }

    /// <summary>语言切换（主窗口内容需要按新文案重建时订阅；PPView 会自动重建）。</summary>
    public event EventHandler? LocaleChanged;

    // MARK: 插槽

    /// <summary>工具栏左侧（侧栏开关、位置标题）。</summary>
    public UIElement? ToolbarLeft { get => _left.Child; set => _left.Child = Detached(value); }

    /// <summary>工具栏中间（拖动区上的内容，一般为空；空白处可以拖动窗口、双击最大化）。</summary>
    public UIElement? ToolbarCenter { get => _center.Child; set => _center.Child = Detached(value); }

    /// <summary>工具栏右侧（搜索、导入、运行中、任务），在窗口按钮左边。</summary>
    public UIElement? ToolbarRight { get => _right.Child; set => _right.Child = Detached(value); }

    /// <summary>左侧栏内容。</summary>
    public UIElement? Sidebar { get => _sidebar.Child; set => _sidebar.Child = Detached(value); }

    /// <summary>内容岛里的页面。</summary>
    public UIElement? Page { get => _page.Child; set => _page.Child = Detached(value); }

    private static UIElement? Detached(UIElement? el)
    {
        if (el != null) UI.Detach(el);
        return el;
    }

    public bool SidebarVisible
    {
        get => _sidebarVisible;
        set
        {
            _sidebarVisible = value;
            _sidebar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _resizer.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _sidebarCol.Width = new GridLength(value ? _sidebarWidth : 0);
            _island.Margin = new Thickness(value ? 0 : M.Sm, 0, M.Sm, M.Sm);
            UiPrefs.Set("SidebarVisible", value ? "1" : "0");
            SidebarVisibleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SidebarVisibleChanged;

    public double SidebarWidth
    {
        get => _sidebarWidth;
        set
        {
            _sidebarWidth = Clamp(value);
            if (_sidebarVisible) _sidebarCol.Width = new GridLength(_sidebarWidth);
        }
    }

    private static double Clamp(double w) => Math.Min(Math.Max(w, M.SidebarMinWidth), M.SidebarMaxWidth);

    private void InstallResizer(Border r)
    {
        double? startX = null, startW = null;
        r.MouseLeftButtonDown += (_, e) =>
        {
            startX = e.GetPosition(this).X;
            startW = _sidebarWidth;
            r.CaptureMouse();
            e.Handled = true;
        };
        r.MouseMove += (_, e) =>
        {
            if (startX == null || !r.IsMouseCaptured) return;
            SidebarWidth = startW!.Value + e.GetPosition(this).X - startX.Value;
        };
        r.MouseLeftButtonUp += (_, _) =>
        {
            if (startX == null) return;
            startX = null;
            r.ReleaseMouseCapture();
            UiPrefs.SetDouble("SidebarWidth", _sidebarWidth);
        };
    }

    // MARK: 窗口

    /// <summary>显示并激活主窗口（托盘、第二次启动、弹对话框时调用）。</summary>
    public void ShowMainWindow()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) Native.SetForegroundWindow(h);
    }

    public void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnStateChanged()
    {
        _max.Restore = WindowState == WindowState.Maximized;
        // 最大化时窗口会超出屏幕一圈边框的宽度：把内容往里收回来
        if (WindowState == WindowState.Maximized)
        {
            int dpi = Native.Dpi(this);
            double scale = dpi / 96.0;
            int fx, fy;
            try
            {
                fx = Native.GetSystemMetricsForDpi(Native.SM_CXSIZEFRAME, (uint)dpi) + Native.GetSystemMetricsForDpi(Native.SM_CXPADDEDBORDER, (uint)dpi);
                fy = Native.GetSystemMetricsForDpi(Native.SM_CYSIZEFRAME, (uint)dpi) + Native.GetSystemMetricsForDpi(Native.SM_CXPADDEDBORDER, (uint)dpi);
            }
            catch (EntryPointNotFoundException)
            {
                fx = Native.GetSystemMetrics(Native.SM_CXSIZEFRAME) + Native.GetSystemMetrics(Native.SM_CXPADDEDBORDER);
                fy = Native.GetSystemMetrics(Native.SM_CYSIZEFRAME) + Native.GetSystemMetrics(Native.SM_CXPADDEDBORDER);
            }
            _root.Margin = new Thickness(fx / scale, fy / scale, fx / scale, fy / scale);
        }
        else
        {
            _root.Margin = new Thickness(0);
        }
    }

    private void SetActiveLook(bool active)
    {
        foreach (var b in new[] { _min, _max, _close })
        {
            b.WindowActive = active;
            b.UpdateVisual();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyDwm();

    /// <summary>Windows 11：圆角、深色边框与系统部分（调整大小时露出的边）跟随主题。</summary>
    private void ApplyDwm()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        Native.SetDwmInt(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
        Native.SetDwmInt(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsDark ? 1 : 0);
        var c = Theme.Color(ColorToken.Border);
        Native.SetDwmInt(h, Native.DWMWA_BORDER_COLOR, c.R | (c.G << 8) | (c.B << 16));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDwm();
        // 在 WindowChrome 之后挂：最大化按钮返回 HTMAXBUTTON（悬停出贴靠布局）
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST:
                if (IsOverMax(lParam))
                {
                    _max.NcHover = true;
                    handled = true;
                    return new IntPtr(Native.HTMAXBUTTON);
                }
                _max.NcHover = false;
                break;
            case Native.WM_NCMOUSEMOVE:
                if (wParam.ToInt32() == Native.HTMAXBUTTON)
                {
                    var t = new Native.TRACKMOUSEEVENT { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.TRACKMOUSEEVENT>(), dwFlags = Native.TME_LEAVE | Native.TME_NONCLIENT, hwndTrack = hwnd };
                    Native.TrackMouseEvent(ref t);
                }
                else
                {
                    _max.NcHover = false;
                    _max.NcPressed = false;
                }
                break;
            case Native.WM_NCMOUSELEAVE:
            case Native.WM_MOUSEMOVE:
                _max.NcHover = false;
                if (msg == Native.WM_NCMOUSELEAVE) _max.NcPressed = false;
                break;
            case Native.WM_NCLBUTTONDOWN:
                PopupController.DismissAll(); // 点标题栏也关弹出层
                if (wParam.ToInt32() == Native.HTMAXBUTTON)
                {
                    _max.NcPressed = true;
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
            case Native.WM_NCLBUTTONDBLCLK:
                if (wParam.ToInt32() == Native.HTMAXBUTTON) { handled = true; return IntPtr.Zero; }
                break;
            case Native.WM_NCRBUTTONDOWN:
                PopupController.DismissAll();
                break;
            case Native.WM_NCLBUTTONUP:
                if (wParam.ToInt32() == Native.HTMAXBUTTON)
                {
                    bool was = _max.NcPressed;
                    _max.NcPressed = false;
                    handled = true;
                    if (was) ToggleMaximize();
                    return IntPtr.Zero;
                }
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMax(IntPtr lParam)
    {
        if (!_max.IsVisible) return false;
        long lp = lParam.ToInt64();
        var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        try
        {
            var p = _max.PointFromScreen(screen);
            return p.X >= 0 && p.Y >= 0 && p.X < _max.ActualWidth && p.Y < _max.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>调试桥用：对窗口坐标（DIP）发 WM_NCHITTEST，返回系统得到的命中码。</summary>
    internal int HitTest(Point windowPoint)
    {
        var h = new WindowInteropHelper(this).Handle;
        var s = PointToScreen(windowPoint);
        int lp = ((int)s.Y << 16) | ((int)s.X & 0xFFFF);
        var r = Native.SendMessage(h, Native.WM_NCHITTEST, IntPtr.Zero, new IntPtr(lp));
        _max.NcHover = false;
        return r.ToInt32();
    }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (PopupController.IsShowingAny) return; // 弹出层自己处理（PopupController 也挂在这个窗口上）
        if (DialogLayer.HandleKey(e)) { e.Handled = true; return; }
        if (DialogStore.Shared.Top != null) return; // 对话框打开时不响应全局快捷键
        if (Shortcuts.Handle(e)) e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        SaveBounds();
        if (HideOnClose?.Invoke() == true)
        {
            e.Cancel = true;
            PopupController.DismissAll();
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        base.OnClosed(e);
        if (Current == this) Current = null;
    }

    // MARK: 窗口位置

    private void SaveBounds()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (b.IsEmpty || b.Width < MinWidth) return;
        UiPrefs.Set("WindowBounds", string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4}", b.Left, b.Top, b.Width, b.Height,
            WindowState == WindowState.Maximized ? 1 : 0));
    }

    /// <summary>窗口标题栏的中点是否落在某块显示器的工作区里（像素换算按主屏 DPI，够用来判断）。</summary>
    private bool IsOnAnyScreen(Rect r)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var x = (int)((r.Left + r.Width / 2) * dpi.DpiScaleX);
        var y = (int)((r.Top + 20) * dpi.DpiScaleY);
        return System.Windows.Forms.Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(x, y));
    }

    private void RestoreBounds_()
    {
        var s = UiPrefs.Get("WindowBounds");
        if (s == null || Environment.GetEnvironmentVariable("PAPERPLANE_DEBUG_BRIDGE") != null) return;
        var parts = s.Split(',');
        if (parts.Length < 5) return;
        double P(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
        try
        {
            var r = new Rect(P(0), P(1), P(2), P(3));
            // 显示器拔掉、分辨率变了时上次的位置可能大半在屏幕外：整个窗口拉回屏幕内（放不下就缩小），标题栏一定可见
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!vs.IntersectsWith(r)) return;
            var work = SystemParameters.WorkArea;
            var inside = Rect.Intersect(vs, r);
            if (inside.Width < r.Width - 1 || inside.Height < r.Height - 1)
            {
                var w = Math.Min(r.Width, work.Width);
                var h = Math.Min(r.Height, work.Height);
                r = new Rect(Math.Max(vs.Left, Math.Min(r.Left, vs.Right - w)), Math.Max(vs.Top, Math.Min(r.Top, vs.Bottom - h)), w, h);
                // 跨显示器的空洞里（虚拟屏幕内但不在任何显示器上）退回主屏工作区
                if (!work.IntersectsWith(r) && !IsOnAnyScreen(r)) r = new Rect(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);
            }
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = r.Left;
            Top = r.Top;
            Width = r.Width;
            Height = r.Height;
            if (parts[4] == "1") WindowState = WindowState.Maximized;
        }
        catch (FormatException)
        {
            // 忽略坏数据
        }
    }
}

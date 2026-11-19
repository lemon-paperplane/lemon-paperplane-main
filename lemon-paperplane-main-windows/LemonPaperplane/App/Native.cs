using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LemonPaperplane;

/// <summary>Win32 / DWM 调用（窗口外壳、弹出层定位、单实例激活）。</summary>
internal static class Native
{
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCMOUSEMOVE = 0x00A0;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_NCLBUTTONUP = 0x00A2;
    public const int WM_NCLBUTTONDBLCLK = 0x00A3;
    public const int WM_NCRBUTTONDOWN = 0x00A4;
    public const int WM_NCMOUSELEAVE = 0x02A2;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DPICHANGED = 0x02E0;
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTMAXBUTTON = 9;

    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CYSIZEFRAME = 33;
    public const int SM_CXPADDEDBORDER = 92;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    public const uint TME_LEAVE = 0x00000002;
    public const uint TME_NONCLIENT = 0x00000010;

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int handle);

    public const int SW_RESTORE = 9;
    public const int ASFW_ANY = -1;

    public static void SetDwmInt(IntPtr hwnd, int attr, int value)
    {
        try { DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)); }
        catch
        {
            // Windows 10 上部分属性不存在，忽略
        }
    }

    /// <summary>某个屏幕点（设备像素）所在显示器的工作区（设备像素）。</summary>
    public static Rect WorkAreaPx(Point devicePoint)
    {
        var mon = MonitorFromPoint(new POINT { X = (int)devicePoint.X, Y = (int)devicePoint.Y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            return new Rect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
        return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    /// <summary>元素在屏幕上的矩形（DIP，按元素所在窗口的 DPI 换算）与该屏幕工作区（DIP）。</summary>
    public static (Rect Element, Rect Work)? ScreenRects(Visual v)
    {
        var src = PresentationSource.FromVisual(v);
        if (src?.CompositionTarget == null) return null;
        var fromDevice = src.CompositionTarget.TransformFromDevice;
        var fe = v as FrameworkElement;
        var tl = v.PointToScreen(new Point(0, 0));
        var br = v.PointToScreen(new Point(fe?.ActualWidth ?? 0, fe?.ActualHeight ?? 0));
        var work = WorkAreaPx(tl);
        var e = new Rect(fromDevice.Transform(tl), fromDevice.Transform(br));
        var w = new Rect(fromDevice.Transform(work.TopLeft), fromDevice.Transform(work.BottomRight));
        return (e, w);
    }

    public static int Dpi(Visual v) => (int)Math.Round(VisualTreeHelper.GetDpi(v).PixelsPerInchX);

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;
}

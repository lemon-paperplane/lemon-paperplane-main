using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PaperplaneKit;
using static LemonPaperplane.UI;
using Forms = System.Windows.Forms;
using RunModel = PaperplaneKit.Run;

namespace LemonPaperplane;

/// <summary>
/// 托盘图标（对应 Mac 菜单栏图标）：单击 / 右键弹出运行列表浮窗，双击打开主窗口。
/// 关闭主窗口只是收到托盘，服务继续在后台运行。
/// </summary>
public sealed class Tray : IDisposable
{
    public static readonly Tray Shared = new();

    private Forms.NotifyIcon? _icon;
    private TrayFlyout? _flyout;
    private DateTime _flyoutClosedAt;

    private Tray() { }

    public void Install()
    {
        if (_icon != null) return;
        _icon = new Forms.NotifyIcon { Text = L.T("app.name"), Visible = true };
        UpdateIcon();
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right) Toggle();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            _flyout?.Hide();
            MainWindow.Current?.ShowMainWindow();
        };
        Theme.SystemThemeChanged += (_, _) => UpdateIcon();
        // 调试桥：{"cmd":"tray","x":…,"y":…} 在给定屏幕像素位置（默认鼠标位置）弹出托盘浮窗（ssh 里没法点托盘图标）
        DebugBridge.Extra["tray"] = r =>
        {
            _flyout ??= new TrayFlyout(() => _flyoutClosedAt = DateTime.Now);
            var x = (int?)r["x"];
            var y = (int?)r["y"];
            _flyout.ShowNearCursor(x.HasValue && y.HasValue ? new System.Drawing.Point(x.Value, y.Value) : null);
            return System.Threading.Tasks.Task.FromResult(new Newtonsoft.Json.Linq.JObject { ["visible"] = _flyout.IsVisible });
        };
        L10n.Shared.Changed += (_, _) => { if (_icon != null) _icon.Text = L.T("app.name"); };
    }

    /// <summary>任务栏深浅变了换图标颜色；按主屏 DPI 取像素尺寸。</summary>
    private void UpdateIcon()
    {
        if (_icon == null) return;
        var scale = 1.0;
        try
        {
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            scale = g.DpiX / 96.0;
        }
        catch { }
        var old = _icon.Icon;
        _icon.Icon = IconStore.Shared.TrayIcon((int)Math.Round(16 * scale), IconStore.TrayColor);
        old?.Dispose();
    }

    private void Toggle()
    {
        // 浮窗失去焦点时已经关了；同一次点击里又收到 MouseUp 不要再打开
        if (_flyout is { IsVisible: true })
        {
            _flyout.Hide();
            return;
        }
        if ((DateTime.Now - _flyoutClosedAt).TotalMilliseconds < 250) return;
        _flyout ??= new TrayFlyout(() => _flyoutClosedAt = DateTime.Now);
        _flyout.ShowNearCursor();
    }

    public void HideFlyout() => _flyout?.Hide();

    public void Dispose()
    {
        if (_icon != null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
        _flyout?.Close();
        _flyout = null;
    }
}

/// <summary>托盘浮窗：无边框置顶窗口，失去焦点即隐藏。</summary>
internal sealed class TrayFlyout : Window
{
    private readonly Action _onHidden;

    public TrayFlyout(Action onHidden)
    {
        _onHidden = onHidden;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = true;
        var card = Box(new TrayContent()).Rounded(M.RadiusLG).Bg(ColorToken.BgElevated).Outline(ColorToken.Border);
        card.ClipToBounds = true;
        var shadow = Box().Rounded(M.RadiusLG).Bg(ColorToken.BgElevated).Shadow(ShadowToken.Popup);
        Content = ZStack(shadow, card).Margins(12);
        Deactivated += (_, _) => Hide();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _onHidden(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };
    }

    /// <summary>贴着鼠标（托盘图标）显示，整个浮窗落在鼠标所在屏幕的工作区内。</summary>
    public void ShowNearCursor(System.Drawing.Point? at = null)
    {
        var cursor = at ?? Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = ActualWidth * dpi.DpiScaleX, h = ActualHeight * dpi.DpiScaleY;
        // 任务栏在下方时浮窗在鼠标上方；在上方时在下方；左右同理
        var x = Math.Min(Math.Max(cursor.X - w / 2, area.Left), area.Right - w);
        var y = cursor.Y > area.Bottom - 4 || cursor.Y > area.Top + area.Height / 2 ? area.Bottom - h : area.Top;
        if (cursor.X > area.Right) x = area.Right - w;
        if (cursor.X < area.Left) x = area.Left;
        Left = x / dpi.DpiScaleX;
        Top = y / dpi.DpiScaleY;
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Native.SetForegroundWindow(hwnd);
    }
}

/// <summary>托盘浮窗内容：按工程列出运行中的服务（状态、地址、内存、停止），全部停止，打开主窗口，退出（对应 Mac MenuBarContent）。</summary>
public sealed class TrayContent : PPView
{
    private readonly AppModel _model = AppModel.Shared;

    public TrayContent()
    {
        Observe(_model, nameof(AppModel.Connection));
        Observe(_model.Runs, nameof(RunStore.Runs));
        Observe(_model.Data, nameof(DataStore.Projects), nameof(DataStore.Workspaces));
    }

    protected override UIElement Build()
    {
        var active = _model.Runs.ActiveRuns;
        var groups = active.GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.ToList());
        var order = _model.Data.AllProjects.Select(p => p.Id).Where(groups.ContainsKey)
            .Concat(groups.Keys.Where(k => _model.Data.Project(k) == null)).ToList();

        var col = VStack(0);
        col.Align = CrossAlign.Stretch;
        col.Children.Add(HStack(M.Sm, Text(L.T("app.name"), Typo.Title), Spacer(), ConnectionBadge()).Pad(M.Lg, 0).H(40));
        col.Children.Add(new PPDivider(subtle: true));
        if (active.Count == 0)
        {
            col.Children.Add(HStack(M.Sm, new PPIcon("activity", M.Icon, ColorToken.IconSecondary),
                Text(L.T("runs.none"), Typo.Body, ColorToken.TextSecondary)).Pad(M.Lg));
        }
        else
        {
            var list = VStack(M.Xs);
            list.Align = CrossAlign.Stretch;
            foreach (var pid in order)
            {
                list.Children.Add(Text(_model.Data.Project(pid)?.Name ?? pid, Typo.Caption, ColorToken.TextTertiary).Margins(M.Md, M.Sm, M.Md, 0));
                foreach (var r in groups[pid]) list.Children.Add(TrayRunRow(r));
            }
            col.Children.Add(Scroll(list.Pad(M.Xs)).Do(s => s.MaxHeight = 420));
        }
        col.Children.Add(new PPDivider(subtle: true));
        var actions = VStack(0);
        actions.Align = CrossAlign.Stretch;
        if (active.Count > 0) actions.Children.Add(TrayAction(L.T("menu.stopAll"), "square", Actions.StopAll));
        actions.Children.Add(TrayAction(L.T("menubar.openWindow"), "app-window", () =>
        {
            Tray.Shared.HideFlyout();
            MainWindow.Current?.ShowMainWindow();
        }).Id("tray.open"));
        actions.Children.Add(new PPDivider(subtle: true).Margins(0, M.Xs, 0, M.Xs));
        actions.Children.Add(TrayAction(L.T("menu.quitUI"), "log-out", () => _model.Quit(false)).Id("tray.quitUI"));
        actions.Children.Add(TrayAction(L.T("menu.quitAll"), "power", () => _model.Quit(true), destructive: true).Id("tray.quitAll"));
        col.Children.Add(actions.Pad(M.Xs));
        return col.W(340);
    }

    private UIElement ConnectionBadge() => _model.Connection.Kind switch
    {
        "connected" => Gap(0),
        "failed" => new PPBadge(L.T("connection.failed"), tone: "danger"),
        _ => new PPSpinner(M.IconSmall),
    };

    private static UIElement TrayRunRow(RunModel run)
    {
        UIElement address = run.Urls.FirstOrDefault() is { } u
            ? new PPLink(Format.ShortURL(u), () => Actions.OpenURL(u), mono: true)
            : Text(RunStatusStyle.Title(run.State), Typo.Small, ColorToken.TextTertiary);
        var content = HStack(M.Sm,
            new StatusDot(run.State),
            VStack(1, Text(run.Name, Typo.Body), address).Flex(),
            new RunResourceText(run.Id, showCPU: false, fallbackCpu: run.Cpu, fallbackRss: run.RssMB),
            new PPIconButton("square", L.T("action.stop"), () => Actions.Stop(run.Id), size: M.IconButtonSmall, tint: ColorToken.Danger))
            .Margins(0, M.Xs, 0, M.Xs);
        return new PPRow(content, action: () =>
        {
            Tray.Shared.HideFlyout();
            MainWindow.Current?.ShowMainWindow();
            AppModel.Shared.Nav.OpenRunConsole(run.ProjectId, run.ConfigId);
        }, height: 0);
    }

    private static PPRow TrayAction(string title, string icon, Action action, bool destructive = false) =>
        new(HStack(M.Md,
            new PPIcon(icon, M.Icon, destructive ? ColorToken.Danger : ColorToken.IconPrimary),
            Text(title, Typo.Body, destructive ? ColorToken.Danger : ColorToken.TextPrimary), Spacer()), action: action);
}

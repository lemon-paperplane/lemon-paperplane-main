using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

// WebView2 的公共部分：编辑窗口、终端页、代码输入框、插件网页视图共用。
//
// 轻量（docs/editor.md 第 2 节）：平时不创建任何 WebView2；用到时才创建，离开（Unloaded 一小会儿后）或关闭即 Dispose，
// 全部关掉后 WebView2 的浏览器进程自己退出。所有网页共用一个用户数据目录 %LOCALAPPDATA%\LemonPaperplane\WebView2，
// 每个网页用 InPrivate 会话（对应 Mac 每个 WKWebView 独占的 nonPersistent 存储：只在内存里，关掉就没了）。
//
// 空域（airspace）：WPF 的 WebView2 是 HwndHost，子窗口总是盖在同一窗口里的 WPF 内容上面。
//   - 主窗口里的对话框（窗口内覆盖层）打开时：先截一张网页的图垫在原处，再把 WebView2 藏起来，遮罩照常压暗；
//   - 右下角的提示：在 WebView2 的窗口区域上挖掉提示所在的矩形（SetWindowRgn），提示照常显示在上面；
//   - 圆角容器（内容岛底角、代码输入框）：窗口区域做成圆角；
//   - 菜单、下拉、悬停提示是独立的弹出窗口，本来就在上面。

/// <summary>WebView2 运行时与共用的浏览器环境。</summary>
public static class WebView2Env
{
    /// <summary>没装运行时（老的 Windows 10）时给用户的下载说明页。</summary>
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LemonPaperplane", "WebView2");

    /// <summary>已安装的运行时版本；没装时为 null（每次现查，用户装完不用重启 App）。</summary>
    public static string? RuntimeVersion
    {
        get
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return null; }
        }
    }

    public static bool IsAvailable => RuntimeVersion != null;

    private static Task<CoreWebView2Environment>? _env;

    /// <summary>共用的环境（第一次用到时创建；浏览器进程退出后下次重新创建）。</summary>
    public static Task<CoreWebView2Environment> GetAsync()
    {
        if (_env == null || _env.IsFaulted || _env.IsCanceled) _env = CreateAsync();
        return _env;
    }

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(UserDataFolder);
        // 网页自带的右键菜单（剪切 / 复制 / 粘贴）跟 App 的语言
        var options = new CoreWebView2EnvironmentOptions { Language = L10n.Shared.Locale == "zh-Hans" ? "zh-CN" : "en-US" };
        var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, options);
        env.BrowserProcessExited += (_, _) =>
        {
            var cur = _env;
            if (cur != null && cur.Status == TaskStatus.RanToCompletion && ReferenceEquals(cur.Result, env)) _env = null;
        };
        return env;
    }

    /// <summary>System.Drawing.Color（WebView2 的底色）。</summary>
    public static System.Drawing.Color DrawingColor(string token)
    {
        var c = Theme.Color(token);
        return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    /// <summary>JS 字面量（JSON 编码；字符串、数字、数组、null 都可以）。</summary>
    public static string Js(object? v) => v == null ? "null" : Newtonsoft.Json.JsonConvert.SerializeObject(v);
}

/// <summary>
/// 一个 WebView2 网页（按需创建、离开即释放），带加载失败 / 进程退出 / 没装运行时的空状态和空域处理。
/// 网页 → 宿主的消息（chrome.webview.postMessage）只收来自内核同源页面的，{type:"ready"} 自己处理，其余经 Message 交给使用方。
/// </summary>
public sealed class PPWebView : Grid
{
    private static readonly List<WeakReference<PPWebView>> LiveList = new();

    private readonly Func<string?> _url;
    private readonly string _background;
    private readonly Image _snapshot = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _overlay = new() { Visibility = Visibility.Collapsed };
    private WebView2? _wv;
    private Uri? _origin;
    private bool _loaded;
    private bool _ready;
    private bool _revealed;
    private bool _failed;
    private bool _covered;
    private bool _frozen;
    private bool _disposed;
    private bool _hasRegion;
    private int _freezeSeq;
    private DispatcherTimer? _releaseTimer;
    private DispatcherTimer? _revealTimer;
    private DispatcherTimer? _regionTimer;
    private BitmapSource? _lastShot;
    private ScrollViewer? _scrollParent;

    /// <summary>调试桥里用的名字（terminal / snippet / editor:&lt;工程&gt; / webview）。</summary>
    public string DebugName { get; }

    /// <summary>网页发 ready 之前不显示（避免白屏和半成品界面）。不发 ready 的网页另设 ReadyOnNavigation。</summary>
    public bool HideUntilReady { get; set; }

    /// <summary>网页不发 ready（插件页面）：导航完成就算就绪。</summary>
    public bool ReadyOnNavigation { get; set; }

    /// <summary>只允许内核同源的页面；别的链接交给系统浏览器（编辑器、终端、代码输入框）。</summary>
    public bool RestrictToOrigin { get; set; } = true;

    /// <summary>
    /// 窗口区域四个角的圆角（设备无关像素，代码输入框这类自己是圆角框的）。0 时自动：哪个角正好落在主窗口内容岛的圆角上，
    /// 就按内容岛的圆角半径裁那个角（终端、插件网页贴着内容岛底边）。
    /// </summary>
    public double CornerRadius { get; set; }

    /// <summary>
    /// 焦点在这个网页里时，主窗口的 Ctrl 快捷键不拦截，按键全部交给网页（终端：和 Mac 一样 Ctrl 组合键都给 shell）。
    /// </summary>
    public bool KeysToPage { get; set; }

    /// <summary>空状态的图标与文案。</summary>
    public string ErrorIcon { get; set; } = "globe";
    public string LoadFailedText { get; set; } = L.T("webview.loadFailed");
    public string CrashedText { get; set; } = L.T("webview.crashed");
    public string ReloadText { get; set; } = L.T("webview.reload");

    /// <summary>导航前的额外设置（注入脚本等），在 CoreWebView2 初始化后、打开地址前调用。</summary>
    public Func<CoreWebView2, Task>? Configure { get; set; }

    /// <summary>网页 → 宿主的消息（JSON 对象，ready 也会转发）。</summary>
    public event Action<JObject>? Message;

    /// <summary>网页就绪（每次加载一次）。</summary>
    public event Action? Ready;

    /// <summary>网页进程或浏览器进程退出（网页里的状态都没了）。</summary>
    public event Action? Crashed;

    /// <summary>App 明暗变化（参数：是否暗色）。底色已经自动换好，网页里的主题由使用方通知。</summary>
    public event Action<bool>? ThemeChanged;

    /// <summary>App 语言变化。</summary>
    public event Action? LocaleChanged;

    static PPWebView()
    {
        // 焦点在终端这类网页里时，主窗口的 Ctrl 快捷键让给网页
        Shortcuts.Bypass = OwnsKey;
    }

    public PPWebView(string debugName, Func<string?> url, string background)
    {
        DebugName = debugName;
        _url = url;
        _background = background;
        ClipToBounds = true;
        Children.Add(_snapshot);
        Children.Add(_overlay);
        _overlay.Bg(background);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool IsReady => _ready;
    public WebView2? Control => _wv;
    public CoreWebView2? Core => _wv?.CoreWebView2;

    /// <summary>被同一位置的 WPF 内容盖住（终端页没有终端时的空状态）：藏起网页但不释放。</summary>
    public bool Covered
    {
        get => _covered;
        set
        {
            if (_covered == value) return;
            _covered = value;
            ApplyVisibility();
        }
    }

    // MARK: 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _releaseTimer?.Stop();
        if (_loaded) return;
        _loaded = true;
        DialogStore.Shared.Changed += OnDialogsChanged;
        ToastStore.Shared.Added += OnToastAdded;
        ToastStore.Shared.Removed += OnToastRemoved;
        Theme.Changed += OnThemeChanged;
        L10n.Shared.Changed += OnLocaleChanged;
        _scrollParent = FindAncestor<ScrollViewer>(this);
        if (_scrollParent != null) _scrollParent.ScrollChanged += OnScrollChanged;
        if (_wv == null && !_disposed && !_failed) Start();
        UpdateFreeze();
        UpdateRegionTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _loaded = false;
        DialogStore.Shared.Changed -= OnDialogsChanged;
        ToastStore.Shared.Added -= OnToastAdded;
        ToastStore.Shared.Removed -= OnToastRemoved;
        Theme.Changed -= OnThemeChanged;
        L10n.Shared.Changed -= OnLocaleChanged;
        if (_scrollParent != null) _scrollParent.ScrollChanged -= OnScrollChanged;
        _scrollParent = null;
        _regionTimer?.Stop();
        // 父视图重建时会先摘下再挂回：等一会儿还没回来才真正释放
        _releaseTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _releaseTimer!.Stop();
            if (!_loaded) Release();
        }, Dispatcher);
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    private async void Start()
    {
        _failed = false;
        _ready = false;
        _revealed = false;
        HideOverlay();
        if (!WebView2Env.IsAvailable)
        {
            ShowMissingRuntime();
            return;
        }
        var url = _url();
        if (url == null)
        {
            ShowError(L.T("connection.notConnected"), retry: true);
            return;
        }
        var wv = new WebView2 { DefaultBackgroundColor = WebView2Env.DrawingColor(_background), Focusable = true };
        _wv = wv;
        Children.Insert(1, wv);
        ApplyVisibility();
        Register(this);
        try
        {
            var env = await WebView2Env.GetAsync();
            if (_wv != wv) return;
            var options = env.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await wv.EnsureCoreWebView2Async(env, options);
            if (_wv != wv) return;
            var core = wv.CoreWebView2;
            Setup(wv, core);
            if (Configure != null) await Configure(core);
            if (_wv != wv) return;
            _origin = new Uri(url);
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            DebugBridge.LogError(ex);
            if (_wv == wv) ShowError(LoadFailedText + "\n" + ex.Message, retry: true);
        }
    }

    private void Setup(WebView2 wv, CoreWebView2 core)
    {
        var s = core.Settings;
        s.IsWebMessageEnabled = true;
        s.IsScriptEnabled = true;
        s.AreDefaultScriptDialogsEnabled = false;
        s.AreDefaultContextMenusEnabled = true; // 只留编辑项，见 ContextMenuRequested
        s.AreBrowserAcceleratorKeysEnabled = false; // F5、Ctrl+P、Ctrl+F 等交给网页
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsBuiltInErrorPageEnabled = false;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif
        core.WebMessageReceived += (_, e) => OnWebMessage(e);
        core.NavigationStarting += (_, e) =>
        {
            if (!RestrictToOrigin || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || SameOrigin(e.Uri)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true; // 不开新的网页窗口：http/https/mailto 交给系统浏览器
            OpenExternal(e.Uri);
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (_wv != wv) return;
            if (!e.IsSuccess)
            {
                if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                ShowError(LoadFailedText + "\n" + e.WebErrorStatus, retry: true);
                return;
            }
            if (e.HttpStatusCode >= 400)
            {
                ShowError(LoadFailedText + " (HTTP " + e.HttpStatusCode + ")", retry: true);
                return;
            }
            if (ReadyOnNavigation)
            {
                BecameReady();
                return;
            }
            // 网页没有发 ready（旧版本或出错）时，几秒后也显示出来
            _revealTimer?.Stop();
            _revealTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) =>
            {
                _revealTimer?.Stop();
                if (_ready || _wv != wv) return;
                _revealed = true;
                ApplyVisibility();
            }, Dispatcher);
            _revealTimer.Start();
        };
        core.ProcessFailed += (_, e) =>
        {
            if (_wv != wv) return;
            switch (e.ProcessFailedKind)
            {
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                    _ready = false;
                    ShowError(CrashedText, retry: true);
                    Crashed?.Invoke();
                    break;
            }
        };
        core.ScriptDialogOpening += OnScriptDialog;
        core.ContextMenuRequested += (_, e) =>
        {
            // 浏览器自带的右键菜单只留编辑项（剪切、复制、粘贴、全选…），去掉后退 / 刷新 / 另存为 / 检查等
            var keep = new HashSet<string> { "cut", "copy", "paste", "pasteAndMatchStyle", "selectAll", "undo", "redo", "emoji" };
            var items = e.MenuItems;
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i].Kind != CoreWebView2ContextMenuItemKind.Separator && !keep.Contains(items[i].Name)) items.RemoveAt(i);
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i].Kind == CoreWebView2ContextMenuItemKind.Separator
                    && (i == 0 || i == items.Count - 1 || items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator))
                    items.RemoveAt(i);
            if (items.Count == 0) e.Handled = true;
        };
        wv.SizeChanged += (_, _) => UpdateRegion();
        wv.GotFocus += (_, _) => PopupController.DismissAll(); // 点进网页（另一个窗口句柄）时收起菜单
    }

    /// <summary>立即释放 WebView2（窗口关闭、视图离开时）。之后再显示会重新创建。</summary>
    public void Release()
    {
        _releaseTimer?.Stop();
        _revealTimer?.Stop();
        _regionTimer?.Stop();
        var wv = _wv;
        _wv = null;
        _ready = false;
        _revealed = false;
        _hasRegion = false;
        _lastShot = null;
        _snapshot.Source = null;
        Unregister(this);
        if (wv == null) return;
        try { wv.CoreWebView2?.Stop(); } catch { /* 已经坏了 */ }
        Children.Remove(wv);
        try { wv.Dispose(); } catch (Exception e) { DebugBridge.LogError(e); }
    }

    /// <summary>彻底释放，不再自动重建（编辑窗口关闭）。</summary>
    public void Dispose()
    {
        _disposed = true;
        Release();
    }

    /// <summary>重新加载（释放后重建，崩溃后也能恢复）。</summary>
    public void Reload()
    {
        Release();
        _failed = false;
        HideOverlay();
        if (!_disposed) Start();
    }

    // MARK: 网页通信

    private void OnWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!SameOrigin(e.Source)) return;
        JObject? m;
        try { m = JToken.Parse(e.WebMessageAsJson) as JObject; }
        catch { return; }
        if (m == null) return;
        if ((string?)m["type"] == "ready") BecameReady();
        Message?.Invoke(m);
    }

    /// <summary>执行 JS，返回结果的 JSON 文本；没就绪或出错返回 null。</summary>
    public async Task<string?> EvalAsync(string js)
    {
        var wv = _wv;
        if (wv?.CoreWebView2 == null) return null;
        try { return await wv.ExecuteScriptAsync(js); }
        catch (Exception e)
        {
            DebugBridge.LogError(e);
            return null;
        }
    }

    /// <summary>执行 JS，不关心结果。</summary>
    public void Exec(string js) => _ = EvalAsync(js);

    /// <summary>宿主 → 网页的消息（chrome.webview 的 message 事件）。</summary>
    public void PostJson(JToken message)
    {
        try { _wv?.CoreWebView2?.PostWebMessageAsJson(message.ToString(Newtonsoft.Json.Formatting.None)); }
        catch (Exception e) { DebugBridge.LogError(e); }
    }

    /// <summary>把键盘焦点交给网页。</summary>
    public void FocusPage()
    {
        if (_wv == null || _wv.Visibility != Visibility.Visible) return;
        _wv.Focus();
    }

    /// <summary>网页当前画面（PNG → 图像）；没有网页时为 null。</summary>
    public async Task<BitmapSource?> CaptureAsync()
    {
        var core = _wv?.CoreWebView2;
        if (core == null) return null;
        try
        {
            var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e)
        {
            DebugBridge.LogError(e);
            return null;
        }
    }

    private bool SameOrigin(string? uri)
    {
        if (_origin == null || uri == null || !Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        return u.Scheme == _origin.Scheme && string.Equals(u.Host, _origin.Host, StringComparison.OrdinalIgnoreCase) && u.Port == _origin.Port;
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "mailto"))
            Actions.OpenURL(u.AbsoluteUri);
    }

    private void BecameReady()
    {
        if (_ready) return;
        _ready = true;
        _revealTimer?.Stop();
        HideOverlay();
        ApplyVisibility();
        Ready?.Invoke();
        UpdateRegion();
        // 对话框淡入时有缩放：等动画走完再按最终位置摆一次子窗口
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _wv?.InvalidateArrange()));
    }

    /// <summary>alert / confirm 兜底：系统消息框（编辑器自己的对话框是网页画的，正常用不到）。</summary>
    private void OnScriptDialog(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var owner = Window.GetWindow(this);
                switch (e.Kind)
                {
                    case CoreWebView2ScriptDialogKind.Alert:
                        if (owner != null) MessageBox.Show(owner, e.Message, L.T("app.name"), MessageBoxButton.OK);
                        else MessageBox.Show(e.Message, L.T("app.name"), MessageBoxButton.OK);
                        e.Accept();
                        break;
                    case CoreWebView2ScriptDialogKind.Confirm:
                    {
                        var r = owner != null
                            ? MessageBox.Show(owner, e.Message, L.T("app.name"), MessageBoxButton.OKCancel)
                            : MessageBox.Show(e.Message, L.T("app.name"), MessageBoxButton.OKCancel);
                        if (r == MessageBoxResult.OK) e.Accept();
                        break;
                    }
                    default:
                        e.Accept();
                        break;
                }
            }
            finally { deferral.Complete(); }
        }));
    }

    // MARK: 显示状态

    private void ApplyVisibility()
    {
        var wv = _wv;
        bool show = wv != null && !_failed && !_covered && !_frozen && (_ready || _revealed || !HideUntilReady);
        if (wv != null) wv.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        _snapshot.Visibility = _frozen && !_covered && !_failed && _snapshot.Source != null ? Visibility.Visible : Visibility.Collapsed;
        if (show) UpdateRegion();
    }

    private void ShowError(string message, bool retry)
    {
        _failed = true;
        _ready = false;
        _overlay.Child = new PPEmptyState(ErrorIcon, message, retry ? ReloadText : null, retry ? Reload : null, retry ? "refresh-cw" : null);
        _overlay.Visibility = Visibility.Visible;
        ApplyVisibility();
    }

    private void ShowMissingRuntime()
    {
        _failed = true;
        _overlay.Child = new PPEmptyState(ErrorIcon, L.T("webview.missing"), L.T("webview.download"),
            () => Actions.OpenURL(WebView2Env.DownloadUrl), "external-link");
        _overlay.Visibility = Visibility.Visible;
    }

    private void HideOverlay()
    {
        _overlay.Child = null;
        _overlay.Visibility = Visibility.Collapsed;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_wv != null) _wv.DefaultBackgroundColor = WebView2Env.DrawingColor(_background);
        if (_ready) ThemeChanged?.Invoke(Theme.IsDark);
        // 被对话框盖着时垫的是旧主题的截图：撤掉（底下是同色的页面背景），对话框关掉后网页本身已是新主题
        if (_frozen)
        {
            _snapshot.Source = null;
            ApplyVisibility();
        }
    }

    private void OnLocaleChanged(object? sender, EventArgs e)
    {
        LoadFailedText = L.T("webview.loadFailed");
        CrashedText = L.T("webview.crashed");
        ReloadText = L.T("webview.reload");
        if (_ready) LocaleChanged?.Invoke();
    }

    // MARK: 空域：对话框

    private void OnDialogsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(UpdateFreeze));

    /// <summary>主窗口里有对话框盖在这个网页上（网页不在这个对话框里）：截图垫底、藏起子窗口。</summary>
    private async void UpdateFreeze()
    {
        var top = DialogStore.Shared.Top;
        bool should = _loaded && top != null && Window.GetWindow(this) is MainWindow && !IsDescendantOf(this, top.View);
        if (should == _frozen) return;
        int seq = ++_freezeSeq;
        if (should)
        {
            var shot = _wv != null && _wv.Visibility == Visibility.Visible ? await CaptureAsync() : null;
            if (seq != _freezeSeq) return;
            _snapshot.Source = shot;
            _frozen = true;
        }
        else
        {
            _frozen = false;
            _snapshot.Source = null;
        }
        ApplyVisibility();
    }

    private static bool IsDescendantOf(DependencyObject d, DependencyObject ancestor)
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    // MARK: 空域：提示与圆角（窗口区域）

    private void OnToastAdded(ToastStore.Toast t) => UpdateRegionTimer();
    private void OnToastRemoved(Guid id) => UpdateRegionTimer();

    /// <summary>有提示时每 200ms 按提示的位置更新一次窗口区域（提示滑入、堆叠、窗口缩放都跟得上）。</summary>
    private void UpdateRegionTimer()
    {
        bool need = _loaded && ToastStore.Shared.Toasts.Count > 0 && Window.GetWindow(this) is MainWindow;
        if (need)
        {
            _regionTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => UpdateRegion(), Dispatcher);
            if (!_regionTimer.IsEnabled) _regionTimer.Start();
        }
        else
        {
            _regionTimer?.Stop();
        }
        UpdateRegion();
        // 提示淡出（160ms）后再算一次
        Dispatcher.BeginInvoke(new Action(async () => { await Task.Delay(260); UpdateRegion(); }));
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateRegion();

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    /// <summary>所在 ScrollViewer 的可视区（网页坐标）；网页整个可见时为 null。对话框内容滚动时子窗口不越出去。</summary>
    private Rect? ScrollClip(FrameworkElement wv)
    {
        var sv = _scrollParent;
        if (sv == null || sv.ActualWidth <= 0) return null;
        try
        {
            var vis = sv.TransformToVisual(wv).TransformBounds(new Rect(0, 0, sv.ActualWidth, sv.ActualHeight));
            var bounds = new Rect(0, 0, wv.ActualWidth, wv.ActualHeight);
            if (vis.Contains(bounds)) return null;
            vis.Intersect(bounds);
            return vis.IsEmpty ? new Rect(0, 0, 0, 0) : vis;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>窗口区域 = 圆角矩形 ∩ 所在滚动区的可视区 − 提示所在的矩形（设备像素）。</summary>
    private void UpdateRegion()
    {
        var wv = _wv;
        if (wv == null || wv.Handle == IntPtr.Zero || wv.Visibility != Visibility.Visible) return;
        var src = PresentationSource.FromVisual(wv);
        if (src?.CompositionTarget == null) return;
        var m = src.CompositionTarget.TransformToDevice;
        double sx = m.M11, sy = m.M22;
        int w = (int)Math.Ceiling(wv.ActualWidth * sx), h = (int)Math.Ceiling(wv.ActualHeight * sy);
        if (w <= 0 || h <= 0) return;
        var holes = ToastRects(wv);
        var clip = ScrollClip(wv);
        var (corners, radius) = Corners(wv);
        if (corners == 0 && holes.Count == 0 && clip == null)
        {
            if (_hasRegion)
            {
                WebNative.SetWindowRgn(wv.Handle, IntPtr.Zero, true);
                _hasRegion = false;
            }
            return;
        }
        var rgn = WebNative.CreateRectRgn(0, 0, w, h);
        if (corners != 0)
        {
            int d = (int)Math.Round(radius * 2 * sx);
            // 每个要圆的角：去掉角上的 r×r 方块，再补回同一位置的 1/4 圆
            for (int i = 0; i < 4; i++)
            {
                if ((corners & (1 << i)) == 0) continue;
                int x0 = i is 1 or 2 ? w - d / 2 : 0, y0 = i is 2 or 3 ? h - d / 2 : 0;
                var sq = WebNative.CreateRectRgn(x0, y0, x0 + d / 2, y0 + d / 2);
                WebNative.CombineRgn(rgn, rgn, sq, WebNative.RGN_DIFF);
                WebNative.DeleteObject(sq);
                int ex = i is 1 or 2 ? w - d : 0, ey = i is 2 or 3 ? h - d : 0;
                var ell = WebNative.CreateEllipticRgn(ex, ey, ex + d + 1, ey + d + 1);
                var quarter = WebNative.CreateRectRgn(x0, y0, x0 + d / 2, y0 + d / 2);
                WebNative.CombineRgn(ell, ell, quarter, WebNative.RGN_AND);
                WebNative.CombineRgn(rgn, rgn, ell, WebNative.RGN_OR);
                WebNative.DeleteObject(ell);
                WebNative.DeleteObject(quarter);
            }
        }
        if (clip is { } c)
        {
            var vis = WebNative.CreateRectRgn((int)Math.Floor(c.Left * sx), (int)Math.Floor(c.Top * sy), (int)Math.Ceiling(c.Right * sx), (int)Math.Ceiling(c.Bottom * sy));
            WebNative.CombineRgn(rgn, rgn, vis, WebNative.RGN_AND);
            WebNative.DeleteObject(vis);
        }
        foreach (var r in holes)
        {
            var hole = WebNative.CreateRectRgn((int)Math.Floor(r.Left * sx), (int)Math.Floor(r.Top * sy), (int)Math.Ceiling(r.Right * sx), (int)Math.Ceiling(r.Bottom * sy));
            WebNative.CombineRgn(rgn, rgn, hole, WebNative.RGN_DIFF);
            WebNative.DeleteObject(hole);
        }
        if (WebNative.SetWindowRgn(wv.Handle, rgn, true) == 0) WebNative.DeleteObject(rgn); // 成功后区域归系统所有
        else _hasRegion = true;
    }

    /// <summary>主窗口右下角的提示在网页坐标里的矩形（和网页相交的部分）。</summary>
    private List<Rect> ToastRects(FrameworkElement wv)
    {
        var list = new List<Rect>();
        if (Window.GetWindow(this) is not MainWindow mw) return list;
        var bounds = new Rect(0, 0, wv.ActualWidth, wv.ActualHeight);
        foreach (UIElement child in mw.ToastLayer.Children)
        {
            if (child is not FrameworkElement fe || !fe.IsVisible || fe.Opacity < 0.05) continue;
            try
            {
                var r = fe.TransformToVisual(wv).TransformBounds(new Rect(fe.RenderSize));
                r.Intersect(bounds);
                if (!r.IsEmpty && r.Width > 0 && r.Height > 0) list.Add(r);
            }
            catch (InvalidOperationException) { /* 不在同一棵树里 */ }
        }
        return list;
    }

    /// <summary>要圆的角（位 0 左上、1 右上、2 右下、3 左下）与半径。</summary>
    private (int Corners, double Radius) Corners(FrameworkElement wv)
    {
        if (CornerRadius > 0) return (0b1111, CornerRadius);
        // 主窗口内容岛：页面区域按 RadiusLG 裁圆角（MainWindow 里 _page.Clip）
        FrameworkElement? island = null;
        for (var p = VisualTreeHelper.GetParent(wv); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is Border b && System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "content.island") { island = b.Child as FrameworkElement; break; }
        if (island == null) return (0, 0);
        try
        {
            var r = island.TransformToVisual(wv).TransformBounds(new Rect(0, 0, island.ActualWidth, island.ActualHeight));
            double w = wv.ActualWidth, h = wv.ActualHeight;
            bool Near(double a, double b) => Math.Abs(a - b) < 1.5;
            int c = 0;
            if (Near(r.Left, 0) && Near(r.Top, 0)) c |= 1;
            if (Near(r.Right, w) && Near(r.Top, 0)) c |= 2;
            if (Near(r.Right, w) && Near(r.Bottom, h)) c |= 4;
            if (Near(r.Left, 0) && Near(r.Bottom, h)) c |= 8;
            return (c, M.RadiusLG);
        }
        catch (InvalidOperationException) { return (0, 0); }
    }

    /// <summary>当前窗口区域（设备无关像素，相对网页左上角）；截图时按它裁剪。</summary>
    private Geometry? ClipGeometry()
    {
        var wv = _wv;
        if (wv == null) return null;
        var rect = new Rect(0, 0, wv.ActualWidth, wv.ActualHeight);
        Geometry g = new RectangleGeometry(rect);
        var (corners, radius) = Corners(wv);
        for (int i = 0; i < 4; i++)
        {
            if ((corners & (1 << i)) == 0) continue;
            double x0 = i is 1 or 2 ? rect.Width - radius : 0, y0 = i is 2 or 3 ? rect.Height - radius : 0;
            var corner = new RectangleGeometry(new Rect(x0, y0, radius, radius));
            var ex = i is 1 or 2 ? rect.Width - radius : radius;
            var ey = i is 2 or 3 ? rect.Height - radius : radius;
            var keep = new CombinedGeometry(GeometryCombineMode.Intersect, corner, new EllipseGeometry(new Point(ex, ey), radius, radius));
            g = new CombinedGeometry(GeometryCombineMode.Union, new CombinedGeometry(GeometryCombineMode.Exclude, g, corner), keep);
        }
        if (ScrollClip(wv) is { } c) g = new CombinedGeometry(GeometryCombineMode.Intersect, g, new RectangleGeometry(c));
        foreach (var r in ToastRects(wv)) g = new CombinedGeometry(GeometryCombineMode.Exclude, g, new RectangleGeometry(r));
        return g;
    }

    // MARK: 登记（调试桥截图、快捷键判断）

    private static void Register(PPWebView v)
    {
        LiveList.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, v));
        LiveList.Add(new WeakReference<PPWebView>(v));
    }

    private static void Unregister(PPWebView v) => LiveList.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, v));

    /// <summary>调试桥看的内部状态。</summary>
    internal JObject DebugState()
    {
        var o = new JObject
        {
            ["frozen"] = _frozen,
            ["covered"] = _covered,
            ["failed"] = _failed,
            ["snapshot"] = _snapshot.Visibility.ToString(),
            ["region"] = _hasRegion,
        };
        if (_wv != null && _wv.Handle != IntPtr.Zero)
        {
            WebNative.GetWindowRect(_wv.Handle, out var rc);
            o["hwndVisible"] = WebNative.IsWindowVisible(_wv.Handle);
            o["hwndRect"] = $"{rc.Left},{rc.Top},{rc.Right - rc.Left}x{rc.Bottom - rc.Top}";
        }
        return o;
    }

    /// <summary>现在有 WebView2 的网页。</summary>
    public static IReadOnlyList<PPWebView> Live
    {
        get
        {
            var list = new List<PPWebView>();
            foreach (var w in LiveList) if (w.TryGetTarget(out var t) && t._wv != null) list.Add(t);
            return list;
        }
    }

    /// <summary>主窗口快捷键要不要让给网页：按键来自 KeysToPage 的网页（终端）。</summary>
    public static bool OwnsKey(KeyEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is PPWebView v) return v.KeysToPage;
        return false;
    }

    /// <summary>截图前：给每个显示着的网页取一张当前画面（调试桥用）。</summary>
    public static async Task CaptureAllAsync()
    {
        foreach (var v in Live)
            v._lastShot = v._wv?.Visibility == Visibility.Visible ? await v.CaptureAsync() : null;
    }

    /// <summary>截图时叠加到窗口上的网页画面（RenderTargetBitmap 画不出 HwndHost 的内容）。</summary>
    public static IEnumerable<(FrameworkElement Element, ImageSource Image, Geometry? Clip)> SnapshotOverlays(Window window)
    {
        foreach (var v in Live)
        {
            var wv = v._wv;
            if (wv == null || v._lastShot == null || wv.Visibility != Visibility.Visible || !wv.IsVisible) continue;
            if (Window.GetWindow(v) != window) continue;
            yield return (wv, v._lastShot, v.ClipGeometry());
        }
    }
}

/// <summary>窗口区域、DWM 标题栏颜色、整窗截图（PrintWindow）。</summary>
internal static class WebNative
{
    public const int RGN_AND = 1;
    public const int RGN_OR = 2;
    public const int RGN_DIFF = 4;

    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // 调试桥（前台窗口 / 焦点）用
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder s, int max);

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    // 调试桥的真实按键（SendInput）
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { vk = vk, scan = scan, flags = flags } } };

    private static void Send(List<INPUT> list) => SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());

    public static void SendCombo(int vk, System.Windows.Input.ModifierKeys mods)
    {
        var mvk = new List<ushort>();
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control)) mvk.Add(0x11);
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)) mvk.Add(0x10);
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Alt)) mvk.Add(0x12);
        var list = new List<INPUT>();
        foreach (var m in mvk) list.Add(Key(m, 0, 0));
        list.Add(Key((ushort)vk, 0, 0));
        list.Add(Key((ushort)vk, 0, 2));
        for (int i = mvk.Count - 1; i >= 0; i--) list.Add(Key(mvk[i], 0, 2));
        Send(list);
    }

    public static void SendText(string text)
    {
        var list = new List<INPUT>();
        foreach (var ch in text)
        {
            if (ch == '\n' || ch == '\r') { list.Add(Key(0x0D, 0, 0)); list.Add(Key(0x0D, 0, 2)); continue; }
            list.Add(Key(0, ch, 4));
            list.Add(Key(0, ch, 4 | 2));
        }
        Send(list);
    }

    public static string WindowText(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
    public static string ClassName(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWA_TEXT_COLOR = 36;

    /// <summary>COLORREF（0x00BBGGRR）。</summary>
    public static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>标准标题栏跟设计变量走（Windows 11 支持标题栏颜色；Windows 10 只换明暗）。</summary>
    public static void ApplyCaption(IntPtr hwnd, bool dark, Color caption, Color text)
    {
        if (hwnd == IntPtr.Zero) return;
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, 4);
        int c = ColorRef(caption);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref c, 4);
        int t = ColorRef(text);
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref t, 4);
    }
}

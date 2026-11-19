using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

// 编辑窗口（docs/editor.md）：每个工程一个 Window + 一个 WebView2（CodeMirror 6 网页，标签页在网页里）。对应 Mac EditorWindows.swift。
// 用得很少，所以：平时零占用；打开时才创建；关闭后把 WebView2 释放（Dispose → 这个网页的渲染进程退出，
// 全部网页都关掉后 WebView2 浏览器进程也退出），不缓存、不预热。
//
// 和 Mac 的对应：
//   - 标准标题栏（透明、底色 bgContent）→ 系统标题栏，用 DWM 把标题栏染成 bgContent、文字 textPrimary（Windows 11；Windows 10 只换明暗）；
//   - 关闭按钮上的未保存圆点 → 标题前加「● 」（Windows 没有 isDocumentEdited）；
//   - 编辑窗口在前时暂停菜单快捷键 → 这里是独立窗口，主窗口的快捷键本来就不在这里生效，按键全部交给网页；
//   - 红绿灯关闭先让网页处理未保存 → Closing 里取消关闭，调 paperplane.requestClose()，网页确认后发 closeWindow。

/// <summary>每个工程一个编辑窗口（对应 Mac EditorWindows）。</summary>
public sealed class EditorWindows
{
    public static readonly EditorWindows Shared = new();

    private readonly Dictionary<string, EditorWindow> _windows = new();

    public bool IsEmpty => _windows.Count == 0;

    public bool HasUnsaved => _windows.Values.Any(w => w.IsDirty);

    /// <summary>打开着的编辑窗口（调试桥用）。</summary>
    internal IReadOnlyCollection<EditorWindow> Windows => _windows.Values;

    internal EditorWindow? Find(string projectId) => _windows.TryGetValue(projectId, out var w) ? w : null;

    /// <summary>打开（或聚焦）工程的编辑窗口，可选打开某个文件并跳到某行。</summary>
    public void Open(string projectId, string? file = null, int? line = null)
    {
        if (_windows.TryGetValue(projectId, out var existing))
        {
            existing.FocusWindow();
            if (file != null) existing.OpenFile(file, line);
            return;
        }
        var model = AppModel.Shared;
        if (model.Api == null)
        {
            model.Toasts.Show("error", L.T("connection.notConnected"));
            return;
        }
        var summary = model.Data.Project(projectId);
        var w = new EditorWindow(projectId, summary?.Name ?? projectId, summary?.Path ?? "", file, line);
        _windows[projectId] = w;
        w.FocusWindow();
    }

    internal void Closed(string projectId)
    {
        _windows.Remove(projectId);
        // 把刚释放的托管内存还回去（网页进程由 WebView2 自己回收）
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        };
        t.Start();
    }

    /// <summary>退出前：没有未保存的就全部关掉并返回 true；有未保存的让这些窗口各自提示，返回 false。</summary>
    public bool PrepareForQuit()
    {
        var dirty = _windows.Values.Where(w => w.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            foreach (var w in _windows.Values.ToList()) w.ForceClose();
            return true;
        }
        foreach (var w in dirty)
        {
            w.FocusWindow();
            w.RequestClose();
        }
        return false;
    }

    /// <summary>工程被移除时关闭它的编辑窗口（网页自己处理未保存）。</summary>
    public void RequestClose(string projectId)
    {
        if (_windows.TryGetValue(projectId, out var w)) w.RequestClose();
    }
}

/// <summary>一个工程的编辑窗口（对应 Mac EditorWindowController）。</summary>
internal sealed class EditorWindow : Window
{
    public string ProjectId { get; }
    private readonly string _projectPath;
    private readonly PPWebView _web;
    private readonly List<string> _pending = new();
    private string? _file;
    private int? _line;
    private string _pageTitle;
    private bool _closing;
    private bool _closed;

    public bool IsDirty { get; private set; }
    public PPWebView Web => _web;

    private static bool Debugging => Environment.GetEnvironmentVariable("PAPERPLANE_DEBUG_BRIDGE") != null;

    public EditorWindow(string projectId, string projectName, string projectPath, string? file, int? line)
    {
        ProjectId = projectId;
        _projectPath = projectPath;
        _file = file;
        _line = line;
        _pageTitle = projectName + " — " + L.T("editorWindow.title");
        Title = _pageTitle;
        Width = 1100;
        Height = 760;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        this.Bg(ColorToken.BgContent);
        RestoreSavedBounds();

        _web = new PPWebView("editor:" + projectId, MakeUrl, ColorToken.BgContent)
        {
            HideUntilReady = true, // 网页就绪前不显示，避免白屏闪烁
            ErrorIcon = "file-pen",
            LoadFailedText = L.T("editorWindow.loadFailed"),
            CrashedText = L.T("editorWindow.crashed"),
            ReloadText = L.T("editorWindow.reload"),
        };
        _web.Message += OnMessage;
        _web.Ready += FlushPending;
        _web.Crashed += () => SetDirty(false);
        var root = new Grid();
        root.Children.Add(_web);
        Templates.ApplyRootDefaults(root);
        Content = root;

        SourceInitialized += (_, _) => ApplyCaption();
        Theme.Changed += OnThemeChanged;
        L10n.Shared.Changed += OnLocaleChanged;
    }

    /// <summary>首次加载带上要打开的文件；「重新加载」不再带（和 Mac 一样）。</summary>
    private string? MakeUrl()
    {
        var api = AppModel.Shared.Api;
        if (api == null) return null;
        var url = EditorURL.Make(api.BaseUrl, api.Token, ProjectId, _file, _line, L10n.Shared.Locale, Theme.IsDark ? "dark" : "light");
        _file = null;
        _line = null;
        return url;
    }

    public void FocusWindow()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _web.FocusPage();
    }

    /// <summary>已打开时切到某个文件。</summary>
    public void OpenFile(string path, int? line) => Call("openFile", path, line);

    /// <summary>让网页处理未保存（确认后网页发 closeWindow）；网页没就绪或没有实现时直接关。</summary>
    public async void RequestClose()
    {
        if (!_web.IsReady)
        {
            ForceClose();
            return;
        }
        var r = await _web.EvalAsync("window.paperplane && typeof paperplane.requestClose === 'function' ? (paperplane.requestClose(), 'ok') : 'missing'");
        if (r != "\"ok\"") ForceClose();
    }

    public void ForceClose()
    {
        if (_closed) return;
        _closing = true;
        Close();
    }

    // MARK: 调用网页

    /// <summary>paperplane.&lt;fn&gt;(args…)；网页就绪前排队。</summary>
    private void Call(string fn, params object?[] args)
    {
        var js = "window.paperplane && paperplane." + fn + "(" + string.Join(", ", args.Select(WebView2Env.Js)) + ")";
        if (!_web.IsReady)
        {
            _pending.Add(js);
            return;
        }
        _web.Exec(js);
    }

    private void FlushPending()
    {
        var queued = _pending.ToList();
        _pending.Clear();
        foreach (var js in queued) _web.Exec(js);
    }

    // MARK: 网页 → 宿主

    private void OnMessage(JObject m)
    {
        switch ((string?)m["type"])
        {
            case "title":
                if ((string?)m["title"] is { Length: > 0 } t)
                {
                    _pageTitle = t;
                    UpdateTitle();
                }
                break;
            case "dirty":
                SetDirty((bool?)m["dirty"] ?? false);
                break;
            case "closeWindow":
                ForceClose();
                break;
            case "closeCancelled":
                // 用户在网页的未保存提示里点了取消：窗口保持打开，宿主没有挂起的关闭状态需要恢复
                break;
            case "reveal":
                if ((string?)m["path"] is { } p) FileActions.RevealInExplorer(p, _projectPath);
                break;
            case "openExternal":
                if ((string?)m["url"] is { } s && Uri.TryCreate(s, UriKind.Absolute, out var u)
                    && (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "mailto"))
                    Actions.OpenURL(u.AbsoluteUri);
                break;
        }
    }

    private void SetDirty(bool dirty)
    {
        IsDirty = dirty;
        UpdateTitle();
    }

    private void UpdateTitle() => Title = (IsDirty ? "● " : "") + _pageTitle;

    // MARK: 主题与语言

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyCaption();
        Call("setTheme", Theme.IsDark ? "dark" : "light");
    }

    private void OnLocaleChanged(object? sender, EventArgs e) => Call("setLocale", L10n.Shared.Locale);

    private void ApplyCaption() =>
        WebNative.ApplyCaption(new WindowInteropHelper(this).Handle, Theme.IsDark, Theme.Color(ColorToken.BgContent), Theme.Color(ColorToken.TextPrimary));

    // MARK: 关闭与释放

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        SaveBounds();
        // 标题栏关闭 / Alt+F4：先让网页处理未保存；网页没就绪（加载失败、崩溃）时直接关
        if (_closing || !_web.IsReady) return;
        e.Cancel = true;
        RequestClose();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true;
        Theme.Changed -= OnThemeChanged;
        L10n.Shared.Changed -= OnLocaleChanged;
        _pending.Clear();
        _web.Message -= OnMessage;
        _web.Dispose();
        Content = null;
        EditorWindows.Shared.Closed(ProjectId);
    }

    // MARK: 窗口位置（按工程记住）

    private string BoundsKey => "EditorWindow." + ProjectId;

    private void SaveBounds()
    {
        if (Debugging) return; // 自动化测试不写用户的界面偏好
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (b.IsEmpty || b.Width < MinWidth || double.IsNaN(b.Left)) return;
        UiPrefs.Set(BoundsKey, string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4}", b.Left, b.Top, b.Width, b.Height,
            WindowState == WindowState.Maximized ? 1 : 0));
    }

    private void RestoreSavedBounds()
    {
        if (Debugging) return;
        var s = UiPrefs.Get(BoundsKey);
        var parts = s?.Split(',');
        if (parts == null || parts.Length < 5) return;
        try
        {
            double P(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
            var r = new Rect(P(0), P(1), Math.Max(MinWidth, P(2)), Math.Max(MinHeight, P(3)));
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            // 显示器拔掉后不出现在屏幕外
            var visible = Rect.Intersect(r, screen);
            if (visible.IsEmpty || visible.Width < 120 || visible.Height < 80) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = r.Left;
            Top = r.Top;
            Width = r.Width;
            Height = r.Height;
            if (parts[4] == "1") WindowState = WindowState.Maximized;
        }
        catch (FormatException) { }
    }
}

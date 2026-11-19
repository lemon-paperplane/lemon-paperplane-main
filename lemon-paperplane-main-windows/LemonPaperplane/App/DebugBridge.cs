using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LemonPaperplane;

/// <summary>
/// 调试桥（自动化测试用，PORTING.md 第 6 节）：环境变量 PAPERPLANE_DEBUG_BRIDGE=&lt;管道名&gt; 时开命名管道 \\.\pipe\&lt;管道名&gt;，
/// 每行一个 JSON 命令、回一行 JSON（{"ok":true,…} 或 {"ok":false,"error":"…"}）。
/// 客户端：LemonPaperplane.exe --debug-client &lt;管道名&gt; '&lt;json&gt;' ['&lt;json&gt;' …]（逐条发送，每条回应打印一行）。
///
/// 命令（cmd）：
///   ping
///   snapshot  {path, scale?=1, wait?=300, id?, bg?}  主窗口 + 打开着的弹出层合成 PNG（scale 模拟高 DPI，如 1.5）；
///                                                  给 id 时只截该元素的完整内容（不受滚动裁剪），底色 bg（默认 bgContent）
///   scroll    {id?, y?, x?}                        滚动（默认第一个能滚的区域）
///   tree      {id?, all?=false, depth?=60}        元素树：类型、AutomationId、名字、文字、位置大小（相对窗口）；弹出层另列
///   click     {id? | text?, index?=0, button?="left"|"right", contains?=false}
///                                                  按 AutomationId 或可见文字找元素，向上找到可点击的（按钮、.OnClick、菜单行）并触发
///   focus     {id}                                 聚焦（输入框聚焦到里面的 TextBox）
///   key       {keys:"Ctrl+R" | "Escape" | "Down" | "Enter"}   模拟按键（发给焦点元素，修饰键经 KeyInput.Modifiers）
///   type      {text}                               往焦点输入框输入文字
///   theme     {value:"system"|"light"|"dark"}      切换主题（不保存）
///   locale    {value:"system"|"zh-Hans"|"en"}      切换语言（不保存）
///   resize    {width, height}                      窗口大小（DIP）
///   state     {value:"normal"|"maximized"|"minimized"}
///   gallery   {tab?}                               打开组件陈列页（controls / display / overlays / icons）
///   force     {id, state:"hover"|"pressed"|"focus"|"open"|null}  强制显示某个交互状态（截图用）
///   hittest   {id? | x,y}                          对窗口坐标发 WM_NCHITTEST，返回命中码（9 = HTMAXBUTTON，2 = 标题栏，1 = 客户区）
///   nav       {page?:"workspace"|"running"|"toolchains"|"plugins"|"settings", id?, project?, tab?, config?}
///                                                  导航：切页面；给 project 时打开工程详情（tab 选标签，config 选运行配置）
///   popups / closepopups / info / errors / quit
///   功能页另外登记的命令见 Extra（例如 Features/Editor/WebViewDebug.cs 的 web.* / editor.* / *.demo）。
/// </summary>
public static class DebugBridge
{
    private static readonly List<string> ErrorLog = new();
    private static Thread? _thread;

    public static bool IsEnabled => _thread != null;

    /// <summary>功能页登记的额外命令（cmd → 在 UI 线程上执行，可以异步）。</summary>
    public static readonly Dictionary<string, Func<JObject, Task<JObject>>> Extra = new();

    /// <summary>截图前在 UI 线程上调用并等它完成（WebView2 这类 RenderTargetBitmap 画不出来的内容先异步取图）。</summary>
    public static Func<Task>? BeforeSnapshot { get; set; }

    /// <summary>截图时叠加到窗口上的图：元素、图、元素坐标里的裁剪（可空）。</summary>
    public static Func<Window, IEnumerable<(FrameworkElement Element, ImageSource Image, Geometry? Clip)>>? SnapshotOverlays { get; set; }

    public static void LogError(Exception e)
    {
        lock (ErrorLog)
        {
            ErrorLog.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + e);
            if (ErrorLog.Count > 50) ErrorLog.RemoveAt(0);
        }
        try { Console.Error.WriteLine(e); } catch { /* 没有控制台 */ }
    }

    public static void Start(string pipeName)
    {
        WebViewDebug.Register();
        _thread = new Thread(() => Serve(pipeName)) { IsBackground = true, Name = "DebugBridge" };
        _thread.Start();
    }

    private static void Serve(string pipeName)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte);
                server.WaitForConnection();
                var reader = new StreamReader(server, new UTF8Encoding(false));
                var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    writer.WriteLine(Handle(line));
                }
            }
            catch (Exception e)
            {
                LogError(e);
                Thread.Sleep(100);
            }
        }
    }

    private static string Handle(string line)
    {
        JObject req;
        try { req = JObject.Parse(line); }
        catch (Exception e) { return Err("JSON 格式不对：" + e.Message); }
        var cmd = (string?)req["cmd"] ?? "";
        var app = Application.Current;
        if (app == null) return Err("界面还没启动");
        try
        {
            if (Extra.TryGetValue(cmd, out var extra))
            {
                Idle(app);
                var task = app.Dispatcher.Invoke(() => extra(req));
                if (!task.Wait(TimeSpan.FromSeconds(30))) return Err("超时（30 秒）");
                var r = task.GetAwaiter().GetResult();
                r["ok"] = true;
                return r.ToString(Formatting.None);
            }
            if (cmd == "snapshot")
            {
                Thread.Sleep((int?)req["wait"] ?? 300); // 等动画（淡入 120–160ms）走完
                Idle(app);
                if (BeforeSnapshot != null) app.Dispatcher.Invoke(BeforeSnapshot).Wait(5000);
            }
            else if (cmd is not ("ping" or "quit"))
            {
                Idle(app); // 等上一条命令排队的点击、重建、焦点移动做完
            }
            var result = app.Dispatcher.Invoke(() => Execute(cmd, req));
            result["ok"] = true;
            return result.ToString(Formatting.None);
        }
        catch (Exception e)
        {
            var inner = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            return Err(inner.GetType().Name + ": " + inner.Message);
        }
    }

    /// <summary>等界面把排队的重建、布局、渲染做完。</summary>
    private static void Idle(Application app) => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static string Err(string msg) => new JObject { ["ok"] = false, ["error"] = msg }.ToString(Formatting.None);

    private static MainWindow Win => MainWindow.Current ?? throw new InvalidOperationException("主窗口不存在");

    private static JObject Execute(string cmd, JObject req)
    {
        switch (cmd)
        {
            case "ping":
                return new JObject { ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id };
            case "snapshot":
                if (req["id"] != null)
                    return SnapshotElement(req, (string?)req["path"] ?? throw new ArgumentException("缺少 path"), (double?)req["scale"] ?? 1);
                return Snapshot((string?)req["path"] ?? throw new ArgumentException("缺少 path"), (double?)req["scale"] ?? 1);
            case "scroll":
            {
                var sv = (req["id"] != null ? Find(req) as ScrollViewer : null)
                         ?? PPView.Descendants<ScrollViewer>(Win).FirstOrDefault(s => s.ScrollableHeight > 0)
                         ?? throw new ArgumentException("找不到可滚动区域");
                if (req["y"] != null) sv.ScrollToVerticalOffset((double)req["y"]!);
                if (req["x"] != null) sv.ScrollToHorizontalOffset((double)req["x"]!);
                sv.UpdateLayout();
                return new JObject { ["y"] = sv.VerticalOffset, ["max"] = sv.ScrollableHeight };
            }
            case "tree":
                return Tree((string?)req["id"], (bool?)req["all"] ?? false, (int?)req["depth"] ?? 60);
            case "click":
                return Click(req);
            case "focus":
            {
                var el = Find(req) ?? throw new ArgumentException("找不到元素");
                bool ok = el is PPTextField tf ? tf.Focus() : (el is UIElement u && u.Focus());
                return new JObject { ["focused"] = ok, ["target"] = Describe(el) };
            }
            case "key":
                return PressKey((string?)req["keys"] ?? throw new ArgumentException("缺少 keys"));
            case "type":
            {
                var target = Keyboard.FocusedElement ?? throw new InvalidOperationException("没有焦点元素");
                var text = (string?)req["text"] ?? "";
                TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, target, text));
                return new JObject { ["target"] = Describe(target as DependencyObject) };
            }
            case "theme":
                Theme.Apply((string?)req["value"], persist: false);
                return new JObject { ["dark"] = Theme.IsDark };
            case "locale":
                L10n.Shared.Apply((string?)req["value"], persist: false);
                return new JObject { ["locale"] = L10n.Shared.Locale };
            case "resize":
                Win.WindowState = WindowState.Normal;
                if (req["width"] != null) Win.Width = (double)req["width"]!;
                if (req["height"] != null) Win.Height = (double)req["height"]!;
                return new JObject { ["width"] = Win.Width, ["height"] = Win.Height };
            case "state":
                Win.WindowState = ((string?)req["value"]) switch { "maximized" => WindowState.Maximized, "minimized" => WindowState.Minimized, _ => WindowState.Normal };
                return new JObject { ["state"] = Win.WindowState.ToString() };
            case "gallery":
                Gallery.Show(Win, (string?)req["tab"]);
                return new JObject();
            case "force":
            {
                var el = Find(req) ?? throw new ArgumentException("找不到元素");
                el.SetValue(PPDebug.ForceStateProperty, (string?)req["state"]);
                (el as IVisualStateful)?.UpdateVisual();
                return new JObject { ["target"] = Describe(el) };
            }
            case "hittest":
            {
                Point p;
                if (req["id"] != null || req["text"] != null)
                {
                    var el = Find(req) as FrameworkElement ?? throw new ArgumentException("找不到元素");
                    p = el.TransformToAncestor(Win).Transform(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
                }
                else p = new Point((double?)req["x"] ?? 0, (double?)req["y"] ?? 0);
                int code = Win.HitTest(p);
                return new JObject { ["code"] = code, ["x"] = p.X, ["y"] = p.Y };
            }
            case "nav":
            {
                var nav = AppModel.Shared.Nav;
                switch ((string?)req["page"])
                {
                    case "workspace": nav.Page = NavPage.WorkspacePage((string?)req["id"] ?? AppModel.Shared.Data.Workspaces.FirstOrDefault()?.Id ?? ""); break;
                    case "running": nav.Page = NavPage.Running; break;
                    case "toolchains": nav.Page = NavPage.ToolchainsPage; break;
                    case "plugins": nav.Page = NavPage.PluginsPage; break;
                    case "settings": nav.Page = NavPage.SettingsPage; break;
                }
                if ((string?)req["project"] is { } pid)
                {
                    if ((string?)req["config"] is { } cid) nav.SelectConfig(pid, cid);
                    nav.OpenProject(pid, (string?)req["tab"] ?? ProjectTab.Overview);
                }
                else if ((string?)req["tab"] is { } tab) nav.ProjectTabSelection = tab;
                return new JObject { ["page"] = nav.Page.Kind, ["id"] = nav.Page.Id, ["project"] = nav.OpenProjectId, ["tab"] = nav.ProjectTabSelection };
            }
            case "popups":
                return new JObject { ["popups"] = new JArray(PopupController.Open.Select(p => new JObject { ["anchor"] = Describe(p.Anchor) })) };
            case "closepopups":
                PopupController.DismissAll();
                return new JObject();
            case "info":
            {
                var dpi = VisualTreeHelper.GetDpi(Win);
                return new JObject
                {
                    ["dpi"] = dpi.PixelsPerInchX,
                    ["uiFont"] = UIFonts.UI.Source,
                    ["uiFontFirst"] = UIFonts.UIName,
                    ["monoFont"] = UIFonts.Mono.Source,
                    ["dark"] = Theme.IsDark,
                    ["themeSetting"] = Theme.Setting,
                    ["locale"] = L10n.Shared.Locale,
                    ["width"] = Win.ActualWidth,
                    ["height"] = Win.ActualHeight,
                    ["state"] = Win.WindowState.ToString(),
                    ["os"] = Environment.OSVersion.VersionString,
                    ["renderTier"] = RenderCapability.Tier >> 16,
                };
            }
            case "errors":
                lock (ErrorLog) return new JObject { ["errors"] = new JArray(ErrorLog.ToArray()) };
            case "quit":
                Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
                return new JObject();
            default:
                throw new ArgumentException("未知命令：" + cmd);
        }
    }

    // MARK: 截图

    private static JObject Snapshot(string path, double scale)
    {
        var w = Win;
        var root = (FrameworkElement)w.Content;
        var origin = root.PointToScreen(new Point(0, 0));
        var src = PresentationSource.FromVisual(root)!;
        var fromDevice = src.CompositionTarget.TransformFromDevice;
        double width = w.ActualWidth, height = w.ActualHeight;
        int pw = (int)Math.Ceiling(width * scale), ph = (int)Math.Ceiling(height * scale);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(Render(w, width, height, scale), new Rect(0, 0, width, height));
            if (SnapshotOverlays != null)
                foreach (var (el, img, clip) in SnapshotOverlays(w))
                {
                    var r = el.TransformToAncestor(w).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                    if (clip != null)
                    {
                        var g = clip.Clone();
                        g.Transform = new TranslateTransform(r.X, r.Y);
                        dc.PushClip(g);
                    }
                    dc.DrawImage(img, r);
                    if (clip != null) dc.Pop();
                }
            foreach (var p in PopupController.Open)
            {
                var c = p.Popup.Child as FrameworkElement;
                if (c == null || PresentationSource.FromVisual(c) == null) continue;
                var tl = fromDevice.Transform(c.PointToScreen(new Point(0, 0)));
                var o = fromDevice.Transform(origin);
                dc.DrawImage(Render(c, c.ActualWidth, c.ActualHeight, scale), new Rect(tl.X - o.X + root.Margin.Left, tl.Y - o.Y + root.Margin.Top, c.ActualWidth, c.ActualHeight));
            }
        }
        var final = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        final.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(final));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var f = File.Create(path)) enc.Save(f);
        return new JObject { ["path"] = path, ["width"] = pw, ["height"] = ph, ["popups"] = PopupController.Open.Count };
    }

    /// <summary>只截某个元素（完整尺寸，不受滚动裁剪）：{"cmd":"snapshot","id":"gallery.body","path":…,"bg":"bgContent"}。</summary>
    private static JObject SnapshotElement(JObject req, string path, double scale)
    {
        var el = Find(req) as FrameworkElement ?? throw new ArgumentException("找不到元素");
        double w = el.ActualWidth, h = el.ActualHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Theme.Brush((string?)req["bg"] ?? ColorToken.BgContent), null, new Rect(0, 0, w, h));
            dc.DrawImage(Render(el, w, h, scale), new Rect(0, 0, w, h));
        }
        var final = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        final.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(final));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var f = File.Create(path)) enc.Save(f);
        return new JObject { ["path"] = path, ["width"] = final.PixelWidth, ["height"] = final.PixelHeight };
    }

    private static BitmapSource Render(Visual v, double w, double h, double scale)
    {
        var rtb = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    // MARK: 元素树

    private static IEnumerable<FrameworkElement> Roots()
    {
        foreach (var p in PopupController.Open.Reverse())
            if (p.Popup.Child is FrameworkElement c) yield return c;
        yield return Win;
    }

    private static JObject Tree(string? id, bool all, int depth)
    {
        DependencyObject root = Win;
        if (id != null) root = Find(new JObject { ["id"] = id }) ?? throw new ArgumentException("找不到 " + id);
        var result = new JObject { ["tree"] = Node(root, all, depth) ?? new JObject() };
        if (id == null)
            result["popups"] = new JArray(PopupController.Open.Where(p => p.Popup.Child != null).Select(p => Node(p.Popup.Child, all, depth)));
        return result;
    }

    private static JObject? Node(DependencyObject d, bool all, int depth)
    {
        var children = new JArray();
        if (depth > 0)
        {
            foreach (var c in VisualChildren(d))
            {
                if (c is UIElement u && u.Visibility != Visibility.Visible) continue;
                var n = Node(c, all, depth - 1);
                if (n == null) continue;
                if (n["hoist"] != null) foreach (var x in (JArray)n["hoist"]!) children.Add(x);
                else children.Add(n);
            }
        }
        bool interesting = all || IsInteresting(d);
        if (!interesting) return children.Count == 0 ? null : new JObject { ["hoist"] = children };
        var o = Describe(d);
        if (d is FrameworkElement fe && fe.IsVisible)
        {
            try
            {
                var p = ToWindow(fe);
                o["x"] = Math.Round(p.X, 1);
                o["y"] = Math.Round(p.Y, 1);
                o["w"] = Math.Round(fe.ActualWidth, 1);
                o["h"] = Math.Round(fe.ActualHeight, 1);
            }
            catch (InvalidOperationException) { }
            if (!fe.IsEnabled) o["enabled"] = false;
            if (fe.IsKeyboardFocused || fe.IsKeyboardFocusWithin && fe is PPTextField) o["focused"] = true;
        }
        if (children.Count > 0) o["children"] = children;
        return o;
    }

    private static Point ToWindow(FrameworkElement fe)
    {
        var w = Win;
        try { return fe.TransformToAncestor(w).Transform(new Point(0, 0)); }
        catch (InvalidOperationException)
        {
            // 弹出层：按屏幕坐标换算
            var root = (FrameworkElement)w.Content;
            var src = PresentationSource.FromVisual(root)!;
            var fromDevice = src.CompositionTarget.TransformFromDevice;
            var a = fromDevice.Transform(fe.PointToScreen(new Point(0, 0)));
            var b = fromDevice.Transform(w.PointToScreen(new Point(0, 0)));
            return new Point(a.X - b.X, a.Y - b.Y);
        }
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject d)
    {
        if (d is not Visual && d is not System.Windows.Media.Media3D.Visual3D) yield break;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) yield return VisualTreeHelper.GetChild(d, i);
    }

    private static bool IsInteresting(DependencyObject d) =>
        !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(d)) || !string.IsNullOrEmpty(AutomationProperties.GetName(d)) ||
        d is ButtonBase || d is TextBox || d is PasswordBox || d is PPView || d is PPIcon ||
        (d is TextBlock t && !string.IsNullOrEmpty(t.Text) && t.Parent is not MiddleTrimText) || d is MiddleTrimText || UI.GetClickAction(d) != null;

    private static JObject Describe(DependencyObject? d)
    {
        var o = new JObject();
        if (d == null) return o;
        o["type"] = d.GetType().Name;
        var id = AutomationProperties.GetAutomationId(d);
        if (!string.IsNullOrEmpty(id)) o["id"] = id;
        var name = AutomationProperties.GetName(d);
        if (!string.IsNullOrEmpty(name)) o["name"] = name;
        var text = TextOf(d);
        if (!string.IsNullOrEmpty(text)) o["text"] = text;
        if (d is PPIcon icon && icon.Key is IconKey k) o["icon"] = k.Key;
        return o;
    }

    private static string? TextOf(DependencyObject d) => d switch
    {
        MiddleTrimText m => m.FullText,
        TextBlock t => t.Text,
        TextBox tb => tb.Text,
        _ => null,
    };

    // MARK: 查找与点击

    private static DependencyObject? Find(JObject req)
    {
        var id = (string?)req["id"];
        var text = (string?)req["text"];
        bool contains = (bool?)req["contains"] ?? false;
        int index = (int?)req["index"] ?? 0;
        var matches = new List<DependencyObject>();
        foreach (var root in Roots())
        {
            foreach (var d in PPView.Descendants<DependencyObject>(root))
            {
                if (d is UIElement u && !u.IsVisible) continue;
                if (id != null)
                {
                    if (AutomationProperties.GetAutomationId(d) == id) matches.Add(d);
                }
                else if (text != null)
                {
                    var t = TextOf(d);
                    var n = AutomationProperties.GetName(d);
                    bool hit = contains ? (t?.Contains(text) == true || n?.Contains(text) == true) : (t == text || (d is not TextBox && n == text));
                    if (hit && !(d is TextBlock tb && tb.Parent is MiddleTrimText)) matches.Add(d);
                }
            }
        }
        return index < matches.Count ? matches[index] : null;
    }

    private static JObject Click(JObject req)
    {
        var el = Find(req) ?? throw new ArgumentException("找不到元素：" + req.ToString(Formatting.None));
        bool right = (string?)req["button"] == "right";
        for (var d = el; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (right)
            {
                if (ContextMenus.GetEntries(d) != null && d is FrameworkElement fe)
                {
                    SimulateMouseDown(fe);
                    ContextMenus.Open(fe, new Point(fe.ActualWidth / 2, fe.ActualHeight / 2));
                    return new JObject { ["target"] = Describe(fe), ["menu"] = true };
                }
                continue;
            }
            if (d is ButtonBase b)
            {
                if (!b.IsEnabled) throw new InvalidOperationException("按钮已禁用：" + Describe(b).ToString(Formatting.None));
                SimulateMouseDown(b);
                if (b is Button btn) (new ButtonAutomationPeer(btn).GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
                else if (b is ToggleButton tg) (new ToggleButtonAutomationPeer(tg).GetPattern(PatternInterface.Toggle) as IToggleProvider)?.Toggle();
                return new JObject { ["target"] = Describe(b) };
            }
            if (UI.GetClickAction(d) is Action a)
            {
                SimulateMouseDown(d);
                a();
                return new JObject { ["target"] = Describe(d) };
            }
            if (d is TextBox or PasswordBox)
            {
                ((UIElement)d).Focus();
                return new JObject { ["target"] = Describe(d), ["focused"] = true };
            }
        }
        throw new InvalidOperationException("没有可点击的元素：" + Describe(el).ToString(Formatting.None));
    }

    /// <summary>真实点击之前会先有一次按下：弹出层外的按下会关闭全部弹出层（点的若是锚点本身，记下来避免马上又打开）。</summary>
    private static void SimulateMouseDown(DependencyObject target)
    {
        bool inPopup = PopupController.Open.Any(p => p.Popup.Child != null && IsWithin(target, p.Popup.Child));
        if (inPopup)
        {
            var owner = PopupController.Open.Last(p => p.Popup.Child != null && IsWithin(target, p.Popup.Child));
            PopupController.DismissAbove(owner);
            return;
        }
        PopupController.SimulateOutsidePress(target);
    }

    private static bool IsWithin(DependencyObject d, DependencyObject ancestor)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (ReferenceEquals(x, ancestor)) return true;
        return false;
    }

    // MARK: 键盘

    private static JObject PressKey(string keys)
    {
        if (!KeyInput.TryParse(keys, out var key, out var mods)) throw new ArgumentException("按键格式不对：" + keys);
        var target = Keyboard.FocusedElement as DependencyObject ?? Win;
        var visual = target as Visual ?? Win;
        var source = PresentationSource.FromVisual(visual) ?? PresentationSource.FromVisual(Win);
        // 文本框的 Ctrl+A/C/V/X/Z/Y 由 WPF 按真实键盘状态匹配，模拟不了：直接执行对应的编辑命令
        if (mods == ModifierKeys.Control && target is IInputElement ie && (target is TextBox || target is PasswordBox))
        {
            System.Windows.Input.RoutedUICommand? editCmd = key switch
            {
                System.Windows.Input.Key.A => ApplicationCommands.SelectAll,
                System.Windows.Input.Key.C => ApplicationCommands.Copy,
                System.Windows.Input.Key.V => ApplicationCommands.Paste,
                System.Windows.Input.Key.X => ApplicationCommands.Cut,
                System.Windows.Input.Key.Z => ApplicationCommands.Undo,
                System.Windows.Input.Key.Y => ApplicationCommands.Redo,
                _ => null,
            };
            if (editCmd != null)
            {
                editCmd.Execute(null, ie);
                return new JObject { ["handled"] = true, ["command"] = editCmd.Name, ["target"] = Describe(target) };
            }
        }
        KeyInput.Override = mods;
        bool handled;
        try
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            InputManager.Current.ProcessInput(args);
            handled = args.Handled;
            // 回车、Tab、字符键在文本框里由 TextInput 处理
            if (!handled && mods == ModifierKeys.None && key == System.Windows.Input.Key.Enter && Keyboard.FocusedElement is TextBox tb && tb.AcceptsReturn)
                TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, tb, "\r"));
        }
        finally
        {
            KeyInput.Override = null;
        }
        return new JObject { ["handled"] = handled, ["target"] = Describe(target) };
    }

    // MARK: 客户端

    /// <summary>
    /// LemonPaperplane.exe --debug-client &lt;管道名&gt; '&lt;json&gt;' …：逐条发送，每条回应打印一行；全部 ok 时退出码 0。
    /// 命令写成 "-" 时从标准输入逐行读取（PowerShell 5.1 传给原生程序的参数会吃掉双引号，用管道传更省事）。
    /// </summary>
    public static int RunClient(string pipeName, IEnumerable<string> commands)
    {
        if (Native.GetStdHandle(-11) == IntPtr.Zero) Native.AttachConsole(-1);
        var output = Console.Out;
        var list = commands.ToList();
        if (list.Count == 1 && list[0] == "-")
        {
            list.Clear();
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            string? l;
            while ((l = stdin.ReadLine()) != null) if (l.Trim().Length > 0) list.Add(l);
        }
        commands = list;
        try
        {
            using var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            c.Connect(20000);
            var writer = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(c, new UTF8Encoding(false));
            bool allOk = true;
            foreach (var cmd in commands)
            {
                writer.WriteLine(cmd.Replace("\r", "").Replace("\n", " "));
                var resp = reader.ReadLine() ?? Err("连接断开");
                output.WriteLine(resp);
                if (!resp.StartsWith("{\"ok\":true", StringComparison.Ordinal) && !resp.Contains("\"ok\":true")) allOk = false;
            }
            output.Flush();
            return allOk ? 0 : 1;
        }
        catch (Exception e)
        {
            output.WriteLine(Err(e.Message));
            output.Flush();
            return 2;
        }
    }
}

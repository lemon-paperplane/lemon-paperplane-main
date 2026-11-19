using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 调试桥里和 WebView2 有关的命令（只在开了调试桥时登记，见 DebugBridge.Extra）：
///   web.list                              现有网页（名字、是否就绪、地址）
///   web.exec   {name, js}                 在网页里执行 JS，返回结果 JSON
///   web.cdp    {name, method, params?}     DevTools 协议（Input.insertText / Input.dispatchKeyEvent 模拟真实按键）
///   web.shot   {name, path}               网页画面存 PNG（CapturePreviewAsync）
///   win.shot   {target:"main"|工程 id, path}  整个窗口（含系统标题栏与 WebView2）存 PNG（PrintWindow）
///   activate   {target, focus?}            激活窗口（并把焦点给某个网页）；sendkeys {keys:"Ctrl+C"} / {text:"…"}  真实按键（SendInput）
///   editor.open {projectId, file?, line?} / editor.list / editor.close {projectId, force?} / editor.quit（PrepareForQuit）
///   files.demo {projectId} / terminal.demo {workspaceId} / codeinput.demo {workspaceId?} / page.restore
///     （工程详情、工作区页由别人负责，自测时把自己的视图直接放进内容岛）
/// snapshot 命令会先给每个显示着的网页取图并叠加到主窗口截图里（RenderTargetBitmap 画不出 HwndHost）。
/// </summary>
internal static class WebViewDebug
{
    public static void Register()
    {
        DebugBridge.BeforeSnapshot = PPWebView.CaptureAllAsync;
        DebugBridge.SnapshotOverlays = PPWebView.SnapshotOverlays;
        var x = DebugBridge.Extra;
        x["web.list"] = _ => Task.FromResult(new JObject
        {
            ["webviews"] = new JArray(PPWebView.Live.Select(v => new JObject
            {
                ["name"] = v.DebugName,
                ["ready"] = v.IsReady,
                ["visible"] = v.Control?.Visibility.ToString(),
                ["url"] = v.Core?.Source,
                ["state"] = v.DebugState(),
            })),
        });
        x["web.exec"] = async r => new JObject { ["result"] = await Web(r).EvalAsync((string?)r["js"] ?? "") };
        x["web.cdp"] = async r =>
        {
            var core = Web(r).Core ?? throw new InvalidOperationException("网页还没初始化");
            var call = core.CallDevToolsProtocolMethodAsync((string?)r["method"] ?? "", (r["params"] ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None));
            // Page.crash 这类调用不会有回应：最多等 5 秒
            if (await Task.WhenAny(call, Task.Delay(5000)) != call) return new JObject { ["result"] = null, ["timeout"] = true };
            return new JObject { ["result"] = await call };
        };
        x["web.shot"] = async r =>
        {
            var v = Web(r);
            var core = v.Core ?? throw new InvalidOperationException("网页还没初始化");
            var path = (string?)r["path"] ?? throw new ArgumentException("缺少 path");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var f = File.Create(path)) await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, f);
            return new JObject { ["path"] = path };
        };
        x["win.shot"] = r => Task.FromResult(WindowShot(r));
        // 真实按键（SendInput，发给前台窗口），验证按键经 WPF 到 WebView2 的整条路径：
        //   {keys:"Ctrl+C"}（组合键，写法同快捷键）或 {text:"echo 你好\n"}（逐字 Unicode 输入，不经输入法；\n 为回车）
        x["sendkeys"] = async r =>
        {
            // 只发给自己的窗口：前台不是本进程时拒绝（不往别的程序里打字）
            WebNative.GetWindowThreadProcessId(WebNative.GetForegroundWindow(), out var fgPid);
            if (fgPid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                throw new InvalidOperationException("前台窗口不是本程序，不发按键");
            if ((string?)r["text"] is { } text) WebNative.SendText(text);
            if ((string?)r["keys"] is { } keys)
            {
                if (!KeyInput.TryParse(keys, out var key, out var mods)) throw new ArgumentException("按键写法不对：" + keys);
                WebNative.SendCombo(System.Windows.Input.KeyInterop.VirtualKeyFromKey(key), mods);
            }
            await Task.Delay(200);
            return new JObject();
        };
        x["fg"] = _ =>
        {
            var fg = WebNative.GetForegroundWindow();
            var info = new WebNative.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<WebNative.GUITHREADINFO>() };
            WebNative.GetGUIThreadInfo(WebNative.GetWindowThreadProcessId(fg, out var pid), ref info);
            return Task.FromResult(new JObject
            {
                ["foreground"] = WebNative.WindowText(fg),
                ["foregroundClass"] = WebNative.ClassName(fg),
                ["ours"] = pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id,
                ["focusClass"] = WebNative.ClassName(info.hwndFocus),
                ["wpfFocus"] = System.Windows.Input.Keyboard.FocusedElement?.GetType().Name,
            });
        };
        x["activate"] = r =>
        {
            var target = (string?)r["target"] ?? "main";
            Window w = target == "main" ? Win : EditorWindows.Shared.Find(target) ?? throw new ArgumentException("没有这个窗口：" + target);
            // 后台进程拿不到前台：先按一下 Alt（系统允许最近有输入的进程设前台），再 SetForegroundWindow
            var hwnd = new WindowInteropHelper(w).Handle;
            WebNative.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            WebNative.SetForegroundWindow(hwnd);
            WebNative.keybd_event(0x12, 0, 2, UIntPtr.Zero);
            w.Activate();
            if ((string?)r["focus"] is { } name) Web(new JObject { ["name"] = name }).FocusPage();
            return Task.FromResult(new JObject { ["active"] = w.IsActive });
        };
        x["editor.open"] = r =>
        {
            EditorWindows.Shared.Open((string?)r["projectId"] ?? throw new ArgumentException("缺少 projectId"), (string?)r["file"], (int?)r["line"]);
            return Task.FromResult(new JObject());
        };
        x["editor.list"] = _ => Task.FromResult(new JObject
        {
            ["windows"] = new JArray(EditorWindows.Shared.Windows.Select(w => new JObject
            {
                ["projectId"] = w.ProjectId,
                ["title"] = w.Title,
                ["dirty"] = w.IsDirty,
                ["ready"] = w.Web.IsReady,
                ["active"] = w.IsActive,
                ["bounds"] = $"{w.Left:0},{w.Top:0},{w.ActualWidth:0},{w.ActualHeight:0}",
            })),
        });
        x["editor.close"] = r =>
        {
            var w = EditorWindows.Shared.Find((string?)r["projectId"] ?? "") ?? throw new ArgumentException("没有这个编辑窗口");
            if ((bool?)r["force"] == true) w.ForceClose(); else w.RequestClose();
            return Task.FromResult(new JObject());
        };
        x["editor.reload"] = r =>
        {
            // 等同空状态里的「重新加载」按钮（调试桥的 click 只找主窗口里的元素）
            var w = EditorWindows.Shared.Find((string?)r["projectId"] ?? "") ?? throw new ArgumentException("没有这个编辑窗口");
            w.Web.Reload();
            return Task.FromResult(new JObject());
        };
        x["editor.quit"] = _ => Task.FromResult(new JObject { ["canQuit"] = EditorWindows.Shared.PrepareForQuit() });
        x["files.demo"] = async r =>
        {
            var pid = (string?)r["projectId"] ?? throw new ArgumentException("缺少 projectId");
            var p = await AppModel.Shared.Client().ProjectAsync(pid);
            Win.Page = new FilesTab(p);
            return new JObject { ["name"] = p.Name };
        };
        x["terminal.demo"] = r =>
        {
            var ws = (string?)r["workspaceId"] ?? AppModel.Shared.Data.Workspaces.FirstOrDefault()?.Id ?? throw new ArgumentException("没有工作区");
            Win.Page = new TerminalPage(ws);
            return Task.FromResult(new JObject { ["workspaceId"] = ws });
        };
        x["toast"] = r =>
        {
            ToastStore.Shared.Show((string?)r["level"] ?? "info", (string?)r["title"] ?? "toast", (string?)r["message"]);
            return Task.FromResult(new JObject());
        };
        x["webviewhost.demo"] = r =>
        {
            // 插件网页视图：拿内核自带的一个静态页面当插件页面，验证 window.paperplane.call 桥
            var api = AppModel.Shared.Client();
            var url = new Uri(api.BaseUrl, (string?)r["path"] ?? "editor/i18n/zh-Hans.json");
            Win.Page = new WebviewHost(url, new ViewContext { Plugin = "demo", View = "demo", ProjectId = (string?)r["projectId"] });
            return Task.FromResult(new JObject { ["url"] = url.AbsoluteUri });
        };
        x["page.restore"] = _ =>
        {
            Win.Page = new MainContent();
            return Task.FromResult(new JObject());
        };
        x["codeinput.demo"] = r =>
        {
            AddCommandDemo((string?)r["workspaceId"] ?? AppModel.Shared.Data.Workspaces.FirstOrDefault()?.Id ?? "");
            return Task.FromResult(new JObject());
        };
    }

    private static MainWindow Win => MainWindow.Current ?? throw new InvalidOperationException("主窗口不存在");

    private static PPWebView Web(JObject r)
    {
        var name = (string?)r["name"] ?? "";
        return PPWebView.Live.LastOrDefault(v => v.DebugName == name) ?? throw new ArgumentException("没有网页：" + name);
    }

    /// <summary>PrintWindow（PW_RENDERFULLCONTENT）：窗口实际显示的样子，WebView2 也在里面。</summary>
    private static JObject WindowShot(JObject r)
    {
        var target = (string?)r["target"] ?? "main";
        var path = (string?)r["path"] ?? throw new ArgumentException("缺少 path");
        Window w = target == "main" ? Win : EditorWindows.Shared.Find(target) ?? throw new ArgumentException("没有这个窗口：" + target);
        var hwnd = new WindowInteropHelper(w).Handle;
        WebNative.GetWindowRect(hwnd, out var rc);
        int width = rc.Right - rc.Left, height = rc.Bottom - rc.Top;
        using var bmp = new System.Drawing.Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            WebNative.PrintWindow(hwnd, hdc, 2);
            g.ReleaseHdc(hdc);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
        return new JObject { ["path"] = path, ["width"] = width, ["height"] = height };
    }

    /// <summary>照 Mac 启动页的「添加命令」对话框排一个，看代码输入框（真正的对话框由启动页负责人做）。</summary>
    private static void AddCommandDemo(string workspaceId)
    {
        const string id = "codeinput-demo";
        void Close() => Dialogs.Dismiss(id);
        var name = new State<string>("");
        var cwd = new State<string>("");
        var command = new State<string>("");
        var projects = AppModel.Shared.Data.ProjectsIn(workspaceId);
        var projectId = new State<string>(projects.FirstOrDefault()?.Id ?? "");
        UIElement Row(string title, UIElement content) => HStack(M.Md, Text(title).W(M.FormLabelWidth).Shrink(false), content.Flex());
        var code = new PPCodeInput(command, L.T("launch.addCommand.commandPlaceholder"), "shell", 220, onSubmit: Close, onEscape: Close);
        var label = HStack(M.Xs, Text(L.T("launch.addCommand.command")), Text("*", Typo.Body, ColorToken.Danger), new PPInfoButton(L.T("launch.addCommand.commandInfo")));
        var commandRow = HStack(M.Md, new System.Windows.Controls.Border { Child = label, Width = M.FormLabelWidth, Padding = new Thickness(0, 6, 0, 0) }, code.Flex());
        commandRow.Align = CrossAlign.Start;
        var content = VStack(M.Md,
            Row(L.T("common.name"), new PPTextField(L.T("launch.addCommand.namePlaceholder"), name)),
            Row(L.T("launch.addCommand.project"), new PPDropdown<string>(projects.Select(p => new DropdownOption<string>(p.Id, p.Name, detail: Format.AbbreviatePath(p.Path))), projectId, fullWidth: true)),
            Row(L.T("launch.addCommand.cwd"), HStack(M.Sm, new PPTextField(L.T("launch.addCommand.cwdPlaceholder"), cwd, mono: true).Flex(), new PPButton(L.T("form.browse")))),
            commandRow);
        content.Align = CrossAlign.Stretch;
        var cancel = new PPButton(L.T("common.cancel"), Close);
        var add = new PPButton(L.T("launch.addCommand.add"), Close, kind: PPButtonKind.Primary);
        var dialog = new PPDialog(L.T("launch.addCommand"), content, new UIElement[] { cancel, add }, M.DialogWidthLarge, Close)
        {
            DefaultButton = add,
            CancelButton = cancel,
        };
        Dialogs.Present(id, dialog);
    }
}

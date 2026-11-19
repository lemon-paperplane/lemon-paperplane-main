using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;

namespace LemonPaperplane.App;

/// <summary>
/// 程序入口（没有 App.xaml，Application 在代码里创建）。
///   LemonPaperplane.exe                       正常启动（单实例：第二次启动时激活已有窗口）
///   LemonPaperplane.exe --gallery             打开组件陈列页
///   LemonPaperplane.exe --gen-icon &lt;ico&gt; [&lt;预览 png&gt;]   生成 App 图标
///   LemonPaperplane.exe --debug-client &lt;管道名&gt; '&lt;json&gt;' …   调试桥客户端（见 DebugBridge）
/// 环境变量 PAPERPLANE_DEBUG_BRIDGE=&lt;管道名&gt; 时开调试桥。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--debug-client") return DebugBridge.RunClient(args[1], args.Skip(2));
        if (args.Length >= 2 && args[0] == "--gen-icon") return IconGen.Run(args[1], args.Length >= 3 ? args[2] : null);

        var bridge = Environment.GetEnvironmentVariable("PAPERPLANE_DEBUG_BRIDGE");
        bridge = string.IsNullOrWhiteSpace(bridge) ? null : bridge!.Trim();

        // 单实例（每个登录会话一个；调试桥实例按管道名区分，不和正常实例互相激活）
        var name = @"Local\LemonPaperplane-" + Environment.UserName + (bridge != null ? "-" + bridge : "");
        using var mutex = new Mutex(true, name, out bool first);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-activate");
        if (!first)
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            activate.Set();
            return 0;
        }

        ServicePointManager.DefaultConnectionLimit = 64;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            DebugBridge.LogError(e.Exception);
            if (bridge != null) e.Handled = true; // 自动化测试时不因为一个异常退出，用 errors 命令查看
        };
        Templates.Install(app);

        var window = new MainWindow();
        window.Closed += (_, _) => app.Shutdown();
        ThreadPool.RegisterWaitForSingleObject(activate, (_, _) =>
            app.Dispatcher.BeginInvoke(new Action(() => MainWindow.Current?.ShowMainWindow())), null, -1, false);

        if (args.Contains("--gallery"))
        {
            Gallery.Show(window);
        }
        else
        {
            // 正式界面：工具栏 / 侧栏 / 页面、连接内核、托盘；关闭窗口只收到托盘，服务继续运行
            MainView.Install(window);
            AppCommands.Register();
            AppModel.Shared.Start();
            Tray.Shared.Install();
            window.HideOnClose = () => true;
            app.Exit += (_, _) => Tray.Shared.Dispose();
        }
        if (bridge != null) DebugBridge.Start(bridge);
        app.Run(window);
        GC.KeepAlive(mutex);
        return 0;
    }
}

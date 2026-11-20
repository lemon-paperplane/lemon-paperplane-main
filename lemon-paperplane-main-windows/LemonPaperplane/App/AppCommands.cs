namespace LemonPaperplane;

/// <summary>
/// 应用命令与快捷键（对应 Mac AppCommands / MainCommands；⌘ → Ctrl，⌥ → Alt）：
/// Ctrl+R 运行、Ctrl+. 停止、Ctrl+F 搜索、Ctrl+, 设置、Ctrl+N 导入文件夹、Ctrl+Shift+N 扫描文件夹、Ctrl+Q 退出界面、Ctrl+Alt+Q 退出并停止全部服务。
/// </summary>
public static class AppCommands
{
    public static void Register()
    {
        Shortcuts.Register("Ctrl+,", OpenSettings);
        Shortcuts.Register("Ctrl+N", ImportFolder);
        Shortcuts.Register("Ctrl+Shift+N", ScanFolder);
        Shortcuts.Register("Ctrl+F", Find);
        Shortcuts.Register("Ctrl+R", Actions.RunCurrent);
        Shortcuts.Register("Ctrl+.", Actions.StopCurrent);
        // 退出 = 界面和本机内核一起退出（内核在跑，托盘就一定有图标，不留看不见的内核）
        Shortcuts.Register("Ctrl+Q", () => AppModel.Shared.Quit());
    }

    public static void OpenSettings()
    {
        MainWindow.Current?.ShowMainWindow();
        AppModel.Shared.Nav.Page = NavPage.SettingsPage;
    }

    public static void ImportFolder()
    {
        MainWindow.Current?.ShowMainWindow();
        ImportFlow.ImportFolder();
    }

    public static void ScanFolder()
    {
        MainWindow.Current?.ShowMainWindow();
        ImportFlow.ScanFolder();
    }

    public static void NewWorkspace()
    {
        MainWindow.Current?.ShowMainWindow();
        Actions.CreateWorkspace();
    }

    /// <summary>Ctrl+F：有可见的控制台就搜控制台，否则聚焦工程搜索框。</summary>
    public static void Find()
    {
        var nav = AppModel.Shared.Nav;
        if (nav.VisibleConsoles > 0) nav.RequestConsoleFind(); else nav.RequestFocusSearch();
    }
}

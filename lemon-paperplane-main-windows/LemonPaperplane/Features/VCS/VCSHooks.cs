namespace LemonPaperplane;

/// <summary>
/// 版本控制模块挂到主界面的入口（为 null 时对应菜单项不显示）。对应 Mac VCSHooks。
/// 默认值在首次访问时初始化（主工具栏打开「导入」菜单时），不需要启动时另行赋值。
/// </summary>
public static class VCSHooks
{
    /// <summary>导入菜单里的「从 Git 克隆…」</summary>
    public static System.Action? CloneAction { get; set; } = VCSCloneDialog.Present;
}

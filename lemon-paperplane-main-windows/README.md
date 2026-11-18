# Lemon Paperplane — Windows

下一轮实现。已定口径：

- WPF + .NET Framework 4.8（Windows 10 1903 起与 Windows 11 系统自带运行库，安装包只有几 MB），C# 新语法通过 `LangVersion=latest` 使用。
- 界面与 macOS 版一致：照 `../docs/design-system.md` 与 `../shared/design-tokens.json` 实现同一套自绘控件；图标用 `../shared/icons/` 的 SVG（WPF 的 Geometry 路径语法与 SVG path 基本一致，写一个子集渲染器）。
- 文案用 `../shared/i18n/*.json`。
- 内核与插件复用 Go 版本：`GOOS=windows` 交叉编译 `paperplane.exe` 与 `paperplane-plugins.exe`；内核里 Windows 相关的待补项：Job Object 管理进程树、toolhelp32 统计进程资源、系统语言读取、askpass 的 .cmd 包装。
- 安装包：Inno Setup 或 MSIX（下一轮定）。

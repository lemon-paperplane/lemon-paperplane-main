# Lemon Paperplane — Windows

柠檬纸飞机的 Windows 界面：WPF + .NET Framework 4.8，和 macOS 版功能、布局、配色、图标、文案一致。内核与插件复用 Go 版本（`GOOS=windows` 交叉编译）。

- 系统要求：Windows 10 1809+ / Windows 11（x64、ARM64）；.NET Framework 4.8（Windows 10 1903 起自带）；编辑窗口、终端需要 WebView2 运行时（Windows 11 自带，缺少时界面里给下载入口）。
- 安装：装到当前用户（`%LOCALAPPDATA%\Programs\Lemon Paperplane`），不需要管理员权限；数据在 `%APPDATA%\LemonPaperplane`。关闭主窗口收到托盘，服务继续运行；托盘菜单「退出并停止全部服务」才全部停止。

## 目录

| 路径 | 内容 |
|---|---|
| `PaperplaneKit/` | 数据与接口层（对应 Mac 的 PaperplaneKit） |
| `PaperplaneKit.Tests/` | 单元测试（自带运行器，`build.ps1 -Test`） |
| `LemonPaperplane/` | 界面：`App`（入口、窗口、调试桥）、`Core`（AppModel、Stores、Actions）、`Design`、`Components`、`Renderers`、`Features/<区域>` |
| `Vendor/` | 第三方库（Newtonsoft.Json、WebView2），随仓库提交 |
| `installer/` | Inno Setup 安装包脚本 |
| `build.ps1` / `package.ps1` | 编译 / 打包 |

开发约定、组件 API、测试方法见 [PORTING.md](PORTING.md) 与 [LemonPaperplane/Components/README.md](LemonPaperplane/Components/README.md)。

## 编译

需要 Visual Studio 2022+ 或 Build Tools（MSBuild），或挂载的 EWDK。没有 .NET Framework 4.8 目标包时 `build.ps1` 自动下载参考程序集。

```powershell
.\build.ps1            # Debug
.\build.ps1 -Test      # 编译并跑测试
.\build.ps1 -Release
```

开发时界面用环境变量 `PAPERPLANE_BIN` 指向 Windows 版内核、`PAPERPLANE_DATA_DIR` 隔离数据目录；`PAPERPLANE_DEBUG_BRIDGE=<管道名>` 打开调试桥（截图、点击、导航，见 `LemonPaperplane/App/DebugBridge.cs`）。

## 发布包

在仓库根目录执行 `build.cmd`（新电脑也可以，依赖自动准备，见[根目录 README](../README.md#打包)），产物在 `../dist/windows/`：
`LemonPaperplane-<版本>-windows-<x64|arm64>-setup.exe` 与 `.zip`。

根目录脚本用 Windows 自带的 MSBuild 4.x 加 NuGet 上的 Roslyn 编译（`build/Common.props` 里对 MSBuild 4.x 的兼容就是为它准备的），
不需要 Visual Studio；`package.ps1` 负责整理安装目录、打 zip 和调用 Inno Setup。

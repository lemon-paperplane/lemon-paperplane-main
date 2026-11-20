# Lemon Paperplane / 柠檬纸飞机

没有编辑器的工程管理器。把 IntelliJ IDEA / WebStorm / PyCharm / GoLand 里「导入即识别、自动装依赖和工具链、一键运行、看日志、报错一键修复、Git 管理」做成一个轻量的原生工具；所有功能同时提供 HTTP API、MCP 工具和命令行，AI 可以直接驱动。

## 下载

[![构建](https://github.com/lemon-paperplane/lemon-paperplane-main/actions/workflows/build.yml/badge.svg)](https://github.com/lemon-paperplane/lemon-paperplane-main/actions/workflows/build.yml)

每次提交到 main 都会自动构建全部安装包，放在 [Releases → 最新构建](https://github.com/lemon-paperplane/lemon-paperplane-main/releases/tag/latest-build)；推送 `v0.2.0` 这样的标签时另发正式版本。

| 系统 | 文件 |
|---|---|
| macOS 13+（Apple 芯片与 Intel） | `LemonPaperplane-<版本>-macos.dmg` |
| Windows 10/11（x64、ARM64） | `LemonPaperplane-<版本>-windows-<架构>-setup.exe` 安装包，或 `.zip` 免安装版 |
| Linux 服务器（无界面，10 个架构） | `paperplane-<版本>-linux-<架构>`，见[部署说明](lemon-paperplane-main-linux-headless/README.md) |

手机版（Android，iOS 与 HarmonyOS 计划中）在 [lemon-paperplane-mobile](https://github.com/lemon-paperplane/lemon-paperplane-mobile)。

## 仓库结构

| 目录 | 说明 |
|---|---|
| `lemon-paperplane-main-core/` | Go 内核：daemon、操作注册表（HTTP / MCP / CLI）、进程托管、诊断修复、工具链、插件宿主 |
| `lemon-paperplane-main-plugin-sdk/` | 插件协议与 Go SDK；`cmd/paperplane-plugin-probe` 是插件开发探针 |
| `lemon-paperplane-main-macos/` | macOS 界面（Swift，macOS 13+） |
| `lemon-paperplane-main-windows/` | Windows 界面（WPF，.NET Framework 4.8，Windows 10 1809+ / 11，x64 与 ARM64） |
| `lemon-paperplane-main-linux-headless/` | Linux 无头版：一个可执行文件（内核 + 全部内置插件），作为远程服务器被 Mac / Windows 客户端管理（[部署说明](lemon-paperplane-main-linux-headless/README.md)） |
| `lemon-paperplane-main-linux/` | Linux 界面（GTK）—— 下一轮 |
| `shared/` | 三端共用：SVG 图标、设计变量、界面文案 |
| `docs/` | [远程服务器](docs/remote.md)、[总体架构](docs/architecture.md)、[插件协议](docs/plugin-protocol.md)、[API](docs/api.md)、[设计规范](docs/design-system.md)、[借鉴来源](docs/references.md) |
| `scripts/` | `ppbuild/`（Go 写的构建工具：内置插件、离线商店、Windows 内核目录、拉取插件源码），`build-*.sh` 是它和根目录构建脚本的入口 |

插件在上一级目录，每个一个文件夹：`../lemon-paperplane-plugin-<id>/`。

## 开发

```bash
# 内核
cd lemon-paperplane-main-core && go build -o bin/paperplane ./cmd/paperplane && go test ./...

# 用源码目录里的插件起一个开发用 daemon（各插件先执行自己的 build.sh）
PAPERPLANE_DEV_PLUGINS=.. ./bin/paperplane daemon

# 命令行
./bin/paperplane import ~/Code/some-project
./bin/paperplane ls
./bin/paperplane run some-project
./bin/paperplane ops
```

环境变量：`PAPERPLANE_DATA_DIR`（数据目录，测试时用临时目录）、`PAPERPLANE_DEV_PLUGINS`（开发插件目录）、`PAPERPLANE_BUNDLED_PLUGINS`、`PAPERPLANE_LOCALE`、`PAPERPLANE_NO_SHELL_ENV`。

## 打包

一条命令出安装包。新电脑也一样：把这个文件夹（最好连同旁边的 `lemon-paperplane-plugin-*`）拷过去直接执行，
缺的依赖脚本自己准备，不需要先装 Xcode、Visual Studio、Homebrew、Go 或 Node.js。

| 系统 | 命令 | 产物 |
|---|---|---|
| macOS 13+ | `./build.sh [版本] [--install]` | `dist/macos/LemonPaperplane-<版本>-macos.dmg`：通用二进制，打开后把图标拖进「应用程序」即可安装 |
| Windows 10/11 | `build.cmd [-Version 版本] [-Arch all\|x64\|arm64]` | `dist\windows\`：x64 与 ARM64 各一个安装包（`-setup.exe`，装到当前用户）和免安装 zip |
| Linux 无头版 | Mac 上 `./build.sh --linux-headless`，或 Windows 上 `build.cmd -LinuxHeadless` | `dist/linux-headless/`：10 个架构各一个可执行文件 |

版本默认取 Xcode 工程的 `MARKETING_VERSION`，两端一致。

自动准备的依赖都放在工具缓存目录（Mac `~/Library/Caches/LemonPaperplane/build-tools`，Windows `%LOCALAPPDATA%\LemonPaperplane\build-tools`），
不改系统设置，第二次构建不再下载；下载源会在官方源和国内镜像之间自动挑最快的，固定版本的下载都校验 SHA-256：

- 两端：Go（版本低于 `go.mod` 要求时）、Node.js（低于 18 时）；旁边没有插件源码时从 GitHub 下载全部插件仓库。
- macOS：Xcode 命令行工具（没有时调用系统安装，要输入本机密码；不需要完整的 Xcode，界面用 SwiftPM 编译）、dmgbuild（DMG 窗口排版）。
- Windows：Roslyn C# 编译器 + .NET Framework 4.8 参考程序集（NuGet），配合 Windows 自带的 MSBuild，不需要 Visual Studio；Inno Setup 6（便携安装）。

签名：Mac 默认 ad-hoc，别的 Mac 首次打开要在「系统设置 → 隐私与安全性」里允许；正式分发设置
`SIGN_IDENTITY="Developer ID Application: …"`，再设置 `NOTARY_PROFILE`（`xcrun notarytool store-credentials` 建的配置名）会自动公证。

在 Mac 上出 Windows 包：`WINDOWS_BUILD_HOST=user@host scripts/build-windows.sh`（把源码传到一台开了 OpenSSH 的 Windows 上执行 `build.cmd`，再取回产物）。

## 接入 AI（MCP）

```bash
claude mcp add paperplane -- "/Applications/Lemon Paperplane.app/Contents/MacOS/paperplane" mcp
```

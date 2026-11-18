# Lemon Paperplane / 柠檬纸飞机

没有编辑器的工程管理器。把 IntelliJ IDEA / WebStorm / PyCharm / GoLand 里「导入即识别、自动装依赖和工具链、一键运行、看日志、报错一键修复、Git 管理」做成一个轻量的原生工具；所有功能同时提供 HTTP API、MCP 工具和命令行，AI 可以直接驱动。

## 仓库结构

| 目录 | 说明 |
|---|---|
| `lemon-paperplane-main-core/` | Go 内核：daemon、操作注册表（HTTP / MCP / CLI）、进程托管、诊断修复、工具链、插件宿主 |
| `lemon-paperplane-main-plugin-sdk/` | 插件协议与 Go SDK；`cmd/paperplane-plugin-probe` 是插件开发探针 |
| `lemon-paperplane-main-macos/` | macOS 界面（Swift，macOS 13+） |
| `lemon-paperplane-main-windows/` | Windows 界面（WPF，.NET Framework 4.8）—— 下一轮 |
| `lemon-paperplane-main-linux/` | Linux 界面（GTK）—— 下一轮 |
| `shared/` | 三端共用：SVG 图标、设计变量、界面文案 |
| `docs/` | [总体架构](docs/architecture.md)、[插件协议](docs/plugin-protocol.md)、[API](docs/api.md)、[设计规范](docs/design-system.md)、[借鉴来源](docs/references.md) |
| `scripts/` | `build-plugins.sh`（内置插件多入口二进制）、`build-macos.sh`（打包 .app） |

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

## 打包 macOS

```bash
scripts/build-macos.sh 0.1.0
```

产物在 `dist/macos/`。

## 接入 AI（MCP）

```bash
claude mcp add paperplane -- "/Applications/Lemon Paperplane.app/Contents/MacOS/paperplane" mcp
```

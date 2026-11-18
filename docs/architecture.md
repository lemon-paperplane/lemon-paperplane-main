# Lemon Paperplane 总体架构

Lemon Paperplane（中文名：柠檬纸飞机，命令行 `paperplane`）是一个**没有编辑器**的工程管理器：把 IntelliJ IDEA / WebStorm / PyCharm / GoLand 里「导入即识别、自动装依赖、一键运行、看日志、诊断修复、Git 管理」这一套拿出来，做成一个轻量的原生桌面工具。目标用户场景是 vibe coding：关心效果，不关心代码。

## 1. 设计原则

1. **一切皆插件**（IDEA 的原则）。内核不认识任何语言，Java、Node、Python、Go、Git 全部由插件提供；内置插件和第三方插件走同一套协议，内置插件只是预装。
2. **一切皆接口**。界面、命令行、MCP 都是同一套「操作」（operation）的不同外壳，界面能做的事 AI 都能做。
3. **不改用户工程**。识别、导入、运行只读用户文件；镜像、JDK 选择、运行参数一律通过环境变量、命令行参数、Gradle init script、临时 settings.xml 注入。会写进工程目录的只有各生态的标准产物（`node_modules`、`.venv`、`build/`、`target/`），且安装依赖不改锁文件（`npm ci`、`yarn --frozen-lockfile`、`uv sync --frozen`）。
4. **轻**。内核是一个 Go 静态二进制，空闲常驻内存目标 < 30 MB；插件按需拉起、闲置 60 秒退出；界面是各平台原生 UI，不带浏览器内核。
5. **三端一致**。Mac（Swift）、Windows（WPF）、Linux（GTK）界面布局、配色、图标、文案完全一致，像 IDEA 一样；设计规范和资源放在 `shared/`，三端共用。

## 2. 进程结构

```
┌──────────────┐   HTTP + WebSocket (127.0.0.1, token)   ┌───────────────────────────┐
│ 原生界面      │ ───────────────────────────────────────▶ │ paperplane daemon (Go)     │
│ Swift/WPF/GTK │                                          │  操作注册表 → REST/MCP/CLI │
└──────────────┘                                          │  工作区/工程存储           │
┌──────────────┐   MCP (stdio 桥 或 Streamable HTTP)       │  进程托管 + 日志 + 诊断     │
│ Claude Code 等│ ───────────────────────────────────────▶ │  工具链管理 + 下载 + 镜像   │
└──────────────┘                                          │  插件宿主                  │
┌──────────────┐   同一 HTTP API                           └─────────────┬─────────────┘
│ paperplane CLI│ ───────────────────────────────────────▶               │ JSON-RPC 2.0 / stdio
└──────────────┘                                    按需拉起、闲置退出   ▼
                                       ┌────────┬────────┬────────┬────────┬─────┐
                                       │ java   │ gradle │ nodejs │ python │ git │ …
                                       └────────┴────────┴────────┴────────┴─────┘
                                                  被运行的用户工程（java / node / python …）
```

- **daemon** 是唯一的状态持有者。界面关掉，daemon 和正在跑的工程继续运行（菜单栏图标可管理）；从菜单「退出并停止全部」才会停。
- 界面启动时读运行时文件（端口 + token），连不上就拉起 daemon（`paperplane daemon --detach`）。
- 用户工程进程由 daemon 托管：独立进程组（Windows 用 Job Object），停止时整组发 SIGTERM，超时再 SIGKILL，保证 `npm → sh → node` 这类进程树不残留。

## 3. 目录布局

```
lemon-paperplane/
├── lemon-paperplane-main/                  基座（本仓库）
│   ├── docs/                                设计文档
│   ├── shared/                              三端共用：SVG 图标、设计变量、界面文案
│   ├── lemon-paperplane-main-core/          Go 内核：daemon + API + MCP + CLI
│   │   └── bundled-plugins/                 把内置插件打成一个多入口二进制（独立 go module）
│   ├── lemon-paperplane-main-plugin-sdk/    插件协议类型 + Go SDK（插件只依赖它）
│   ├── lemon-paperplane-main-macos/         Mac 界面（Swift，macOS 13+）
│   ├── lemon-paperplane-main-windows/       Windows 界面（WPF，.NET Framework 4.8）
│   ├── lemon-paperplane-main-linux/         Linux 界面（GTK）
│   └── scripts/                             构建打包脚本
├── lemon-paperplane-plugin-java/            每个插件一个文件夹、一个仓库
├── lemon-paperplane-plugin-gradle/
└── …
```

运行时数据目录（下称 `$DATA`）：

| 平台 | 路径 |
|---|---|
| macOS | `~/Library/Application Support/LemonPaperplane/` |
| Windows | `%APPDATA%\LemonPaperplane\` |
| Linux | `$XDG_CONFIG_HOME/lemon-paperplane/`（默认 `~/.config/lemon-paperplane/`） |

```
$DATA/
├── runtime.json          daemon 端口、token、pid（权限 0600）
├── settings.json         全局设置
├── workspaces.json       工作区列表与顺序
├── projects/<id>.json    每个工程一份（模块、识别结果、运行配置、工程级设置）
├── toolchains/<kind>/…   Paperplane 下载的 JDK / Node / Go / Python 等
├── plugins/<id>/…        从商店或本地安装的插件（覆盖同 id 内置插件）
├── plugin-data/<id>/     插件私有数据
├── cache/                下载缓存
└── logs/                 daemon.log、runs/<runId>.log
```

## 4. 插件划分

和 IDEA 一样按「语言 / 构建系统 / 框架 / 版本控制」细分，框架插件依赖语言或构建插件。

| 插件 | 依赖 | 提供 |
|---|---|---|
| java | – | 工具链 `jdk`（发现/下载 Temurin）、Java facet、`java-application` 运行配置、`java.buildCommand` 服务（argfile/classpath jar 处理） |
| gradle | java | Gradle facet、模型同步（init script，不改工程）、`gradle-task` 运行配置、`gradle.prepareClasspath` 服务、Gradle JVM 选择、发行包镜像预下载、仓库镜像 |
| maven | java | Maven facet、reactor 解析、`maven-goal` 运行配置、`maven.prepareClasspath` 服务、临时 settings.xml 镜像 |
| kotlin | java | Kotlin facet、顶层 `fun main` 识别、`kotlin-application` 运行配置 |
| spring-boot | java（可选 gradle/maven/kotlin） | Spring Boot facet、`@SpringBootApplication` 识别、profile 选择、端口/URL 识别、FailureAnalyzer 报错解析 |
| nodejs | – | 工具链 `node`（nvm/fnm/volta/Homebrew 发现、下载）、npm/yarn/pnpm/bun、package.json 脚本运行配置、依赖安装、Node 版本推断 |
| vue | nodejs | Vue/Vite/Vue CLI/Nuxt 识别、默认脚本、旧 webpack 与新 Node 兼容规则 |
| react | nodejs | React/Next/CRA/Vite React 识别与规则 |
| uniapp | nodejs | uni-app CLI 工程（Vue2/Vue3）、HBuilderX 工程识别、H5 运行、小程序编译 + 打开开发者工具 |
| python | – | 工具链 `python`（uv/pyenv/conda/系统发现，uv 下载）、venv、uv/pip/poetry/pdm/pipenv/conda、脚本运行配置 |
| django | python | manage.py runserver、迁移提示 |
| fastapi | python | `FastAPI()` 识别、uvicorn 运行配置 |
| flask | python | `Flask(__name__)` 识别、flask run |
| go | – | 工具链 `go`、go.mod/go.work、main 包识别、构建后运行、GOPROXY |
| git | – | VCS 提供者：状态、提交、推送、拉取、分支、日志图、暂存、冲突处理、只读差异 |

以后加 SVN、Docker Compose、PHP、Rust、.NET，都是新建一个 `lemon-paperplane-plugin-xxx` 文件夹。

## 5. 核心数据模型

- **工作区 Workspace**：`{id, name, projectIds[]}`。一个工程只属于一个工作区；不同工作区的工程可以同时运行。
- **工程 Project**：用户导入的一个文件夹。`{id, name, path, workspaceId, modules[], settings, vcs}`。
- **模块 Module**：工程里一个被插件认出来的目录（工程根本身也可以是模块）。`{id, relDir, name, facets[], models{plugin→JSON}, runConfigurations[], requirements[], dependencies}`。
  - Gradle/Maven 根会「认领」子工程目录，Node workspace 会认领成员目录，被认领的目录不再单独成模块。
  - 例：`vrtalk-api` 是 1 个模块（Gradle 多模块由 gradle 插件内部表达）；`vrtalk-play-app` 有 Android（gradle）、core-ui（node）等多个模块。
- **Facet**（沿用 IDEA 术语）：模块的技术特征，如 `java`、`gradle`、`spring-boot`、`nodejs`、`vue`。界面上的技术徽标就是 facet。
- **工具链 Toolchain**（IDEA 叫 SDK）：`{id, kind, version, vendor, path, env, pathPrepend, source, managed}`。kind 有 `jdk`、`node`、`python`、`go`、`gradle`、`maven`。
- **运行配置 RunConfiguration**：`{id, name, type, plugin, moduleId, options{}, env{}, source}`。`source=auto` 的由插件生成、重新导入会刷新；用户改过即变 `user`，不再被覆盖。还能从 `.idea/workspace.xml`、`.run/*.run.xml`、`.vscode/launch.json` 导入（`source=idea|vscode`）。
- **运行实例 Run**：`{id, projectId, runConfigId, state, pid, startedAt, exitCode, urls[], ports[], stats{cpu, rssMB}, diagnoses[]}`。state：`preparing → starting → running → stopping → exited|failed`。
- **任务 Task**：导入、同步、装依赖、下载工具链、Git 推送等耗时操作。`{id, kind, title, state, progress, logs}`，可取消。

## 6. 导入流水线

1. **扫描**：从工程根向下最多 4 层，跳过 `node_modules .git build dist target out .gradle .idea .venv venv __pycache__ Pods DerivedData` 等目录，收集每个目录的文件名。
2. **激活**：插件 manifest 里声明 `activation.files`（文件名 glob），目录里有匹配文件才问这个插件。没有匹配的插件根本不会被拉起。
3. **识别 detect**（廉价、只看文件）：按插件依赖拓扑序调用，返回 facets 和 claims。
4. **分析 analyze**（静态解析，秒级）：返回工具链需求、运行配置候选、依赖状态、关键信息（如「Gradle 8.12 · Java 17 · Spring Boot 3.5」）。依赖插件能看到被依赖插件的分析结果。
5. **选工具链**：内核按需求约束在已发现的工具链里选（工程设置优先 → 精确匹配 preferred → 满足约束的最高版本 → 同架构优先）。缺的标记为待下载；设置里「自动下载缺失工具链」默认开。
6. **同步 sync**（可选，重，带进度）：例如 Gradle 用 init script 拉模型、Maven 解析 reactor。需要已选好的工具链。
7. **依赖安装**：Node/Python/Go 检测依赖未装时可一键安装；首次运行时若依赖缺失自动安装。

## 7. 运行流水线

1. 合并环境变量：系统环境 → 工具链 env/PATH → 镜像 env → 工程 env → 运行配置 env。
2. 调插件 `run/prepare`。插件可以通过 `host/exec` 把编译、类路径解析等前置步骤的输出直接写进这次运行的控制台（相当于 IDEA 的 Before launch），最后返回主进程 `LaunchSpec`。
3. 内核启动主进程并托管：日志进内存环形缓冲（每个运行最多 20000 行）+ 落盘；按行做诊断匹配和 URL 识别；每 2 秒统计进程树 CPU/内存；识别监听端口。
4. 就绪判定：命中插件声明的 ready/URL 规则即 `running`；没有规则的，进程存活 3 秒视为 `running`。
5. 停止：SIGTERM（或插件指定的信号）给整个进程组，超时 8 秒 SIGKILL。
6. 默认运行配置：用户指定的一直保留；否则每次分析后自动打分重选——从 IDEA/VS Code 导入或用户新建的 > 插件标记默认 > 框架插件生成的 > 所在模块用了框架（是应用而不是脚本目录）> 目录层级浅。
7. 构建文件（各插件激活文件、锁文件、版本文件）变化时自动重新分析；重型同步（Gradle/Maven）不会自动跑，只标记需要同步，下次运行前自动同步。

Java 系运行方式参考 IDEA：用 Gradle/Maven 编译并导出运行时类路径，然后**直接**用 `java` 启动主类（JDK 9+ 用 `@argfile`，JDK 8 用 classpath jar），这样 Gradle daemon 可以闲置回收、重启更快、JVM 参数可控；同时保留「通过 bootRun / spring-boot:run 启动」的备选模式。

## 8. 诊断与修复

- 插件在 manifest 里声明规则：`hint`（字面量预筛，避免每行跑正则）+ `pattern`（正则）+ 标题/说明 + 修复项。内核逐行匹配，命中生成 Diagnosis，同一规则只报一次。
- 进程非零退出时，额外调插件 `diagnose`（可选）做复杂分析，比如 Spring Boot 的 `APPLICATION FAILED TO START` 段落解析。
- 修复项是**声明式的工程设置变更**，内核通用执行，不改用户文件：

| kind | 作用 | 参数 |
|---|---|---|
| `setToolchain` | 给工程指定工具链版本（缺就下载） | `kind`, `constraint` |
| `setEnv` / `appendEnv` / `unsetEnv` | 改工程或运行配置的环境变量 | `key`, `value`, `scope` |
| `setRunOption` | 改运行配置的某个选项 | `key`, `value` |
| `installDependencies` | 安装依赖 | – |
| `killPort` | 结束占用端口的进程（需确认） | `port` |
| `reimport` | 重新识别/同步 | – |
| `restart` | 重启运行 | – |
| `openUrl` | 打开说明链接 | `url` |
| `custom` | 交给插件 `fix/apply` 处理 | 插件自定义 |

- 修复后可自动重跑。规则库解决不了的，通过 MCP 让外部 AI 读日志、改设置、重启。

## 9. 工具链与镜像

- 发现：每个工具链插件扫描已知安装位置（例如 JDK：`/usr/libexec/java_home`、`~/Library/Java/JavaVirtualMachines`、`~/.sdkman`、`~/.gradle/jdks`、`~/.jdks`、Homebrew；Node：nvm/fnm/volta/n/Homebrew），只读不动。
- 下载：插件返回下载计划（URL、镜像 URL、sha256、解压规则），内核统一下载、校验、解压到 `$DATA/toolchains/`；Python 由插件调用 uv 安装。
- 镜像区域 `auto | china | global`。`auto` 启动时测速（缓存 24 小时）。镜像通过 `initialize` 传给插件，插件只用环境变量/命令行注入：

| 用途 | 国内镜像 |
|---|---|
| npm registry | `https://registry.npmmirror.com` |
| Node 发行包 | `https://cdn.npmmirror.com/binaries/node` |
| Maven 中央仓库 | `https://maven.aliyun.com/repository/public` |
| Gradle 插件门户 | `https://maven.aliyun.com/repository/gradle-plugin` |
| Gradle 发行包 | `https://mirrors.cloud.tencent.com/gradle` |
| PyPI | `https://mirrors.aliyun.com/pypi/simple/` |
| Python 发行包（uv） | `https://registry.npmmirror.com/-/binary/python-build-standalone` |
| Go 模块代理 | `https://goproxy.cn,direct` |
| Go 发行包 | `https://mirrors.aliyun.com/golang` |
| JDK（Temurin） | `https://mirrors.tuna.tsinghua.edu.cn/Adoptium` |

## 10. API / MCP / CLI

内核里每个功能是一条「操作」：名字（如 `project.run`）、输入输出 JSON Schema、是否只读/是否破坏性、处理函数。由同一张注册表生成：

- HTTP：`POST /api/v1/call/<op>`，`GET /api/v1/ops` 列出全部操作和 schema，`GET /api/v1/events` 为 WebSocket 事件流。
- MCP：Streamable HTTP 挂在 `/mcp`；`paperplane mcp` 是 stdio 桥（给 Claude Code / Claude Desktop 用，自动读 token）。工具名是操作名把 `.` 换成 `_`。
- CLI：`paperplane call <op> '<json>'` 通用调用，外加 `ls`、`import`、`run`、`stop`、`logs -f` 等快捷命令。

安全：只监听 127.0.0.1；所有请求必须带 `Authorization: Bearer <token>`；校验 Host 头只能是 `127.0.0.1:<port>` / `localhost:<port>`；拒绝带非本应用 Origin 的浏览器请求，防 DNS rebinding 和跨站调用。

详见 [api.md](api.md)、[plugin-protocol.md](plugin-protocol.md)、[design-system.md](design-system.md)。

## 11. 插件商店

- 包格式：zip，内含 `plugin.json`、`icons/`、`l10n/`、`bin/<os>-<arch>/<可执行文件>`；纯声明插件（只有编辑器着色/快捷键/配色文件）没有 bin，一个包通吃所有平台。
- 索引：一个静态 JSON（可放 GitHub Releases / 对象存储 / CDN），列出插件、版本、引擎兼容范围（对应 IDEA 的 since-build/until-build）、各平台下载地址与 sha256。
- 内置插件随安装包预装；商店装的同 id 新版本覆盖内置版本（IDEA 的做法）；可禁用、卸载、回退到内置版本。
- 第一版：本地 zip 安装 + 索引客户端；索引托管放到后续。

## 12. 借鉴来源

见 [references.md](references.md)。

# 借鉴来源

设计与实现站在下列项目的肩膀上。直接取用代码/资源的列出许可证；只借鉴思路的列出借鉴点。

## 直接使用的资源

| 项目 | 许可证 | 用在哪 |
|---|---|---|
| [Lucide](https://lucide.dev)（lucide-static） | ISC | `shared/icons/` 界面图标，许可证见 `shared/icons/LICENSE-lucide.txt` |
| [Simple Icons](https://simpleicons.org) | CC0-1.0 | 各插件 `icons/` 下的技术品牌图标（品牌商标归各自所有，仅用于标示兼容性） |

## 借鉴设计与逻辑

| 项目 | 许可证 | 借鉴点 |
|---|---|---|
| IntelliJ Platform（IntelliJ IDEA Community） | Apache-2.0 | 「一切皆插件」；插件描述符的依赖/可选依赖、since/until 兼容范围；extension point；Facet、SDK、Run Configuration、Before launch、Gradle JVM 的选择逻辑；通过 init script 向 Gradle 注入而不改工程；git4idea 直接调用原生 git；VCS 无关的变更/日志界面 + VCS 提供者；IDE 运行配置格式（`.idea/workspace.xml`、`.run/*.run.xml`）的导入 |
| JetBrains Marketplace | – | 插件商店：索引 + 版本兼容范围 + 内置插件可被商店新版覆盖 |
| Visual Studio Code | MIT | 扩展进程隔离；`activationEvents`（按工程文件懒激活插件）；`contributes` 声明式贡献点与 `package.nls.json` 式本地化；problem matcher 的「正则匹配日志」思路；`launch.json` 的导入；图形界面进程从登录 shell 读取环境变量 |
| Language Server Protocol | – | JSON-RPC 2.0 + Content-Length 帧、`$/cancelRequest` 取消、双向请求 |
| HashiCorp go-plugin / Terraform providers | MPL-2.0 | 插件即独立可执行文件、宿主按需拉起 |
| Zed extensions | Apache-2.0 | 扩展 = 声明式清单 + 少量逻辑；扩展可以贡献界面元素 |
| [Railpack](https://github.com/railwayapp/railpack)（Railway） | MIT | Node 包管理器/workspace 识别、Python 包管理器与框架识别、Go/Java 工程识别的规则（插件实现时参考） |
| Cloud Native Buildpacks / Paketo | Apache-2.0 | detect → build 两阶段；每个构建包只关心自己的生态 |
| mise / asdf / nvm / SDKMAN | MIT 等 | `.nvmrc`、`.node-version`、`.tool-versions`、`.python-version`、`.sdkmanrc` 的版本文件约定；按约束选版本 |
| Gradle Toolchains / foojay Disco API / Adoptium API | Apache-2.0 | JDK 发现与下载元数据 |
| uv（Astral） | MIT/Apache-2.0 | Python 解释器下载与虚拟环境、依赖安装都交给 uv |
| lazygit | MIT | git 命令层与提交图的布局算法思路 |
| Model Context Protocol | MIT | MCP 规范（Streamable HTTP + stdio），内核自行实现 tools 子集 |
| Docker Desktop / OrbStack | – | 原生壳 + 后台 daemon + 菜单栏常驻的产品形态 |

## 镜像

国内镜像来自 npmmirror（阿里）、阿里云 Maven/PyPI/Go、腾讯云 Gradle、清华 TUNA Adoptium、goproxy.cn（七牛）。仅在镜像区域为「国内」时通过环境变量/命令行参数注入，不改用户工程文件。

# 远程服务器

客户端（Mac / Windows 界面）除了管理本机，还能连到其他「服务器」，像用本机一样管理那台机器上的工程、运行、终端、文件、插件和设置。

- **服务器** = 一个柠檬纸飞机内核（`paperplane daemon`）。本机界面自带的内核就是「本机」这台服务器。
- 服务器可以是：开着「允许其他设备连接本机」的 Mac / Windows，或 Linux 无头版（`lemon-paperplane-main-linux-headless`，单个可执行文件，没有界面）。
- 三端共用一份 Go 内核，平台相关的代码按 `_darwin.go` / `_windows.go` / `_linux.go` 分开。

## 1. 架构

```
 客户端电脑                                                    服务器
┌──────────────────────────────────────────┐            ┌────────────────────────────┐
│ 界面（Swift / WPF）                        │            │ 内核                        │
│   │ http://127.0.0.1:<网关端口> + 本机令牌   │            │  远程监听 https://0.0.0.0:47290│
│   ▼                                       │   TLS 1.2+ │   ├ 证书：自签（固定指纹）或 CA │
│ 本机内核                                   │ ─────────▶ │   ├ 认证：访问密钥（Bearer）   │
│   └ 远程网关（每台服务器一个回环端口）         │  固定指纹   │   ├ 限速 / 封 IP / 白名单     │
│       · 校验服务器证书指纹                   │  + 密钥     │   └ 审计日志                  │
│       · 请求加上访问密钥，原样转发（含 SSE）   │            │  同一套 API：操作、事件流、     │
│       · /editor/ 用本机的编辑器网页           │            │  控制台、终端、插件文件、MCP     │
│       · 端口转发：本机端口 ⇄ 服务器 localhost │            │  /api/v1/tunnel（端口转发）    │
└──────────────────────────────────────────┘            └────────────────────────────┘
```

为什么走本机内核的网关，而不是界面直接连服务器：

- 界面、编辑器网页、终端网页、插件图标现在都是用「`http://127.0.0.1:端口` + 令牌」访问内核。网关给每台服务器开一个同样形式的回环端口，界面切换服务器只是换了端口和令牌，其余代码不变。
- TLS、证书指纹校验、访问密钥、端口转发只在 Go 里实现一次，Mac 和 Windows 共用；WKWebView / WebView2 不用处理自签证书。
- 插件装在服务器上。插件界面本来就是内核下发的描述数据（`view.render`），图标和网页资源经 `/plugins/<id>/…` 从服务器取，所以插件界面远程同样能用。参考了 VS Code Remote 的做法：界面在本地，扩展和进程在远端。

## 2. 安全

远程端口可能暴露在公网，而能调接口就等于能在那台机器上执行任意命令，所以按「SSH 级别」来设计（参考 Docker 远程 API 强制 mTLS、Syncthing 的证书指纹、Home Assistant 的长期令牌）：

| 措施 | 做法 |
|---|---|
| 默认关闭 | 远程监听默认不开；本机回环接口不受影响 |
| 只走 TLS | 强制 TLS 1.2+（优先 1.3）。第一次打开远程或生成访问密钥时生成 ECDSA P-256 自签证书（私钥 0600；只看设置页不会生成）；也可以换成自己的证书（Let's Encrypt 等）。没有明文 HTTP 选项 |
| 证书指纹固定 | 指纹 = 证书公钥（SPKI）的 SHA-256。连接码里带指纹，客户端只认这个指纹；账号密码登录时第一次展示指纹让用户核对（和 SSH 第一次连接一样）。CA 签发的证书按正常证书链校验 |
| 一切都要认证 | 除 `GET /api/v1/auth/hello`（只回版本、系统和是否允许密码登录）和 `POST /api/v1/auth/login` 外，全部接口（含事件流、终端流、插件文件、MCP、端口转发）都要 `Authorization: Bearer <访问密钥>`。不用 Cookie，网页无法跨站伪造请求；带 `Origin` 头的请求一律拒绝 |
| 访问密钥 | `ppk_<编号>_<256 位随机>`，服务器只存 SHA-256，按编号查找后恒定时间比较。每台设备一条，可单独吊销，记录最后使用时间和 IP。吊销（或删除账号）时，这条密钥上已经建立的事件流、终端流、端口转发隧道立即切断 |
| 账号密码 | 只存 Argon2id 哈希（PHC 字符串）。登录成功后发给这台设备一条专用密钥，客户端不保存密码。用户不存在时同样计算一次哈希，防止枚举用户名。密码至少 10 位。不支持命令行参数传密码（会出现在进程列表和历史记录里） |
| 防暴力破解 | 每个 IP：15 分钟内认证失败 5 次封禁 15 分钟（再犯翻倍，最长 24 小时）；全局登录每分钟最多 30 次；封禁期间回 429 |
| IP 白名单 | 可选，CIDR 列表；不在名单里直接断开 |
| 连接限制 | 请求头 10 秒超时、空闲 2 分钟断开、请求头上限 64 KB、请求体上限 32 MB |
| 审计日志 | `$DATA/remote/audit.log`（JSON 行）：登录成功/失败、封禁、密钥增删、账号增删、配置变更、远程调用的写操作（打开终端、运行、写文件…，只记操作名不记参数） |
| 远程不能调的操作 | `gateway.*`（客户端功能）、`system.shutdown`（防止界面退出时把无头服务器停掉）、`mcp.config`（会带出服务器的本机令牌） |
| 本机令牌不出机器 | 服务器自己的回环令牌绝不发给远程客户端：远程调用 `view.render` 时 webview 地址只给相对路径（`/plugins/<id>/…?projectId=…`，不带 token），客户端拼上自己网关的地址和令牌 |
| 运行身份 | 建议用普通用户运行（Linux 无头版以 root 运行时启动日志告警）；systemd 示例带 `NoNewPrivileges` 等加固 |
| 客户端保存 | 服务器列表存在界面偏好里；访问密钥存在钥匙串（Mac）/ Windows 凭据管理器，不落普通文件 |

## 3. 服务器端

数据目录下 `remote/`：

| 文件 | 内容 |
|---|---|
| `config.json` | `{"enabled", "listen", "passwordLogin", "autostart", "allowCIDRs", "tlsCert", "tlsKey"}`，可手改；Linux 无头版也可以在这里写 `"users": [{"name", "password"}]`，启动时自动换成哈希并改写文件 |
| `credentials.json`（0600） | 访问密钥（哈希）与账号（Argon2id 哈希） |
| `tls/cert.pem`、`tls/key.pem`（0600） | 自签证书 |
| `audit.log` | 审计日志 |

默认监听 `0.0.0.0:47290`。

命令行（三端内核都有，Linux 无头版主要靠它）：

```
paperplane serve [--listen 0.0.0.0:47290]      前台运行并打开远程监听（systemd 用）
paperplane server info                         地址、证书指纹、密钥与账号数
paperplane server key add <名称> [--address 域名或IP]   生成访问密钥，打印连接码（只显示这一次）
paperplane server key list | revoke <编号>
paperplane server user set <账号>               交互输入密码（--stdin 从标准输入读）
paperplane server user list | remove <账号>
paperplane server enable | disable
```

内核在跑时这些命令经本机回环接口交给内核执行；没在跑时直接改数据目录里的文件。

**连接码**：`ppc1_` + base64url(JSON `{"u":["https://地址:端口",…],"f":"sha256:…","k":"ppk_…","n":"服务器名"}`)。`u` 是服务器猜到的所有可用地址（各网卡 IP、主机名，加上 `--address` 指定的），客户端按顺序试；对话框里可以改地址。

## 4. 接口

### 4.1 远程监听上的 HTTP 端点

| 端点 | 认证 | 说明 |
|---|---|---|
| `GET /api/v1/auth/hello` | 否 | `{"name","hostname","version","apiVersion":1,"os","arch","passwordLogin"}` |
| `POST /api/v1/auth/login` | 否（限速） | `{"user","password","device"}` → `{"token","keyId"}` |
| `GET /api/v1/tunnel?port=N` | 是 | `Connection: Upgrade`、`Upgrade: paperplane-tunnel`，回 101 后是到服务器 `localhost:N` 的原始 TCP 流（只能连服务器本机端口） |
| 其余 `/api/v1/*`、`/plugins/*`、`/mcp` | 是 | 和本机接口相同 |

### 4.2 操作（`POST /api/v1/call/<名称>`）

**服务器端**（在哪台服务器上调就管哪台，本机和远程都能调）：

| 操作 | 参数 | 返回 |
|---|---|---|
| `remote.status` | — | `RemoteStatus` |
| `remote.configure` | `{enabled?, port?, passwordLogin?, autostart?, allowCIDRs?}` | `RemoteStatus` |
| `remote.keys` | — | `[RemoteKey]` |
| `remote.keyCreate` | `{name, address?}` | `{key: RemoteKey, token, code}`（token、code 只返回这一次） |
| `remote.keyRevoke` | `{id}` | — |
| `remote.users` | — | `[{name, createdAt, updatedAt}]` |
| `remote.userSet` | `{name, password}` | —（新建或改密码） |
| `remote.userRemove` | `{name}` | —（同时吊销这个账号登录过的设备） |
| `remote.audit` | `{limit?}` | `[{time, event, ip, who, detail}]`，新的在前 |

```jsonc
// RemoteStatus
{
  "enabled": true, "running": true, "error": "",          // error：端口被占用等
  "listen": "0.0.0.0:47290", "port": 47290,
  "addresses": ["https://192.168.1.156:47290", "https://my-mac.local:47290"],
  "fingerprint": "sha256:ab12…", "fingerprintDisplay": "AB12 CD34 …（16 组）",
  "customCert": false,
  "passwordLogin": true,
  "autostart": false, "autostartSupported": true,          // Linux 无头版用 systemd，autostartSupported=false
  "allowCIDRs": [],
  "keys": 2, "users": 1
}
// RemoteKey
{ "id": "1a2b3c4d", "name": "MacBook", "kind": "key" /* key=手动生成，device=账号密码登录发的 */,
  "user": "", "createdAt": "…", "lastUsedAt": "…", "lastIP": "192.168.1.156", "current": false /* 是不是当前请求用的这条 */ }
```

**客户端网关**（只能在本机调，远程调用会被拒绝）：

| 操作 | 参数 | 返回 |
|---|---|---|
| `gateway.decode` | `{code}` | `{urls, fingerprint, token, name}` |
| `gateway.probe` | `{address}`（`host`、`host:port`、`https://…` 都行） | `{url, fingerprint, fingerprintDisplay, trusted, hello}`；`trusted`=证书由受信任的 CA 签发 |
| `gateway.login` | `{url, fingerprint, trusted, user, password, device}` | `{token}` |
| `gateway.open` | `{url, fingerprint, trusted, token}` | `{id, port, token, info}`：`port`/`token` 用法和本机 runtime.json 完全一样；`info` 是服务器的 `system.info`。同一 url+token 重复调用复用同一个网关。`trusted`=probe 时证书受 CA 信任（此时按证书链校验，不看指纹） |
| `gateway.close` | `{id}` | — |
| `gateway.forward` | `{id, port}` | `{localPort, url}`：把服务器 `localhost:port` 转到本机，优先用同一个端口号；已转发则直接返回 |
| `gateway.unforward` | `{id, port}` | — |

`gateway.open` / `gateway.probe` / `gateway.login` 的错误码：`unreachable`（连不上，message 是原因）、`fingerprint_mismatch`（`data.fingerprint` 是服务器现在的指纹）、`unauthorized`（密钥失效/账号密码错）、`rate_limited`、`incompatible`（`apiVersion` 不同）、`password_disabled`（服务器没开账号密码登录）。

**经网关调用时**：服务器拒绝了访问密钥（被吊销）→ 网关回 **403** + `remote_unauthorized`（不是 401，免得界面当成本机令牌失效去重启本机内核）；服务器连不上 → **502** + `unreachable` / `fingerprint_mismatch`。界面收到这几种时进入「连不上 X」状态，提示重新登录或重试。

**其他新增 / 变化**：

| 操作 | 说明 |
|---|---|
| `fs.mkdir` | `{path}` → `{path}`：在绝对路径新建文件夹（上一级必须存在），远程选择文件夹的「新建文件夹」用 |
| `fs.browse` | `{path?}` → `{path, parent, home, sep, roots, entries:[{name, path, dir, hidden, size, modTime}]}`。不限于工程目录，给「选择服务器上的文件夹」用；`path` 为空时是用户主目录；`roots` 是 Windows 的盘符列表 |
| `system.info` | 新增 `hostname`、`home`、`pathSeparator`、`apiVersion` |
| `runconfig.setRemoteLinks` | `{projectId, configId, forward, direct}`：至少一个为 true；两个都 true 等于默认（删掉记录）。存在工程上（重新识别不会丢），`project.list` 的工程摘要和 `project.get` 的工程里都有 `remoteLinks: {配置id: {forward, direct}}`，没有记录的配置按两个都 true |

## 5. 界面（Mac 与 Windows 一致）

### 5.1 服务器切换器（窗口左上角，替换原来的工作区标题）

- 侧栏开关按钮右边：系统图标 + 当前服务器名 + `⌄`。
- 点开是弹出面板：每台服务器一行（系统图标、名称、第二行「主机名 · 系统」或「地址 · 状态」、右侧运行中数量与状态点），当前那台打勾；「本机」永远在第一行。底部「添加服务器…」「管理服务器…」（打开 设置 → 服务器）。
- 切换：本机内核 `gateway.open` → 用返回的端口和令牌重新连接，所有数据重新加载；记住上次选的服务器，下次启动自动连。连不上时内容区显示「连不上 X」+ 原因 + 重试 / 切回本机。
- 本机内核本身没连上时不能切到远程（网关在本机内核里）。

### 5.2 添加服务器对话框

- 顶部分段：「连接码」|「账号密码」。
- 连接码：名称（默认取连接码里的服务器名）、连接码粘贴框（`i` 说明在哪生成）。粘贴后显示解析出的地址（可改）。连接 = `gateway.decode` → 依次 `gateway.open` 试地址。
- 账号密码：名称、地址、账号、密码。连接 = `gateway.probe` → 不是 CA 证书时弹出「确认服务器证书」展示指纹 → `gateway.login`（device = 本机名）→ `gateway.open`。
- 成功后保存：`{id, name, url, fingerprint, trusted, os, hostname}` 进偏好，密钥进钥匙串 / 凭据管理器，并切到这台。

### 5.3 设置 → 服务器（新分组）

1. **已添加的服务器**：每行 系统图标、名称、地址、指纹前 8 位；`···` 菜单：连接、重命名、重新登录（换密钥）、删除（删除时只删本机记录和密钥）。
2. **允许其他设备连接本机**（连着远程服务器时，标题改为「允许其他设备连接 <服务器名>」，管理的是那台服务器）：
   - 开关；端口；开机自动启动后台服务（只有 `autostartSupported` 时显示）。
   - 地址（每条可复制）、证书指纹（可复制）。
   - 所有 `remote.*` 操作都是 `internal`（不给 MCP / AI 用）。
   - 访问密钥：列表（名称、创建时间、最后使用时间与 IP）+「生成密钥」→ 对话框只显示一次连接码（复制按钮，提示它等同于登录凭据）；每条可吊销。
   - 账号密码登录：开关；账号列表（设置密码、删除）。
   - 已登录的设备：`kind=device` 的密钥（账号、设备名、最后使用），可吊销；当前设备标「本设备」。
   - 安全日志：最近 50 条。

### 5.4 连着远程服务器时界面上的差别

| 地方 | 做法 |
|---|---|
| 选文件夹 / 文件（导入、克隆目标、工具链目录、表单里的路径字段…） | 不弹系统对话框，弹「选择服务器上的文件夹」（`fs.browse`）：路径栏、上一级、主目录、盘符（Windows 服务器）、文件夹列表、新建文件夹 |
| 拖文件夹进窗口导入 | 不接受（本机路径在服务器上不存在），给提示 |
| 在访达 / 资源管理器中显示、用其他应用打开（`project.open`）、安装命令行工具 | 隐藏 |
| 运行中服务的地址 | 按运行配置的 `remoteLinks` 显示两个链接：`⇄ localhost:本机端口`（点开先 `gateway.forward`）和 `◫ 服务器地址:端口`；运行配置编辑里有「远程访问方式」两个勾选（端口转发 / 服务器地址），改动调 `runconfig.setRemoteLinks` |
| 退出 | 「退出并停止全部服务」只停本机；远程服务器的服务不受界面退出影响 |
| 路径 | 一律按服务器的 `pathSeparator` 处理，不用本机的路径函数拼服务器路径（Mac 连 Windows 服务器时是 `C:\…`） |
| 托盘 / 菜单栏 | 显示当前服务器的运行；标题带服务器名 |
| 编辑窗口、终端 | 不用改：网关端口上 `/editor/` 是本机的编辑器网页，接口转发到服务器 |

## 6. Linux 无头版

- 目录 `lemon-paperplane-main-linux-headless/`：说明、systemd 单元、配置示例。
- 产物是**一个文件** `paperplane-<版本>-linux-<架构>`：内核 + 全部内置插件编在一个可执行文件里（插件仍是独立进程：内核用 `自己 __plugin <id>` 启动），插件的静态文件嵌在里面，启动时解到数据目录。纯 Go、不依赖 glibc，任何发行版（含 Alpine）都能跑。
- 架构：amd64、arm64、armv7、armv6、386、riscv64、loong64、ppc64le、s390x、mips64le。
- 构建：`./build.sh --linux-headless`（Mac）或 `build.cmd -LinuxHeadless`（Windows），缺 Go 时自动准备；产物在 `dist/linux-headless/`，附 `SHA256SUMS`。实现是 `scripts/ppbuild headless`：生成 `lemon-paperplane-main-core/headless-gen/`（入口程序 + 插件表 + 嵌入的插件目录），内核入口在可复用的 `lemon-paperplane-main-core/app` 包里。
- 实测（Docker / QEMU 里的 Alpine，经网关从 Mac 端到端：导入、运行、`/proc` 端口检测、端口转发、远程终端）：amd64、arm64、armv7、armv6、386、riscv64、ppc64le、s390x 全部通过；loong64、mips64le 只验证编译。
- 部署、systemd、配置、安全建议见 [lemon-paperplane-main-linux-headless/README.md](../lemon-paperplane-main-linux-headless/README.md)。

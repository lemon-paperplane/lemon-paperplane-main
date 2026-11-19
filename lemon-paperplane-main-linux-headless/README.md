# Lemon Paperplane — Linux 无头版

没有界面的柠檬纸飞机服务器：**一个可执行文件**（内核 + 全部内置插件），拷到 Linux 机器上运行，就能在 Mac / Windows 客户端里通过「添加服务器」远程管理这台机器上的工程、运行、终端、文件、插件和设置。设计见 [docs/remote.md](../docs/remote.md)。

- 纯 Go 静态编译，不依赖 glibc，任何发行版都能跑（含 Alpine、容器镜像、树莓派）。
- 插件仍是独立进程（内核以 `paperplane __plugin <id>` 启动自己），插件的静态文件嵌在可执行文件里，第一次启动解到数据目录。
- 进程统计、端口检测直接读 `/proc`，不需要 `ps` / `lsof`。

## 架构

| 文件 | 适用 | 实测 |
|---|---|---|
| `paperplane-<版本>-linux-amd64` | x86_64 服务器、PC | 容器内端到端 |
| `paperplane-<版本>-linux-arm64` | ARM64 服务器、树莓派 4/5（64 位系统）、Apple 芯片虚拟机 | 容器内端到端 |
| `paperplane-<版本>-linux-armv7` | 树莓派 2/3/4（32 位系统）等 ARMv7 | 模拟器端到端 |
| `paperplane-<版本>-linux-armv6` | 树莓派 Zero / 1 | 模拟器端到端 |
| `paperplane-<版本>-linux-386` | 32 位 x86 | 模拟器端到端 |
| `paperplane-<版本>-linux-riscv64` | RISC-V 64 | 模拟器端到端 |
| `paperplane-<版本>-linux-ppc64le` | IBM POWER（小端） | 模拟器端到端 |
| `paperplane-<版本>-linux-s390x` | IBM Z | 模拟器端到端 |
| `paperplane-<版本>-linux-loong64` | 龙芯 LoongArch64 | 只验证编译 |
| `paperplane-<版本>-linux-mips64le` | MIPS64（小端） | 只验证编译 |

`uname -m` 对应：x86_64 → amd64，aarch64 → arm64，armv7l → armv7，armv6l → armv6，i686 → 386。

## 构建

在仓库根目录（Mac 上）：

```bash
./build.sh --linux-headless
```

缺 Go 时自动准备。产物在 `dist/linux-headless/`，附 `SHA256SUMS`。只要部分架构：`go -C scripts/ppbuild run . headless -out dist/linux-headless -arch amd64,arm64`。

## 快速开始

```bash
# 1. 拷到服务器上
scp dist/linux-headless/paperplane-0.1.0-linux-amd64 user@server:~/paperplane
ssh user@server
chmod +x ~/paperplane && sudo mv ~/paperplane /usr/local/bin/paperplane

# 2. 生成一条访问密钥（打印连接码；只显示这一次）
paperplane server key add my-mac --address server.example.com

# 3. 前台运行（会打印监听地址和证书指纹）
paperplane serve
```

然后在 Mac / Windows 客户端：左上角服务器切换器 →「添加服务器…」→「连接码」，粘贴第 2 步的连接码。

- `--address` 是客户端用来连接的地址（域名、公网 IP 或端口映射后的 `host:port`），会排在连接码地址列表的最前面；不写时只有这台机器各网卡的地址。
- 防火墙要放行 TCP 47290（或你改的端口），例如 `sudo ufw allow 47290/tcp`。

## 开机自动运行（systemd）

用你平时用的普通用户运行（工程、工具链都在这个用户的目录下），**不要用 root**：

```bash
sudo cp paperplane.service /etc/systemd/system/paperplane.service
sudo sed -i "s/^User=.*/User=$USER/" /etc/systemd/system/paperplane.service
sudo systemctl daemon-reload
sudo systemctl enable --now paperplane
journalctl -u paperplane -f      # 看日志（含监听地址和证书指纹）
```

服务运行时，同一个用户执行的 `paperplane server …` 命令会交给正在运行的服务处理；服务没在运行时直接改数据目录里的文件，下次启动生效。

## 命令

```
paperplane serve [--listen 0.0.0.0:47290] [--data-dir 目录]   前台运行并打开远程监听
paperplane server info                                        地址、证书指纹、密钥与账号数
paperplane server key add <名称> [--address 主机[:端口]]      生成访问密钥，打印连接码
paperplane server key list
paperplane server key revoke <编号>                           吊销（用它的设备立即断开）
paperplane server user set <账号> [--stdin]                   新建账号或改密码（交互输入，不回显）
paperplane server user list | remove <账号>
paperplane server password-login on|off                       是否允许账号密码登录
paperplane server enable | disable [--port n]                 改配置里的远程开关（serve 总是打开）
paperplane ls / import / run / stop / logs / call / ops       和桌面版内核相同的命令行
paperplane shutdown                                           停掉正在运行的服务和全部工程
```

## 数据目录与配置

数据目录：`$XDG_CONFIG_HOME/lemon-paperplane`，没设时 `~/.config/lemon-paperplane`；`--data-dir` 或环境变量 `PAPERPLANE_DATA_DIR` 可改。远程相关在 `remote/` 下：

| 文件 | 内容 |
|---|---|
| `config.json` | 远程配置，可手改（示例见 [config.example.json](config.example.json)），改后重启服务 |
| `credentials.json`（0600） | 访问密钥（只存 SHA-256）与账号（只存 Argon2id 哈希） |
| `tls/cert.pem`、`tls/key.pem` | 自签证书（第一次开启时生成，十年有效） |
| `audit.log` | 安全日志：登录成功/失败、封禁、密钥与账号变更、远程写操作 |

`config.json` 字段：

| 字段 | 说明 |
|---|---|
| `enabled` | 远程开关（`paperplane serve` 不看它，总是打开） |
| `listen` | 监听地址，默认 `0.0.0.0:47290` |
| `passwordLogin` | 是否允许账号密码登录（默认不允许） |
| `allowCIDRs` | 只允许这些来源，例如 `["192.168.0.0/16", "100.64.0.0/10"]`；空表示不限 |
| `tlsCert` / `tlsKey` | 换成自己的证书（PEM），例如 Let's Encrypt 的 `fullchain.pem` / `privkey.pem`；客户端会按证书链校验，不用核对指纹 |
| `users` | 手写账号：`[{"name": "admin", "password": "…"}]`，启动时自动换成哈希存进 `credentials.json` 并从这里删掉。也可以用 `paperplane server user set` |

## 安全

这个端口能做的事等同于登录这台机器的 shell，请按 SSH 的标准对待：

- 强制 TLS；客户端固定证书指纹（连接码里自带），第一次用账号密码登录时会让你核对指纹（和 `paperplane server info` 显示的一致才继续）。
- 除 `GET /api/v1/auth/hello` 和 `POST /api/v1/auth/login` 外，所有接口都要访问密钥；浏览器发来的跨站请求一律拒绝。
- 同一 IP 15 分钟内认证失败 5 次封禁 15 分钟，再犯翻倍（最长 24 小时）；全局每分钟最多 30 次登录尝试。
- 暴露在公网时：只用访问密钥（`passwordLogin` 保持关闭），能限制来源就设 `allowCIDRs`，或者干脆只在内网 / Tailscale / WireGuard 上开放。
- 用普通用户运行，不要用 root（以 root 运行时启动日志会告警）。
- 设备丢了：`paperplane server key list` 找到它的编号，`paperplane server key revoke <编号>`，立即生效。
- 定期看 `remote/audit.log`。

## 升级与卸载

升级：替换 `/usr/local/bin/paperplane` 后 `sudo systemctl restart paperplane`（工程配置、密钥都在数据目录里，不受影响）。

卸载：`sudo systemctl disable --now paperplane`，删掉 `/etc/systemd/system/paperplane.service`、`/usr/local/bin/paperplane`，需要的话再删数据目录。

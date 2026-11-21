package app

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"

	"golang.org/x/term"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/paths"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/remote"
)

// 不带参数在终端里运行 paperplane：逐级菜单引导，一项一项选，不用记命令。
// 主要给 Linux 无头版用（服务、开机自动运行、添加设备、访问密钥、账号、远程访问、工程与运行）；macOS 上同样可用。

// menuAvailable：标准输入输出都是终端时才进菜单（脚本、管道里仍打印用法）。Windows 控制台先不做。
func menuAvailable() bool {
	return runtime.GOOS != "windows" && term.IsTerminal(int(os.Stdin.Fd())) && term.IsTerminal(int(os.Stdout.Fd()))
}

func runMenu() error {
	applyCLILocale()
	t := newTUI()
	fmt.Println()
	fmt.Println(bold(i18n.T("cli.title", Version)))
	if os.Geteuid() == 0 {
		fmt.Println(red("  " + i18n.T("cli.rootWarn")))
	}
	fmt.Println()
	err := t.mainMenu()
	if errors.Is(err, errQuit) {
		return nil
	}
	return err
}

// applyCLILocale：命令行跟服务用同一个语言设置（设置里没选就跟随系统）。
func applyCLILocale() {
	loc := ""
	if c := runningClient(); c != nil {
		var s struct {
			Locale string `json:"locale"`
		}
		if c.call("settings.get", nil, &s) == nil {
			loc = s.Locale
		}
	} else if b, err := os.ReadFile(filepath.Join(paths.DataDir(), "settings.json")); err == nil {
		var s struct {
			Locale string `json:"locale"`
		}
		if json.Unmarshal(b, &s) == nil {
			loc = s.Locale
		}
	}
	i18n.SetLocale(loc)
}

// runningClient 返回正在运行的服务；没在运行返回 nil（不会自动拉起）。
func runningClient() *client {
	if rt, err := readRuntime(); err == nil && ping(rt) {
		return &client{rt: rt, hc: &http.Client{Timeout: 60 * time.Second}}
	}
	return nil
}

// ---------------------------------------------------------------- 状态

type menuState struct {
	c      *client // 服务在运行时非空
	pid    int
	remote remote.Status
	rmErr  error
	sd     systemdInfo
}

func loadState() menuState {
	var st menuState
	st.c = runningClient()
	if st.c != nil {
		st.pid = st.c.rt.PID
	}
	if s, err := openServer(); err == nil {
		st.remote, st.rmErr = s.status()
	} else {
		st.rmErr = err
	}
	st.sd = systemdStatus()
	return st
}

func (st menuState) summary() string {
	var lines []string
	svc := i18n.T("cli.status.stopped")
	if st.c != nil {
		svc = green("●") + " " + i18n.T("cli.status.running", st.pid)
	} else {
		svc = dim("○") + " " + svc
	}
	if st.sd.installed && st.sd.enabled {
		svc += dim(" · " + i18n.T("cli.status.systemd"))
	}
	lines = append(lines, "  "+svc)
	rs := st.remote
	switch {
	case st.rmErr != nil:
		lines = append(lines, "  "+red(st.rmErr.Error()))
	case rs.Running:
		lines = append(lines, "  "+i18n.T("cli.status.remoteOn", rs.Listen))
	case rs.Enabled && st.c != nil:
		lines = append(lines, "  "+red(i18n.T("cli.status.remoteNotListening", rs.Error)))
	case rs.Enabled:
		lines = append(lines, "  "+i18n.T("cli.status.remoteOnIdle", rs.Listen))
	default:
		lines = append(lines, "  "+i18n.T("cli.status.remoteOff"))
	}
	if st.rmErr == nil {
		if rs.Keys == 0 && rs.Users == 0 {
			lines = append(lines, "  "+i18n.T("cli.status.noCreds"))
		} else {
			lines = append(lines, "  "+dim(i18n.T("cli.status.creds", rs.Keys, rs.Users)))
		}
	}
	return strings.Join(lines, "\n")
}

// ---------------------------------------------------------------- 主菜单

func (t *tui) mainMenu() error {
	sel := 0
	for {
		st := loadState()
		pw := i18n.T("cli.off")
		if st.remote.PasswordLogin {
			pw = i18n.T("cli.on")
		}
		svcHint := i18n.T("cli.main.service.hintStopped")
		if st.c != nil {
			svcHint = i18n.T("cli.main.service.hintRunning")
		}
		items := []menuItem{
			{"service", i18n.T("cli.main.service"), svcHint},
			{"device", i18n.T("cli.main.addDevice"), i18n.T("cli.main.addDevice.hint")},
			{"keys", i18n.T("cli.main.keys"), i18n.T("cli.main.keys.hint", st.remote.Keys)},
			{"users", i18n.T("cli.main.users"), i18n.T("cli.main.users.hint", st.remote.Users, pw)},
			{"remote", i18n.T("cli.main.remote"), i18n.T("cli.main.remote.hint")},
			{"projects", i18n.T("cli.main.projects"), i18n.T("cli.main.projects.hint")},
			{"language", i18n.T("cli.main.language"), languageName(currentLocaleSetting(st.c))},
			{"quit", i18n.T("cli.main.quit"), ""},
		}
		k, err := t.choose(st.summary()+"\n\n"+bold(i18n.T("cli.whatToDo")), items, sel, true)
		if err != nil {
			return err
		}
		for i, it := range items {
			if it.key == k {
				sel = i
			}
		}
		switch k {
		case "", "quit":
			return nil
		case "service":
			err = t.serviceMenu()
		case "device":
			err = t.addDevice()
		case "keys":
			err = t.keysMenu()
		case "users":
			err = t.usersMenu()
		case "remote":
			err = t.remoteMenu()
		case "projects":
			err = t.projectsMenu()
		case "language":
			err = t.languageMenu()
		}
		if errors.Is(err, errQuit) {
			return err
		}
		if err != nil {
			fmt.Println(red("  " + i18n.T("cli.error", err.Error())))
			if err := t.pause(); err != nil {
				return err
			}
		}
		fmt.Println()
	}
}

// ---------------------------------------------------------------- 服务

func (t *tui) serviceMenu() error {
	for {
		st := loadState()
		var items []menuItem
		if st.sd.installed {
			if st.sd.active {
				items = append(items, menuItem{"sd-restart", i18n.T("cli.svc.restart"), ""}, menuItem{"sd-stop", i18n.T("cli.svc.stop"), i18n.T("cli.svc.stop.hint")})
			} else {
				items = append(items, menuItem{"sd-start", i18n.T("cli.svc.start"), ""})
			}
			items = append(items, menuItem{"sd-log", i18n.T("cli.svc.log"), "journalctl -u paperplane"},
				menuItem{"sd-uninstall", i18n.T("cli.svc.uninstall"), i18n.T("cli.svc.uninstall.hint")})
		} else {
			if st.c == nil {
				items = append(items, menuItem{"background", i18n.T("cli.svc.background"), i18n.T("cli.svc.background.hint")},
					menuItem{"foreground", i18n.T("cli.svc.foreground"), i18n.T("cli.svc.foreground.hint")})
			} else {
				items = append(items, menuItem{"restart", i18n.T("cli.svc.restart"), ""}, menuItem{"stop", i18n.T("cli.svc.stop"), i18n.T("cli.svc.stop.hint")})
			}
			items = append(items, menuItem{"log", i18n.T("cli.svc.log"), i18n.T("cli.svc.log.hint")})
			if st.sd.available {
				items = append(items, menuItem{"sd-install", i18n.T("cli.svc.install"), i18n.T("cli.svc.install.hint")})
			}
		}
		k, err := t.choose(st.summary()+"\n\n"+bold(i18n.T("cli.svc.title")), items, 0, false)
		if err != nil || k == "" {
			return err
		}
		switch k {
		case "background":
			err = startBackground()
		case "foreground":
			fmt.Println(dim("  " + i18n.T("cli.svc.foregroundNote")))
			if err := runServe(nil); err != nil {
				return err
			}
			return errQuit
		case "stop", "restart":
			if k == "stop" {
				ok, cerr := t.confirm(i18n.T("cli.svc.confirmStop"))
				if cerr != nil || !ok {
					if cerr != nil {
						return cerr
					}
					continue
				}
			}
			if err = runShutdown(); err == nil && k == "restart" {
				err = startBackground()
			}
		case "log":
			err = tailFile(filepath.Join(paths.DataDir(), "logs", "daemon.log"), 60)
		case "sd-start", "sd-restart":
			err = privileged("systemctl", strings.TrimPrefix(k, "sd-"), "paperplane")
			if err == nil {
				waitService(true)
			}
		case "sd-stop":
			ok, cerr := t.confirm(i18n.T("cli.svc.confirmStop"))
			if cerr != nil {
				return cerr
			}
			if ok {
				err = privileged("systemctl", "stop", "paperplane")
			}
		case "sd-log":
			cmd := exec.Command("journalctl", "-u", "paperplane", "-n", "60", "--no-pager")
			cmd.Stdout, cmd.Stderr = os.Stdout, os.Stderr
			err = cmd.Run()
		case "sd-install":
			err = t.installSystemd(st)
		case "sd-uninstall":
			ok, cerr := t.confirm(i18n.T("cli.svc.confirmUninstall"))
			if cerr != nil {
				return cerr
			}
			if ok {
				err = uninstallSystemd()
			}
		}
		if err != nil {
			if errors.Is(err, errQuit) {
				return err
			}
			fmt.Println(red("  " + i18n.T("cli.error", err.Error())))
		}
		if k == "log" || k == "sd-log" || err != nil {
			if perr := t.pause(); perr != nil {
				return perr
			}
		}
		fmt.Println()
	}
}

// startBackground 在后台启动服务（paperplane serve --detach：强制打开远程监听）。
func startBackground() error {
	cmd := exec.Command(exePath(), "serve", "--detach")
	cmd.Stdout, cmd.Stderr = os.Stdout, os.Stderr
	if err := cmd.Run(); err != nil {
		return err
	}
	if rt, err := readRuntime(); err == nil && ping(rt) {
		fmt.Println(green("  " + i18n.T("cli.svc.started", rt.PID)))
	}
	return nil
}

func tailFile(path string, n int) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	lines := strings.Split(strings.TrimRight(string(b), "\n"), "\n")
	if len(lines) > n {
		lines = lines[len(lines)-n:]
	}
	fmt.Println(dim("  " + path))
	for _, l := range lines {
		fmt.Println(l)
	}
	return nil
}

// ---------------------------------------------------------------- systemd

const systemdUnit = "/etc/systemd/system/paperplane.service"

type systemdInfo struct {
	available, installed, enabled, active bool
}

func systemdStatus() systemdInfo {
	var s systemdInfo
	if runtime.GOOS != "linux" {
		return s
	}
	if _, err := exec.LookPath("systemctl"); err != nil {
		return s
	}
	if st, err := os.Stat("/run/systemd/system"); err != nil || !st.IsDir() { // 容器里通常没有 systemd
		return s
	}
	s.available = true
	_, err := os.Stat(systemdUnit)
	s.installed = err == nil
	if !s.installed {
		return s
	}
	out, _ := exec.Command("systemctl", "is-enabled", "paperplane").Output()
	s.enabled = strings.TrimSpace(string(out)) == "enabled"
	s.active = exec.Command("systemctl", "is-active", "--quiet", "paperplane").Run() == nil
	return s
}

// privileged 以 root 执行（不是 root 时经 sudo，密码在这个终端里输入）。
func privileged(args ...string) error {
	var cmd *exec.Cmd
	if os.Geteuid() == 0 {
		cmd = exec.Command(args[0], args[1:]...)
	} else {
		if _, err := exec.LookPath("sudo"); err != nil {
			return errors.New(i18n.T("cli.svc.needSudo"))
		}
		cmd = exec.Command("sudo", args...)
	}
	cmd.Stdin, cmd.Stdout, cmd.Stderr = os.Stdin, os.Stdout, os.Stderr
	fmt.Println(dim("  $ " + strings.Join(cmd.Args, " ")))
	return cmd.Run()
}

// unitText 生成 systemd 单元（与 lemon-paperplane-main-linux-headless/paperplane.service 保持一致）。
func unitText(userName, exe string) string {
	if strings.ContainsAny(exe, " \t") {
		exe = strconv.Quote(exe)
	}
	env := ""
	if d := os.Getenv("PAPERPLANE_DATA_DIR"); d != "" {
		env = "Environment=" + strconv.Quote("PAPERPLANE_DATA_DIR="+d) + "\n"
	}
	return `# 柠檬纸飞机服务器（Linux 无头版）systemd 单元，由 paperplane 菜单「设为开机自动运行」生成。
[Unit]
Description=Lemon Paperplane server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=` + userName + `
ExecStart=` + exe + ` serve
` + env + `Restart=on-failure
RestartSec=3
# 停止时内核会先优雅停掉托管的工程
KillMode=mixed
TimeoutStopSec=30
# 加固：不能提权、独立 /tmp、系统目录只读
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true

[Install]
WantedBy=multi-user.target
`
}

func (t *tui) installSystemd(st menuState) error {
	def := ""
	if u, err := user.Current(); err == nil {
		def = u.Username
	}
	if os.Geteuid() == 0 && os.Getenv("SUDO_USER") != "" {
		def = os.Getenv("SUDO_USER")
	}
	name, ok, err := t.input(i18n.T("cli.svc.installUser"), def, i18n.T("cli.svc.installUser.hint"))
	if err != nil || !ok || name == "" {
		return err
	}
	if _, err := user.Lookup(name); err != nil {
		return err
	}
	if st.c != nil {
		// 端口要让给 systemd 管理的服务
		fmt.Println(dim("  " + i18n.T("cli.svc.stopOtherFirst")))
		if err := runShutdown(); err != nil {
			return err
		}
	}
	tmp, err := os.CreateTemp("", "paperplane-*.service")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	if _, err := tmp.WriteString(unitText(name, exePath())); err != nil {
		return err
	}
	tmp.Close()
	fmt.Println(dim("  " + i18n.T("cli.svc.installing")))
	for _, args := range [][]string{
		{"install", "-m", "0644", tmp.Name(), systemdUnit},
		{"systemctl", "daemon-reload"},
		{"systemctl", "enable", "--now", "paperplane"},
	} {
		if err := privileged(args...); err != nil {
			return err
		}
	}
	if waitService(true) {
		fmt.Println(green("  " + i18n.T("cli.svc.installed")))
	} else {
		fmt.Println(red("  " + i18n.T("cli.svc.installFailed")))
	}
	return nil
}

func uninstallSystemd() error {
	for _, args := range [][]string{
		{"systemctl", "disable", "--now", "paperplane"},
		{"rm", "-f", systemdUnit},
		{"systemctl", "daemon-reload"},
	} {
		if err := privileged(args...); err != nil {
			return err
		}
	}
	fmt.Println(green("  " + i18n.T("cli.svc.uninstalled")))
	return nil
}

// waitService 等服务（不论谁启动的）可以连上，最多 8 秒。
func waitService(up bool) bool {
	for i := 0; i < 80; i++ {
		if (runningClient() != nil) == up {
			return true
		}
		time.Sleep(100 * time.Millisecond)
	}
	return false
}

// ---------------------------------------------------------------- 添加设备、访问密钥、账号

func (t *tui) addDevice() error {
	name, ok, err := t.input(i18n.T("cli.dev.name"), "", i18n.T("cli.dev.name.hint"))
	if err != nil || !ok {
		return err
	}
	if name == "" {
		return errors.New(i18n.T("cli.dev.nameRequired"))
	}
	addr, ok, err := t.input(i18n.T("cli.dev.address"), "", i18n.T("cli.dev.address.hint"))
	if err != nil || !ok {
		return err
	}
	s, err := openServer()
	if err != nil {
		return err
	}
	if err := s.createKey(name, addr, false); err != nil {
		return err
	}
	if err := t.offerRemoteOn(); err != nil {
		return err
	}
	return t.pause()
}

// offerRemoteOn：远程访问没开 / 服务没在运行时，问一句要不要现在开。
func (t *tui) offerRemoteOn() error {
	st := loadState()
	if st.rmErr != nil || st.remote.Running {
		return nil
	}
	if !st.remote.Enabled {
		ok, err := t.confirm(i18n.T("cli.dev.enableNow"))
		if err != nil || !ok {
			return err
		}
		s, err := openServer()
		if err != nil {
			return err
		}
		on := true
		if _, err := s.configure(remote.ConfigureParams{Enabled: &on}); err != nil {
			return err
		}
	}
	if runningClient() == nil {
		ok, err := t.confirm(i18n.T("cli.dev.startNow"))
		if err != nil || !ok {
			return err
		}
		if st.sd.installed {
			return privileged("systemctl", "start", "paperplane")
		}
		return startBackground()
	}
	return nil
}

func (t *tui) keysMenu() error {
	for {
		s, err := openServer()
		if err != nil {
			return err
		}
		var keys []remote.KeyInfo
		if s.c != nil {
			if err := s.c.call("remote.keys", nil, &keys); err != nil {
				return err
			}
		} else {
			keys = s.rm.Keys("")
		}
		items := []menuItem{{"add", i18n.T("cli.keys.add"), i18n.T("cli.main.addDevice.hint")}}
		for _, k := range keys {
			used := i18n.T("cli.keys.neverUsed")
			if k.LastUsedAt != nil {
				used = i18n.T("cli.keys.lastUsed", k.LastUsedAt.Local().Format("2006-01-02 15:04"), k.LastIP)
			}
			who := k.Name
			if k.User != "" {
				who = k.User + "@" + k.Name
			}
			items = append(items, menuItem{"key:" + k.ID, who, k.ID + " · " + used})
		}
		k, err := t.choose(bold(i18n.T("cli.keys.title", len(keys))), items, 0, false)
		if err != nil || k == "" {
			return err
		}
		if k == "add" {
			if err := t.addDevice(); err != nil {
				return err
			}
			continue
		}
		id := strings.TrimPrefix(k, "key:")
		label := id
		for _, it := range items {
			if it.key == k {
				label = it.label
			}
		}
		a, err := t.choose(bold(label), []menuItem{{"revoke", i18n.T("cli.keys.revoke"), i18n.T("cli.keys.revoke.hint")}}, 0, false)
		if err != nil {
			return err
		}
		if a != "revoke" {
			continue
		}
		ok, err := t.confirm(i18n.T("cli.keys.confirmRevoke", label))
		if err != nil {
			return err
		}
		if !ok {
			continue
		}
		if s.c != nil {
			err = s.c.call("remote.keyRevoke", map[string]string{"id": id}, nil)
		} else {
			err = s.rm.RevokeKey(id, cliWho)
		}
		if err != nil {
			return err
		}
		fmt.Println(green("  " + i18n.T("cli.keys.revoked", label)))
	}
}

func (t *tui) usersMenu() error {
	for {
		s, err := openServer()
		if err != nil {
			return err
		}
		var users []remote.UserInfo
		if s.c != nil {
			if err := s.c.call("remote.users", nil, &users); err != nil {
				return err
			}
		} else {
			users = s.rm.Users()
		}
		st, _ := s.status()
		items := []menuItem{{"add", i18n.T("cli.users.add"), ""}}
		if st.PasswordLogin {
			items = append(items, menuItem{"pw-off", i18n.T("cli.users.pwLoginOn"), i18n.T("cli.users.pwLoginOn.hint")})
		} else {
			items = append(items, menuItem{"pw-on", i18n.T("cli.users.pwLoginOff"), i18n.T("cli.users.pwLoginOff.hint")})
		}
		for _, u := range users {
			items = append(items, menuItem{"user:" + u.Name, u.Name, i18n.T("cli.users.updated", u.UpdatedAt.Local().Format("2006-01-02 15:04"))})
		}
		k, err := t.choose(bold(i18n.T("cli.users.title", len(users))), items, 0, false)
		if err != nil || k == "" {
			return err
		}
		switch {
		case k == "add":
			name, ok, err := t.input(i18n.T("cli.users.name"), "", "")
			if err != nil {
				return err
			}
			if !ok || name == "" {
				continue
			}
			if err := t.setPassword(s, name); err != nil {
				fmt.Println(red("  " + i18n.T("cli.error", err.Error())))
			}
		case k == "pw-on" || k == "pw-off":
			on := k == "pw-on"
			if _, err := s.configure(remote.ConfigureParams{PasswordLogin: &on}); err != nil {
				return err
			}
		default:
			name := strings.TrimPrefix(k, "user:")
			a, err := t.choose(bold(name), []menuItem{
				{"passwd", i18n.T("cli.users.changePw"), ""},
				{"remove", i18n.T("cli.users.remove"), i18n.T("cli.users.remove.hint")},
			}, 0, false)
			if err != nil {
				return err
			}
			switch a {
			case "passwd":
				if err := t.setPassword(s, name); err != nil {
					fmt.Println(red("  " + i18n.T("cli.error", err.Error())))
				}
			case "remove":
				ok, err := t.confirm(i18n.T("cli.users.confirmRemove", name))
				if err != nil {
					return err
				}
				if ok {
					if s.c != nil {
						err = s.c.call("remote.userRemove", map[string]string{"name": name}, nil)
					} else {
						err = s.rm.RemoveUser(name, cliWho)
					}
					if err != nil {
						return err
					}
					fmt.Println(green("  " + i18n.T("cli.users.removed", name)))
				}
			}
		}
	}
}

func (t *tui) setPassword(s *serverClient, name string) error {
	pw, err := readPassword(false)
	if err != nil {
		return err
	}
	if s.c != nil {
		err = s.c.call("remote.userSet", map[string]string{"name": name, "password": pw}, nil)
	} else {
		err = s.rm.SetUser(name, pw, cliWho)
	}
	if err != nil {
		return err
	}
	fmt.Println(green("  " + i18n.T("cli.users.saved", name)))
	if st, _ := s.status(); !st.PasswordLogin {
		ok, err := t.confirm(i18n.T("cli.users.pwOffAsk"))
		if err != nil || !ok {
			return err
		}
		on := true
		_, err = s.configure(remote.ConfigureParams{PasswordLogin: &on})
		return err
	}
	return nil
}

// ---------------------------------------------------------------- 远程访问

func (t *tui) remoteMenu() error {
	for {
		s, err := openServer()
		if err != nil {
			return err
		}
		st, err := s.status()
		if err != nil {
			return err
		}
		var items []menuItem
		if st.Enabled {
			items = append(items, menuItem{"off", i18n.T("cli.remote.disable"), i18n.T("cli.remote.disable.hint")})
		} else {
			items = append(items, menuItem{"on", i18n.T("cli.remote.enable"), ""})
		}
		items = append(items,
			menuItem{"port", i18n.T("cli.remote.port"), i18n.T("cli.remote.port.hint", st.Port)},
			menuItem{"info", i18n.T("cli.remote.info"), ""})
		title := loadState().summary() + "\n\n" + bold(i18n.T("cli.remote.title"))
		k, err := t.choose(title, items, 0, false)
		if err != nil || k == "" {
			return err
		}
		switch k {
		case "on", "off":
			on := k == "on"
			if _, err := s.configure(remote.ConfigureParams{Enabled: &on}); err != nil {
				return err
			}
			if on && s.c == nil {
				if err := t.offerRemoteOn(); err != nil {
					return err
				}
			}
		case "port":
			v, ok, err := t.input(i18n.T("cli.remote.portPrompt"), strconv.Itoa(st.Port), "")
			if err != nil {
				return err
			}
			if !ok {
				continue
			}
			p, perr := strconv.Atoi(v)
			if perr != nil || p < 1 || p > 65535 {
				fmt.Println(red("  " + i18n.T("cli.remote.badPort")))
				continue
			}
			if _, err := s.configure(remote.ConfigureParams{Port: &p}); err != nil {
				return err
			}
			if s.c == nil {
				fmt.Println(dim("  " + i18n.T("cli.remote.idleNote")))
			}
		case "info":
			printRemoteInfo(st)
			if err := t.pause(); err != nil {
				return err
			}
		}
	}
}

func printRemoteInfo(st remote.Status) {
	fmt.Println()
	for _, a := range st.Addresses {
		fmt.Println("  " + i18n.T("cli.info.address") + "  " + a)
	}
	fmt.Println("  " + i18n.T("cli.info.fingerprint") + "  " + st.FingerprintDisplay)
	fmt.Println()
}

// ---------------------------------------------------------------- 工程与运行

func (t *tui) projectsMenu() error {
	c := runningClient()
	if c == nil {
		ok, err := t.confirm(i18n.T("cli.proj.needService"))
		if err != nil || !ok {
			return err
		}
		if sd := systemdStatus(); sd.installed {
			err = privileged("systemctl", "start", "paperplane")
		} else {
			err = startBackground()
		}
		if err != nil {
			return err
		}
		waitService(true)
		if c = runningClient(); c == nil {
			return errors.New(i18n.T("cli.status.stopped"))
		}
	}
	sel := 0
	for {
		var list []summary
		if err := c.call("project.list", nil, &list); err != nil {
			return err
		}
		items := []menuItem{{"import", i18n.T("cli.proj.import"), i18n.T("cli.proj.import.hint")}}
		for _, p := range list {
			var fs []string
			for _, f := range p.Facets {
				fs = append(fs, f.Name)
			}
			hint := strings.Join(fs, " · ")
			if n := activeRuns(p); n > 0 {
				hint = green(i18n.T("cli.proj.runningN", n)) + sDim + "  " + hint
			}
			items = append(items, menuItem{"p:" + p.ID, p.Name, hint})
		}
		title := bold(i18n.T("cli.proj.title", len(list)))
		if len(list) == 0 {
			title += "\n" + dim("  "+i18n.T("cli.proj.none"))
		}
		k, err := t.choose(title, items, sel, false)
		if err != nil || k == "" {
			return err
		}
		for i, it := range items {
			if it.key == k {
				sel = i
			}
		}
		if k == "import" {
			wd, _ := os.Getwd()
			path, ok, err := t.input(i18n.T("cli.proj.path"), wd, i18n.T("cli.proj.path.hint"))
			if err != nil {
				return err
			}
			if !ok {
				continue
			}
			if err := runImport([]string{path}); err != nil {
				fmt.Println(red("  " + i18n.T("cli.error", err.Error())))
			}
			if err := t.pause(); err != nil {
				return err
			}
			continue
		}
		if err := t.projectMenu(c, strings.TrimPrefix(k, "p:")); err != nil {
			return err
		}
	}
}

func activeRuns(p summary) int {
	n := 0
	for _, r := range p.Runs {
		if r.State == "preparing" || r.State == "starting" || r.State == "running" || r.State == "stopping" {
			n++
		}
	}
	return n
}

func (t *tui) projectMenu(c *client, id string) error {
	for {
		p, err := c.findProject(id)
		if err != nil {
			return err
		}
		running := map[string]bool{}
		for _, r := range p.Runs {
			if r.State == "preparing" || r.State == "starting" || r.State == "running" || r.State == "stopping" {
				running[r.Name] = true
			}
		}
		var items []menuItem
		for _, cfg := range p.Configs {
			hint := ""
			if cfg.ID == p.DefaultConfigID {
				hint = i18n.T("cli.proj.default")
			}
			if running[cfg.Name] {
				hint = green(i18n.T("cli.proj.running"))
			}
			items = append(items, menuItem{"run:" + cfg.ID, i18n.T("cli.proj.run", cfg.Name), hint})
		}
		if n := activeRuns(*p); n > 0 {
			items = append(items, menuItem{"output", i18n.T("cli.proj.output"), ""}, menuItem{"stop", i18n.T("cli.proj.stopAll"), i18n.T("cli.proj.runningN", n)})
		} else {
			items = append(items, menuItem{"output", i18n.T("cli.proj.lastOutput"), ""})
		}
		k, err := t.choose(bold(p.Name)+"  "+dim(p.Path), items, 0, false)
		if err != nil || k == "" {
			return err
		}
		switch {
		case strings.HasPrefix(k, "run:"):
			var run struct {
				ID string `json:"id"`
			}
			if err := c.call("run.start", map[string]string{"projectId": p.ID, "configId": strings.TrimPrefix(k, "run:")}, &run); err != nil {
				fmt.Println(red("  " + i18n.T("cli.error", err.Error())))
				continue
			}
			if err := t.watch(c, run.ID, 0); err != nil {
				return err
			}
		case k == "output":
			var runs []struct {
				ID    string `json:"id"`
				Name  string `json:"name"`
				State string `json:"state"`
			}
			if err := c.call("run.list", map[string]string{"projectId": p.ID}, &runs); err != nil {
				return err
			}
			if len(runs) == 0 {
				fmt.Println(dim("  " + i18n.T("cli.proj.noRuns")))
				continue
			}
			rid := runs[0].ID
			if len(runs) > 1 {
				var ri []menuItem
				for _, r := range runs {
					ri = append(ri, menuItem{r.ID, r.Name, r.State})
				}
				if rid, err = t.choose(bold(i18n.T("cli.proj.pickRun")), ri, 0, false); err != nil {
					return err
				}
				if rid == "" {
					continue
				}
			}
			if err := t.watch(c, rid, 200); err != nil {
				return err
			}
		case k == "stop":
			var r map[string]int
			if err := c.call("run.stopAll", map[string]string{"projectId": p.ID}, &r); err != nil {
				return err
			}
			fmt.Println(green("  " + i18n.T("cli.proj.stopped", r["stopped"])))
		}
	}
}

// watch 显示运行输出直到运行结束或按 Ctrl+C（只回到菜单，不停止运行）。
func (t *tui) watch(c *client, runID string, tail int) error {
	fmt.Println(dim("  " + i18n.T("cli.proj.following")))
	if err := c.follow(runID, tail); err != nil {
		return err
	}
	var r struct {
		State string `json:"state"`
	}
	if c.call("run.get", map[string]string{"runId": runID}, &r) == nil && r.State != "" &&
		r.State != "preparing" && r.State != "starting" && r.State != "running" && r.State != "stopping" {
		fmt.Println(dim("  " + i18n.T("cli.proj.runEnded")))
		return t.pause()
	}
	fmt.Println()
	return nil
}

// ---------------------------------------------------------------- 语言

func currentLocaleSetting(c *client) string {
	if c != nil {
		var s struct {
			Locale string `json:"locale"`
		}
		if c.call("settings.get", nil, &s) == nil {
			return s.Locale
		}
	}
	var s struct {
		Locale string `json:"locale"`
	}
	if b, err := os.ReadFile(filepath.Join(paths.DataDir(), "settings.json")); err == nil {
		_ = json.Unmarshal(b, &s)
	}
	return s.Locale
}

func languageName(l string) string {
	switch {
	case strings.HasPrefix(l, "zh"):
		return "简体中文"
	case l == "en":
		return "English"
	}
	return i18n.T("cli.lang.system")
}

func (t *tui) languageMenu() error {
	k, err := t.choose(bold(i18n.T("cli.main.language"))+"\n"+dim("  "+i18n.T("cli.lang.note")), []menuItem{
		{"system", i18n.T("cli.lang.system"), ""},
		{"zh-Hans", "简体中文", ""},
		{"en", "English", ""},
	}, 0, false)
	if err != nil || k == "" {
		return err
	}
	if c := runningClient(); c != nil {
		if err := c.call("settings.update", map[string]string{"locale": k}, nil); err != nil {
			return err
		}
	} else {
		// 服务没在运行：直接改设置文件，下次启动生效
		file := filepath.Join(paths.DataDir(), "settings.json")
		m := map[string]any{}
		if b, err := os.ReadFile(file); err == nil {
			_ = json.Unmarshal(b, &m)
		}
		m["locale"] = k
		b, _ := json.MarshalIndent(m, "", "  ")
		if err := os.MkdirAll(filepath.Dir(file), 0o755); err != nil {
			return err
		}
		if err := os.WriteFile(file+".tmp", b, 0o644); err != nil {
			return err
		}
		if err := os.Rename(file+".tmp", file); err != nil {
			return err
		}
	}
	i18n.SetLocale(k)
	return nil
}

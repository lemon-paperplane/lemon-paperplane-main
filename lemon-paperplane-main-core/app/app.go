// paperplane 是 Lemon Paperplane 内核与命令行。
//
//	paperplane daemon [--detach]   启动内核
//	paperplane serve               前台运行并打开远程监听（服务器、systemd 用）
//	paperplane server …            远程访问：信息、访问密钥、账号
//	paperplane mcp                 MCP stdio 桥
//	paperplane askpass <提示>       git/ssh 的 askpass（在界面弹框）
//	paperplane ls / import / run / stop / logs / call / ops / plugin pack / version
package app

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"runtime"
	"runtime/debug"
	"strings"
	"syscall"
	"time"

	"golang.org/x/term"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/api"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/console"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/engine"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/gateway"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/mcp"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/ops"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/paths"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/remote"
)

// Version 由构建脚本用 -ldflags 注入。
// Version 由入口程序设置（ldflags -X main.Version=…）。
var Version = "0.1.0"

// Main 是命令行入口：cmd/paperplane 与 Linux 无头版（内核 + 全部插件编在一个文件里）共用。
func Main() {
	// Windows：本程序也是「停止运行」时发 Ctrl+C 的辅助进程
	if proc.RunHelper(os.Args) {
		return
	}
	if len(os.Args) < 2 {
		// 在终端里直接运行 paperplane：逐级菜单引导
		if menuAvailable() {
			if err := runMenu(); err != nil {
				fmt.Fprintln(os.Stderr, "error:", err)
				os.Exit(1)
			}
			return
		}
		usage()
		os.Exit(2)
	}
	cmd, args := os.Args[1], os.Args[2:]
	var err error
	switch cmd {
	case "daemon":
		err = runDaemon(args)
	case "mcp":
		err = runMCPBridge()
	case "askpass":
		err = runAskpass(args)
	case "call":
		err = runCall(args)
	case "ops":
		err = runOps()
	case "ls":
		err = runLs(args)
	case "import":
		err = runImport(args)
	case "run":
		err = runRun(args)
	case "stop":
		err = runStop(args)
	case "shutdown":
		err = runShutdown()
	case "serve":
		err = runServe(args)
	case "server":
		err = runServerCmd(args)
	case "logs":
		err = runLogs(args)
	case "plugin":
		err = runPlugin(args)
	case "version", "--version", "-v":
		fmt.Println("paperplane", Version)
	case "menu":
		err = runMenu()
	case "help", "-h", "--help":
		usage()
	default:
		fmt.Fprintln(os.Stderr, "unknown command:", cmd)
		usage()
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}

func usage() {
	fmt.Fprint(os.Stderr, `Lemon Paperplane

Usage:
  paperplane                              interactive menu (when run in a terminal)
  paperplane daemon [--detach]            start the daemon
  paperplane mcp                          MCP server over stdio (for Claude Code / Claude Desktop)
  paperplane ls [workspace]               list projects
  paperplane import <path> [-w workspace] import a folder
  paperplane run <project> [config]       start and follow logs (Ctrl-C detaches, does not stop)
  paperplane stop <project|runId>         stop runs
  paperplane shutdown                     stop the running daemon and all runs (never starts one)
  paperplane serve [--detach] [--listen addr] [--data-dir dir]
                                          run in the foreground with remote access on (servers, systemd)
  paperplane server info                  remote access: addresses, certificate fingerprint
  paperplane server key add <name> [--address host]   create an access key, print its connection code
  paperplane server key list | revoke <id>
  paperplane server user set <name> [--stdin]          create an account / change its password
  paperplane server user list | remove <name>
  paperplane server enable | disable [--port n] | password-login on|off
  paperplane logs <project|runId> [-f] [-n 200]
  paperplane call <op> ['<json>']         call any operation
  paperplane ops                          list operations
  paperplane plugin pack <dir>            package a plugin for the store
  paperplane version
`)
}

// ---------------------------------------------------------------------------
// daemon

func exePath() string {
	exe, _ := os.Executable()
	if real, err := filepath.EvalSymlinks(exe); err == nil {
		exe = real
	}
	return exe
}

// bundledPluginDirs：包内插件目录（macOS: Contents/Resources/plugins；其他：可执行文件旁的 plugins）。
func bundledPluginDirs(exe string) []string {
	var dirs []string
	if d := os.Getenv("PAPERPLANE_BUNDLED_PLUGINS"); d != "" {
		dirs = append(dirs, filepath.SplitList(d)...)
	}
	dir := filepath.Dir(exe)
	cands := []string{filepath.Join(dir, "..", "Resources", "plugins"), filepath.Join(dir, "plugins"), filepath.Join(dir, "..", "lib", "lemon-paperplane", "plugins")}
	for _, c := range cands {
		if st, err := os.Stat(c); err == nil && st.IsDir() {
			dirs = append(dirs, filepath.Clean(c))
		}
	}
	return dirs
}

func runDaemon(args []string) error {
	return runDaemonOpts(args, daemonOpts{})
}

// daemonOpts：paperplane serve 用（前台运行并强制打开远程监听）。
type daemonOpts struct {
	serve  bool
	listen string
}

func runDaemonOpts(args []string, dopt daemonOpts) error {
	fs := flag.NewFlagSet("daemon", flag.ExitOnError)
	detach := fs.Bool("detach", false, "run in background")
	replace := fs.Bool("replace", false, "stop a running daemon that belongs to another installation (or another version) and start this one")
	_ = fs.Parse(args)
	layout, err := paths.New(paths.DataDir())
	if err != nil {
		return err
	}
	exe := exePath()
	if rt, err := readRuntime(); err == nil && ping(rt) {
		if dopt.serve {
			return fmt.Errorf("已有后台服务在运行（pid %d，数据目录 %s），先执行 paperplane shutdown", rt.PID, layout.Root)
		}
		other := filepath.Clean(rt.Executable) != filepath.Clean(exe) || rt.Version != Version
		if !*replace || !other {
			fmt.Printf("daemon already running (pid %d, port %d)\n", rt.PID, rt.Port)
			return nil
		}
		// 替换别处安装（或旧版本）的 daemon：先让它停掉运行中的工程并退出
		fmt.Printf("replacing daemon %s (pid %d)\n", rt.Executable, rt.PID)
		c := &client{rt: rt, hc: &http.Client{Timeout: 15 * time.Second}}
		_ = c.call("system.shutdown", map[string]bool{"stopRuns": true}, nil)
		for i := 0; i < 150 && proc.Alive(rt.PID); i++ {
			time.Sleep(100 * time.Millisecond)
		}
		if proc.Alive(rt.PID) {
			proc.KillGroup(rt.PID)
			_ = proc.Kill(rt.PID)
		}
		_ = os.Remove(layout.RuntimeFile())
	}
	if *detach {
		args := []string{"daemon"}
		if dopt.serve {
			args = []string{"serve"}
			if dopt.listen != "" {
				args = append(args, "--listen", dopt.listen)
			}
		}
		cmd := exec.Command(exe, args...)
		logf, _ := os.OpenFile(filepath.Join(layout.Logs, "daemon.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
		cmd.Stdout, cmd.Stderr = logf, logf
		cmd.Env = append(os.Environ(), "PAPERPLANE_DETACHED=1")
		proc.PrepareDetached(cmd)
		if err := proc.StartDetached(cmd); err != nil {
			return err
		}
		for i := 0; i < 150; i++ {
			time.Sleep(100 * time.Millisecond)
			if rt, err := readRuntime(); err == nil && rt.PID == cmd.Process.Pid && ping(rt) {
				fmt.Printf("daemon started (pid %d, port %d)\n", rt.PID, rt.Port)
				return nil
			}
		}
		return errors.New("daemon did not start in time, see " + filepath.Join(layout.Logs, "daemon.log"))
	}

	proc.PrepareDaemon()
	// 省内存：限制 Go 堆、更积极的 GC
	debug.SetMemoryLimit(96 << 20)
	debug.SetGCPercent(50)
	logger := log.New(os.Stderr, "", log.LstdFlags)
	if os.Getenv("PAPERPLANE_DETACHED") == "" {
		if lf, err := os.OpenFile(filepath.Join(layout.Logs, "daemon.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644); err == nil {
			logger.SetOutput(io.MultiWriter(os.Stderr, lf))
		}
	}
	// 登录 shell 的环境变量（PATH、JAVA_HOME…）在后台读：用户的 shell 配置可能要一两秒，
	// 不让它拖慢 App 打开；插件进程会等它读完再启动
	envReady := make(chan struct{})
	go func() {
		defer close(envReady)
		loadShellEnv(logger)
	}()
	var devDirs []string
	if d := os.Getenv("PAPERPLANE_DEV_PLUGINS"); d != "" {
		devDirs = filepath.SplitList(d)
	}
	bundledDirs, store := bundledPluginDirs(exe), bundledStore(exe)
	if Embedded != nil {
		// 单文件版：内置插件嵌在可执行文件里，解到数据目录
		pd, sd, err := extractEmbedded(layout.Root)
		if err != nil {
			return fmt.Errorf("解出内置插件：%w", err)
		}
		bundledDirs = append([]string{pd}, bundledDirs...)
		if sd != "" {
			store = sd
		}
	}
	e, err := engine.New(engine.Config{Version: Version, Layout: layout, BundledDirs: bundledDirs, DevDirs: devDirs, Executable: exe, Logger: logger, BundledStore: store, EnvReady: envReady})
	if err != nil {
		return err
	}
	reg := ops.New(e)
	srv := &api.Server{E: e, Reg: reg, MCP: &mcp.Handler{Reg: reg, Version: Version}, Token: api.NewToken(), EditorDir: editorDir(exe)}
	if err := srv.Listen(e.Store.Settings().APIPort); err != nil {
		return err
	}
	e.SetAPI(srv.Base(), srv.Token)
	rm, err := remote.New(remote.Options{Dir: filepath.Join(layout.Root, "remote"), Handler: srv.Handler(), Logger: logger,
		Version: Version, Executable: exe, Serve: dopt.serve, Listen: dopt.listen})
	if err != nil {
		return err
	}
	gw := gateway.New(srv.EditorDir, Version, logger)
	stop := make(chan bool, 1)
	reg.Extras = ops.Extras{PID: os.Getpid(), MCPURL: srv.Base() + "/mcp", Shutdown: func(stopRuns bool) { stop <- stopRuns }, Remote: rm, Gateway: gw}
	rt := api.Runtime{Port: srv.Port, Token: srv.Token, PID: os.Getpid(), Version: Version, Executable: exe}
	if err := writeRuntime(layout, rt); err != nil {
		return err
	}
	logger.Printf("paperplane %s listening on %s (data %s, plugins %d)", Version, srv.Base(), layout.Root, len(e.Plugins.Active()))
	if len(e.Plugins.Active()) == 0 {
		logger.Printf("WARNING: no plugins loaded — projects cannot be recognized. bundled dirs: %v, user dir: %s. The app bundle is incomplete (Contents/Resources/plugins missing?)", bundledPluginDirs(exe), layout.Plugins)
	}
	go func() {
		if err := srv.Serve(); err != nil {
			logger.Printf("server: %v", err)
		}
	}()
	rm.Start()
	if dopt.serve {
		printServeBanner(rm.Status())
	}
	// 内核在跑，菜单栏 / 托盘就要有图标：界面没开时以菜单栏模式拉起
	go ensureUI(exe, dopt.serve, logger)
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, os.Interrupt, syscall.SIGTERM)
	stopRuns := true
	select {
	case <-sig:
	case stopRuns = <-stop:
	}
	logger.Printf("shutting down (stopRuns=%v)", stopRuns)
	gw.CloseAll()
	rm.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	srv.Shutdown(ctx)
	cancel()
	e.Close(stopRuns)
	if cur, err := readRuntime(); err == nil && cur.PID == os.Getpid() {
		_ = os.Remove(layout.RuntimeFile())
	}
	return nil
}

// loadShellEnv 从用户的登录 shell 取环境变量（图形界面启动的进程拿不到 ~/.zshrc 里的 PATH 等），与 VS Code 的做法相同。
func loadShellEnv(logger *log.Logger) {
	if runtime.GOOS == "windows" {
		loadRegistryEnv(logger)
		return
	}
	if os.Getenv("PAPERPLANE_NO_SHELL_ENV") != "" {
		return
	}
	shell := os.Getenv("SHELL")
	if shell == "" {
		// systemd、容器里通常没有 $SHELL：macOS 默认 zsh，Linux 按 bash、sh 找
		for _, c := range []string{"/bin/zsh", "/bin/bash", "/bin/sh"} {
			if runtime.GOOS != "darwin" && c == "/bin/zsh" {
				continue
			}
			if st, err := os.Stat(c); err == nil && !st.IsDir() {
				shell = c
				break
			}
		}
	}
	if shell == "" {
		return
	}
	const mark = "__PAPERPLANE_ENV__"
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, shell, "-ilc", "echo "+mark+"; env -0")
	cmd.Stdin = nil
	out, err := cmd.Output()
	if err != nil {
		logger.Printf("shell env: %v", err)
		return
	}
	i := bytes.Index(out, []byte(mark+"\n"))
	if i < 0 {
		return
	}
	n := 0
	for _, kv := range bytes.Split(out[i+len(mark)+1:], []byte{0}) {
		k, v, ok := strings.Cut(string(kv), "=")
		if !ok || k == "" || strings.HasPrefix(k, "PAPERPLANE_") {
			continue
		}
		switch k {
		case "PWD", "OLDPWD", "SHLVL", "_", "TERM_SESSION_ID", "PS1":
			continue
		}
		if os.Getenv(k) != v {
			_ = os.Setenv(k, v)
			n++
		}
	}
	logger.Printf("loaded %d variables from login shell", n)
}

func writeRuntime(layout *paths.Layout, rt api.Runtime) error {
	b, _ := json.MarshalIndent(rt, "", "  ")
	tmp := layout.RuntimeFile() + ".tmp"
	if err := os.WriteFile(tmp, b, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, layout.RuntimeFile())
}

func readRuntime() (api.Runtime, error) {
	var rt api.Runtime
	b, err := os.ReadFile(filepath.Join(paths.DataDir(), "runtime.json"))
	if err != nil {
		return rt, err
	}
	err = json.Unmarshal(b, &rt)
	return rt, err
}

func ping(rt api.Runtime) bool {
	c := &http.Client{Timeout: 800 * time.Millisecond}
	resp, err := c.Get(fmt.Sprintf("http://127.0.0.1:%d/healthz", rt.Port))
	if err != nil {
		return false
	}
	resp.Body.Close()
	return resp.StatusCode == 200
}

// ensureDaemon 返回运行中的 daemon；没有就拉起。
// runShutdown 停掉正在运行的内核与全部运行（安装包升级 / 卸载前用）；没有内核在跑时什么也不做，也不会拉起新的。
func runShutdown() error {
	rt, err := readRuntime()
	if err != nil || !ping(rt) {
		fmt.Println("daemon not running")
		return nil
	}
	c := &client{rt: rt, hc: &http.Client{Timeout: 15 * time.Second}}
	_ = c.call("system.shutdown", map[string]bool{"stopRuns": true}, nil)
	for i := 0; i < 150 && proc.Alive(rt.PID); i++ {
		time.Sleep(100 * time.Millisecond)
	}
	if proc.Alive(rt.PID) {
		proc.KillGroup(rt.PID)
		_ = proc.Kill(rt.PID)
	}
	fmt.Printf("daemon stopped (pid %d)\n", rt.PID)
	return nil
}

func ensureDaemon() (api.Runtime, error) {
	if rt, err := readRuntime(); err == nil && ping(rt) {
		return rt, nil
	}
	cmd := exec.Command(exePath(), "daemon", "--detach")
	cmd.Stdout, cmd.Stderr = io.Discard, os.Stderr
	if err := cmd.Run(); err != nil {
		return api.Runtime{}, err
	}
	rt, err := readRuntime()
	if err != nil || !ping(rt) {
		return rt, errors.New("daemon not reachable")
	}
	return rt, nil
}

// ---------------------------------------------------------------------------
// 客户端调用

type client struct {
	rt api.Runtime
	hc *http.Client
}

func newClient() (*client, error) {
	rt, err := ensureDaemon()
	if err != nil {
		return nil, err
	}
	return &client{rt: rt, hc: &http.Client{}}, nil
}

func (c *client) base() string { return fmt.Sprintf("http://127.0.0.1:%d", c.rt.Port) }

func (c *client) call(op string, in any, out any) error {
	var body []byte
	switch v := in.(type) {
	case nil:
		body = []byte("{}")
	case []byte:
		body = v
	case string:
		body = []byte(v)
	default:
		body, _ = json.Marshal(v)
	}
	req, _ := http.NewRequest(http.MethodPost, c.base()+"/api/v1/call/"+op, bytes.NewReader(body))
	req.Header.Set("Authorization", "Bearer "+c.rt.Token)
	req.Header.Set("Content-Type", "application/json")
	resp, err := c.hc.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	var env struct {
		Result json.RawMessage `json:"result"`
		Error  *struct {
			Code    string `json:"code"`
			Message string `json:"message"`
		} `json:"error"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&env); err != nil {
		return err
	}
	if env.Error != nil {
		return fmt.Errorf("%s: %s", env.Error.Code, env.Error.Message)
	}
	if out != nil {
		if raw, ok := out.(*json.RawMessage); ok {
			*raw = env.Result
			return nil
		}
		return json.Unmarshal(env.Result, out)
	}
	return nil
}

func runCall(args []string) error {
	if len(args) < 1 {
		return errors.New("usage: paperplane call <op> ['<json>']")
	}
	c, err := newClient()
	if err != nil {
		return err
	}
	in := "{}"
	if len(args) > 1 {
		in = args[1]
		if in == "-" {
			b, _ := io.ReadAll(os.Stdin)
			in = string(b)
		}
	}
	var raw json.RawMessage
	if err := c.call(args[0], in, &raw); err != nil {
		return err
	}
	var pretty bytes.Buffer
	if json.Indent(&pretty, raw, "", "  ") == nil {
		fmt.Println(pretty.String())
	} else {
		fmt.Println(string(raw))
	}
	return nil
}

func runOps() error {
	c, err := newClient()
	if err != nil {
		return err
	}
	req, _ := http.NewRequest(http.MethodGet, c.base()+"/api/v1/ops", nil)
	req.Header.Set("Authorization", "Bearer "+c.rt.Token)
	resp, err := c.hc.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	var list []struct {
		Name        string `json:"name"`
		Description string `json:"description"`
		ReadOnly    bool   `json:"readOnly"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&list); err != nil {
		return err
	}
	for _, o := range list {
		d := o.Description
		if len(d) > 90 {
			d = d[:90] + "…"
		}
		fmt.Printf("%-28s %s\n", o.Name, d)
	}
	return nil
}

type summary struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	Path   string `json:"path"`
	Status string `json:"status"`
	Facets []struct {
		Name string `json:"name"`
	} `json:"facets"`
	Runs []struct {
		ID    string   `json:"id"`
		Name  string   `json:"name"`
		State string   `json:"state"`
		URLs  []string `json:"urls"`
	} `json:"runs"`
	Configs []struct {
		ID   string `json:"id"`
		Name string `json:"name"`
	} `json:"configs"`
	DefaultConfigID string `json:"defaultConfigId"`
}

func (c *client) findProject(q string) (*summary, error) {
	var list []summary
	if err := c.call("project.list", nil, &list); err != nil {
		return nil, err
	}
	abs, _ := filepath.Abs(q)
	for i := range list {
		p := &list[i]
		if p.ID == q || strings.EqualFold(p.Name, q) || p.Path == abs {
			return p, nil
		}
	}
	return nil, fmt.Errorf("project %q not found", q)
}

func runLs(args []string) error {
	c, err := newClient()
	if err != nil {
		return err
	}
	in := map[string]string{}
	if len(args) > 0 {
		var ws []struct {
			ID   string `json:"id"`
			Name string `json:"name"`
		}
		_ = c.call("workspace.list", nil, &ws)
		for _, w := range ws {
			if w.ID == args[0] || strings.EqualFold(w.Name, args[0]) {
				in["workspaceId"] = w.ID
			}
		}
	}
	var list []summary
	if err := c.call("project.list", in, &list); err != nil {
		return err
	}
	for _, p := range list {
		var fs []string
		for _, f := range p.Facets {
			fs = append(fs, f.Name)
		}
		line := fmt.Sprintf("%-9s %-32s %s", p.Status, p.Name, strings.Join(fs, " · "))
		for _, r := range p.Runs {
			line += fmt.Sprintf("\n          └ %s [%s] %s", r.Name, r.State, strings.Join(r.URLs, " "))
		}
		fmt.Println(line)
	}
	return nil
}

func runImport(args []string) error {
	fs := flag.NewFlagSet("import", flag.ExitOnError)
	ws := fs.String("w", "", "workspace id or name")
	_ = fs.Parse(reorder(args))
	if fs.NArg() < 1 {
		return errors.New("usage: paperplane import <path> [-w workspace]")
	}
	c, err := newClient()
	if err != nil {
		return err
	}
	in := map[string]string{"path": fs.Arg(0)}
	if *ws != "" {
		var list []struct {
			ID   string `json:"id"`
			Name string `json:"name"`
		}
		_ = c.call("workspace.list", nil, &list)
		for _, w := range list {
			if w.ID == *ws || strings.EqualFold(w.Name, *ws) {
				in["workspaceId"] = w.ID
			}
		}
	}
	var p struct {
		ID      string `json:"id"`
		Name    string `json:"name"`
		Modules []struct {
			RelDir string `json:"relDir"`
			Facets []struct {
				ID string `json:"id"`
			} `json:"facets"`
		} `json:"modules"`
	}
	if err := c.call("project.import", in, &p); err != nil {
		return err
	}
	fmt.Printf("imported %s (%s)\n", p.Name, p.ID)
	for _, m := range p.Modules {
		var ids []string
		for _, f := range m.Facets {
			ids = append(ids, f.ID)
		}
		rel := m.RelDir
		if rel == "" {
			rel = "."
		}
		fmt.Printf("  %s: %s\n", rel, strings.Join(ids, ", "))
	}
	return nil
}

func reorder(args []string) []string {
	var flags, rest []string
	for i := 0; i < len(args); i++ {
		if strings.HasPrefix(args[i], "-") {
			flags = append(flags, args[i])
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") && !strings.Contains(args[i], "=") && args[i] != "-f" {
				flags = append(flags, args[i+1])
				i++
			}
		} else {
			rest = append(rest, args[i])
		}
	}
	return append(flags, rest...)
}

func runRun(args []string) error {
	if len(args) < 1 {
		return errors.New("usage: paperplane run <project> [config]")
	}
	c, err := newClient()
	if err != nil {
		return err
	}
	p, err := c.findProject(args[0])
	if err != nil {
		return err
	}
	in := map[string]string{"projectId": p.ID}
	if len(args) > 1 {
		for _, cfg := range p.Configs {
			if cfg.ID == args[1] || strings.EqualFold(cfg.Name, args[1]) {
				in["configId"] = cfg.ID
			}
		}
		if in["configId"] == "" {
			return fmt.Errorf("config %q not found", args[1])
		}
	}
	var run struct {
		ID string `json:"id"`
	}
	if err := c.call("run.start", in, &run); err != nil {
		return err
	}
	return c.follow(run.ID, 0)
}

func runStop(args []string) error {
	if len(args) < 1 {
		return errors.New("usage: paperplane stop <project|runId>")
	}
	c, err := newClient()
	if err != nil {
		return err
	}
	if p, err := c.findProject(args[0]); err == nil {
		var r map[string]int
		if err := c.call("run.stopAll", map[string]string{"projectId": p.ID}, &r); err != nil {
			return err
		}
		fmt.Printf("stopped %d run(s)\n", r["stopped"])
		return nil
	}
	return c.call("run.stop", map[string]string{"runId": args[0]}, nil)
}

func runLogs(args []string) error {
	fs := flag.NewFlagSet("logs", flag.ExitOnError)
	follow := fs.Bool("f", false, "follow")
	n := fs.Int("n", 200, "lines")
	_ = fs.Parse(reorder(args))
	if fs.NArg() < 1 {
		return errors.New("usage: paperplane logs <project|runId> [-f] [-n 200]")
	}
	c, err := newClient()
	if err != nil {
		return err
	}
	id := fs.Arg(0)
	if p, err := c.findProject(id); err == nil {
		if len(p.Runs) > 0 {
			id = p.Runs[0].ID
		} else {
			var runs []struct {
				ID string `json:"id"`
			}
			_ = c.call("run.list", map[string]string{"projectId": p.ID}, &runs)
			if len(runs) == 0 {
				return errors.New("no runs")
			}
			id = runs[0].ID
		}
	}
	if *follow {
		return c.follow(id, *n)
	}
	var res struct {
		Lines []struct {
			Text string `json:"text"`
		} `json:"lines"`
	}
	if err := c.call("run.logs", map[string]any{"consoleId": id, "tail": *n, "raw": true}, &res); err != nil {
		return err
	}
	for _, l := range res.Lines {
		fmt.Println(l.Text)
	}
	return nil
}

// follow 跟随控制台输出直到结束（Ctrl-C 只退出跟随）。
// 输出到终端时，实时尾部（伪终端里原地刷新的进度条）画在最下面，随推送原地重画；不是终端时只打印定型行。
func (c *client) follow(id string, tail int) error {
	since := int64(0)
	if tail > 0 {
		var res struct {
			LastSeq int64 `json:"lastSeq"`
		}
		_ = c.call("run.logs", map[string]any{"consoleId": id, "tail": 1}, &res)
		since = res.LastSeq - int64(tail)
		if since < 0 {
			since = 0
		}
	}
	req, _ := http.NewRequest(http.MethodGet, fmt.Sprintf("%s/api/v1/consoles/%s/stream?since=%d", c.base(), id, since), nil)
	req.Header.Set("Authorization", "Bearer "+c.rt.Token)
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	req = req.WithContext(ctx)
	resp, err := c.hc.Do(req)
	if err != nil {
		if ctx.Err() != nil {
			return nil
		}
		return err
	}
	defer resp.Body.Close()
	tty := term.IsTerminal(int(os.Stdout.Fd()))
	cols := 0
	if tty {
		cols, _, _ = term.GetSize(int(os.Stdout.Fd()))
	}
	var live []string
	liveRows := 0
	clearLive := func() {
		if liveRows > 0 {
			fmt.Printf("\x1b[%dA\r\x1b[J", liveRows)
			liveRows = 0
		}
	}
	defer clearLive()
	sc := bufio.NewScanner(resp.Body)
	sc.Buffer(make([]byte, 1<<20), 16<<20)
	for sc.Scan() {
		line := sc.Text()
		if strings.HasPrefix(line, "event: end") {
			return nil
		}
		if !strings.HasPrefix(line, "data: ") {
			continue
		}
		var batch struct {
			Lines []struct {
				Text string `json:"text"`
			} `json:"lines"`
			Live *[]struct {
				Text string `json:"text"`
			} `json:"live"`
		}
		if json.Unmarshal([]byte(line[6:]), &batch) != nil {
			continue
		}
		if !tty {
			for _, l := range batch.Lines {
				fmt.Println(l.Text)
			}
			continue
		}
		if len(batch.Lines) == 0 && batch.Live == nil {
			continue
		}
		clearLive()
		for _, l := range batch.Lines {
			fmt.Println(l.Text)
		}
		if batch.Live != nil {
			live = live[:0]
			for _, l := range *batch.Live {
				live = append(live, l.Text)
			}
		}
		for _, l := range live {
			fmt.Println(l + "\x1b[0m")
			rows := 1
			if w := console.DisplayWidth(l); cols > 0 && w > cols {
				rows = (w + cols - 1) / cols
			}
			liveRows += rows
		}
	}
	return nil
}

func runPlugin(args []string) error {
	if len(args) >= 2 && args[0] == "pack" {
		out, err := engine.PackPlugin(args[1], "")
		if err != nil {
			return err
		}
		fmt.Println(out)
		return nil
	}
	return errors.New("usage: paperplane plugin pack <dir>")
}

// ---------------------------------------------------------------------------
// MCP stdio 桥

func runMCPBridge() error {
	c, err := newClient()
	if err != nil {
		return err
	}
	in := bufio.NewReader(os.Stdin)
	out := bufio.NewWriter(os.Stdout)
	for {
		line, err := in.ReadBytes('\n')
		if len(bytes.TrimSpace(line)) > 0 {
			resp, perr := c.postMCP(line)
			if perr != nil {
				// daemon 可能重启了：重新连接一次
				if rt, e2 := ensureDaemon(); e2 == nil {
					c.rt = rt
					resp, perr = c.postMCP(line)
				}
			}
			if perr != nil {
				var m struct {
					ID json.RawMessage `json:"id"`
				}
				_ = json.Unmarshal(line, &m)
				if len(m.ID) > 0 {
					resp, _ = json.Marshal(map[string]any{"jsonrpc": "2.0", "id": m.ID, "error": map[string]any{"code": -32603, "message": perr.Error()}})
				}
			}
			if len(resp) > 0 {
				out.Write(bytes.TrimSpace(resp))
				out.WriteByte('\n')
				out.Flush()
			}
		}
		if err != nil {
			return nil
		}
	}
}

func (c *client) postMCP(body []byte) ([]byte, error) {
	req, _ := http.NewRequest(http.MethodPost, c.base()+"/mcp", bytes.NewReader(body))
	req.Header.Set("Authorization", "Bearer "+c.rt.Token)
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json, text/event-stream")
	resp, err := c.hc.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusAccepted {
		return nil, nil
	}
	return io.ReadAll(resp.Body)
}

// ---------------------------------------------------------------------------
// askpass

func runAskpass(args []string) error {
	prompt := strings.Join(args, " ")
	rt, err := readRuntime()
	if err != nil {
		return err
	}
	c := &client{rt: rt, hc: &http.Client{Timeout: 11 * time.Minute}}
	lower := strings.ToLower(prompt)
	secret := strings.Contains(lower, "password") || strings.Contains(lower, "passphrase") || strings.Contains(lower, "token") || strings.Contains(lower, "pin")
	field := map[string]any{"key": "value", "title": strings.TrimSpace(strings.TrimSuffix(prompt, ":")), "type": "string"}
	if secret {
		field["type"] = "password"
	}
	if strings.Contains(lower, "yes/no") || strings.Contains(lower, "(yes/no") {
		field["type"] = "select"
		field["options"] = []map[string]string{{"value": "yes"}, {"value": "no"}}
	}
	var res struct {
		Cancelled bool           `json:"cancelled"`
		Values    map[string]any `json:"values"`
	}
	if err := c.call("ui.ask", map[string]any{"title": "Git", "message": prompt, "fields": []any{field}}, &res); err != nil {
		return err
	}
	if res.Cancelled {
		os.Exit(1)
	}
	fmt.Println(fmt.Sprint(res.Values["value"]))
	return nil
}

// editorDir：编辑器网页目录（PAPERPLANE_EDITOR_DIR > 包内 Resources/editor > 可执行文件旁 editor）。
func editorDir(exe string) string {
	if d := os.Getenv("PAPERPLANE_EDITOR_DIR"); d != "" {
		return d
	}
	dir := filepath.Dir(exe)
	for _, c := range []string{filepath.Join(dir, "..", "Resources", "editor"), filepath.Join(dir, "editor"), filepath.Join(dir, "..", "lib", "lemon-paperplane", "editor")} {
		if st, err := os.Stat(filepath.Join(c, "index.html")); err == nil && !st.IsDir() {
			return filepath.Clean(c)
		}
	}
	return ""
}

// bundledStore：随 App 附带的离线商店索引（PAPERPLANE_BUNDLED_STORE > 包内 Resources/store/index.json）。
func bundledStore(exe string) string {
	if p := os.Getenv("PAPERPLANE_BUNDLED_STORE"); p != "" {
		return p
	}
	dir := filepath.Dir(exe)
	for _, c := range []string{filepath.Join(dir, "..", "Resources", "store", "index.json"), filepath.Join(dir, "store", "index.json")} {
		if st, err := os.Stat(c); err == nil && !st.IsDir() {
			return filepath.Clean(c)
		}
	}
	return ""
}

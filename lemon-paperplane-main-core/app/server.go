package app

import (
	"bufio"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"golang.org/x/term"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/paths"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/remote"
)

// Embedded 是单文件版（Linux 无头版）嵌在可执行文件里的内置插件：根目录下 plugins/ 与可选的 store/。
// 由 ppbuild 生成的入口程序设置；插件程序是可执行文件自己（`<自己> __plugin <id>`）。
var Embedded fs.FS

// ---------------------------------------------------------------- paperplane serve

func runServe(args []string) error {
	fs := flag.NewFlagSet("serve", flag.ExitOnError)
	listen := fs.String("listen", "", "remote listen address (default from remote/config.json, else 0.0.0.0:47290)")
	dataDir := fs.String("data-dir", "", "data directory (default ~/.config/lemon-paperplane on Linux)")
	_ = fs.Parse(args)
	if *dataDir != "" {
		abs, err := filepath.Abs(*dataDir)
		if err != nil {
			return err
		}
		_ = os.Setenv("PAPERPLANE_DATA_DIR", abs)
	}
	return runDaemonOpts(nil, daemonOpts{serve: true, listen: *listen})
}

func printServeBanner(st remote.Status) {
	w := os.Stderr
	fmt.Fprintln(w, "")
	fmt.Fprintln(w, "Lemon Paperplane server", Version)
	if st.Running {
		fmt.Fprintln(w, "  listening:   ", st.Listen)
	} else {
		fmt.Fprintln(w, "  NOT listening:", st.Error)
	}
	for _, a := range st.Addresses {
		fmt.Fprintln(w, "  address:     ", a)
	}
	fmt.Fprintln(w, "  fingerprint: ", st.FingerprintDisplay)
	fmt.Fprintf(w, "  access keys:  %d   accounts: %d   password login: %v\n", st.Keys, st.Users, st.PasswordLogin)
	if st.Keys == 0 && st.Users == 0 {
		fmt.Fprintln(w, "  No credentials yet. In another shell run:  paperplane server key add <device-name>")
	}
	fmt.Fprintln(w, "")
}

// ---------------------------------------------------------------- paperplane server …

// serverClient：内核在跑时经本机接口操作（单一写入方）；没在跑时直接改数据目录里的文件。
type serverClient struct {
	c  *client
	rm *remote.Manager
}

func openServer() (*serverClient, error) {
	if rt, err := readRuntime(); err == nil && ping(rt) {
		return &serverClient{c: &client{rt: rt, hc: &http.Client{Timeout: 60 * time.Second}}}, nil
	}
	rm, err := remote.New(remote.Options{Dir: filepath.Join(paths.DataDir(), "remote"), Version: Version, Executable: exePath()})
	if err != nil {
		return nil, err
	}
	return &serverClient{rm: rm}, nil
}

func (s *serverClient) status() (remote.Status, error) {
	if s.c != nil {
		var st remote.Status
		return st, s.c.call("remote.status", nil, &st)
	}
	return s.rm.Status(), nil
}

var cliWho = remote.AuditEntry{Who: "cli"}

func runServerCmd(args []string) error {
	if len(args) == 0 {
		args = []string{"info"}
	}
	s, err := openServer()
	if err != nil {
		return err
	}
	switch args[0] {
	case "info", "status":
		st, err := s.status()
		if err != nil {
			return err
		}
		printServerInfo(st, s.c != nil)
		return nil
	case "enable", "disable":
		fs := flag.NewFlagSet("server "+args[0], flag.ExitOnError)
		port := fs.Int("port", 0, "listen port")
		_ = fs.Parse(args[1:])
		on := args[0] == "enable"
		p := remote.ConfigureParams{Enabled: &on}
		if *port > 0 {
			p.Port = port
		}
		st, err := s.configure(p)
		if err != nil {
			return err
		}
		printServerInfo(st, s.c != nil)
		return nil
	case "password-login":
		if len(args) < 2 || (args[1] != "on" && args[1] != "off") {
			return errors.New("usage: paperplane server password-login on|off")
		}
		on := args[1] == "on"
		_, err := s.configure(remote.ConfigureParams{PasswordLogin: &on})
		if err == nil {
			fmt.Println("password login:", args[1])
		}
		return err
	case "key", "keys":
		return s.keyCmd(args[1:])
	case "user", "users":
		return s.userCmd(args[1:])
	}
	return fmt.Errorf("unknown: paperplane server %s", args[0])
}

func (s *serverClient) configure(p remote.ConfigureParams) (remote.Status, error) {
	if s.c != nil {
		var st remote.Status
		return st, s.c.call("remote.configure", p, &st)
	}
	return s.rm.Configure(p, cliWho)
}

func printServerInfo(st remote.Status, daemon bool) {
	state := "off"
	switch {
	case st.Running:
		state = "on, listening on " + st.Listen
	case st.Enabled && !daemon:
		state = "on (the daemon is not running; start it with: paperplane serve)"
	case st.Enabled:
		state = "on, NOT listening: " + st.Error
	}
	fmt.Println("remote access:", state)
	for _, a := range st.Addresses {
		fmt.Println("  address:    ", a)
	}
	fmt.Println("  fingerprint:", st.FingerprintDisplay)
	fmt.Printf("  access keys: %d   accounts: %d   password login: %v\n", st.Keys, st.Users, st.PasswordLogin)
}

func (s *serverClient) keyCmd(args []string) error {
	if len(args) == 0 {
		args = []string{"list"}
	}
	switch args[0] {
	case "list", "ls":
		var keys []remote.KeyInfo
		if s.c != nil {
			if err := s.c.call("remote.keys", nil, &keys); err != nil {
				return err
			}
		} else {
			keys = s.rm.Keys("")
		}
		if len(keys) == 0 {
			fmt.Println("no access keys — create one: paperplane server key add <device-name>")
			return nil
		}
		for _, k := range keys {
			used := "never used"
			if k.LastUsedAt != nil {
				used = "last used " + k.LastUsedAt.Local().Format("2006-01-02 15:04") + " from " + k.LastIP
			}
			who := k.Name
			if k.User != "" {
				who = k.User + "@" + k.Name
			}
			fmt.Printf("%s  %-6s  %-28s  created %s  %s\n", k.ID, k.Kind, who, k.CreatedAt.Local().Format("2006-01-02"), used)
		}
		return nil
	case "add", "create":
		fs := flag.NewFlagSet("server key add", flag.ExitOnError)
		addr := fs.String("address", "", "extra address for the connection code (public domain, forwarded port…)")
		name, rest := firstPositional(args[1:])
		_ = fs.Parse(rest)
		if name == "" {
			name, _ = firstPositional(fs.Args())
		}
		if name == "" {
			return errors.New("usage: paperplane server key add <device-name> [--address host[:port]]")
		}
		var ck *remote.CreatedKey
		if s.c != nil {
			ck = &remote.CreatedKey{}
			if err := s.c.call("remote.keyCreate", map[string]string{"name": name, "address": *addr}, ck); err != nil {
				return err
			}
		} else {
			var err error
			if ck, err = s.rm.CreateKey(name, *addr, cliWho); err != nil {
				return err
			}
		}
		st, _ := s.status()
		fmt.Printf("Access key %s created for %q.\n\n", ck.Key.ID, ck.Key.Name)
		fmt.Println("Connection code (paste it into Add Server on the other device, or scan the QR code with the phone app;")
		fmt.Println("shown only once, treat it like a password):")
		fmt.Println()
		fmt.Println(ck.Code)
		fmt.Println()
		if term.IsTerminal(int(os.Stdout.Fd())) {
			if q, err := remote.QRTerminal(ck.Code); err == nil {
				fmt.Print(q)
				fmt.Println()
			}
		}
		fmt.Println("Certificate fingerprint:", st.FingerprintDisplay)
		if !st.Enabled {
			fmt.Println("\nRemote access is off. Turn it on with: paperplane server enable   (or run: paperplane serve)")
		}
		return nil
	case "revoke", "rm", "remove":
		if len(args) < 2 {
			return errors.New("usage: paperplane server key revoke <id>")
		}
		if s.c != nil {
			if err := s.c.call("remote.keyRevoke", map[string]string{"id": args[1]}, nil); err != nil {
				return err
			}
		} else if err := s.rm.RevokeKey(args[1], cliWho); err != nil {
			return err
		}
		fmt.Println("revoked", args[1])
		return nil
	}
	return fmt.Errorf("unknown: paperplane server key %s", args[0])
}

func (s *serverClient) userCmd(args []string) error {
	if len(args) == 0 {
		args = []string{"list"}
	}
	switch args[0] {
	case "list", "ls":
		var users []remote.UserInfo
		if s.c != nil {
			if err := s.c.call("remote.users", nil, &users); err != nil {
				return err
			}
		} else {
			users = s.rm.Users()
		}
		if len(users) == 0 {
			fmt.Println("no accounts — add one: paperplane server user set <name>")
		}
		for _, u := range users {
			fmt.Printf("%-24s  created %s  updated %s\n", u.Name, u.CreatedAt.Local().Format("2006-01-02"), u.UpdatedAt.Local().Format("2006-01-02 15:04"))
		}
		return nil
	case "set", "add", "passwd":
		fs := flag.NewFlagSet("server user set", flag.ExitOnError)
		fromStdin := fs.Bool("stdin", false, "read the password from standard input (one line)")
		name, rest := firstPositional(args[1:])
		_ = fs.Parse(rest)
		if name == "" {
			name, _ = firstPositional(fs.Args())
		}
		if name == "" {
			return errors.New("usage: paperplane server user set <name> [--stdin]")
		}
		pw, err := readPassword(*fromStdin)
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
		st, _ := s.status()
		fmt.Printf("account %q saved.\n", name)
		if !st.PasswordLogin {
			fmt.Println("Password login is off. Turn it on with: paperplane server password-login on")
		}
		return nil
	case "remove", "rm", "delete":
		if len(args) < 2 {
			return errors.New("usage: paperplane server user remove <name>")
		}
		var err error
		if s.c != nil {
			err = s.c.call("remote.userRemove", map[string]string{"name": args[1]}, nil)
		} else {
			err = s.rm.RemoveUser(args[1], cliWho)
		}
		if err == nil {
			fmt.Println("removed", args[1], "(and revoked its devices)")
		}
		return err
	}
	return fmt.Errorf("unknown: paperplane server user %s", args[0])
}

// firstPositional：允许「名称」写在参数前面（key add mac --address x）。
func firstPositional(args []string) (string, []string) {
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		return args[0], args[1:]
	}
	return "", args
}

// readPassword：终端里不回显输入两次；--stdin 从标准输入读一行。不支持命令行参数传密码（会留在进程列表和历史记录里）。
func readPassword(fromStdin bool) (string, error) {
	if fromStdin || !term.IsTerminal(int(os.Stdin.Fd())) {
		line, err := bufio.NewReader(os.Stdin).ReadString('\n')
		if err != nil && err != io.EOF {
			return "", err
		}
		pw := strings.TrimRight(line, "\r\n")
		return pw, remote.ValidatePassword(pw)
	}
	fmt.Fprint(os.Stderr, "Password: ")
	a, err := term.ReadPassword(int(os.Stdin.Fd()))
	fmt.Fprintln(os.Stderr)
	if err != nil {
		return "", err
	}
	if err := remote.ValidatePassword(string(a)); err != nil {
		return "", err
	}
	fmt.Fprint(os.Stderr, "Again: ")
	b, err := term.ReadPassword(int(os.Stdin.Fd()))
	fmt.Fprintln(os.Stderr)
	if err != nil {
		return "", err
	}
	if string(a) != string(b) {
		return "", errors.New("passwords do not match")
	}
	return string(a), nil
}

// ---------------------------------------------------------------- 单文件版：解出内置插件

// extractEmbedded 把嵌入的 plugins/、store/ 解到 $DATA/bundled/<内容哈希>/（内容没变就复用），删掉旧的。
func extractEmbedded(dataRoot string) (pluginsDir, storeIndex string, err error) {
	h := sha256.New()
	var files []string
	err = fs.WalkDir(Embedded, ".", func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if !d.IsDir() {
			files = append(files, p)
		}
		return nil
	})
	if err != nil {
		return "", "", err
	}
	sort.Strings(files)
	for _, f := range files {
		b, err := fs.ReadFile(Embedded, f)
		if err != nil {
			return "", "", err
		}
		h.Write([]byte(f))
		h.Write([]byte{0})
		h.Write(b)
	}
	sum := hex.EncodeToString(h.Sum(nil))[:16]
	base := filepath.Join(dataRoot, "bundled")
	dst := filepath.Join(base, Version+"-"+sum)
	if _, err := os.Stat(filepath.Join(dst, ".complete")); err != nil {
		tmp := dst + ".tmp"
		_ = os.RemoveAll(tmp)
		for _, f := range files {
			b, _ := fs.ReadFile(Embedded, f)
			out := filepath.Join(tmp, filepath.FromSlash(f))
			if err := os.MkdirAll(filepath.Dir(out), 0o755); err != nil {
				return "", "", err
			}
			if err := os.WriteFile(out, b, 0o644); err != nil {
				return "", "", err
			}
		}
		if err := os.WriteFile(filepath.Join(tmp, ".complete"), []byte(Version), 0o644); err != nil {
			return "", "", err
		}
		_ = os.RemoveAll(dst)
		if err := os.Rename(tmp, dst); err != nil {
			return "", "", err
		}
	}
	if ents, err := os.ReadDir(base); err == nil {
		for _, e := range ents {
			if p := filepath.Join(base, e.Name()); p != dst {
				_ = os.RemoveAll(p)
			}
		}
	}
	pluginsDir = filepath.Join(dst, "plugins")
	if st, err := os.Stat(filepath.Join(dst, "store", "index.json")); err == nil && !st.IsDir() {
		storeIndex = filepath.Join(dst, "store", "index.json")
	}
	return pluginsDir, storeIndex, nil
}

package remote

import (
	"context"
	"crypto/tls"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Options 构造 Manager。
type Options struct {
	Dir        string       // $DATA/remote
	Handler    http.Handler // 内核 API（不含本机令牌校验）；只读命令行用时为 nil
	Logger     *log.Logger
	Version    string
	Executable string // 开机启动用
	// Serve：paperplane serve 前台运行时强制打开远程监听（不改配置文件）；Listen 覆盖监听地址
	Serve  bool
	Listen string
}

// Manager 管理远程监听、密钥、账号与审计日志。
type Manager struct {
	opt      Options
	hostname string
	audit    *auditLog
	lim      *limiter

	mu         sync.Mutex
	cfg        Config
	creds      credentials
	cert       *tls.Certificate
	customCert bool
	srv        *http.Server
	ln         net.Listener
	lastErr    string
	dirtyUsed  bool
	allow      []*net.IPNet

	// 每条密钥正在进行的请求 / 长连接（事件流、终端流、端口转发隧道）：吊销时立即切断
	amu    sync.Mutex
	anext  int
	active map[string]map[int]func()
}

// track 登记一条密钥的进行中连接，返回注销函数。
func (m *Manager) track(keyID string, cut func()) func() {
	m.amu.Lock()
	defer m.amu.Unlock()
	if m.active == nil {
		m.active = map[string]map[int]func(){}
	}
	if m.active[keyID] == nil {
		m.active[keyID] = map[int]func(){}
	}
	m.anext++
	n := m.anext
	m.active[keyID][n] = cut
	return func() {
		m.amu.Lock()
		delete(m.active[keyID], n)
		if len(m.active[keyID]) == 0 {
			delete(m.active, keyID)
		}
		m.amu.Unlock()
	}
}

// cutKeys 切断这些密钥上所有进行中的请求和长连接。
func (m *Manager) cutKeys(ids ...string) {
	m.amu.Lock()
	var cuts []func()
	for _, id := range ids {
		for _, c := range m.active[id] {
			cuts = append(cuts, c)
		}
	}
	m.amu.Unlock()
	for _, c := range cuts {
		c()
	}
}

// Status 是 remote.status 的结果。
type Status struct {
	Enabled            bool     `json:"enabled"`
	Running            bool     `json:"running"`
	Error              string   `json:"error,omitempty"`
	Listen             string   `json:"listen"`
	Port               int      `json:"port"`
	Addresses          []string `json:"addresses"`
	Fingerprint        string   `json:"fingerprint"`
	FingerprintDisplay string   `json:"fingerprintDisplay"`
	CustomCert         bool     `json:"customCert"`
	PasswordLogin      bool     `json:"passwordLogin"`
	Autostart          bool     `json:"autostart"`
	AutostartSupported bool     `json:"autostartSupported"`
	AllowCIDRs         []string `json:"allowCIDRs"`
	Keys               int      `json:"keys"`
	Users              int      `json:"users"`
	Hostname           string   `json:"hostname"`
}

// New 读配置与凭据（手写在 config.json 里的明文密码换成哈希）。
func New(opt Options) (*Manager, error) {
	if opt.Logger == nil {
		opt.Logger = log.New(os.Stderr, "", log.LstdFlags)
	}
	if err := os.MkdirAll(opt.Dir, 0o700); err != nil {
		return nil, err
	}
	host, _ := os.Hostname()
	m := &Manager{opt: opt, hostname: host, audit: &auditLog{path: filepath.Join(opt.Dir, "audit.log")}, lim: newLimiter()}
	if err := readJSON(m.cfgPath(), &m.cfg); err != nil && !os.IsNotExist(err) {
		return nil, errors.New(i18n.T("remote.readFile", m.cfgPath(), err))
	}
	if err := readJSON(m.credPath(), &m.creds); err != nil && !os.IsNotExist(err) {
		return nil, errors.New(i18n.T("remote.readFile", m.credPath(), err))
	}
	if len(m.cfg.Users) > 0 {
		for _, u := range m.cfg.Users {
			if err := m.setUserLocked(u.Name, u.Password); err != nil {
				return nil, errors.New(i18n.T("remote.configUser", u.Name, err))
			}
			m.opt.Logger.Printf("remote: password of account %s moved to credentials.json as a hash", u.Name)
		}
		m.cfg.Users = nil
		if err := m.saveLocked(); err != nil {
			return nil, err
		}
	}
	m.allow = parseCIDRs(m.cfg.AllowCIDRs)
	return m, nil
}

func (m *Manager) cfgPath() string  { return filepath.Join(m.opt.Dir, "config.json") }
func (m *Manager) credPath() string { return filepath.Join(m.opt.Dir, "credentials.json") }

func (m *Manager) saveLocked() error {
	if err := writeJSON(m.cfgPath(), m.cfg, 0o600); err != nil {
		return err
	}
	return writeJSON(m.credPath(), m.creds, 0o600)
}

// Audit 记一条审计日志。
func (m *Manager) Audit(e AuditEntry) { m.audit.add(e) }

// AuditRecent 返回最近的审计日志。
func (m *Manager) AuditRecent(limit int) []AuditEntry { return m.audit.recent(limit) }

// ---------------------------------------------------------------- 监听

func (m *Manager) shouldRun() bool { return m.cfg.Enabled || m.opt.Serve }

func (m *Manager) listenAddr() string {
	if m.opt.Listen != "" {
		return m.opt.Listen
	}
	return m.cfg.ListenAddr()
}

// Start 按配置打开远程监听（没开启时什么都不做）。错误记在状态里，不影响本机使用。
func (m *Manager) Start() {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.restartLocked()
	if m.cfg.Autostart {
		_ = setAutostart(m.opt.Executable, true) // 可执行文件可能挪过位置：每次启动刷新一次
	}
	go m.flushLoop()
}

func (m *Manager) restartLocked() {
	m.stopLocked()
	m.lastErr = ""
	if !m.shouldRun() || m.opt.Handler == nil {
		return
	}
	cert, custom, err := loadOrCreateCert(m.opt.Dir, &m.cfg, m.hostname, true)
	if err != nil {
		m.lastErr = err.Error()
		m.opt.Logger.Printf("remote: %v", err)
		return
	}
	m.cert, m.customCert = cert, custom
	addr := m.listenAddr()
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		m.lastErr = err.Error()
		m.opt.Logger.Printf("remote: listen %s: %v", addr, err)
		return
	}
	tcfg := &tls.Config{MinVersion: tls.VersionTLS12, Certificates: []tls.Certificate{*cert}, NextProtos: []string{"h2", "http/1.1"}}
	m.ln = ln
	m.srv = &http.Server{
		Handler:           m.guard(m.opt.Handler),
		TLSConfig:         tcfg,
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       2 * time.Minute,
		MaxHeaderBytes:    64 << 10,
		ErrorLog:          log.New(discard{}, "", 0), // TLS 握手失败等噪声不进日志
	}
	srv := m.srv
	go func() {
		if err := srv.ServeTLS(ln, "", ""); err != nil && !errors.Is(err, http.ErrServerClosed) {
			m.opt.Logger.Printf("remote: serve: %v", err)
		}
	}()
	m.opt.Logger.Printf("remote: listening on https://%s (fingerprint %s)", ln.Addr(), Fingerprint(cert.Leaf))
	if os.Geteuid() == 0 {
		m.opt.Logger.Printf("remote: WARNING running as root — anyone who can connect gets root; run as a normal user")
	}
}

type discard struct{}

func (discard) Write(p []byte) (int, error) { return len(p), nil }

func (m *Manager) stopLocked() {
	if m.srv != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		_ = m.srv.Shutdown(ctx)
		cancel()
		_ = m.srv.Close()
	}
	m.srv, m.ln = nil, nil
}

// Close 关闭监听并写回最后使用时间。
func (m *Manager) Close() {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.stopLocked()
	if m.dirtyUsed {
		_ = writeJSON(m.credPath(), m.creds, 0o600)
		m.dirtyUsed = false
	}
}

// flushLoop：密钥最后使用时间最多每分钟写一次盘。
func (m *Manager) flushLoop() {
	t := time.NewTicker(time.Minute)
	defer t.Stop()
	for range t.C {
		m.mu.Lock()
		if m.dirtyUsed {
			_ = writeJSON(m.credPath(), m.creds, 0o600)
			m.dirtyUsed = false
		}
		m.mu.Unlock()
	}
}

// ---------------------------------------------------------------- 状态与配置

// Status 返回当前状态（只读已有的证书；证书在打开远程或生成密钥时才生成，没有时指纹为空）。
func (m *Manager) Status() Status {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.statusLocked()
}

func (m *Manager) statusLocked() Status {
	if m.cert == nil {
		if c, custom, err := loadOrCreateCert(m.opt.Dir, &m.cfg, m.hostname, false); err == nil {
			m.cert, m.customCert = c, custom
		}
	}
	st := Status{
		Enabled: m.cfg.Enabled, Running: m.ln != nil, Error: m.lastErr, Listen: m.listenAddr(),
		PasswordLogin: m.cfg.PasswordLogin, Autostart: m.cfg.Autostart, AutostartSupported: autostartSupported,
		AllowCIDRs: append([]string{}, m.cfg.AllowCIDRs...), Keys: len(m.creds.Keys), Users: len(m.creds.Users),
		Hostname: m.hostname, CustomCert: m.customCert,
	}
	if m.opt.Serve {
		st.Enabled = true
	}
	_, ps, _ := net.SplitHostPort(st.Listen)
	st.Port, _ = strconv.Atoi(ps)
	if m.ln != nil {
		st.Port = m.ln.Addr().(*net.TCPAddr).Port
	}
	if m.cert != nil && m.cert.Leaf != nil {
		st.Fingerprint = Fingerprint(m.cert.Leaf)
		st.FingerprintDisplay = FingerprintDisplay(st.Fingerprint)
	}
	st.Addresses = Addresses(st.Listen, st.Port, m.hostname)
	return st
}

// ConfigureParams 是 remote.configure 的参数（只改给出的字段）。
type ConfigureParams struct {
	Enabled       *bool     `json:"enabled"`
	Port          *int      `json:"port"`
	PasswordLogin *bool     `json:"passwordLogin"`
	Autostart     *bool     `json:"autostart"`
	AllowCIDRs    *[]string `json:"allowCIDRs"`
}

// Configure 改配置并按需重启监听。
func (m *Manager) Configure(p ConfigureParams, who AuditEntry) (Status, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	restart := false
	var changes []string
	if p.Port != nil {
		if *p.Port < 1 || *p.Port > 65535 {
			return Status{}, errors.New(i18n.T("remote.portRange"))
		}
		host, _, err := net.SplitHostPort(m.cfg.ListenAddr())
		if err != nil {
			host = "0.0.0.0"
		}
		m.cfg.Listen = net.JoinHostPort(host, strconv.Itoa(*p.Port))
		restart = true
		changes = append(changes, "port="+strconv.Itoa(*p.Port))
	}
	if p.AllowCIDRs != nil {
		for _, c := range *p.AllowCIDRs {
			if _, _, err := net.ParseCIDR(strings.TrimSpace(c)); err != nil && net.ParseIP(strings.TrimSpace(c)) == nil {
				return Status{}, errors.New(i18n.T("remote.badCIDR", c))
			}
		}
		m.cfg.AllowCIDRs = *p.AllowCIDRs
		m.allow = parseCIDRs(m.cfg.AllowCIDRs)
		changes = append(changes, "allowCIDRs")
	}
	if p.PasswordLogin != nil {
		m.cfg.PasswordLogin = *p.PasswordLogin
		changes = append(changes, fmt.Sprintf("passwordLogin=%v", *p.PasswordLogin))
	}
	if p.Autostart != nil {
		if *p.Autostart && !autostartSupported {
			return Status{}, errors.New(i18n.T("remote.autostartUnsupported"))
		}
		if err := setAutostart(m.opt.Executable, *p.Autostart); err != nil {
			return Status{}, errors.New(i18n.T("remote.autostartFailed", err))
		}
		m.cfg.Autostart = *p.Autostart
		changes = append(changes, fmt.Sprintf("autostart=%v", *p.Autostart))
	}
	if p.Enabled != nil && *p.Enabled != m.cfg.Enabled {
		m.cfg.Enabled = *p.Enabled
		restart = true
		changes = append(changes, fmt.Sprintf("enabled=%v", *p.Enabled))
	}
	if err := m.saveLocked(); err != nil {
		return Status{}, err
	}
	if restart {
		m.restartLocked()
	}
	if len(changes) > 0 {
		who.Event, who.Detail = "config", strings.Join(changes, " ")
		m.audit.add(who)
	}
	return m.statusLocked(), nil
}

// ---------------------------------------------------------------- 密钥

// Keys 列出密钥；current 是当前请求所用密钥的编号。
func (m *Manager) Keys(current string) []KeyInfo {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]KeyInfo, 0, len(m.creds.Keys))
	for _, k := range m.creds.Keys {
		out = append(out, KeyInfo{ID: k.ID, Name: k.Name, Kind: k.Kind, User: k.User, CreatedAt: k.CreatedAt,
			LastUsedAt: k.LastUsedAt, LastIP: k.LastIP, Current: k.ID == current})
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].CreatedAt.After(out[j].CreatedAt) })
	return out
}

// CreatedKey 是新建密钥的结果（token、code 只出现这一次）。
type CreatedKey struct {
	Key   KeyInfo `json:"key"`
	Token string  `json:"token"`
	Code  string  `json:"code"`
}

// CreateKey 生成访问密钥。address 是额外的连接地址（外网域名等）。
func (m *Manager) CreateKey(name, address string, who AuditEntry) (*CreatedKey, error) {
	name = cleanName(name, 64)
	if name == "" {
		return nil, errors.New(i18n.T("remote.keyName"))
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	// 连接码里要带证书指纹：还没有证书就现在生成（之后打开远程时用同一份）
	if m.cert == nil {
		c, custom, err := loadOrCreateCert(m.opt.Dir, &m.cfg, m.hostname, true)
		if err != nil {
			return nil, err
		}
		m.cert, m.customCert = c, custom
	}
	k, tok := m.newKeyLocked(name, "key", "")
	if err := writeJSON(m.credPath(), m.creds, 0o600); err != nil {
		return nil, err
	}
	who.Event, who.Detail = "key.create", k.ID+" "+name
	m.audit.add(who)
	st := m.statusLocked()
	urls := st.Addresses
	if a := strings.TrimSpace(address); a != "" {
		if u, err := NormalizeAddress(a, st.Port); err == nil {
			urls = append([]string{u}, urls...)
		}
	}
	code := EncodeCode(CodePayload{URLs: urls, Fingerprint: st.Fingerprint, Token: tok, Name: m.hostname})
	return &CreatedKey{Key: KeyInfo{ID: k.ID, Name: k.Name, Kind: k.Kind, CreatedAt: k.CreatedAt}, Token: tok, Code: code}, nil
}

func (m *Manager) newKeyLocked(name, kind, user string) (*Key, string) {
	id, tok, hash := newToken()
	for m.keyByIDLocked(id) != nil {
		id, tok, hash = newToken()
	}
	k := &Key{ID: id, Name: name, Kind: kind, User: user, Hash: hash, CreatedAt: time.Now()}
	m.creds.Keys = append(m.creds.Keys, k)
	return k, tok
}

func (m *Manager) keyByIDLocked(id string) *Key {
	for _, k := range m.creds.Keys {
		if k.ID == id {
			return k
		}
	}
	return nil
}

// RevokeKey 吊销密钥（立即生效：后续请求都会被拒绝）。
func (m *Manager) RevokeKey(id string, who AuditEntry) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	k := m.keyByIDLocked(id)
	if k == nil {
		return ErrNotFound
	}
	m.removeKeysLocked(func(x *Key) bool { return x.ID == id })
	if err := writeJSON(m.credPath(), m.creds, 0o600); err != nil {
		return err
	}
	who.Event, who.Detail = "key.revoke", id+" "+k.Name
	m.audit.add(who)
	go m.cutKeys(id)
	return nil
}

func (m *Manager) removeKeysLocked(match func(*Key) bool) {
	kept := m.creds.Keys[:0]
	for _, k := range m.creds.Keys {
		if !match(k) {
			kept = append(kept, k)
		}
	}
	m.creds.Keys = kept
}

// ---------------------------------------------------------------- 账号

// Users 列出账号。
func (m *Manager) Users() []UserInfo {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]UserInfo, 0, len(m.creds.Users))
	for _, u := range m.creds.Users {
		out = append(out, UserInfo{Name: u.Name, CreatedAt: u.CreatedAt, UpdatedAt: u.UpdatedAt})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// SetUser 新建账号或改密码。
func (m *Manager) SetUser(name, password string, who AuditEntry) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.setUserLocked(name, password); err != nil {
		return err
	}
	if err := writeJSON(m.credPath(), m.creds, 0o600); err != nil {
		return err
	}
	who.Event, who.Detail = "user.set", name
	m.audit.add(who)
	return nil
}

func (m *Manager) setUserLocked(name, password string) error {
	if !ValidUserName(name) {
		return errors.New(i18n.T("remote.userName"))
	}
	if err := ValidatePassword(password); err != nil {
		return err
	}
	h, err := HashPassword(password)
	if err != nil {
		return err
	}
	now := time.Now()
	for _, u := range m.creds.Users {
		if u.Name == name {
			u.Hash, u.UpdatedAt = h, now
			return nil
		}
	}
	m.creds.Users = append(m.creds.Users, &User{Name: name, Hash: h, CreatedAt: now, UpdatedAt: now})
	return nil
}

// RemoveUser 删除账号，并吊销它登录过的设备。
func (m *Manager) RemoveUser(name string, who AuditEntry) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	found := false
	kept := m.creds.Users[:0]
	for _, u := range m.creds.Users {
		if u.Name == name {
			found = true
			continue
		}
		kept = append(kept, u)
	}
	if !found {
		return ErrNotFound
	}
	m.creds.Users = kept
	var cut []string
	for _, k := range m.creds.Keys {
		if k.Kind == "device" && k.User == name {
			cut = append(cut, k.ID)
		}
	}
	m.removeKeysLocked(func(k *Key) bool { return k.Kind == "device" && k.User == name })
	go m.cutKeys(cut...)
	if err := writeJSON(m.credPath(), m.creds, 0o600); err != nil {
		return err
	}
	who.Event, who.Detail = "user.remove", name
	m.audit.add(who)
	return nil
}

// ---------------------------------------------------------------- 连接码

// CodePayload 是连接码里的内容。
type CodePayload struct {
	URLs        []string `json:"u"`
	Fingerprint string   `json:"f"`
	Token       string   `json:"k"`
	Name        string   `json:"n,omitempty"`
}

const codePrefix = "ppc1_"

// EncodeCode 生成连接码。
func EncodeCode(p CodePayload) string {
	b, _ := json.Marshal(p)
	return codePrefix + base64.RawURLEncoding.EncodeToString(b)
}

// DecodeCode 解析连接码（容忍首尾空白和换行）。
func DecodeCode(code string) (*CodePayload, error) {
	code = strings.Join(strings.Fields(code), "")
	if !strings.HasPrefix(code, codePrefix) {
		return nil, errors.New(i18n.T("remote.codePrefix"))
	}
	b, err := base64.RawURLEncoding.DecodeString(strings.TrimPrefix(code, codePrefix))
	if err != nil {
		return nil, errors.New(i18n.T("remote.codeIncomplete"))
	}
	var p CodePayload
	if err := json.Unmarshal(b, &p); err != nil || len(p.URLs) == 0 || p.Token == "" || p.Fingerprint == "" {
		return nil, errors.New(i18n.T("remote.codeIncomplete"))
	}
	if _, _, ok := parseToken(p.Token); !ok {
		return nil, errors.New(i18n.T("remote.codeKey"))
	}
	return &p, nil
}

// NormalizeAddress：「主机」「主机:端口」「https://…」都转成 https://主机:端口。
// 没写协议也没写端口时用默认端口；写了 https:// 但没写端口时用 443（反向代理的情况）。
func NormalizeAddress(addr string, defPort int) (string, error) {
	a := strings.TrimSpace(addr)
	if a == "" {
		return "", errors.New(i18n.T("remote.addrEmpty"))
	}
	if defPort == 0 {
		defPort = DefaultPort
	}
	if strings.HasPrefix(strings.ToLower(a), "http://") {
		return "", errors.New(i18n.T("remote.httpsOnly"))
	}
	scheme := strings.HasPrefix(strings.ToLower(a), "https://")
	a = strings.TrimSuffix(strings.TrimPrefix(strings.TrimPrefix(a, "https://"), "HTTPS://"), "/")
	if i := strings.Index(a, "/"); i >= 0 {
		a = a[:i]
	}
	host, port, err := net.SplitHostPort(a)
	if err != nil {
		host = strings.Trim(a, "[]")
		port = strconv.Itoa(defPort)
		if scheme {
			port = "443"
		}
	}
	if host == "" {
		return "", errors.New(i18n.T("remote.addrNoHost"))
	}
	if p, err := strconv.Atoi(port); err != nil || p < 1 || p > 65535 {
		return "", errors.New(i18n.T("remote.badPort", port))
	}
	return "https://" + net.JoinHostPort(host, port), nil
}

// ---------------------------------------------------------------- 地址

// Addresses 猜其他设备能用的地址：各网卡 IP（跳过回环、链路本地、虚拟网卡、代理 TUN），最后是主机名。
func Addresses(listen string, port int, hostname string) []string {
	host, _, _ := net.SplitHostPort(listen)
	if host != "" && host != "0.0.0.0" && host != "::" {
		return []string{"https://" + net.JoinHostPort(host, strconv.Itoa(port))}
	}
	var v4, v6 []string
	ifs, _ := net.Interfaces()
	for _, ifc := range ifs {
		if ifc.Flags&net.FlagUp == 0 || ifc.Flags&net.FlagLoopback != 0 || virtualIface(ifc.Name) {
			continue
		}
		addrs, _ := ifc.Addrs()
		for _, a := range addrs {
			ipn, ok := a.(*net.IPNet)
			if !ok || ipn.IP.IsLoopback() || ipn.IP.IsLinkLocalUnicast() || benchmarkRange.Contains(ipn.IP) {
				continue
			}
			u := "https://" + net.JoinHostPort(ipn.IP.String(), strconv.Itoa(port))
			if ipn.IP.To4() != nil {
				v4 = append(v4, u)
			} else {
				v6 = append(v6, u)
			}
		}
	}
	// IPv6 往往有好几个临时地址，只取第一个，免得连接码太长
	if len(v6) > 1 {
		v6 = v6[:1]
	}
	out := append(v4, v6...)
	if hostname != "" {
		out = append(out, "https://"+net.JoinHostPort(hostname, strconv.Itoa(port)))
	}
	return out
}

// 198.18.0.0/15：Clash 等代理软件 TUN 网卡常用的地址段
var _, benchmarkRange, _ = net.ParseCIDR("198.18.0.0/15")

func virtualIface(name string) bool {
	n := strings.ToLower(name)
	// 不排除 utun / tun / wg：Tailscale、WireGuard 等 VPN 地址正是远程连接常用的
	for _, p := range []string{"docker", "br-", "veth", "virbr", "vethernet", "vmnet", "vboxnet", "llw", "awdl", "anpi", "bridge"} {
		if strings.HasPrefix(n, p) {
			return true
		}
	}
	return false
}

func parseCIDRs(list []string) []*net.IPNet {
	var out []*net.IPNet
	for _, c := range list {
		c = strings.TrimSpace(c)
		if c == "" {
			continue
		}
		if !strings.Contains(c, "/") {
			if ip := net.ParseIP(c); ip != nil {
				if ip.To4() != nil {
					c += "/32"
				} else {
					c += "/128"
				}
			}
		}
		if _, n, err := net.ParseCIDR(c); err == nil {
			out = append(out, n)
		}
	}
	return out
}

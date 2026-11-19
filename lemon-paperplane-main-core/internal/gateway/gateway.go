// Package gateway 是客户端的远程网关：本机内核给每台远程服务器开一个回环端口（形式和本机接口一样：
// http://127.0.0.1:端口 + 令牌），把请求加上访问密钥、经固定指纹的 TLS 转发给服务器。
// 界面切换服务器只是换端口和令牌；编辑器网页（/editor/）用本机的；端口转发把服务器 localhost 的端口转到本机。
package gateway

import (
	"bufio"
	"context"
	"crypto/rand"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/remote"
)

// Error 是带错误码的网关错误（API 层按 Code/Status 返回）。
type Error struct {
	C    string
	Msg  string
	St   int
	Info any
}

func (e *Error) Error() string { return e.Msg }

// Code 是 API 错误码：unreachable / fingerprint_mismatch / unauthorized / rate_limited / incompatible / password_disabled。
func (e *Error) Code() string { return e.C }

// Status 是 HTTP 状态。
func (e *Error) Status() int { return e.St }

// Data 是附加数据（指纹不符时是服务器现在的指纹）。
func (e *Error) Data() any { return e.Info }

func errUnreachable(err error) *Error {
	return &Error{C: "unreachable", Msg: friendlyNetErr(err), St: 502}
}

func friendlyNetErr(err error) string {
	var fe *fingerprintError
	if errors.As(err, &fe) {
		return i18n.T("gw.certMismatch")
	}
	var ne net.Error
	if errors.As(err, &ne) && ne.Timeout() {
		return i18n.T("gw.timeout")
	}
	msg := err.Error()
	switch {
	case strings.Contains(msg, "connection refused"), strings.Contains(msg, "actively refused"):
		return i18n.T("gw.refused")
	case strings.Contains(msg, "no such host"):
		return i18n.T("gw.noHost")
	case strings.Contains(msg, "first record does not look like a TLS handshake"):
		return i18n.T("gw.notTLS")
	}
	return msg
}

type fingerprintError struct{ got string }

func (e *fingerprintError) Error() string { return "certificate fingerprint mismatch: " + e.got }

// Manager 管理全部网关。
type Manager struct {
	EditorDir string
	Version   string
	Logger    *log.Logger

	mu    sync.Mutex
	conns map[string]*Conn
}

// New 创建网关管理器。
func New(editorDir, version string, logger *log.Logger) *Manager {
	if logger == nil {
		logger = log.New(os.Stderr, "", log.LstdFlags)
	}
	return &Manager{EditorDir: editorDir, Version: version, Logger: logger, conns: map[string]*Conn{}}
}

// Conn 是到一台服务器的网关。
type Conn struct {
	ID          string
	base        *url.URL
	fingerprint string
	trusted     bool
	token       string
	localToken  string
	ln          net.Listener
	srv         *http.Server
	port        int
	client      *http.Client
	mgr         *Manager

	fmu      sync.Mutex
	forwards map[int]net.Listener
}

func randHex(n int) string {
	b := make([]byte, n)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// tlsConfig：CA 签发的证书走正常校验；自签证书只认保存的指纹（不看主机名，等同 SSH 的 known_hosts）。
func tlsConfig(u *url.URL, fingerprint string, trusted bool, alpn []string) *tls.Config {
	if trusted {
		return &tls.Config{ServerName: u.Hostname(), MinVersion: tls.VersionTLS12, NextProtos: alpn}
	}
	return &tls.Config{
		MinVersion:         tls.VersionTLS12,
		InsecureSkipVerify: true, // 由 VerifyConnection 按指纹校验
		NextProtos:         alpn,
		VerifyConnection: func(cs tls.ConnectionState) error {
			if len(cs.PeerCertificates) == 0 {
				return errors.New("no certificate")
			}
			got := remote.Fingerprint(cs.PeerCertificates[0])
			if !remote.SameFingerprint(got, fingerprint) {
				return &fingerprintError{got: got}
			}
			return nil
		},
	}
}

func newClient(u *url.URL, fingerprint string, trusted bool) *http.Client {
	tr := &http.Transport{
		Proxy:               nil, // 不走系统代理：内网地址经代理会连不上
		DialContext:         (&net.Dialer{Timeout: 10 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		TLSClientConfig:     tlsConfig(u, fingerprint, trusted, nil),
		ForceAttemptHTTP2:   true,
		TLSHandshakeTimeout: 10 * time.Second,
		MaxIdleConnsPerHost: 8,
		IdleConnTimeout:     90 * time.Second,
	}
	return &http.Client{Transport: tr}
}

func parseURL(s string) (*url.URL, error) {
	n, err := remote.NormalizeAddress(s, remote.DefaultPort)
	if err != nil {
		return nil, &Error{C: "bad_request", Msg: err.Error(), St: 400}
	}
	return url.Parse(n)
}

// ---------------------------------------------------------------- 探测、登录

// ProbeResult 是 gateway.probe 的结果。
type ProbeResult struct {
	URL                string       `json:"url"`
	Fingerprint        string       `json:"fingerprint"`
	FingerprintDisplay string       `json:"fingerprintDisplay"`
	Trusted            bool         `json:"trusted"`
	Hello              remote.Hello `json:"hello"`
}

// Probe 连上服务器取证书指纹（不校验）和 hello；证书能被系统信任时 trusted=true。
func (m *Manager) Probe(ctx context.Context, address string) (*ProbeResult, error) {
	u, err := parseURL(address)
	if err != nil {
		return nil, err
	}
	d := &tls.Dialer{NetDialer: &net.Dialer{Timeout: 10 * time.Second}, Config: &tls.Config{InsecureSkipVerify: true, MinVersion: tls.VersionTLS12}}
	c, err := d.DialContext(ctx, "tcp", u.Host)
	if err != nil {
		return nil, errUnreachable(err)
	}
	cs := c.(*tls.Conn).ConnectionState()
	_ = c.Close()
	if len(cs.PeerCertificates) == 0 {
		return nil, &Error{C: "unreachable", Msg: i18n.T("gw.noCert"), St: 502}
	}
	leaf := cs.PeerCertificates[0]
	fp := remote.Fingerprint(leaf)
	trusted := false
	inter := x509.NewCertPool()
	for _, c := range cs.PeerCertificates[1:] {
		inter.AddCert(c)
	}
	if _, err := leaf.Verify(x509.VerifyOptions{DNSName: u.Hostname(), Intermediates: inter}); err == nil {
		trusted = true
	}
	hello, err := m.hello(ctx, newClient(u, fp, trusted), u)
	if err != nil {
		return nil, err
	}
	return &ProbeResult{URL: u.String(), Fingerprint: fp, FingerprintDisplay: remote.FingerprintDisplay(fp), Trusted: trusted, Hello: *hello}, nil
}

func (m *Manager) hello(ctx context.Context, hc *http.Client, u *url.URL) (*remote.Hello, error) {
	req, _ := http.NewRequestWithContext(ctx, "GET", u.String()+"/api/v1/auth/hello", nil)
	resp, err := hc.Do(req)
	if err != nil {
		var fe *fingerprintError
		if errors.As(err, &fe) {
			return nil, &Error{C: "fingerprint_mismatch", Msg: i18n.T("gw.fingerprintChanged"), St: 409, Info: map[string]string{"fingerprint": fe.got}}
		}
		return nil, errUnreachable(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusTooManyRequests {
		return nil, &Error{C: "rate_limited", Msg: i18n.T("gw.rateLimited"), St: 429}
	}
	if resp.StatusCode != 200 {
		return nil, &Error{C: "unreachable", Msg: i18n.T("gw.notPaperplaneHTTP", resp.StatusCode), St: 502}
	}
	var h remote.Hello
	if err := json.NewDecoder(io.LimitReader(resp.Body, 64<<10)).Decode(&h); err != nil || h.APIVersion == 0 {
		return nil, &Error{C: "unreachable", Msg: i18n.T("gw.notPaperplane"), St: 502}
	}
	if h.APIVersion != remote.APIVersion {
		return nil, &Error{C: "incompatible", Msg: i18n.T("gw.incompatible", h.Version, m.Version), St: 409,
			Info: map[string]string{"serverVersion": h.Version, "clientVersion": m.Version}}
	}
	return &h, nil
}

// Login 用账号密码换一条设备专用的访问密钥（密码不保存）。
func (m *Manager) Login(ctx context.Context, rawURL, fingerprint, user, password, device string, trusted bool) (string, error) {
	u, err := parseURL(rawURL)
	if err != nil {
		return "", err
	}
	hc := newClient(u, fingerprint, trusted)
	if _, err := m.hello(ctx, hc, u); err != nil {
		return "", err
	}
	body, _ := json.Marshal(remote.LoginRequest{User: user, Password: password, Device: device})
	req, _ := http.NewRequestWithContext(ctx, "POST", u.String()+"/api/v1/auth/login", strings.NewReader(string(body)))
	req.Header.Set("Content-Type", "application/json")
	resp, err := hc.Do(req)
	if err != nil {
		return "", errUnreachable(err)
	}
	defer resp.Body.Close()
	switch resp.StatusCode {
	case 200:
	case http.StatusUnauthorized:
		return "", &Error{C: "unauthorized", Msg: i18n.T("gw.badLogin"), St: 401}
	case http.StatusTooManyRequests:
		return "", &Error{C: "rate_limited", Msg: i18n.T("gw.rateLimited"), St: 429}
	case http.StatusForbidden:
		return "", &Error{C: "password_disabled", Msg: i18n.T("gw.passwordDisabled"), St: 403}
	default:
		return "", &Error{C: "unreachable", Msg: i18n.T("gw.loginFailed", resp.StatusCode), St: 502}
	}
	var lr remote.LoginResponse
	if err := json.NewDecoder(io.LimitReader(resp.Body, 64<<10)).Decode(&lr); err != nil || lr.Token == "" {
		return "", &Error{C: "unreachable", Msg: i18n.T("gw.loginBadResponse"), St: 502}
	}
	return lr.Token, nil
}

// ---------------------------------------------------------------- 打开网关

// OpenResult 是 gateway.open 的结果：port / token 用法与本机 runtime.json 相同。
type OpenResult struct {
	ID    string          `json:"id"`
	Port  int             `json:"port"`
	Token string          `json:"token"`
	Info  json.RawMessage `json:"info"`
}

// Open 校验密钥后开一个回环端口；同一服务器 + 同一密钥重复调用复用同一个网关。
func (m *Manager) Open(ctx context.Context, rawURL, fingerprint, token string, trusted bool) (*OpenResult, error) {
	u, err := parseURL(rawURL)
	if err != nil {
		return nil, err
	}
	if !trusted && fingerprint == "" {
		return nil, &Error{C: "bad_request", Msg: i18n.T("gw.noFingerprint"), St: 400}
	}
	m.mu.Lock()
	for _, c := range m.conns {
		if c.base.String() == u.String() && subtle.ConstantTimeCompare([]byte(c.token), []byte(token)) == 1 && c.fingerprint == fingerprint {
			m.mu.Unlock()
			info, err := c.systemInfo(ctx)
			if err != nil {
				return nil, err
			}
			return &OpenResult{ID: c.ID, Port: c.port, Token: c.localToken, Info: info}, nil
		}
	}
	m.mu.Unlock()

	c := &Conn{ID: randHex(6), base: u, fingerprint: fingerprint, trusted: trusted, token: token, localToken: randHex(32),
		client: newClient(u, fingerprint, trusted), mgr: m, forwards: map[int]net.Listener{}}
	if _, err := m.hello(ctx, c.client, u); err != nil {
		return nil, err
	}
	info, err := c.systemInfo(ctx)
	if err != nil {
		return nil, err
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	c.ln, c.port = ln, ln.Addr().(*net.TCPAddr).Port
	c.srv = &http.Server{Handler: c.handler(), ReadHeaderTimeout: 10 * time.Second}
	go func() { _ = c.srv.Serve(ln) }()
	m.mu.Lock()
	m.conns[c.ID] = c
	m.mu.Unlock()
	m.Logger.Printf("gateway %s: %s → 127.0.0.1:%d", c.ID, u, c.port)
	return &OpenResult{ID: c.ID, Port: c.port, Token: c.localToken, Info: info}, nil
}

// systemInfo 用访问密钥调一次 system.info（顺便验证密钥）。
func (c *Conn) systemInfo(ctx context.Context) (json.RawMessage, error) {
	req, _ := http.NewRequestWithContext(ctx, "POST", c.base.String()+"/api/v1/call/system.info", strings.NewReader("{}"))
	req.Header.Set("Authorization", "Bearer "+c.token)
	req.Header.Set("Content-Type", "application/json")
	resp, err := c.client.Do(req)
	if err != nil {
		var fe *fingerprintError
		if errors.As(err, &fe) {
			return nil, &Error{C: "fingerprint_mismatch", Msg: i18n.T("gw.fingerprintChanged"), St: 409, Info: map[string]string{"fingerprint": fe.got}}
		}
		return nil, errUnreachable(err)
	}
	defer resp.Body.Close()
	switch resp.StatusCode {
	case 200:
	case http.StatusUnauthorized:
		return nil, &Error{C: "unauthorized", Msg: i18n.T("gw.keyRevoked"), St: 401}
	case http.StatusTooManyRequests:
		return nil, &Error{C: "rate_limited", Msg: i18n.T("gw.rateLimited"), St: 429}
	case http.StatusForbidden:
		return nil, &Error{C: "unreachable", Msg: i18n.T("gw.ipDenied"), St: 403}
	default:
		return nil, &Error{C: "unreachable", Msg: "HTTP " + strconv.Itoa(resp.StatusCode), St: 502}
	}
	var env struct {
		Result json.RawMessage `json:"result"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&env); err != nil {
		return nil, errUnreachable(err)
	}
	return env.Result, nil
}

// Close 关掉一个网关（连同端口转发）。
func (m *Manager) Close(id string) error {
	m.mu.Lock()
	c := m.conns[id]
	delete(m.conns, id)
	m.mu.Unlock()
	if c == nil {
		return nil
	}
	c.shutdown()
	return nil
}

// CloseAll 在内核退出时调用。
func (m *Manager) CloseAll() {
	m.mu.Lock()
	all := m.conns
	m.conns = map[string]*Conn{}
	m.mu.Unlock()
	for _, c := range all {
		c.shutdown()
	}
}

func (c *Conn) shutdown() {
	c.fmu.Lock()
	for p, ln := range c.forwards {
		_ = ln.Close()
		delete(c.forwards, p)
	}
	c.fmu.Unlock()
	if c.srv != nil {
		_ = c.srv.Close()
	}
	c.client.CloseIdleConnections()
}

func (m *Manager) get(id string) (*Conn, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	c := m.conns[id]
	if c == nil {
		return nil, &Error{C: "not_found", Msg: i18n.T("gw.closed"), St: 404}
	}
	return c, nil
}

// ---------------------------------------------------------------- 回环端口上的处理

// handler：校验本机令牌（规则和本机接口一样）→ /editor/ 用本机文件 → 其余转发给服务器。
func (c *Conn) handler() http.Handler {
	proxy := &httputil.ReverseProxy{
		Rewrite: func(pr *httputil.ProxyRequest) {
			pr.SetURL(c.base)
			pr.Out.Host = c.base.Host
			q := pr.Out.URL.Query()
			q.Del("token")
			pr.Out.URL.RawQuery = q.Encode()
			pr.Out.Header.Set("Authorization", "Bearer "+c.token)
			pr.Out.Header.Del("Origin")
			pr.Out.Header.Del("Referer")
			pr.Out.Header.Del("Cookie")
		},
		Transport:     c.client.Transport,
		FlushInterval: -1, // 事件流、日志流、终端流立刻送达
		ModifyResponse: func(resp *http.Response) error {
			// 服务器拒绝了访问密钥：改成 403 remote_unauthorized，免得界面当成本机令牌失效去重启本机内核
			if resp.StatusCode == http.StatusUnauthorized {
				resp.Body.Close()
				b, _ := json.Marshal(map[string]any{"error": map[string]string{"code": "remote_unauthorized", "message": i18n.T("gw.keyRevoked")}})
				resp.StatusCode, resp.Status = http.StatusForbidden, "403 Forbidden"
				resp.Body = io.NopCloser(strings.NewReader(string(b)))
				resp.ContentLength = int64(len(b))
				resp.Header.Set("Content-Length", strconv.Itoa(len(b)))
				resp.Header.Set("Content-Type", "application/json")
			}
			return nil
		},
		ErrorHandler: func(w http.ResponseWriter, r *http.Request, err error) {
			if errors.Is(err, context.Canceled) {
				return
			}
			code, msg := "unreachable", friendlyNetErr(err)
			var fe *fingerprintError
			if errors.As(err, &fe) {
				code = "fingerprint_mismatch"
			}
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusBadGateway)
			_ = json.NewEncoder(w).Encode(map[string]any{"error": map[string]string{"code": code, "message": msg}})
		},
		ErrorLog: log.New(io.Discard, "", 0),
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		port := strconv.Itoa(c.port)
		if r.Host != "127.0.0.1:"+port && r.Host != "localhost:"+port && r.Host != "[::1]:"+port {
			http.Error(w, "forbidden host", http.StatusForbidden)
			return
		}
		if r.URL.Path == "/healthz" {
			_, _ = io.WriteString(w, "ok")
			return
		}
		if strings.HasPrefix(r.URL.Path, "/editor/") {
			c.serveEditor(w, r)
			return
		}
		if origin := r.Header.Get("Origin"); origin != "" && origin != "http://127.0.0.1:"+port && origin != "http://localhost:"+port {
			http.Error(w, "forbidden origin", http.StatusForbidden)
			return
		}
		static := strings.HasPrefix(r.URL.Path, "/plugins/") || strings.HasPrefix(r.URL.Path, "/api/v1/plugins/")
		tok := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
		if tok == "" && (static || r.Method == http.MethodGet) {
			tok = r.URL.Query().Get("token")
		}
		if subtle.ConstantTimeCompare([]byte(tok), []byte(c.localToken)) != 1 {
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusUnauthorized)
			_ = json.NewEncoder(w).Encode(map[string]any{"error": map[string]string{"code": "unauthorized", "message": "invalid token"}})
			return
		}
		r.Header.Del("Authorization")
		proxy.ServeHTTP(w, r)
	})
}

// serveEditor：编辑器网页用本机的（和界面版本一致，服务器上不需要）。
func (c *Conn) serveEditor(w http.ResponseWriter, r *http.Request) {
	dir := c.mgr.EditorDir
	if dir == "" {
		http.NotFound(w, r)
		return
	}
	rel := filepath.Clean("/" + strings.TrimPrefix(r.URL.Path, "/editor/"))[1:]
	if rel == "" {
		rel = "index.html"
	}
	full := filepath.Join(dir, rel)
	if !strings.HasPrefix(full, filepath.Clean(dir)+string(filepath.Separator)) {
		http.NotFound(w, r)
		return
	}
	if strings.HasSuffix(rel, ".html") {
		w.Header().Set("Cache-Control", "no-cache")
	} else {
		w.Header().Set("Cache-Control", "max-age=86400")
	}
	http.ServeFile(w, r, full)
}

// ---------------------------------------------------------------- 端口转发

// ForwardResult 是 gateway.forward 的结果。
type ForwardResult struct {
	LocalPort int    `json:"localPort"`
	URL       string `json:"url"`
}

// Forward 把服务器 localhost:port 转到本机（优先用同一个端口号）。
func (m *Manager) Forward(id string, port int) (*ForwardResult, error) {
	c, err := m.get(id)
	if err != nil {
		return nil, err
	}
	if port < 1 || port > 65535 {
		return nil, &Error{C: "bad_request", Msg: i18n.T("gw.badPort"), St: 400}
	}
	c.fmu.Lock()
	defer c.fmu.Unlock()
	if ln, ok := c.forwards[port]; ok {
		lp := ln.Addr().(*net.TCPAddr).Port
		return &ForwardResult{LocalPort: lp, URL: "http://localhost:" + strconv.Itoa(lp)}, nil
	}
	ln, err := net.Listen("tcp", "127.0.0.1:"+strconv.Itoa(port))
	if err != nil {
		if ln, err = net.Listen("tcp", "127.0.0.1:0"); err != nil {
			return nil, err
		}
	}
	c.forwards[port] = ln
	go c.acceptForward(ln, port)
	lp := ln.Addr().(*net.TCPAddr).Port
	c.mgr.Logger.Printf("gateway %s: forward localhost:%d → %s localhost:%d", c.ID, lp, c.base.Host, port)
	return &ForwardResult{LocalPort: lp, URL: "http://localhost:" + strconv.Itoa(lp)}, nil
}

// Unforward 停止一个端口转发。
func (m *Manager) Unforward(id string, port int) error {
	c, err := m.get(id)
	if err != nil {
		return err
	}
	c.fmu.Lock()
	defer c.fmu.Unlock()
	if ln, ok := c.forwards[port]; ok {
		_ = ln.Close()
		delete(c.forwards, port)
	}
	return nil
}

func (c *Conn) acceptForward(ln net.Listener, port int) {
	for {
		lc, err := ln.Accept()
		if err != nil {
			return
		}
		go func() {
			rc, err := c.dialTunnel(port)
			if err != nil {
				c.mgr.Logger.Printf("gateway %s: tunnel %d: %v", c.ID, port, err)
				_ = lc.Close()
				return
			}
			remote.Pipe(lc, rc)
		}()
	}
}

// dialTunnel：新开一条 HTTP/1.1 的 TLS 连接，升级成到服务器 localhost:port 的原始 TCP。
func (c *Conn) dialTunnel(port int) (net.Conn, error) {
	d := &tls.Dialer{NetDialer: &net.Dialer{Timeout: 10 * time.Second, KeepAlive: 30 * time.Second},
		Config: tlsConfig(c.base, c.fingerprint, c.trusted, []string{"http/1.1"})}
	conn, err := d.Dial("tcp", c.base.Host)
	if err != nil {
		return nil, err
	}
	req := "GET /api/v1/tunnel?port=" + strconv.Itoa(port) + " HTTP/1.1\r\nHost: " + c.base.Host +
		"\r\nAuthorization: Bearer " + c.token + "\r\nConnection: Upgrade\r\nUpgrade: paperplane-tunnel\r\n\r\n"
	_ = conn.SetDeadline(time.Now().Add(15 * time.Second))
	if _, err := io.WriteString(conn, req); err != nil {
		_ = conn.Close()
		return nil, err
	}
	br := bufio.NewReader(conn)
	resp, err := http.ReadResponse(br, nil)
	if err != nil {
		_ = conn.Close()
		return nil, err
	}
	if resp.StatusCode != http.StatusSwitchingProtocols {
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 4<<10))
		_ = conn.Close()
		return nil, fmt.Errorf("HTTP %d %s", resp.StatusCode, strings.TrimSpace(string(b)))
	}
	_ = conn.SetDeadline(time.Time{})
	return &bufConn{Conn: conn, r: br}, nil
}

// bufConn：升级响应后可能已经读进缓冲的字节要先交出去。
type bufConn struct {
	net.Conn
	r *bufio.Reader
}

func (b *bufConn) Read(p []byte) (int, error) { return b.r.Read(p) }

func (b *bufConn) CloseWrite() error {
	if cw, ok := b.Conn.(interface{ CloseWrite() error }); ok {
		return cw.CloseWrite()
	}
	return nil
}

package remote

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"runtime"
	"strconv"
	"strings"
	"time"
)

// Principal 是远程请求的调用者（经访问密钥认证）。本机请求没有 Principal。
type Principal struct {
	KeyID string
	Name  string // 密钥名
	User  string // 账号密码登录发的设备密钥：账号
	IP    string
}

// Who 是审计日志里的身份。
func (p *Principal) Who() string {
	if p.User != "" {
		return p.User + "@" + p.Name
	}
	return p.Name
}

type ctxKey struct{}

// PrincipalFrom 取请求的远程调用者；本机请求返回 nil。
func PrincipalFrom(ctx context.Context) *Principal {
	p, _ := ctx.Value(ctxKey{}).(*Principal)
	return p
}

// WithPrincipal 把调用者放进上下文（测试与内部转调用）。
func WithPrincipal(ctx context.Context, p *Principal) context.Context {
	return context.WithValue(ctx, ctxKey{}, p)
}

// AuditFrom 生成审计条目的身份部分（本机操作 IP 为空、Who 为 local）。
func AuditFrom(ctx context.Context) AuditEntry {
	if p := PrincipalFrom(ctx); p != nil {
		return AuditEntry{IP: p.IP, Who: p.Who()}
	}
	return AuditEntry{Who: "local"}
}

// Hello 是 GET /api/v1/auth/hello 的结果（不需要认证，不含敏感信息）。
type Hello struct {
	Name          string `json:"name"`
	Hostname      string `json:"hostname"`
	Version       string `json:"version"`
	APIVersion    int    `json:"apiVersion"`
	OS            string `json:"os"`
	Arch          string `json:"arch"`
	PasswordLogin bool   `json:"passwordLogin"`
}

func writeJSONResp(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, status int, code, msg string) {
	writeJSONResp(w, status, map[string]any{"error": map[string]string{"code": code, "message": msg}})
}

func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// guard：白名单 → 封禁 → 拒绝浏览器跨站 → hello / login 免认证 → 其余一律校验访问密钥。
func (m *Manager) guard(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		ip := clientIP(r)
		m.mu.Lock()
		allow := m.allow
		m.mu.Unlock()
		if len(allow) > 0 {
			ok := false
			if pip := net.ParseIP(ip); pip != nil {
				for _, n := range allow {
					if n.Contains(pip) {
						ok = true
						break
					}
				}
			}
			if !ok {
				writeErr(w, http.StatusForbidden, "forbidden", "ip not allowed")
				return
			}
		}
		if banned, d := m.lim.banned(ip); banned {
			w.Header().Set("Retry-After", strconv.Itoa(int(d.Seconds())+1))
			writeErr(w, http.StatusTooManyRequests, "rate_limited", "too many failed attempts")
			return
		}
		// 接口只给客户端程序用：浏览器发来的跨站请求（带 Origin）一律拒绝
		if r.Header.Get("Origin") != "" {
			writeErr(w, http.StatusForbidden, "forbidden", "browser requests are not allowed")
			return
		}
		switch r.URL.Path {
		case "/api/v1/auth/hello":
			m.handleHello(w, r)
			return
		case "/api/v1/auth/login":
			m.handleLogin(w, r, ip)
			return
		}
		tok := strings.TrimSpace(strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer "))
		m.mu.Lock()
		k := matchKey(m.creds.Keys, tok)
		var p *Principal
		if k != nil {
			now := time.Now()
			if k.LastUsedAt == nil || now.Sub(*k.LastUsedAt) > time.Minute || k.LastIP != ip {
				k.LastUsedAt, k.LastIP = &now, ip
				m.dirtyUsed = true
			}
			p = &Principal{KeyID: k.ID, Name: k.Name, User: k.User, IP: ip}
		}
		m.mu.Unlock()
		if p == nil {
			if bannedNow, d := m.lim.fail(ip); bannedNow {
				m.audit.add(AuditEntry{Event: "ban", IP: ip, Detail: d.String()})
			}
			m.audit.add(AuditEntry{Event: "auth.fail", IP: ip, Detail: r.URL.Path})
			writeErr(w, http.StatusUnauthorized, "unauthorized", "invalid or revoked access key")
			return
		}
		m.lim.success(ip)
		r.Header.Del("Authorization") // 内部接口用不到，不往下传
		// 吊销这条密钥时取消请求：事件流、终端流等长连接随之结束
		ctx, cancel := context.WithCancel(WithPrincipal(r.Context(), p))
		defer cancel()
		untrack := m.track(p.KeyID, cancel)
		defer untrack()
		switch {
		case r.URL.Path == "/api/v1/tunnel":
			m.handleTunnel(w, r.WithContext(ctx), p)
		case strings.HasPrefix(r.URL.Path, "/editor/"):
			// 编辑器网页由客户端自己提供，服务器不对外
			http.NotFound(w, r)
		default:
			next.ServeHTTP(w, r.WithContext(ctx))
		}
	})
}

func (m *Manager) handleHello(w http.ResponseWriter, r *http.Request) {
	m.mu.Lock()
	pw := m.cfg.PasswordLogin && len(m.creds.Users) > 0
	m.mu.Unlock()
	writeJSONResp(w, 200, Hello{Name: m.hostname, Hostname: m.hostname, Version: m.opt.Version, APIVersion: APIVersion,
		OS: runtime.GOOS, Arch: runtime.GOARCH, PasswordLogin: pw})
}

// LoginRequest / LoginResponse：POST /api/v1/auth/login。
type LoginRequest struct {
	User     string `json:"user"`
	Password string `json:"password"`
	Device   string `json:"device"`
}

type LoginResponse struct {
	Token string `json:"token"`
	KeyID string `json:"keyId"`
}

func (m *Manager) handleLogin(w http.ResponseWriter, r *http.Request, ip string) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "bad_request", "POST only")
		return
	}
	if !m.lim.allowLogin() {
		writeErr(w, http.StatusTooManyRequests, "rate_limited", "too many login attempts")
		return
	}
	var req LoginRequest
	if err := json.NewDecoder(io.LimitReader(r.Body, 4<<10)).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, "bad_request", "invalid request")
		return
	}
	m.mu.Lock()
	enabled := m.cfg.PasswordLogin
	hash := dummyHash
	found := false
	for _, u := range m.creds.Users {
		if u.Name == req.User {
			hash, found = u.Hash, true
			break
		}
	}
	m.mu.Unlock()
	if !enabled {
		writeErr(w, http.StatusForbidden, "password_disabled", "password login is disabled on this server")
		return
	}
	// 同时最多 2 个哈希在算（每个 19 MiB），排队等
	select {
	case m.lim.hashes <- struct{}{}:
	case <-r.Context().Done():
		return
	}
	ok := VerifyPassword(hash, req.Password) && found
	<-m.lim.hashes
	if !ok {
		if bannedNow, d := m.lim.fail(ip); bannedNow {
			m.audit.add(AuditEntry{Event: "ban", IP: ip, Detail: d.String()})
		}
		m.audit.add(AuditEntry{Event: "login.fail", IP: ip, Who: cleanName(req.User, 32)})
		writeErr(w, http.StatusUnauthorized, "unauthorized", "wrong account or password")
		return
	}
	m.lim.success(ip)
	device := cleanName(req.Device, 64)
	if device == "" {
		device = "device"
	}
	m.mu.Lock()
	k, tok := m.newKeyLocked(device, "device", req.User)
	now := time.Now()
	k.LastUsedAt, k.LastIP = &now, ip
	err := writeJSON(m.credPath(), m.creds, 0o600)
	m.mu.Unlock()
	if err != nil {
		writeErr(w, 500, "internal", err.Error())
		return
	}
	m.audit.add(AuditEntry{Event: "login.ok", IP: ip, Who: req.User + "@" + device, Detail: k.ID})
	writeJSONResp(w, 200, LoginResponse{Token: tok, KeyID: k.ID})
}

// handleTunnel：GET /api/v1/tunnel?port=N，Upgrade: paperplane-tunnel → 到本机 localhost:N 的原始 TCP。
// 只能连这台服务器自己的端口（端口转发），不能当跳板访问别的机器。
func (m *Manager) handleTunnel(w http.ResponseWriter, r *http.Request, p *Principal) {
	if !strings.EqualFold(r.Header.Get("Upgrade"), "paperplane-tunnel") {
		writeErr(w, http.StatusBadRequest, "bad_request", "upgrade required")
		return
	}
	port, err := strconv.Atoi(r.URL.Query().Get("port"))
	if err != nil || port < 1 || port > 65535 {
		writeErr(w, http.StatusBadRequest, "bad_request", "invalid port")
		return
	}
	hj, ok := w.(http.Hijacker)
	if !ok {
		writeErr(w, http.StatusBadRequest, "bad_request", "tunnel needs HTTP/1.1")
		return
	}
	var d net.Dialer
	ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
	up, err := d.DialContext(ctx, "tcp", net.JoinHostPort("localhost", strconv.Itoa(port)))
	cancel()
	if err != nil {
		writeErr(w, http.StatusBadGateway, "unreachable", fmt.Sprintf("localhost:%d: %v", port, err))
		return
	}
	c, brw, err := hj.Hijack()
	if err != nil {
		_ = up.Close()
		return
	}
	_ = c.SetDeadline(time.Time{})
	_, _ = c.Write([]byte("HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: paperplane-tunnel\r\n\r\n"))
	if n := brw.Reader.Buffered(); n > 0 {
		b, _ := brw.Reader.Peek(n)
		_, _ = up.Write(b)
	}
	m.audit.add(AuditEntry{Event: "tunnel", IP: p.IP, Who: p.Who(), Detail: strconv.Itoa(port)})
	// 隧道已脱离 HTTP：吊销密钥时直接关掉两端
	untrack := m.track(p.KeyID, func() { _ = c.Close(); _ = up.Close() })
	defer untrack()
	Pipe(c, up)
}

// Pipe 双向转发，任一方向结束后关闭两端。
func Pipe(a, b net.Conn) {
	done := make(chan struct{}, 2)
	cp := func(dst, src net.Conn) {
		_, _ = io.Copy(dst, src)
		if cw, ok := dst.(interface{ CloseWrite() error }); ok {
			_ = cw.CloseWrite()
		}
		done <- struct{}{}
	}
	go cp(a, b)
	go cp(b, a)
	<-done
	// 另一方向给一点时间把剩余数据送完
	t := time.NewTimer(5 * time.Second)
	select {
	case <-done:
	case <-t.C:
	}
	t.Stop()
	_ = a.Close()
	_ = b.Close()
}

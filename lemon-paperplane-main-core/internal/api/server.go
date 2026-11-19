// Package api 是本机 HTTP 服务：操作调用、SSE 事件流、控制台日志流、插件静态文件、MCP。
package api

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"runtime/pprof"
	"strconv"
	"strings"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/engine"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/mcp"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/ops"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// Runtime 是 runtime.json 的内容。
type Runtime struct {
	Port       int    `json:"port"`
	Token      string `json:"token"`
	PID        int    `json:"pid"`
	Version    string `json:"version"`
	Executable string `json:"executable"`
}

// Server 是 HTTP 服务。
type Server struct {
	E         *engine.Engine
	Reg       *ops.Registry
	MCP       *mcp.Handler
	Token     string
	Port      int
	EditorDir string // 编辑器网页目录
	srv       *http.Server
	ln        net.Listener
}

// NewToken 生成随机 token。
func NewToken() string {
	b := make([]byte, 32)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// Listen 监听 127.0.0.1:port（被占用则用随机端口）。
func (s *Server) Listen(port int) error {
	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		ln, err = net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			return err
		}
	}
	s.ln = ln
	s.Port = ln.Addr().(*net.TCPAddr).Port
	return nil
}

// Base 返回 API 根地址。
func (s *Server) Base() string { return fmt.Sprintf("http://127.0.0.1:%d", s.Port) }

// Serve 阻塞提供服务。
func (s *Server) Serve() error {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/v1/call/", s.handleCall)
	mux.HandleFunc("/api/v1/ops", s.handleOps)
	mux.HandleFunc("/api/v1/events", s.handleEvents)
	mux.HandleFunc("/api/v1/consoles/", s.handleConsole)
	mux.HandleFunc("/api/v1/terminals/", s.handleTerminal)
	mux.HandleFunc("/api/v1/plugins/", s.handlePluginFile)
	mux.HandleFunc("/plugins/", s.handlePluginFile)
	mux.HandleFunc("/mcp", s.handleMCP)
	mux.HandleFunc("/editor/", s.handleEditor)
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) { _, _ = io.WriteString(w, "ok") })
	// 排查卡死：全部协程的调用栈（Windows 没有 SIGQUIT）。同样要令牌；只在设了 PAPERPLANE_DEBUG 时开放
	if os.Getenv("PAPERPLANE_DEBUG") != "" {
		mux.HandleFunc("/debug/goroutines", func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "text/plain; charset=utf-8")
			_ = pprof.Lookup("goroutine").WriteTo(w, 2)
		})
	}
	s.srv = &http.Server{Handler: s.guard(mux), ReadHeaderTimeout: 10 * time.Second}
	err := s.srv.Serve(s.ln)
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}

// Shutdown 关闭服务。
func (s *Server) Shutdown(ctx context.Context) {
	if s.srv != nil {
		_ = s.srv.Shutdown(ctx)
	}
}

// guard 做 Host/Origin/Token 校验。
func (s *Server) guard(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		host := r.Host
		port := strconv.Itoa(s.Port)
		if host != "127.0.0.1:"+port && host != "localhost:"+port && host != "[::1]:"+port {
			http.Error(w, "forbidden host", http.StatusForbidden)
			return
		}
		if r.URL.Path == "/healthz" || strings.HasPrefix(r.URL.Path, "/editor/") {
			// 编辑器网页是静态资源，不含敏感数据；网页调用 API 时仍须带 token
			next.ServeHTTP(w, r)
			return
		}
		static := strings.HasPrefix(r.URL.Path, "/plugins/") || strings.HasPrefix(r.URL.Path, "/api/v1/plugins/")
		if origin := r.Header.Get("Origin"); origin != "" && origin != s.Base() && origin != "http://localhost:"+port {
			http.Error(w, "forbidden origin", http.StatusForbidden)
			return
		}
		tok := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
		if tok == "" && (static || r.Method == http.MethodGet) {
			tok = r.URL.Query().Get("token")
		}
		if subtle.ConstantTimeCompare([]byte(tok), []byte(s.Token)) != 1 {
			writeErr(w, http.StatusUnauthorized, "unauthorized", "invalid token")
			return
		}
		next.ServeHTTP(w, r)
	})
}

type apiError struct {
	Code    string `json:"code"`
	Message string `json:"message"`
	Data    any    `json:"data,omitempty"` // 插件给出的结构化信息，如 {"reason":"identityMissing"}
}

func writeErr(w http.ResponseWriter, status int, code, msg string) {
	writeErrData(w, status, code, msg, nil)
}

func writeErrData(w http.ResponseWriter, status int, code, msg string, data any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(map[string]any{"error": apiError{Code: code, Message: msg, Data: data}})
}

// ErrorData 取插件错误里的结构化 data。
func ErrorData(err error) any {
	var re *sdk.Error
	if errors.As(err, &re) {
		return re.Data
	}
	return nil
}

// ErrorInfo 把错误映射为 HTTP 状态与错误码。
func ErrorInfo(err error) (int, string, string) {
	var ue *engine.UserError
	var br *ops.BadRequest
	var re *sdk.Error
	switch {
	case errors.Is(err, engine.ErrNotFound), errors.Is(err, store.ErrNotFound):
		return 404, "not_found", i18n.T("err.notFound")
	case errors.Is(err, ops.ErrUnknownOp):
		return 404, "not_found", err.Error()
	case errors.As(err, &ue):
		return 400, "user_error", ue.Msg
	case errors.As(err, &br):
		return 400, "bad_request", br.Msg
	case errors.As(err, &re):
		switch re.Code {
		case sdk.ErrCodeNotSupported:
			return 400, "not_supported", re.Message
		case sdk.ErrCodeUserError:
			return 400, "user_error", re.Message
		case sdk.ErrCodeCancelled:
			return 409, "cancelled", re.Message
		}
		return 502, "plugin_error", re.Message
	case errors.Is(err, context.Canceled):
		return 409, "cancelled", "cancelled"
	}
	return 500, "internal", err.Error()
}

func (s *Server) handleCall(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		writeErr(w, http.StatusMethodNotAllowed, "bad_request", "POST only")
		return
	}
	name := strings.TrimPrefix(r.URL.Path, "/api/v1/call/")
	body, err := io.ReadAll(io.LimitReader(r.Body, 32<<20))
	if err != nil {
		writeErr(w, 400, "bad_request", err.Error())
		return
	}
	res, err := s.Reg.Call(r.Context(), name, body)
	if err != nil {
		st, code, msg := ErrorInfo(err)
		writeErrData(w, st, code, msg, ErrorData(err))
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{"result": res})
}

func (s *Server) handleOps(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(s.Reg.All())
}

func sseHeaders(w http.ResponseWriter) (http.Flusher, bool) {
	f, ok := w.(http.Flusher)
	if !ok {
		return nil, false
	}
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	w.WriteHeader(200)
	f.Flush()
	return f, true
}

func (s *Server) handleEvents(w http.ResponseWriter, r *http.Request) {
	ch, cancel := s.E.Bus.Subscribe()
	defer cancel()
	f, ok := sseHeaders(w)
	if !ok {
		return
	}
	hello, _ := json.Marshal(model.Event{Type: "hello", Data: s.E.SystemInfo(os.Getpid(), i18n.Locale(), s.Base()+"/mcp")})
	fmt.Fprintf(w, "data: %s\n\n", hello)
	f.Flush()
	ping := time.NewTicker(15 * time.Second)
	defer ping.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case <-ping.C:
			fmt.Fprint(w, ": ping\n\n")
			f.Flush()
		case ev, ok := <-ch:
			if !ok {
				return
			}
			b, err := json.Marshal(ev)
			if err != nil {
				continue
			}
			fmt.Fprintf(w, "data: %s\n\n", b)
			f.Flush()
		}
	}
}

func (s *Server) handleConsole(w http.ResponseWriter, r *http.Request) {
	rest := strings.TrimPrefix(r.URL.Path, "/api/v1/consoles/")
	id, tail, _ := strings.Cut(rest, "/")
	if tail != "stream" {
		writeErr(w, 404, "not_found", "not found")
		return
	}
	c := s.E.Console(id)
	if c == nil {
		writeErr(w, 404, "not_found", "console not found")
		return
	}
	since, _ := strconv.ParseInt(r.URL.Query().Get("since"), 10, 64)
	backlog, ch, cancel, done := c.Subscribe(since)
	defer cancel()
	f, ok := sseHeaders(w)
	if !ok {
		return
	}
	send := func(lines []model.LogLine) {
		for len(lines) > 0 {
			n := len(lines)
			if n > 500 {
				n = 500
			}
			b, _ := json.Marshal(map[string]any{"lines": lines[:n]})
			fmt.Fprintf(w, "data: %s\n\n", b)
			lines = lines[n:]
		}
		f.Flush()
	}
	send(backlog)
	var pending []model.LogLine
	flush := time.NewTicker(100 * time.Millisecond)
	defer flush.Stop()
	ping := time.NewTicker(15 * time.Second)
	defer ping.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case l := <-ch:
			pending = append(pending, l)
		case <-flush.C:
			if len(pending) > 0 {
				send(pending)
				pending = nil
			}
		case <-ping.C:
			fmt.Fprint(w, ": ping\n\n")
			f.Flush()
		case <-done:
			// 排空剩余
		drain:
			for {
				select {
				case l := <-ch:
					pending = append(pending, l)
				default:
					break drain
				}
			}
			if len(pending) > 0 {
				send(pending)
			}
			fmt.Fprint(w, "event: end\ndata: {}\n\n")
			f.Flush()
			return
		}
	}
}

// handleTerminal 推送终端输出：GET /api/v1/terminals/<id>/stream?since=<字节偏移>。
// 先发缓冲里 since 之后还留着的输出，再实时推送。每条 data: {"o":起始偏移,"d":base64 原始字节}；
// event: state 是终端状态（退出、重新启动）；event: closed 表示终端已关闭。连接断开后带上已收到的末尾偏移重连即可。
func (s *Server) handleTerminal(w http.ResponseWriter, r *http.Request) {
	rest := strings.TrimPrefix(r.URL.Path, "/api/v1/terminals/")
	id, tail, _ := strings.Cut(rest, "/")
	if tail != "stream" {
		writeErr(w, 404, "not_found", "not found")
		return
	}
	since, _ := strconv.ParseInt(r.URL.Query().Get("since"), 10, 64)
	backlog, start, info, ch, cancel, err := s.E.TerminalSubscribe(id, since)
	if err != nil {
		writeErr(w, 404, "not_found", "terminal not found")
		return
	}
	defer cancel()
	f, ok := sseHeaders(w)
	if !ok {
		return
	}
	sendState := func(t model.Terminal) {
		b, _ := json.Marshal(t)
		fmt.Fprintf(w, "event: state\ndata: %s\n\n", b)
	}
	sendData := func(off int64, d []byte) {
		for len(d) > 0 {
			n := len(d)
			if n > 64<<10 {
				n = 64 << 10
			}
			fmt.Fprintf(w, "data: {\"o\":%d,\"d\":\"%s\"}\n\n", off, base64.StdEncoding.EncodeToString(d[:n]))
			off += int64(n)
			d = d[n:]
		}
	}
	sendState(info)
	sendData(start, backlog)
	f.Flush()
	ping := time.NewTicker(15 * time.Second)
	defer ping.Stop()
	for {
		select {
		case <-r.Context().Done():
			return
		case <-ping.C:
			fmt.Fprint(w, ": ping\n\n")
			f.Flush()
		case c, ok := <-ch:
			if !ok {
				if !s.E.TerminalExists(id) {
					fmt.Fprint(w, "event: closed\ndata: {}\n\n")
					f.Flush()
				}
				return
			}
			// 把已经到的输出合并成一次发送，大量输出时少刷几次
			for {
				if c.Info != nil {
					sendState(*c.Info)
				} else {
					sendData(c.Off, c.Data)
				}
				select {
				case c, ok = <-ch:
					if ok {
						continue
					}
				default:
				}
				break
			}
			f.Flush()
			if !ok {
				if !s.E.TerminalExists(id) {
					fmt.Fprint(w, "event: closed\ndata: {}\n\n")
					f.Flush()
				}
				return
			}
		}
	}
}

// handlePluginFile 提供插件目录下 icons/ web/ resources/ editor/ keymaps/ themes/ 的文件。
func (s *Server) handlePluginFile(w http.ResponseWriter, r *http.Request) {
	p := strings.TrimPrefix(strings.TrimPrefix(r.URL.Path, "/api/v1/plugins/"), "/plugins/")
	id, rest, _ := strings.Cut(p, "/")
	rest = strings.TrimPrefix(rest, "files/")
	plug := s.E.Plugins.Get(id)
	if plug == nil {
		http.NotFound(w, r)
		return
	}
	clean := filepath.Clean("/" + rest)[1:]
	top := strings.Split(filepath.ToSlash(clean), "/")[0]
	if top != "icons" && top != "web" && top != "resources" && top != "editor" && top != "keymaps" && top != "themes" {
		http.NotFound(w, r)
		return
	}
	full := filepath.Join(plug.Dir, clean)
	if !strings.HasPrefix(full, plug.Dir+string(filepath.Separator)) {
		http.NotFound(w, r)
		return
	}
	if strings.HasSuffix(full, ".svg") {
		w.Header().Set("Content-Type", "image/svg+xml")
	}
	// 插件升级后文件会变：每次都向内核确认（本机请求，未变时只回 304）
	w.Header().Set("Cache-Control", "no-cache")
	http.ServeFile(w, r, full)
}

func (s *Server) handleMCP(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodPost:
		body, err := io.ReadAll(io.LimitReader(r.Body, 32<<20))
		if err != nil {
			http.Error(w, err.Error(), 400)
			return
		}
		out := s.MCP.Handle(r.Context(), body)
		if out == nil {
			w.WriteHeader(http.StatusAccepted)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(out)
	case http.MethodDelete:
		w.WriteHeader(200)
	default:
		w.Header().Set("Allow", "POST, DELETE")
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// handleEditor 提供编辑器网页（静态文件，目录见 Server.EditorDir）。
func (s *Server) handleEditor(w http.ResponseWriter, r *http.Request) {
	if s.EditorDir == "" {
		http.NotFound(w, r)
		return
	}
	rel := filepath.Clean("/" + strings.TrimPrefix(r.URL.Path, "/editor/"))[1:]
	if rel == "" {
		rel = "index.html"
	}
	full := filepath.Join(s.EditorDir, rel)
	if !strings.HasPrefix(full, filepath.Clean(s.EditorDir)+string(filepath.Separator)) {
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

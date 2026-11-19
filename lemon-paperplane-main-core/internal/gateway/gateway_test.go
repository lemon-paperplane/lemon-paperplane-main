package gateway

import (
	"bufio"
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/remote"
)

// fakeAPI 模拟内核接口：call 回显调用者，events 推两条事件。
func fakeAPI() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/v1/call/", func(w http.ResponseWriter, r *http.Request) {
		who := ""
		if p := remote.PrincipalFrom(r.Context()); p != nil {
			who = p.Name
		}
		_ = json.NewEncoder(w).Encode(map[string]any{"result": map[string]string{"op": strings.TrimPrefix(r.URL.Path, "/api/v1/call/"), "who": who, "auth": r.Header.Get("Authorization")}})
	})
	mux.HandleFunc("/api/v1/events", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		f := w.(http.Flusher)
		for i := 0; i < 2; i++ {
			fmt.Fprintf(w, "data: {\"n\":%d}\n\n", i)
			f.Flush()
			time.Sleep(50 * time.Millisecond)
		}
		if r.URL.Query().Get("hold") != "" {
			<-r.Context().Done() // 长连接：一直挂着，直到连接被切断
		}
	})
	return mux
}

type env struct {
	rm   *remote.Manager
	url  string
	fp   string
	gw   *Manager
	port int
}

func setup(t *testing.T) *env {
	t.Helper()
	dir := t.TempDir()
	// 端口 0：由系统分配，避免「先探测空闲端口再监听」之间被别的进程占用
	rm, err := remote.New(remote.Options{Dir: filepath.Join(dir, "remote"), Handler: fakeAPI(), Logger: log.New(io.Discard, "", 0),
		Version: "test", Serve: true, Listen: "127.0.0.1:0"})
	if err != nil {
		t.Fatal(err)
	}
	rm.Start()
	t.Cleanup(rm.Close)
	st := rm.Status()
	if !st.Running {
		t.Fatalf("not running: %s", st.Error)
	}
	port := st.Port
	ed := filepath.Join(dir, "editor")
	_ = os.MkdirAll(ed, 0o755)
	_ = os.WriteFile(filepath.Join(ed, "index.html"), []byte("local editor"), 0o644)
	gw := New(ed, "test", log.New(io.Discard, "", 0))
	t.Cleanup(gw.CloseAll)
	return &env{rm: rm, url: fmt.Sprintf("https://127.0.0.1:%d", port), fp: st.Fingerprint, gw: gw, port: port}
}

func localCall(t *testing.T, port int, token, op string) (int, map[string]any) {
	t.Helper()
	req, _ := http.NewRequest("POST", fmt.Sprintf("http://127.0.0.1:%d/api/v1/call/%s", port, op), strings.NewReader("{}"))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	var out map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&out)
	return resp.StatusCode, out
}

func TestKeyGatewayForwardRevoke(t *testing.T) {
	e := setup(t)
	ctx := context.Background()
	ck, err := e.rm.CreateKey("mac", "", remote.AuditEntry{Who: "test"})
	if err != nil {
		t.Fatal(err)
	}
	p, err := remote.DecodeCode(ck.Code)
	if err != nil || p.Token != ck.Token || !remote.SameFingerprint(p.Fingerprint, e.fp) {
		t.Fatalf("code: %v %+v", err, p)
	}

	// 探测：自签证书不受信任，指纹一致
	pr, err := e.gw.Probe(ctx, e.url)
	if err != nil || pr.Trusted || !remote.SameFingerprint(pr.Fingerprint, e.fp) || pr.Hello.APIVersion != remote.APIVersion {
		t.Fatalf("probe: %v %+v", err, pr)
	}

	// 打开网关，经回环端口调用：服务器看到的是密钥身份，本机令牌不会传过去
	or, err := e.gw.Open(ctx, e.url, e.fp, ck.Token, false)
	if err != nil {
		t.Fatal(err)
	}
	code, out := localCall(t, or.Port, or.Token, "project.list")
	res, _ := out["result"].(map[string]any)
	if code != 200 || res["who"] != "mac" || res["auth"] != "" {
		t.Fatalf("call via gateway: %d %v", code, out)
	}
	// 本机令牌不对 → 401
	if code, _ := localCall(t, or.Port, "wrong", "project.list"); code != 401 {
		t.Fatalf("bad local token: %d", code)
	}
	// 编辑器网页用本机的
	resp, err := http.Get(fmt.Sprintf("http://127.0.0.1:%d/editor/index.html", or.Port))
	if err != nil {
		t.Fatal(err)
	}
	b, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if string(b) != "local editor" {
		t.Fatalf("editor: %q", b)
	}
	// 事件流透传（逐条到达）
	resp, err = http.Get(fmt.Sprintf("http://127.0.0.1:%d/api/v1/events?token=%s", or.Port, or.Token))
	if err != nil {
		t.Fatal(err)
	}
	sc := bufio.NewScanner(resp.Body)
	var events []string
	for sc.Scan() {
		if strings.HasPrefix(sc.Text(), "data:") {
			events = append(events, sc.Text())
		}
	}
	resp.Body.Close()
	if len(events) != 2 {
		t.Fatalf("events: %v", events)
	}

	// 端口转发：服务器 localhost 上的回显服务
	echo, _ := net.Listen("tcp", "127.0.0.1:0")
	defer echo.Close()
	go func() {
		for {
			c, err := echo.Accept()
			if err != nil {
				return
			}
			go func() { _, _ = io.Copy(c, c); c.Close() }()
		}
	}()
	ep := echo.Addr().(*net.TCPAddr).Port
	fr, err := e.gw.Forward(or.ID, ep)
	if err != nil {
		t.Fatal(err)
	}
	c, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", fr.LocalPort))
	if err != nil {
		t.Fatal(err)
	}
	_, _ = c.Write([]byte("ping\n"))
	line, err := bufio.NewReader(c).ReadString('\n')
	c.Close()
	if err != nil || line != "ping\n" {
		t.Fatalf("tunnel echo: %q %v", line, err)
	}

	// 吊销前先开一条长事件流和一条隧道连接：吊销后两条都要立即断开
	hold, err := http.Get(fmt.Sprintf("http://127.0.0.1:%d/api/v1/events?hold=1&token=%s", or.Port, or.Token))
	if err != nil {
		t.Fatal(err)
	}
	holdDone := make(chan struct{})
	go func() { _, _ = io.Copy(io.Discard, hold.Body); hold.Body.Close(); close(holdDone) }()
	tc, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", fr.LocalPort))
	if err != nil {
		t.Fatal(err)
	}
	_, _ = tc.Write([]byte("x\n"))
	tr := bufio.NewReader(tc)
	if l, err := tr.ReadString('\n'); err != nil || l != "x\n" {
		t.Fatalf("tunnel before revoke: %q %v", l, err)
	}
	time.Sleep(100 * time.Millisecond)

	// 吊销后立即失效：经网关的调用变成 403 remote_unauthorized，重新打开报 unauthorized
	if err := e.rm.RevokeKey(ck.Key.ID, remote.AuditEntry{Who: "test"}); err != nil {
		t.Fatal(err)
	}
	select {
	case <-holdDone:
	case <-time.After(3 * time.Second):
		t.Fatal("event stream still open after revoke")
	}
	_ = tc.SetReadDeadline(time.Now().Add(3 * time.Second))
	if _, err := tr.ReadString('\n'); err == nil {
		t.Fatal("tunnel still open after revoke")
	} else if ne, ok := err.(net.Error); ok && ne.Timeout() {
		t.Fatal("tunnel not closed after revoke (timed out)")
	}
	tc.Close()
	code, out = localCall(t, or.Port, or.Token, "project.list")
	if errm, _ := out["error"].(map[string]any); code != 403 || errm["code"] != "remote_unauthorized" {
		t.Fatalf("after revoke: %d %v", code, out)
	}
	_ = e.gw.Close(or.ID)
	if _, err := e.gw.Open(ctx, e.url, e.fp, ck.Token, false); err == nil || err.(*Error).Code() != "unauthorized" {
		t.Fatalf("open after revoke: %v", err)
	}
}

func TestFingerprintMismatch(t *testing.T) {
	e := setup(t)
	ck, _ := e.rm.CreateKey("mac", "", remote.AuditEntry{})
	bad := "sha256:" + strings.Repeat("ab", 32)
	_, err := e.gw.Open(context.Background(), e.url, bad, ck.Token, false)
	ge, ok := err.(*Error)
	if !ok || ge.Code() != "fingerprint_mismatch" {
		t.Fatalf("want fingerprint_mismatch, got %v", err)
	}
}

func TestPasswordLoginAndBan(t *testing.T) {
	e := setup(t)
	ctx := context.Background()
	if _, err := e.gw.Login(ctx, e.url, e.fp, "alice", "correct horse battery", "mac", false); err == nil || err.(*Error).Code() != "password_disabled" {
		t.Fatalf("disabled: %v", err)
	}
	on := true
	if _, err := e.rm.Configure(remote.ConfigureParams{PasswordLogin: &on}, remote.AuditEntry{}); err != nil {
		t.Fatal(err)
	}
	if err := e.rm.SetUser("alice", "short", remote.AuditEntry{}); err == nil {
		t.Fatal("short password accepted")
	}
	if err := e.rm.SetUser("alice", "correct horse battery", remote.AuditEntry{}); err != nil {
		t.Fatal(err)
	}
	tok, err := e.gw.Login(ctx, e.url, e.fp, "alice", "correct horse battery", "mac", false)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := e.gw.Open(ctx, e.url, e.fp, tok, false); err != nil {
		t.Fatalf("open with device key: %v", err)
	}
	keys := e.rm.Keys("")
	if len(keys) != 1 || keys[0].Kind != "device" || keys[0].User != "alice" {
		t.Fatalf("device key: %+v", keys)
	}
	// 连续失败 5 次 → 封禁（429），正确密码也进不来
	for i := 0; i < 5; i++ {
		if _, err := e.gw.Login(ctx, e.url, e.fp, "alice", "wrong password!!", "mac", false); err == nil {
			t.Fatal("wrong password accepted")
		}
	}
	if _, err := e.gw.Login(ctx, e.url, e.fp, "alice", "correct horse battery", "mac", false); err == nil || err.(*Error).Code() != "rate_limited" {
		t.Fatalf("want rate_limited, got %v", err)
	}
	// 删除账号同时吊销它登录过的设备
	if err := e.rm.RemoveUser("alice", remote.AuditEntry{}); err != nil {
		t.Fatal(err)
	}
	if n := len(e.rm.Keys("")); n != 0 {
		t.Fatalf("device keys left: %d", n)
	}
}

func TestRemoteGuard(t *testing.T) {
	e := setup(t)
	ck, _ := e.rm.CreateKey("mac", "", remote.AuditEntry{})
	hc := &http.Client{Transport: &http.Transport{TLSClientConfig: &tls.Config{InsecureSkipVerify: true}}}
	do := func(method, path, token string, hdr map[string]string) int {
		req, _ := http.NewRequest(method, e.url+path, strings.NewReader("{}"))
		if token != "" {
			req.Header.Set("Authorization", "Bearer "+token)
		}
		for k, v := range hdr {
			req.Header.Set(k, v)
		}
		resp, err := hc.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		resp.Body.Close()
		return resp.StatusCode
	}
	if c := do("GET", "/api/v1/auth/hello", "", nil); c != 200 {
		t.Fatalf("hello: %d", c)
	}
	if c := do("POST", "/api/v1/call/project.list", "", nil); c != 401 {
		t.Fatalf("no token: %d", c)
	}
	if c := do("POST", "/api/v1/call/project.list", ck.Token, nil); c != 200 {
		t.Fatalf("token: %d", c)
	}
	if c := do("POST", "/api/v1/call/project.list", ck.Token, map[string]string{"Origin": "https://evil.example"}); c != 403 {
		t.Fatalf("origin: %d", c)
	}
	if c := do("GET", "/editor/index.html", ck.Token, nil); c != 404 {
		t.Fatalf("editor should not be served remotely: %d", c)
	}
	// 用错误密钥试探 5 次后整个 IP 被封，正确密钥也是 429
	for i := 0; i < 5; i++ {
		do("POST", "/api/v1/call/project.list", "ppk_00000000_"+strings.Repeat("x", 43), nil)
	}
	if c := do("POST", "/api/v1/call/project.list", ck.Token, nil); c != 429 {
		t.Fatalf("ban: %d", c)
	}
	var sawBan bool
	for _, a := range e.rm.AuditRecent(50) {
		if a.Event == "ban" {
			sawBan = true
		}
	}
	if !sawBan {
		t.Fatal("ban not audited")
	}
}

func TestNormalizeAddress(t *testing.T) {
	cases := map[string]string{
		"192.168.1.10":              "https://192.168.1.10:47290",
		"host:8443":                 "https://host:8443",
		"https://example.com":       "https://example.com:443",
		"https://example.com:9000/": "https://example.com:9000",
		"[::1]:5000":                "https://[::1]:5000",
		"fe80::1":                   "https://[fe80::1]:47290",
	}
	for in, want := range cases {
		got, err := remote.NormalizeAddress(in, 0)
		if err != nil || got != want {
			t.Errorf("%s → %s (%v), want %s", in, got, err, want)
		}
	}
	if _, err := remote.NormalizeAddress("http://x", 0); err == nil {
		t.Error("http accepted")
	}
}

package remote

import (
	"encoding/json"
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestPasswordHash(t *testing.T) {
	h, err := HashPassword("correct horse battery")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(h, "$argon2id$v=19$m=19456,t=2,p=1$") {
		t.Fatalf("phc: %s", h)
	}
	if !VerifyPassword(h, "correct horse battery") || VerifyPassword(h, "correct horse batterY") {
		t.Fatal("verify")
	}
	for _, bad := range []string{"", "$argon2i$x", "$argon2id$v=19$m=0,t=1,p=1$AA$AA", "$argon2id$v=19$m=99999999,t=1,p=1$AA$AA"} {
		if VerifyPassword(bad, "x") {
			t.Fatalf("accepted %q", bad)
		}
	}
	if ValidatePassword("123456789") == nil || ValidatePassword("1234567890") != nil {
		t.Fatal("length rule")
	}
}

func TestTokenAndCode(t *testing.T) {
	id, tok, hash := newToken()
	k := &Key{ID: id, Hash: hash}
	if matchKey([]*Key{k}, tok) != k {
		t.Fatal("match")
	}
	if matchKey([]*Key{k}, tok+"x") != nil || matchKey([]*Key{k}, strings.Replace(tok, id, "00000000", 1)) != nil || matchKey([]*Key{k}, "") != nil {
		t.Fatal("false match")
	}
	code := EncodeCode(CodePayload{URLs: []string{"https://a:1"}, Fingerprint: "sha256:" + strings.Repeat("0", 64), Token: tok, Name: "srv"})
	p, err := DecodeCode("  " + code[:20] + "\n" + code[20:] + " ")
	if err != nil || p.Token != tok || p.Name != "srv" {
		t.Fatalf("decode: %v %+v", err, p)
	}
	for _, bad := range []string{"", "ppc1_", "ppc1_!!!", "xyz", EncodeCode(CodePayload{URLs: []string{"https://a:1"}, Fingerprint: "f", Token: "nope"})} {
		if _, err := DecodeCode(bad); err == nil {
			t.Fatalf("accepted %q", bad)
		}
	}
	if !SameFingerprint("sha256:ABCD"+strings.Repeat("0", 60), "abcd"+strings.Repeat("0", 60)) || SameFingerprint("sha256:ab", "sha256:ab") {
		t.Fatal("fingerprint compare")
	}
}

func TestLimiterEscalates(t *testing.T) {
	l := newLimiter()
	now := time.Unix(1_800_000_000, 0)
	l.now = func() time.Time { return now }
	var d time.Duration
	for i := 0; i < failThreshold; i++ {
		_, d = l.fail("1.2.3.4")
	}
	if b, _ := l.banned("1.2.3.4"); !b || d != banBase {
		t.Fatalf("first ban %v %v", b, d)
	}
	if b, _ := l.banned("5.6.7.8"); b {
		t.Fatal("other ip banned")
	}
	now = now.Add(banBase + time.Second)
	if b, _ := l.banned("1.2.3.4"); b {
		t.Fatal("ban not expired")
	}
	for i := 0; i < failThreshold; i++ {
		_, d = l.fail("1.2.3.4")
	}
	if d != 2*banBase {
		t.Fatalf("second ban should double: %v", d)
	}
	// 失败分散在窗口之外不累计
	now = now.Add(48 * time.Hour)
	for i := 0; i < failThreshold-1; i++ {
		l.fail("9.9.9.9")
		now = now.Add(failWindow)
	}
	if b, _ := l.banned("9.9.9.9"); b {
		t.Fatal("failures outside the window counted")
	}
	// 全局登录频率
	l2 := newLimiter()
	l2.now = func() time.Time { return now }
	for i := 0; i < loginPerMin; i++ {
		if !l2.allowLogin() {
			t.Fatal("too early")
		}
	}
	if l2.allowLogin() {
		t.Fatal("global login limit")
	}
}

func TestPlainUsersMigrated(t *testing.T) {
	dir := t.TempDir()
	cfg := `{"enabled": true, "passwordLogin": true, "users": [{"name": "admin", "password": "a very long password"}]}`
	if err := os.WriteFile(filepath.Join(dir, "config.json"), []byte(cfg), 0o600); err != nil {
		t.Fatal(err)
	}
	m, err := New(Options{Dir: dir, Logger: log.New(io.Discard, "", 0)})
	if err != nil {
		t.Fatal(err)
	}
	b, _ := os.ReadFile(filepath.Join(dir, "config.json"))
	if strings.Contains(string(b), "a very long password") || strings.Contains(string(b), "users") {
		t.Fatalf("plaintext left in config: %s", b)
	}
	var creds credentials
	cb, _ := os.ReadFile(filepath.Join(dir, "credentials.json"))
	_ = json.Unmarshal(cb, &creds)
	if len(creds.Users) != 1 || !VerifyPassword(creds.Users[0].Hash, "a very long password") {
		t.Fatalf("hash: %+v", creds.Users)
	}
	if st, _ := os.Stat(filepath.Join(dir, "credentials.json")); st.Mode().Perm() != 0o600 {
		t.Fatalf("credentials perm %v", st.Mode().Perm())
	}
	if len(m.Users()) != 1 {
		t.Fatal("users")
	}
	// 太短的明文密码直接报错，不悄悄放过
	_ = os.WriteFile(filepath.Join(dir, "config.json"), []byte(`{"users":[{"name":"x","password":"short"}]}`), 0o600)
	if _, err := New(Options{Dir: dir, Logger: log.New(io.Discard, "", 0)}); err == nil {
		t.Fatal("short plaintext password accepted")
	}
}

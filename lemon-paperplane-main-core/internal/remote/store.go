// Package remote 是远程监听：其他设备的界面经 TLS + 访问密钥管理这台机器（设计见 docs/remote.md）。
//
// 能调接口就等于能在这台机器上执行任意命令，所以按 SSH 级别设计：默认关闭、只走 TLS（证书指纹固定）、
// 除 hello / login 外一律要访问密钥、登录限速与封禁、审计日志。
package remote

import (
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

const (
	// DefaultPort 是远程监听的默认端口。
	DefaultPort = 47290
	// APIVersion 是远程协议版本：客户端与服务器不同就拒绝连接。
	APIVersion = 1
	// MinPasswordLen 是账号密码的最短长度。
	MinPasswordLen = 10
)

// Config 是 remote/config.json（可以手改）。
type Config struct {
	Enabled       bool     `json:"enabled"`
	Listen        string   `json:"listen,omitempty"` // 默认 0.0.0.0:47290
	PasswordLogin bool     `json:"passwordLogin"`
	Autostart     bool     `json:"autostart,omitempty"`
	AllowCIDRs    []string `json:"allowCIDRs,omitempty"`
	TLSCert       string   `json:"tlsCert,omitempty"` // 自己的证书（PEM）；不填用自签证书
	TLSKey        string   `json:"tlsKey,omitempty"`
	// Users 只用于手写配置：明文密码，启动时换成哈希存进 credentials.json，并从这里删掉
	Users []PlainUser `json:"users,omitempty"`
}

// PlainUser 是手写在 config.json 里的账号。
type PlainUser struct {
	Name     string `json:"name"`
	Password string `json:"password"`
}

// ListenAddr 返回实际监听地址。
func (c *Config) ListenAddr() string {
	if strings.TrimSpace(c.Listen) == "" {
		return fmt.Sprintf("0.0.0.0:%d", DefaultPort)
	}
	return c.Listen
}

// Key 是一条访问密钥（只存哈希）。
type Key struct {
	ID         string     `json:"id"`
	Name       string     `json:"name"`
	Kind       string     `json:"kind"` // key：手动生成；device：账号密码登录时发给设备的
	User       string     `json:"user,omitempty"`
	Hash       string     `json:"hash"` // sha256(密钥的随机部分)
	CreatedAt  time.Time  `json:"createdAt"`
	LastUsedAt *time.Time `json:"lastUsedAt,omitempty"`
	LastIP     string     `json:"lastIP,omitempty"`
}

// User 是账号（只存 Argon2id 哈希）。
type User struct {
	Name      string    `json:"name"`
	Hash      string    `json:"hash"`
	CreatedAt time.Time `json:"createdAt"`
	UpdatedAt time.Time `json:"updatedAt"`
}

type credentials struct {
	Keys  []*Key  `json:"keys"`
	Users []*User `json:"users"`
}

// KeyInfo 是给界面看的密钥信息（不含哈希）。
type KeyInfo struct {
	ID         string     `json:"id"`
	Name       string     `json:"name"`
	Kind       string     `json:"kind"`
	User       string     `json:"user,omitempty"`
	CreatedAt  time.Time  `json:"createdAt"`
	LastUsedAt *time.Time `json:"lastUsedAt,omitempty"`
	LastIP     string     `json:"lastIP,omitempty"`
	Current    bool       `json:"current"`
}

// UserInfo 是给界面看的账号信息。
type UserInfo struct {
	Name      string    `json:"name"`
	CreatedAt time.Time `json:"createdAt"`
	UpdatedAt time.Time `json:"updatedAt"`
}

// ---------------------------------------------------------------- 访问密钥

const tokenPrefix = "ppk_"

// newToken 生成 ppk_<编号>_<256 位随机>，返回编号、完整密钥与哈希。
func newToken() (id, token, hash string) {
	idb := make([]byte, 4)
	sec := make([]byte, 32)
	_, _ = rand.Read(idb)
	_, _ = rand.Read(sec)
	id = hex.EncodeToString(idb)
	s := base64.RawURLEncoding.EncodeToString(sec)
	return id, tokenPrefix + id + "_" + s, hashSecret(s)
}

func hashSecret(s string) string {
	h := sha256.Sum256([]byte(s))
	return hex.EncodeToString(h[:])
}

// parseToken 拆出编号与随机部分。
func parseToken(tok string) (id, secret string, ok bool) {
	if !strings.HasPrefix(tok, tokenPrefix) {
		return "", "", false
	}
	id, secret, ok = strings.Cut(strings.TrimPrefix(tok, tokenPrefix), "_")
	if !ok || len(id) != 8 || len(secret) < 40 || len(secret) > 64 {
		return "", "", false
	}
	return id, secret, true
}

// matchKey：按编号找到密钥后恒定时间比较哈希。
func matchKey(keys []*Key, tok string) *Key {
	id, secret, ok := parseToken(tok)
	if !ok {
		return nil
	}
	want := hashSecret(secret)
	for _, k := range keys {
		if k.ID == id && subtle.ConstantTimeCompare([]byte(k.Hash), []byte(want)) == 1 {
			return k
		}
	}
	return nil
}

var userNameRe = regexp.MustCompile(`^[A-Za-z0-9._@-]{1,32}$`)

// ValidUserName 校验账号名。
func ValidUserName(n string) bool { return userNameRe.MatchString(n) }

func cleanName(s string, max int) string {
	s = strings.TrimSpace(strings.Map(func(r rune) rune {
		if r < 0x20 || r == 0x7f {
			return -1
		}
		return r
	}, s))
	if r := []rune(s); len(r) > max {
		s = string(r[:max])
	}
	return s
}

// ---------------------------------------------------------------- 文件

func readJSON(path string, v any) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	return json.Unmarshal(b, v)
}

// writeJSON 原子写入（先写临时文件再改名）。
func writeJSON(path string, v any, perm os.FileMode) error {
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, append(b, '\n'), perm); err != nil {
		return err
	}
	_ = os.Chmod(tmp, perm)
	if err := os.Rename(tmp, path); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return nil
}

// ErrNotFound 表示没有这条密钥 / 账号。
var ErrNotFound = errors.New("not found")

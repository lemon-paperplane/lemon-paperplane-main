package remote

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/hex"
	"encoding/pem"
	"errors"
	"fmt"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"strings"
	"time"

	"golang.org/x/crypto/argon2"
)

// ---------------------------------------------------------------- 证书

// Fingerprint 是证书公钥（SPKI）的 SHA-256：「sha256:<64 位十六进制>」。换证书但不换私钥时不变。
func Fingerprint(cert *x509.Certificate) string {
	h := sha256.Sum256(cert.RawSubjectPublicKeyInfo)
	return "sha256:" + hex.EncodeToString(h[:])
}

// FingerprintDisplay 把指纹排成 16 组 4 位大写，方便人眼核对。
func FingerprintDisplay(fp string) string {
	h := strings.ToUpper(strings.TrimPrefix(fp, "sha256:"))
	var parts []string
	for i := 0; i+4 <= len(h); i += 4 {
		parts = append(parts, h[i:i+4])
	}
	return strings.Join(parts, " ")
}

// SameFingerprint 恒定时间比较两个指纹（大小写、分隔符不敏感）。
func SameFingerprint(a, b string) bool {
	norm := func(s string) string {
		s = strings.ToLower(strings.TrimPrefix(strings.TrimSpace(s), "sha256:"))
		return strings.NewReplacer(" ", "", ":", "").Replace(s)
	}
	x, y := norm(a), norm(b)
	return len(x) == 64 && subtle.ConstantTimeCompare([]byte(x), []byte(y)) == 1
}

// errNoCert：还没有证书（只查看状态时不生成）。
var errNoCert = errors.New("no certificate yet")

// loadOrCreateCert：自己的证书优先；否则用 tls/ 下的自签证书，没有（且 create）就生成（ECDSA P-256，十年）。
func loadOrCreateCert(dir string, cfg *Config, hostname string, create bool) (*tls.Certificate, bool, error) {
	if cfg.TLSCert != "" || cfg.TLSKey != "" {
		c, err := tls.LoadX509KeyPair(cfg.TLSCert, cfg.TLSKey)
		if err != nil {
			return nil, true, errors.New(i18n.T("remote.readCert", cfg.TLSCert, err))
		}
		if c.Leaf == nil {
			c.Leaf, _ = x509.ParseCertificate(c.Certificate[0])
		}
		return &c, true, nil
	}
	certPath, keyPath := filepath.Join(dir, "tls", "cert.pem"), filepath.Join(dir, "tls", "key.pem")
	if c, err := tls.LoadX509KeyPair(certPath, keyPath); err == nil {
		if c.Leaf == nil {
			c.Leaf, _ = x509.ParseCertificate(c.Certificate[0])
		}
		if c.Leaf != nil && time.Now().Before(c.Leaf.NotAfter) {
			return &c, false, nil
		}
	}
	if !create {
		return nil, false, errNoCert
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, false, err
	}
	serial, _ := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 127))
	tpl := &x509.Certificate{
		SerialNumber:          serial,
		Subject:               pkix.Name{CommonName: "Lemon Paperplane " + hostname, Organization: []string{"Lemon Paperplane"}},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().AddDate(10, 0, 0),
		KeyUsage:              x509.KeyUsageDigitalSignature,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		BasicConstraintsValid: true,
		DNSNames:              []string{"localhost"},
		IPAddresses:           []net.IP{net.IPv4(127, 0, 0, 1), net.IPv6loopback},
	}
	if hostname != "" {
		tpl.DNSNames = append(tpl.DNSNames, hostname)
	}
	der, err := x509.CreateCertificate(rand.Reader, tpl, tpl, &key.PublicKey, key)
	if err != nil {
		return nil, false, err
	}
	kb, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		return nil, false, err
	}
	if err := os.MkdirAll(filepath.Dir(certPath), 0o700); err != nil {
		return nil, false, err
	}
	if err := os.WriteFile(keyPath, pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: kb}), 0o600); err != nil {
		return nil, false, err
	}
	if err := os.WriteFile(certPath, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), 0o644); err != nil {
		return nil, false, err
	}
	c, err := tls.LoadX509KeyPair(certPath, keyPath)
	if err != nil {
		return nil, false, err
	}
	c.Leaf, _ = x509.ParseCertificate(c.Certificate[0])
	return &c, false, nil
}

// ---------------------------------------------------------------- 密码（Argon2id，PHC 字符串）

// OWASP 推荐的最低参数：19 MiB 内存、2 轮、1 线程（小内存 ARM 机器也撑得住）
const (
	argonMem  = 19 * 1024
	argonTime = 2
	argonPar  = 1
	argonLen  = 32
)

// HashPassword 返回 $argon2id$v=19$m=…,t=…,p=…$<盐>$<哈希>。
func HashPassword(pw string) (string, error) {
	salt := make([]byte, 16)
	if _, err := rand.Read(salt); err != nil {
		return "", err
	}
	h := argon2.IDKey([]byte(pw), salt, argonTime, argonMem, argonPar, argonLen)
	return fmt.Sprintf("$argon2id$v=%d$m=%d,t=%d,p=%d$%s$%s", argon2.Version, argonMem, argonTime, argonPar,
		base64.RawStdEncoding.EncodeToString(salt), base64.RawStdEncoding.EncodeToString(h)), nil
}

// VerifyPassword 校验 PHC 字符串。
func VerifyPassword(phc, pw string) bool {
	parts := strings.Split(phc, "$")
	if len(parts) != 6 || parts[1] != "argon2id" {
		return false
	}
	var m, t uint32
	var p uint8
	if _, err := fmt.Sscanf(parts[3], "m=%d,t=%d,p=%d", &m, &t, &p); err != nil || m == 0 || m > 1<<20 || t == 0 || t > 20 || p == 0 {
		return false
	}
	salt, err1 := base64.RawStdEncoding.DecodeString(parts[4])
	want, err2 := base64.RawStdEncoding.DecodeString(parts[5])
	if err1 != nil || err2 != nil || len(want) == 0 || len(want) > 128 {
		return false
	}
	got := argon2.IDKey([]byte(pw), salt, t, m, p, uint32(len(want)))
	return subtle.ConstantTimeCompare(got, want) == 1
}

// dummyHash：账号不存在时也算一次，防止按响应时间猜账号名。
var dummyHash = func() string {
	h, _ := HashPassword("paperplane-dummy-password")
	return h
}()

// ValidatePassword 检查密码强度。
func ValidatePassword(pw string) error {
	if len([]rune(pw)) < MinPasswordLen {
		return errors.New(i18n.T("remote.pwShort", MinPasswordLen))
	}
	if len(pw) > 256 {
		return errors.New(i18n.T("remote.pwLong"))
	}
	return nil
}

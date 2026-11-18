package sdk

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// Bundle 是一份本地化文案（l10n/<locale>.json，扁平 key → 文案）。
// 查找顺序：精确 locale → 语言前缀（zh-Hans → zh）→ en → key 本身。
type Bundle struct {
	primary  map[string]string
	fallback map[string]string
}

// LoadBundle 从插件目录加载 locale 对应的文案。
func LoadBundle(pluginDir, locale string) (*Bundle, error) {
	b := &Bundle{}
	for _, l := range LocaleChain(locale) {
		if m, err := readStrings(filepath.Join(pluginDir, "l10n", l+".json")); err == nil {
			b.primary = m
			break
		}
	}
	if m, err := readStrings(filepath.Join(pluginDir, "l10n", "en.json")); err == nil {
		b.fallback = m
	}
	return b, nil
}

// LocaleChain 返回 locale 的回退链，如 "zh-Hans-CN" → ["zh-Hans-CN","zh-Hans","zh"]。
func LocaleChain(locale string) []string {
	var out []string
	parts := strings.Split(strings.ReplaceAll(locale, "_", "-"), "-")
	for i := len(parts); i > 0; i-- {
		out = append(out, strings.Join(parts[:i], "-"))
	}
	return out
}

func readStrings(path string) (map[string]string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var m map[string]string
	if err := json.Unmarshal(data, &m); err != nil {
		return nil, err
	}
	return m, nil
}

// NewBundle 用内存中的表构造（测试或内核使用）。
func NewBundle(primary, fallback map[string]string) *Bundle {
	return &Bundle{primary: primary, fallback: fallback}
}

// T 取文案并替换 {0}{1}… 占位。
func (b *Bundle) T(key string, args ...any) string {
	s, ok := "", false
	if b != nil {
		if s, ok = b.primary[key]; !ok {
			s, ok = b.fallback[key]
		}
	}
	if !ok {
		s = key
	}
	for i, a := range args {
		s = strings.ReplaceAll(s, fmt.Sprintf("{%d}", i), fmt.Sprint(a))
	}
	return s
}

// Resolve 解析 "%key%" 形式的字符串；不是该形式的原样返回。
func (b *Bundle) Resolve(s string) string {
	if len(s) > 2 && strings.HasPrefix(s, "%") && strings.HasSuffix(s, "%") {
		return b.T(s[1 : len(s)-1])
	}
	return s
}

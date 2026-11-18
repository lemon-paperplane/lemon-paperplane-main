// Package i18n 是内核自身的文案（错误信息、内置诊断、任务标题）。
package i18n

import (
	"embed"
	"encoding/json"
	"os"
	"strings"
	"sync"

	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

//go:embed l10n/*.json
var files embed.FS

var (
	mu      sync.RWMutex
	locale  = "en"
	bundles = map[string]map[string]string{}
	current *sdk.Bundle
)

func init() {
	for _, l := range []string{"en", "zh-Hans"} {
		b, err := files.ReadFile("l10n/" + l + ".json")
		if err != nil {
			continue
		}
		m := map[string]string{}
		_ = json.Unmarshal(b, &m)
		bundles[l] = m
	}
	SetLocale("system")
}

// SystemLocale 推断系统语言：显式环境变量 > 系统界面语言（macOS AppleLanguages）> LANG。
func SystemLocale() string {
	if v := os.Getenv("PAPERPLANE_LOCALE"); v != "" {
		if strings.HasPrefix(strings.ToLower(v), "zh") {
			return "zh-Hans"
		}
		return "en"
	}
	if l := platformLocale(); l != "" {
		return l
	}
	for _, k := range []string{"LC_ALL", "LC_MESSAGES", "LANG"} {
		if v := os.Getenv(k); v != "" && v != "C" && v != "POSIX" {
			if strings.HasPrefix(strings.ToLower(v), "zh") {
				return "zh-Hans"
			}
			return "en"
		}
	}
	return "en"
}

// SetLocale 设置当前语言（"system" 表示跟随系统）。返回生效的 locale。
func SetLocale(l string) string {
	if l == "" || l == "system" {
		l = SystemLocale()
	}
	if strings.HasPrefix(l, "zh") {
		l = "zh-Hans"
	} else if _, ok := bundles[l]; !ok {
		l = "en"
	}
	mu.Lock()
	locale = l
	current = sdk.NewBundle(bundles[l], bundles["en"])
	mu.Unlock()
	return l
}

// Locale 返回当前生效语言。
func Locale() string {
	mu.RLock()
	defer mu.RUnlock()
	return locale
}

// T 取文案。
func T(key string, args ...any) string {
	mu.RLock()
	b := current
	mu.RUnlock()
	return b.T(key, args...)
}

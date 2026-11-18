package i18n

import (
	"os/exec"
	"strings"
)

func platformLocale() string {
	out, err := exec.Command("defaults", "read", "-g", "AppleLanguages").Output()
	if err == nil {
		s := strings.ToLower(string(out))
		for _, line := range strings.Split(s, "\n") {
			line = strings.Trim(strings.TrimSpace(line), `",()`)
			if line == "" {
				continue
			}
			if strings.HasPrefix(line, "zh") {
				return "zh-Hans"
			}
			return "en"
		}
	}
	return ""
}

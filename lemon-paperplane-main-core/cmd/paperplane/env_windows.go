//go:build windows

package main

import (
	"log"
	"os"
	"path/filepath"
	"strings"

	"golang.org/x/sys/windows/registry"
)

// loadRegistryEnv 从注册表读最新的系统 / 用户环境变量。daemon 可能在用户装好 Node、JDK 之前就启动了，
// 进程环境是旧的；这里把注册表里的值补上（PATH 合并：注册表里的在前，进程里多出来的保留在后）。
func loadRegistryEnv(logger *log.Logger) {
	if os.Getenv("PAPERPLANE_NO_SHELL_ENV") != "" {
		return
	}
	sys := readEnvKey(registry.LOCAL_MACHINE, `SYSTEM\CurrentControlSet\Control\Session Manager\Environment`)
	usr := readEnvKey(registry.CURRENT_USER, `Environment`)
	n := 0
	merged := map[string]string{}
	for k, v := range sys {
		merged[strings.ToUpper(k)] = v
	}
	for k, v := range usr {
		merged[strings.ToUpper(k)] = v
	}
	for k, v := range merged {
		switch k {
		case "PATH", "USERNAME", "USERPROFILE", "TEMP", "TMP", "PAPERPLANE_DATA_DIR":
			continue
		}
		if strings.HasPrefix(k, "PAPERPLANE_") {
			continue
		}
		if os.Getenv(k) != v {
			_ = os.Setenv(k, v)
			n++
		}
	}
	var parts []string
	seen := map[string]bool{}
	add := func(list string) {
		for _, d := range filepath.SplitList(list) {
			d = strings.TrimSpace(d)
			key := strings.ToLower(strings.TrimRight(d, `\`))
			if d == "" || seen[key] {
				continue
			}
			seen[key] = true
			parts = append(parts, d)
		}
	}
	add(sys["Path"])
	add(usr["Path"])
	add(os.Getenv("PATH"))
	if p := strings.Join(parts, string(os.PathListSeparator)); p != os.Getenv("PATH") {
		_ = os.Setenv("PATH", p)
		n++
	}
	logger.Printf("loaded %d variables from registry", n)
}

func readEnvKey(root registry.Key, path string) map[string]string {
	out := map[string]string{}
	k, err := registry.OpenKey(root, path, registry.QUERY_VALUE)
	if err != nil {
		return out
	}
	defer k.Close()
	names, err := k.ReadValueNames(0)
	if err != nil {
		return out
	}
	for _, name := range names {
		v, typ, err := k.GetStringValue(name)
		if err != nil {
			continue
		}
		if typ == registry.EXPAND_SZ {
			if x, err := registry.ExpandString(v); err == nil {
				v = x
			}
		}
		// 名字统一成 PATH 的写法，方便合并
		if strings.EqualFold(name, "Path") {
			name = "Path"
		}
		out[name] = v
	}
	return out
}

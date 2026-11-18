// Package paths 决定运行时数据目录。
package paths

import (
	"os"
	"path/filepath"
	"runtime"
)

// DataDir 返回 $DATA。可用环境变量 PAPERPLANE_DATA_DIR 覆盖（测试/便携模式）。
func DataDir() string {
	if d := os.Getenv("PAPERPLANE_DATA_DIR"); d != "" {
		return d
	}
	home, _ := os.UserHomeDir()
	switch runtime.GOOS {
	case "darwin":
		return filepath.Join(home, "Library", "Application Support", "LemonPaperplane")
	case "windows":
		if a := os.Getenv("APPDATA"); a != "" {
			return filepath.Join(a, "LemonPaperplane")
		}
		return filepath.Join(home, "AppData", "Roaming", "LemonPaperplane")
	default:
		if x := os.Getenv("XDG_CONFIG_HOME"); x != "" {
			return filepath.Join(x, "lemon-paperplane")
		}
		return filepath.Join(home, ".config", "lemon-paperplane")
	}
}

// Layout 是数据目录下的各子目录。
type Layout struct {
	Root       string
	Projects   string
	Toolchains string
	Plugins    string
	PluginData string
	Cache      string
	Logs       string
	RunLogs    string
	Bin        string
}

// New 构造并创建目录。
func New(root string) (*Layout, error) {
	l := &Layout{
		Root:       root,
		Projects:   filepath.Join(root, "projects"),
		Toolchains: filepath.Join(root, "toolchains"),
		Plugins:    filepath.Join(root, "plugins"),
		PluginData: filepath.Join(root, "plugin-data"),
		Cache:      filepath.Join(root, "cache"),
		Logs:       filepath.Join(root, "logs"),
		RunLogs:    filepath.Join(root, "logs", "runs"),
		Bin:        filepath.Join(root, "bin"),
	}
	for _, d := range []string{l.Root, l.Projects, l.Toolchains, l.Plugins, l.PluginData, l.Cache, l.Logs, l.RunLogs, l.Bin} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			return nil, err
		}
	}
	return l, nil
}

// RuntimeFile 是 runtime.json 路径。
func (l *Layout) RuntimeFile() string { return filepath.Join(l.Root, "runtime.json") }

// SettingsFile 是 settings.json 路径。
func (l *Layout) SettingsFile() string { return filepath.Join(l.Root, "settings.json") }

// WorkspacesFile 是 workspaces.json 路径。
func (l *Layout) WorkspacesFile() string { return filepath.Join(l.Root, "workspaces.json") }

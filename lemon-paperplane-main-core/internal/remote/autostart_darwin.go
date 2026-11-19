package remote

import (
	"errors"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"html"
	"os"
	"path/filepath"
)

const autostartSupported = true

const launchAgentLabel = "cn.lemonit.paperplane.daemon"

// setAutostart：~/Library/LaunchAgents 下的 LaunchAgent，登录时以 `paperplane daemon` 启动后台服务。
func setAutostart(exe string, on bool) error {
	home, err := os.UserHomeDir()
	if err != nil {
		return err
	}
	p := filepath.Join(home, "Library", "LaunchAgents", launchAgentLabel+".plist")
	if !on {
		if err := os.Remove(p); err != nil && !os.IsNotExist(err) {
			return err
		}
		return nil
	}
	if exe == "" {
		return errors.New(i18n.T("remote.noExe"))
	}
	env := ""
	if d := os.Getenv("PAPERPLANE_DATA_DIR"); d != "" {
		env = "\t<key>EnvironmentVariables</key>\n\t<dict>\n\t\t<key>PAPERPLANE_DATA_DIR</key>\n\t\t<string>" + html.EscapeString(d) + "</string>\n\t</dict>\n"
	}
	plist := `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>Label</key>
	<string>` + launchAgentLabel + `</string>
	<key>ProgramArguments</key>
	<array>
		<string>` + html.EscapeString(exe) + `</string>
		<string>daemon</string>
	</array>
	<key>RunAtLoad</key>
	<true/>
	<key>ProcessType</key>
	<string>Background</string>
` + env + `</dict>
</plist>
`
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return err
	}
	return os.WriteFile(p, []byte(plist), 0o644)
}

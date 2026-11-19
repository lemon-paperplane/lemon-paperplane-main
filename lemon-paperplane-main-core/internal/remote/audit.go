package remote

import (
	"bufio"
	"encoding/json"
	"os"
	"sync"
	"time"
)

// AuditEntry 是审计日志的一行。
type AuditEntry struct {
	Time   time.Time `json:"time"`
	Event  string    `json:"event"`            // login.ok / login.fail / auth.fail / ban / key.create / key.revoke / user.set / user.remove / config / op / tunnel
	IP     string    `json:"ip,omitempty"`     // 远程来源；本机操作为空
	Who    string    `json:"who,omitempty"`    // 密钥名或账号
	Detail string    `json:"detail,omitempty"` // 操作名、端口等（不记参数）
}

const auditMaxBytes = 5 << 20

type auditLog struct {
	mu   sync.Mutex
	path string
}

func (a *auditLog) add(e AuditEntry) {
	if e.Time.IsZero() {
		e.Time = time.Now()
	}
	b, err := json.Marshal(e)
	if err != nil {
		return
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if st, err := os.Stat(a.path); err == nil && st.Size() > auditMaxBytes {
		_ = os.Rename(a.path, a.path+".1")
	}
	f, err := os.OpenFile(a.path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return
	}
	_, _ = f.Write(append(b, '\n'))
	_ = f.Close()
}

// recent 返回最近 limit 条，新的在前。
func (a *auditLog) recent(limit int) []AuditEntry {
	if limit <= 0 || limit > 1000 {
		limit = 50
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	f, err := os.Open(a.path)
	if err != nil {
		return []AuditEntry{}
	}
	defer f.Close()
	var all []AuditEntry
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 64<<10), 1<<20)
	for sc.Scan() {
		var e AuditEntry
		if json.Unmarshal(sc.Bytes(), &e) == nil {
			all = append(all, e)
			if len(all) > limit*4 {
				all = all[len(all)-limit:]
			}
		}
	}
	if len(all) > limit {
		all = all[len(all)-limit:]
	}
	out := make([]AuditEntry, 0, len(all))
	for i := len(all) - 1; i >= 0; i-- {
		out = append(out, all[i])
	}
	return out
}

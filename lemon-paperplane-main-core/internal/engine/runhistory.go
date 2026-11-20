package engine

import (
	"encoding/json"
	"os"
	"path/filepath"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/console"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
)

// 运行记录落盘：已结束的运行（连同日志文件）在内核重启后还能看到。
// 输出一直由内核写进 logs/runs/<id>.log；这里只保存运行列表 logs/runs/index.json。

func (e *Engine) runIndexPath() string { return filepath.Join(e.Layout.RunLogs, "index.json") }

// persistRuns 写运行列表（只记已结束的；按 runOrder 的先后）。interrupted 为 true 时把还在运行的也记成「被中断」（内核退出时）。
func (e *Engine) persistRuns(interrupted bool) {
	e.persistMu.Lock()
	defer e.persistMu.Unlock()
	e.mu.Lock()
	ids := append([]string{}, e.runOrder...)
	states := make([]*runState, 0, len(ids))
	for _, id := range ids {
		if rs := e.runs[id]; rs != nil {
			states = append(states, rs)
		}
	}
	e.mu.Unlock()
	var out []model.Run
	now := time.Now()
	for _, rs := range states {
		r := rs.snapshot()
		if active(r.State) {
			if !interrupted {
				continue
			}
			r.State, r.Error, r.EndedAt = model.RunFailed, i18n.T("run.interrupted"), &now
		}
		r.CPU, r.RSSMB = 0, 0
		out = append(out, r)
	}
	b, err := json.Marshal(out)
	if err != nil {
		return
	}
	tmp := e.runIndexPath() + ".tmp"
	if os.WriteFile(tmp, b, 0o600) == nil {
		_ = os.Rename(tmp, e.runIndexPath())
	}
}

// loadRuns 在启动时恢复已结束的运行（日志文件还在的）。
func (e *Engine) loadRuns() {
	b, err := os.ReadFile(e.runIndexPath())
	if err != nil {
		return
	}
	var runs []model.Run
	if json.Unmarshal(b, &runs) != nil {
		return
	}
	capacity := e.Store.Settings().LogLinesPerRun
	e.mu.Lock()
	defer e.mu.Unlock()
	for _, r := range runs {
		if r.ID == "" || r.LogFile == "" || e.runs[r.ID] != nil {
			continue
		}
		c, err := console.Open(r.ID, capacity, r.LogFile)
		if err != nil {
			continue
		}
		exited := make(chan struct{})
		close(exited)
		e.runs[r.ID] = &runState{r: r, console: c, exited: exited, diagSeen: map[string]bool{}, cancel: func() {}}
		e.runOrder = append(e.runOrder, r.ID)
		e.consoles[r.ID] = c
	}
}

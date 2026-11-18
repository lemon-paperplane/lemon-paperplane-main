package engine

import (
	"context"
	"encoding/json"
	"path/filepath"
	"sync"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/console"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
)

type taskState struct {
	mu       sync.Mutex
	t        model.Task
	cancel   context.CancelFunc
	console  *console.Console
	lastPub  time.Time
	finished chan struct{}
}

// NewTask 创建任务（带同名控制台）。fn 在后台执行，返回值写入 Result。
func (e *Engine) NewTask(kind, title, projectID, moduleID string, fn func(ctx context.Context, t *taskState) (any, error)) model.Task {
	id := store.NewID()
	ctx, cancel := context.WithCancel(context.Background())
	c := console.New(id, 5000, filepath.Join(e.Layout.RunLogs, "task-"+id+".log"))
	ts := &taskState{t: model.Task{ID: id, Kind: kind, Title: title, ProjectID: projectID, ModuleID: moduleID, State: model.TaskRunning, Percent: -1, StartedAt: time.Now()}, cancel: cancel, console: c, finished: make(chan struct{})}
	e.mu.Lock()
	e.tasks[id] = ts
	e.taskOrder = append(e.taskOrder, id)
	e.consoles[id] = c
	if projectID != "" {
		e.busy[projectID] = title
	}
	e.trimTasksLocked()
	e.mu.Unlock()
	e.Bus.Publish(model.EvTask, ts.snapshot())
	if projectID != "" {
		e.publishProject(projectID)
	}
	go func() {
		res, err := fn(ctx, ts)
		ts.mu.Lock()
		now := time.Now()
		ts.t.EndedAt = &now
		switch {
		case ctx.Err() != nil:
			ts.t.State = model.TaskCancelled
		case err != nil:
			ts.t.State = model.TaskFailed
			ts.t.Error = errString(err)
		default:
			ts.t.State = model.TaskSucceeded
			ts.t.Percent = 100
		}
		if res != nil {
			if b, err := json.Marshal(res); err == nil {
				ts.t.Result = b
			}
		}
		ts.mu.Unlock()
		if err != nil && ctx.Err() == nil {
			c.System(errString(err))
		}
		c.Close()
		cancel()
		close(ts.finished)
		e.mu.Lock()
		if projectID != "" && e.busy[projectID] == title {
			delete(e.busy, projectID)
		}
		e.mu.Unlock()
		e.Bus.Publish(model.EvTask, ts.snapshot())
		if projectID != "" {
			e.publishProject(projectID)
		}
	}()
	return ts.snapshot()
}

func (e *Engine) trimTasksLocked() {
	const keep = 100
	for len(e.taskOrder) > keep {
		id := e.taskOrder[0]
		ts := e.tasks[id]
		if ts != nil && ts.snapshot().State == model.TaskRunning {
			break
		}
		e.taskOrder = e.taskOrder[1:]
		delete(e.tasks, id)
		delete(e.consoles, id)
	}
}

func (t *taskState) snapshot() model.Task {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.t
}

// progress 更新进度（节流发布）。
func (e *Engine) taskProgress(id, msg string, pct float64) {
	e.mu.Lock()
	ts := e.tasks[id]
	e.mu.Unlock()
	if ts == nil {
		return
	}
	ts.mu.Lock()
	if msg != "" {
		ts.t.Message = msg
	}
	ts.t.Percent = pct
	pub := time.Since(ts.lastPub) > 200*time.Millisecond
	if pub {
		ts.lastPub = time.Now()
	}
	ts.mu.Unlock()
	if pub {
		e.Bus.Publish(model.EvTask, ts.snapshot())
	}
}

func (e *Engine) taskAddDiagnoses(id string, d []model.Task) {}

// Tasks 返回任务列表（新的在前）。
func (e *Engine) Tasks(activeOnly bool) []model.Task {
	e.mu.Lock()
	defer e.mu.Unlock()
	var out []model.Task
	for i := len(e.taskOrder) - 1; i >= 0; i-- {
		ts := e.tasks[e.taskOrder[i]]
		if ts == nil {
			continue
		}
		s := ts.snapshot()
		if activeOnly && s.State != model.TaskRunning {
			continue
		}
		out = append(out, s)
	}
	return out
}

// Task 返回任务。
func (e *Engine) Task(id string) (model.Task, bool) {
	e.mu.Lock()
	ts := e.tasks[id]
	e.mu.Unlock()
	if ts == nil {
		return model.Task{}, false
	}
	return ts.snapshot(), true
}

// CancelTask 取消任务（并结束挂在它控制台上的命令）。
func (e *Engine) CancelTask(id string) error {
	e.mu.Lock()
	ts := e.tasks[id]
	e.mu.Unlock()
	if ts == nil {
		return ErrNotFound
	}
	ts.cancel()
	e.killExecs(id)
	return nil
}

// WaitTask 等待任务结束。
func (e *Engine) WaitTask(ctx context.Context, id string) (model.Task, error) {
	e.mu.Lock()
	ts := e.tasks[id]
	e.mu.Unlock()
	if ts == nil {
		return model.Task{}, ErrNotFound
	}
	select {
	case <-ts.finished:
		return ts.snapshot(), nil
	case <-ctx.Done():
		return ts.snapshot(), ctx.Err()
	}
}

// Console 返回运行/任务的控制台。
func (e *Engine) Console(id string) *console.Console {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.consoles[id]
}

func (e *Engine) consoleSystem(id, text string) {
	if c := e.Console(id); c != nil {
		c.System(text)
	}
}

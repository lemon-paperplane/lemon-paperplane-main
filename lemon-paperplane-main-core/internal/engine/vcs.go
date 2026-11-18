package engine

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// 需要建任务（输出到任务控制台）的远程/长操作。
var vcsLongOps = map[string]bool{
	sdk.VCSOpPush: true, sdk.VCSOpPull: true, sdk.VCSOpFetch: true, sdk.VCSOpClone: true,
	sdk.VCSOpSubmoduleUpdate: true, sdk.VCSOpMergeTool: true, sdk.VCSOpTagPush: true,
}

// 只读操作（不需要刷新状态）。
var vcsReadOps = map[string]bool{
	sdk.VCSOpDetect: true, sdk.VCSOpStatus: true, sdk.VCSOpDiff: true, sdk.VCSOpBranches: true, sdk.VCSOpLog: true,
	sdk.VCSOpShow: true, sdk.VCSOpStashList: true, sdk.VCSOpTags: true, sdk.VCSOpRemotes: true, sdk.VCSOpFileHistory: true,
	sdk.VCSOpCompare: true, sdk.VCSOpPushPreview: true, sdk.VCSOpBlame: true, sdk.VCSOpSubmodules: true,
	sdk.VCSOpWorktrees: true, sdk.VCSOpConfigGet: true, sdk.VCSOpRepos: true,
	sdk.VCSOpFileContent: true, sdk.VCSOpContainingBranches: true, sdk.VCSOpMergeTools: true, sdk.VCSOpStashShow: true,
}

// VCSReadOnly 判断操作是否只读（MCP 注解用）。
func VCSReadOnly(op string) bool { return vcsReadOps[op] }

// VCSDestructive 判断操作是否破坏性。
func VCSDestructive(op string) bool {
	switch op {
	case sdk.VCSOpDiscard, sdk.VCSOpDiscardHunks, sdk.VCSOpReset, sdk.VCSOpClean, sdk.VCSOpBranchDelete, sdk.VCSOpStashDrop, sdk.VCSOpRemoteRemove, sdk.VCSOpTagDelete, sdk.VCSOpWorktreeRemove:
		return true
	}
	return false
}

func (e *Engine) vcsPlugins() []*plugins.Plugin {
	var out []*plugins.Plugin
	for _, p := range e.Plugins.Active() {
		if p.Manifest.Contributes.VCS != nil {
			out = append(out, p)
		}
	}
	return out
}

// findVCSRoots 在工程根、其祖先和子目录（3 层）中找仓库。
func (e *Engine) findVCSRoots(root string) []model.VCSRoot {
	var out []model.VCSRoot
	seen := map[string]bool{}
	add := func(dir string, p *plugins.Plugin) {
		if seen[dir] {
			return
		}
		seen[dir] = true
		rel, _ := filepath.Rel(root, dir)
		out = append(out, model.VCSRoot{Path: dir, RelPath: filepath.ToSlash(rel), VCS: p.Manifest.Contributes.VCS.ID, Plugin: p.ID()})
	}
	for _, p := range e.vcsPlugins() {
		marker := p.Manifest.Contributes.VCS.RootMarker
		has := func(d string) bool { _, err := os.Stat(filepath.Join(d, marker)); return err == nil }
		// 祖先（工程是某个仓库的子目录）
		found := false
		for d := root; ; {
			if has(d) {
				add(d, p)
				found = true
				break
			}
			parent := filepath.Dir(d)
			if parent == d {
				break
			}
			d = parent
		}
		_ = found
		// 子目录中的独立仓库
		var walk func(dir string, depth int)
		walk = func(dir string, depth int) {
			if depth > 3 {
				return
			}
			for _, s := range subdirsOf(dir) {
				base := filepath.Base(s)
				if sdk.IsGeneratedDir(dir, base) {
					continue
				}
				if has(s) {
					add(s, p)
				}
				walk(s, depth+1)
			}
		}
		walk(root, 1)
	}
	return out
}

func (e *Engine) resolveRoot(p *model.Project, root string) (*model.VCSRoot, *plugins.Plugin, error) {
	if len(p.VCSRoots) == 0 {
		return nil, nil, Errf("err.noRepo")
	}
	r := &p.VCSRoots[0]
	if root != "" {
		r = nil
		for i := range p.VCSRoots {
			if p.VCSRoots[i].Path == root || p.VCSRoots[i].RelPath == root {
				r = &p.VCSRoots[i]
			}
		}
		if r == nil {
			return nil, nil, ErrNotFound
		}
	}
	plug := e.Plugins.ActiveGet(r.Plugin)
	if plug == nil {
		return nil, nil, Errf("err.pluginUnavailable", r.Plugin)
	}
	return r, plug, nil
}

// VCSRoots 返回工程的仓库（重新扫描）。
func (e *Engine) VCSRoots(projectID string) ([]model.VCSRoot, error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return nil, ErrNotFound
	}
	roots := e.findVCSRoots(p.Path)
	_, _ = e.Store.UpdateProject(projectID, func(pp *model.Project) error {
		pp.VCSRoots = roots
		return nil
	})
	return roots, nil
}

// VCSResult 是长操作的返回：结果 + 任务。
type VCSResult struct {
	Result json.RawMessage `json:"result,omitempty"`
	Task   *model.Task     `json:"task,omitempty"`
}

// VCSCall 调用 VCS 操作。长操作同步等待结果，同时生成任务（界面可看输出）。
func (e *Engine) VCSCall(ctx context.Context, projectID, root, op string, args json.RawMessage) (json.RawMessage, error) {
	var p *model.Project
	var plug *plugins.Plugin
	var rootPath string
	if op == sdk.VCSOpClone {
		ps := e.vcsPlugins()
		if len(ps) == 0 {
			return nil, Errf("err.pluginUnavailable", "git")
		}
		plug = ps[0]
	} else if op == sdk.VCSOpInit {
		// 初始化：工程还没有仓库，直接在工程目录（或指定子目录）建，完成后重新扫描仓库
		ps := e.vcsPlugins()
		if len(ps) == 0 {
			return nil, Errf("err.pluginUnavailable", "git")
		}
		var ok bool
		p, ok = e.Store.Project(projectID)
		if !ok {
			return nil, ErrNotFound
		}
		plug, rootPath = ps[0], p.Path
		if root != "" {
			if filepath.IsAbs(root) {
				rootPath = root
			} else {
				rootPath = filepath.Join(p.Path, root)
			}
		}
		var res json.RawMessage
		err := e.Plugins.Call(ctx, plug, sdk.MethodVCS, &sdk.VCSCallParams{Op: op, Root: rootPath, Args: json.RawMessage("{}")}, &res)
		if err == nil {
			_, _ = e.VCSRoots(p.ID)
			e.afterVCSWrite(p.ID, rootPath)
		}
		return res, err
	} else {
		var ok bool
		p, ok = e.Store.Project(projectID)
		if !ok {
			return nil, ErrNotFound
		}
		r, pl, err := e.resolveRoot(p, root)
		if err != nil {
			return nil, err
		}
		plug, rootPath = pl, r.Path
	}
	if len(args) == 0 {
		args = json.RawMessage("{}")
	}
	call := func(ctx context.Context, taskID string) (json.RawMessage, error) {
		var res json.RawMessage
		err := e.Plugins.Call(ctx, plug, sdk.MethodVCS, &sdk.VCSCallParams{Op: op, Root: rootPath, Args: args, TaskID: taskID}, &res)
		return res, err
	}
	if !vcsLongOps[op] {
		res, err := call(ctx, "")
		if err == nil && !vcsReadOps[op] && p != nil {
			e.afterVCSWrite(p.ID, rootPath)
		}
		return res, err
	}
	title := i18n.T("task.vcs."+op, filepath.Base(firstNonEmptyStr(rootPath, "")))
	resCh := make(chan json.RawMessage, 1)
	errCh := make(chan error, 1)
	pid := ""
	if p != nil {
		pid = p.ID
	}
	t := e.NewTask("vcs", title, pid, "", func(tctx context.Context, ts *taskState) (any, error) {
		res, err := call(tctx, ts.t.ID)
		resCh <- res
		errCh <- err
		if err != nil {
			return nil, err
		}
		var r sdk.VCSOpResult
		if json.Unmarshal(res, &r) == nil && r.Message != "" {
			ts.console.System(r.Message)
		}
		return res, nil
	})
	select {
	case res := <-resCh:
		err := <-errCh
		if p != nil {
			e.afterVCSWrite(p.ID, rootPath)
		}
		return wrapTask(res, t), err
	case <-ctx.Done():
		// 调用方断开：任务继续在后台执行
		return wrapTask(nil, t), nil
	}
}

func wrapTask(res json.RawMessage, t model.Task) json.RawMessage {
	var m map[string]any
	if len(res) > 0 && json.Unmarshal(res, &m) == nil && m != nil {
		m["taskId"] = t.ID
		b, _ := json.Marshal(m)
		return b
	}
	if len(res) > 0 {
		return res
	}
	b, _ := json.Marshal(map[string]any{"taskId": t.ID})
	return b
}

func (e *Engine) afterVCSWrite(projectID, root string) {
	e.Bus.Publish(model.EvVCS, map[string]string{"projectId": projectID, "root": root})
	go e.refreshVCSBrief(context.Background(), projectID)
}

// refreshVCSBrief 刷新卡片上的 Git 摘要（主仓库）。
func (e *Engine) refreshVCSBrief(ctx context.Context, projectID string) {
	p, ok := e.Store.Project(projectID)
	if !ok || len(p.VCSRoots) == 0 {
		return
	}
	_, plug, err := e.resolveRoot(p, "")
	if err != nil {
		return
	}
	cctx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	var st sdk.VCSStatus
	if err := e.Plugins.Call(cctx, plug, sdk.MethodVCS, &sdk.VCSCallParams{Op: sdk.VCSOpStatus, Root: p.VCSRoots[0].Path, Args: json.RawMessage(`{"numstat":false}`)}, &st); err != nil {
		return
	}
	b := &model.VCSBrief{Branch: st.Branch, Ahead: st.Ahead, Behind: st.Behind, Changes: len(st.Files), Conflicts: st.Conflicts, Operation: st.Operation, Roots: len(p.VCSRoots), At: time.Now()}
	if st.Detached && b.Branch == "" {
		b.Branch = st.Head
	}
	e.mu.Lock()
	old := e.vcsBrief[projectID]
	e.vcsBrief[projectID] = b
	e.mu.Unlock()
	if old == nil || old.Branch != b.Branch || old.Ahead != b.Ahead || old.Behind != b.Behind || old.Changes != b.Changes || old.Operation != b.Operation {
		e.publishProject(projectID)
	}
}

// vcsLoop 定期刷新 Git 摘要（有界面连着时每 30 秒），可选自动 fetch。
func (e *Engine) vcsLoop() {
	t := time.NewTicker(30 * time.Second)
	defer t.Stop()
	lastFetch := time.Now()
	for {
		select {
		case <-e.stopCh:
			return
		case <-t.C:
		}
		if e.Bus.Subscribers() == 0 {
			continue
		}
		e.checkStale()
		for _, p := range e.Store.Projects("") {
			e.refreshVCSBrief(context.Background(), p.ID)
		}
		mins := e.Store.Settings().GitAutoFetchMinutes
		if mins > 0 && time.Since(lastFetch) > time.Duration(mins)*time.Minute {
			lastFetch = time.Now()
			for _, p := range e.Store.Projects("") {
				if len(p.VCSRoots) > 0 {
					go func(id string) {
						ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
						defer cancel()
						_, _ = e.VCSCall(ctx, id, "", sdk.VCSOpFetch, json.RawMessage(`{"prune":true}`))
					}(p.ID)
				}
			}
		}
	}
}

// CloneAndImport 克隆仓库并导入。
func (e *Engine) CloneAndImport(ctx context.Context, url, dest, branch, workspaceID string) (*model.Project, error) {
	args, _ := json.Marshal(sdk.VCSCloneArgs{URL: url, Dest: dest, Branch: branch})
	if _, err := e.VCSCall(ctx, "", "", sdk.VCSOpClone, args); err != nil {
		return nil, err
	}
	if !sdk.DirExists(dest) {
		return nil, Errf("err.cloneFailed")
	}
	return e.Import(ctx, dest, workspaceID, "")
}

var _ = strings.TrimSpace

// VCSRepoOverview 是工作区 Git 总览里的一个仓库。
type VCSRepoOverview struct {
	ProjectID   string          `json:"projectId"`
	ProjectName string          `json:"projectName"`
	Root        string          `json:"root"`    // 仓库绝对路径（调 vcs.* 时作为 root 传）
	RelPath     string          `json:"relPath"` // 相对工程根，主仓库为 "" 或 "."，工程在仓库子目录时为 ".."
	Name        string          `json:"name"`    // 显示名：仓库目录名
	VCS         string          `json:"vcs"`
	Status      json.RawMessage `json:"status,omitempty"` // VCSStatus（插件返回的超集原样透传）
	Error       string          `json:"error,omitempty"`
}

// VCSOverview 列出工作区内所有工程的所有仓库及其状态（像 VS Code 打开文件夹后的多仓库源代码管理）。
// workspaceID 为空表示全部工作区。同一个仓库被多个工程包含时只出现一次。
func (e *Engine) VCSOverview(ctx context.Context, workspaceID string) []VCSRepoOverview {
	var out []VCSRepoOverview
	seen := map[string]bool{}
	for _, p := range e.Store.Projects(workspaceID) {
		roots := e.findVCSRoots(p.Path)
		if len(roots) > 0 && !equalJSON(roots, p.VCSRoots) {
			pid := p.ID
			_, _ = e.Store.UpdateProject(pid, func(pp *model.Project) error { pp.VCSRoots = roots; return nil })
		}
		for _, r := range roots {
			key := filepath.Clean(r.Path)
			if seen[key] {
				continue
			}
			seen[key] = true
			out = append(out, VCSRepoOverview{ProjectID: p.ID, ProjectName: p.Name, Root: r.Path, RelPath: r.RelPath, Name: filepath.Base(r.Path), VCS: r.VCS})
		}
	}
	sem := make(chan struct{}, 6)
	done := make(chan struct{})
	for i := range out {
		go func(i int) {
			sem <- struct{}{}
			defer func() { <-sem; done <- struct{}{} }()
			o := &out[i]
			plug := e.Plugins.ActiveGet(e.vcsPluginFor(o.VCS))
			if plug == nil {
				o.Error = i18n.T("err.pluginUnavailable", o.VCS)
				return
			}
			cctx, cancel := context.WithTimeout(ctx, 20*time.Second)
			defer cancel()
			var st json.RawMessage
			if err := e.Plugins.Call(cctx, plug, sdk.MethodVCS, &sdk.VCSCallParams{Op: sdk.VCSOpStatus, Root: o.Root, Args: json.RawMessage(`{"numstat":false}`)}, &st); err != nil {
				o.Error = errString(err)
				return
			}
			o.Status = st
		}(i)
	}
	for range out {
		<-done
	}
	if out == nil {
		out = []VCSRepoOverview{}
	}
	return out
}

func (e *Engine) vcsPluginFor(vcs string) string {
	for _, p := range e.vcsPlugins() {
		if p.Manifest.Contributes.VCS.ID == vcs {
			return p.ID()
		}
	}
	return ""
}

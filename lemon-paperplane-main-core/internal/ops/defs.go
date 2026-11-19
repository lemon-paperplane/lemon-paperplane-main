package ops

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/console"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/engine"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/schema"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

type empty struct{}

type projectIn struct {
	ProjectID string `json:"projectId" required:"true" desc:"Project id (from project_list)."`
}

type wsIn struct {
	WorkspaceID string `json:"workspaceId" required:"true" desc:"Workspace id (from workspace_list)."`
}

func ok() (any, error) { return map[string]bool{"ok": true}, nil }

func (r *Registry) registerAll() {
	e := r.e

	// ---------------------------------------------------------------- 系统
	def(r, "system.info", "System info", "Return daemon version, data directory, API/MCP addresses, memory usage and plugin process count.", "r",
		func(ctx context.Context, _ *empty) (any, error) {
			return e.SystemInfo(r.Extras.PID, i18n.Locale(), r.Extras.MCPURL), nil
		})
	def(r, "system.shutdown", "Shutdown daemon", "Stop the daemon. stopRuns=true also stops every running project.", "di",
		func(ctx context.Context, in *struct {
			StopRuns bool `json:"stopRuns"`
		}) (any, error) {
			if r.Extras.Shutdown != nil {
				go r.Extras.Shutdown(in.StopRuns)
			}
			return ok()
		})
	def(r, "settings.get", "Get settings", "Return global settings (language, theme, mirror region, auto install options, plugin settings).", "r",
		func(ctx context.Context, _ *empty) (any, error) { return e.Store.Settings(), nil })
	// 输入是 JSON 合并补丁；schema 取设置模型（全部可选），MCP 客户端要求 inputSchema 为 object
	settingsSchema := schema.Of(model.Settings{})
	delete(settingsSchema, "required")
	def(r, "settings.update", "Update settings", "Merge the given fields into global settings (only the fields you pass change, nested objects such as editor included). Common fields: locale (system|zh-Hans|en), theme, mirrorRegion (auto|china|global), autoInstallToolchains, autoInstallDependencies, pluginIdleSeconds, logLinesPerRun, storeIndexUrl, gitAutoFetchMinutes, terminal, editor.", "",
		func(ctx context.Context, in *json.RawMessage) (any, error) { return e.UpdateSettings(*in) }).InputSchema = settingsSchema
	def(r, "mirrors.status", "Mirror status", "Show configured and effective mirror region (china uses domestic mirrors for npm/Maven/PyPI/Go/JDK/Node/Gradle) and latency probes.", "r",
		func(ctx context.Context, _ *empty) (any, error) { return e.MirrorStatus(ctx, false), nil })
	def(r, "mirrors.probe", "Probe mirrors", "Re-run latency probes and re-decide the mirror region when it is auto.", "",
		func(ctx context.Context, _ *empty) (any, error) { return e.MirrorStatus(ctx, true), nil })
	def(r, "ui.contributions", "UI contributions", "All UI contributions from plugins (facets, run configuration types with forms, views, actions, settings), localized.", "ri",
		func(ctx context.Context, _ *empty) (any, error) { return e.Contributions(), nil })
	def(r, "ui.respond", "Answer prompt", "Answer a pending prompt dialog.", "i",
		func(ctx context.Context, in *struct {
			PromptID  string         `json:"promptId" required:"true"`
			Cancelled bool           `json:"cancelled"`
			Values    map[string]any `json:"values"`
		}) (any, error) {
			return nil, e.Respond(in.PromptID, sdk.PromptResult{Cancelled: in.Cancelled, Values: in.Values})
		})
	def(r, "ui.prompts", "Pending prompts", "Prompts waiting for an answer.", "ri",
		func(ctx context.Context, _ *empty) (any, error) { return e.PendingPrompts(), nil })
	def(r, "ui.ask", "Ask user", "Show a dialog and wait for the answer (used by askpass).", "i",
		func(ctx context.Context, in *sdk.PromptParams) (any, error) { return e.Ask(ctx, "", *in), nil })
	def(r, "mcp.config", "MCP config", "How to connect AI clients to this daemon over MCP.", "ri",
		func(ctx context.Context, _ *empty) (any, error) {
			exe := e.Executable()
			return map[string]any{
				"url":           r.Extras.MCPURL,
				"token":         tokenOf(e),
				"stdioCommand":  []string{exe, "mcp"},
				"claudeCode":    "claude mcp add paperplane -- \"" + exe + "\" mcp",
				"claudeDesktop": map[string]any{"mcpServers": map[string]any{"paperplane": map[string]any{"command": exe, "args": []string{"mcp"}}}},
			}, nil
		})
	def(r, "cli.install", "Install CLI", "Link the paperplane command into a directory on PATH (default ~/.local/bin, or /usr/local/bin when writable). On Windows: add the installation folder to the user PATH.", "i",
		func(ctx context.Context, in *struct {
			Dir string `json:"dir"`
		}) (any, error) {
			return installCLI(e.Executable(), in.Dir)
		})

	// ---------------------------------------------------------------- 工作区
	def(r, "workspace.list", "List workspaces", "List workspaces with their project ids. Workspaces group projects by team or company.", "r",
		func(ctx context.Context, _ *empty) (any, error) { return e.Workspaces(), nil })
	def(r, "workspace.create", "Create workspace", "Create a workspace.", "",
		func(ctx context.Context, in *struct {
			Name string `json:"name" required:"true"`
		}) (any, error) {
			w := e.Store.CreateWorkspace(in.Name)
			e.Bus.Publish(model.EvWorkspaces, e.Workspaces())
			return w, nil
		})
	def(r, "workspace.rename", "Rename workspace", "Rename a workspace.", "",
		func(ctx context.Context, in *struct {
			WorkspaceID string `json:"workspaceId" required:"true"`
			Name        string `json:"name" required:"true"`
		}) (any, error) {
			w, err := e.Store.RenameWorkspace(in.WorkspaceID, in.Name)
			e.Bus.Publish(model.EvWorkspaces, e.Workspaces())
			return w, err
		})
	def(r, "workspace.delete", "Delete workspace", "Delete a workspace. Projects are moved to moveTo when given, otherwise removed from Paperplane (files on disk are never deleted).", "d",
		func(ctx context.Context, in *struct {
			WorkspaceID string `json:"workspaceId" required:"true"`
			MoveTo      string `json:"moveTo"`
		}) (any, error) {
			removed, err := e.Store.DeleteWorkspace(in.WorkspaceID, in.MoveTo)
			if err != nil {
				return nil, err
			}
			for _, id := range removed {
				e.StopAll("", id)
				e.Bus.Publish(model.EvProjectDel, map[string]string{"id": id})
			}
			e.Bus.Publish(model.EvWorkspaces, e.Workspaces())
			if in.MoveTo != "" {
				if w, ok := e.Store.Workspace(in.MoveTo); ok {
					for _, id := range w.ProjectIDs {
						e.PublishProject(id)
					}
				}
			}
			return ok()
		})
	def(r, "workspace.reorder", "Reorder workspaces", "Set the order of workspaces.", "i",
		func(ctx context.Context, in *struct {
			WorkspaceIDs []string `json:"workspaceIds" required:"true"`
		}) (any, error) {
			e.Store.ReorderWorkspaces(in.WorkspaceIDs)
			e.Bus.Publish(model.EvWorkspaces, e.Workspaces())
			return ok()
		})

	// ---------------------------------------------------------------- 工程
	def(r, "project.list", "List projects", "List projects (cards) with detected technologies, run status, URLs, git branch and problems. Omit workspaceId to list all.", "r",
		func(ctx context.Context, in *struct {
			WorkspaceID string `json:"workspaceId"`
		}) (any, error) {
			return e.ProjectSummaries(in.WorkspaceID), nil
		})
	def(r, "project.get", "Get project", "Full project detail: modules, facets, analysis info, selected toolchains, run configurations, dependency state, VCS roots.", "r",
		func(ctx context.Context, in *projectIn) (any, error) {
			p, ok := e.Store.Project(in.ProjectID)
			if !ok {
				return nil, engine.ErrNotFound
			}
			return p, nil
		})
	def(r, "project.scan", "Scan folder", "Find candidate projects under a folder (git repositories or folders recognized by plugins) for batch import.", "r",
		func(ctx context.Context, in *struct {
			Path     string `json:"path" required:"true" desc:"Absolute folder path."`
			MaxDepth int    `json:"maxDepth" desc:"Default 2."`
		}) (any, error) {
			return e.ScanFolder(ctx, in.Path, in.MaxDepth)
		})
	def(r, "project.import", "Import project", "Import a folder as a project. Detection and analysis run immediately (languages, build tools, frameworks, required toolchains, run configurations); heavy sync (e.g. Gradle) continues in a background task.", "",
		func(ctx context.Context, in *struct {
			Path        string `json:"path" required:"true" desc:"Absolute folder path."`
			WorkspaceID string `json:"workspaceId" desc:"Default: first workspace."`
			Name        string `json:"name"`
		}) (any, error) {
			return e.Import(ctx, in.Path, in.WorkspaceID, in.Name)
		})
	def(r, "project.importMany", "Import projects", "Import several folders.", "",
		func(ctx context.Context, in *struct {
			Paths       []string `json:"paths" required:"true"`
			WorkspaceID string   `json:"workspaceId"`
		}) (any, error) {
			var out []*model.Project
			for _, p := range in.Paths {
				pr, err := e.Import(ctx, p, in.WorkspaceID, "")
				if err != nil {
					return out, err
				}
				out = append(out, pr)
			}
			return out, nil
		})
	def(r, "project.clone", "Clone and import", "Clone a git repository into dest and import it.", "",
		func(ctx context.Context, in *struct {
			URL         string `json:"url" required:"true"`
			Dest        string `json:"dest" required:"true" desc:"Absolute target folder (must not exist or be empty)."`
			Branch      string `json:"branch"`
			WorkspaceID string `json:"workspaceId"`
		}) (any, error) {
			return e.CloneAndImport(ctx, in.URL, in.Dest, in.Branch, in.WorkspaceID)
		})
	def(r, "project.remove", "Remove project", "Remove a project from Paperplane and stop its runs. Files on disk are not touched.", "d",
		func(ctx context.Context, in *projectIn) (any, error) {
			e.StopAll("", in.ProjectID)
			if err := e.Store.RemoveProject(in.ProjectID); err != nil {
				return nil, err
			}
			e.Bus.Publish(model.EvProjectDel, map[string]string{"id": in.ProjectID})
			e.Bus.Publish(model.EvWorkspaces, e.Workspaces())
			return ok()
		})
	def(r, "project.rename", "Rename project", "Change the display name of a project.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			Name      string `json:"name" required:"true"`
		}) (any, error) {
			p, err := e.Store.UpdateProject(in.ProjectID, func(p *model.Project) error { p.Name = in.Name; return nil })
			if err != nil {
				return nil, err
			}
			s := e.Summary(p)
			e.Bus.Publish(model.EvProject, s)
			return s, nil
		})
	def(r, "project.move", "Move project", "Move a project to another workspace.", "",
		func(ctx context.Context, in *struct {
			ProjectID   string `json:"projectId" required:"true"`
			WorkspaceID string `json:"workspaceId" required:"true"`
			Index       *int   `json:"index"`
		}) (any, error) {
			idx := -1
			if in.Index != nil {
				idx = *in.Index
			}
			if err := e.Store.MoveProject(in.ProjectID, in.WorkspaceID, idx); err != nil {
				return nil, err
			}
			e.Bus.Publish(model.EvWorkspaces, e.Workspaces())
			e.PublishProject(in.ProjectID) // 摘要里的 workspaceId 变了
			return ok()
		})
	def(r, "project.reorder", "Reorder projects", "Set project order inside a workspace.", "i",
		func(ctx context.Context, in *struct {
			WorkspaceID string   `json:"workspaceId" required:"true"`
			ProjectIDs  []string `json:"projectIds" required:"true"`
		}) (any, error) {
			if err := e.Store.ReorderProjects(in.WorkspaceID, in.ProjectIDs); err != nil {
				return nil, err
			}
			e.Bus.Publish(model.EvWorkspaces, e.Workspaces())
			return ok()
		})
	def(r, "project.reimport", "Reimport project", "Re-detect modules and re-analyze (after files changed). Returns a task.", "",
		func(ctx context.Context, in *projectIn) (any, error) { return e.Reimport(in.ProjectID) })
	def(r, "project.sync", "Sync project", "Run the heavy build-tool sync (Gradle model import, Maven reactor resolution). Returns a task.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ModuleID  string `json:"moduleId"`
		}) (any, error) {
			return e.Sync(in.ProjectID, in.ModuleID)
		})
	def(r, "project.settings", "Project settings", "Set project environment variables (replaces the whole map when given) and per-kind toolchain overrides, e.g. {\"toolchains\":{\"jdk\":{\"constraint\":\"17\"}}} or {\"toolchains\":{\"node\":null}} to clear. Missing toolchains are downloaded automatically.", "",
		func(ctx context.Context, in *struct {
			ProjectID  string                              `json:"projectId" required:"true"`
			Env        map[string]string                   `json:"env"`
			Toolchains map[string]*model.ToolchainOverride `json:"toolchains"`
		}) (any, error) {
			return e.UpdateProjectSettings(ctx, in.ProjectID, in.Env, in.Toolchains)
		})
	def(r, "project.open", "Open project", "Open the project folder in Finder/Explorer, a terminal, IntelliJ IDEA or VS Code.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ModuleID  string `json:"moduleId"`
			With      string `json:"with" enum:"finder|terminal|idea|vscode|default"`
		}) (any, error) {
			return nil, e.OpenProject(in.ProjectID, in.ModuleID, in.With)
		})
	def(r, "project.problems", "Project problems", "Static problems, missing toolchains and missing dependencies, each with suggested fixes (apply with fix_apply using projectId + diagnosisId).", "r",
		func(ctx context.Context, in *projectIn) (any, error) {
			ps := e.ProjectProblems(in.ProjectID)
			for i := range ps {
				ps[i].Line = ""
			}
			if ps == nil {
				ps = []sdk.Diagnosis{}
			}
			return ps, nil
		})
	def(r, "project.action", "Run plugin action", "Invoke a plugin action on a module (see ui_contributions actions). Returns a task.", "",
		func(ctx context.Context, in *struct {
			ProjectID string         `json:"projectId" required:"true"`
			ModuleID  string         `json:"moduleId"`
			Plugin    string         `json:"plugin" required:"true"`
			Action    string         `json:"action" required:"true"`
			Args      map[string]any `json:"args"`
			RunID     string         `json:"runId" desc:"For actions on the run toolbar: the run they apply to."`
		}) (any, error) {
			return e.InvokeAction(in.ProjectID, in.ModuleID, in.Plugin, in.Action, in.Args, in.RunID)
		})
	def(r, "deps.install", "Install dependencies", "Install project dependencies with the detected package manager (npm/yarn/pnpm/uv/pip/poetry/go mod...). Lock files are never modified. Returns a task.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ModuleID  string `json:"moduleId"`
			Clean     bool   `json:"clean" desc:"Remove installed dependencies first."`
		}) (any, error) {
			return e.InstallDeps(in.ProjectID, in.ModuleID, in.Clean)
		})

	// ---------------------------------------------------------------- 运行配置
	def(r, "runconfig.types", "Run configuration types", "Run configuration types available for a module, with option forms.", "r",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ModuleID  string `json:"moduleId" required:"true"`
		}) (any, error) {
			return e.RunConfigTypes(in.ProjectID, in.ModuleID)
		})
	def(r, "runconfig.create", "Create run configuration", "Create a user run configuration.", "",
		func(ctx context.Context, in *struct {
			ProjectID string            `json:"projectId" required:"true"`
			ModuleID  string            `json:"moduleId" required:"true"`
			Type      string            `json:"type" required:"true"`
			Name      string            `json:"name" required:"true"`
			Options   map[string]any    `json:"options"`
			Env       map[string]string `json:"env"`
		}) (any, error) {
			return e.CreateRunConfig(in.ProjectID, in.ModuleID, in.Type, in.Name, in.Options, in.Env)
		})
	def(r, "runconfig.update", "Update run configuration", "Change a run configuration: options are merged (null removes a key), env replaces the map. Edited configurations are no longer overwritten by re-import.", "",
		func(ctx context.Context, in *struct {
			ProjectID string            `json:"projectId" required:"true"`
			ConfigID  string            `json:"configId" required:"true"`
			Name      *string           `json:"name"`
			Options   map[string]any    `json:"options"`
			Env       map[string]string `json:"env"`
		}) (any, error) {
			return e.UpdateRunConfig(in.ProjectID, in.ConfigID, in.Name, in.Options, in.Env)
		})
	def(r, "runconfig.delete", "Delete run configuration", "Delete a run configuration.", "d",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ConfigID  string `json:"configId" required:"true"`
		}) (any, error) {
			return nil, e.DeleteRunConfig(in.ProjectID, in.ConfigID)
		})
	def(r, "runconfig.reset", "Reset run configuration", "Discard user edits and restore the plugin-generated configuration.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ConfigID  string `json:"configId" required:"true"`
		}) (any, error) {
			return e.ResetRunConfig(ctx, in.ProjectID, in.ConfigID)
		})
	def(r, "runconfig.options", "Live form options", "Fetch dropdown options that change at any time (fields marked live, e.g. connected devices for iOS/Android run configurations). keys are optionsFrom names such as ios.devices or android.devices.", "r",
		func(ctx context.Context, in *struct {
			ProjectID string   `json:"projectId" required:"true"`
			ModuleID  string   `json:"moduleId"`
			Keys      []string `json:"keys" required:"true"`
		}) (any, error) {
			return e.LiveOptions(ctx, in.ProjectID, in.ModuleID, in.Keys)
		})
	def(r, "runconfig.setDefault", "Set default run configuration", "Make a configuration the project's default (used by run_start without configId).", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ConfigID  string `json:"configId" required:"true"`
		}) (any, error) {
			return nil, e.SetDefaultConfig(in.ProjectID, in.ConfigID)
		})

	// ---------------------------------------------------------------- 运行
	def(r, "run.start", "Start", "Start a project with a run configuration (default configuration when configId is omitted). Missing toolchains and dependencies are installed first, then the app is launched and supervised. Returns the run; follow it with run_get / run_logs.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ConfigID  string `json:"configId"`
			Wait      int    `json:"wait" desc:"Optional seconds to wait until the run is running/failed/exited before returning (max 300)."`
		}) (any, error) {
			run, err := e.StartRun(in.ProjectID, in.ConfigID)
			if err != nil || in.Wait <= 0 {
				return run, err
			}
			return waitRun(ctx, e, run.ID, in.Wait), nil
		})
	def(r, "run.stop", "Stop", "Stop a run (graceful signal to the whole process tree, force kill after timeout).", "",
		func(ctx context.Context, in *struct {
			RunID string `json:"runId" required:"true"`
		}) (any, error) {
			return e.StopRun(in.RunID)
		})
	def(r, "run.restart", "Restart", "Restart a run (by runId, or by projectId + configId).", "",
		func(ctx context.Context, in *struct {
			RunID     string `json:"runId"`
			ProjectID string `json:"projectId"`
			ConfigID  string `json:"configId"`
		}) (any, error) {
			return e.RestartRun(in.RunID, in.ProjectID, in.ConfigID)
		})
	def(r, "run.stopAll", "Stop all", "Stop every run, or those in a workspace or project.", "",
		func(ctx context.Context, in *struct {
			WorkspaceID string `json:"workspaceId"`
			ProjectID   string `json:"projectId"`
		}) (any, error) {
			return map[string]int{"stopped": e.StopAll(in.WorkspaceID, in.ProjectID)}, nil
		})
	// ---------------------------------------------------------------- 启动面板
	def(r, "launch.list", "List launch board", "The launch board: run configurations the user picked to start together, grouped by project, with the latest run of each (state, URLs, CPU/memory). Filter by workspaceId and/or projectId.", "r",
		func(ctx context.Context, in *struct {
			WorkspaceID string `json:"workspaceId"`
			ProjectID   string `json:"projectId"`
		}) (any, error) {
			return e.LaunchList(in.WorkspaceID, in.ProjectID), nil
		})
	def(r, "launch.add", "Add to launch board", "Add run configurations of a project to the launch board (kept in the order added). Returns the project's launch list.", "",
		func(ctx context.Context, in *struct {
			ProjectID string   `json:"projectId" required:"true"`
			ConfigIDs []string `json:"configIds" required:"true"`
		}) (any, error) {
			ids, err := e.LaunchAdd(in.ProjectID, in.ConfigIDs)
			return map[string]any{"launch": nonNilStrings(ids)}, err
		})
	def(r, "launch.remove", "Remove from launch board", "Remove run configurations of a project from the launch board. Returns the project's launch list.", "",
		func(ctx context.Context, in *struct {
			ProjectID string   `json:"projectId" required:"true"`
			ConfigIDs []string `json:"configIds" required:"true"`
		}) (any, error) {
			ids, err := e.LaunchRemove(in.ProjectID, in.ConfigIDs)
			return map[string]any{"launch": nonNilStrings(ids)}, err
		})
	def(r, "launch.addFile", "Add script to launch board", "Add a script or executable file of a project to the launch board: creates a 'Script / Executable' run configuration for it (reused if one already exists for the same file). The interpreter is chosen automatically (#! line, execute permission, or file extension).", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			Path      string `json:"path" required:"true" desc:"file path relative to the project root"`
		}) (any, error) {
			return e.LaunchAddFile(in.ProjectID, in.Path)
		})
	def(r, "launch.candidates", "List runnable files", "List files of a project that can be run as launch items: scripts (shell extensions or a #! line) and executables (execute permission plus a real executable format). With includeSources, also other source files such as .py/.js without #!. Documents, data and images are never listed, even with execute permission.", "r",
		func(ctx context.Context, in *struct {
			ProjectID      string `json:"projectId" required:"true"`
			IncludeSources bool   `json:"includeSources"`
			Limit          int    `json:"limit" desc:"max files, default 2000"`
		}) (any, error) {
			return e.LaunchCandidates(in.ProjectID, in.IncludeSources, in.Limit)
		})
	def(r, "launch.addCommand", "Add command to launch board", "Add a custom shell command (may span several lines) to the launch board: creates a 'Custom Command' run configuration that runs it in the user's default shell.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			Name      string `json:"name" desc:"display name; defaults to the first line of the command"`
			Command   string `json:"command" required:"true"`
			Cwd       string `json:"cwd" desc:"working directory, relative to the project root or absolute; empty for the project root"`
			Shell     string `json:"shell" desc:"auto (default shell), zsh, bash, sh, pwsh or cmd"`
		}) (any, error) {
			return e.LaunchAddCommand(in.ProjectID, in.Name, in.Command, in.Cwd, in.Shell)
		})
	def(r, "launch.setAlias", "Set launch item alias", "Give a launch-board item a display name (only shown on the launch board; the run configuration keeps its own name). An empty alias clears it.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			ConfigID  string `json:"configId" required:"true"`
			Alias     string `json:"alias"`
		}) (any, error) {
			return nil, e.LaunchSetAlias(in.ProjectID, in.ConfigID, in.Alias)
		})
	def(r, "launch.run", "Run launch board", "Start every launch-board item that is not running yet, optionally limited to a workspace, a project or some configIds. Returns the started runs and per-item errors.", "",
		func(ctx context.Context, in *struct {
			WorkspaceID string   `json:"workspaceId"`
			ProjectID   string   `json:"projectId"`
			ConfigIDs   []string `json:"configIds"`
		}) (any, error) {
			return e.LaunchRun(in.WorkspaceID, in.ProjectID, in.ConfigIDs), nil
		})
	def(r, "launch.stop", "Stop launch board", "Stop the running launch-board items, optionally limited to a workspace, a project or some configIds.", "",
		func(ctx context.Context, in *struct {
			WorkspaceID string   `json:"workspaceId"`
			ProjectID   string   `json:"projectId"`
			ConfigIDs   []string `json:"configIds"`
		}) (any, error) {
			return e.LaunchStop(in.WorkspaceID, in.ProjectID, in.ConfigIDs), nil
		})
	def(r, "terminal.list", "List terminals", "Interactive terminals (login shells in a pseudo terminal) kept by the daemon, in creation order. Filter by workspaceId. Output streams from GET /api/v1/terminals/<id>/stream?since=<offset>.", "r",
		func(ctx context.Context, in *struct {
			WorkspaceID string `json:"workspaceId"`
		}) (any, error) {
			return e.TerminalList(in.WorkspaceID), nil
		})
	def(r, "terminal.create", "Create terminal", "Open a new terminal. With projectId it starts in the project's root directory (or cwd relative to it), joins the project's workspace and is named after the project; the project's environment variables apply. Returns the terminal.", "",
		func(ctx context.Context, in *engine.TerminalCreateParams) (any, error) {
			return e.TerminalCreate(*in)
		})
	def(r, "terminal.input", "Type into terminal", "Send keystrokes or text to a terminal, exactly as typed (use \\r for Enter, \\u0003 for Ctrl-C).", "",
		func(ctx context.Context, in *struct {
			ID   string `json:"id" required:"true"`
			Data string `json:"data" required:"true"`
		}) (any, error) {
			return nil, e.TerminalInput(in.ID, in.Data)
		})
	def(r, "terminal.read", "Read terminal", "Recent output of a terminal as plain text (colors and cursor movement removed). maxBytes defaults to 16384.", "r",
		func(ctx context.Context, in *struct {
			ID       string `json:"id" required:"true"`
			MaxBytes int    `json:"maxBytes"`
		}) (any, error) {
			text, err := e.TerminalRead(in.ID, in.MaxBytes)
			return map[string]any{"text": text}, err
		})
	def(r, "terminal.resize", "Resize terminal", "Set the terminal size in columns and rows.", "i",
		func(ctx context.Context, in *struct {
			ID   string `json:"id" required:"true"`
			Cols int    `json:"cols" required:"true"`
			Rows int    `json:"rows" required:"true"`
		}) (any, error) {
			return nil, e.TerminalResize(in.ID, in.Cols, in.Rows)
		})
	def(r, "terminal.rename", "Rename terminal", "Rename a terminal.", "",
		func(ctx context.Context, in *struct {
			ID   string `json:"id" required:"true"`
			Name string `json:"name" required:"true"`
		}) (any, error) {
			return nil, e.TerminalRename(in.ID, in.Name)
		})
	def(r, "terminal.restart", "Restart terminal", "Start the shell again in a terminal whose process has exited (same directory; output continues below).", "",
		func(ctx context.Context, in *struct {
			ID string `json:"id" required:"true"`
		}) (any, error) {
			return e.TerminalRestart(in.ID)
		})
	def(r, "terminal.busy", "Terminal busy", "Whether a command is running in the foreground of a terminal (not the shell waiting for input), and its name. Check before closing.", "r",
		func(ctx context.Context, in *struct {
			ID string `json:"id" required:"true"`
		}) (any, error) {
			busy, name, err := e.TerminalBusy(in.ID)
			return map[string]any{"busy": busy, "process": name}, err
		})
	def(r, "terminal.close", "Close terminal", "Close a terminal: hang up its shell and everything started from it.", "d",
		func(ctx context.Context, in *struct {
			ID string `json:"id" required:"true"`
		}) (any, error) {
			return nil, e.TerminalClose(in.ID)
		})
	def(r, "run.list", "List runs", "List runs (active and recently finished) with state, URLs, ports, CPU/memory and diagnoses.", "r",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId"`
			Active    bool   `json:"active"`
		}) (any, error) {
			out := e.Runs(in.ProjectID, in.Active)
			if out == nil {
				out = []model.Run{}
			}
			return out, nil
		})
	def(r, "run.get", "Get run", "Get one run with its diagnoses (each diagnosis lists fixes that fix_apply can execute).", "r",
		func(ctx context.Context, in *struct {
			RunID string `json:"runId" required:"true"`
		}) (any, error) {
			run, ok := e.Run(in.RunID)
			if !ok {
				return nil, engine.ErrNotFound
			}
			return run, nil
		})
	def(r, "run.logs", "Read logs", "Read console output of a run or task (consoleId = runId or taskId). ANSI colors are stripped unless raw=true. Use grep to filter (regular expression, case-insensitive).", "r",
		func(ctx context.Context, in *struct {
			ConsoleID string `json:"consoleId" required:"true"`
			Tail      int    `json:"tail" desc:"Last N lines (default 200, max 5000)."`
			Since     int64  `json:"since" desc:"Only lines with seq greater than this."`
			Grep      string `json:"grep"`
			Stream    string `json:"stream" enum:"stdout|stderr|system"`
			Raw       bool   `json:"raw"`
		}) (any, error) {
			return readLogs(e, in.ConsoleID, in.Tail, in.Since, in.Grep, in.Stream, in.Raw)
		})
	def(r, "run.stats", "Resource usage", "CPU and memory of a run's whole process tree: the last 5 minutes sampled every 2 s, peak memory, uptime, listening ports, and each process in the tree.", "r",
		func(ctx context.Context, in *struct {
			RunID string `json:"runId" required:"true"`
		}) (any, error) {
			return e.RunStats(in.RunID)
		})
	def(r, "run.input", "Send input", "Write a line to the standard input of a running process.", "",
		func(ctx context.Context, in *struct {
			RunID string `json:"runId" required:"true"`
			Text  string `json:"text" required:"true"`
		}) (any, error) {
			return nil, e.RunInput(in.RunID, in.Text)
		})
	def(r, "run.clear", "Clear console", "Clear the in-memory console buffer of a run.", "i",
		func(ctx context.Context, in *struct {
			RunID string `json:"runId" required:"true"`
		}) (any, error) {
			if c := e.Console(in.RunID); c != nil {
				c.Clear()
			}
			return ok()
		})
	def(r, "fix.apply", "Apply fix", "Apply a suggested fix from a run diagnosis (runId + fixId [+ diagnosisId]) or from project_problems (projectId + diagnosisId + fixId). Fixes change Paperplane settings only (toolchain, env, run options), install dependencies, or kill a process holding a port; affected runs are restarted.", "",
		func(ctx context.Context, in *struct {
			RunID       string `json:"runId"`
			TaskID      string `json:"taskId" desc:"For diagnoses attached to a task (dependency install, sync)."`
			ProjectID   string `json:"projectId"`
			DiagnosisID string `json:"diagnosisId"`
			FixID       string `json:"fixId" required:"true"`
		}) (any, error) {
			id := in.RunID
			if id == "" {
				id = in.TaskID
			}
			return e.ApplyFix(ctx, id, in.ProjectID, in.DiagnosisID, in.FixID)
		})
	def(r, "port.kill", "Kill port", "Kill processes listening on a TCP port.", "d",
		func(ctx context.Context, in *struct {
			Port int `json:"port" required:"true"`
		}) (any, error) {
			return map[string]any{"pids": e.KillPort(in.Port)}, nil
		})

	// ---------------------------------------------------------------- 任务
	def(r, "task.list", "List tasks", "Background tasks (import, sync, dependency install, toolchain download, git push...).", "r",
		func(ctx context.Context, in *struct {
			Active bool `json:"active"`
		}) (any, error) {
			out := e.Tasks(in.Active)
			if out == nil {
				out = []model.Task{}
			}
			return out, nil
		})
	def(r, "task.get", "Get task", "Get a task. wait=N waits up to N seconds for it to finish.", "r",
		func(ctx context.Context, in *struct {
			TaskID string `json:"taskId" required:"true"`
			Wait   int    `json:"wait"`
		}) (any, error) {
			if in.Wait > 0 {
				w := in.Wait
				if w > 600 {
					w = 600
				}
				c, cancel := context.WithTimeout(ctx, time.Duration(w)*time.Second)
				defer cancel()
				t, err := e.WaitTask(c, in.TaskID)
				if err == engine.ErrNotFound {
					return nil, err
				}
				return t, nil
			}
			t, ok := e.Task(in.TaskID)
			if !ok {
				return nil, engine.ErrNotFound
			}
			return t, nil
		})
	def(r, "task.cancel", "Cancel task", "Cancel a running task.", "",
		func(ctx context.Context, in *struct {
			TaskID string `json:"taskId" required:"true"`
		}) (any, error) {
			return nil, e.CancelTask(in.TaskID)
		})

	// ---------------------------------------------------------------- 工具链
	def(r, "toolchain.kinds", "Toolchain kinds", "Toolchain kinds provided by plugins (jdk, node, python, go, gradle, maven...).", "r",
		func(ctx context.Context, _ *empty) (any, error) { return e.ToolchainKinds(), nil })
	def(r, "toolchain.list", "List toolchains", "Installed toolchains discovered on this machine (system, nvm, sdkman, IDE downloads, Homebrew, uv, conda...) and those managed by Paperplane.", "r",
		func(ctx context.Context, in *struct {
			Kind    string `json:"kind"`
			Refresh bool   `json:"refresh"`
		}) (any, error) {
			if in.Refresh {
				e.InvalidateToolchains()
			}
			out := e.AllToolchains(ctx, in.Kind)
			if out == nil {
				out = []sdk.Toolchain{}
			}
			return out, nil
		})
	def(r, "toolchain.available", "Available toolchains", "Versions that can be downloaded for a kind.", "r",
		func(ctx context.Context, in *struct {
			Kind string `json:"kind" required:"true"`
		}) (any, error) {
			return e.AvailableToolchains(ctx, in.Kind)
		})
	def(r, "toolchain.install", "Install toolchain", "Download and install a toolchain version (domestic mirrors are used in China). Returns a task.", "",
		func(ctx context.Context, in *struct {
			Kind    string `json:"kind" required:"true"`
			Version string `json:"version" required:"true" desc:"Exact version or major, e.g. 17, 22, 3.12, 1.23."`
		}) (any, error) {
			return e.InstallToolchain(in.Kind, in.Version)
		})
	def(r, "toolchain.add", "Add toolchain", "Register a toolchain installed in a custom location.", "",
		func(ctx context.Context, in *struct {
			Kind string `json:"kind" required:"true"`
			Path string `json:"path" required:"true"`
		}) (any, error) {
			return e.AddToolchain(ctx, in.Kind, in.Path)
		})
	def(r, "toolchain.remove", "Remove toolchain", "Delete a toolchain downloaded by Paperplane, or forget a manually added one.", "d",
		func(ctx context.Context, in *struct {
			ToolchainID string `json:"toolchainId" required:"true"`
		}) (any, error) {
			return nil, e.RemoveToolchain(in.ToolchainID)
		})
	def(r, "toolchain.refresh", "Refresh toolchains", "Re-discover installed toolchains.", "",
		func(ctx context.Context, _ *empty) (any, error) { e.InvalidateToolchains(); return ok() })

	// ---------------------------------------------------------------- 插件
	def(r, "plugin.list", "List plugins", "Installed plugins with version, source (bundled/user/dev), state and contributions.", "r",
		func(ctx context.Context, _ *empty) (any, error) { return e.PluginInfos(), nil })
	def(r, "plugin.reload", "Reload plugins", "Re-scan plugin folders and reload manifests (development: picks up changed plugin.json, rules and rebuilt binaries).", "",
		func(ctx context.Context, _ *empty) (any, error) {
			e.ReloadPlugins()
			return ok()
		})
	def(r, "plugin.enable", "Enable plugin", "Enable a plugin.", "",
		func(ctx context.Context, in *struct {
			PluginID string `json:"pluginId" required:"true"`
		}) (any, error) {
			return nil, e.SetPluginEnabled(in.PluginID, true)
		})
	def(r, "plugin.disable", "Disable plugin", "Disable a plugin.", "",
		func(ctx context.Context, in *struct {
			PluginID string `json:"pluginId" required:"true"`
		}) (any, error) {
			return nil, e.SetPluginEnabled(in.PluginID, false)
		})
	def(r, "plugin.install", "Install plugin", "Install a plugin from a zip package or a plugin folder.", "",
		func(ctx context.Context, in *struct {
			Path string `json:"path" required:"true"`
		}) (any, error) {
			return e.InstallPlugin(in.Path)
		})
	def(r, "plugin.uninstall", "Uninstall plugin", "Uninstall a user-installed plugin (bundled plugins can only be disabled).", "d",
		func(ctx context.Context, in *struct {
			PluginID string `json:"pluginId" required:"true"`
		}) (any, error) {
			return nil, e.UninstallPlugin(in.PluginID)
		})
	def(r, "plugin.settings", "Plugin settings", "Save settings of a plugin.", "",
		func(ctx context.Context, in *struct {
			PluginID string         `json:"pluginId" required:"true"`
			Values   map[string]any `json:"values" required:"true"`
		}) (any, error) {
			return nil, e.SetPluginSettings(in.PluginID, in.Values)
		})
	def(r, "store.list", "Plugin store", "List plugins in the store index.", "r",
		func(ctx context.Context, _ *empty) (any, error) { return e.StoreList(ctx) })
	def(r, "store.install", "Install from store", "Download and install a plugin from the store. Returns a task.", "",
		func(ctx context.Context, in *struct {
			PluginID string `json:"pluginId" required:"true"`
		}) (any, error) {
			return e.StoreInstall(in.PluginID)
		})
	def(r, "view.render", "Render plugin view", "Render a plugin-contributed view (declarative UI tree).", "r",
		func(ctx context.Context, in *struct {
			Plugin    string         `json:"plugin" required:"true"`
			View      string         `json:"view" required:"true"`
			ProjectID string         `json:"projectId"`
			ModuleID  string         `json:"moduleId"`
			RunID     string         `json:"runId"`
			State     map[string]any `json:"state"`
		}) (any, error) {
			return e.RenderView(ctx, in.Plugin, in.View, in.ProjectID, in.ModuleID, in.RunID, in.State)
		})
	def(r, "view.event", "Plugin view event", "Send a UI event to a plugin view.", "",
		func(ctx context.Context, in *struct {
			Plugin    string         `json:"plugin" required:"true"`
			View      string         `json:"view" required:"true"`
			Event     string         `json:"event" required:"true"`
			Values    map[string]any `json:"values"`
			ProjectID string         `json:"projectId"`
			ModuleID  string         `json:"moduleId"`
			RunID     string         `json:"runId"`
			State     map[string]any `json:"state"`
		}) (any, error) {
			return e.ViewEvent(ctx, in.Plugin, in.View, in.Event, in.Values, in.ProjectID, in.ModuleID, in.RunID, in.State)
		})

	// ---------------------------------------------------------------- 版本控制
	// ---------------------------------------------------------------- 文件（编辑器与 AI 共用）
	def(r, "fs.list", "List folder", "List a folder inside a project (path relative to the project root, \"\" for the root). Folders first; ignored folders such as node_modules are marked.", "r",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			Path      string `json:"path"`
		}) (any, error) {
			return e.FSListDir(in.ProjectID, in.Path)
		})
	def(r, "fs.read", "Read file", "Read a text file of a project. Returns content (line endings normalized to \\n), detected encoding, original line ending (with preferredLineEnding when mixed) and mtime (pass it back as expectedMtime when writing). Binary or very large files are flagged instead of returned; images up to 8 MB come back as dataUrl.", "r",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			Path      string `json:"path" required:"true"`
			Encoding  string `json:"encoding,omitempty" desc:"decode with this encoding instead of detecting it: utf-8, gb18030, gbk, big5, latin1, utf-16le, utf-16be"`
		}) (any, error) {
			return e.FSRead(in.ProjectID, in.Path, in.Encoding)
		})
	def(r, "fs.write", "Write file", "Write a text file of a project atomically, keeping its encoding and line ending when given. With expectedMtime the write fails (reason modifiedExternally) if the file changed since it was read.", "d",
		func(ctx context.Context, in *engine.FSWriteParams) (any, error) { return e.FSWrite(in) })
	def(r, "fs.stat", "Stat files", "Existence, size and mtime of project files (used to detect external modifications).", "r",
		func(ctx context.Context, in *struct {
			ProjectID string   `json:"projectId" required:"true"`
			Paths     []string `json:"paths" required:"true"`
		}) (any, error) {
			return e.FSStatPaths(in.ProjectID, in.Paths)
		})
	def(r, "fs.create", "Create file or folder", "Create an empty file or a folder in a project.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			Path      string `json:"path" required:"true"`
			Dir       bool   `json:"dir"`
		}) (any, error) {
			p, err := e.FSCreate(in.ProjectID, in.Path, in.Dir)
			return map[string]string{"path": p}, err
		})
	def(r, "fs.rename", "Rename", "Rename or move a file/folder inside a project.", "",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			From      string `json:"from" required:"true"`
			To        string `json:"to" required:"true"`
		}) (any, error) {
			p, err := e.FSRename(in.ProjectID, in.From, in.To)
			return map[string]string{"path": p}, err
		})
	def(r, "fs.delete", "Move to Trash", "Move project files/folders to the Trash (they can be restored from there).", "d",
		func(ctx context.Context, in *struct {
			ProjectID string   `json:"projectId" required:"true"`
			Paths     []string `json:"paths" required:"true"`
		}) (any, error) {
			t, err := e.FSDelete(in.ProjectID, in.Paths)
			return map[string]any{"trashed": t}, err
		})
	def(r, "fs.search", "Find file", "Find project files by fuzzy file-name match (quick open).", "r",
		func(ctx context.Context, in *struct {
			ProjectID string `json:"projectId" required:"true"`
			Query     string `json:"query"`
			Limit     int    `json:"limit"`
		}) (any, error) {
			return e.FSSearch(in.ProjectID, in.Query, in.Limit)
		})
	def(r, "fs.grep", "Find in files", "Search text (or a regular expression) in project files; skips binary files, files over 2 MB and ignored folders.", "r",
		func(ctx context.Context, in *engine.FSGrepParams) (any, error) { return e.FSGrep(ctx, in) })

	def(r, "vcs.overview", "Workspace repositories", "Every version-controlled repository in a workspace (all projects, nested repositories included) with its status: branch, ahead/behind, changed files, operation in progress. Use the returned root with vcs_* operations (projectId + root) to commit, pull or push each one.", "r",
		func(ctx context.Context, in *struct {
			WorkspaceID string `json:"workspaceId" desc:"Workspace id; empty for all workspaces."`
		}) (any, error) {
			return e.VCSOverview(ctx, in.WorkspaceID), nil
		})
	def(r, "vcs.roots", "Repositories", "Version control repositories inside the project (the project folder's repository, nested repositories and submodules).", "r",
		func(ctx context.Context, in *projectIn) (any, error) { return e.VCSRoots(in.ProjectID) })
	r.registerVCS()
}

func tokenOf(e *engine.Engine) string {
	type tokener interface{ Token() string }
	if t, ok := any(e).(tokener); ok {
		return t.Token()
	}
	return ""
}

func waitRun(ctx context.Context, e *engine.Engine, id string, secs int) model.Run {
	if secs > 300 {
		secs = 300
	}
	deadline := time.Now().Add(time.Duration(secs) * time.Second)
	for time.Now().Before(deadline) && ctx.Err() == nil {
		r, ok := e.Run(id)
		if !ok {
			break
		}
		if r.State == model.RunRunning || r.State == model.RunFailed || r.State == model.RunExited {
			return r
		}
		time.Sleep(300 * time.Millisecond)
	}
	r, _ := e.Run(id)
	return r
}

// LogsResult 是 run.logs 的返回。
type LogsResult struct {
	Lines     []model.LogLine `json:"lines"`
	LastSeq   int64           `json:"lastSeq"`
	Total     int             `json:"total"`
	Truncated bool            `json:"truncated"`
}

func readLogs(e *engine.Engine, id string, tail int, since int64, grep, stream string, raw bool) (*LogsResult, error) {
	c := e.Console(id)
	if c == nil {
		return nil, engine.ErrNotFound
	}
	if tail <= 0 {
		tail = 200
	}
	if tail > 5000 {
		tail = 5000
	}
	lines := c.Since(since)
	var re interface{ MatchString(string) bool }
	if grep != "" {
		cre, err := compileGrep(grep)
		if err != nil {
			return nil, &BadRequest{Msg: err.Error()}
		}
		re = cre
	}
	var out []model.LogLine
	for _, l := range lines {
		if stream != "" && l.Stream != stream {
			continue
		}
		if !raw {
			l.Text = console.StripANSI(l.Text)
		}
		if re != nil && !re.MatchString(l.Text) {
			continue
		}
		out = append(out, l)
	}
	res := &LogsResult{LastSeq: c.LastSeq(), Total: len(out)}
	if len(out) > tail {
		out = out[len(out)-tail:]
		res.Truncated = true
	}
	if out == nil {
		out = []model.LogLine{}
	}
	res.Lines = out
	return res, nil
}

func installCLI(exe, dir string) (any, error) {
	if exe == "" {
		return nil, engine.Errf("err.noExecutable")
	}
	if runtime.GOOS == "windows" && dir == "" {
		return installCLIWindows(exe)
	}
	home, _ := os.UserHomeDir()
	name := "paperplane"
	if runtime.GOOS == "windows" {
		name += ".exe"
	}
	cands := []string{dir}
	if dir == "" {
		cands = []string{"/usr/local/bin", filepath.Join(home, ".local", "bin")}
	}
	var lastErr error
	for _, d := range cands {
		if d == "" {
			continue
		}
		if err := os.MkdirAll(d, 0o755); err != nil {
			lastErr = err
			continue
		}
		target := filepath.Join(d, name)
		_ = os.Remove(target)
		if err := os.Symlink(exe, target); err != nil {
			lastErr = err
			continue
		}
		inPath := strings.Contains(os.Getenv("PATH"), d)
		return map[string]any{"path": target, "inPath": inPath}, nil
	}
	return nil, lastErr
}

// nonNilStrings 让空列表输出为 [] 而不是 null。
func nonNilStrings(v []string) []string {
	if v == nil {
		return []string{}
	}
	return v
}

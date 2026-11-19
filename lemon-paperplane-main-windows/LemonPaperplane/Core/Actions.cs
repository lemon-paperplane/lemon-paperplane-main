using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>用户操作的统一入口（按钮、菜单、快捷键、托盘都走这里，对应 Mac Actions）。启动面板的操作在 Features/Launch/LaunchBoard.cs（同 Mac 的 extension）。</summary>
public static partial class Actions
{
    private static AppModel Model => AppModel.Shared;

    // ---------------------------------------------------------------------------
    // 运行

    public static void Run(string projectId, string? configId = null) =>
        Model.Perform(L.T("action.run.failed"), async api => Model.Runs.PutRun(await api.StartRunAsync(projectId, configId)));

    public static void Stop(string runId) =>
        Model.Perform(L.T("action.stop.failed"), async api => Model.Runs.PutRun(await api.StopRunAsync(runId)));

    public static void Restart(string runId) =>
        Model.Perform(L.T("action.restart.failed"), async api => Model.Runs.PutRun(await api.RestartRunAsync(runId)));

    public static void Restart(string projectId, string configId) =>
        Model.Perform(L.T("action.restart.failed"), async api => Model.Runs.PutRun(await api.RestartRunAsync(projectId, configId)));

    public static void StopProject(string projectId) =>
        Model.Perform(L.T("action.stop.failed"), async api => await api.StopAllAsync(projectId: projectId));

    public static void StopAll() =>
        Model.Perform(L.T("action.stop.failed"), async api => await api.StopAllAsync());

    /// <summary>当前工程（详情页打开的）。</summary>
    public static string? CurrentProjectId => Model.Nav.OpenProjectId;

    /// <summary>Ctrl+R：运行当前工程（运行页选中的配置，否则默认配置）。</summary>
    public static void RunCurrent()
    {
        if (CurrentProjectId is not { } pid) return;
        Run(pid, Model.Nav.SelectedConfig.TryGetValue(pid, out var c) ? c : null);
    }

    /// <summary>Ctrl+.：停止当前工程（运行页选中配置的运行，否则整个工程）。</summary>
    public static void StopCurrent()
    {
        if (CurrentProjectId is not { } pid) return;
        if (Model.Nav.SelectedConfig.TryGetValue(pid, out var cid) && Model.Runs.ActiveRun(pid, cid) is { } r) Stop(r.Id);
        else StopProject(pid);
    }

    // ---------------------------------------------------------------------------
    // 工程

    public static void Open(string projectId, string with, string? moduleId = null) =>
        Model.Perform(L.T("action.open.failed"), async api => await api.OpenProjectAsync(projectId, with, moduleId));

    public static void Reimport(string projectId) =>
        Model.Perform(L.T("action.reimport.failed"), async api => Model.Track(await api.ReimportAsync(projectId)));

    public static void Sync(string projectId, string? moduleId = null) =>
        Model.Perform(L.T("action.sync.failed"), async api => Model.Track(await api.SyncAsync(projectId, moduleId)));

    public static void InstallDeps(string projectId, string? moduleId, bool clean = false) =>
        Model.Perform(L.T("action.deps.failed"), async api => Model.Track(await api.InstallDependenciesAsync(projectId, moduleId, clean)));

    public static void Rename(ProjectSummary p) =>
        Dialogs.Input(L.T("project.rename"), L.T("common.name"), async name =>
        {
            Model.Data.PutProject(await Model.Client().RenameProjectAsync(p.Id, name));
        }, initial: p.Name, ok: L.T("common.save"));

    public static void Remove(ProjectSummary p) =>
        Dialogs.Confirm(L.T("project.remove.title", p.Name), L.T("project.remove.message"), async () =>
        {
            await Model.Client().RemoveProjectAsync(p.Id);
            Model.Data.RemoveProject(p.Id);
            if (Model.Nav.OpenProjectId == p.Id) Model.Nav.OpenProjectId = null;
        }, ok: L.T("common.remove"), danger: true);

    public static void Move(string projectId, string workspaceId) =>
        Model.Perform(async api => await api.MoveProjectAsync(projectId, workspaceId));

    /// <summary>只在本机可用的操作（在资源管理器中显示、用其他应用打开、打开本机文件、安装命令行工具）：连着远程服务器时隐藏。</summary>
    public static bool LocalOnlyAvailable => !Model.IsRemote;

    /// <summary>在资源管理器中显示（文件则选中它）。连着远程服务器时路径在服务器上，不可用。</summary>
    public static void RevealInExplorer(string path)
    {
        if (Model.IsRemote)
        {
            Model.Toasts.Show("warning", L.T("remote.localOnly"));
            return;
        }
        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full)) Process.Start("explorer.exe", "/select,\"" + full + "\"");
            else Process.Start("explorer.exe", "\"" + full + "\"");
        }
        catch (Exception e) { Model.Toasts.Error(e); }
    }

    public static void OpenURL(string s)
    {
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps && u.Scheme != "mailto") return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) { Model.Toasts.Error(e); }
    }

    /// <summary>
    /// 打开服务器输出里的地址（控制台、终端里的链接）：连着远程服务器时 localhost / 127.0.0.1 指的是服务器上的端口，
    /// 先经网关转发到本机再打开；其余照常打开。
    /// </summary>
    public static void OpenServerURL(string url)
    {
        if (Model.IsRemote && RunAddress.TryLocal(url, out var port)) Model.OpenForwarded(url, port);
        else OpenURL(url);
    }

    public static void Copy(string text, string? toast = null)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
            Model.Toasts.Show("success", toast ?? L.T("common.copied"));
        }
        catch (Exception e) { Model.Toasts.Error(e); }
    }

    /// <summary>用系统默认程序打开文件。</summary>
    public static void OpenFile(string path)
    {
        if (Model.IsRemote)
        {
            Model.Toasts.Show("warning", L.T("remote.localOnly"));
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Model.Toasts.Error(e); }
    }

    // ---------------------------------------------------------------------------
    // 工作区

    public static void CreateWorkspace() =>
        Dialogs.Input(L.T("workspace.new"), L.T("common.name"), async name =>
        {
            var ws = await Model.Client().CreateWorkspaceAsync(name);
            if (Model.Data.Workspaces.All(w => w.Id != ws.Id)) Model.Data.Workspaces = Model.Data.Workspaces.Append(ws).ToList();
            Model.Nav.Page = NavPage.WorkspacePage(ws.Id);
        }, ok: L.T("common.create"));

    public static void RenameWorkspace(Workspace ws) =>
        Dialogs.Input(L.T("workspace.rename"), L.T("common.name"), async name =>
        {
            var n = await Model.Client().RenameWorkspaceAsync(ws.Id, name);
            Model.Data.Workspaces = Model.Data.Workspaces.Select(w => w.Id == n.Id ? n : w).ToList();
        }, initial: ws.Name, ok: L.T("common.save"));

    public static void DeleteWorkspace(Workspace ws)
    {
        var others = Model.Data.Workspaces.Where(w => w.Id != ws.Id).ToList();
        if (ws.ProjectIds.Count == 0 || others.Count == 0)
        {
            Dialogs.Confirm(L.T("workspace.delete.title", ws.Name), ws.ProjectIds.Count == 0 ? null : L.T("workspace.delete.message"), async () =>
            {
                await Model.Client().DeleteWorkspaceAsync(ws.Id);
                AfterWorkspaceDeleted(ws.Id);
            }, ok: L.T("common.delete"), danger: true);
            return;
        }
        // 有工程：选择移到哪个工作区，或一并移除
        var field = new FormField
        {
            Key = "moveTo", Title = L.T("workspace.delete.moveTo"), Type = "select",
            Options = new[] { new Option { Value = "", Label = L.T("workspace.delete.removeProjects") } }
                .Concat(others.Select(o => new Option { Value = o.Id, Label = o.Name })).ToList(),
        };
        Dialogs.Form(L.T("workspace.delete.title", ws.Name), new List<FormField> { field }, async v =>
        {
            var target = v.TryGetValue("moveTo", out var t) ? t.StringValue() : null;
            await Model.Client().DeleteWorkspaceAsync(ws.Id, string.IsNullOrEmpty(target) ? null : target);
            AfterWorkspaceDeleted(ws.Id);
        }, values: new Dictionary<string, JToken> { ["moveTo"] = others[0].Id }, ok: L.T("common.delete"), danger: true);
    }

    private static void AfterWorkspaceDeleted(string id)
    {
        Model.Data.Workspaces = Model.Data.Workspaces.Where(w => w.Id != id).ToList();
        if (Model.Nav.CurrentWorkspaceId == id)
            Model.Nav.Page = Model.Data.Workspaces.FirstOrDefault() is { } f ? NavPage.WorkspacePage(f.Id) : NavPage.Running;
    }

    /// <summary>导入时的目标工作区：当前页的，否则第一个。</summary>
    public static string? TargetWorkspaceId => Model.Nav.CurrentWorkspaceId ?? Model.Data.Workspaces.FirstOrDefault()?.Id;

    // ---------------------------------------------------------------------------
    // 任务

    public static void CancelTask(string id) => Model.Perform(async api => await api.CancelTaskAsync(id));

    public static void ShowTaskLog(string taskId)
    {
        var title = Model.Runs.Task(taskId)?.Title ?? L.T("task.log");
        var id = "tasklog-" + taskId;
        var close = new PPButton(L.T("common.close"), () => Dialogs.Dismiss(id));
        var console = new ConsolePanel(taskId, runId: null, logFile: null, compact: true) { Height = 420 };
        var dialog = new PPDialog(title, console, new UIElement[] { close }, width: 760, onClose: () => Dialogs.Dismiss(id))
        {
            CancelButton = close,
        };
        Dialogs.Present(id, dialog);
    }

    // ---------------------------------------------------------------------------
    // 修复

    /// <summary>执行修复：带 confirm（或 killPort）的先确认。</summary>
    public static void ApplyFix(Fix fix, Diagnosis diagnosis, string? runId, string? projectId, Action? onDone = null)
    {
        if (fix.Kind == FixKind.OpenURL && fix.Params.TryGetValue("url", out var url))
        {
            OpenURL(url);
            return;
        }
        async Task Exec()
        {
            var api = Model.Client();
            var res = await api.ApplyFixAsync(fix.Id, runId, runId == null ? projectId : null, diagnosis.Id);
            if (res.Task != null) Model.Track(res.Task);
            if (res.Rerun != null) Model.Runs.PutRun(res.Rerun);
            if (!string.IsNullOrEmpty(res.OpenUrl)) OpenURL(res.OpenUrl!);
            if (!string.IsNullOrEmpty(res.Message)) Model.Toasts.Show("success", fix.Title, res.Message);
            onDone?.Invoke();
        }
        var confirm = !string.IsNullOrEmpty(fix.Confirm) ? fix.Confirm
            : fix.Kind == FixKind.KillPort ? L.T("fix.killPort.confirm", fix.Params.TryGetValue("port", out var port) ? port : "") : null;
        if (confirm != null)
            Dialogs.Confirm(fix.Title, confirm, Exec, ok: L.T("fix.apply"), danger: fix.Kind == FixKind.KillPort);
        else
            _ = RunReporting(Exec, fix.Title);
    }

    private static async Task RunReporting(Func<Task> op, string title)
    {
        try { await op(); }
        catch (Exception e) { Model.Toasts.Error(e, title); }
    }

    // ---------------------------------------------------------------------------
    // 插件动作

    /// <summary>插件动作：确定模块 → 确认 → 表单 → project.action。</summary>
    public static async void Invoke(ActionInfo action, string projectId, string? moduleId = null, string? runId = null)
    {
        try
        {
            var api = Model.Client();
            var mid = moduleId;
            if (mid == null)
            {
                var p = await api.ProjectAsync(projectId);
                var candidates = p.Modules.Where(m => m.HasAnyFacet(action.Facets)
                    && (action.Facets.Count > 0 || m.Facets.Any(f => f.Plugin == action.Plugin))).ToList();
                if (candidates.Count > 1)
                {
                    PickModule(candidates, action.Title, picked => ContinueInvoke(action, projectId, picked, runId));
                    return;
                }
                mid = candidates.FirstOrDefault()?.Id ?? p.Modules.FirstOrDefault()?.Id;
            }
            if (mid == null) return;
            ContinueInvoke(action, projectId, mid, runId);
        }
        catch (Exception e)
        {
            Model.Toasts.Error(e, action.Title);
        }
    }

    private static async void ContinueInvoke(ActionInfo action, string projectId, string moduleId, string? runId)
    {
        async Task Send(Dictionary<string, JToken>? args) =>
            Model.Track(await Model.Client().InvokeActionAsync(projectId, moduleId, action.Plugin, action.Id, args, runId));

        if (action.Form.Count > 0)
        {
            // 带 optionsFrom 的下拉框取模块分析结果里的动态选项（如 scheme 列表）
            var opts = new Dictionary<string, List<Option>>();
            if (action.Form.Any(f => !string.IsNullOrEmpty(f.OptionsFrom)))
            {
                try
                {
                    var p = await Model.Client().ProjectAsync(projectId);
                    opts = (p.Module(moduleId) ?? p.Modules.FirstOrDefault())?.OptionValues ?? opts;
                }
                catch { }
            }
            Dialogs.Form(action.Title, action.Form, v => Send(v), message: action.Confirm, optionValues: opts, ok: action.Title);
        }
        else if (!string.IsNullOrEmpty(action.Confirm))
        {
            Dialogs.Confirm(action.Title, action.Confirm, () => Send(null), ok: action.Title);
        }
        else
        {
            await RunReporting(() => Send(null), action.Title);
        }
    }

    public static void PickModule(List<Module> modules, string title, Action<string> then)
    {
        var field = new FormField
        {
            Key = "module", Title = L.T("project.module"), Type = "select", Required = true,
            Options = modules.Select(m => new Option { Value = m.Id, Label = string.IsNullOrEmpty(m.RelDir) ? m.Name : $"{m.Name}  ({m.RelDir})" }).ToList(),
        };
        Dialogs.Form(title, new List<FormField> { field }, v =>
        {
            if (v.TryGetValue("module", out var id) && id.StringValue() is { } s)
                Application.Current.Dispatcher.BeginInvoke(new Action(() => then(s)));
            return Task.CompletedTask;
        }, values: new Dictionary<string, JToken> { ["module"] = modules[0].Id }, ok: L.T("common.continue"));
    }

    /// <summary>插件动作是否适用于这个工程（按 facet）。</summary>
    public static List<ActionInfo> Applicable(IEnumerable<ActionInfo> actions, ISet<string> facets, ISet<string> plugins) =>
        actions.Where(a => a.Facets.Count == 0 ? plugins.Contains(a.Plugin) : a.Facets.Any(facets.Contains)).ToList();

    /// <summary>插件动作的菜单项。</summary>
    public static IEnumerable<MenuEntry> MenuEntries(IEnumerable<ActionInfo> actions, string projectId, string? moduleId = null) =>
        actions.Select(a => MenuEntry.Item(new PPMenuItem(a.Title, () => Invoke(a, projectId, moduleId),
            iconRef: IconKey.Parse(a.Icon, a.Plugin) ?? IconKey.Builtin("puzzle"))));

    // ---------------------------------------------------------------------------
    // 打开方式

    /// <summary>用其他应用打开（project.open 在服务器上打开）：连着远程服务器时没有。</summary>
    public static IEnumerable<MenuEntry> OpenWithEntries(string projectId, string? moduleId = null) => Model.IsRemote ? Array.Empty<MenuEntry>() : new[]
    {
        MenuEntry.Item(L.T("open.finder"), () => Open(projectId, "finder", moduleId), icon: "folder-open"),
        MenuEntry.Item(L.T("open.terminal"), () => Open(projectId, "terminal", moduleId), icon: "square-terminal"),
        MenuEntry.Item(L.T("open.idea"), () => Open(projectId, "idea", moduleId), icon: "code-xml"),
        MenuEntry.Item(L.T("open.vscode"), () => Open(projectId, "vscode", moduleId), icon: "code-xml"),
    };
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit;

// docs/api.md 第 5 节全部操作的类型化封装（对应 Mac 的 API/Operations.swift，方法名 = Swift 名 + Async）。
// 界面代码优先用这里的方法；插件贡献的工具（<pluginId>.<tool>）用通用的 CallAsync(op, input)。
// 输入里值为 null 的键不发送（Json.Object）。

public sealed partial class ApiClient
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // MARK: 系统与设置

    public Task<SystemInfo> SystemInfoAsync(TimeSpan? timeout = null) => CallAsync<SystemInfo>("system.info", timeout: timeout);

    public Task ShutdownAsync(bool stopRuns) => SendAsync("system.shutdown", Json.Object(("stopRuns", stopRuns)), S(10));

    public Task<AppSettings> SettingsAsync() => CallAsync<AppSettings>("settings.get");

    /// <summary>JSON merge：只传要改的字段（JObject 或匿名对象）</summary>
    public Task<AppSettings> UpdateSettingsAsync(object patch) => CallAsync<AppSettings>("settings.update", patch);

    public Task<MirrorStatus> MirrorsStatusAsync() => CallAsync<MirrorStatus>("mirrors.status");
    public Task<MirrorStatus> MirrorsProbeAsync() => CallAsync<MirrorStatus>("mirrors.probe", timeout: S(60));
    public Task<Contributions> ContributionsAsync() => CallAsync<Contributions>("ui.contributions");

    public Task RespondPromptAsync(string id, bool cancelled, IDictionary<string, JToken> values) =>
        SendAsync("ui.respond", Json.Object(("promptId", id), ("cancelled", cancelled), ("values", values)));

    public Task<List<Prompt>> PendingPromptsAsync() => CallAsync<List<Prompt>>("ui.prompts");

    public Task<PathResult> InstallCLIAsync(string? dir = null) => CallAsync<PathResult>("cli.install", Json.Object(("dir", dir)));

    public Task<MCPConfig> McpConfigAsync() => CallAsync<MCPConfig>("mcp.config");

    // MARK: 工作区

    public Task<List<Workspace>> WorkspacesAsync() => CallAsync<List<Workspace>>("workspace.list");

    public Task<Workspace> CreateWorkspaceAsync(string name) => CallAsync<Workspace>("workspace.create", Json.Object(("name", name)));

    public Task<Workspace> RenameWorkspaceAsync(string id, string name) =>
        CallAsync<Workspace>("workspace.rename", Json.Object(("workspaceId", id), ("name", name)));

    public Task DeleteWorkspaceAsync(string id, string? moveTo = null) =>
        SendAsync("workspace.delete", Json.Object(("workspaceId", id), ("moveTo", moveTo)));

    public Task ReorderWorkspacesAsync(IEnumerable<string> ids) => SendAsync("workspace.reorder", Json.Object(("workspaceIds", ids)));

    // MARK: 工程

    public Task<List<ProjectSummary>> ProjectsAsync(string? workspaceId = null) =>
        CallAsync<List<ProjectSummary>>("project.list", Json.Object(("workspaceId", workspaceId)));

    public Task<Project> ProjectAsync(string id) => CallAsync<Project>("project.get", Json.Object(("projectId", id)));

    public Task<List<ScanEntry>> ScanAsync(string path, int? maxDepth = null) =>
        CallAsync<List<ScanEntry>>("project.scan", Json.Object(("path", path), ("maxDepth", maxDepth)), S(120));

    public Task<Project> ImportProjectAsync(string path, string workspaceId, string? name = null) =>
        CallAsync<Project>("project.import", Json.Object(("path", path), ("workspaceId", workspaceId), ("name", name)), S(600));

    public Task<List<Project>> ImportProjectsAsync(IEnumerable<string> paths, string workspaceId) =>
        CallAsync<List<Project>>("project.importMany", Json.Object(("paths", paths), ("workspaceId", workspaceId)), S(1800));

    public Task RemoveProjectAsync(string id) => SendAsync("project.remove", Json.Object(("projectId", id)));

    public Task<ProjectSummary> RenameProjectAsync(string id, string name) =>
        CallAsync<ProjectSummary>("project.rename", Json.Object(("projectId", id), ("name", name)));

    public Task MoveProjectAsync(string id, string workspaceId, int? index = null) =>
        SendAsync("project.move", Json.Object(("projectId", id), ("workspaceId", workspaceId), ("index", index)));

    public Task ReorderProjectsAsync(string workspaceId, IEnumerable<string> projectIds) =>
        SendAsync("project.reorder", Json.Object(("workspaceId", workspaceId), ("projectIds", projectIds)));

    public Task<TaskInfo> ReimportAsync(string id) => CallAsync<TaskInfo>("project.reimport", Json.Object(("projectId", id)));

    public Task<TaskInfo> SyncAsync(string id, string? moduleId = null) =>
        CallAsync<TaskInfo>("project.sync", Json.Object(("projectId", id), ("moduleId", moduleId)));

    /// <summary>env：整体替换工程环境变量；toolchains：kind → {id}/{constraint}，值为 null 表示清除覆盖</summary>
    public Task<Project> UpdateProjectSettingsAsync(string id, IDictionary<string, string>? env = null,
        IDictionary<string, ToolchainOverride?>? toolchains = null)
    {
        var input = Json.Object(("projectId", id), ("env", env));
        if (toolchains != null)
        {
            var o = new JObject();
            foreach (var kv in toolchains) o[kv.Key] = kv.Value == null ? JValue.CreateNull() : Json.ToToken(kv.Value);
            input["toolchains"] = o;
        }
        return CallAsync<Project>("project.settings", input);
    }

    /// <summary>with：finder | terminal | idea | vscode | default（Windows 内核把 finder 和其他未知值都当成资源管理器）</summary>
    public Task OpenProjectAsync(string id, string with, string? moduleId = null) =>
        SendAsync("project.open", Json.Object(("projectId", id), ("moduleId", moduleId), ("with", with)));

    public Task<List<Diagnosis>> ProblemsAsync(string id) => CallAsync<List<Diagnosis>>("project.problems", Json.Object(("projectId", id)));

    /// <summary>runId：挂在 run.toolbar 的动作触发时所在的运行</summary>
    public Task<TaskInfo> InvokeActionAsync(string projectId, string moduleId, string plugin, string action,
        IDictionary<string, JToken>? args = null, string? runId = null) =>
        CallAsync<TaskInfo>("project.action", Json.Object(("projectId", projectId), ("moduleId", moduleId), ("plugin", plugin),
            ("action", action), ("args", args), ("runId", runId)));

    public Task<TaskInfo> InstallDependenciesAsync(string projectId, string? moduleId = null, bool clean = false) =>
        CallAsync<TaskInfo>("deps.install", Json.Object(("projectId", projectId), ("moduleId", moduleId), ("clean", clean ? true : null)));

    // MARK: 运行配置

    public Task<List<RunConfigTypeChoice>> RunConfigTypesAsync(string projectId, string moduleId) =>
        CallAsync<List<RunConfigTypeChoice>>("runconfig.types", Json.Object(("projectId", projectId), ("moduleId", moduleId)));

    public Task<RunConfig> CreateRunConfigAsync(string projectId, string moduleId, string type, string name,
        IDictionary<string, JToken> options, IDictionary<string, string> env) =>
        CallAsync<RunConfig>("runconfig.create", Json.Object(("projectId", projectId), ("moduleId", moduleId), ("type", type),
            ("name", name), ("options", options), ("env", env)));

    public Task<RunConfig> UpdateRunConfigAsync(string projectId, string configId, string? name = null,
        IDictionary<string, JToken>? options = null, IDictionary<string, string>? env = null) =>
        CallAsync<RunConfig>("runconfig.update", Json.Object(("projectId", projectId), ("configId", configId), ("name", name),
            ("options", options), ("env", env)));

    public Task DeleteRunConfigAsync(string projectId, string configId) =>
        SendAsync("runconfig.delete", Json.Object(("projectId", projectId), ("configId", configId)));

    public Task<RunConfig> ResetRunConfigAsync(string projectId, string configId) =>
        CallAsync<RunConfig>("runconfig.reset", Json.Object(("projectId", projectId), ("configId", configId)));

    /// <summary>实时取表单里 live 的下拉选项（如 ios.devices / android.devices）</summary>
    public Task<Dictionary<string, List<Option>>> LiveOptionsAsync(string projectId, string? moduleId, IEnumerable<string> keys) =>
        CallAsync<Dictionary<string, List<Option>>>("runconfig.options", Json.Object(("projectId", projectId), ("moduleId", moduleId), ("keys", keys)), S(30));

    public Task SetDefaultRunConfigAsync(string projectId, string configId) =>
        SendAsync("runconfig.setDefault", Json.Object(("projectId", projectId), ("configId", configId)));

    // MARK: 运行

    public Task<Run> StartRunAsync(string projectId, string? configId = null) =>
        CallAsync<Run>("run.start", Json.Object(("projectId", projectId), ("configId", configId)));

    public Task<Run> StopRunAsync(string runId) => CallAsync<Run>("run.stop", Json.Object(("runId", runId)));

    public Task<Run> RestartRunAsync(string runId) => CallAsync<Run>("run.restart", Json.Object(("runId", runId)));

    public Task<Run> RestartRunAsync(string projectId, string configId) =>
        CallAsync<Run>("run.restart", Json.Object(("projectId", projectId), ("configId", configId)));

    // MARK: 启动面板

    public Task<LaunchIDs> LaunchAddAsync(string projectId, IEnumerable<string> configIds) =>
        CallAsync<LaunchIDs>("launch.add", Json.Object(("projectId", projectId), ("configIds", configIds)));

    public Task<LaunchIDs> LaunchRemoveAsync(string projectId, IEnumerable<string> configIds) =>
        CallAsync<LaunchIDs>("launch.remove", Json.Object(("projectId", projectId), ("configIds", configIds)));

    /// <summary>设置启动项别名（空字符串清除）</summary>
    public Task LaunchSetAliasAsync(string projectId, string configId, string alias) =>
        SendAsync("launch.setAlias", Json.Object(("projectId", projectId), ("configId", configId), ("alias", alias)));

    /// <summary>工程里能作为启动项运行的文件（脚本、程序；includeSources 时加上其他源码）</summary>
    public Task<RunnableList> LaunchCandidatesAsync(string projectId, bool includeSources) =>
        CallAsync<RunnableList>("launch.candidates", Json.Object(("projectId", projectId), ("includeSources", includeSources)), S(60));

    /// <summary>把工程里的脚本/可执行文件加到启动（同一文件已有运行配置就复用）</summary>
    public Task<LaunchAddResult> LaunchAddFileAsync(string projectId, string path) =>
        CallAsync<LaunchAddResult>("launch.addFile", Json.Object(("projectId", projectId), ("path", path)));

    /// <summary>把一段自定义命令加到启动；cwd 为空表示工程根目录</summary>
    public Task<LaunchAddResult> LaunchAddCommandAsync(string projectId, string name, string command, string cwd, string? shell = null) =>
        CallAsync<LaunchAddResult>("launch.addCommand", Json.Object(("projectId", projectId), ("name", name), ("command", command),
            ("cwd", cwd), ("shell", shell)));

    public Task<LaunchResult> LaunchRunAsync(string? workspaceId = null, string? projectId = null, IEnumerable<string>? configIds = null) =>
        CallAsync<LaunchResult>("launch.run", Json.Object(("workspaceId", workspaceId), ("projectId", projectId), ("configIds", configIds)), S(120));

    public Task<LaunchResult> LaunchStopAsync(string? workspaceId = null, string? projectId = null, IEnumerable<string>? configIds = null) =>
        CallAsync<LaunchResult>("launch.stop", Json.Object(("workspaceId", workspaceId), ("projectId", projectId), ("configIds", configIds)));

    public Task<StopAllResult> StopAllAsync(string? workspaceId = null, string? projectId = null) =>
        CallAsync<StopAllResult>("run.stopAll", Json.Object(("workspaceId", workspaceId), ("projectId", projectId)));

    // MARK: 终端

    public Task<List<TerminalInfo>> TerminalListAsync(string? workspaceId = null) =>
        CallAsync<List<TerminalInfo>>("terminal.list", Json.Object(("workspaceId", workspaceId)));

    /// <summary>新建终端：起始目录是工程根目录，名字默认是工程名</summary>
    public Task<TerminalInfo> TerminalCreateAsync(string projectId, int cols, int rows) =>
        CallAsync<TerminalInfo>("terminal.create", Json.Object(("projectId", projectId), ("cols", cols), ("rows", rows)));

    public Task TerminalRenameAsync(string id, string name) => SendAsync("terminal.rename", Json.Object(("id", id), ("name", name)));

    public Task TerminalRestartAsync(string id) => SendAsync("terminal.restart", Json.Object(("id", id)));

    /// <summary>终端里是否有命令在前台运行（关闭前确认用）</summary>
    public Task<TerminalBusy> TerminalBusyAsync(string id) => CallAsync<TerminalBusy>("terminal.busy", Json.Object(("id", id)));

    public Task TerminalCloseAsync(string id) => SendAsync("terminal.close", Json.Object(("id", id)));

    /// <summary>原样写入终端（回车用 \r，Ctrl-C 用 \u0003）。Mac 由终端网页直接调，这里给原生终端用。</summary>
    public Task TerminalInputAsync(string id, string data) => SendAsync("terminal.input", Json.Object(("id", id), ("data", data)));

    public Task TerminalResizeAsync(string id, int cols, int rows) =>
        SendAsync("terminal.resize", Json.Object(("id", id), ("cols", cols), ("rows", rows)));

    // MARK: 运行列表与日志

    public Task<List<Run>> RunsAsync(string? projectId = null, bool? active = null) =>
        CallAsync<List<Run>>("run.list", Json.Object(("projectId", projectId), ("active", active)));

    public Task<Run> RunAsync(string id) => CallAsync<Run>("run.get", Json.Object(("runId", id)));

    public Task<LogsResult> LogsAsync(string consoleId, int? tail = null, long? since = null, string? grep = null) =>
        CallAsync<LogsResult>("run.logs", Json.Object(("consoleId", consoleId), ("tail", tail), ("since", since), ("grep", grep)));

    public Task RunInputAsync(string runId, string text) => SendAsync("run.input", Json.Object(("runId", runId), ("text", text)));

    public Task ClearRunAsync(string runId) => SendAsync("run.clear", Json.Object(("runId", runId)));

    /// <summary>运行中的诊断：传 runId（+ diagnosisId）；静态问题：传 projectId + diagnosisId</summary>
    public Task<FixResult> ApplyFixAsync(string fixId, string? runId = null, string? projectId = null, string? diagnosisId = null) =>
        CallAsync<FixResult>("fix.apply", Json.Object(("runId", runId), ("projectId", projectId), ("diagnosisId", diagnosisId),
            ("fixId", fixId)), S(600));

    public Task<PortKillResult> KillPortAsync(int port) => CallAsync<PortKillResult>("port.kill", Json.Object(("port", port)));

    // MARK: 任务

    public Task<List<TaskInfo>> TasksAsync(bool? active = null) => CallAsync<List<TaskInfo>>("task.list", Json.Object(("active", active)));

    public Task<TaskInfo> TaskAsync(string id) => CallAsync<TaskInfo>("task.get", Json.Object(("taskId", id)));

    public Task CancelTaskAsync(string id) => SendAsync("task.cancel", Json.Object(("taskId", id)));

    // MARK: 工具链

    public Task<List<ToolchainKind>> ToolchainKindsAsync() => CallAsync<List<ToolchainKind>>("toolchain.kinds");

    public Task<List<Toolchain>> ToolchainsAsync(string? kind = null) => CallAsync<List<Toolchain>>("toolchain.list", Json.Object(("kind", kind)));

    public Task<List<AvailableVersion>> AvailableToolchainsAsync(string kind) =>
        CallAsync<List<AvailableVersion>>("toolchain.available", Json.Object(("kind", kind)), S(60));

    public Task<TaskInfo> InstallToolchainAsync(string kind, string version) =>
        CallAsync<TaskInfo>("toolchain.install", Json.Object(("kind", kind), ("version", version)));

    public Task<Toolchain> AddToolchainAsync(string kind, string path) =>
        CallAsync<Toolchain>("toolchain.add", Json.Object(("kind", kind), ("path", path)));

    public Task RemoveToolchainAsync(string id) => SendAsync("toolchain.remove", Json.Object(("toolchainId", id)));

    public Task RefreshToolchainsAsync() => SendAsync("toolchain.refresh", timeout: S(120));

    // MARK: 插件

    public Task<List<PluginInfo>> PluginsAsync() => CallAsync<List<PluginInfo>>("plugin.list");

    public Task SetPluginEnabledAsync(string id, bool enabled) =>
        SendAsync(enabled ? "plugin.enable" : "plugin.disable", Json.Object(("pluginId", id)));

    public Task<PluginInfo> InstallPluginAsync(string path) => CallAsync<PluginInfo>("plugin.install", Json.Object(("path", path)), S(120));

    public Task UninstallPluginAsync(string id) => SendAsync("plugin.uninstall", Json.Object(("pluginId", id)));

    public Task UpdatePluginSettingsAsync(string id, IDictionary<string, JToken> values) =>
        SendAsync("plugin.settings", Json.Object(("pluginId", id), ("values", values)));

    public Task<List<StoreEntry>> StoreAsync(bool refresh = false) =>
        CallAsync<List<StoreEntry>>("store.list", Json.Object(("refresh", refresh ? true : null)), S(60));

    public Task<TaskInfo> InstallFromStoreAsync(string id) => CallAsync<TaskInfo>("store.install", Json.Object(("pluginId", id)));

    // MARK: 插件界面

    public Task<ViewRenderResult> RenderViewAsync(ViewContext ctx, IDictionary<string, JToken>? state)
    {
        var input = ctx.Input;
        if (state != null) input["state"] = Json.ToToken(state);
        return CallAsync<ViewRenderResult>("view.render", input);
    }

    /// <summary>webview 视图：view.render 不调插件，直接返回 {"webviewUrl": "..."}（docs/api.md 第 6 节）</summary>
    public async Task<Uri?> WebviewURLAsync(ViewContext ctx)
    {
        var r = await CallAsync<JToken>("view.render", ctx.Input).ConfigureAwait(false);
        var s = r.Get("webviewUrl").StringValue();
        return s != null && Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;
    }

    public Task<ViewEventResult> ViewEventAsync(ViewContext ctx, string @event, IDictionary<string, JToken> values,
        IDictionary<string, JToken>? state)
    {
        var input = ctx.Input;
        input["event"] = @event;
        input["values"] = Json.ToToken(values);
        if (state != null) input["state"] = Json.ToToken(state);
        return CallAsync<ViewEventResult>("view.event", input);
    }

    // MARK: 版本控制（vcs.<op>，Args 平铺进输入）

    /// <summary>调 vcs.&lt;op&gt;：args（VCSXxxArgs 或 null）平铺进输入，再加上 projectId、root。超时默认按 VcsTimeout(op)。</summary>
    public Task<T> VcsAsync<T>(string op, string projectId, string? root = null, object? args = null, TimeSpan? timeout = null)
    {
        var input = args == null ? new JObject() : Json.ToToken(args) as JObject ?? new JObject();
        input["projectId"] = projectId;
        if (root != null) input["root"] = root;
        return CallAsync<T>("vcs." + op, input, timeout ?? VcsTimeout(op));
    }

    /// <summary>各操作的超时：远程操作、钩子、变基可能很久；读操作用默认值（null）</summary>
    public static TimeSpan? VcsTimeout(string op)
    {
        switch (op)
        {
            case VCSOp.Push: case VCSOp.Pull: case VCSOp.Fetch: case VCSOp.Clone: case VCSOp.SubmoduleUpdate: case VCSOp.TagPush:
                return S(3600);
            case VCSOp.Commit: case VCSOp.Merge: case VCSOp.Rebase: case VCSOp.RebaseInteractive: case VCSOp.Continue: case VCSOp.Skip:
            case VCSOp.CherryPick: case VCSOp.Revert: case VCSOp.Checkout: case VCSOp.StashSave: case VCSOp.StashApply: case VCSOp.Reword:
            case VCSOp.BranchDelete: case VCSOp.TagCreate: case VCSOp.TagDelete: case VCSOp.MergeTool:
                return S(900);
            default:
                return null;
        }
    }

    /// <summary>vcs.overview：工作区内全部仓库及状态（workspaceId 为空表示全部工作区）</summary>
    public Task<List<VCSRepoOverview>> VcsOverviewAsync(string? workspaceId) =>
        CallAsync<List<VCSRepoOverview>>("vcs.overview", Json.Object(("workspaceId", workspaceId)), S(300));

    public Task<List<VCSRoot>> VcsRootsAsync(string projectId) => VcsAsync<List<VCSRoot>>(VCSOp.Roots, projectId);

    public Task<VCSStatus> VcsStatusAsync(string projectId, string? root = null) => VcsAsync<VCSStatus>(VCSOp.Status, projectId, root);

    public Task<VCSDiff> VcsDiffAsync(string projectId, VCSDiffArgs args, string? root = null) =>
        VcsAsync<VCSDiff>(VCSOp.Diff, projectId, root, args);

    public Task<VCSLog> VcsLogAsync(string projectId, VCSLogArgs args, string? root = null) =>
        VcsAsync<VCSLog>(VCSOp.Log, projectId, root, args);

    public Task<VCSLog> VcsFileHistoryAsync(string projectId, VCSFileHistoryArgs args, string? root = null) =>
        VcsAsync<VCSLog>(VCSOp.FileHistory, projectId, root, args);

    public Task<VCSCommitDetail> VcsShowAsync(string projectId, string hash, int? parent = null, string? root = null) =>
        VcsAsync<VCSCommitDetail>(VCSOp.Show, projectId, root, new VCSShowArgs { Hash = hash, Parent = parent });

    public Task<VCSBranches> VcsBranchesAsync(string projectId, string? root = null) => VcsAsync<VCSBranches>(VCSOp.Branches, projectId, root);

    public Task<VCSCommitResult> VcsCommitAsync(string projectId, VCSCommitArgs args, string? root = null) =>
        VcsAsync<VCSCommitResult>(VCSOp.Commit, projectId, root, args);

    public Task<VCSOpResult> VcsPushAsync(string projectId, VCSPushArgs args, string? root = null) =>
        VcsAsync<VCSOpResult>(VCSOp.Push, projectId, root, args);

    public Task<VCSOpResult> VcsPullAsync(string projectId, VCSPullArgs args, string? root = null) =>
        VcsAsync<VCSOpResult>(VCSOp.Pull, projectId, root, args);

    public Task<VCSOpResult> VcsFetchAsync(string projectId, VCSFetchArgs args, string? root = null) =>
        VcsAsync<VCSOpResult>(VCSOp.Fetch, projectId, root, args);

    public Task<VCSPushPreview> VcsPushPreviewAsync(string projectId, VCSPushArgs args, string? root = null) =>
        VcsAsync<VCSPushPreview>(VCSOp.PushPreview, projectId, root, args);

    public Task<VCSStashList> VcsStashListAsync(string projectId, string? root = null) => VcsAsync<VCSStashList>(VCSOp.StashList, projectId, root);

    public Task<VCSStashShow> VcsStashShowAsync(string projectId, int index, string? root = null) =>
        VcsAsync<VCSStashShow>(VCSOp.StashShow, projectId, root, new VCSStashArgs { Index = index });

    public Task<VCSFileContent> VcsFileContentAsync(string projectId, VCSFileContentArgs args, string? root = null) =>
        VcsAsync<VCSFileContent>(VCSOp.FileContent, projectId, root, args);

    public Task<VCSContainingBranches> VcsContainingBranchesAsync(string projectId, string hash, string? root = null) =>
        VcsAsync<VCSContainingBranches>(VCSOp.ContainingBranches, projectId, root, new VCSShowArgs { Hash = hash });

    public Task<VCSMergeTools> VcsMergeToolsAsync(string projectId, string? root = null) => VcsAsync<VCSMergeTools>(VCSOp.MergeTools, projectId, root);

    public Task<VCSCompare> VcsCompareAsync(string projectId, VCSCompareArgs args, string? root = null) =>
        VcsAsync<VCSCompare>(VCSOp.Compare, projectId, root, args);

    public Task<VCSConfig> VcsConfigGetAsync(string projectId, IEnumerable<string> keys, bool? global = null, string? root = null) =>
        VcsAsync<VCSConfig>(VCSOp.ConfigGet, projectId, root, new VCSConfigGetArgs { Keys = new List<string>(keys), Global = global });

    /// <summary>写操作的通用形式：返回 VCSOpResult（如 VcsRunAsync(VCSOp.Stage, pid, new VCSPathsArgs { Paths = … })）</summary>
    public Task<VCSOpResult> VcsRunAsync(string op, string projectId, object? args, string? root = null) =>
        VcsAsync<VCSOpResult>(op, projectId, root, args);

    /// <summary>project.clone：克隆并导入（同步等克隆完成；克隆输出在任务控制台）</summary>
    public Task<Project> CloneProjectAsync(string url, string dest, string? branch = null, string? workspaceId = null) =>
        CallAsync<Project>("project.clone", Json.Object(("url", url), ("dest", dest), ("branch", branch), ("workspaceId", workspaceId)), S(3600));
}

/// <summary>插件视图调用上下文（view.render / view.event 的公共字段）。</summary>
public sealed record ViewContext
{
    public required string Plugin { get; set; }
    public required string View { get; set; }
    public string? ProjectId { get; set; }
    public string? ModuleId { get; set; }
    public string? RunId { get; set; }
    /// <summary>首次 render 时带给插件的 state（如版本控制页把当前仓库 {"root": …} 传给插件子页）</summary>
    public Dictionary<string, JToken>? InitialState { get; set; }

    /// <summary>每次新建一个输入对象（调用方会往里加字段）</summary>
    internal JObject Input => Json.Object(("plugin", Plugin), ("view", View), ("projectId", ProjectId), ("moduleId", ModuleId), ("runId", RunId));
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit;

// 对应 lemon-paperplane-main-core/internal/model/model.go，字段名以 Go json tag 为准（C# 属性 PascalCase，JSON camelCase）。
// 改名同 Mac：Task → TaskInfo，Settings → AppSettings，Event → DaemonEvent。
// 约定：模型是 record（可以 with 浅拷贝；相等只比较集合引用，按内容比较用 Json.DeepEquals）；
// Mac 里带默认值包装器的字段在这里有初始值（集合空、字符串 ""），Mac 里的可选字段在这里可空。
// 只读的计算属性不参与 JSON。

// MARK: - 工作区与工程

public sealed record Workspace
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> ProjectIds { get; set; } = new();
    public DateTimeOffset? CreatedAt { get; set; }
}

public sealed record Project
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public List<Module> Modules { get; set; } = new();
    public ProjectSettings? Settings { get; set; }
    public List<VCSRoot> VcsRoots { get; set; } = new();
    public DateTimeOffset? ImportedAt { get; set; }
    public DateTimeOffset? AnalyzedAt { get; set; }
    public string? DefaultConfigId { get; set; }

    /// <summary>全部运行配置（按模块顺序）</summary>
    public List<RunConfig> AllRunConfigs => Modules.SelectMany(m => m.RunConfigs).ToList();

    public Module? Module(string? id) => id == null ? null : Modules.FirstOrDefault(m => m.Id == id);

    public RunConfig? RunConfig(string? id) => id == null ? null : Modules.SelectMany(m => m.RunConfigs).FirstOrDefault(c => c.Id == id);

    /// <summary>工程里出现过的全部 facet id</summary>
    public HashSet<string> FacetIds => new(Modules.SelectMany(m => m.Facets.Select(f => f.Id)));

    /// <summary>参与过本工程的插件</summary>
    public HashSet<string> PluginIds
    {
        get
        {
            var s = new HashSet<string>();
            foreach (var m in Modules)
            {
                foreach (var f in m.Facets) if (f.Plugin != null) s.Add(f.Plugin);
                foreach (var k in m.Analysis.Keys) s.Add(k);
            }
            return s;
        }
    }
}

public sealed record ProjectSettings
{
    public Dictionary<string, string> Env { get; set; } = new();
    public Dictionary<string, ToolchainOverride> Toolchains { get; set; } = new();
    public string? Notes { get; set; }
}

public sealed record ToolchainOverride
{
    public string? Id { get; set; }
    public string? Constraint { get; set; }
}

public sealed record VCSRoot
{
    public string Path { get; set; } = "";
    public string RelPath { get; set; } = "";
    public string Vcs { get; set; } = "";
    public string Plugin { get; set; } = "";
}

public sealed record Module
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RelDir { get; set; } = "";
    public List<Facet> Facets { get; set; } = new();
    public Dictionary<string, JToken> Models { get; set; } = new();
    public Dictionary<string, ModuleAnalysis> Analysis { get; set; } = new();
    public Dictionary<string, ToolchainSelection> Toolchains { get; set; } = new();
    public List<ToolchainRequirement> Requirements { get; set; } = new();
    public List<RunConfig> RunConfigs { get; set; } = new();
    public DependencyState? Dependencies { get; set; }
    public bool Synced { get; set; }
    public bool NeedsSync { get; set; }

    private IEnumerable<ModuleAnalysis> SortedAnalysis =>
        Analysis.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => Analysis[k]);

    /// <summary>各插件 info 合并（按插件 id 排序）</summary>
    public List<InfoItem> Info => SortedAnalysis.SelectMany(a => a.Info).ToList();

    /// <summary>各插件静态问题合并</summary>
    public List<Diagnosis> Problems => SortedAnalysis.SelectMany(a => a.Problems).ToList();

    /// <summary>各插件动态下拉选项合并（FormField.OptionsFrom 查这里）</summary>
    public Dictionary<string, List<Option>> OptionValues
    {
        get
        {
            var o = new Dictionary<string, List<Option>>();
            foreach (var a in SortedAnalysis)
                foreach (var kv in a.OptionValues)
                {
                    if (!o.TryGetValue(kv.Key, out var list)) o[kv.Key] = list = new List<Option>();
                    list.AddRange(kv.Value);
                }
            return o;
        }
    }

    public bool HasAnyFacet(IReadOnlyCollection<string> ids) => ids.Count == 0 || Facets.Any(f => ids.Contains(f.Id));
}

public sealed record ModuleAnalysis
{
    public List<InfoItem> Info { get; set; } = new();
    public List<Diagnosis> Problems { get; set; } = new();
    public Dictionary<string, List<Option>> OptionValues { get; set; } = new();
    public DependencyState? Dependencies { get; set; }
    public DateTimeOffset? At { get; set; }
}

public sealed record ToolchainSelection
{
    public string Kind { get; set; } = "";
    public string? ToolchainId { get; set; }
    public string? Version { get; set; }
    public string? Constraint { get; set; }
    public string? Preferred { get; set; }
    public bool Missing { get; set; }
    public bool Override { get; set; }
    public string? Source { get; set; }
}

/// <summary>持久化的运行配置（Go 里嵌入了 sdk.RunConfiguration，JSON 平铺）。</summary>
public sealed record RunConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Plugin { get; set; }
    public string? ModuleId { get; set; }
    public Dictionary<string, JToken> Options { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public bool Default { get; set; }
    /// <summary>auto | user | idea | vscode</summary>
    public string? Source { get; set; }
    public int Priority { get; set; }
    public bool Edited { get; set; }
    public bool Stale { get; set; }
}

// MARK: - 卡片摘要

public sealed record ProjectSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public List<FacetBadge> Facets { get; set; } = new();
    public List<InfoItem> Info { get; set; } = new();
    /// <summary>idle | starting | running | failed | stopping | partial</summary>
    public string Status { get; set; } = "";
    public List<RunBrief> Runs { get; set; } = new();
    public List<ConfigBrief> Configs { get; set; } = new();
    public string? DefaultConfigId { get; set; }
    /// <summary>加到工作区「启动」面板的运行配置 id（按加入顺序，只含仍存在的）</summary>
    public List<string> Launch { get; set; } = new();
    /// <summary>启动项别名（运行配置 id → 别名），只在「启动」里显示</summary>
    public Dictionary<string, string> LaunchAlias { get; set; } = new();
    public VCSBrief? Vcs { get; set; }
    public int Problems { get; set; }
    public string? ProblemSeverity { get; set; }
    public List<string> MissingToolchains { get; set; } = new();
    public string? Dependencies { get; set; }
    public string? Busy { get; set; }
    /// <summary>最近一次结束的运行（卡片「未运行 · 上次运行 3 小时前」）。</summary>
    public LastRun? LastRun { get; set; }

    /// <summary>是否有运行中（含启动中、准备中、停止中）的运行</summary>
    public bool HasActiveRun => Runs.Any(r => RunState.IsActive(r.State));
}

/// <summary>工程最近一次结束的运行（内核随工程保存，内核重启后也还在）。</summary>
public sealed record LastRun
{
    public string ConfigId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset? EndedAt { get; set; }
    public int ExitCode { get; set; }
    /// <summary>异常退出或启动失败（用户停止不算）。</summary>
    public bool Failed { get; set; }
}

public sealed record FacetBadge
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public string? Category { get; set; }
    public string Plugin { get; set; } = "";
}

public sealed record ConfigBrief
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Icon { get; set; }
    public string ModuleId { get; set; } = "";
    public string ModuleName { get; set; } = "";
}

public sealed record RunBrief
{
    public string Id { get; set; } = "";
    public string? ProjectId { get; set; }
    public string ConfigId { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public List<string> Urls { get; set; } = new();
    public double Cpu { get; set; }
    public double RssMB { get; set; }
    /// <summary>本次运行的内存峰值</summary>
    public double PeakRSSMB { get; set; }
    /// <summary>进程树里的进程数</summary>
    public int Procs { get; set; }
    /// <summary>采样时间（Unix 毫秒），据此追加趋势点</summary>
    public long T { get; set; }
}

public sealed record VCSBrief
{
    public string Branch { get; set; } = "";
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public int Changes { get; set; }
    public int Conflicts { get; set; }
    public string? Operation { get; set; }
    public int Roots { get; set; }
    public DateTimeOffset? At { get; set; }
}

// MARK: - 运行与任务

public static class RunState
{
    public const string Preparing = "preparing";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopping = "stopping";
    public const string Exited = "exited";
    public const string Failed = "failed";

    /// <summary>还没结束</summary>
    public static bool IsActive(string? s) => s == Preparing || s == Starting || s == Running || s == Stopping;
}

public sealed record Run
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string ConfigId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Plugin { get; set; } = "";
    public string State { get; set; } = "";
    public int? Pid { get; set; }
    public string? Command { get; set; }
    public string? Cwd { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public bool StoppedByUser { get; set; }
    public string? Error { get; set; }
    public List<string> Urls { get; set; } = new();
    public List<int> Ports { get; set; } = new();
    public double Cpu { get; set; }
    public double RssMB { get; set; }
    /// <summary>本次运行的内存峰值</summary>
    public double PeakRSSMB { get; set; }
    /// <summary>进程树里的进程数</summary>
    public int Procs { get; set; }
    public List<Diagnosis> Diagnoses { get; set; } = new();
    public string? LogFile { get; set; }
    public int Lines { get; set; }

    public bool IsActive => RunState.IsActive(State);
}

public static class TaskState
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>model.Task</summary>
public sealed record TaskInfo
{
    public string Id { get; set; } = "";
    /// <summary>import | reimport | sync | install | toolchain | action | vcs | plugin</summary>
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? ModuleId { get; set; }
    public string State { get; set; } = "";
    public string? Message { get; set; }
    /// <summary>&lt;0 不确定</summary>
    public double Percent { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public JToken? Result { get; set; }
    public List<Diagnosis> Diagnoses { get; set; } = new();

    public bool IsRunning => State == TaskState.Running;
}

/// <summary>控制台的一行。Text 保留 ANSI 转义。</summary>
public sealed record LogLine
{
    public long Seq { get; set; }
    public long T { get; set; }
    /// <summary>stdout | stderr | system</summary>
    public string S { get; set; } = "";
    public string Text { get; set; } = "";
}

// MARK: - 工具链与插件

public sealed record ToolchainKind
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public bool Installable { get; set; }
    public string Plugin { get; set; } = "";
    public string Id => Kind;
}

public sealed record PluginInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Vendor { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Category { get; set; }
    public string? Homepage { get; set; }
    /// <summary>bundled | user | dev</summary>
    public string Source { get; set; } = "";
    public string Dir { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    public string? Error { get; set; }
    public List<Dependency> Dependencies { get; set; } = new();
    public string? BundledVersion { get; set; }
    public ContribSummary? Contributes { get; set; }
}

public sealed record ContribSummary
{
    public List<string> Facets { get; set; } = new();
    public List<string> Toolchains { get; set; } = new();
    public List<string> RunConfigTypes { get; set; } = new();
    public int Diagnostics { get; set; }
    public List<string> Views { get; set; } = new();
    public List<string> Actions { get; set; } = new();
    public List<string> Tools { get; set; } = new();
    public string? Vcs { get; set; }
}

public sealed record StoreEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Vendor { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public Engines? Engines { get; set; }
    public Dictionary<string, StorePlatform> Platforms { get; set; } = new();
    public string? Installed { get; set; }
    public bool Compatible { get; set; }
}

public sealed record StorePlatform
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long? Size { get; set; }
}

// MARK: - 界面贡献

public sealed record Contributions
{
    public List<FacetBadge> Facets { get; set; } = new();
    public List<ToolchainKind> ToolchainKinds { get; set; } = new();
    public List<RunConfigTypeInfo> RunConfigTypes { get; set; } = new();
    public List<ViewInfo> Views { get; set; } = new();
    public List<ActionInfo> Actions { get; set; } = new();
    public List<PluginSettingsInfo> Settings { get; set; } = new();
    public List<VCSInfo> Vcs { get; set; } = new();
    /// <summary>编辑器：语言着色、快捷键方案、配色主题（docs/editor.md 第 4 节）</summary>
    public List<LanguageInfo> Languages { get; set; } = new();
    public List<KeymapInfo> Keymaps { get; set; } = new();
    public List<EditorThemeInfo> EditorThemes { get; set; } = new();
    /// <summary>本平台默认的快捷键方案 id（Windows 上是 eclipse-windows）</summary>
    public string? DefaultKeymap { get; set; }

    /// <summary>挂在某位置的视图，按 (order, title) 排序</summary>
    public List<ViewInfo> ViewsAt(string location) => Views.Where(v => v.Location == location)
        .OrderBy(v => v.Order).ThenBy(v => v.Title, StringComparer.Ordinal).ToList();

    /// <summary>挂在某位置的动作；没写 locations 的动作默认挂 project.menu</summary>
    public List<ActionInfo> ActionsAt(string location) => Actions.Where(a =>
        a.Locations.Count == 0 ? location == ActionLocation.ProjectMenu : a.Locations.Contains(location)).ToList();

    public RunConfigTypeInfo? RunConfigType(string? plugin, string type) =>
        RunConfigTypes.FirstOrDefault(t => t.Id == type && (plugin == null || t.Plugin == plugin))
        ?? RunConfigTypes.FirstOrDefault(t => t.Id == type);

    public FacetBadge? Facet(string id) => Facets.FirstOrDefault(f => f.Id == id);
}

/// <summary>RunConfigTypeInfo：sdk.RunConfigTypeDef + plugin（平铺）</summary>
public sealed record RunConfigTypeInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
    public List<string> Facets { get; set; } = new();
    public List<FormField> Options { get; set; } = new();
    public string Plugin { get; set; } = "";
}

/// <summary>ViewInfo：sdk.ViewDef + plugin</summary>
public sealed record ViewInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Icon { get; set; }
    public string Location { get; set; } = "";
    public List<string> Facets { get; set; } = new();
    /// <summary>declarative | webview</summary>
    public string? Kind { get; set; }
    public string? Entry { get; set; }
    public int Order { get; set; }
    public string? RunState { get; set; }
    public string Plugin { get; set; } = "";

    /// <summary>全局唯一键</summary>
    public string Key => $"{Plugin}/{Id}";
    public bool IsWebview => Kind == "webview";
}

/// <summary>ActionInfo：sdk.ActionDef + plugin</summary>
public sealed record ActionInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Icon { get; set; }
    public List<string> Facets { get; set; } = new();
    public string? Confirm { get; set; }
    public string? Group { get; set; }
    public List<string> Locations { get; set; } = new();
    public List<FormField> Form { get; set; } = new();
    public string Plugin { get; set; } = "";

    public string Key => $"{Plugin}/{Id}";
}

public sealed record PluginSettingsInfo
{
    public string Plugin { get; set; } = "";
    public string Title { get; set; } = "";
    public List<FormField> Fields { get; set; } = new();
    public Dictionary<string, JToken> Values { get; set; } = new();
}

public sealed record VCSInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RootMarker { get; set; } = "";
    public string Plugin { get; set; } = "";
}

// MARK: - 设置

/// <summary>model.Settings</summary>
public sealed record AppSettings
{
    /// <summary>system | zh-Hans | en</summary>
    public string Locale { get; set; } = "";
    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "";
    /// <summary>auto | china | global</summary>
    public string MirrorRegion { get; set; } = "";
    public bool AutoInstallToolchains { get; set; }
    public bool AutoInstallDependencies { get; set; }
    /// <summary>导入后自动执行构建工具同步（如 Gradle）</summary>
    public bool AutoSyncOnImport { get; set; }
    public int PluginIdleSeconds { get; set; }
    public int LogLinesPerRun { get; set; }
    public int LogRetentionRuns { get; set; }
    public int ApiPort { get; set; }
    public string? StoreIndexUrl { get; set; }
    public int GitAutoFetchMinutes { get; set; }
    public string? Terminal { get; set; }
    public Dictionary<string, Dictionary<string, JToken>> PluginSettings { get; set; } = new();
    public List<string> DisabledPlugins { get; set; } = new();
    /// <summary>settings.editor（缺失时用 new EditorSettings() 的默认值）</summary>
    public EditorSettings? Editor { get; set; }
}

// MARK: - 事件

/// <summary>/api/v1/events 的事件类型</summary>
public static class EventType
{
    public const string Hello = "hello";
    public const string Workspaces = "workspaces.changed";
    public const string Project = "project.changed";
    public const string ProjectRemoved = "project.removed";
    public const string Run = "run.changed";
    public const string Task = "task.changed";
    public const string Toolchains = "toolchains.changed";
    public const string Plugins = "plugins.changed";
    public const string Settings = "settings.changed";
    public const string Notify = "notify";
    public const string Prompt = "prompt";
    public const string PromptDone = "prompt.done";
    public const string Vcs = "vcs.changed";
    public const string Stats = "stats";
    public const string Terminals = "terminals.changed";
}

/// <summary>一条事件；Data 先保留为 JToken，由使用方按 Type 解码（e.Decode&lt;Run&gt;()）。</summary>
public sealed record DaemonEvent
{
    public string Type { get; set; } = "";
    public JToken? Data { get; set; }

    /// <summary>解码 Data。Data 为 null 时：集合给空，其他抛出。</summary>
    public T Decode<T>() => Json.Decode<T>(Data);
}

/// <summary>需要界面回答的对话框（Go 里嵌入了 sdk.PromptParams，JSON 平铺）。</summary>
public sealed record Prompt
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Message { get; set; }
    public List<FormField> Fields { get; set; } = new();
    public string? OkText { get; set; }
    public bool Danger { get; set; }
    public string? Plugin { get; set; }
}

public sealed record SystemInfo
{
    public string Version { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
    public int Pid { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string DataDir { get; set; } = "";
    public string ApiBase { get; set; } = "";
    public string McpUrl { get; set; } = "";
    public string Executable { get; set; } = "";
    public double MemoryMB { get; set; }
    public int PluginProcs { get; set; }
    public double PluginMemoryMB { get; set; }
    public string Locale { get; set; } = "";
    public string MirrorRegion { get; set; } = "";
    /// <summary>可用插件数；0 表示安装不完整，任何工程都识别不了</summary>
    public int Plugins { get; set; }
    public List<string>? PluginErrors { get; set; }
    public List<string>? PluginDirs { get; set; }
}

// MARK: - 各操作的专用输出

/// <summary>project.scan 的一项</summary>
public sealed record ScanEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public List<FacetBadge> Facets { get; set; } = new();
    public bool Imported { get; set; }
    public string? ProjectId { get; set; }
    public string Id => Path;
}

/// <summary>runconfig.types 的一项</summary>
public sealed record RunConfigTypeChoice
{
    public RunConfigTypeInfo Type { get; set; } = new();
    public Dictionary<string, List<Option>> OptionValues { get; set; } = new();
}

/// <summary>run.logs 输出</summary>
public sealed record LogsResult
{
    public List<LogLine> Lines { get; set; } = new();
    public long LastSeq { get; set; }
    public long Total { get; set; }
    public bool Truncated { get; set; }
}

/// <summary>fix.apply 输出</summary>
public sealed record FixResult
{
    public string? Message { get; set; }
    public string? OpenUrl { get; set; }
    public Run? Rerun { get; set; }
    /// <summary>修复动作生成的任务（如安装依赖、下载工具链）</summary>
    public TaskInfo? Task { get; set; }
}

/// <summary>mirrors.status / mirrors.probe 输出</summary>
public sealed record MirrorStatus
{
    public string Configured { get; set; } = "";
    public string Effective { get; set; } = "";
    public List<MirrorProbe> Probes { get; set; } = new();
    public DateTimeOffset? At { get; set; }
}

public sealed record MirrorProbe
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public double? Ms { get; set; }
    public bool Ok { get; set; }
    public string Id => Name + "|" + Url;
}

/// <summary>mcp.config 输出</summary>
public sealed record MCPConfig
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    public List<string> StdioCommand { get; set; } = new();
    public string ClaudeCode { get; set; } = "";
    public JToken? ClaudeDesktop { get; set; }
}

public sealed record StopAllResult
{
    public int Stopped { get; set; }
}

/// <summary>launch.candidates 的一项：工程里能作为启动项运行的文件</summary>
public sealed record RunnableFile
{
    public string Path { get; set; } = "";
    /// <summary>script | binary | source</summary>
    public string Kind { get; set; } = "";
    public bool Exec { get; set; }
    public long Size { get; set; }
    /// <summary>已经在启动里</summary>
    public bool Launch { get; set; }
    public string Id => Path;

    /// <summary>文件名（路径用 / 分隔）</summary>
    public string Name
    {
        get
        {
            var p = Path.TrimEnd('/');
            var i = p.LastIndexOf('/');
            return i < 0 ? p : p.Substring(i + 1);
        }
    }

    /// <summary>所在目录（相对工程根，根目录为空）</summary>
    public string Directory
    {
        get
        {
            var p = Path.TrimEnd('/');
            var i = p.LastIndexOf('/');
            if (i < 0) return "";
            var d = i == 0 ? "/" : p.Substring(0, i);
            return d == "." ? "" : d;
        }
    }
}

public sealed record RunnableList
{
    public List<RunnableFile> Files { get; set; } = new();
    public bool Truncated { get; set; }
}

/// <summary>launch.addFile / launch.addCommand 的结果</summary>
public sealed record LaunchAddResult
{
    public string ConfigId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Created { get; set; }
    public List<string> Launch { get; set; } = new();
}

/// <summary>launch.add / launch.remove 的结果：该工程现在的启动项</summary>
public sealed record LaunchIDs
{
    public List<string> Launch { get; set; } = new();
}

/// <summary>launch.run / launch.stop 的结果</summary>
public sealed record LaunchResult
{
    public List<Run> Started { get; set; } = new();
    public int Stopped { get; set; }
    public int Skipped { get; set; }
    public List<LaunchRunError> Errors { get; set; } = new();
}

public sealed record LaunchRunError
{
    public string ProjectId { get; set; } = "";
    public string ConfigId { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed record PathResult
{
    public string Path { get; set; } = "";
}

/// <summary>交互终端（daemon 里伪终端中的 shell；terminal.* 接口与 terminals.changed 事件）</summary>
public sealed record TerminalInfo
{
    public string Id { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Shell { get; set; } = "";
    public int Pid { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public bool Exited { get; set; }
    public int ExitCode { get; set; }
    /// <summary>累计输出字节数</summary>
    public long Output { get; set; }
}

public sealed record TerminalBusy
{
    public bool Busy { get; set; }
    public string Process { get; set; } = "";
}

public sealed record PortKillResult
{
    public List<int> Pids { get; set; } = new();
}

/// <summary>运行时文件 runtime.json</summary>
public sealed record RuntimeInfo
{
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public int? Pid { get; set; }
    public string? Version { get; set; }
    public string? Executable { get; set; }
}

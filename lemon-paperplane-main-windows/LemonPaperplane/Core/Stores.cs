using System;
using System.Collections.Generic;
using System.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>工作区、工程摘要、设置、插件贡献等「较少变化」的数据（对应 Mac DataStore）。</summary>
public sealed class DataStore : PPObservable
{
    private List<Workspace> _workspaces = new();
    private Dictionary<string, ProjectSummary> _projects = new();
    private AppSettings? _settings;
    private Contributions _contributions = new();
    private List<PluginInfo> _plugins = new();
    private List<ToolchainKind> _toolchainKinds = new();
    private List<Toolchain> _toolchains = new();
    private bool _loaded;

    public List<Workspace> Workspaces { get => _workspaces; set { _workspaces = value; Notify(); } }
    /// <summary>工程摘要（id → 摘要）。整体替换或用 PutProject / RemoveProject 修改，不要直接改字典。</summary>
    public IReadOnlyDictionary<string, ProjectSummary> Projects => _projects;
    public AppSettings? Settings { get => _settings; set { _settings = value; Notify(); } }
    public Contributions Contributions { get => _contributions; set { _contributions = value; Notify(); } }
    public List<PluginInfo> Plugins { get => _plugins; set { _plugins = value; Notify(); } }
    public List<ToolchainKind> ToolchainKinds { get => _toolchainKinds; set { _toolchainKinds = value; Notify(); } }
    public List<Toolchain> Toolchains { get => _toolchains; set { _toolchains = value; Notify(); } }
    public bool Loaded { get => _loaded; set => Set(ref _loaded, value); }

    public Workspace? Workspace(string? id) => id == null ? null : _workspaces.FirstOrDefault(w => w.Id == id);

    public ProjectSummary? Project(string? id) => id != null && _projects.TryGetValue(id, out var p) ? p : null;

    public void SetProjects(IEnumerable<ProjectSummary> list)
    {
        _projects = new Dictionary<string, ProjectSummary>();
        foreach (var p in list) if (!_projects.ContainsKey(p.Id)) _projects[p.Id] = p;
        Notify(nameof(Projects));
    }

    public void PutProject(ProjectSummary p)
    {
        _projects[p.Id] = p;
        Notify(nameof(Projects));
    }

    public void RemoveProject(string id)
    {
        if (_projects.Remove(id)) Notify(nameof(Projects));
    }

    /// <summary>以内核的工作区列表为准：同时校正工程摘要里的 workspaceId（移动工程后摘要可能比工作区列表晚到）。</summary>
    public void ApplyWorkspaces(List<Workspace> ws)
    {
        _workspaces = ws;
        foreach (var w in ws)
            foreach (var id in w.ProjectIds)
                if (_projects.TryGetValue(id, out var p) && p.WorkspaceId != w.Id)
                    _projects[id] = p with { WorkspaceId = w.Id };
        Notify(nameof(Workspaces));
        Notify(nameof(Projects));
    }

    /// <summary>工作区里的工程（按工作区内顺序）。刚导入、工作区列表还没到的工程补在后面。</summary>
    public List<ProjectSummary> ProjectsIn(string workspaceId)
    {
        var ws = Workspace(workspaceId);
        if (ws == null) return new List<ProjectSummary>();
        var outList = ws.ProjectIds.Select(Project).Where(p => p != null).Select(p => p!).ToList();
        var listed = new HashSet<string>(_workspaces.SelectMany(w => w.ProjectIds));
        outList.AddRange(_projects.Values.Where(p => p.WorkspaceId == workspaceId && !listed.Contains(p.Id))
            .OrderBy(p => p.Name, StringComparer.Ordinal));
        return outList;
    }

    /// <summary>全部工程（按工作区顺序）。</summary>
    public List<ProjectSummary> AllProjects => _workspaces.SelectMany(w => ProjectsIn(w.Id)).ToList();

    public List<Toolchain> ToolchainsOf(string kind) => _toolchains.Where(t => t.Kind == kind).ToList();

    public string KindName(string kind) => _toolchainKinds.FirstOrDefault(k => k.Kind == kind)?.Name ?? kind;

    public PluginInfo? Plugin(string id) => _plugins.FirstOrDefault(p => p.Id == id);
}

/// <summary>运行与任务（变化频繁：stats 每 2 秒一次）。对应 Mac RunStore。</summary>
public sealed class RunStore : PPObservable
{
    private Dictionary<string, Run> _runs = new();
    private Dictionary<string, TaskInfo> _tasks = new();

    public IReadOnlyDictionary<string, Run> Runs => _runs;
    public IReadOnlyDictionary<string, TaskInfo> Tasks => _tasks;
    /// <summary>本界面发起的工具链下载任务：taskId → kind。</summary>
    public Dictionary<string, string> ToolchainTaskKind { get; } = new();

    public void SetRuns(IEnumerable<Run> list)
    {
        _runs = new Dictionary<string, Run>();
        foreach (var r in list) if (!_runs.ContainsKey(r.Id)) _runs[r.Id] = r;
        Notify(nameof(Runs));
    }

    public void PutRun(Run r)
    {
        _runs[r.Id] = r;
        Notify(nameof(Runs));
    }

    public void SetTasks(IEnumerable<TaskInfo> list)
    {
        _tasks = new Dictionary<string, TaskInfo>();
        foreach (var t in list) if (!_tasks.ContainsKey(t.Id)) _tasks[t.Id] = t;
        Notify(nameof(Tasks));
    }

    public void PutTask(TaskInfo t)
    {
        _tasks[t.Id] = t;
        Notify(nameof(Tasks));
    }

    public TaskInfo? Task(string id) => _tasks.TryGetValue(id, out var t) ? t : null;

    public List<Run> ActiveRuns => _runs.Values.Where(r => r.IsActive)
        .OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue).ToList();

    public List<TaskInfo> ActiveTasks => _tasks.Values.Where(t => t.IsRunning)
        .OrderByDescending(t => t.StartedAt ?? DateTimeOffset.MinValue).ToList();

    /// <summary>最近的任务（运行中在前，其后最近结束的若干个）。</summary>
    public List<TaskInfo> RecentTasks
    {
        get
        {
            var ended = _tasks.Values.Where(t => !t.IsRunning).OrderByDescending(t => t.EndedAt ?? DateTimeOffset.MinValue).Take(8);
            return ActiveTasks.Concat(ended).ToList();
        }
    }

    public Run? LatestRun(string projectId, string configId) =>
        _runs.Values.Where(r => r.ProjectId == projectId && r.ConfigId == configId)
            .OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue).FirstOrDefault();

    public List<Run> ActiveRunsOf(string projectId) => ActiveRuns.Where(r => r.ProjectId == projectId).ToList();

    public Run? ActiveRun(string projectId, string configId) =>
        ActiveRuns.FirstOrDefault(r => r.ProjectId == projectId && r.ConfigId == configId);

    public List<TaskInfo> TasksOf(string projectId) => ActiveTasks.Where(t => t.ProjectId == projectId).ToList();

    /// <summary>stats 事件：CPU/内存走 ResourceStore；这里只在地址或状态变了时才更新，避免每 2 秒让所有观察者重建。</summary>
    public void ApplyStats(IEnumerable<RunBrief> briefs)
    {
        var changed = false;
        foreach (var b in briefs)
        {
            if (!_runs.TryGetValue(b.Id, out var r)) continue;
            var urlsChanged = b.Urls.Count > 0 && !r.Urls.SequenceEqual(b.Urls);
            var stateChanged = !string.IsNullOrEmpty(b.State) && r.State != b.State;
            if (!urlsChanged && !stateChanged) continue;
            _runs[b.Id] = r with
            {
                Urls = urlsChanged ? b.Urls : r.Urls,
                State = stateChanged ? b.State : r.State,
                Cpu = b.Cpu,
                RssMB = b.RssMB,
            };
            changed = true;
        }
        if (changed) Notify(nameof(Runs));
    }

    /// <summary>只保留进行中的和最近 50 个结束的运行、30 个结束的任务，防止长期运行后无限增长。</summary>
    public void Prune()
    {
        var ended = _runs.Values.Where(r => !r.IsActive).OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue).Skip(50).ToList();
        foreach (var r in ended) _runs.Remove(r.Id);
        var done = _tasks.Values.Where(t => !t.IsRunning).OrderByDescending(t => t.EndedAt ?? DateTimeOffset.MinValue).Skip(30).ToList();
        foreach (var t in done) _tasks.Remove(t.Id);
        if (ended.Count > 0) Notify(nameof(Runs));
        if (done.Count > 0) Notify(nameof(Tasks));
    }
}

/// <summary>页面（对应 Mac NavStore.Page）。值相等比较。</summary>
public sealed record NavPage(string Kind, string? Id = null)
{
    public const string KindWorkspace = "workspace";
    public const string KindRunning = "running";
    public const string KindToolchains = "toolchains";
    public const string KindPlugins = "plugins";
    public const string KindSettings = "settings";
    public const string KindPluginView = "pluginView";

    public static NavPage WorkspacePage(string id) => new(KindWorkspace, id);
    public static readonly NavPage Running = new(KindRunning);
    public static readonly NavPage ToolchainsPage = new(KindToolchains);
    public static readonly NavPage PluginsPage = new(KindPlugins);
    public static readonly NavPage SettingsPage = new(KindSettings);
    public static NavPage PluginView(string key) => new(KindPluginView, key);

    public bool IsWorkspace => Kind == KindWorkspace;
}

/// <summary>工程详情的标签（对应 Mac NavStore.ProjectTab）："overview" "run" "files" "vcs" "config"，插件标签是 "plugin:" + ViewInfo.Key。</summary>
public static class ProjectTab
{
    public const string Overview = "overview";
    public const string Run = "run";
    public const string Files = "files";
    public const string Vcs = "vcs";
    public const string Config = "config";
    public static string Plugin(string viewKey) => "plugin:" + viewKey;
}

/// <summary>页面导航与界面状态（对应 Mac NavStore）。</summary>
public sealed class NavStore : PPObservable
{
    private NavPage _page = NavPage.Running;
    private string? _openProjectId;
    private string _projectTab = ProjectTab.Overview;
    private string _search = "";

    public NavPage Page
    {
        get => _page;
        set
        {
            if (value.IsWorkspace && value.Id != null) UiPrefs.Set("LastWorkspace", value.Id);
            var changed = _page != value;
            _page = value;
            _openProjectId = null;
            if (changed) Notify();
            Notify(nameof(OpenProjectId));
        }
    }

    public string? OpenProjectId { get => _openProjectId; set => Set(ref _openProjectId, value); }
    public string ProjectTabSelection { get => _projectTab; set => Set(ref _projectTab, value); }
    public string Search { get => _search; set => Set(ref _search, value); }

    /// <summary>运行页里每个工程选中的运行配置。</summary>
    public Dictionary<string, string> SelectedConfig { get; } = new();

    public void SelectConfig(string projectId, string configId)
    {
        SelectedConfig[projectId] = configId;
        Notify(nameof(SelectedConfig));
    }

    /// <summary>Ctrl+F：工具栏搜索框或控制台搜索。</summary>
    public event EventHandler? FocusSearch;
    public event EventHandler? ConsoleFind;
    public void RequestFocusSearch() => FocusSearch?.Invoke(this, EventArgs.Empty);
    public void RequestConsoleFind() => ConsoleFind?.Invoke(this, EventArgs.Empty);

    /// <summary>当前可见的控制台数量（Ctrl+F 判断去向）。控制台出现时加一、消失时减一。</summary>
    public int VisibleConsoles { get; set; }

    public string? LastWorkspaceId => UiPrefs.Get("LastWorkspace");

    public string? CurrentWorkspaceId => _page.IsWorkspace ? _page.Id : null;

    public void OpenProject(string id, string tab = ProjectTab.Overview)
    {
        _projectTab = tab;
        _openProjectId = id;
        Notify(nameof(ProjectTabSelection));
        Notify(nameof(OpenProjectId));
    }

    public void OpenRunConsole(string projectId, string configId)
    {
        SelectedConfig[projectId] = configId;
        OpenProject(projectId, ProjectTab.Run);
    }
}

/// <summary>事件总线：给只关心某类事件的视图（版本控制、工程详情、工具链页）用。回调都在 UI 线程。</summary>
public sealed class EventBus
{
    /// <summary>project.changed（工程 id）。</summary>
    public event Action<string>? ProjectChanged;
    public event Action<VCSChangedEvent>? VcsChanged;
    public event Action? ToolchainsChanged;
    public event Action? PluginsChanged;
    /// <summary>任何原始事件。</summary>
    public event Action<DaemonEvent>? Raw;

    internal void RaiseProjectChanged(string id) => ProjectChanged?.Invoke(id);
    internal void RaiseVcsChanged(VCSChangedEvent e) => VcsChanged?.Invoke(e);
    internal void RaiseToolchainsChanged() => ToolchainsChanged?.Invoke();
    internal void RaisePluginsChanged() => PluginsChanged?.Invoke();
    internal void RaiseRaw(DaemonEvent e) => Raw?.Invoke(e);
}

/// <summary>交互终端（terminals.changed 事件整体替换）。终端在后台服务里，界面关掉再打开还在。</summary>
public sealed class TerminalStore : PPObservable
{
    private List<TerminalInfo> _list = new();

    public List<TerminalInfo> List { get => _list; set { _list = value; Notify(); } }

    /// <summary>每个工作区当前显示的终端。</summary>
    public Dictionary<string, string> Selected { get; } = new();

    public void Select(string workspaceId, string terminalId)
    {
        Selected[workspaceId] = terminalId;
        Notify(nameof(Selected));
    }

    public List<TerminalInfo> In(string workspaceId) => _list.Where(t => t.WorkspaceId == workspaceId).ToList();

    /// <summary>工作区当前显示的终端：记住的那个还在就用它，否则第一个。</summary>
    public TerminalInfo? Current(string workspaceId)
    {
        var l = In(workspaceId);
        if (Selected.TryGetValue(workspaceId, out var id))
        {
            var t = l.FirstOrDefault(x => x.Id == id);
            if (t != null) return t;
        }
        return l.FirstOrDefault();
    }

    public void Upsert(TerminalInfo t)
    {
        var i = _list.FindIndex(x => x.Id == t.Id);
        if (i >= 0) _list[i] = t; else _list.Add(t);
        Notify(nameof(List));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>连接状态（对应 Mac AppModel.Connection）。</summary>
public sealed record ConnectionState(string Kind, string? Message = null)
{
    public static readonly ConnectionState Connecting = new("connecting");
    public static readonly ConnectionState Connected = new("connected");
    public static readonly ConnectionState Reconnecting = new("reconnecting");
    public static ConnectionState Failed(string message) => new("failed", message);
    public bool IsFailed => Kind == "failed";
    public bool IsConnected => Kind == "connected";
}

/// <summary>界面的根状态：连接内核、订阅事件流、分发到各个 store（对应 Mac AppModel）。只在 UI 线程使用。</summary>
public sealed class AppModel : PPObservable
{
    public static readonly AppModel Shared = new();

    private ConnectionState _connection = ConnectionState.Connecting;
    private SystemInfo? _systemInfo;
    private ApiClient? _api;
    private readonly DaemonManager _daemon = new();
    private EventStream? _events;
    /// <summary>本界面发起的任务：结束时给出提示。</summary>
    private readonly HashSet<string> _trackedTasks = new();
    private bool _started;
    private bool _recovering;

    public ConnectionState Connection { get => _connection; private set => Set(ref _connection, value); }
    public SystemInfo? SystemInfo { get => _systemInfo; private set => Set(ref _systemInfo, value); }
    public ApiClient? Api => _api;

    public DataStore Data { get; } = new();
    public RunStore Runs { get; } = new();
    public TerminalStore Terminals { get; } = new();
    public NavStore Nav { get; } = new();
    public DialogStore Dialogs => DialogStore.Shared;
    public ToastStore Toasts => ToastStore.Shared;
    public EventBus Bus { get; } = new();

    private AppModel() { }

    private static Dispatcher UI => Application.Current.Dispatcher;

    // ---------------------------------------------------------------------------
    // 连接

    public void Start()
    {
        if (_started) return;
        _started = true;
        Theme.Apply(UiPrefs.Get("Theme") ?? "system");
        IconStore.Shared.PluginLoader = async (id, path) => await Client().PluginFileAsync(id, path).ConfigureAwait(false);
        Connect();
    }

    public async void Connect()
    {
        Connection = ConnectionState.Connecting;
        try
        {
            var (client, info) = await _daemon.ConnectAsync(L10n.Shared.SystemLocale);
            _api = client;
            SystemInfo = info;
            Connection = ConnectionState.Connected;
            if (info.Plugins == 0) Toasts.Show("danger", L.T("app.noPlugins"), info.Executable);
            StartEvents(client);
            await RefreshAll();
        }
        catch (Exception e)
        {
            Connection = ConnectionState.Failed(e.Message);
        }
    }

    /// <summary>需要已连接的接口；没连上时抛出可展示的错误。</summary>
    public ApiClient Client() =>
        _api ?? throw new ApiException(ApiException.CodeNetwork, L.T("connection.notConnected"));

    private void StartEvents(ApiClient client)
    {
        _events?.Stop();
        var es = new EventStream();
        _events = es;
        es.Start(client, u => UI.BeginInvoke(new Action(() => { if (_events == es) Handle(u); })));
    }

    private void Handle(EventStream.Update update)
    {
        switch (update)
        {
            case EventStream.Update.Connected:
                if (!Connection.IsConnected) Connection = ConnectionState.Connected;
                break;
            case EventStream.Update.Disconnected:
                if (Connection.IsConnected) Connection = ConnectionState.Reconnecting;
                ScheduleRecovery();
                break;
            case EventStream.Update.Event ev:
                HandleEvent(ev.Value);
                break;
        }
    }

    /// <summary>事件流断开 2 秒后仍没恢复：重新读 runtime.json（内核可能换了端口 / 令牌），必要时重新拉起内核。</summary>
    private async void ScheduleRecovery()
    {
        if (_recovering) return;
        _recovering = true;
        try
        {
            await Task.Delay(2000);
            if (Connection.IsConnected) return;
            (ApiClient Client, SystemInfo Info) r;
            try { r = await _daemon.ConnectAsync(L10n.Shared.SystemLocale); }
            catch { return; }
            if (_api == null || r.Client.BaseUrl != _api.BaseUrl || r.Client.Token != _api.Token)
            {
                _api = r.Client;
                SystemInfo = r.Info;
                StartEvents(r.Client);
            }
        }
        finally { _recovering = false; }
    }

    /// <summary>重新拉取全部状态（启动、重连后的 hello）。</summary>
    public async Task RefreshAll()
    {
        if (_api is not { } api) return;
        var ws = Try(api.WorkspacesAsync());
        var ps = Try(api.ProjectsAsync());
        var rs = Try(api.RunsAsync());
        var ts = Try(api.TasksAsync());
        var st = Try(api.SettingsAsync());
        var cs = Try(api.ContributionsAsync());
        var pl = Try(api.PluginsAsync());
        var kinds = Try(api.ToolchainKindsAsync());
        var tcs = Try(api.ToolchainsAsync());
        var prompts = Try(api.PendingPromptsAsync());
        var terms = Try(api.TerminalListAsync());

        if (await ps is { } p) Data.SetProjects(p);
        if (await ws is { } w) Data.ApplyWorkspaces(w);
        if (await rs is { } r) Runs.SetRuns(r);
        if (await ts is { } t) Runs.SetTasks(t);
        if (await st is { } s) ApplySettings(s);
        if (await cs is { } c) Data.Contributions = c;
        if (await pl is { } plugins) SetPlugins(plugins);
        if (await kinds is { } k) Data.ToolchainKinds = k;
        if (await tcs is { } tc) Data.Toolchains = tc;
        if (await prompts is { } pr) foreach (var x in pr) PromptPresenter.Present(x);
        if (await terms is { } tm) Terminals.List = tm;
        Data.Loaded = true;
        FixNavigation();
    }

    private static async Task<T?> Try<T>(Task<T> task) where T : class
    {
        try { return await task; }
        catch { return null; }
    }

    /// <summary>启动后选一个合理的页面。</summary>
    private void FixNavigation()
    {
        var page = Nav.Page;
        if (page.IsWorkspace && Data.Workspace(page.Id) != null) return;
        if (page.Kind is NavPage.KindToolchains or NavPage.KindPlugins or NavPage.KindSettings or NavPage.KindPluginView) return;
        if (Nav.LastWorkspaceId is { } last && Data.Workspace(last) != null) Nav.Page = NavPage.WorkspacePage(last);
        else if (Data.Workspaces.FirstOrDefault() is { } first) Nav.Page = NavPage.WorkspacePage(first.Id);
    }

    private void SetPlugins(List<PluginInfo> list)
    {
        Data.Plugins = list;
        IconStore.Shared.PluginVersions.Clear();
        foreach (var p in list) IconStore.Shared.PluginVersions[p.Id] = p.Version;
    }

    public void ApplySettings(AppSettings s)
    {
        Data.Settings = s;
        L10n.Shared.Apply(s.Locale);
        UiPrefs.Set("Theme", s.Theme);
        Theme.Apply(s.Theme);
    }

    public void UpdateSystemInfo(SystemInfo i) => SystemInfo = i;

    public async Task ReloadToolchains()
    {
        if (_api is not { } api) return;
        if (await Try(api.ToolchainsAsync()) is { } t) Data.Toolchains = t;
        if (await Try(api.ToolchainKindsAsync()) is { } k) Data.ToolchainKinds = k;
    }

    public async Task ReloadPlugins()
    {
        if (_api is not { } api) return;
        if (await Try(api.PluginsAsync()) is { } pl) SetPlugins(pl);
        if (await Try(api.ContributionsAsync()) is { } cs) Data.Contributions = cs;
        if (await Try(api.ToolchainKindsAsync()) is { } k) Data.ToolchainKinds = k;
    }

    // ---------------------------------------------------------------------------
    // 事件分发

    private void HandleEvent(DaemonEvent e)
    {
        Bus.RaiseRaw(e);
        try
        {
            switch (e.Type)
            {
                case EventType.Hello:
                    SystemInfo = e.Decode<SystemInfo>();
                    Connection = ConnectionState.Connected;
                    _ = RefreshAll();
                    break;
                case EventType.Workspaces:
                    Data.ApplyWorkspaces(e.Decode<List<Workspace>>());
                    break;
                case EventType.Project:
                {
                    var p = e.Decode<ProjectSummary>();
                    Data.PutProject(p);
                    Bus.RaiseProjectChanged(p.Id);
                    break;
                }
                case EventType.ProjectRemoved:
                {
                    var id = e.Data.Get("id").StringValue();
                    if (id == null) break;
                    Data.RemoveProject(id);
                    if (Nav.OpenProjectId == id) Nav.OpenProjectId = null;
                    EditorWindows.Shared.RequestClose(id);
                    break;
                }
                case EventType.Run:
                {
                    var r = e.Decode<Run>();
                    Runs.PutRun(r);
                    ResourceStore.Shared.RunChanged(r);
                    if (!r.IsActive)
                    {
                        Runs.Prune();
                        ResourceStore.Shared.Prune(new HashSet<string>(Runs.Runs.Keys));
                    }
                    break;
                }
                case EventType.Task:
                    TaskChanged(e.Decode<TaskInfo>());
                    break;
                case EventType.Toolchains:
                    Bus.RaiseToolchainsChanged();
                    _ = ReloadToolchains();
                    break;
                case EventType.Plugins:
                    IconStore.Shared.ClearPluginIcons();
                    Bus.RaisePluginsChanged();
                    _ = ReloadPlugins();
                    break;
                case EventType.Settings:
                    ApplySettings(e.Decode<AppSettings>());
                    break;
                case EventType.Notify:
                {
                    var n = e.Decode<NotifyParams>();
                    Toasts.Show(n.Level, n.Title, n.Message);
                    break;
                }
                case EventType.Prompt:
                    PromptPresenter.Present(e.Decode<Prompt>());
                    break;
                case EventType.PromptDone:
                    if (e.Data.Get("id").StringValue() is { } pid) PromptPresenter.Dismiss(pid);
                    break;
                case EventType.Vcs:
                    Bus.RaiseVcsChanged(e.Decode<VCSChangedEvent>());
                    break;
                case EventType.Stats:
                    ApplyStats(e.Decode<List<RunBrief>>());
                    break;
                case EventType.Terminals:
                    Terminals.List = e.Decode<List<TerminalInfo>>();
                    break;
            }
        }
        catch (Exception ex)
        {
            DebugBridge.LogError(ex);
        }
    }

    /// <summary>
    /// stats 每 2 秒一次：数值与趋势进 ResourceStore（每个运行一个可观察对象，只重建对应的小视图），
    /// 工程摘要只在地址变化时更新，不每 2 秒重建卡片网格。
    /// </summary>
    private void ApplyStats(List<RunBrief> briefs)
    {
        ResourceStore.Shared.Apply(briefs);
        Runs.ApplyStats(briefs);
        var byId = briefs.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var p in Data.Projects.Values.ToList())
        {
            var changed = p.Runs.Any(r => byId.TryGetValue(r.Id, out var b) && b.Urls.Count > 0 && !b.Urls.SequenceEqual(r.Urls));
            if (!changed) continue;
            var runs = p.Runs.Select(r => byId.TryGetValue(r.Id, out var b) && b.Urls.Count > 0 ? r with { Urls = b.Urls } : r).ToList();
            Data.PutProject(p with { Runs = runs });
        }
    }

    private void TaskChanged(TaskInfo t)
    {
        Runs.PutTask(t);
        if (t.IsRunning || !_trackedTasks.Remove(t.Id)) return;
        switch (t.State)
        {
            case TaskState.Succeeded:
            {
                var msg = t.Result.Get("message").StringValue();
                var url = t.Result.Get("openUrl").StringValue();
                if (!string.IsNullOrEmpty(url)) Actions.OpenURL(url!);
                if (!string.IsNullOrEmpty(msg)) Toasts.Show("success", t.Title, msg);
                if (t.Result.Get("reimport").BoolValue() == true && t.ProjectId != null) Actions.Reimport(t.ProjectId);
                else if (t.Result.Get("sync").BoolValue() == true && t.ProjectId != null) Actions.Sync(t.ProjectId, t.ModuleId);
                break;
            }
            case TaskState.Failed:
                Toasts.Show("error", t.Title, t.Error, L.T("task.viewLog"), () => Actions.ShowTaskLog(t.Id));
                break;
        }
        Runs.Prune();
    }

    /// <summary>记下本界面发起的任务，结束时提示。</summary>
    public void Track(TaskInfo task)
    {
        Runs.PutTask(task);
        _trackedTasks.Add(task.Id);
    }

    // ---------------------------------------------------------------------------
    // 通用执行

    /// <summary>执行一个接口调用，失败时弹提示。</summary>
    public async void Perform(string? title, Func<ApiClient, Task> op)
    {
        try { await op(Client()); }
        catch (Exception e) { Toasts.Error(e, title); }
    }

    public void Perform(Func<ApiClient, Task> op) => Perform(null, op);

    // ---------------------------------------------------------------------------
    // 退出

    /// <summary>退出界面；stopServices 时同时让内核停止全部服务并退出。</summary>
    public async void Quit(bool stopServices)
    {
        // 编辑窗口有未保存的文件：先让它们提示，不退出也不停服务
        if (!EditorWindows.Shared.PrepareForQuit())
        {
            Toasts.Show("warning", L.T("editorWindow.unsaved"));
            return;
        }
        _events?.Stop();
        if (stopServices && _api is { } api)
        {
            try { await api.ShutdownAsync(true); } catch { }
        }
        Tray.Shared.Dispose();
        if (MainWindow.Current is { } w) w.HideOnClose = null; // 退出时真的关掉窗口
        Application.Current.Shutdown();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>连接状态（对应 Mac AppModel.Connection）。Remote = 连不上的是远程服务器（Code 是网关错误码，"local" 表示本机内核没连上）。</summary>
public sealed record ConnectionState(string Kind, string? Message = null, string? Code = null, bool Remote = false)
{
    public static readonly ConnectionState Connecting = new("connecting");
    public static readonly ConnectionState Connected = new("connected");
    public static readonly ConnectionState Reconnecting = new("reconnecting");
    public static ConnectionState Failed(string message) => new("failed", message);
    public static ConnectionState RemoteFailed(string message, string? code) => new("failed", message, code, true);
    public bool IsFailed => Kind == "failed";
    public bool IsConnected => Kind == "connected";
    /// <summary>本机内核没连上（连远程也要经本机内核的网关）</summary>
    public const string CodeLocal = "local";
}

/// <summary>界面的根状态：连接内核、订阅事件流、分发到各个 store（对应 Mac AppModel）。只在 UI 线程使用。</summary>
public sealed class AppModel : PPObservable
{
    public static readonly AppModel Shared = new();

    private ConnectionState _connection = ConnectionState.Connecting;
    private SystemInfo? _systemInfo;
    /// <summary>当前服务器的客户端：本机时就是本机客户端，远程时是网关回环端口的客户端。</summary>
    private ApiClient? _api;
    /// <summary>本机内核的客户端（远程时也保留：gateway.* 只能在本机调）。</summary>
    private ApiClient? _localApi;
    private SystemInfo? _localInfo;
    private AppSettings? _localSettings;
    private ServerRecord? _server;
    private string? _gatewayId;
    /// <summary>每台服务器打开过的网关（服务器 id → 网关 id）：删除、重新登录时关掉。</summary>
    private readonly Dictionary<string, string> _gateways = new();
    /// <summary>每次连接 / 切换加一：异步回来时发现已经换了就丢掉结果。</summary>
    private int _connectGen;
    private readonly DaemonManager _daemon = new();
    private EventStream? _events;
    /// <summary>本界面发起的任务：结束时给出提示。</summary>
    private readonly HashSet<string> _trackedTasks = new();
    private bool _started;
    private bool _recovering;

    public ConnectionState Connection { get => _connection; private set => Set(ref _connection, value); }
    /// <summary>当前服务器的系统信息（远程时是那台服务器的）。</summary>
    public SystemInfo? SystemInfo { get => _systemInfo; private set { Set(ref _systemInfo, value); ApplyPathRules(value); } }
    public ApiClient? Api => _api;

    /// <summary>当前服务器：null = 本机。</summary>
    public ServerRecord? Server { get => _server; private set => Set(ref _server, value); }
    public bool IsRemote => _server != null;
    /// <summary>当前服务器的显示名（本机 / 记录的名称）。</summary>
    public string ServerName => _server?.Name ?? L.T("server.local");
    /// <summary>本机内核（远程时也在：网关、端口转发、退出时停本机服务都用它）。</summary>
    public ApiClient? LocalApi => _localApi;
    public SystemInfo? LocalInfo { get => _localInfo; private set => Set(ref _localInfo, value); }
    /// <summary>当前远程服务器的网关 id（gateway.forward 用）。</summary>
    public string? GatewayId => _gatewayId;
    /// <summary>当前服务器的路径分隔符（拼服务器路径用它，不用本机的 System.IO.Path）。</summary>
    public string PathSeparator => _systemInfo?.Separator ?? "\\";

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
        // 记住的服务器：启动时自动连
        Server = ServerStore.Shared.Remembered;
        // 调试桥：{"cmd":"server"} 当前服务器与连接状态；{"cmd":"server","switch":"<id>|local"} 切换
        DebugBridge.Extra["server"] = r =>
        {
            if ((string?)r["switch"] is { } sw) SwitchTo(sw == "local" ? null : ServerStore.Shared.Find(sw));
            return Task.FromResult(new JObject
            {
                ["server"] = _server?.Id ?? "local",
                ["name"] = ServerName,
                ["connection"] = Connection.Kind,
                ["code"] = Connection.Code,
                ["message"] = Connection.Message,
                ["gateway"] = _gatewayId,
                ["api"] = _api?.BaseUrl.ToString(),
                ["local"] = _localApi?.BaseUrl.ToString(),
                ["sep"] = PathSeparator,
                ["servers"] = new JArray(ServerStore.Shared.Servers.Select(x => new JObject { ["id"] = x.Id, ["name"] = x.Name, ["url"] = x.Url, ["os"] = x.Os })),
                ["projects"] = Data.Projects.Count,
                ["runs"] = Runs.ActiveRuns.Count,
            });
        };
        // 任何经网关的调用被服务器拒绝了访问密钥：进入「连不上」并停止重连（事件流不一定会断）
        ApiClient.RemoteRejected += (client, e) => UI.BeginInvoke(new Action(() =>
        {
            if (!IsRemote || !ReferenceEquals(client, _api) || Connection.IsFailed) return;
            ShowRemoteError(e);
        }));
        // 调试桥：{"cmd":"server.forward","port":N} 只做端口转发、返回本机地址（不打开浏览器）
        DebugBridge.Extra["server.forward"] = async r =>
        {
            if (_localApi is not { } local || _gatewayId is not { } gid) throw new InvalidOperationException("not remote");
            var f = await local.GatewayForwardAsync(gid, (int?)r["port"] ?? 0);
            return new JObject { ["localPort"] = f.LocalPort, ["url"] = f.Url };
        };
        Connect();
    }

    /// <summary>
    /// 连接当前服务器：先连本机内核（必要时拉起；远程也要经本机内核的网关），远程时再 gateway.open，
    /// 用返回的回环端口和令牌建客户端（docs/remote.md 5.1）。重试也调它。
    /// </summary>
    public async void Connect()
    {
        var gen = ++_connectGen;
        _events?.Stop();
        _events = null;
        Connection = ConnectionState.Connecting;
        ApiClient local;
        SystemInfo localInfo;
        try
        {
            (local, localInfo) = await _daemon.ConnectAsync(L10n.Shared.SystemLocale);
        }
        catch (Exception e)
        {
            if (gen != _connectGen) return;
            Connection = _server == null ? ConnectionState.Failed(e.Message)
                : ConnectionState.RemoteFailed(L.T("server.localRequired") + "\n" + e.Message, ConnectionState.CodeLocal);
            return;
        }
        if (gen != _connectGen) return;
        _localApi = local;
        LocalInfo = localInfo;
        if (_server is { } s)
        {
            _ = LoadLocalSettings(local);
            await OpenRemote(s, gen);
            return;
        }
        if (localInfo.Plugins == 0) Toasts.Show("danger", L.T("app.noPlugins"), localInfo.Executable);
        Attach(local, localInfo);
    }

    /// <summary>用这个客户端作为当前服务器：订阅事件流、拉取全部状态。</summary>
    private void Attach(ApiClient client, SystemInfo info)
    {
        _api = client;
        SystemInfo = info;
        Connection = ConnectionState.Connected;
        StartEvents(client);
        _ = RefreshAll();
    }

    /// <summary>远程时界面语言、主题仍按本机的设置（它们是这台电脑上界面的偏好）。</summary>
    private async Task LoadLocalSettings(ApiClient local)
    {
        try { _localSettings = await local.SettingsAsync(); }
        catch { }
    }

    /// <summary>在本机内核上打开到远程服务器的网关，并换用它。</summary>
    private async Task OpenRemote(ServerRecord s, int gen)
    {
        var token = ServerStore.Shared.Secret(s.Id);
        if (string.IsNullOrEmpty(token))
        {
            Connection = ConnectionState.RemoteFailed(L.T("server.err.unauthorized"), RemoteErrorCode.Unauthorized);
            return;
        }
        try
        {
            var r = await _localApi!.GatewayOpenAsync(s.Url, s.Fingerprint, s.Trusted, token!);
            if (gen != _connectGen) return;
            _gatewayId = r.Id;
            _gateways[s.Id] = r.Id;
            // 记录里的系统、主机名跟着服务器走（服务器重装或改了主机名）
            if (r.Info.Os.Length > 0 && (r.Info.Os != s.Os || r.Info.Hostname != s.Hostname))
            {
                var n = s with { Os = r.Info.Os, Hostname = r.Info.Hostname };
                ServerStore.Shared.Put(n);
                Server = n;
            }
            Attach(new ApiClient(new Uri("http://127.0.0.1:" + r.Port), r.Token), r.Info);
        }
        catch (Exception e)
        {
            if (gen != _connectGen) return;
            Connection = ConnectionState.RemoteFailed(ServerDisplay.ErrorMessage(e), (e as ApiException)?.Code);
        }
    }

    /// <summary>
    /// 切换服务器（null = 本机）：清空当前数据、重装主窗口内容，再连接（docs/remote.md 5.1）。
    /// 编辑窗口有未保存的文件时不切换（先让它们提示）。
    /// </summary>
    public void SwitchTo(ServerRecord? s)
    {
        if (s?.Id == _server?.Id && !Connection.IsFailed) return;
        if (!EditorWindows.Shared.PrepareForQuit())
        {
            Toasts.Show("warning", L.T("editorWindow.unsaved"));
            return;
        }
        ServerStore.Shared.Remember(s);
        Reload(s);
    }

    /// <summary>换成这台服务器（或用新密钥重连同一台）：停掉旧的事件流、清空数据、重装主窗口内容，再连接。</summary>
    private void Reload(ServerRecord? s)
    {
        _connectGen++;
        _events?.Stop();
        _events = null;
        _api = null;
        _gatewayId = null;
        Server = s;
        ResetData();
        if (MainWindow.Current is { } w) MainView.Install(w);
        Connect();
    }

    /// <summary>换服务器前清空上一台的数据（工程、运行、任务、终端、导航、对话框、插件图标）。</summary>
    private void ResetData()
    {
        Data.Loaded = false;
        Data.SetProjects(Array.Empty<ProjectSummary>());
        Data.Workspaces = new List<Workspace>();
        Data.Contributions = new Contributions();
        Data.Plugins = new List<PluginInfo>();
        Data.ToolchainKinds = new List<ToolchainKind>();
        Data.Toolchains = new List<Toolchain>();
        Data.Settings = null;
        Runs.SetRuns(Array.Empty<Run>());
        Runs.SetTasks(Array.Empty<TaskInfo>());
        Runs.ToolchainTaskKind.Clear();
        Terminals.List = new List<TerminalInfo>();
        Terminals.Selected.Clear();
        Nav.SelectedConfig.Clear();
        Nav.Search = "";
        Nav.OpenProjectId = null;
        // 工作区、插件视图属于上一台：回到运行中（设置、工具链、插件页留在原处，同 Mac）
        if (Nav.Page.Kind is NavPage.KindWorkspace or NavPage.KindPluginView) Nav.Page = NavPage.Running;
        _trackedTasks.Clear();
        ResourceStore.Shared.Prune(new HashSet<string>());
        IconStore.Shared.ClearPluginIcons();
        IconStore.Shared.PluginVersions.Clear();
        foreach (var d in Dialogs.Stack.ToList()) Dialogs.Dismiss(d.Id);
        PopupController.DismissAll();
    }

    /// <summary>服务器记录改了（重命名、重新登录）：更新当前的那份。</summary>
    public void ServerUpdated(ServerRecord s)
    {
        if (_server?.Id == s.Id) Server = s;
    }

    /// <summary>删除服务器记录前：关掉它的网关；正在用它时切回本机。</summary>
    public void ServerRemoved(string id)
    {
        CloseGateway(id);
        if (_server?.Id == id) SwitchTo(null);
    }

    /// <summary>重新登录拿到新密钥后：关掉旧网关；正在用它时换新网关重新连接（旧网关端口上的页面、编辑窗口都要换掉）。</summary>
    public void ServerRelogged(ServerRecord s)
    {
        CloseGateway(s.Id);
        if (_server?.Id != s.Id) return;
        if (!EditorWindows.Shared.PrepareForQuit())
        {
            // 编辑窗口有未保存的文件：先只重连，不重装界面
            Server = s;
            Connect();
            return;
        }
        Reload(s);
    }

    private void CloseGateway(string serverId)
    {
        if (!_gateways.TryGetValue(serverId, out var gid)) return;
        _gateways.Remove(serverId);
        if (_gatewayId == gid) _gatewayId = null;
        if (_localApi is { } local) Fire(local.GatewayCloseAsync(gid));
    }

    private static async void Fire(Task t)
    {
        try { await t; }
        catch { }
    }

    /// <summary>端口转发：把服务器 localhost:port 转到本机后打开（保留原地址的路径）。</summary>
    public async void OpenForwarded(string url, int port)
    {
        try
        {
            if (_localApi is not { } local || _gatewayId is not { } gid) throw new ApiException(ApiException.CodeNetwork, L.T("connection.notConnected"));
            var r = await local.GatewayForwardAsync(gid, port);
            Actions.OpenURL(RunAddress.WithPort(url, "localhost", r.LocalPort));
        }
        catch (Exception e)
        {
            Toasts.Error(e, L.T("remote.forward.failed"));
        }
    }

    private void ApplyPathRules(SystemInfo? info)
    {
        if (info == null) return;
        Format.ServerSeparator = info.Separator;
        Format.ServerHome = string.IsNullOrEmpty(info.Home) ? null : info.Home;
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
            case EventStream.Update.Disconnected d:
                if (Connection.IsConnected) Connection = ConnectionState.Reconnecting;
                if (IsRemote) HandleRemoteDisconnect(d.Reason);
                else ScheduleRecovery();
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
        var gen = _connectGen;
        try
        {
            await Task.Delay(2000);
            if (Connection.IsConnected || gen != _connectGen || IsRemote) return;
            (ApiClient Client, SystemInfo Info) r;
            try { r = await _daemon.ConnectAsync(L10n.Shared.SystemLocale); }
            catch { return; }
            if (gen != _connectGen || IsRemote) return;
            _localApi = r.Client;
            LocalInfo = r.Info;
            if (_api == null || r.Client.BaseUrl != _api.BaseUrl || r.Client.Token != _api.Token)
            {
                _api = r.Client;
                SystemInfo = r.Info;
                StartEvents(r.Client);
            }
        }
        finally { _recovering = false; }
    }

    /// <summary>
    /// 远程服务器的事件流断了（docs/remote.md 4.2「经网关调用时」）：
    /// 403 = 服务器拒绝了访问密钥（网关改成 403 remote_unauthorized，不能当成本机令牌失效去重启本机内核）→ 停止重连，提示重新登录；
    /// 429 = 被服务器限流 → 停止重连；502 = 服务器连不上 / 证书变了 → 取一次具体原因显示出来（连不上时事件流继续重连，好了自动恢复）；
    /// 其他（网关端口没了：本机内核重启或退出）→ 重新连本机内核并重新打开网关。
    /// </summary>
    private async void HandleRemoteDisconnect(string? reason)
    {
        var gen = _connectGen;
        switch (reason)
        {
            case "HTTP 403":
                _events?.Stop();
                Connection = ConnectionState.RemoteFailed(L.T("server.err.unauthorized"), RemoteErrorCode.RemoteUnauthorized);
                return;
            case "HTTP 429":
                _events?.Stop();
                Connection = ConnectionState.RemoteFailed(L.T("server.err.rateLimited"), RemoteErrorCode.RateLimited);
                return;
            case "HTTP 502":
            {
                if (_api is not { } api) return;
                ApiException? err = null;
                try { await api.SystemInfoAsync(TimeSpan.FromSeconds(20)); }
                catch (ApiException e) { err = e; }
                catch (Exception) { }
                if (gen != _connectGen || Connection.IsConnected || err == null) return;
                if (err.Code == ApiException.CodeNetwork)
                {
                    ScheduleRemoteRecovery();
                    return;
                }
                ShowRemoteError(err);
                return;
            }
            default:
                ScheduleRemoteRecovery();
                return;
        }
    }

    /// <summary>经网关的错误：证书变了、密钥失效、被限流时停止重连（避免反复认证失败被服务器封 IP）。</summary>
    private void ShowRemoteError(ApiException e)
    {
        if (e.Code is RemoteErrorCode.FingerprintMismatch or RemoteErrorCode.RemoteUnauthorized or RemoteErrorCode.Unauthorized or RemoteErrorCode.RateLimited)
            _events?.Stop();
        Connection = ConnectionState.RemoteFailed(ServerDisplay.ErrorMessage(e), e.Code);
    }

    /// <summary>网关端口没了（本机内核重启 / 退出）：2 秒后还没恢复就重新连本机内核（必要时拉起）并重新打开网关。</summary>
    private async void ScheduleRemoteRecovery()
    {
        if (_recovering) return;
        _recovering = true;
        var gen = _connectGen;
        try
        {
            await Task.Delay(2000);
            if (Connection.IsConnected || gen != _connectGen || _server is not { } s || _api is not { } api) return;
            try
            {
                await api.SystemInfoAsync(TimeSpan.FromSeconds(5));
                return; // 网关和服务器都通：事件流自己会重连
            }
            catch (ApiException e) when (e.Code != ApiException.CodeNetwork && e.Code != ApiException.CodeUnauthorized)
            {
                if (gen == _connectGen && !Connection.IsConnected) ShowRemoteError(e);
                return;
            }
            catch (Exception) { }
            if (gen != _connectGen) return;
            ApiClient local;
            SystemInfo li;
            try { (local, li) = await _daemon.ConnectAsync(L10n.Shared.SystemLocale); }
            catch (Exception e)
            {
                if (gen == _connectGen) Connection = ConnectionState.RemoteFailed(L.T("server.localRequired") + "\n" + e.Message, ConnectionState.CodeLocal);
                return;
            }
            if (gen != _connectGen) return;
            _localApi = local;
            LocalInfo = li;
            _events?.Stop();
            _events = null;
            _gateways.Remove(s.Id);
            var oldBase = api.BaseUrl;
            await OpenRemote(s, gen);
            // 网关换了端口：已经打开的页面（控制台流、终端）还连着旧端口，重装主窗口内容（数据还在 store 里）
            if (gen == _connectGen && _api != null && _api.BaseUrl != oldBase && MainWindow.Current is { } w) MainView.Install(w);
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
        // 远程服务器的语言、主题不改本机界面（界面偏好按本机的设置）
        if (IsRemote) return;
        _localSettings = s;
        ApplyUiPrefs(s);
    }

    private static void ApplyUiPrefs(AppSettings s)
    {
        L10n.Shared.Apply(s.Locale);
        UiPrefs.Set("Theme", s.Theme);
        Theme.Apply(s.Theme);
    }

    /// <summary>界面偏好（语言、主题）：总是本机内核的设置。</summary>
    public AppSettings? UiSettings => IsRemote ? _localSettings : Data.Settings;

    /// <summary>改界面偏好（语言、主题）：写本机内核的设置；远程时不动服务器的设置。</summary>
    public async Task UpdateUiSettings(object patch)
    {
        if (!IsRemote)
        {
            ApplySettings(await Client().UpdateSettingsAsync(patch));
            return;
        }
        var local = _localApi ?? throw new ApiException(ApiException.CodeNetwork, L.T("connection.notConnected"));
        _localSettings = await local.UpdateSettingsAsync(patch);
        ApplyUiPrefs(_localSettings);
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
                    if (!IsRemote) LocalInfo = SystemInfo;
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
        // 只停本机的服务：远程服务器的服务不受界面退出影响（远程也不允许 system.shutdown）
        if (stopServices && (_localApi ?? (IsRemote ? null : _api)) is { } api)
        {
            try { await api.ShutdownAsync(true); } catch { }
        }
        Tray.Shared.Dispose();
        if (MainWindow.Current is { } w) w.HideOnClose = null; // 退出时真的关掉窗口
        Application.Current.Shutdown();
    }
}

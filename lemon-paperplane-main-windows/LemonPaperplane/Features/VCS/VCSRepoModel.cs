using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>版本控制标签页的子页（插件子页为 "plugin:" + ViewInfo.Key）。对应 Mac VCSPage。</summary>
public static class VCSPage
{
    public const string Changes = "changes";
    public const string Log = "log";
    public const string Branches = "branches";
    public const string Stashes = "stashes";
    public const string Conflicts = "conflicts";
    public static string Plugin(string viewKey) => "plugin:" + viewKey;
    public static bool IsPlugin(string page) => page.StartsWith("plugin:", StringComparison.Ordinal);
    public static string PluginKey(string page) => page.Substring("plugin:".Length);
}

/// <summary>一个工程的版本控制会话：仓库列表、当前仓库、当前子页。按工程缓存，切换标签或语言重建视图时状态不丢。对应 Mac VCSSession。</summary>
public sealed class VCSSession : PPObservable
{
    private static readonly Dictionary<string, VCSSession> Sessions = new();

    public static VCSSession Of(string projectId)
    {
        if (Sessions.TryGetValue(projectId, out var s)) return s;
        s = new VCSSession(projectId);
        Sessions[projectId] = s;
        return s;
    }

    public string ProjectId { get; }
    private List<VCSRoot> _roots = new();
    private bool _rootsLoaded;
    private string? _rootsError;
    private string? _selectedRoot;
    private string _page = VCSPage.Changes;
    private readonly Dictionary<string, VCSRepo> _repos = new();
    private DateTime _lastProjectRefresh = DateTime.MinValue;

    /// <summary>视图是否可见（不可见时不响应外部刷新）</summary>
    public bool Visible { get; set; }

    public List<VCSRoot> Roots { get => _roots; private set { _roots = value; Notify(); } }
    public bool RootsLoaded { get => _rootsLoaded; private set => Set(ref _rootsLoaded, value); }
    public string? RootsError { get => _rootsError; set => Set(ref _rootsError, value); }

    public string? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (!Set(ref _selectedRoot, value)) return;
            if (value != null) VCSPrefs.SetString("VCSRoot:" + ProjectId, value);
        }
    }

    public string Page { get => _page; set => Set(ref _page, value); }

    private VCSSession(string projectId)
    {
        ProjectId = projectId;
        var bus = AppModel.Shared.Bus;
        bus.VcsChanged += e => { if (e.ProjectId == ProjectId) VcsChanged(e.Root); };
        // 卡片摘要变化（内核每 30 秒对一次）：可见时顺带刷新状态，能发现编辑器里做的外部修改
        bus.ProjectChanged += id => { if (id == ProjectId) ExternalRefresh(); };
        if (Application.Current != null) Application.Current.Activated += (_, _) => ExternalRefresh(force: true);
    }

    public VCSRepo? Repo => _selectedRoot != null ? RepoFor(_selectedRoot) : null;

    public VCSRepo RepoFor(string root)
    {
        if (_repos.TryGetValue(root, out var r)) return r;
        var info = _roots.FirstOrDefault(x => x.Path == root);
        var name = info != null ? (info.RelPath == "." || info.RelPath.Length == 0 ? VCSPath.LocalName(info.Path) : info.RelPath) : VCSPath.LocalName(root);
        r = new VCSRepo(this, root, name);
        _repos[root] = r;
        return r;
    }

    /// <summary>重新扫描工程内的仓库（vcs.roots 会更新工程记录）</summary>
    public async Task LoadRoots()
    {
        try
        {
            var list = await AppModel.Shared.Client().VcsRootsAsync(ProjectId);
            Roots = list;
            RootsError = null;
            if (_selectedRoot == null || list.All(r => r.Path != _selectedRoot))
            {
                var saved = VCSPrefs.String("VCSRoot:" + ProjectId);
                SelectedRoot = list.FirstOrDefault(r => r.Path == saved)?.Path ?? list.FirstOrDefault()?.Path;
            }
        }
        catch (Exception e)
        {
            RootsError = e.Message;
        }
        RootsLoaded = true;
    }

    private void VcsChanged(string? root)
    {
        if (root != null && _repos.TryGetValue(root, out var r)) r.Invalidate();
        else foreach (var x in _repos.Values) x.Invalidate();
    }

    private void ExternalRefresh(bool force = false)
    {
        if (!Visible || Repo is not { } repo) return;
        if (!force && (DateTime.UtcNow - _lastProjectRefresh).TotalSeconds < 5) return;
        _lastProjectRefresh = DateTime.UtcNow;
        repo.ScheduleRefresh();
    }

    /// <summary>打开这个工程的版本控制页并切到指定仓库与子页（从工作区总览跳过来时用）</summary>
    public void Reveal(string root, string page)
    {
        if (_roots.Any(r => r.Path == root) || !_rootsLoaded) SelectedRoot = root;
        Page = page;
        var nav = AppModel.Shared.Nav;
        if (!Visible || nav.OpenProjectId != ProjectId) nav.OpenProject(ProjectId, ProjectTab.Vcs);
    }

    /// <summary>在这个工程的仓库里初始化 Git</summary>
    public void InitRepository() =>
        AppModel.Shared.Perform(L.T("vcs.init.failed"), async api =>
        {
            var p = await api.ProjectAsync(ProjectId);
            var r = await api.VcsRunAsync(VCSOp.InitRepo, ProjectId, new VCSInitArgs(), p.Path);
            if (!r.Ok) throw new ApiException(ApiException.CodeUserError, r.Message ?? L.T("vcs.init.failed"));
            await LoadRoots();
        });
}

/// <summary>页内提示条（操作失败或需要下一步时）。对应 Mac VCSNotice。</summary>
public sealed class VCSNotice
{
    public sealed class Action
    {
        public string Title = "";
        public bool Primary;
        public bool Danger;
        public System.Action Run = () => { };

        public Action(string title, System.Action run, bool primary = false, bool danger = false)
        {
            Title = title;
            Run = run;
            Primary = primary;
            Danger = danger;
        }
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Tone = "danger";
    public string Title = "";
    public string? Message;
    public string? Output;
    public string? TaskId;
    public List<Action> Actions = new();
}

/// <summary>正在进行的远程 / 长操作（工具栏显示转圈与「输出」）</summary>
public sealed record VCSActivity(string Op, string Title)
{
    public DateTime Started { get; } = DateTime.UtcNow;
}

/// <summary>一个仓库：状态、分支、储藏；所有写操作经 Run(...) 统一处理结果。对应 Mac VCSRepo。</summary>
public sealed partial class VCSRepo : PPObservable
{
    public VCSSession Session { get; }
    public string Root { get; }
    public string Name { get; }
    public string ProjectId => Session.ProjectId;

    private VCSStatus? _status;
    private JToken? _statusToken;
    private string? _statusError;
    private VCSBranches? _branches;
    private JToken? _branchesToken;
    private List<VCSTag> _tags = new();
    private List<VCSStash> _stashes = new();
    private List<VCSRemote> _remotes = new();
    private VCSNotice? _notice;
    private VCSActivity? _activity;
    private int _revision;

    public VCSStatus? Status { get => _status; private set { _status = value; Notify(); } }
    public string? StatusError { get => _statusError; private set => Set(ref _statusError, value); }
    public VCSBranches? Branches { get => _branches; private set { _branches = value; Notify(); } }
    public List<VCSTag> Tags { get => _tags; private set { _tags = value; Notify(); } }
    public List<VCSStash> Stashes { get => _stashes; private set { _stashes = value; Notify(); } }
    public List<VCSRemote> Remotes { get => _remotes; private set { _remotes = value; Notify(); } }
    public VCSNotice? Notice { get => _notice; set { _notice = value; Notify(); } }
    public VCSActivity? Activity { get => _activity; private set { _activity = value; Notify(); } }

    /// <summary>每次仓库变化加一：日志、储藏等子页据此重新加载</summary>
    public int Revision { get => _revision; private set => Set(ref _revision, value); }

    public VCSChangesModel Changes { get; }
    private VCSLogModel? _log;
    public VCSLogModel Log => _log ??= new VCSLogModel(this);

    private int _refreshGen;
    private bool _identityChecked;
    private bool _moduleCached;
    private string? _cachedModuleId;

    public VCSRepo(VCSSession session, string root, string name)
    {
        Session = session;
        Root = root;
        Name = name;
        Changes = new VCSChangesModel(root);
    }

    public ApiClient Api() => AppModel.Shared.Client();

    // MARK: 读

    /// <summary>合并短时间内的多次刷新</summary>
    public async void ScheduleRefresh()
    {
        var gen = ++_refreshGen;
        await Task.Delay(120);
        if (gen != _refreshGen) return;
        await Refresh();
    }

    /// <summary>仓库被改动（vcs.changed）：刷新状态，通知子页</summary>
    public void Invalidate()
    {
        Revision++;
        ScheduleRefresh();
    }

    public async Task Refresh()
    {
        await LoadStatus();
        if (_branches != null) await LoadBranches();
        if (_stashes.Count > 0 || (_status?.StashCount ?? 0) > 0) await LoadStashes();
    }

    public async Task LoadStatus()
    {
        try
        {
            var s = await Api().VcsStatusAsync(ProjectId, Root);
            SetStatus(s);
            StatusError = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            StatusError = e.Message;
        }
    }

    private void SetStatus(VCSStatus s)
    {
        var tok = Json.ToToken(s);
        if (_statusToken != null && JToken.DeepEquals(tok, _statusToken)) return;
        // HEAD 或分支变了（终端里提交、切分支等外部操作）：日志等子页也要刷新
        var moved = _status != null && (_status.HeadHash != s.HeadHash || _status.Branch != s.Branch);
        _statusToken = tok;
        Status = s;
        Changes.Sync(s);
        if (moved) Revision++;
    }

    /// <summary>用工作区总览（vcs.overview）取到的状态更新：总览不统计增删行数，沿用已有的</summary>
    public void Apply(VCSStatus overviewStatus)
    {
        var s = overviewStatus;
        if (_status != null)
        {
            var byPath = new Dictionary<string, VCSFileChange>();
            foreach (var f in _status.Files) if (!byPath.ContainsKey(f.Path)) byPath[f.Path] = f;
            s = s with
            {
                Files = s.Files.Select(f => byPath.TryGetValue(f.Path, out var o) && o.Status == f.Status
                    ? f with { Additions = o.Additions, Deletions = o.Deletions, Binary = o.Binary } : f).ToList(),
            };
        }
        SetStatus(s);
        StatusError = null;
    }

    public async Task LoadBranches()
    {
        try
        {
            var b = await Api().VcsBranchesAsync(ProjectId, Root);
            var tok = Json.ToToken(b);
            if (_branchesToken != null && JToken.DeepEquals(tok, _branchesToken)) return;
            _branchesToken = tok;
            Branches = b;
        }
        catch { }
    }

    public async Task LoadTags()
    {
        try { Tags = (await Api().VcsAsync<VCSTags>(VCSOp.Tags, ProjectId, Root)).Tags; }
        catch { }
    }

    public async Task LoadStashes()
    {
        try
        {
            var list = (await Api().VcsStashListAsync(ProjectId, Root)).Stashes;
            if (!Json.DeepEquals(list, _stashes)) Stashes = list;
        }
        catch { }
    }

    public async Task LoadRemotes()
    {
        try { Remotes = (await Api().VcsAsync<VCSRemotes>(VCSOp.Remotes, ProjectId, Root)).Remotes; }
        catch { }
    }

    /// <summary>当前分支名（分离头时为短哈希）</summary>
    public string HeadTitle
    {
        get
        {
            if (_status is not { } s) return "";
            if (s.Branch.Length > 0) return s.Branch;
            return VCSStyle.ShortHash(s.HeadHash ?? s.Head);
        }
    }

    public string? CurrentBranch => _status is { } s && s.Branch.Length > 0 && !s.Detached ? s.Branch : null;

    /// <summary>project.action 需要模块：取工程第一个模块（插件动作与模块无关）</summary>
    public async Task<string> FirstModuleId()
    {
        if (_moduleCached) return _cachedModuleId ?? "";
        var p = await Api().ProjectAsync(ProjectId);
        _cachedModuleId = p.Modules.FirstOrDefault()?.Id;
        _moduleCached = true;
        return _cachedModuleId ?? "";
    }

    // MARK: 写

    /// <summary>执行写操作并按结果给出提示或下一步按钮。API 错误弹提示，返回 null。</summary>
    public async Task<VCSOpResult?> Run(string op, object? args, string title, bool @long = false, bool quietSuccess = false,
                                       Func<VCSOpResult, List<VCSNotice.Action>>? extra = null)
    {
        try
        {
            return await RunThrowing(op, args, title, @long, quietSuccess, extra);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            ShowError(e, title);
            return null;
        }
    }

    /// <summary>API 错误：内核带回插件的 reason 时给下一步按钮，否则轻提示</summary>
    public void ShowError(Exception error, string title)
    {
        if (error is not ApiException e || e.Reason is not { } reason)
        {
            AppModel.Shared.Toasts.Error(error, title);
            return;
        }
        var n = new VCSNotice { Tone = "danger", Title = e.Message, Message = title };
        switch (reason)
        {
            case "noRemote":
                n.Tone = "warning";
                n.Actions = new() { new(L.T("vcs.remote.add"), () => { Notice = null; VCSRemoteActions.AddRemote(this); }, primary: true) };
                break;
            case VCSReason.IdentityMissing:
                n.Actions = new() { new(L.T("vcs.identity.set"), () => { Notice = null; VCSIdentityDialog.Present(this, new List<string>(), () => { }); }, primary: true) };
                break;
            default:
                AppModel.Shared.Toasts.Error(error, title);
                return;
        }
        Notice = n;
    }

    /// <summary>同上，但 API 错误抛出（对话框里调用，错误显示在对话框内）</summary>
    public async Task<VCSOpResult> RunThrowing(string op, object? args, string title, bool @long = false, bool quietSuccess = false,
                                               Func<VCSOpResult, List<VCSNotice.Action>>? extra = null)
    {
        if (@long) Activity = new VCSActivity(op, title);
        try
        {
            var r = await Api().VcsRunAsync(op, ProjectId, args, Root);
            Present(r, op, title, quietSuccess, extra, retry: () => Perform(op, args, title, @long, quietSuccess, extra));
            ScheduleRefresh();
            return r;
        }
        finally
        {
            if (@long) Activity = null;
        }
    }

    /// <summary>后台执行（按钮直接触发的）</summary>
    public async void Perform(string op, object? args, string title, bool @long = false, bool quietSuccess = false,
                              Func<VCSOpResult, List<VCSNotice.Action>>? extra = null, Action<VCSOpResult>? then = null)
    {
        if (await Run(op, args, title, @long, quietSuccess, extra) is { } r) then?.Invoke(r);
    }

    /// <summary>结果 → 提示。成功：轻提示；失败或需要下一步：页内提示条。</summary>
    public void Present(VCSOpResult r, string op, string title, bool quietSuccess = false,
                        Func<VCSOpResult, List<VCSNotice.Action>>? extra = null, Action? retry = null)
    {
        var reason = r.Reason ?? (r.NeedsAuth ? VCSReason.NeedsAuth : r.Rejected ? VCSReason.Rejected : null);
        if (r.Ok && r.Conflicts.Count == 0)
        {
            Notice = null;
            if (quietSuccess) return;
            var msg = string.IsNullOrEmpty(r.Message) ? null : r.Message;
            if (reason == VCSReason.UpToDate || reason == VCSReason.NoChanges) AppModel.Shared.Toasts.Show("info", msg ?? title);
            else AppModel.Shared.Toasts.Show("success", title, msg);
            return;
        }
        var n = new VCSNotice
        {
            Tone = r.Ok ? "warning" : "danger",
            Title = string.IsNullOrEmpty(r.Message) ? title : r.Message!,
            Output = r.Output,
            TaskId = r.TaskId,
        };
        if (n.Title != title) n.Message = title;
        var acts = extra?.Invoke(r) ?? new List<VCSNotice.Action>();
        var goConflicts = new VCSNotice.Action(L.T("vcs.action.resolveConflicts"), () => { Notice = null; Session.Reveal(Root, VCSPage.Conflicts); }, primary: true);
        switch (reason)
        {
            case VCSReason.Conflicts:
            case VCSReason.AutostashConflict:
            case VCSReason.UnresolvedConflicts:
                n.Tone = "warning";
                if (r.Conflicts.Count > 0) n.Message = string.Join("\n", r.Conflicts.Take(8));
                acts.Insert(0, goConflicts);
                break;
            case VCSReason.Rejected:
                n.Tone = "warning";
                acts.Add(new(L.T("vcs.pull"), () => { Notice = null; VCSPullDialog.Present(this); }, primary: acts.Count == 0));
                break;
            case VCSReason.NeedsAuth:
            case VCSReason.HostKey:
            case VCSReason.Network:
            case VCSReason.Locked:
            case VCSReason.RemoteRejected:
                if (retry != null) acts.Add(new(L.T("vcs.retry"), () => { Notice = null; retry(); }, primary: acts.Count == 0));
                break;
            case VCSReason.NoUpstream:
                acts.Add(new(L.T("vcs.branch.setUpstream"), () => { Notice = null; VCSBranchActions.SetUpstream(this, CurrentBranch ?? ""); }, primary: acts.Count == 0));
                break;
            case VCSReason.IdentityMissing:
                acts.Add(new(L.T("vcs.identity.set"), () => { Notice = null; VCSIdentityDialog.Present(this, new List<string>(), () => { }); }, primary: true));
                break;
            case VCSReason.EmptyCommit:
                n.Tone = "warning";
                acts.Add(new(L.T("vcs.op.skip"), () => { Notice = null; Perform(VCSOp.Skip, new VCSNoArgs(), L.T("vcs.op.skip")); }, primary: acts.Count == 0));
                break;
            case VCSReason.LocalChanges:
            case VCSReason.UntrackedOverwritten:
                if (r.Files.Count > 0) n.Message = string.Join("\n", r.Files.Take(8)) + (r.Files.Count > 8 ? "\n…" : "");
                break;
        }
        n.Actions = acts;
        Notice = n;
    }

    /// <summary>提交前确认已设置 user.name / user.email（插件会报 identityMissing，但内核 HTTP 错误里没有 reason，这里先查）</summary>
    public async void EnsureIdentity(Action then)
    {
        if (_identityChecked) { then(); return; }
        try
        {
            var cfg = await Api().VcsConfigGetAsync(ProjectId, new[] { "user.name", "user.email" }, root: Root);
            var missing = new[] { "user.name", "user.email" }.Where(k => !cfg.Values.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
            if (missing.Count == 0)
            {
                _identityChecked = true;
                then();
            }
            else
            {
                VCSIdentityDialog.Present(this, missing, () =>
                {
                    _identityChecked = true;
                    then();
                });
            }
        }
        catch
        {
            then(); // 读不到配置就交给插件判断
        }
    }

    /// <summary>正在运行的本仓库任务（远程操作的控制台）</summary>
    public TaskInfo? RunningTask => AppModel.Shared.Runs.ActiveTasks.FirstOrDefault(t => t.Kind == "vcs" && t.ProjectId == ProjectId);

    public void ShowOutput(string? taskId)
    {
        if ((taskId ?? RunningTask?.Id) is { } id) Actions.ShowTaskLog(id);
    }
}

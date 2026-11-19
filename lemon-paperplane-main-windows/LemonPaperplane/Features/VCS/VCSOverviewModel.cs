using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PaperplaneKit;

namespace LemonPaperplane;

// 工作区 Git 总览（像 VS Code 打开文件夹后的多仓库源代码管理）：工作区里全部工程的全部仓库，挨个 pull / push / commit。
// 每个仓库复用工程版本控制页的 VCSRepo（同一个仓库在两处看到的是同一份状态、勾选与提交信息）。对应 Mac VCSOverviewModel.swift。

public sealed class VCSOverviewModel : PPObservable
{
    private static readonly Dictionary<string, VCSOverviewModel> Models = new();

    public static VCSOverviewModel Of(string workspaceId)
    {
        if (Models.TryGetValue(workspaceId, out var m)) return m;
        m = new VCSOverviewModel(workspaceId);
        Models[workspaceId] = m;
        return m;
    }

    /// <summary>一个仓库（来自 vcs.overview；状态放在对应的 VCSRepo 里）</summary>
    public sealed record Entry(string Root, string ProjectId, string ProjectName, string RelPath, string Name, string? Error)
    {
        /// <summary>仓库名之外的说明：工程名（与仓库名不同时）+ 相对路径</summary>
        public string? Subtitle
        {
            get
            {
                var parts = new List<string>();
                if (ProjectName != Name) parts.Add(ProjectName);
                if (RelPath.Length > 0 && RelPath != "." && RelPath != Name) parts.Add(RelPath);
                return parts.Count == 0 ? null : string.Join(" / ", parts);
            }
        }
    }

    public string WorkspaceId { get; }
    private List<Entry> _entries = new();
    private bool _loading;
    private bool _loaded;
    private string? _error;
    private HashSet<string> _expanded = new();
    private HashSet<string> _showAllFiles = new();
    private string _query = "";
    private bool _onlyChanged;
    private VCSBatch? _batch;

    public List<Entry> Entries { get => _entries; private set { _entries = value; Notify(); } }
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }
    public bool Loaded { get => _loaded; private set => Set(ref _loaded, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public HashSet<string> Expanded { get => _expanded; set { _expanded = value; Notify(); } }
    public HashSet<string> ShowAllFiles { get => _showAllFiles; set { _showAllFiles = value; Notify(); } }
    public string Query { get => _query; set => Set(ref _query, value ?? ""); }

    public bool OnlyChanged
    {
        get => _onlyChanged;
        set { if (Set(ref _onlyChanged, value)) VCSPrefs.SetString("VCSOverviewOnlyChanged:" + WorkspaceId, value ? "1" : "0"); }
    }

    public VCSBatch? Batch { get => _batch; set { _batch = value; Notify(); } }

    /// <summary>批量进度变了（同一个 Batch 对象里的项）</summary>
    public void BatchChanged() => Notify(nameof(Batch));

    /// <summary>任何一个仓库的状态变化（统计、过滤要重算）</summary>
    public const string StatsProperty = "Stats";

    private bool _visible;
    private DispatcherTimer? _timer;
    private readonly HashSet<string> _known = new();
    private readonly List<VCSRepo> _observed = new();
    private int _reloadGen;
    private bool _statsPending;

    /// <summary>页面可见时每 30 秒刷新一次</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value) StartTimer();
            else { _timer?.Stop(); _timer = null; }
        }
    }

    private VCSOverviewModel(string workspaceId)
    {
        WorkspaceId = workspaceId;
        _onlyChanged = VCSPrefs.String("VCSOverviewOnlyChanged:" + workspaceId) == "1";
        AppModel.Shared.Bus.VcsChanged += VcsChanged;
        if (Application.Current != null) Application.Current.Activated += (_, _) => { if (_visible) ScheduleReload(); };
    }

    public VCSRepo Repo(Entry e)
    {
        var r = VCSSession.Of(e.ProjectId).RepoFor(e.Root);
        r.Changes.AutoIncludeUntracked = true;
        return r;
    }

    public static bool HasChanges(VCSStatus? s) => s != null && (s.Files.Count > 0 || s.Operation != null || s.Conflicts > 0);

    // MARK: 加载与刷新

    public async Task Reload()
    {
        if (_loading) return;
        Loading = true;
        try
        {
            var list = await AppModel.Shared.Client().VcsOverviewAsync(WorkspaceId);
            var next = new List<Entry>();
            var expanded = new HashSet<string>(_expanded);
            foreach (var o in list)
            {
                var e = new Entry(o.Root, o.ProjectId, o.ProjectName, o.RelPath, o.Name.Length == 0 ? VCSPath.LocalName(o.Root) : o.Name,
                    o.Status == null ? o.Error ?? "" : null);
                next.Add(e);
                if (o.Status is { } st) Repo(e).Apply(st);
                // 第一次见到的仓库：有改动 / 冲突 / 进行中操作的默认展开
                if (_known.Add(e.Root) && HasChanges(o.Status)) expanded.Add(e.Root);
            }
            if (!expanded.SetEquals(_expanded)) Expanded = expanded;
            if (!next.SequenceEqual(_entries))
            {
                Entries = next;
                ObserveRepos();
            }
            Error = null;
            Loaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Error = e.Message;
            Loaded = true;
        }
        finally
        {
            Loading = false;
        }
    }

    public async void ScheduleReload()
    {
        var gen = ++_reloadGen;
        await Task.Delay(300);
        if (gen != _reloadGen) return;
        await Reload();
    }

    /// <summary>任何一个仓库的状态变化都让工具栏统计、过滤重新计算</summary>
    private void ObserveRepos()
    {
        foreach (var r in _observed) r.PropertyChanged -= OnRepoChanged;
        _observed.Clear();
        foreach (var e in _entries)
        {
            var r = Repo(e);
            if (_observed.Contains(r)) continue;
            r.PropertyChanged += OnRepoChanged;
            _observed.Add(r);
        }
    }

    private void OnRepoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VCSRepo.Status) || _statsPending) return;
        _statsPending = true;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            _statsPending = false;
            Notify(StatsProperty);
        }));
    }

    /// <summary>只刷新对应 root 的那一个仓库；不认识的仓库（新加的嵌套仓库等）整体重取</summary>
    private void VcsChanged(VCSChangedEvent e)
    {
        if (!_loaded) return;
        if (e.Root != null && _entries.FirstOrDefault(x => x.Root == e.Root) is { } entry) Repo(entry).ScheduleRefresh();
        else if (_entries.Any(x => x.ProjectId == e.ProjectId) || AppModel.Shared.Data.Project(e.ProjectId)?.WorkspaceId == WorkspaceId) ScheduleReload();
    }

    private void StartTimer()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) =>
        {
            if (!_visible || _batch?.Running == true) return;
            await Reload();
        };
        _timer.Start();
    }

    // MARK: 统计与过滤

    public sealed class StatsInfo
    {
        public int Repos;
        public int Changed;
        public int ToPush;
        public int ToPull;
    }

    public StatsInfo Stats
    {
        get
        {
            var s = new StatsInfo { Repos = _entries.Count };
            foreach (var e in _entries)
            {
                var st = Repo(e).Status;
                if (HasChanges(st)) s.Changed++;
                if ((st?.Ahead ?? 0) > 0) s.ToPush++;
                if ((st?.Behind ?? 0) > 0) s.ToPull++;
            }
            return s;
        }
    }

    public List<Entry> Filtered(string globalSearch)
    {
        var q = _query.Trim();
        var g = globalSearch.Trim();
        bool Has(string a, string t) => a.IndexOf(t, StringComparison.CurrentCultureIgnoreCase) >= 0;
        return _entries.Where(e =>
        {
            if (_onlyChanged)
            {
                var st = Repo(e).Status;
                if (!(HasChanges(st) || (st?.Ahead ?? 0) > 0 || (st?.Behind ?? 0) > 0 || e.Error != null)) return false;
            }
            foreach (var t in new[] { q, g })
                if (t.Length > 0 && !(Has(e.Name, t) || Has(e.ProjectName, t) || Has(e.RelPath, t))) return false;
            return true;
        }).ToList();
    }

    public void Toggle(string root)
    {
        var s = new HashSet<string>(_expanded);
        if (!s.Remove(root)) s.Add(root);
        Expanded = s;
    }

    public void Expand(string root)
    {
        if (_expanded.Contains(root)) return;
        Expanded = new HashSet<string>(_expanded) { root };
    }

    // MARK: 批量

    public void StartBatch(VCSBatch.BatchKind kind)
    {
        if (_batch?.Running == true) return;
        var targets = _entries.Where(e =>
        {
            if (e.Error != null || Repo(e).Status is not { } st) return false;
            bool tracking = st.Upstream != null && !st.UpstreamGone && st.Operation == null;
            return kind switch
            {
                VCSBatch.BatchKind.Fetch => true,
                VCSBatch.BatchKind.Pull => tracking && st.Behind > 0 && st.Conflicts == 0,
                _ => tracking && st.Ahead > 0,
            };
        }).ToList();
        if (targets.Count == 0)
        {
            AppModel.Shared.Toasts.Show("info", VCSBatch.EmptyMessage(kind));
            return;
        }
        var names = targets.Select(e => e.Subtitle is { } sub ? $"{e.Name}  ({sub})" : e.Name).ToList();
        Dialogs.Confirm(VCSBatch.ConfirmTitle(kind, targets.Count),
            string.Join("\n", names.Take(20)) + (names.Count > 20 ? "\n…" : ""),
            () => Run(kind, targets), ok: VCSBatch.Title(kind));
    }

    private async void Run(VCSBatch.BatchKind kind, List<Entry> targets)
    {
        var batch = new VCSBatch(kind, targets.Select(t => new VCSBatch.Item(t.Root, t.Name)).ToList()) { Running = true };
        Batch = batch;
        for (int i = 0; i < targets.Count; i++)
        {
            if (!ReferenceEquals(_batch, batch)) return;
            var e = targets[i];
            batch.Items[i].State = VCSBatch.ItemState.Running;
            BatchChanged();
            var r = Repo(e);
            try
            {
                VCSOpResult res = kind switch
                {
                    VCSBatch.BatchKind.Fetch => await r.RunThrowing(VCSOp.Fetch, new VCSFetchArgs(), L.T("vcs.fetch"), @long: true, quietSuccess: true),
                    VCSBatch.BatchKind.Pull => await r.RunThrowing(VCSOp.Pull, new VCSPullArgs { Mode = VCSPrefs.PullMode, Autostash = VCSPrefs.PullAutostash ? true : null },
                        L.T("vcs.pull"), @long: true, quietSuccess: true),
                    _ => await r.RunThrowing(VCSOp.Push, new VCSPushArgs(), L.T("vcs.push"), @long: true, quietSuccess: true),
                };
                bool ok = res.Ok && res.Conflicts.Count == 0;
                batch.Items[i].State = ok ? VCSBatch.ItemState.Ok : VCSBatch.ItemState.Failed;
                batch.Items[i].Message = res.Message;
                if (!ok) Expand(e.Root);
            }
            catch (Exception ex)
            {
                // 没配置远程的仓库不算失败
                bool skipped = (ex as ApiException)?.Reason == "noRemote";
                batch.Items[i].State = skipped ? VCSBatch.ItemState.Skipped : VCSBatch.ItemState.Failed;
                batch.Items[i].Message = ex.Message;
                if (!skipped) Expand(e.Root);
            }
            BatchChanged();
        }
        batch.Running = false;
        BatchChanged();
        int failed = batch.Items.Count(x => x.State == VCSBatch.ItemState.Failed);
        AppModel.Shared.Toasts.Show(failed == 0 ? "success" : "warning", VCSBatch.Title(kind), L.T("vcs.overview.batchDone", batch.Items.Count - failed, failed));
    }
}

/// <summary>批量获取 / 拉取 / 推送的进度</summary>
public sealed class VCSBatch
{
    public enum BatchKind { Fetch, Pull, Push }

    public enum ItemState { Pending, Running, Ok, Failed, Skipped }

    public sealed class Item
    {
        public string Root;
        public string Name;
        public ItemState State = ItemState.Pending;
        public string? Message;

        public Item(string root, string name)
        {
            Root = root;
            Name = name;
        }
    }

    public BatchKind Kind { get; }
    public List<Item> Items { get; }
    public bool Running { get; set; }

    public VCSBatch(BatchKind kind, List<Item> items)
    {
        Kind = kind;
        Items = items;
    }

    public int Done => Items.Count(x => x.State != ItemState.Pending && x.State != ItemState.Running);

    public static string Title(BatchKind k) => k switch
    {
        BatchKind.Fetch => L.T("vcs.overview.fetchAll"),
        BatchKind.Pull => L.T("vcs.overview.pullAll"),
        _ => L.T("vcs.overview.pushAll"),
    };

    public static string ConfirmTitle(BatchKind k, int n) => k switch
    {
        BatchKind.Fetch => L.T("vcs.overview.fetchAllTitle", n),
        BatchKind.Pull => L.T("vcs.overview.pullAllTitle", n),
        _ => L.T("vcs.overview.pushAllTitle", n),
    };

    public static string EmptyMessage(BatchKind k) => k switch
    {
        BatchKind.Fetch => L.T("vcs.overview.noRepos"),
        BatchKind.Pull => L.T("vcs.overview.nothingToPull"),
        _ => L.T("vcs.overview.nothingToPush"),
    };
}

/// <summary>单个仓库的快捷拉取 / 推送：有上游时直接按默认方式执行，否则弹对话框</summary>
public static class VCSOverviewActions
{
    public static void Pull(VCSRepo repo)
    {
        if (repo.Status is not { } s || s.Upstream == null || s.UpstreamGone)
        {
            VCSPullDialog.Present(repo);
            return;
        }
        VCSRemoteActions.Pull(repo, new VCSPullArgs { Mode = VCSPrefs.PullMode, Autostash = VCSPrefs.PullAutostash ? true : null });
    }

    public static void Push(VCSRepo repo)
    {
        if (repo.Status is not { } s || s.Upstream == null || s.UpstreamGone || s.Ahead <= 0)
        {
            VCSPushDialog.Present(repo);
            return;
        }
        VCSRemoteActions.Push(repo, new VCSPushArgs());
    }

    /// <summary>在终端中打开（Windows Terminal，没有时 PowerShell 7 / Windows PowerShell）</summary>
    public static void OpenInTerminal(string path)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var wt = Path.Combine(local, "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(wt))
            {
                Process.Start(new ProcessStartInfo(wt, "-d \"" + path.TrimEnd('\\') + "\"") { UseShellExecute = false });
                return;
            }
            var pwsh = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe") ?? "powershell.exe";
            Process.Start(new ProcessStartInfo(pwsh, "-NoExit") { UseShellExecute = true, WorkingDirectory = path });
        }
        catch (Exception e)
        {
            AppModel.Shared.Toasts.Error(e);
        }
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}

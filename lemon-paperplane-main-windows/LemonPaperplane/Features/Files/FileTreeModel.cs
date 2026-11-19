using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>
/// 文件树数据：按需逐级展开（fs.list），过滤走 fs.search，Git 状态取各仓库的 vcs.status。对应 Mac FileTreeModel。
/// 只在「文件」标签页可见时存在，离开即释放；展开状态按工程记在内存里。只在 UI 线程使用。
/// </summary>
public sealed class FileTreeModel : PPObservable
{
    /// <summary>树的一行（Footer 为「条目过多」提示行）。值相等：条目、层级都一样就是同一行。</summary>
    public sealed record Row(FSEntry Entry, int Depth, bool Footer = false)
    {
        public string Id => Footer ? "\u0000footer" : Entry.Path;
    }

    public string ProjectId { get; }
    public string ProjectPath { get; }
    private readonly List<VCSRoot> _vcsRoots;

    /// <summary>目录（"" 为根）→ 子项。</summary>
    private readonly Dictionary<string, List<FSEntry>> _children = new();
    private HashSet<string> _expanded;
    private readonly HashSet<string> _loading = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly HashSet<string> _truncated = new();
    private string? _selection;
    private Dictionary<string, string> _git = new();
    private HashSet<string> _changedDirs = new();
    private string _query = "";
    private List<FSSearchHit>? _results;
    private bool _searching;
    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _vcsDebounce;
    private bool _attached;

    /// <summary>各工程的展开状态（只存路径集合，很小）。</summary>
    private static readonly Dictionary<string, HashSet<string>> ExpandedCache = new();

    public FileTreeModel(Project project)
    {
        ProjectId = project.Id;
        ProjectPath = project.Path;
        _vcsRoots = project.VcsRoots;
        _expanded = ExpandedCache.TryGetValue(project.Id, out var e) ? new HashSet<string>(e) : new HashSet<string> { "" };
    }

    public IReadOnlyDictionary<string, List<FSEntry>> Children => _children;
    public IReadOnlyCollection<string> Expanded => _expanded;
    public bool IsExpanded(string dir) => _expanded.Contains(dir);
    public bool IsLoading(string dir) => _loading.Contains(dir);
    public string? Error(string dir) => _errors.TryGetValue(dir, out var e) ? e : null;
    public bool IsTruncated(string dir) => _truncated.Contains(dir);

    public string? Selection { get => _selection; set => Set(ref _selection, value); }

    /// <summary>相对工程根的路径 → Git 状态（M A D R C U ? T）。</summary>
    public IReadOnlyDictionary<string, string> Git => _git;

    /// <summary>含有改动的目录。</summary>
    public IReadOnlyCollection<string> ChangedDirs => _changedDirs;
    public bool IsChangedDir(string dir) => _changedDirs.Contains(dir);
    public string? GitStatus(string path) => _git.TryGetValue(path, out var s) ? s : null;

    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? "")) return;
            ScheduleSearch();
        }
    }

    public List<FSSearchHit>? Results { get => _results; private set => Set(ref _results, value); }
    public bool Searching { get => _searching; private set => Set(ref _searching, value); }

    // MARK: 仓库变化

    /// <summary>可见时订阅仓库变化（提交、切分支、外部修改被 git 发现）：400ms 防抖后刷新状态与已展开的目录。</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        AppModel.Shared.Bus.VcsChanged += OnVcsChanged;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        AppModel.Shared.Bus.VcsChanged -= OnVcsChanged;
        _vcsDebounce?.Stop();
        _searchCts?.Cancel();
    }

    private void OnVcsChanged(VCSChangedEvent e)
    {
        if (e.ProjectId != ProjectId) return;
        _vcsDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) =>
        {
            _vcsDebounce!.Stop();
            _ = Refresh();
        }, Dispatcher.CurrentDispatcher);
        _vcsDebounce.Stop();
        _vcsDebounce.Start();
    }

    // MARK: 目录

    public Task Start() => Task.WhenAll(_expanded.ToList().Select(d => Reload(d)).Concat(new[] { LoadGit() }));

    public async Task Reload(string dir, bool expand = false)
    {
        if (expand) SetExpanded(dir, true);
        _loading.Add(dir);
        Notify(nameof(Children));
        try
        {
            var r = await AppModel.Shared.Client().FsListAsync(ProjectId, dir);
            _children[dir] = r.Entries;
            _errors.Remove(dir);
            if (r.Truncated) _truncated.Add(dir); else _truncated.Remove(dir);
            // 已展开但被删掉的子目录不再记
            var alive = new HashSet<string>(r.Entries.Where(e => e.Dir).Select(e => e.Path));
            foreach (var e in _expanded.ToList())
                if (e.Length > 0 && FileActions.Parent(e) == dir && !alive.Contains(e)) SetExpanded(e, false);
        }
        catch (Exception e)
        {
            _errors[dir] = e.Message;
        }
        finally
        {
            _loading.Remove(dir);
            Notify(nameof(Children));
        }
    }

    public void Toggle(FSEntry entry)
    {
        if (!entry.Dir) return;
        if (_expanded.Contains(entry.Path))
        {
            SetExpanded(entry.Path, false);
        }
        else
        {
            SetExpanded(entry.Path, true);
            if (!_children.ContainsKey(entry.Path)) _ = Reload(entry.Path);
        }
    }

    public void CollapseAll()
    {
        _expanded = new HashSet<string> { "" };
        ExpandedCache[ProjectId] = new HashSet<string>(_expanded);
        Notify(nameof(Expanded));
    }

    public Task Refresh() => Start();

    private void SetExpanded(string dir, bool on)
    {
        if (on) _expanded.Add(dir); else _expanded.Remove(dir);
        ExpandedCache[ProjectId] = new HashSet<string>(_expanded);
        Notify(nameof(Expanded));
    }

    /// <summary>展开的目录压平成行（只算已加载的部分）。</summary>
    public List<Row> Rows
    {
        get
        {
            var output = new List<Row>();
            void Walk(string dir, int depth)
            {
                if (!_children.TryGetValue(dir, out var list)) return;
                foreach (var e in list)
                {
                    output.Add(new Row(e, depth));
                    if (e.Dir && _expanded.Contains(e.Path)) Walk(e.Path, depth + 1);
                }
            }
            Walk("", 0);
            return output;
        }
    }

    /// <summary>选中项所在目录（新建文件用）。</summary>
    public string SelectedDir
    {
        get
        {
            if (_selection is not { } s) return "";
            if (_expanded.Contains(s) || _children.ContainsKey(s)) return s;
            var e = Rows.FirstOrDefault(r => r.Entry.Path == s)?.Entry;
            if (e is { Dir: true }) return e.Path;
            return FileActions.Parent(s);
        }
    }

    // MARK: Git

    public async Task LoadGit()
    {
        if (_vcsRoots.Count == 0 || AppModel.Shared.Api is not { } api) return;
        var map = new Dictionary<string, string>();
        foreach (var root in _vcsRoots)
        {
            VCSStatus st;
            try { st = await api.VcsStatusAsync(ProjectId, root.Path); }
            catch { continue; }
            // 仓库在工程根时 relPath 是 "."（内核按 filepath.Rel 给的），当成空
            var relRoot = root.RelPath.Replace('\\', '/').Trim('/');
            if (relRoot == ".") relRoot = "";
            else if (relRoot.StartsWith("./", StringComparison.Ordinal)) relRoot = relRoot.Substring(2);
            foreach (var f in st.Files)
            {
                var p = f.Path.Replace('\\', '/');
                var rel = relRoot.Length == 0 ? p : relRoot + "/" + p;
                map[rel] = f.Conflicted ? "U" : f.Status;
            }
        }
        var dirs = new HashSet<string>();
        foreach (var kv in map)
        {
            if (kv.Value == "D") continue;
            var d = FileActions.Parent(kv.Key);
            while (d.Length > 0 && dirs.Add(d)) d = FileActions.Parent(d);
        }
        _git = map;
        _changedDirs = dirs;
        Notify(nameof(Git));
    }

    // MARK: 过滤（按文件名模糊搜索整个工程）

    private async void ScheduleSearch()
    {
        _searchCts?.Cancel();
        var q = _query.Trim();
        if (q.Length == 0)
        {
            Results = null;
            Searching = false;
            return;
        }
        Searching = true;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try { await Task.Delay(200, cts.Token); }
        catch (TaskCanceledException) { return; }
        List<FSSearchHit>? hits;
        try { hits = await AppModel.Shared.Client().FsSearchAsync(ProjectId, q, 200); }
        catch { hits = null; }
        if (cts.IsCancellationRequested) return;
        Results = hits ?? new List<FSSearchHit>();
        Searching = false;
    }
}

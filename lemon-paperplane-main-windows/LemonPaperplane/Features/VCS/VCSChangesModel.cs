using System;
using System.Collections.Generic;
using System.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>提交草稿（单独通知：打字时不重绘变更列表和差异）。对应 Mac VCSCommitDraft。</summary>
public sealed class VCSCommitDraft : PPObservable
{
    private string _message = "";
    private bool _amend;
    private bool _signOff;
    private bool _noVerify;
    private string _author = "";
    private bool _committing;

    public string Message { get => _message; set => Set(ref _message, value ?? ""); }
    public bool Amend { get => _amend; set => Set(ref _amend, value); }
    public bool SignOff { get => _signOff; set => Set(ref _signOff, value); }
    public bool NoVerify { get => _noVerify; set => Set(ref _noVerify, value); }
    public string Author { get => _author; set => Set(ref _author, value ?? ""); }
    public bool Committing { get => _committing; set => Set(ref _committing, value); }

    /// <summary>外部改了 Message（填入历史、amend 取回信息）时通知输入框刷新</summary>
    public event Action? MessageReplaced;

    public void ReplaceMessage(string text)
    {
        Message = text;
        MessageReplaced?.Invoke();
    }
}

/// <summary>变更列表里的一侧（暂存区模式下同一文件可能同时出现在已暂存与未暂存两组）</summary>
public static class VCSChangeSide
{
    public const string Head = "head";
    public const string Staged = "staged";
    public const string Unstaged = "unstaged";
    public const string Untracked = "untracked";
    public const string Conflict = "conflict";
}

public readonly record struct VCSChangeSelection(string Path, string Side);

/// <summary>变更与提交的界面状态（每个仓库一份）。对应 Mac VCSChangesModel。</summary>
public sealed class VCSChangesModel : PPObservable
{
    public string Root { get; }

    /// <summary>提交信息与选项</summary>
    public VCSCommitDraft Draft { get; } = new();

    /// <summary>非暂存区模式：不提交的已跟踪文件</summary>
    public HashSet<string> Excluded { get; private set; } = new();
    /// <summary>非暂存区模式：要提交的未跟踪文件</summary>
    public HashSet<string> IncludedUntracked { get; private set; } = new();
    /// <summary>非暂存区模式：文件 → 不提交的块下标（基于 mode=head、上下文 3 的 diff）</summary>
    public Dictionary<string, HashSet<int>> ExcludedHunks { get; private set; } = new();
    /// <summary>文件的块数（diff 加载后记录）</summary>
    public Dictionary<string, int> HunkCounts { get; } = new();
    /// <summary>新出现的未跟踪文件默认勾选（工作区总览里「默认提交全部改动」）</summary>
    public bool AutoIncludeUntracked { get; set; }
    private HashSet<string> _seenUntracked = new();

    private VCSChangeSelection? _selection;
    private HashSet<VCSChangeSelection> _multi = new();
    private HashSet<string> _collapsed = new();
    private bool _groupByDir = VCSPrefs.GroupByDirectory;
    private bool _stagingArea = VCSPrefs.StagingArea;

    /// <summary>勾选状态变化（Excluded / IncludedUntracked / ExcludedHunks）</summary>
    public const string ChecksProperty = "Checks";

    /// <summary>显示差异的文件</summary>
    public VCSChangeSelection? Selection { get => _selection; set { if (Nullable.Equals(_selection, value)) return; _selection = value; Notify(); } }
    /// <summary>多选（右键菜单作用范围）</summary>
    public HashSet<VCSChangeSelection> Multi { get => _multi; set { _multi = value; Notify(); } }
    public HashSet<string> Collapsed { get => _collapsed; set { _collapsed = value; Notify(); } }

    public bool GroupByDir
    {
        get => _groupByDir;
        set { if (Set(ref _groupByDir, value)) VCSPrefs.GroupByDirectory = value; }
    }

    public bool StagingArea
    {
        get => _stagingArea;
        set
        {
            if (!Set(ref _stagingArea, value)) return;
            VCSPrefs.StagingArea = value;
            Selection = null;
            Multi = new HashSet<VCSChangeSelection>();
        }
    }

    public VCSDiffModel Diff { get; } = new();

    /// <summary>Ctrl+K：聚焦提交信息</summary>
    public event Action? FocusMessage;
    public void RequestFocusMessage() => FocusMessage?.Invoke();

    public VCSChangesModel(string root) { Root = root; }

    private void ChecksChanged() => Notify(ChecksProperty);

    /// <summary>状态刷新后：去掉已消失文件的勾选与选中</summary>
    public void Sync(VCSStatus s)
    {
        var paths = new HashSet<string>(s.Files.Select(f => f.Path));
        bool changed = false;
        if (AutoIncludeUntracked)
        {
            var fresh = s.Files.Where(f => f.IsUntracked && !_seenUntracked.Contains(f.Path)).Select(f => f.Path).ToList();
            if (fresh.Count > 0) { IncludedUntracked.UnionWith(fresh); changed = true; }
        }
        _seenUntracked = new HashSet<string>(s.Files.Where(f => f.IsUntracked).Select(f => f.Path));
        if (!Excluded.IsSubsetOf(paths)) { Excluded.IntersectWith(paths); changed = true; }
        if (!IncludedUntracked.IsSubsetOf(paths)) { IncludedUntracked.IntersectWith(paths); changed = true; }
        if (ExcludedHunks.Keys.Any(k => !paths.Contains(k)))
        {
            ExcludedHunks = ExcludedHunks.Where(kv => paths.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            changed = true;
        }
        if (changed) ChecksChanged();
        if (_selection is { } sel)
        {
            var f = s.Files.FirstOrDefault(x => x.Path == sel.Path);
            bool still = sel.Side switch
            {
                VCSChangeSide.Staged => f != null && !string.IsNullOrEmpty(f.IndexStatus) && !f.Conflicted,
                VCSChangeSide.Unstaged => f != null && !string.IsNullOrEmpty(f.WorktreeStatus) && f.WorktreeStatus != "?" && !f.Conflicted,
                VCSChangeSide.Conflict => f?.Conflicted ?? false,
                _ => f != null,
            };
            if (!still) Selection = null;
            else Diff.Reload();
        }
        if (_multi.Any(m => !paths.Contains(m.Path))) Multi = new HashSet<VCSChangeSelection>(_multi.Where(m => paths.Contains(m.Path)));
    }

    // MARK: 分组

    public sealed class Groups
    {
        public List<VCSFileChange> Conflicts = new();
        public List<VCSFileChange> Tracked = new();
        public List<VCSFileChange> Untracked = new();
        public List<VCSFileChange> Staged = new();
        public List<VCSFileChange> Unstaged = new();
    }

    public Groups GroupsOf(List<VCSFileChange> files)
    {
        var g = new Groups();
        foreach (var f in files)
        {
            if (f.Conflicted) { g.Conflicts.Add(f); continue; }
            if (f.IsUntracked) { g.Untracked.Add(f); continue; }
            g.Tracked.Add(f);
            if (!string.IsNullOrEmpty(f.IndexStatus)) g.Staged.Add(f);
            if (!string.IsNullOrEmpty(f.WorktreeStatus) && f.WorktreeStatus != "?") g.Unstaged.Add(f);
        }
        return g;
    }

    // MARK: 勾选（非暂存区模式）

    public bool? IsIncluded(VCSFileChange f)
    {
        if (f.IsUntracked) return IncludedUntracked.Contains(f.Path);
        if (Excluded.Contains(f.Path)) return false;
        if (ExcludedHunks.TryGetValue(f.Path, out var ex) && ex.Count > 0) return null;
        return true;
    }

    private void SetIncludedRaw(VCSFileChange f, bool on)
    {
        if (f.IsUntracked)
        {
            if (on) IncludedUntracked.Add(f.Path); else IncludedUntracked.Remove(f.Path);
        }
        else
        {
            if (on) Excluded.Remove(f.Path); else Excluded.Add(f.Path);
            ExcludedHunks.Remove(f.Path);
        }
    }

    public void SetIncluded(VCSFileChange f, bool on)
    {
        SetIncludedRaw(f, on);
        ChecksChanged();
    }

    public bool? GroupState(IReadOnlyCollection<VCSFileChange> files)
    {
        if (files.Count == 0) return false;
        bool all = true, none = true;
        foreach (var f in files)
        {
            var st = IsIncluded(f);
            if (st != true) all = false;
            if (st != false) none = false;
            if (!all && !none) return null;
        }
        if (all) return true;
        if (none) return false;
        return null;
    }

    public void ToggleGroup(IReadOnlyCollection<VCSFileChange> files)
    {
        var on = GroupState(files) != true;
        foreach (var f in files) SetIncludedRaw(f, on);
        ChecksChanged();
    }

    public void SetHunk(string path, int index, bool included, int total)
    {
        var ex = ExcludedHunks.TryGetValue(path, out var cur) ? new HashSet<int>(cur) : new HashSet<int>();
        if (included) ex.Remove(index); else ex.Add(index);
        HunkCounts[path] = total;
        if (ex.Count >= total)
        {
            ExcludedHunks.Remove(path);
            Excluded.Add(path);
        }
        else
        {
            if (ex.Count == 0) ExcludedHunks.Remove(path); else ExcludedHunks[path] = ex;
            Excluded.Remove(path);
        }
        ChecksChanged();
    }

    public void ClearExcludedHunks()
    {
        if (ExcludedHunks.Count == 0) return;
        ExcludedHunks = new Dictionary<string, HashSet<int>>();
        ChecksChanged();
    }

    /// <summary>非暂存区模式的提交内容：整文件路径 + 部分块</summary>
    public (List<string> Paths, Dictionary<string, List<int>> Hunks) CommitPlan(List<VCSFileChange> files)
    {
        var paths = new List<string>();
        var hunks = new Dictionary<string, List<int>>();
        foreach (var f in files)
        {
            if (f.Conflicted || IsIncluded(f) == false) continue;
            if (ExcludedHunks.TryGetValue(f.Path, out var ex) && ex.Count > 0 && HunkCounts.TryGetValue(f.Path, out var n) && n > 0)
            {
                var inc = Enumerable.Range(0, n).Where(i => !ex.Contains(i)).ToList();
                if (inc.Count > 0) hunks[f.Path] = inc;
            }
            else
            {
                paths.Add(f.Path);
            }
        }
        return (paths, hunks);
    }

    // MARK: 选中与差异

    public void Select(VCSChangeSelection sel, VCSFileChange file, VCSRepo repo)
    {
        if (VCSModifiers.Command)
        {
            var m = new HashSet<VCSChangeSelection>(_multi);
            if (m.Count == 0 && _selection is { } cur) m.Add(cur);
            if (!m.Remove(sel)) m.Add(sel);
            Multi = m;
        }
        else if (_multi.Count > 0)
        {
            Multi = new HashSet<VCSChangeSelection>();
        }
        Selection = sel;
        Diff.Set(Target(sel, file), repo);
    }

    public VCSDiffTarget Target(VCSChangeSelection sel, VCSFileChange file)
    {
        var mode = sel.Side switch
        {
            VCSChangeSide.Staged => VCSDiffMode.Staged,
            VCSChangeSide.Unstaged => VCSDiffMode.Unstaged,
            _ => VCSDiffMode.Head,
        };
        return new VCSDiffTarget(file.Path, file.OldPath, VCSDiffKind.Working(mode), file.Status);
    }

    public VCSHunkMode HunkMode
    {
        get
        {
            if (_selection is not { } sel) return VCSHunkMode.None;
            return sel.Side switch
            {
                VCSChangeSide.Head => _stagingArea ? VCSHunkMode.None : VCSHunkMode.Include,
                VCSChangeSide.Unstaged => VCSHunkMode.Stage,
                VCSChangeSide.Staged => VCSHunkMode.Unstage,
                _ => VCSHunkMode.None,
            };
        }
    }

    /// <summary>右键作用的文件：在多选里就用多选，否则只用这一个</summary>
    public List<VCSChangeSelection> Scope(VCSChangeSelection sel) =>
        _multi.Contains(sel) && _multi.Count > 1 ? _multi.ToList() : new List<VCSChangeSelection> { sel };

    public void ToggleCollapsed(string key)
    {
        var c = new HashSet<string>(_collapsed);
        if (!c.Remove(key)) c.Add(key);
        Collapsed = c;
    }
}

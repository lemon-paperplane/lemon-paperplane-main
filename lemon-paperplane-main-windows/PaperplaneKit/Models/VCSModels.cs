using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit;

// 对应插件 SDK 的 vcs.go。操作名为 "vcs.<op>"，输入为 {projectId, root 可选, ...Args 平铺}（见 docs/api.md 第 5 节）。
// git 插件返回的是 SDK 结构的 JSON 超集（扩展字段见 lemon-paperplane-plugin-git/README.md），这里一并建模，全部可选或带默认值。
// 参数类型（XxxArgs）的必填字段标了 required，用对象初始化器构造：new VCSDiffArgs { Path = "a.txt", Mode = VCSDiffMode.Head }。

public static class VCSOp
{
    public const string Detect = "detect";
    public const string Status = "status";
    public const string Diff = "diff";
    public const string Commit = "commit";
    public const string Push = "push";
    public const string Pull = "pull";
    public const string Fetch = "fetch";
    public const string Branches = "branches";
    public const string Checkout = "checkout";
    public const string BranchCreate = "branchCreate";
    public const string BranchDelete = "branchDelete";
    public const string BranchRename = "branchRename";
    public const string Merge = "merge";
    public const string Rebase = "rebase";
    public const string Abort = "abort";
    public const string Continue = "continue";
    public const string Log = "log";
    public const string Show = "show";
    public const string Discard = "discard";
    public const string StashList = "stashList";
    public const string StashSave = "stashSave";
    public const string StashApply = "stashApply";
    public const string StashDrop = "stashDrop";
    public const string Resolve = "resolve";
    public const string MergeTool = "mergeTool";
    public const string CherryPick = "cherryPick";
    public const string Revert = "revert";
    public const string Reset = "reset";
    public const string Tags = "tags";
    public const string TagCreate = "tagCreate";
    public const string TagDelete = "tagDelete";
    public const string Remotes = "remotes";
    public const string RemoteSet = "remoteSet";
    public const string RemoteRemove = "remoteRemove";
    public const string FileHistory = "fileHistory";
    public const string Clone = "clone";
    public const string InitRepo = "init";
    public const string Stage = "stage";
    public const string Unstage = "unstage";
    public const string StageHunks = "stageHunks";
    public const string UnstageHunks = "unstageHunks";
    public const string DiscardHunks = "discardHunks";
    public const string UndoCommit = "undoCommit";
    public const string Reword = "reword";
    public const string RebaseInteractive = "rebaseInteractive";
    public const string CheckoutFile = "checkoutFile";
    public const string Compare = "compare";
    public const string PushPreview = "pushPreview";
    public const string Blame = "blame";
    public const string SetUpstream = "setUpstream";
    public const string Submodules = "submodules";
    public const string SubmoduleUpdate = "submoduleUpdate";
    public const string Worktrees = "worktrees";
    public const string WorktreeAdd = "worktreeAdd";
    public const string WorktreeRemove = "worktreeRemove";
    public const string ConfigGet = "configGet";
    public const string ConfigSet = "configSet";
    public const string Ignore = "ignore";
    public const string Clean = "clean";
    public const string Repos = "repos";
    // git 插件的扩展操作
    public const string Skip = "skip";
    public const string FileContent = "fileContent";
    public const string ContainingBranches = "containingBranches";
    public const string TagPush = "tagPush";
    public const string MergeTools = "mergeTools";
    public const string StashShow = "stashShow";
    /// <summary>内核额外提供的「列出工程内仓库」操作 vcs.roots</summary>
    public const string Roots = "roots";
}

/// <summary>VCSOpResult.Reason 的取值（git 插件 README「错误与结果」）</summary>
public static class VCSReason
{
    public const string Conflicts = "conflicts";
    public const string UpToDate = "upToDate";
    public const string FastForward = "fastForward";
    public const string Squashed = "squashed";
    public const string NeedsAuth = "needsAuth";
    public const string HostKey = "hostKey";
    public const string Rejected = "rejected";
    public const string RemoteRejected = "remoteRejected";
    public const string RepoNotFound = "repoNotFound";
    public const string Network = "network";
    public const string LocalChanges = "localChanges";
    public const string UntrackedOverwritten = "untrackedOverwritten";
    public const string UnresolvedConflicts = "unresolvedConflicts";
    public const string EmptyCommit = "emptyCommit";
    public const string NotFullyMerged = "notFullyMerged";
    public const string NoUpstream = "noUpstream";
    public const string Divergent = "divergent";
    public const string UnrelatedHistories = "unrelatedHistories";
    public const string CheckedOutElsewhere = "checkedOutElsewhere";
    public const string Locked = "locked";
    public const string MarkersRemain = "markersRemain";
    public const string ApplyFailed = "applyFailed";
    public const string NoChanges = "noChanges";
    public const string AutostashConflict = "autostashConflict";
    public const string IdentityMissing = "identityMissing";
    public const string Failed = "failed";
}

/// <summary>diff / 按块操作的模式</summary>
public static class VCSDiffMode
{
    /// <summary>HEAD → 工作区</summary>
    public const string Head = "head";
    /// <summary>暂存区 → 工作区</summary>
    public const string Unstaged = "unstaged";
    /// <summary>HEAD → 暂存区</summary>
    public const string Staged = "staged";
    /// <summary>提交 → 父提交</summary>
    public const string Commit = "commit";
    /// <summary>base → head（head 空为工作区）</summary>
    public const string Range = "range";
}

public sealed record VCSRepoInfo
{
    public bool IsRepo { get; set; }
    public string? Root { get; set; }
    public string? Vcs { get; set; }
    /// <summary>repos：nested | submodule | worktree</summary>
    public string? Kind { get; set; }
    public string? Parent { get; set; }
}

/// <summary>进行中的操作详情。SDK 声明为字符串，git 插件返回对象；两种都能解码（字符串放进 Message）。</summary>
[JsonConverter(typeof(VCSOperationDetailConverter))]
public sealed record VCSOperationDetail
{
    public string? Op { get; set; }
    public int? Step { get; set; }
    public int? Total { get; set; }
    /// <summary>变基中的分支</summary>
    public string? HeadName { get; set; }
    /// <summary>变基目标提交</summary>
    public string? Onto { get; set; }
    /// <summary>正在应用 / 合并的提交</summary>
    public string? Current { get; set; }
    /// <summary>MERGE_MSG 第一行，或字符串形式的详情</summary>
    public string? Message { get; set; }
}

internal sealed class VCSOperationDetailConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(VCSOperationDetail);
    public override bool CanWrite => false;
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => throw new NotSupportedException();

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        switch (reader.TokenType)
        {
            case JsonToken.Null:
            case JsonToken.Undefined:
                return null;
            case JsonToken.String:
                return new VCSOperationDetail { Message = (string?)reader.Value };
            default:
                var o = JObject.Load(reader);
                var d = new VCSOperationDetail();
                using (var r = o.CreateReader()) serializer.Populate(r, d);
                return d;
        }
    }
}

public sealed record VCSStatus
{
    /// <summary>分离头时为空</summary>
    public string Branch { get; set; } = "";
    public bool Detached { get; set; }
    /// <summary>当前提交短哈希</summary>
    public string? Head { get; set; }
    public string? Upstream { get; set; }
    public bool UpstreamGone { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    /// <summary>merge | rebase | cherry-pick | revert | am</summary>
    public string? Operation { get; set; }
    public VCSOperationDetail? OperationDetail { get; set; }
    public List<VCSFileChange> Files { get; set; } = new();
    public bool UntrackedTruncated { get; set; }
    public int UntrackedCount { get; set; }
    public int Conflicts { get; set; }
    public int StashCount { get; set; }
    /// <summary>HEAD 完整哈希</summary>
    public string? HeadHash { get; set; }
    /// <summary>还没有任何提交</summary>
    public bool Unborn { get; set; }
}

/// <summary>Status：M 修改 | A 新增 | D 删除 | R 重命名 | C 复制 | U 冲突 | ? 未跟踪 | T 类型变化</summary>
public sealed record VCSFileChange
{
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public string Status { get; set; } = "";
    public bool Staged { get; set; }
    public bool Unstaged { get; set; }
    public bool Conflicted { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public bool Binary { get; set; }
    /// <summary>暂存区相对 HEAD 的状态字母（M A D R C T），空表示没变</summary>
    public string? IndexStatus { get; set; }
    /// <summary>工作区相对暂存区的状态字母（M D T ?），空表示没变</summary>
    public string? WorktreeStatus { get; set; }
    /// <summary>UU 双方修改 | AA 双方新增 | DU 我方删除 | UD 对方删除 | AU 我方新增 | UA 对方新增 | DD 双方删除</summary>
    public string? ConflictType { get; set; }
    public bool Submodule { get; set; }
    public string Id => Path;

    public bool IsUntracked => Status == "?";
}

public sealed record VCSDiffArgs
{
    public required string Path { get; set; }
    public string? OldPath { get; set; }
    /// <summary>commit 模式的提交（commit 非空且 mode 为空时即 commit 模式）</summary>
    public string? Commit { get; set; }
    /// <summary>上下文行数，默认 3；-1 表示整个文件</summary>
    public int? Context { get; set; }
    /// <summary>head | unstaged | staged | commit | range（见 VCSDiffMode）</summary>
    public string? Mode { get; set; }
    public bool? Staged { get; set; }
    public string? Base { get; set; }
    public string? Head { get; set; }
    public bool? MergeBase { get; set; }
    /// <summary>commit 模式对比第几个父提交，默认 1</summary>
    public int? Parent { get; set; }
    public bool? IgnoreWhitespace { get; set; }
    /// <summary>不做 1 MB / 5000 行限制</summary>
    public bool? NoLimit { get; set; }
}

public sealed record VCSDiff
{
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public bool Binary { get; set; }
    public bool TooLarge { get; set; }
    public List<VCSDiffHunk> Hunks { get; set; } = new();
    public string? Language { get; set; }
    /// <summary>生成本 diff 的模式（按块操作要传回同样的 mode）</summary>
    public string? Mode { get; set; }
    public int? Context { get; set; }
    /// <summary>A 新增 | D 删除 | M 修改 | R 重命名 | C 复制 | T 类型变化</summary>
    public string? Status { get; set; }
    public string? OldMode { get; set; }
    public string? NewMode { get; set; }
    public int? Similarity { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public bool Untracked { get; set; }
}

public sealed record VCSDiffHunk
{
    public string Header { get; set; } = "";
    public int OldStart { get; set; }
    public int OldLines { get; set; }
    public int NewStart { get; set; }
    public int NewLines { get; set; }
    public List<VCSDiffLine> Lines { get; set; } = new();
}

public sealed record VCSDiffLine
{
    /// <summary>" " | "+" | "-"</summary>
    public string Kind { get; set; } = "";
    public int Old { get; set; }
    public int New { get; set; }
    public string Text { get; set; } = "";
    /// <summary>文件最后一行且没有换行符</summary>
    public bool NoEol { get; set; }
}

public sealed record VCSCommitArgs
{
    public required string Message { get; set; }
    /// <summary>要提交的文件（含未跟踪的新文件），只提交这些路径的工作区内容；为空表示提交暂存区</summary>
    public List<string>? Paths { get; set; }
    /// <summary>提交全部变更（paths 为空时有效）</summary>
    public bool? All { get; set; }
    /// <summary>部分提交：文件 → 块下标（基于 mode=head、上下文 hunkContext 的 diff）</summary>
    public Dictionary<string, List<int>>? Hunks { get; set; }
    public int? HunkContext { get; set; }
    public bool? Amend { get; set; }
    public bool? Push { get; set; }
    /// <summary>「名字 &lt;邮箱&gt;」</summary>
    public string? Author { get; set; }
    public bool? SignOff { get; set; }
    /// <summary>跳过 pre-commit / commit-msg 钩子</summary>
    public bool? NoVerify { get; set; }
}

public sealed record VCSCommitResult
{
    public string Hash { get; set; } = "";
    public string? Short { get; set; }
    public VCSOpResult? Push { get; set; }
}

public sealed record VCSPushArgs
{
    public string? Remote { get; set; }
    public string? Branch { get; set; }
    /// <summary>使用 --force-with-lease</summary>
    public bool? Force { get; set; }
    public bool? SetUpstream { get; set; }
    public bool? Tags { get; set; }
    /// <summary>tags 为 true 时：all（--tags）| follow（--follow-tags）</summary>
    public string? TagsMode { get; set; }
    public bool? NoVerify { get; set; }
}

public sealed record VCSPullArgs
{
    /// <summary>merge | rebase | ff-only | config（按 git 配置）</summary>
    public string? Mode { get; set; }
    public string? Remote { get; set; }
    public string? Branch { get; set; }
    public bool? Autostash { get; set; }
}

public sealed record VCSFetchArgs
{
    public string? Remote { get; set; }
    public bool? Prune { get; set; }
    public bool? Tags { get; set; }
}

public sealed record VCSOpResult
{
    public bool Ok { get; set; }
    /// <summary>给人看的简要结果（已本地化）</summary>
    public string? Message { get; set; }
    /// <summary>原始输出（截断）</summary>
    public string? Output { get; set; }
    public List<string> Conflicts { get; set; } = new();
    public bool NeedsAuth { get; set; }
    public bool Rejected { get; set; }
    /// <summary>结构化原因（见 VCSReason）</summary>
    public string? Reason { get; set; }
    /// <summary>相关文件（如会被覆盖的本地修改）</summary>
    public List<string> Files { get; set; } = new();
    /// <summary>相关提交（如被删分支的提交，可用于恢复）</summary>
    public string? Hash { get; set; }
    /// <summary>mergeTool 用到的工具</summary>
    public string? Tool { get; set; }
    /// <summary>远程操作（push/pull/fetch/clone/submoduleUpdate/mergeTool/tagPush）生成的任务，输出看 consoles/&lt;taskId&gt;/stream</summary>
    public string? TaskId { get; set; }
    /// <summary>clone：克隆出的目录</summary>
    public string? Path { get; set; }
}

public sealed record VCSBranches
{
    public string Current { get; set; } = "";
    public List<VCSBranch> Local { get; set; } = new();
    public List<VCSBranch> Remote { get; set; } = new();
    public bool Detached { get; set; }
    /// <summary>HEAD 完整哈希</summary>
    public string? Head { get; set; }
    /// <summary>最近检出过的本地分支（新的在前）</summary>
    public List<string> Recent { get; set; } = new();
}

public sealed record VCSBranch
{
    public string Name { get; set; } = "";
    public string? Upstream { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    /// <summary>短哈希</summary>
    public string? Head { get; set; }
    public string? Subject { get; set; }
    public string? Date { get; set; }
    public bool Current { get; set; }
    public bool Gone { get; set; }
    /// <summary>完整哈希</summary>
    public string? Hash { get; set; }
    /// <summary>远程分支所属远程</summary>
    public string? Remote { get; set; }
    /// <summary>在别的工作树里检出着</summary>
    public string? Worktree { get; set; }
    public string? Author { get; set; }
    public string Id => Name;

    public DateTimeOffset? ParsedDate => GoTime.Parse(Date);
}

public sealed record VCSCheckoutArgs
{
    public required string Ref { get; set; }
    public bool? Create { get; set; }
    public string? Start { get; set; }
    public bool? Force { get; set; }
    /// <summary>储藏 → 检出 → 恢复（IDEA 的 Smart Checkout）</summary>
    public bool? Smart { get; set; }
}

public sealed record VCSBranchCreateArgs
{
    public required string Name { get; set; }
    public string? Start { get; set; }
    public bool? Checkout { get; set; }
}

public sealed record VCSBranchDeleteArgs
{
    public required string Name { get; set; }
    public bool? Force { get; set; }
    public bool? Remote { get; set; }
}

public sealed record VCSBranchRenameArgs
{
    public required string Old { get; set; }
    public required string New { get; set; }
}

public sealed record VCSRefArgs
{
    public required string Ref { get; set; }
    /// <summary>cherryPick/revert 多个提交</summary>
    public List<string>? Refs { get; set; }
    public bool? NoFF { get; set; }
    public bool? Squash { get; set; }
    public string? Message { get; set; }
    public bool? FfOnly { get; set; }
    public bool? AllowUnrelated { get; set; }
    /// <summary>rebase --onto</summary>
    public string? Onto { get; set; }
    /// <summary>cherry-pick -x</summary>
    public bool? RecordOrigin { get; set; }
    /// <summary>cherry-pick/revert -n</summary>
    public bool? NoCommit { get; set; }
    public int? Mainline { get; set; }
    /// <summary>tagDelete：同时删除这个远程上的标签</summary>
    public string? Remote { get; set; }
}

public sealed record VCSLogArgs
{
    /// <summary>空表示当前分支；"--all" 表示全部分支</summary>
    public string? Ref { get; set; }
    public int? Skip { get; set; }
    /// <summary>默认 200</summary>
    public int? Limit { get; set; }
    public string? Author { get; set; }
    public string? Grep { get; set; }
    public string? Path { get; set; }
    public List<string>? Refs { get; set; }
    public List<string>? Authors { get; set; }
    public string? Since { get; set; }
    public string? Until { get; set; }
    public List<string>? Paths { get; set; }
    /// <summary>topo（默认）| date</summary>
    public string? Order { get; set; }
    public bool? FirstParent { get; set; }
    public bool? NoGraph { get; set; }
}

public sealed record VCSLog
{
    public List<VCSCommit> Commits { get; set; } = new();
    public bool HasMore { get; set; }
    /// <summary>是否带提交图（按作者/关键词/日期过滤时为 false）；旧版没有这个字段时为 null</summary>
    public bool? Graph { get; set; }
}

public sealed record VCSCommit
{
    public string Hash { get; set; } = "";
    public string Short { get; set; } = "";
    public List<string> Parents { get; set; } = new();
    public string Author { get; set; } = "";
    public string? Email { get; set; }
    public string Date { get; set; } = "";
    public string Subject { get; set; } = "";
    public List<VCSRef> Refs { get; set; } = new();
    public VCSGraphRow? Graph { get; set; }
    /// <summary>fileHistory：该提交里文件的路径（跟踪重命名）</summary>
    public string? Path { get; set; }
    /// <summary>fileHistory：该提交对文件做了什么（A/M/D/R）</summary>
    public string? PathStatus { get; set; }
    public string Id => Hash;

    /// <summary>Date 为 RFC3339 字符串</summary>
    public DateTimeOffset? ParsedDate => GoTime.Parse(Date);
}

public sealed record VCSRef
{
    public string Name { get; set; } = "";
    /// <summary>head | local | remote | tag</summary>
    public string Kind { get; set; } = "";
}

public sealed record VCSGraphRow
{
    public int Lane { get; set; }
    public int Lanes { get; set; }
    public List<VCSGraphEdge> Edges { get; set; } = new();
    public int Color { get; set; }
}

public sealed record VCSGraphEdge
{
    public int From { get; set; }
    public int To { get; set; }
    public int Color { get; set; }
}

public sealed record VCSShowArgs
{
    public required string Hash { get; set; }
    /// <summary>合并提交对比第几个父提交，默认 1</summary>
    public int? Parent { get; set; }
}

public sealed record VCSCommitDetail
{
    public VCSCommit Commit { get; set; } = new();
    /// <summary>主题行之后的正文</summary>
    public string? Body { get; set; }
    public List<VCSFileChange> Files { get; set; } = new();
    /// <summary>完整提交信息</summary>
    public string? Message { get; set; }
    public string? Committer { get; set; }
    public string? CommitterEmail { get; set; }
    public string? CommitterDate { get; set; }
    /// <summary>文件列表对比的是第几个父提交</summary>
    public int? Parent { get; set; }
}

public sealed record VCSPathsArgs
{
    public required List<string> Paths { get; set; }
    /// <summary>mergeTool 用：指定外部合并工具 id（mergeTools 的结果），null 表示按设置/自动</summary>
    public string? Tool { get; set; }
}

/// <summary>discard 的参数（VCSPathsArgs 的超集）</summary>
public sealed record VCSDiscardArgs
{
    public required List<string> Paths { get; set; }
    /// <summary>head（默认，恢复到 HEAD）| unstaged（只丢弃未暂存部分）</summary>
    public string? Mode { get; set; }
    /// <summary>新文件只取消暂存、未跟踪文件保留</summary>
    public bool? KeepNew { get; set; }
}

/// <summary>clean 的参数</summary>
public sealed record VCSCleanArgs
{
    public required List<string> Paths { get; set; }
    public bool? All { get; set; }
    public bool? Ignored { get; set; }
}

public sealed record VCSStashList
{
    public List<VCSStash> Stashes { get; set; } = new();
}

public sealed record VCSStash
{
    public int Index { get; set; }
    public string Message { get; set; } = "";
    public string? Branch { get; set; }
    public string? Date { get; set; }
    /// <summary>储藏提交，可用 diff(commit) 查看内容</summary>
    public string? Hash { get; set; }
    public int Id => Index;

    public DateTimeOffset? ParsedDate => GoTime.Parse(Date);
}

public sealed record VCSStashSaveArgs
{
    public string? Message { get; set; }
    public bool? IncludeUntracked { get; set; }
    /// <summary>只储藏这些文件</summary>
    public List<string>? Paths { get; set; }
    /// <summary>保留暂存区</summary>
    public bool? KeepIndex { get; set; }
    /// <summary>只储藏已暂存的</summary>
    public bool? Staged { get; set; }
}

public sealed record VCSStashArgs
{
    public required int Index { get; set; }
    public bool? Pop { get; set; }
    /// <summary>同时恢复暂存区（--index）</summary>
    public bool? RestoreIndex { get; set; }
    /// <summary>恢复为新分支（git stash branch）</summary>
    public string? Branch { get; set; }
    /// <summary>stashDrop：清空全部</summary>
    public bool? All { get; set; }
}

/// <summary>stashShow 的结果</summary>
public sealed record VCSStashShow
{
    public string Hash { get; set; } = "";
    public List<VCSFileChange> Files { get; set; } = new();
    /// <summary>储藏里的未跟踪文件</summary>
    public List<string> Untracked { get; set; } = new();
}

public sealed record VCSResolveArgs
{
    public required string Path { get; set; }
    /// <summary>ours | theirs | resolved</summary>
    public required string Side { get; set; }
    public List<string>? Paths { get; set; }
    /// <summary>resolved 时文件里还有冲突标记也标记解决</summary>
    public bool? Force { get; set; }
}

public sealed record VCSResetArgs
{
    public required string Ref { get; set; }
    /// <summary>soft | mixed | hard | keep | merge</summary>
    public required string Mode { get; set; }
}

public sealed record VCSUndoCommitArgs
{
    /// <summary>提交已推送也撤销</summary>
    public bool? Force { get; set; }
}

public sealed record VCSTags
{
    public List<VCSTag> Tags { get; set; } = new();
}

public sealed record VCSTag
{
    public string Name { get; set; } = "";
    /// <summary>指向的提交</summary>
    public string Hash { get; set; } = "";
    public string? Subject { get; set; }
    public string? Date { get; set; }
    public bool Annotated { get; set; }
    /// <summary>附注标签的说明</summary>
    public string? Message { get; set; }
    public string? Tagger { get; set; }
    public string Id => Name;
}

public sealed record VCSTagCreateArgs
{
    public required string Name { get; set; }
    public string? Ref { get; set; }
    /// <summary>有 message 时建附注标签</summary>
    public string? Message { get; set; }
    public bool? Force { get; set; }
    /// <summary>创建后推送到远程</summary>
    public bool? Push { get; set; }
    public string? Remote { get; set; }
}

/// <summary>tagPush 的参数</summary>
public sealed record VCSTagPushArgs
{
    public required string Ref { get; set; }
    public string? Remote { get; set; }
    public bool? Delete { get; set; }
    public bool? Force { get; set; }
}

public sealed record VCSRemotes
{
    public List<VCSRemote> Remotes { get; set; } = new();
}

public sealed record VCSRemote
{
    public string Name { get; set; } = "";
    public string FetchUrl { get; set; } = "";
    public string? PushUrl { get; set; }
    /// <summary>网页地址（GitHub/GitLab 等）</summary>
    public string? WebUrl { get; set; }
    public string Id => Name;
}

public sealed record VCSFileHistoryArgs
{
    public required string Path { get; set; }
    public int? Limit { get; set; }
    public int? Skip { get; set; }
    /// <summary>默认 HEAD；"--all" 表示全部分支</summary>
    public string? Ref { get; set; }
    /// <summary>跟踪重命名，默认 true</summary>
    public bool? Follow { get; set; }
}

public sealed record VCSCloneArgs
{
    public required string Url { get; set; }
    public required string Dest { get; set; }
    public string? Branch { get; set; }
    public int? Depth { get; set; }
    public bool? RecurseSubmodules { get; set; }
}

public sealed record VCSInitArgs
{
    public string? InitialBranch { get; set; }
}

public sealed record VCSHunksArgs
{
    public required string Path { get; set; }
    /// <summary>VCSDiff.Hunks 的下标</summary>
    public required List<int> Hunks { get; set; }
    /// <summary>取 diff 时的上下文行数，需与界面取 diff 时一致</summary>
    public int? Context { get; set; }
    /// <summary>取 diff 时用的 mode（界面显示 head diff 时传 head）</summary>
    public string? Mode { get; set; }
    /// <summary>所选块的 @@ 头，用来确认 diff 没过期</summary>
    public List<string>? Headers { get; set; }
    public string? OldPath { get; set; }
}

public sealed record VCSFileContentArgs
{
    public required string Path { get; set; }
    /// <summary>空 = 工作区，":" = 暂存区，其它为提交 / 分支</summary>
    public string? Ref { get; set; }
    public int? MaxBytes { get; set; }
}

public sealed record VCSFileContent
{
    public string Path { get; set; } = "";
    public string? Ref { get; set; }
    public bool Exists { get; set; }
    public int Size { get; set; }
    public bool Binary { get; set; }
    public bool TooLarge { get; set; }
    public string? Text { get; set; }
    /// <summary>二进制（图片对比）或非 UTF-8 文本</summary>
    public string? Base64 { get; set; }
    public bool NotUtf8 { get; set; }

    /// <summary>文件内容字节：有 base64 用 base64（格式不对给 null），否则用 text 的 UTF-8</summary>
    public byte[]? Data
    {
        get
        {
            if (Base64 != null)
            {
                try { return Convert.FromBase64String(Base64); }
                catch (FormatException) { return null; }
            }
            return Text == null ? null : Encoding.UTF8.GetBytes(Text);
        }
    }
}

/// <summary>containingBranches 的结果</summary>
public sealed record VCSContainingBranches
{
    public List<VCSRef> Refs { get; set; } = new();
    public bool Truncated { get; set; }
}

public sealed record VCSMergeToolInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Builtin { get; set; }
}

/// <summary>mergeTools 的结果</summary>
public sealed record VCSMergeTools
{
    /// <summary>用户 git 配置里的 merge.tool</summary>
    public string? Configured { get; set; }
    public List<VCSMergeToolInfo> Tools { get; set; } = new();
}

public sealed record VCSRewordArgs
{
    public required string Hash { get; set; }
    public required string Message { get; set; }
}

public sealed record VCSRebasePlan
{
    /// <summary>从这个提交之后开始（不含它）；空为 --root</summary>
    public required string Base { get; set; }
    /// <summary>从旧到新</summary>
    public required List<VCSRebaseStep> Steps { get; set; }
}

public sealed record VCSRebaseStep
{
    public required string Hash { get; set; }
    /// <summary>pick | reword | squash | fixup | drop</summary>
    public required string Action { get; set; }
    public string? Message { get; set; }
}

public sealed record VCSCheckoutFileArgs
{
    public required string Path { get; set; }
    public required string Ref { get; set; }
}

public sealed record VCSCompareArgs
{
    public required string Base { get; set; }
    /// <summary>为空表示当前分支</summary>
    public string? Head { get; set; }
}

public sealed record VCSCompare
{
    /// <summary>在 head 不在 base</summary>
    public List<VCSCommit> Ahead { get; set; } = new();
    /// <summary>在 base 不在 head</summary>
    public List<VCSCommit> Behind { get; set; } = new();
    /// <summary>merge-base(base, head) 到 head 的文件变化</summary>
    public List<VCSFileChange> Files { get; set; } = new();
    public string? MergeBase { get; set; }
    public bool Truncated { get; set; }
}

public sealed record VCSPushPreview
{
    public string Remote { get; set; } = "";
    public string Branch { get; set; } = "";
    public string RemoteRef { get; set; } = "";
    public bool NewBranch { get; set; }
    public List<VCSCommit> Commits { get; set; } = new();
    /// <summary>远端领先的提交数（&gt;0 时推送会被拒）</summary>
    public int Behind { get; set; }
    /// <summary>推送时会设置上游</summary>
    public bool SetUpstream { get; set; }
    /// <summary>提交太多，只列出前 500 个</summary>
    public bool Truncated { get; set; }
}

public sealed record VCSBlameArgs
{
    public required string Path { get; set; }
    public string? Ref { get; set; }
}

public sealed record VCSBlame
{
    public List<VCSBlameLine> Lines { get; set; } = new();
}

public sealed record VCSBlameLine
{
    public int Line { get; set; }
    public string Hash { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string? Summary { get; set; }
}

public sealed record VCSSetUpstreamArgs
{
    public string? Branch { get; set; }
    /// <summary>如 origin/main；空表示取消跟踪</summary>
    public required string Upstream { get; set; }
}

public sealed record VCSSubmodules
{
    public List<VCSSubmodule> Submodules { get; set; } = new();
}

public sealed record VCSSubmodule
{
    public string Path { get; set; } = "";
    public string? Url { get; set; }
    public string? Head { get; set; }
    public string Status { get; set; } = "";
    public string Id => Path;
}

public sealed record VCSSubmoduleUpdateArgs
{
    public List<string>? Paths { get; set; }
    /// <summary>JSON 键是 init</summary>
    [JsonProperty("init")]
    public bool? Initialize { get; set; }
    public bool? Recursive { get; set; }
    public bool? Remote { get; set; }
}

public sealed record VCSWorktrees
{
    public List<VCSWorktree> Worktrees { get; set; } = new();
}

public sealed record VCSWorktree
{
    public string Path { get; set; } = "";
    public string? Branch { get; set; }
    public string? Head { get; set; }
    public bool Main { get; set; }
    public bool Locked { get; set; }
    public string Id => Path;
}

public sealed record VCSWorktreeAddArgs
{
    public required string Path { get; set; }
    public string? Branch { get; set; }
    public bool? NewBranch { get; set; }
    public string? Start { get; set; }
}

public sealed record VCSWorktreeRemoveArgs
{
    public required string Path { get; set; }
    public bool? Force { get; set; }
}

public sealed record VCSConfigGetArgs
{
    public required List<string> Keys { get; set; }
    /// <summary>只读全局配置</summary>
    public bool? Global { get; set; }
}

public sealed record VCSConfig
{
    public Dictionary<string, string> Values { get; set; } = new();
    public Dictionary<string, string> Scopes { get; set; } = new();
}

public sealed record VCSConfigSetArgs
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public bool? Global { get; set; }
    public bool? Unset { get; set; }
}

public sealed record VCSIgnoreArgs
{
    public required List<string> Patterns { get; set; }
    /// <summary>默认仓库根 .gitignore；"exclude" 表示 .git/info/exclude（不提交）；也可为子目录的 .gitignore</summary>
    public string? File { get; set; }
}

public sealed record VCSReposArgs
{
    public int? MaxDepth { get; set; }
}

public sealed record VCSRepoList
{
    public List<VCSRepoInfo> Repos { get; set; } = new();
}

/// <summary>
/// vcs.overview 的一项：工作区里的一个仓库（工作区内全部工程的全部仓库，去重）。
/// 写操作用 vcs.* {projectId, root}。Status 是插件返回的 VCSStatus 超集（不含增删行数）；取状态失败时 Status 为空、Error 有说明。
/// </summary>
public sealed record VCSRepoOverview
{
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    /// <summary>仓库绝对路径（调 vcs.* 时作为 root 传）</summary>
    public string Root { get; set; } = "";
    /// <summary>相对工程根：主仓库为 "" 或 "."，工程在仓库子目录里时为 ".."</summary>
    public string RelPath { get; set; } = "";
    /// <summary>仓库目录名</summary>
    public string Name { get; set; } = "";
    public string Vcs { get; set; } = "";
    public VCSStatus? Status { get; set; }
    public string? Error { get; set; }
    public string Id => Root;
}

/// <summary>vcs.changed 事件的 data</summary>
public sealed record VCSChangedEvent
{
    public string ProjectId { get; set; } = "";
    public string? Root { get; set; }
}

/// <summary>没有参数的 VCS 操作</summary>
public sealed record VCSNoArgs;

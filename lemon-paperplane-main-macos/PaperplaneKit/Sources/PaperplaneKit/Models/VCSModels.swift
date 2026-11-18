import Foundation

// 对应插件 SDK 的 vcs.go。操作名为 "vcs.<op>"，输入为 {projectId, root 可选, ...Args 平铺}（见 docs/api.md 第 5 节）。
// git 插件返回的是 SDK 结构的 JSON 超集（扩展字段见 lemon-paperplane-plugin-git/README.md），这里一并建模，全部可选或带默认值。

public enum VCSOp {
    public static let detect = "detect"
    public static let status = "status"
    public static let diff = "diff"
    public static let commit = "commit"
    public static let push = "push"
    public static let pull = "pull"
    public static let fetch = "fetch"
    public static let branches = "branches"
    public static let checkout = "checkout"
    public static let branchCreate = "branchCreate"
    public static let branchDelete = "branchDelete"
    public static let branchRename = "branchRename"
    public static let merge = "merge"
    public static let rebase = "rebase"
    public static let abort = "abort"
    public static let `continue` = "continue"
    public static let log = "log"
    public static let show = "show"
    public static let discard = "discard"
    public static let stashList = "stashList"
    public static let stashSave = "stashSave"
    public static let stashApply = "stashApply"
    public static let stashDrop = "stashDrop"
    public static let resolve = "resolve"
    public static let mergeTool = "mergeTool"
    public static let cherryPick = "cherryPick"
    public static let revert = "revert"
    public static let reset = "reset"
    public static let tags = "tags"
    public static let tagCreate = "tagCreate"
    public static let tagDelete = "tagDelete"
    public static let remotes = "remotes"
    public static let remoteSet = "remoteSet"
    public static let remoteRemove = "remoteRemove"
    public static let fileHistory = "fileHistory"
    public static let clone = "clone"
    public static let initRepo = "init"
    public static let stage = "stage"
    public static let unstage = "unstage"
    public static let stageHunks = "stageHunks"
    public static let unstageHunks = "unstageHunks"
    public static let discardHunks = "discardHunks"
    public static let undoCommit = "undoCommit"
    public static let reword = "reword"
    public static let rebaseInteractive = "rebaseInteractive"
    public static let checkoutFile = "checkoutFile"
    public static let compare = "compare"
    public static let pushPreview = "pushPreview"
    public static let blame = "blame"
    public static let setUpstream = "setUpstream"
    public static let submodules = "submodules"
    public static let submoduleUpdate = "submoduleUpdate"
    public static let worktrees = "worktrees"
    public static let worktreeAdd = "worktreeAdd"
    public static let worktreeRemove = "worktreeRemove"
    public static let configGet = "configGet"
    public static let configSet = "configSet"
    public static let ignore = "ignore"
    public static let clean = "clean"
    public static let repos = "repos"
    // git 插件的扩展操作
    public static let skip = "skip"
    public static let fileContent = "fileContent"
    public static let containingBranches = "containingBranches"
    public static let tagPush = "tagPush"
    public static let mergeTools = "mergeTools"
    public static let stashShow = "stashShow"
    /// 内核额外提供的「列出工程内仓库」操作 vcs.roots
    public static let roots = "roots"
}

/// VCSOpResult.reason 的取值（git 插件 README「错误与结果」）
public enum VCSReason {
    public static let conflicts = "conflicts"
    public static let upToDate = "upToDate"
    public static let fastForward = "fastForward"
    public static let squashed = "squashed"
    public static let needsAuth = "needsAuth"
    public static let hostKey = "hostKey"
    public static let rejected = "rejected"
    public static let remoteRejected = "remoteRejected"
    public static let repoNotFound = "repoNotFound"
    public static let network = "network"
    public static let localChanges = "localChanges"
    public static let untrackedOverwritten = "untrackedOverwritten"
    public static let unresolvedConflicts = "unresolvedConflicts"
    public static let emptyCommit = "emptyCommit"
    public static let notFullyMerged = "notFullyMerged"
    public static let noUpstream = "noUpstream"
    public static let divergent = "divergent"
    public static let unrelatedHistories = "unrelatedHistories"
    public static let checkedOutElsewhere = "checkedOutElsewhere"
    public static let locked = "locked"
    public static let markersRemain = "markersRemain"
    public static let applyFailed = "applyFailed"
    public static let noChanges = "noChanges"
    public static let autostashConflict = "autostashConflict"
    public static let identityMissing = "identityMissing"
    public static let failed = "failed"
}

/// diff / 按块操作的模式
public enum VCSDiffMode {
    public static let head = "head" // HEAD → 工作区
    public static let unstaged = "unstaged" // 暂存区 → 工作区
    public static let staged = "staged" // HEAD → 暂存区
    public static let commit = "commit" // 提交 → 父提交
    public static let range = "range" // base → head（head 空为工作区）
}

public struct VCSRepoInfo: Codable, Hashable {
    @DefBool public var isRepo: Bool
    public var root: String?
    public var vcs: String?
    /// repos：nested | submodule | worktree
    public var kind: String?
    public var parent: String?
}

/// 进行中的操作详情。SDK 声明为字符串，git 插件返回对象；两种都能解码（字符串放进 message）。
public struct VCSOperationDetail: Codable, Hashable {
    public var op: String?
    public var step: Int?
    public var total: Int?
    /// 变基中的分支
    public var headName: String?
    /// 变基目标提交
    public var onto: String?
    /// 正在应用 / 合并的提交
    public var current: String?
    /// MERGE_MSG 第一行，或字符串形式的详情
    public var message: String?

    public init(op: String? = nil, step: Int? = nil, total: Int? = nil, headName: String? = nil,
                onto: String? = nil, current: String? = nil, message: String? = nil) {
        self.op = op; self.step = step; self.total = total; self.headName = headName
        self.onto = onto; self.current = current; self.message = message
    }

    enum CodingKeys: String, CodingKey { case op, step, total, headName, onto, current, message }

    public init(from decoder: Decoder) throws {
        if let s = try? decoder.singleValueContainer().decode(String.self) {
            self.init(message: s)
            return
        }
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.init(op: try c.decodeIfPresent(String.self, forKey: .op),
                  step: try c.decodeIfPresent(Int.self, forKey: .step),
                  total: try c.decodeIfPresent(Int.self, forKey: .total),
                  headName: try c.decodeIfPresent(String.self, forKey: .headName),
                  onto: try c.decodeIfPresent(String.self, forKey: .onto),
                  current: try c.decodeIfPresent(String.self, forKey: .current),
                  message: try c.decodeIfPresent(String.self, forKey: .message))
    }
}

public struct VCSStatus: Codable, Hashable {
    @DefString public var branch: String // 分离头时为空
    @DefBool public var detached: Bool
    /// 当前提交短哈希
    public var head: String?
    public var upstream: String?
    @DefBool public var upstreamGone: Bool
    @DefInt public var ahead: Int
    @DefInt public var behind: Int
    /// merge | rebase | cherry-pick | revert | am
    public var operation: String?
    public var operationDetail: VCSOperationDetail?
    @DefArray public var files: [VCSFileChange]
    @DefBool public var untrackedTruncated: Bool
    @DefInt public var untrackedCount: Int
    @DefInt public var conflicts: Int
    @DefInt public var stashCount: Int
    /// HEAD 完整哈希
    public var headHash: String?
    /// 还没有任何提交
    @DefBool public var unborn: Bool

    public init() {}
}

/// Status：M 修改 | A 新增 | D 删除 | R 重命名 | C 复制 | U 冲突 | ? 未跟踪 | T 类型变化
public struct VCSFileChange: Codable, Hashable, Identifiable {
    public var path: String
    public var oldPath: String?
    @DefString public var status: String
    @DefBool public var staged: Bool
    @DefBool public var unstaged: Bool
    @DefBool public var conflicted: Bool
    @DefInt public var additions: Int
    @DefInt public var deletions: Int
    @DefBool public var binary: Bool
    /// 暂存区相对 HEAD 的状态字母（M A D R C T），空表示没变
    public var indexStatus: String?
    /// 工作区相对暂存区的状态字母（M D T ?），空表示没变
    public var worktreeStatus: String?
    /// UU 双方修改 | AA 双方新增 | DU 我方删除 | UD 对方删除 | AU 我方新增 | UA 对方新增 | DD 双方删除
    public var conflictType: String?
    @DefBool public var submodule: Bool
    public var id: String { path }

    public init(path: String, oldPath: String? = nil, status: String) {
        self.path = path; self.oldPath = oldPath; self.status = status
    }

    public var isUntracked: Bool { status == "?" }
}

public struct VCSDiffArgs: Codable, Hashable {
    public var path: String
    public var oldPath: String?
    /// commit 模式的提交（commit 非空且 mode 为空时即 commit 模式）
    public var commit: String?
    /// 上下文行数，默认 3；-1 表示整个文件
    public var context: Int?
    /// head | unstaged | staged | commit | range（见 VCSDiffMode）
    public var mode: String?
    public var staged: Bool?
    public var base: String?
    public var head: String?
    public var mergeBase: Bool?
    /// commit 模式对比第几个父提交，默认 1
    public var parent: Int?
    public var ignoreWhitespace: Bool?
    /// 不做 1 MB / 5000 行限制
    public var noLimit: Bool?

    public init(path: String, oldPath: String? = nil, commit: String? = nil, context: Int? = nil, mode: String? = nil,
                base: String? = nil, head: String? = nil, mergeBase: Bool? = nil, parent: Int? = nil,
                ignoreWhitespace: Bool? = nil, noLimit: Bool? = nil) {
        self.path = path; self.oldPath = oldPath; self.commit = commit; self.context = context; self.mode = mode
        self.base = base; self.head = head; self.mergeBase = mergeBase; self.parent = parent
        self.ignoreWhitespace = ignoreWhitespace; self.noLimit = noLimit
    }
}

public struct VCSDiff: Codable, Hashable {
    @DefString public var path: String
    public var oldPath: String?
    @DefBool public var binary: Bool
    @DefBool public var tooLarge: Bool
    @DefArray public var hunks: [VCSDiffHunk]
    public var language: String?
    /// 生成本 diff 的模式（按块操作要传回同样的 mode）
    public var mode: String?
    public var context: Int?
    /// A 新增 | D 删除 | M 修改 | R 重命名 | C 复制 | T 类型变化
    public var status: String?
    public var oldMode: String?
    public var newMode: String?
    public var similarity: Int?
    @DefInt public var additions: Int
    @DefInt public var deletions: Int
    @DefBool public var untracked: Bool

    public init() {}
}

public struct VCSDiffHunk: Codable, Hashable {
    @DefString public var header: String
    @DefInt public var oldStart: Int
    @DefInt public var oldLines: Int
    @DefInt public var newStart: Int
    @DefInt public var newLines: Int
    @DefArray public var lines: [VCSDiffLine]

    public init(header: String, oldStart: Int, oldLines: Int, newStart: Int, newLines: Int, lines: [VCSDiffLine]) {
        self.header = header; self.oldStart = oldStart; self.oldLines = oldLines
        self.newStart = newStart; self.newLines = newLines; self.lines = lines
    }
}

public struct VCSDiffLine: Codable, Hashable {
    @DefString public var kind: String // " " | "+" | "-"
    @DefInt public var old: Int
    @DefInt public var new: Int
    @DefString public var text: String
    /// 文件最后一行且没有换行符
    @DefBool public var noEol: Bool

    public init(kind: String, old: Int = 0, new: Int = 0, text: String) {
        self.kind = kind; self.old = old; self.new = new; self.text = text
    }
}

public struct VCSCommitArgs: Codable, Hashable {
    public var message: String
    /// 要提交的文件（含未跟踪的新文件），只提交这些路径的工作区内容；为空表示提交暂存区
    public var paths: [String]?
    /// 提交全部变更（paths 为空时有效）
    public var all: Bool?
    /// 部分提交：文件 → 块下标（基于 mode=head、上下文 hunkContext 的 diff）
    public var hunks: [String: [Int]]?
    public var hunkContext: Int?
    public var amend: Bool?
    public var push: Bool?
    /// 「名字 <邮箱>」
    public var author: String?
    public var signOff: Bool?
    /// 跳过 pre-commit / commit-msg 钩子
    public var noVerify: Bool?

    public init(message: String, paths: [String]? = nil, amend: Bool? = nil, push: Bool? = nil, all: Bool? = nil,
                hunks: [String: [Int]]? = nil, author: String? = nil, signOff: Bool? = nil, noVerify: Bool? = nil) {
        self.message = message; self.paths = paths; self.amend = amend; self.push = push; self.all = all
        self.hunks = hunks; self.author = author; self.signOff = signOff; self.noVerify = noVerify
    }
}

public struct VCSCommitResult: Codable, Hashable {
    @DefString public var hash: String
    public var short: String?
    public var push: VCSOpResult?
}

public struct VCSPushArgs: Codable, Hashable {
    public var remote: String?
    public var branch: String?
    /// 使用 --force-with-lease
    public var force: Bool?
    public var setUpstream: Bool?
    public var tags: Bool?
    /// tags 为 true 时：all（--tags）| follow（--follow-tags）
    public var tagsMode: String?
    public var noVerify: Bool?

    public init(remote: String? = nil, branch: String? = nil, force: Bool? = nil, setUpstream: Bool? = nil, tags: Bool? = nil,
                tagsMode: String? = nil, noVerify: Bool? = nil) {
        self.remote = remote; self.branch = branch; self.force = force; self.setUpstream = setUpstream; self.tags = tags
        self.tagsMode = tagsMode; self.noVerify = noVerify
    }
}

public struct VCSPullArgs: Codable, Hashable {
    /// merge | rebase | ff-only | config（按 git 配置）
    public var mode: String?
    public var remote: String?
    public var branch: String?
    public var autostash: Bool?
    public init(mode: String? = nil, remote: String? = nil, branch: String? = nil, autostash: Bool? = nil) {
        self.mode = mode; self.remote = remote; self.branch = branch; self.autostash = autostash
    }
}

public struct VCSFetchArgs: Codable, Hashable {
    public var remote: String?
    public var prune: Bool?
    public var tags: Bool?
    public init(remote: String? = nil, prune: Bool? = nil, tags: Bool? = nil) {
        self.remote = remote; self.prune = prune; self.tags = tags
    }
}

public struct VCSOpResult: Codable, Hashable {
    @DefBool public var ok: Bool
    /// 给人看的简要结果（已本地化）
    public var message: String?
    /// 原始输出（截断）
    public var output: String?
    @DefArray public var conflicts: [String]
    @DefBool public var needsAuth: Bool
    @DefBool public var rejected: Bool
    /// 结构化原因（见 VCSReason）
    public var reason: String?
    /// 相关文件（如会被覆盖的本地修改）
    @DefArray public var files: [String]
    /// 相关提交（如被删分支的提交，可用于恢复）
    public var hash: String?
    /// mergeTool 用到的工具
    public var tool: String?
    /// 远程操作（push/pull/fetch/clone/submoduleUpdate/mergeTool/tagPush）生成的任务，输出看 consoles/<taskId>/stream
    public var taskId: String?
    /// clone：克隆出的目录
    public var path: String?

    public init(ok: Bool = true, message: String? = nil) { self.ok = ok; self.message = message }
}

public struct VCSBranches: Codable, Hashable {
    @DefString public var current: String
    @DefArray public var local: [VCSBranch]
    @DefArray public var remote: [VCSBranch]
    @DefBool public var detached: Bool
    /// HEAD 完整哈希
    public var head: String?
    /// 最近检出过的本地分支（新的在前）
    @DefArray public var recent: [String]
}

public struct VCSBranch: Codable, Hashable, Identifiable {
    public var name: String
    public var upstream: String?
    @DefInt public var ahead: Int
    @DefInt public var behind: Int
    /// 短哈希
    public var head: String?
    public var subject: String?
    public var date: String?
    @DefBool public var current: Bool
    @DefBool public var gone: Bool
    /// 完整哈希
    public var hash: String?
    /// 远程分支所属远程
    public var remote: String?
    /// 在别的工作树里检出着
    public var worktree: String?
    public var author: String?
    public var id: String { name }

    public var parsedDate: Date? { date.flatMap(GoTime.parse) }
}

public struct VCSCheckoutArgs: Codable, Hashable {
    public var ref: String
    public var create: Bool?
    public var start: String?
    public var force: Bool?
    /// 储藏 → 检出 → 恢复（IDEA 的 Smart Checkout）
    public var smart: Bool?
    public init(ref: String, create: Bool? = nil, start: String? = nil, force: Bool? = nil, smart: Bool? = nil) {
        self.ref = ref; self.create = create; self.start = start; self.force = force; self.smart = smart
    }
}

public struct VCSBranchCreateArgs: Codable, Hashable {
    public var name: String
    public var start: String?
    public var checkout: Bool?
    public init(name: String, start: String? = nil, checkout: Bool? = nil) {
        self.name = name; self.start = start; self.checkout = checkout
    }
}

public struct VCSBranchDeleteArgs: Codable, Hashable {
    public var name: String
    public var force: Bool?
    public var remote: Bool?
    public init(name: String, force: Bool? = nil, remote: Bool? = nil) {
        self.name = name; self.force = force; self.remote = remote
    }
}

public struct VCSBranchRenameArgs: Codable, Hashable {
    public var old: String
    public var new: String
    public init(old: String, new: String) { self.old = old; self.new = new }
}

public struct VCSRefArgs: Codable, Hashable {
    public var ref: String
    /// cherryPick/revert 多个提交
    public var refs: [String]?
    public var noFF: Bool?
    public var squash: Bool?
    public var message: String?
    public var ffOnly: Bool?
    public var allowUnrelated: Bool?
    /// rebase --onto
    public var onto: String?
    /// cherry-pick -x
    public var recordOrigin: Bool?
    /// cherry-pick/revert -n
    public var noCommit: Bool?
    public var mainline: Int?
    /// tagDelete：同时删除这个远程上的标签
    public var remote: String?

    public init(ref: String, refs: [String]? = nil, noFF: Bool? = nil, squash: Bool? = nil, message: String? = nil,
                ffOnly: Bool? = nil, allowUnrelated: Bool? = nil, onto: String? = nil, recordOrigin: Bool? = nil,
                noCommit: Bool? = nil, remote: String? = nil) {
        self.ref = ref; self.refs = refs; self.noFF = noFF; self.squash = squash; self.message = message
        self.ffOnly = ffOnly; self.allowUnrelated = allowUnrelated; self.onto = onto; self.recordOrigin = recordOrigin
        self.noCommit = noCommit; self.remote = remote
    }
}

public struct VCSLogArgs: Codable, Hashable {
    /// 空表示当前分支；"--all" 表示全部分支
    public var ref: String?
    public var skip: Int?
    /// 默认 200
    public var limit: Int?
    public var author: String?
    public var grep: String?
    public var path: String?
    public var refs: [String]?
    public var authors: [String]?
    public var since: String?
    public var until: String?
    public var paths: [String]?
    /// topo（默认）| date
    public var order: String?
    public var firstParent: Bool?
    public var noGraph: Bool?

    public init(ref: String? = nil, skip: Int? = nil, limit: Int? = nil, author: String? = nil, grep: String? = nil, path: String? = nil,
                refs: [String]? = nil, since: String? = nil, until: String? = nil, firstParent: Bool? = nil, noGraph: Bool? = nil) {
        self.ref = ref; self.skip = skip; self.limit = limit; self.author = author; self.grep = grep; self.path = path
        self.refs = refs; self.since = since; self.until = until; self.firstParent = firstParent; self.noGraph = noGraph
    }
}

public struct VCSLog: Codable, Hashable {
    @DefArray public var commits: [VCSCommit]
    @DefBool public var hasMore: Bool
    /// 是否带提交图（按作者/关键词/日期过滤时为 false）；旧版没有这个字段时为 nil
    public var graph: Bool?
}

public struct VCSCommit: Codable, Hashable, Identifiable {
    public var hash: String
    @DefString public var short: String
    @DefArray public var parents: [String]
    @DefString public var author: String
    public var email: String?
    @DefString public var date: String
    @DefString public var subject: String
    @DefArray public var refs: [VCSRef]
    public var graph: VCSGraphRow?
    /// fileHistory：该提交里文件的路径（跟踪重命名）
    public var path: String?
    /// fileHistory：该提交对文件做了什么（A/M/D/R）
    public var pathStatus: String?
    public var id: String { hash }

    /// date 为 RFC3339 字符串
    public var parsedDate: Date? { GoTime.parse(date) }
}

public struct VCSRef: Codable, Hashable {
    public var name: String
    @DefString public var kind: String // head | local | remote | tag
    public init(name: String, kind: String) { self.name = name; self.kind = kind }
}

public struct VCSGraphRow: Codable, Hashable {
    @DefInt public var lane: Int
    @DefInt public var lanes: Int
    @DefArray public var edges: [VCSGraphEdge]
    @DefInt public var color: Int
    public init(lane: Int, lanes: Int, edges: [VCSGraphEdge], color: Int) {
        self.lane = lane; self.lanes = lanes; self.edges = edges; self.color = color
    }
}

public struct VCSGraphEdge: Codable, Hashable {
    public var from: Int
    public var to: Int
    @DefInt public var color: Int
    public init(from: Int, to: Int, color: Int) { self.from = from; self.to = to; self.color = color }
}

public struct VCSShowArgs: Codable, Hashable {
    public var hash: String
    /// 合并提交对比第几个父提交，默认 1
    public var parent: Int?
    public init(hash: String, parent: Int? = nil) { self.hash = hash; self.parent = parent }
}

public struct VCSCommitDetail: Codable, Hashable {
    public var commit: VCSCommit
    /// 主题行之后的正文
    public var body: String?
    @DefArray public var files: [VCSFileChange]
    /// 完整提交信息
    public var message: String?
    public var committer: String?
    public var committerEmail: String?
    public var committerDate: String?
    /// 文件列表对比的是第几个父提交
    public var parent: Int?
}

public struct VCSPathsArgs: Codable, Hashable {
    public var paths: [String]
    /// mergeTool 用：指定外部合并工具 id（mergeTools 的结果），nil 表示按设置/自动
    public var tool: String?
    public init(paths: [String], tool: String? = nil) { self.paths = paths; self.tool = tool }
}

/// discard 的参数（VCSPathsArgs 的超集）
public struct VCSDiscardArgs: Codable, Hashable {
    public var paths: [String]
    /// head（默认，恢复到 HEAD）| unstaged（只丢弃未暂存部分）
    public var mode: String?
    /// 新文件只取消暂存、未跟踪文件保留
    public var keepNew: Bool?
    public init(paths: [String], mode: String? = nil, keepNew: Bool? = nil) {
        self.paths = paths; self.mode = mode; self.keepNew = keepNew
    }
}

/// clean 的参数
public struct VCSCleanArgs: Codable, Hashable {
    public var paths: [String]
    public var all: Bool?
    public var ignored: Bool?
    public init(paths: [String], all: Bool? = nil, ignored: Bool? = nil) {
        self.paths = paths; self.all = all; self.ignored = ignored
    }
}

public struct VCSStashList: Codable, Hashable {
    @DefArray public var stashes: [VCSStash]
}

public struct VCSStash: Codable, Hashable, Identifiable {
    public var index: Int
    @DefString public var message: String
    public var branch: String?
    public var date: String?
    /// 储藏提交，可用 diff(commit) 查看内容
    public var hash: String?
    public var id: Int { index }

    public var parsedDate: Date? { date.flatMap(GoTime.parse) }
}

public struct VCSStashSaveArgs: Codable, Hashable {
    public var message: String?
    public var includeUntracked: Bool?
    /// 只储藏这些文件
    public var paths: [String]?
    /// 保留暂存区
    public var keepIndex: Bool?
    /// 只储藏已暂存的
    public var staged: Bool?
    public init(message: String? = nil, includeUntracked: Bool? = nil, paths: [String]? = nil, keepIndex: Bool? = nil, staged: Bool? = nil) {
        self.message = message; self.includeUntracked = includeUntracked; self.paths = paths
        self.keepIndex = keepIndex; self.staged = staged
    }
}

public struct VCSStashArgs: Codable, Hashable {
    public var index: Int
    public var pop: Bool?
    /// 同时恢复暂存区（--index）
    public var restoreIndex: Bool?
    /// 恢复为新分支（git stash branch）
    public var branch: String?
    /// stashDrop：清空全部
    public var all: Bool?
    public init(index: Int, pop: Bool? = nil, restoreIndex: Bool? = nil, branch: String? = nil, all: Bool? = nil) {
        self.index = index; self.pop = pop; self.restoreIndex = restoreIndex; self.branch = branch; self.all = all
    }
}

/// stashShow 的结果
public struct VCSStashShow: Codable, Hashable {
    @DefString public var hash: String
    @DefArray public var files: [VCSFileChange]
    /// 储藏里的未跟踪文件
    @DefArray public var untracked: [String]
}

public struct VCSResolveArgs: Codable, Hashable {
    public var path: String
    /// ours | theirs | resolved
    public var side: String
    public var paths: [String]?
    /// resolved 时文件里还有冲突标记也标记解决
    public var force: Bool?
    public init(path: String, side: String, paths: [String]? = nil, force: Bool? = nil) {
        self.path = path; self.side = side; self.paths = paths; self.force = force
    }
}

public struct VCSResetArgs: Codable, Hashable {
    public var ref: String
    /// soft | mixed | hard | keep | merge
    public var mode: String
    public init(ref: String, mode: String) { self.ref = ref; self.mode = mode }
}

public struct VCSUndoCommitArgs: Codable, Hashable {
    /// 提交已推送也撤销
    public var force: Bool?
    public init(force: Bool? = nil) { self.force = force }
}

public struct VCSTags: Codable, Hashable {
    @DefArray public var tags: [VCSTag]
}

public struct VCSTag: Codable, Hashable, Identifiable {
    public var name: String
    /// 指向的提交
    @DefString public var hash: String
    public var subject: String?
    public var date: String?
    @DefBool public var annotated: Bool
    /// 附注标签的说明
    public var message: String?
    public var tagger: String?
    public var id: String { name }
}

public struct VCSTagCreateArgs: Codable, Hashable {
    public var name: String
    public var ref: String?
    /// 有 message 时建附注标签
    public var message: String?
    public var force: Bool?
    /// 创建后推送到远程
    public var push: Bool?
    public var remote: String?
    public init(name: String, ref: String? = nil, message: String? = nil, force: Bool? = nil, push: Bool? = nil, remote: String? = nil) {
        self.name = name; self.ref = ref; self.message = message; self.force = force; self.push = push; self.remote = remote
    }
}

/// tagPush 的参数
public struct VCSTagPushArgs: Codable, Hashable {
    public var ref: String
    public var remote: String?
    public var delete: Bool?
    public var force: Bool?
    public init(ref: String, remote: String? = nil, delete: Bool? = nil, force: Bool? = nil) {
        self.ref = ref; self.remote = remote; self.delete = delete; self.force = force
    }
}

public struct VCSRemotes: Codable, Hashable {
    @DefArray public var remotes: [VCSRemote]
}

public struct VCSRemote: Codable, Hashable, Identifiable {
    public var name: String
    @DefString public var fetchUrl: String
    public var pushUrl: String?
    /// 网页地址（GitHub/GitLab 等）
    public var webUrl: String?
    public var id: String { name }
    public init(name: String, fetchUrl: String, pushUrl: String? = nil) {
        self.name = name; self.fetchUrl = fetchUrl; self.pushUrl = pushUrl
    }
}

public struct VCSFileHistoryArgs: Codable, Hashable {
    public var path: String
    public var limit: Int?
    public var skip: Int?
    /// 默认 HEAD；"--all" 表示全部分支
    public var ref: String?
    /// 跟踪重命名，默认 true
    public var follow: Bool?
    public init(path: String, limit: Int? = nil, skip: Int? = nil, ref: String? = nil, follow: Bool? = nil) {
        self.path = path; self.limit = limit; self.skip = skip; self.ref = ref; self.follow = follow
    }
}

public struct VCSCloneArgs: Codable, Hashable {
    public var url: String
    public var dest: String
    public var branch: String?
    public var depth: Int?
    public var recurseSubmodules: Bool?
    public init(url: String, dest: String, branch: String? = nil, depth: Int? = nil, recurseSubmodules: Bool? = nil) {
        self.url = url; self.dest = dest; self.branch = branch; self.depth = depth; self.recurseSubmodules = recurseSubmodules
    }
}

public struct VCSInitArgs: Codable, Hashable {
    public var initialBranch: String?
    public init(initialBranch: String? = nil) { self.initialBranch = initialBranch }
}

public struct VCSHunksArgs: Codable, Hashable {
    public var path: String
    /// VCSDiff.hunks 的下标
    public var hunks: [Int]
    /// 取 diff 时的上下文行数，需与界面取 diff 时一致
    public var context: Int?
    /// 取 diff 时用的 mode（界面显示 head diff 时传 head）
    public var mode: String?
    /// 所选块的 @@ 头，用来确认 diff 没过期
    public var headers: [String]?
    public var oldPath: String?
    public init(path: String, hunks: [Int], context: Int? = nil, mode: String? = nil, headers: [String]? = nil, oldPath: String? = nil) {
        self.path = path; self.hunks = hunks; self.context = context; self.mode = mode; self.headers = headers; self.oldPath = oldPath
    }
}

public struct VCSFileContentArgs: Codable, Hashable {
    public var path: String
    /// 空 = 工作区，":" = 暂存区，其它为提交 / 分支
    public var ref: String?
    public var maxBytes: Int?
    public init(path: String, ref: String? = nil, maxBytes: Int? = nil) {
        self.path = path; self.ref = ref; self.maxBytes = maxBytes
    }
}

public struct VCSFileContent: Codable, Hashable {
    @DefString public var path: String
    public var ref: String?
    @DefBool public var exists: Bool
    @DefInt public var size: Int
    @DefBool public var binary: Bool
    @DefBool public var tooLarge: Bool
    public var text: String?
    /// 二进制（图片对比）或非 UTF-8 文本
    public var base64: String?
    @DefBool public var notUtf8: Bool

    public var data: Data? {
        if let base64 { return Data(base64Encoded: base64) }
        return text.map { Data($0.utf8) }
    }
}

/// containingBranches 的结果
public struct VCSContainingBranches: Codable, Hashable {
    @DefArray public var refs: [VCSRef]
    @DefBool public var truncated: Bool
}

public struct VCSMergeToolInfo: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var name: String
    @DefString public var path: String
    @DefBool public var builtin: Bool
}

/// mergeTools 的结果
public struct VCSMergeTools: Codable, Hashable {
    /// 用户 git 配置里的 merge.tool
    public var configured: String?
    @DefArray public var tools: [VCSMergeToolInfo]
}

public struct VCSRewordArgs: Codable, Hashable {
    public var hash: String
    public var message: String
    public init(hash: String, message: String) { self.hash = hash; self.message = message }
}

public struct VCSRebasePlan: Codable, Hashable {
    /// 从这个提交之后开始（不含它）；空为 --root
    public var base: String
    /// 从旧到新
    public var steps: [VCSRebaseStep]
    public init(base: String, steps: [VCSRebaseStep]) { self.base = base; self.steps = steps }
}

public struct VCSRebaseStep: Codable, Hashable {
    public var hash: String
    public var action: String // pick | reword | squash | fixup | drop
    public var message: String?
    public init(hash: String, action: String, message: String? = nil) {
        self.hash = hash; self.action = action; self.message = message
    }
}

public struct VCSCheckoutFileArgs: Codable, Hashable {
    public var path: String
    public var ref: String
    public init(path: String, ref: String) { self.path = path; self.ref = ref }
}

public struct VCSCompareArgs: Codable, Hashable {
    public var base: String
    /// 为空表示当前分支
    public var head: String?
    public init(base: String, head: String? = nil) { self.base = base; self.head = head }
}

public struct VCSCompare: Codable, Hashable {
    /// 在 head 不在 base
    @DefArray public var ahead: [VCSCommit]
    /// 在 base 不在 head
    @DefArray public var behind: [VCSCommit]
    /// merge-base(base, head) 到 head 的文件变化
    @DefArray public var files: [VCSFileChange]
    public var mergeBase: String?
    @DefBool public var truncated: Bool
}

public struct VCSPushPreview: Codable, Hashable {
    @DefString public var remote: String
    @DefString public var branch: String
    @DefString public var remoteRef: String
    @DefBool public var newBranch: Bool
    @DefArray public var commits: [VCSCommit]
    /// 远端领先的提交数（>0 时推送会被拒）
    @DefInt public var behind: Int
    /// 推送时会设置上游
    @DefBool public var setUpstream: Bool
    /// 提交太多，只列出前 500 个
    @DefBool public var truncated: Bool
}

public struct VCSBlameArgs: Codable, Hashable {
    public var path: String
    public var ref: String?
    public init(path: String, ref: String? = nil) { self.path = path; self.ref = ref }
}

public struct VCSBlame: Codable, Hashable {
    @DefArray public var lines: [VCSBlameLine]
}

public struct VCSBlameLine: Codable, Hashable {
    @DefInt public var line: Int
    @DefString public var hash: String
    @DefString public var author: String
    @DefString public var date: String
    public var summary: String?
}

public struct VCSSetUpstreamArgs: Codable, Hashable {
    public var branch: String?
    /// 如 origin/main；空表示取消跟踪
    public var upstream: String
    public init(branch: String? = nil, upstream: String) { self.branch = branch; self.upstream = upstream }
}

public struct VCSSubmodules: Codable, Hashable {
    @DefArray public var submodules: [VCSSubmodule]
}

public struct VCSSubmodule: Codable, Hashable, Identifiable {
    public var path: String
    public var url: String?
    public var head: String?
    @DefString public var status: String
    public var id: String { path }
}

public struct VCSSubmoduleUpdateArgs: Codable, Hashable {
    public var paths: [String]?
    public var initialize: Bool?
    public var recursive: Bool?
    public var remote: Bool?
    enum CodingKeys: String, CodingKey { case paths, initialize = "init", recursive, remote }
    public init(paths: [String]? = nil, initialize: Bool? = nil, recursive: Bool? = nil, remote: Bool? = nil) {
        self.paths = paths; self.initialize = initialize; self.recursive = recursive; self.remote = remote
    }
}

public struct VCSWorktrees: Codable, Hashable {
    @DefArray public var worktrees: [VCSWorktree]
}

public struct VCSWorktree: Codable, Hashable, Identifiable {
    public var path: String
    public var branch: String?
    public var head: String?
    @DefBool public var main: Bool
    @DefBool public var locked: Bool
    public var id: String { path }
}

public struct VCSWorktreeAddArgs: Codable, Hashable {
    public var path: String
    public var branch: String?
    public var newBranch: Bool?
    public var start: String?
    public init(path: String, branch: String? = nil, newBranch: Bool? = nil, start: String? = nil) {
        self.path = path; self.branch = branch; self.newBranch = newBranch; self.start = start
    }
}

public struct VCSWorktreeRemoveArgs: Codable, Hashable {
    public var path: String
    public var force: Bool?
    public init(path: String, force: Bool? = nil) { self.path = path; self.force = force }
}

public struct VCSConfigGetArgs: Codable, Hashable {
    public var keys: [String]
    /// 只读全局配置
    public var global: Bool?
    public init(keys: [String], global: Bool? = nil) { self.keys = keys; self.global = global }
}

public struct VCSConfig: Codable, Hashable {
    @DefDict public var values: [String: String]
    @DefDict public var scopes: [String: String]
}

public struct VCSConfigSetArgs: Codable, Hashable {
    public var key: String
    public var value: String
    public var global: Bool?
    public var unset: Bool?
    public init(key: String, value: String, global: Bool? = nil, unset: Bool? = nil) {
        self.key = key; self.value = value; self.global = global; self.unset = unset
    }
}

public struct VCSIgnoreArgs: Codable, Hashable {
    public var patterns: [String]
    /// 默认仓库根 .gitignore；"exclude" 表示 .git/info/exclude（不提交）；也可为子目录的 .gitignore
    public var file: String?
    public init(patterns: [String], file: String? = nil) { self.patterns = patterns; self.file = file }
}

public struct VCSReposArgs: Codable, Hashable {
    public var maxDepth: Int?
    public init(maxDepth: Int? = nil) { self.maxDepth = maxDepth }
}

public struct VCSRepoList: Codable, Hashable {
    @DefArray public var repos: [VCSRepoInfo]
}

/// vcs.overview 的一项：工作区里的一个仓库（工作区内全部工程的全部仓库，去重）。
/// 写操作用 vcs.* {projectId, root}。status 是插件返回的 VCSStatus 超集（不含增删行数）；取状态失败时 status 为空、error 有说明。
public struct VCSRepoOverview: Codable, Hashable, Identifiable {
    @DefString public var projectId: String
    @DefString public var projectName: String
    /// 仓库绝对路径（调 vcs.* 时作为 root 传）
    @DefString public var root: String
    /// 相对工程根：主仓库为 "" 或 "."，工程在仓库子目录里时为 ".."
    @DefString public var relPath: String
    /// 仓库目录名
    @DefString public var name: String
    @DefString public var vcs: String
    public var status: VCSStatus?
    public var error: String?
    public var id: String { root }

    public init(projectId: String, projectName: String, root: String, relPath: String = "", name: String, vcs: String = "git",
                status: VCSStatus? = nil, error: String? = nil) {
        self.projectId = projectId; self.projectName = projectName; self.root = root; self.relPath = relPath
        self.name = name; self.vcs = vcs; self.status = status; self.error = error
    }
}

/// vcs.changed 事件的 data
public struct VCSChangedEvent: Codable, Hashable {
    @DefString public var projectId: String
    public var root: String?
}

/// 没有参数的 VCS 操作
public struct VCSNoArgs: Codable, Hashable { public init() {} }

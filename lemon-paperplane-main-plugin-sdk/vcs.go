package sdk

// VCS 操作（vcs/call 的 op）。界面与 API 是 VCS 无关的，git 插件实现这些操作；
// 以后的 svn 插件实现同一套（不支持的返回 ErrCodeNotSupported）。
const (
	VCSOpDetect       = "detect"       // args: {}                         → VCSRepoInfo
	VCSOpStatus       = "status"       // args: {}                         → VCSStatus
	VCSOpDiff         = "diff"         // args: VCSDiffArgs                → VCSDiff
	VCSOpCommit       = "commit"       // args: VCSCommitArgs              → VCSCommitResult
	VCSOpPush         = "push"         // args: VCSPushArgs                → VCSOpResult
	VCSOpPull         = "pull"         // args: VCSPullArgs                → VCSOpResult
	VCSOpFetch        = "fetch"        // args: VCSFetchArgs               → VCSOpResult
	VCSOpBranches     = "branches"     // args: {}                         → VCSBranches
	VCSOpCheckout     = "checkout"     // args: VCSCheckoutArgs            → VCSOpResult
	VCSOpBranchCreate = "branchCreate" // args: VCSBranchCreateArgs        → VCSOpResult
	VCSOpBranchDelete = "branchDelete" // args: VCSBranchDeleteArgs        → VCSOpResult
	VCSOpBranchRename = "branchRename" // args: VCSBranchRenameArgs        → VCSOpResult
	VCSOpMerge        = "merge"        // args: VCSRefArgs                 → VCSOpResult
	VCSOpRebase       = "rebase"       // args: VCSRefArgs                 → VCSOpResult
	VCSOpAbort        = "abort"        // args: {}  中止进行中的 merge/rebase/cherry-pick/revert → VCSOpResult
	VCSOpContinue     = "continue"     // args: {}                         → VCSOpResult
	VCSOpLog          = "log"          // args: VCSLogArgs                 → VCSLog
	VCSOpShow         = "show"         // args: VCSShowArgs                → VCSCommitDetail
	VCSOpDiscard      = "discard"      // args: VCSPathsArgs               → VCSOpResult
	VCSOpStashList    = "stashList"    // args: {}                         → VCSStashList
	VCSOpStashSave    = "stashSave"    // args: VCSStashSaveArgs           → VCSOpResult
	VCSOpStashApply   = "stashApply"   // args: VCSStashArgs               → VCSOpResult
	VCSOpStashDrop    = "stashDrop"    // args: VCSStashArgs               → VCSOpResult
	VCSOpResolve      = "resolve"      // args: VCSResolveArgs             → VCSOpResult
	VCSOpMergeTool    = "mergeTool"    // args: VCSPathsArgs  用外部工具合并 → VCSOpResult
	VCSOpCherryPick   = "cherryPick"   // args: VCSRefArgs                 → VCSOpResult
	VCSOpRevert       = "revert"       // args: VCSRefArgs                 → VCSOpResult
	VCSOpReset        = "reset"        // args: VCSResetArgs               → VCSOpResult
	VCSOpTags         = "tags"         // args: {}                         → VCSTags
	VCSOpTagCreate    = "tagCreate"    // args: VCSTagCreateArgs           → VCSOpResult
	VCSOpTagDelete    = "tagDelete"    // args: VCSRefArgs                 → VCSOpResult
	VCSOpRemotes      = "remotes"      // args: {}                         → VCSRemotes
	VCSOpRemoteSet    = "remoteSet"    // args: VCSRemote  新增或修改 url   → VCSOpResult
	VCSOpRemoteRemove = "remoteRemove" // args: VCSRefArgs                 → VCSOpResult
	VCSOpFileHistory  = "fileHistory"  // args: VCSFileHistoryArgs         → VCSLog
	VCSOpClone        = "clone"        // args: VCSCloneArgs（Root 为空） → VCSOpResult
	VCSOpInit         = "init"         // args: {}                         → VCSOpResult

	// 暂存区模式（IDEA 的 staging area）与按块提交
	VCSOpStage        = "stage"        // args: VCSPathsArgs               → VCSOpResult
	VCSOpUnstage      = "unstage"      // args: VCSPathsArgs               → VCSOpResult
	VCSOpStageHunks   = "stageHunks"   // args: VCSHunksArgs  暂存某文件的部分块 → VCSOpResult
	VCSOpUnstageHunks = "unstageHunks" // args: VCSHunksArgs               → VCSOpResult
	VCSOpDiscardHunks = "discardHunks" // args: VCSHunksArgs  丢弃部分块   → VCSOpResult

	// 提交历史改写
	VCSOpUndoCommit        = "undoCommit"        // args: {}  撤销最近一次提交，改动保留在工作区 → VCSOpResult
	VCSOpReword            = "reword"            // args: VCSRewordArgs              → VCSOpResult
	VCSOpRebaseInteractive = "rebaseInteractive" // args: VCSRebasePlan     → VCSOpResult
	VCSOpCheckoutFile      = "checkoutFile"      // args: VCSCheckoutFileArgs  把文件恢复到某版本 → VCSOpResult

	// 比较与查看
	VCSOpCompare     = "compare"     // args: VCSCompareArgs  两个引用之间的提交与文件差异 → VCSCompare
	VCSOpPushPreview = "pushPreview" // args: VCSPushArgs  将要推送的提交 → VCSPushPreview
	VCSOpBlame       = "blame"       // args: VCSBlameArgs               → VCSBlame
	VCSOpSetUpstream = "setUpstream" // args: VCSSetUpstreamArgs         → VCSOpResult

	// 仓库结构与配置
	VCSOpSubmodules      = "submodules"      // args: {}                   → VCSSubmodules
	VCSOpSubmoduleUpdate = "submoduleUpdate" // args: VCSSubmoduleUpdateArgs → VCSOpResult
	VCSOpWorktrees       = "worktrees"       // args: {}                   → VCSWorktrees
	VCSOpWorktreeAdd     = "worktreeAdd"     // args: VCSWorktreeAddArgs   → VCSOpResult
	VCSOpWorktreeRemove  = "worktreeRemove"  // args: VCSWorktreeRemoveArgs → VCSOpResult
	VCSOpConfigGet       = "configGet"       // args: VCSConfigGetArgs     → VCSConfig
	VCSOpConfigSet       = "configSet"       // args: VCSConfigSetArgs     → VCSOpResult
	VCSOpIgnore          = "ignore"          // args: VCSIgnoreArgs  追加到 .gitignore → VCSOpResult
	VCSOpClean           = "clean"           // args: VCSPathsArgs  删除未跟踪文件 → VCSOpResult
	VCSOpRepos           = "repos"           // args: VCSReposArgs  在目录下找所有仓库（含嵌套/子模块）→ VCSRepoList

	// git 插件提供的扩展操作（其他 VCS 可不实现）
	VCSOpSkip               = "skip"               // args: {}  跳过当前提交（变基遇到空提交/冲突时）→ VCSOpResult
	VCSOpFileContent        = "fileContent"        // args: VCSFileContentArgs → VCSFileContent（图片对比等）
	VCSOpContainingBranches = "containingBranches" // args: VCSShowArgs → {refs: [VCSRef], truncated}
	VCSOpTagPush            = "tagPush"            // args: {ref, remote, delete, force} → VCSOpResult
	VCSOpMergeTools         = "mergeTools"         // args: {} → 检测到的外部合并工具
	VCSOpStashShow          = "stashShow"          // args: VCSStashArgs → 储藏中的文件
)

type VCSRepoInfo struct {
	IsRepo bool   `json:"isRepo"`
	Root   string `json:"root,omitempty"`
	VCS    string `json:"vcs,omitempty"`
}

// VCSStatus 是工作区状态。
type VCSStatus struct {
	Branch             string          `json:"branch"` // 分离头时为空
	Detached           bool            `json:"detached,omitempty"`
	Head               string          `json:"head,omitempty"` // 当前提交短哈希
	Upstream           string          `json:"upstream,omitempty"`
	UpstreamGone       bool            `json:"upstreamGone,omitempty"`
	Ahead              int             `json:"ahead"`
	Behind             int             `json:"behind"`
	Operation          string          `json:"operation,omitempty"`       // merge | rebase | cherry-pick | revert | ""
	OperationDetail    any             `json:"operationDetail,omitempty"` // 进行中操作的细节（git 插件返回对象：onto、step、total、head 等）
	Files              []VCSFileChange `json:"files"`
	UntrackedTruncated bool            `json:"untrackedTruncated,omitempty"` // 未跟踪文件过多被截断
	Conflicts          int             `json:"conflicts"`
	StashCount         int             `json:"stashCount"`
}

// VCSFileChange 是一个变更文件。
// Status：M 修改 | A 新增 | D 删除 | R 重命名 | C 复制 | U 冲突 | ? 未跟踪 | T 类型变化
type VCSFileChange struct {
	Path           string `json:"path"` // 相对仓库根，使用 "/"
	OldPath        string `json:"oldPath,omitempty"`
	Status         string `json:"status"`
	Staged         bool   `json:"staged,omitempty"`
	Unstaged       bool   `json:"unstaged,omitempty"`
	Conflicted     bool   `json:"conflicted,omitempty"`
	Additions      int    `json:"additions,omitempty"`
	Deletions      int    `json:"deletions,omitempty"`
	Binary         bool   `json:"binary,omitempty"`
	IndexStatus    string `json:"indexStatus,omitempty"`    // 暂存区相对 HEAD 的状态字母
	WorktreeStatus string `json:"worktreeStatus,omitempty"` // 工作区相对暂存区的状态字母
	ConflictType   string `json:"conflictType,omitempty"`   // git 的两字母代码：UU 双方修改、AA 双方新增、DU 我方删除、UD 对方删除、AU/UA、DD
}

type VCSDiffArgs struct {
	Path    string `json:"path"`
	OldPath string `json:"oldPath,omitempty"`
	Commit  string `json:"commit,omitempty"`  // 为空：工作区对 HEAD；非空：该提交对其第一个父提交
	Context int    `json:"context,omitempty"` // 上下文行数，默认 3；-1 表示整个文件
}

type VCSDiff struct {
	Path     string        `json:"path"`
	OldPath  string        `json:"oldPath,omitempty"`
	Binary   bool          `json:"binary,omitempty"`
	TooLarge bool          `json:"tooLarge,omitempty"`
	Hunks    []VCSDiffHunk `json:"hunks"`
	Language string        `json:"language,omitempty"` // 按扩展名猜的语言名，界面可用于等宽着色（不做语法高亮也可以）
}

type VCSDiffHunk struct {
	Header   string        `json:"header"` // @@ -1,3 +1,4 @@ ...
	OldStart int           `json:"oldStart"`
	OldLines int           `json:"oldLines"`
	NewStart int           `json:"newStart"`
	NewLines int           `json:"newLines"`
	Lines    []VCSDiffLine `json:"lines"`
}

type VCSDiffLine struct {
	Kind string `json:"kind"`          // " " 上下文 | "+" 新增 | "-" 删除
	Old  int    `json:"old,omitempty"` // 旧行号，新增行为 0
	New  int    `json:"new,omitempty"` // 新行号，删除行为 0
	Text string `json:"text"`
}

type VCSCommitArgs struct {
	Message string           `json:"message"`
	Paths   []string         `json:"paths,omitempty"` // 要提交的文件（含未跟踪的新文件），只提交这些路径的工作区内容（IDEA 默认模式）；为空表示提交暂存区
	All     bool             `json:"all,omitempty"`   // 提交全部变更
	Hunks   map[string][]int `json:"hunks,omitempty"` // 只提交这些文件的部分块（下标基于 mode=head 的 diff）
	Amend   bool             `json:"amend,omitempty"`
	Push    bool             `json:"push,omitempty"` // 提交后立即推送
	Author  string           `json:"author,omitempty"`
	SignOff bool             `json:"signOff,omitempty"`
}

type VCSCommitResult struct {
	Hash string       `json:"hash"`
	Push *VCSOpResult `json:"push,omitempty"`
}

type VCSPushArgs struct {
	Remote      string `json:"remote,omitempty"` // 默认上游或 origin
	Branch      string `json:"branch,omitempty"` // 默认当前分支
	Force       bool   `json:"force,omitempty"`  // 使用 --force-with-lease
	SetUpstream bool   `json:"setUpstream,omitempty"`
	Tags        bool   `json:"tags,omitempty"`
}

type VCSPullArgs struct {
	Mode      string `json:"mode,omitempty"` // merge（默认）| rebase | ff-only
	Remote    string `json:"remote,omitempty"`
	Branch    string `json:"branch,omitempty"`
	Autostash bool   `json:"autostash,omitempty"` // 有本地修改时先暂存再拉取（IDEA 的「暂存本地更改」）
}

type VCSFetchArgs struct {
	Remote string `json:"remote,omitempty"` // 空表示 --all
	Prune  bool   `json:"prune,omitempty"`
}

// VCSOpResult 是写操作的通用结果。
type VCSOpResult struct {
	OK        bool     `json:"ok"`
	Message   string   `json:"message,omitempty"`   // 给人看的简要结果
	Output    string   `json:"output,omitempty"`    // 原始输出（截断）
	Conflicts []string `json:"conflicts,omitempty"` // 产生冲突的文件
	NeedsAuth bool     `json:"needsAuth,omitempty"` // 认证失败
	Rejected  bool     `json:"rejected,omitempty"`  // 推送被拒（需要先拉取）
	Reason    string   `json:"reason,omitempty"`    // 结构化原因：conflicts | localChanges | rejected | needsAuth | upToDate | notFullyMerged | emptyCommit ...（界面据此决定弹什么框）
	Files     []string `json:"files,omitempty"`     // 相关文件（如会被覆盖的本地修改）
	Hash      string   `json:"hash,omitempty"`      // 相关提交（如被删分支的提交，可用于恢复）
}

type VCSBranches struct {
	Current string      `json:"current"`
	Local   []VCSBranch `json:"local"`
	Remote  []VCSBranch `json:"remote"`
}

type VCSBranch struct {
	Name     string `json:"name"`
	Upstream string `json:"upstream,omitempty"`
	Ahead    int    `json:"ahead,omitempty"`
	Behind   int    `json:"behind,omitempty"`
	Head     string `json:"head,omitempty"`
	Subject  string `json:"subject,omitempty"`
	Date     string `json:"date,omitempty"` // RFC3339
	Current  bool   `json:"current,omitempty"`
	Gone     bool   `json:"gone,omitempty"` // 上游已删除
}

type VCSCheckoutArgs struct {
	Ref    string `json:"ref"`              // 分支名、远程分支（自动建同名本地跟踪分支）、标签或提交
	Create bool   `json:"create,omitempty"` // 新建分支
	Start  string `json:"start,omitempty"`  // 新建分支的起点
	Force  bool   `json:"force,omitempty"`  // 丢弃本地修改
}

type VCSBranchCreateArgs struct {
	Name     string `json:"name"`
	Start    string `json:"start,omitempty"`
	Checkout bool   `json:"checkout,omitempty"`
}

type VCSBranchDeleteArgs struct {
	Name   string `json:"name"`
	Force  bool   `json:"force,omitempty"`
	Remote bool   `json:"remote,omitempty"` // 删除远程分支（push --delete）
}

type VCSBranchRenameArgs struct {
	Old string `json:"old"`
	New string `json:"new"`
}

type VCSRefArgs struct {
	Ref     string   `json:"ref"`
	Refs    []string `json:"refs,omitempty"`    // cherryPick/revert 多个提交
	NoFF    bool     `json:"noFF,omitempty"`    // merge --no-ff
	Squash  bool     `json:"squash,omitempty"`  // merge --squash
	Message string   `json:"message,omitempty"` // merge 提交信息
}

type VCSLogArgs struct {
	Ref    string `json:"ref,omitempty"` // 空表示当前分支；"--all" 表示全部分支
	Skip   int    `json:"skip,omitempty"`
	Limit  int    `json:"limit,omitempty"` // 默认 200
	Author string `json:"author,omitempty"`
	Grep   string `json:"grep,omitempty"`
	Path   string `json:"path,omitempty"`
}

type VCSLog struct {
	Commits []VCSCommit `json:"commits"`
	HasMore bool        `json:"hasMore"`
}

// VCSCommit 带预先算好的提交图，三端界面直接画，不各自实现图算法。
type VCSCommit struct {
	Hash    string       `json:"hash"`
	Short   string       `json:"short"`
	Parents []string     `json:"parents,omitempty"`
	Author  string       `json:"author"`
	Email   string       `json:"email,omitempty"`
	Date    string       `json:"date"` // RFC3339
	Subject string       `json:"subject"`
	Refs    []VCSRef     `json:"refs,omitempty"`
	Graph   *VCSGraphRow `json:"graph,omitempty"`
}

type VCSRef struct {
	Name string `json:"name"`
	Kind string `json:"kind"` // head | local | remote | tag
}

// VCSGraphRow 描述一行提交图：Lane 为本提交所在列；Edges 为本行到下一行的连线。
type VCSGraphRow struct {
	Lane  int            `json:"lane"`
	Lanes int            `json:"lanes"` // 本行总列数
	Edges []VCSGraphEdge `json:"edges,omitempty"`
	Color int            `json:"color"` // 本提交所在线的颜色序号
}

type VCSGraphEdge struct {
	From  int `json:"from"` // 本行列号
	To    int `json:"to"`   // 下一行列号
	Color int `json:"color"`
}

type VCSShowArgs struct {
	Hash string `json:"hash"`
}

type VCSCommitDetail struct {
	Commit VCSCommit       `json:"commit"`
	Body   string          `json:"body,omitempty"`
	Files  []VCSFileChange `json:"files"`
}

type VCSPathsArgs struct {
	Paths []string `json:"paths"`
	Tool  string   `json:"tool,omitempty"` // mergeTool：指定外部合并工具（mergeTools 返回的 id），空表示按设置/自动
}

type VCSStashList struct {
	Stashes []VCSStash `json:"stashes"`
}

type VCSStash struct {
	Index   int    `json:"index"`
	Message string `json:"message"`
	Branch  string `json:"branch,omitempty"`
	Date    string `json:"date,omitempty"`
}

type VCSStashSaveArgs struct {
	Message          string `json:"message,omitempty"`
	IncludeUntracked bool   `json:"includeUntracked,omitempty"`
}

type VCSStashArgs struct {
	Index int  `json:"index"`
	Pop   bool `json:"pop,omitempty"`
}

type VCSResolveArgs struct {
	Path string `json:"path"`
	Side string `json:"side"` // ours | theirs | resolved（用户已在外部工具合并好，标记解决）
}

type VCSResetArgs struct {
	Ref  string `json:"ref"`
	Mode string `json:"mode"` // soft | mixed | hard
}

type VCSTags struct {
	Tags []VCSTag `json:"tags"`
}

type VCSTag struct {
	Name    string `json:"name"`
	Hash    string `json:"hash"`
	Subject string `json:"subject,omitempty"`
	Date    string `json:"date,omitempty"`
}

type VCSTagCreateArgs struct {
	Name    string `json:"name"`
	Ref     string `json:"ref,omitempty"`
	Message string `json:"message,omitempty"`
}

type VCSRemotes struct {
	Remotes []VCSRemote `json:"remotes"`
}

type VCSRemote struct {
	Name     string `json:"name"`
	FetchURL string `json:"fetchUrl"`
	PushURL  string `json:"pushUrl,omitempty"`
}

type VCSFileHistoryArgs struct {
	Path  string `json:"path"`
	Limit int    `json:"limit,omitempty"`
}

type VCSCloneArgs struct {
	URL    string `json:"url"`
	Dest   string `json:"dest"` // 目标目录（不存在或为空）
	Branch string `json:"branch,omitempty"`
	Depth  int    `json:"depth,omitempty"`
}

type VCSHunksArgs struct {
	Path    string   `json:"path"`
	Hunks   []int    `json:"hunks"`             // VCSDiff.Hunks 的下标
	Mode    string   `json:"mode,omitempty"`    // 取 diff 时用的 mode：暂存用 unstaged、取消暂存用 staged、丢弃用 unstaged 或 head
	Headers []string `json:"headers,omitempty"` // 对应块的 @@ 头，用于在文件变化后校准下标
	Context int      `json:"context,omitempty"` // 生成 diff 时用的上下文行数，需与界面取 diff 时一致
}

type VCSFileContentArgs struct {
	Path     string `json:"path"`
	Ref      string `json:"ref,omitempty"` // 空=工作区，":"=暂存区
	MaxBytes int    `json:"maxBytes,omitempty"`
}

type VCSFileContent struct {
	Text   string `json:"text,omitempty"`
	Base64 string `json:"base64,omitempty"` // 二进制或非 UTF-8
	Size   int64  `json:"size"`
}

type VCSRewordArgs struct {
	Hash    string `json:"hash"` // 为空或 HEAD 表示最近一次提交
	Message string `json:"message"`
}

// VCSRebasePlan 是交互式变基计划（IDEA 的 Interactively Rebase from Here）。
type VCSRebasePlan struct {
	Base  string          `json:"base"`  // 从这个提交之后开始（不含它）
	Steps []VCSRebaseStep `json:"steps"` // 从旧到新
}

type VCSRebaseStep struct {
	Hash    string `json:"hash"`
	Action  string `json:"action"`            // pick | reword | squash | fixup | drop
	Message string `json:"message,omitempty"` // reword/squash 的新信息
}

type VCSCheckoutFileArgs struct {
	Path string `json:"path"`
	Ref  string `json:"ref"`
}

type VCSCompareArgs struct {
	Base string `json:"base"`
	Head string `json:"head"` // 为空表示当前分支
}

type VCSCompare struct {
	Ahead  []VCSCommit     `json:"ahead"`  // 在 Head 不在 Base
	Behind []VCSCommit     `json:"behind"` // 在 Base 不在 Head
	Files  []VCSFileChange `json:"files"`  // merge-base(Base,Head) 到 Head 的文件变化
}

type VCSPushPreview struct {
	Remote    string      `json:"remote"`
	Branch    string      `json:"branch"`
	RemoteRef string      `json:"remoteRef"`
	NewBranch bool        `json:"newBranch,omitempty"`
	Commits   []VCSCommit `json:"commits"`
	Behind    int         `json:"behind"` // 远端领先的提交数（>0 时推送会被拒）
}

type VCSBlameArgs struct {
	Path string `json:"path"`
	Ref  string `json:"ref,omitempty"`
}

type VCSBlame struct {
	Lines []VCSBlameLine `json:"lines"`
}

type VCSBlameLine struct {
	Line    int    `json:"line"`
	Hash    string `json:"hash"`
	Author  string `json:"author"`
	Date    string `json:"date"`
	Summary string `json:"summary,omitempty"`
}

type VCSSetUpstreamArgs struct {
	Branch   string `json:"branch,omitempty"`
	Upstream string `json:"upstream"` // 如 origin/main；空表示取消跟踪
}

type VCSSubmodules struct {
	Submodules []VCSSubmodule `json:"submodules"`
}

type VCSSubmodule struct {
	Path   string `json:"path"`
	URL    string `json:"url,omitempty"`
	Head   string `json:"head,omitempty"`
	Status string `json:"status"` // ok | uninitialized | modified | conflict
}

type VCSSubmoduleUpdateArgs struct {
	Paths     []string `json:"paths,omitempty"`
	Init      bool     `json:"init,omitempty"`
	Recursive bool     `json:"recursive,omitempty"`
	Remote    bool     `json:"remote,omitempty"`
}

type VCSWorktrees struct {
	Worktrees []VCSWorktree `json:"worktrees"`
}

type VCSWorktree struct {
	Path   string `json:"path"`
	Branch string `json:"branch,omitempty"`
	Head   string `json:"head,omitempty"`
	Main   bool   `json:"main,omitempty"`
	Locked bool   `json:"locked,omitempty"`
}

type VCSWorktreeAddArgs struct {
	Path      string `json:"path"`
	Branch    string `json:"branch,omitempty"`
	NewBranch bool   `json:"newBranch,omitempty"`
	Start     string `json:"start,omitempty"`
}

type VCSWorktreeRemoveArgs struct {
	Path  string `json:"path"`
	Force bool   `json:"force,omitempty"`
}

type VCSConfigGetArgs struct {
	Keys []string `json:"keys"` // 如 user.name、user.email、pull.rebase
}

type VCSConfig struct {
	Values map[string]string `json:"values"`           // 生效值
	Scopes map[string]string `json:"scopes,omitempty"` // 来源：local | global | system
}

type VCSConfigSetArgs struct {
	Key    string `json:"key"`
	Value  string `json:"value"`
	Global bool   `json:"global,omitempty"`
	Unset  bool   `json:"unset,omitempty"`
}

type VCSIgnoreArgs struct {
	Patterns []string `json:"patterns"`
	File     string   `json:"file,omitempty"` // 默认仓库根 .gitignore；"exclude" 表示 .git/info/exclude（不提交）
}

type VCSReposArgs struct {
	MaxDepth int `json:"maxDepth,omitempty"`
}

type VCSRepoList struct {
	Repos []VCSRepoInfo `json:"repos"`
}

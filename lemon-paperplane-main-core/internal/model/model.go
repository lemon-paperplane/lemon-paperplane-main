// Package model 是内核对外（HTTP API / MCP / 界面）暴露的数据结构，也是持久化格式。
// 字段名即 JSON 契约，改动要同步 docs/api.md 与三端界面。
package model

import (
	"encoding/json"
	"time"

	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// ---------------------------------------------------------------------------
// 工作区与工程

type Workspace struct {
	ID         string    `json:"id"`
	Name       string    `json:"name"`
	ProjectIDs []string  `json:"projectIds"`
	CreatedAt  time.Time `json:"createdAt"`
}

// Project 是持久化的完整工程。
type Project struct {
	ID          string          `json:"id"`
	Name        string          `json:"name"`
	Path        string          `json:"path"`
	WorkspaceID string          `json:"workspaceId"`
	Modules     []*Module       `json:"modules"`
	Settings    ProjectSettings `json:"settings"`
	VCSRoots    []VCSRoot       `json:"vcsRoots,omitempty"`
	ImportedAt  time.Time       `json:"importedAt"`
	AnalyzedAt  time.Time       `json:"analyzedAt,omitempty"`
	// DefaultConfigID 是卡片上运行按钮默认运行的配置
	DefaultConfigID string `json:"defaultConfigId,omitempty"`
	// DefaultByUser 表示默认配置是用户指定的；否则每次分析后自动重选
	DefaultByUser bool `json:"defaultByUser,omitempty"`
	// Launch 是加到工作区「启动」面板的运行配置 id（按加入顺序）。工程换工作区时跟着走
	Launch []string `json:"launch,omitempty"`
	// LaunchAlias 是启动项的别名（运行配置 id → 别名），只在「启动」里显示，不改运行配置本身的名字
	LaunchAlias map[string]string `json:"launchAlias,omitempty"`
	// DetectVersion 是识别模块时所用规则的版本；内核的规则升级后，旧版本识别的工程会在后台自动重新识别
	DetectVersion int `json:"detectVersion,omitempty"`
}

type ProjectSettings struct {
	Env        map[string]string            `json:"env,omitempty"`        // 工程级环境变量
	Toolchains map[string]ToolchainOverride `json:"toolchains,omitempty"` // kind → 用户指定
	Notes      string                       `json:"notes,omitempty"`
}

// ToolchainOverride：ID 指定具体工具链；或 Constraint 指定版本约束（缺失时自动下载）。
type ToolchainOverride struct {
	ID         string `json:"id,omitempty"`
	Constraint string `json:"constraint,omitempty"`
}

type VCSRoot struct {
	Path    string `json:"path"`    // 绝对路径
	RelPath string `json:"relPath"` // 相对工程根
	VCS     string `json:"vcs"`     // git
	Plugin  string `json:"plugin"`
}

// Module 是工程中被识别出的一个模块。
type Module struct {
	ID       string                     `json:"id"`
	Name     string                     `json:"name"`
	RelDir   string                     `json:"relDir"`
	Facets   []sdk.Facet                `json:"facets"`
	Models   map[string]json.RawMessage `json:"models,omitempty"`
	Analysis map[string]*ModuleAnalysis `json:"analysis,omitempty"` // 按插件
	// Toolchains 是为该模块选定的工具链（按 kind）
	Toolchains   map[string]ToolchainSelection `json:"toolchains,omitempty"`
	Requirements []sdk.ToolchainRequirement    `json:"requirements,omitempty"`
	RunConfigs   []*RunConfig                  `json:"runConfigs"`
	Dependencies *sdk.DependencyState          `json:"dependencies,omitempty"`
	Synced       bool                          `json:"synced,omitempty"`
	Fingerprint  string                        `json:"fingerprint,omitempty"` // 关键构建文件的指纹，变化时自动重新分析
	NeedsSync    bool                          `json:"needsSync,omitempty"`
}

// ModuleAnalysis 是某个插件对模块的分析结果（去掉了 Model 与 RunConfigurations，它们单独存）。
type ModuleAnalysis struct {
	Info         []sdk.InfoItem          `json:"info,omitempty"`
	Problems     []sdk.Diagnosis         `json:"problems,omitempty"`
	OptionValues map[string][]sdk.Option `json:"optionValues,omitempty"`
	Dependencies *sdk.DependencyState    `json:"dependencies,omitempty"`
	At           time.Time               `json:"at"`
}

// MarshalJSON 把没有选项的项输出为 [] 而不是 null（插件可能给 nil 切片，界面按数组解析；
// 已经存盘的旧数据也在输出时纠正）。
func (a ModuleAnalysis) MarshalJSON() ([]byte, error) {
	type plain ModuleAnalysis
	p := plain(a)
	p.OptionValues = NonNilOptions(p.OptionValues)
	return json.Marshal(p)
}

// NonNilOptions 返回把 nil 列表换成空列表后的副本。
func NonNilOptions(m map[string][]sdk.Option) map[string][]sdk.Option {
	if len(m) == 0 {
		return m
	}
	out := make(map[string][]sdk.Option, len(m))
	for k, v := range m {
		if v == nil {
			v = []sdk.Option{}
		}
		out[k] = v
	}
	return out
}

type ToolchainSelection struct {
	Kind        string `json:"kind"`
	ToolchainID string `json:"toolchainId,omitempty"` // 空表示缺失
	Version     string `json:"version,omitempty"`
	Constraint  string `json:"constraint,omitempty"` // 合并后的约束
	Preferred   string `json:"preferred,omitempty"`
	Missing     bool   `json:"missing,omitempty"`
	Override    bool   `json:"override,omitempty"` // 来自工程设置
	Source      string `json:"source,omitempty"`   // 需求依据，如 ".nvmrc"
}

// RunConfig 是持久化的运行配置。
type RunConfig struct {
	sdk.RunConfiguration
	// Edited 表示用户改过，重新导入时不再被插件覆盖
	Edited bool `json:"edited,omitempty"`
	// Stale 表示插件这次没再生成它（文件被删等），界面可提示
	Stale bool `json:"stale,omitempty"`
}

// ---------------------------------------------------------------------------
// 卡片摘要（project.list 返回）

type ProjectSummary struct {
	ID                string            `json:"id"`
	Name              string            `json:"name"`
	Path              string            `json:"path"`
	WorkspaceID       string            `json:"workspaceId"`
	Facets            []FacetBadge      `json:"facets"`
	Info              []sdk.InfoItem    `json:"info,omitempty"`
	Status            string            `json:"status"` // idle | starting | running | failed | stopping | partial
	Runs              []RunBrief        `json:"runs,omitempty"`
	Configs           []ConfigBrief     `json:"configs"`
	DefaultConfigID   string            `json:"defaultConfigId,omitempty"`
	Launch            []string          `json:"launch,omitempty"`      // 「启动」面板里的运行配置 id（只含仍存在的）
	LaunchAlias       map[string]string `json:"launchAlias,omitempty"` // 启动项别名（运行配置 id → 别名）
	VCS               *VCSBrief         `json:"vcs,omitempty"`
	Problems          int               `json:"problems"`
	ProblemSeverity   string            `json:"problemSeverity,omitempty"`   // 最严重的：error | warning | info
	MissingToolchains []string          `json:"missingToolchains,omitempty"` // 如 ["jdk 17"]
	Dependencies      string            `json:"dependencies,omitempty"`      // ok | missing | outdated | unknown（各模块中最差的）
	Busy              string            `json:"busy,omitempty"`              // 正在进行的任务标题（导入、同步、安装…）
}

// FacetBadge 是卡片上的技术徽标（已本地化）。
type FacetBadge struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Icon     string `json:"icon,omitempty"` // 图标引用，见 docs/api.md「图标」
	Category string `json:"category,omitempty"`
	Plugin   string `json:"plugin"`
}

type ConfigBrief struct {
	ID         string `json:"id"`
	Name       string `json:"name"`
	Type       string `json:"type"`
	Icon       string `json:"icon,omitempty"`
	ModuleID   string `json:"moduleId"`
	ModuleName string `json:"moduleName,omitempty"`
}

type RunBrief struct {
	ID        string   `json:"id"`
	ProjectID string   `json:"projectId,omitempty"`
	ConfigID  string   `json:"configId"`
	Name      string   `json:"name"`
	State     string   `json:"state"`
	URLs      []string `json:"urls,omitempty"`
	CPU       float64  `json:"cpu"`
	RSSMB     float64  `json:"rssMB"`
	PeakRSSMB float64  `json:"peakRSSMB"`
	Procs     int      `json:"procs"`
	T         int64    `json:"t,omitempty"` // 采样时间（Unix 毫秒），界面据此追加趋势点
}

// ResourceSample 是一次资源采样。
type ResourceSample struct {
	T     int64   `json:"t"` // Unix 毫秒
	CPU   float64 `json:"cpu"`
	RSSMB float64 `json:"rssMB"`
}

// RunStats 是 run.stats 的返回：最近 5 分钟的趋势与进程明细。
type RunStats struct {
	RunID     string           `json:"runId"`
	State     string           `json:"state"`
	Samples   []ResourceSample `json:"samples"`
	Processes []ProcessInfo    `json:"processes"`
	CPU       float64          `json:"cpu"`
	RSSMB     float64          `json:"rssMB"`
	PeakRSSMB float64          `json:"peakRSSMB"`
	Ports     []int            `json:"ports,omitempty"`
	UptimeSec int64            `json:"uptimeSec"`
	Interval  int              `json:"intervalMs"`
}

// ProcessInfo 是进程树里的一个进程。
type ProcessInfo struct {
	PID   int     `json:"pid"`
	PPID  int     `json:"ppid"`
	Name  string  `json:"name"`
	CPU   float64 `json:"cpu"`
	RSSMB float64 `json:"rssMB"`
}

type VCSBrief struct {
	Branch    string    `json:"branch"`
	Ahead     int       `json:"ahead"`
	Behind    int       `json:"behind"`
	Changes   int       `json:"changes"`
	Conflicts int       `json:"conflicts,omitempty"`
	Operation string    `json:"operation,omitempty"`
	Roots     int       `json:"roots"` // 工程内仓库数
	At        time.Time `json:"at"`
}

// ---------------------------------------------------------------------------
// 运行与任务

// Run 的状态。
const (
	RunPreparing = "preparing" // 插件 run/prepare 中（编译、解析类路径、装依赖、下工具链）
	RunStarting  = "starting"  // 主进程已启动，尚未就绪
	RunRunning   = "running"
	RunStopping  = "stopping"
	RunExited    = "exited" // 正常退出（含用户停止）
	RunFailed    = "failed" // 非零退出或准备失败
)

type Run struct {
	ID            string          `json:"id"`
	ProjectID     string          `json:"projectId"`
	ModuleID      string          `json:"moduleId"`
	ConfigID      string          `json:"configId"`
	Name          string          `json:"name"`
	Plugin        string          `json:"plugin"`
	State         string          `json:"state"`
	PID           int             `json:"pid,omitempty"`
	Command       string          `json:"command,omitempty"` // 展示用命令行
	Cwd           string          `json:"cwd,omitempty"`
	StartedAt     time.Time       `json:"startedAt"`
	ReadyAt       *time.Time      `json:"readyAt,omitempty"`
	EndedAt       *time.Time      `json:"endedAt,omitempty"`
	ExitCode      *int            `json:"exitCode,omitempty"`
	StoppedByUser bool            `json:"stoppedByUser,omitempty"`
	Error         string          `json:"error,omitempty"` // 准备阶段失败原因
	URLs          []string        `json:"urls,omitempty"`
	Ports         []int           `json:"ports,omitempty"`
	CPU           float64         `json:"cpu"`       // 进程树 CPU%
	RSSMB         float64         `json:"rssMB"`     // 进程树常驻内存
	PeakRSSMB     float64         `json:"peakRSSMB"` // 本次运行的内存峰值
	Procs         int             `json:"procs"`     // 进程树里的进程数
	Diagnoses     []sdk.Diagnosis `json:"diagnoses,omitempty"`
	LogFile       string          `json:"logFile,omitempty"`
	Lines         int64           `json:"lines"` // 已产生的日志行数
}

// Task 是耗时操作。
const (
	TaskRunning   = "running"
	TaskSucceeded = "succeeded"
	TaskFailed    = "failed"
	TaskCancelled = "cancelled"
)

type Task struct {
	ID        string          `json:"id"`
	Kind      string          `json:"kind"` // import | reimport | sync | install | toolchain | action | vcs | plugin
	Title     string          `json:"title"`
	ProjectID string          `json:"projectId,omitempty"`
	ModuleID  string          `json:"moduleId,omitempty"`
	State     string          `json:"state"`
	Message   string          `json:"message,omitempty"`
	Percent   float64         `json:"percent"` // <0 不确定
	Error     string          `json:"error,omitempty"`
	StartedAt time.Time       `json:"startedAt"`
	EndedAt   *time.Time      `json:"endedAt,omitempty"`
	Result    json.RawMessage `json:"result,omitempty"`
	Diagnoses []sdk.Diagnosis `json:"diagnoses,omitempty"`
}

// LogLine 是控制台的一行。Text 保留 ANSI 转义（界面负责着色）。
type LogLine struct {
	Seq    int64  `json:"seq"`
	Time   int64  `json:"t"` // Unix 毫秒
	Stream string `json:"s"` // stdout | stderr | system
	Text   string `json:"text"`
}

// ---------------------------------------------------------------------------
// 工具链与插件

type ToolchainKind struct {
	Kind        string `json:"kind"`
	Name        string `json:"name"`
	Icon        string `json:"icon,omitempty"`
	Installable bool   `json:"installable"`
	Plugin      string `json:"plugin"`
}

type PluginInfo struct {
	ID             string           `json:"id"`
	Name           string           `json:"name"`
	Version        string           `json:"version"`
	Vendor         string           `json:"vendor,omitempty"`
	Description    string           `json:"description,omitempty"`
	Icon           string           `json:"icon,omitempty"`
	Category       string           `json:"category,omitempty"`
	Homepage       string           `json:"homepage,omitempty"`
	Source         string           `json:"source"` // bundled | user | dev
	Dir            string           `json:"dir"`
	Enabled        bool             `json:"enabled"`
	Running        bool             `json:"running"`
	Error          string           `json:"error,omitempty"` // 加载/兼容性错误
	Dependencies   []sdk.Dependency `json:"dependencies,omitempty"`
	BundledVersion string           `json:"bundledVersion,omitempty"` // 用户版覆盖内置版时，内置版本号
	Contributes    ContribSummary   `json:"contributes"`
}

type ContribSummary struct {
	Facets         []string `json:"facets,omitempty"`
	Toolchains     []string `json:"toolchains,omitempty"`
	RunConfigTypes []string `json:"runConfigTypes,omitempty"`
	Diagnostics    int      `json:"diagnostics"`
	Views          []string `json:"views,omitempty"`
	Actions        []string `json:"actions,omitempty"`
	Tools          []string `json:"tools,omitempty"`
	VCS            string   `json:"vcs,omitempty"`
}

// StoreEntry 是插件商店索引的一项。
type StoreEntry struct {
	ID          string                   `json:"id"`
	Name        string                   `json:"name"`
	Version     string                   `json:"version"`
	Vendor      string                   `json:"vendor,omitempty"`
	Description string                   `json:"description,omitempty"`
	Icon        string                   `json:"icon,omitempty"` // URL
	Engines     sdk.Engines              `json:"engines"`
	Platforms   map[string]StorePlatform `json:"platforms"`
	// Versions 是在线商店给的全部可用版本（新到旧）；读取时挑第一个兼容本机的，替换上面的 version/engines/platforms
	Versions   []StoreVersion `json:"versions,omitempty"`
	Installed  string         `json:"installed,omitempty"` // 已安装版本
	Compatible bool           `json:"compatible"`
}

// StoreVersion 是商店里一个条目的一个版本。
type StoreVersion struct {
	Version   string                   `json:"version"`
	Engines   sdk.Engines              `json:"engines"`
	Platforms map[string]StorePlatform `json:"platforms"`
}

type StorePlatform struct {
	URL    string `json:"url"`
	SHA256 string `json:"sha256"`
	Size   int64  `json:"size,omitempty"`
}

// ---------------------------------------------------------------------------
// 界面贡献（ui.contributions 返回，已本地化）

type Contributions struct {
	Facets         []FacetBadge         `json:"facets"`
	ToolchainKinds []ToolchainKind      `json:"toolchainKinds"`
	RunConfigTypes []RunConfigTypeInfo  `json:"runConfigTypes"`
	Views          []ViewInfo           `json:"views"`
	Actions        []ActionInfo         `json:"actions"`
	Settings       []PluginSettingsInfo `json:"settings"`
	VCS            []VCSInfo            `json:"vcs"`
	Languages      []LanguageInfo       `json:"languages"`
	Keymaps        []KeymapInfo         `json:"keymaps"`
	EditorThemes   []EditorThemeInfo    `json:"editorThemes"`
	DefaultKeymap  string               `json:"defaultKeymap"` // 当前平台默认快捷键方案
}

// LanguageInfo、KeymapInfo、EditorThemeInfo 里的 module/file 已转成 plugin:<id>/<path> 引用。
type LanguageInfo struct {
	sdk.LanguageDef
	Plugin string `json:"plugin"`
}

type KeymapInfo struct {
	sdk.KeymapDef
	Plugin string `json:"plugin"`
}

type EditorThemeInfo struct {
	sdk.EditorThemeDef
	Plugin string `json:"plugin"`
}

type RunConfigTypeInfo struct {
	sdk.RunConfigTypeDef
	Plugin string `json:"plugin"`
}

type ViewInfo struct {
	sdk.ViewDef
	Plugin string `json:"plugin"`
}

type ActionInfo struct {
	sdk.ActionDef
	Plugin string `json:"plugin"`
}

type PluginSettingsInfo struct {
	Plugin string          `json:"plugin"`
	Title  string          `json:"title"`
	Fields []sdk.FormField `json:"fields"`
	Values map[string]any  `json:"values"`
}

type VCSInfo struct {
	sdk.VCSDef
	Plugin string `json:"plugin"`
}

// ---------------------------------------------------------------------------
// 设置

type Settings struct {
	Locale                  string                    `json:"locale"`       // system | zh-Hans | en
	Theme                   string                    `json:"theme"`        // system | light | dark
	MirrorRegion            string                    `json:"mirrorRegion"` // auto | china | global
	AutoInstallToolchains   bool                      `json:"autoInstallToolchains"`
	AutoInstallDependencies bool                      `json:"autoInstallDependencies"`
	AutoSyncOnImport        bool                      `json:"autoSyncOnImport"` // 导入后自动执行构建工具同步（如 Gradle）
	PluginIdleSeconds       int                       `json:"pluginIdleSeconds"`
	LogLinesPerRun          int                       `json:"logLinesPerRun"`
	LogRetentionRuns        int                       `json:"logRetentionRuns"`
	APIPort                 int                       `json:"apiPort"`
	StoreIndexURL           string                    `json:"storeIndexUrl,omitempty"`
	GitAutoFetchMinutes     int                       `json:"gitAutoFetchMinutes"`
	Terminal                string                    `json:"terminal,omitempty"` // macOS: Terminal | iTerm | Warp | Ghostty
	PluginSettings          map[string]map[string]any `json:"pluginSettings,omitempty"`
	DisabledPlugins         []string                  `json:"disabledPlugins,omitempty"`
	Editor                  EditorSettings            `json:"editor"`
}

// EditorSettings 是文件编辑器设置（docs/editor.md）。
type EditorSettings struct {
	Keymap                 string `json:"keymap"` // 空表示按平台默认（eclipse-macos / eclipse-windows）
	Theme                  string `json:"theme"`  // 空表示跟随 App 明暗
	FontSize               int    `json:"fontSize"`
	FontFamily             string `json:"fontFamily,omitempty"`
	TabSize                int    `json:"tabSize"`
	InsertSpaces           bool   `json:"insertSpaces"`
	WordWrap               bool   `json:"wordWrap"`
	AutoSave               string `json:"autoSave"` // off | onFocusChange | afterDelay
	TrimTrailingWhitespace bool   `json:"trimTrailingWhitespace"`
	InsertFinalNewline     bool   `json:"insertFinalNewline"`
	ShowWhitespace         bool   `json:"showWhitespace"`
}

// DefaultSettings 返回默认设置。
func DefaultSettings() Settings {
	return Settings{
		Locale:                  "system",
		Theme:                   "system",
		MirrorRegion:            "auto",
		AutoInstallToolchains:   true,
		AutoInstallDependencies: true,
		AutoSyncOnImport:        true,
		PluginIdleSeconds:       60,
		LogLinesPerRun:          20000,
		LogRetentionRuns:        20,
		APIPort:                 47213,
		GitAutoFetchMinutes:     0,
		Editor:                  EditorSettings{FontSize: 13, TabSize: 4, InsertSpaces: true, AutoSave: "off", InsertFinalNewline: true},
	}
}

// ---------------------------------------------------------------------------
// 事件（GET /api/v1/events 的 SSE data）

type Event struct {
	Type string `json:"type"`
	Data any    `json:"data,omitempty"`
}

// 事件类型。
const (
	EvWorkspaces = "workspaces.changed" // data: []Workspace
	EvProject    = "project.changed"    // data: ProjectSummary
	EvProjectDel = "project.removed"    // data: {id}
	EvRun        = "run.changed"        // data: Run
	EvTask       = "task.changed"       // data: Task
	EvToolchains = "toolchains.changed" // data: null
	EvPlugins    = "plugins.changed"    // data: null
	EvSettings   = "settings.changed"   // data: Settings
	EvNotify     = "notify"             // data: sdk.NotifyParams
	EvPrompt     = "prompt"             // data: Prompt
	EvPromptDone = "prompt.done"        // data: {id}
	EvVCS        = "vcs.changed"        // data: {projectId, root}
	EvStats      = "stats"              // data: []RunBrief（每 2 秒，只含运行中的）
	EvTerminals  = "terminals.changed"  // data: []Terminal（全部终端）
)

// Terminal 是一个交互终端（伪终端里跑的登录 shell）。只在 daemon 内存里，daemon 退出即关闭。
// 输出流：GET /api/v1/terminals/<id>/stream?since=<字节偏移>（SSE，data: {"o":偏移,"d":base64}）。
type Terminal struct {
	ID          string    `json:"id"`
	WorkspaceID string    `json:"workspaceId,omitempty"`
	ProjectID   string    `json:"projectId,omitempty"` // 起始目录所在的工程
	Name        string    `json:"name"`
	Cwd         string    `json:"cwd"`   // 起始目录
	Shell       string    `json:"shell"` // shell 可执行文件
	PID         int       `json:"pid,omitempty"`
	Cols        int       `json:"cols"`
	Rows        int       `json:"rows"`
	StartedAt   time.Time `json:"startedAt"`
	Exited      bool      `json:"exited,omitempty"`
	ExitCode    int       `json:"exitCode,omitempty"`
	Output      int64     `json:"output"` // 累计输出字节数（流的末尾偏移）
}

// Prompt 是需要界面回答的对话框（host/prompt、git askpass）。
type Prompt struct {
	ID string `json:"id"`
	sdk.PromptParams
	Plugin string `json:"plugin,omitempty"`
}

// SystemInfo 是 system.info 的返回。
type SystemInfo struct {
	Version        string    `json:"version"`
	OS             string    `json:"os"`
	Arch           string    `json:"arch"`
	PID            int       `json:"pid"`
	StartedAt      time.Time `json:"startedAt"`
	DataDir        string    `json:"dataDir"`
	APIBase        string    `json:"apiBase"`
	MCPURL         string    `json:"mcpUrl"`
	Executable     string    `json:"executable"`
	MemoryMB       float64   `json:"memoryMB"`    // daemon 自身常驻内存
	PluginProcs    int       `json:"pluginProcs"` // 当前活着的插件进程数
	PluginMemoryMB float64   `json:"pluginMemoryMB"`
	Plugins        int       `json:"plugins"`                // 可用插件数；为 0 说明安装不完整，什么工程都识别不了
	PluginErrors   []string  `json:"pluginErrors,omitempty"` // 加载失败的插件：「id: 原因」
	PluginDirs     []string  `json:"pluginDirs,omitempty"`   // 内置插件目录（排查用）
	Locale         string    `json:"locale"`                 // 生效语言
	MirrorRegion   string    `json:"mirrorRegion"`           // 生效镜像区域
}

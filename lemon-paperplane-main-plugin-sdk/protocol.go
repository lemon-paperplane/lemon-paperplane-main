// Package sdk 是 Lemon Paperplane 插件协议的类型定义与 Go 实现。
//
// 插件是一个独立可执行文件，通过 stdin/stdout 与内核交换 JSON-RPC 2.0 消息，
// 帧格式与 LSP 相同（Content-Length 头 + 空行 + JSON 正文）。stderr 会被内核收进 daemon 日志。
// 协议说明见 docs/plugin-protocol.md；本文件是协议的唯一真源。
package sdk

import "encoding/json"

// ProtocolVersion 是本 SDK 实现的协议版本，写进 plugin.json 的 "protocol" 字段。
const ProtocolVersion = 1

// 内核 → 插件 的方法。
const (
	MethodInitialize         = "initialize"
	MethodShutdown           = "shutdown"
	MethodCancelRequest      = "$/cancelRequest"
	MethodDetect             = "project/detect"
	MethodAnalyze            = "module/analyze"
	MethodSync               = "module/sync"
	MethodInstallDeps        = "dependencies/install"
	MethodPrepareRun         = "run/prepare"
	MethodOptions            = "options/list"
	MethodDiagnose           = "run/diagnose"
	MethodApplyFix           = "fix/apply"
	MethodInvokeAction       = "action/invoke"
	MethodDiscoverToolchains = "toolchain/discover"
	MethodAvailableToolchain = "toolchain/available"
	MethodInstallToolchain   = "toolchain/install"
	MethodServiceCall        = "service/call"
	MethodVCS                = "vcs/call"
	MethodViewRender         = "view/render"
	MethodViewEvent          = "view/event"
	MethodToolCall           = "tool/call"
)

// 插件 → 内核 的方法。
const (
	HostExec       = "host/exec"
	HostConsole    = "host/console"
	HostProgress   = "host/progress"
	HostLog        = "host/log"
	HostCall       = "host/call"
	HostToolchains = "host/toolchains"
	HostDownload   = "host/download"
	HostNotify     = "host/notify"
	HostPrompt     = "host/prompt"
)

// ---------------------------------------------------------------------------
// initialize

type InitializeParams struct {
	ProtocolVersion int            `json:"protocolVersion"`
	HostVersion     string         `json:"hostVersion"`
	Locale          string         `json:"locale"` // "zh-Hans" | "en" | ...
	OS              string         `json:"os"`     // runtime.GOOS 取值
	Arch            string         `json:"arch"`   // runtime.GOARCH 取值
	PluginDir       string         `json:"pluginDir"`
	DataDir         string         `json:"dataDir"`  // 插件私有数据目录，已创建
	CacheDir        string         `json:"cacheDir"` // 插件私有缓存目录，已创建
	HostDataDir     string         `json:"hostDataDir"`
	HostExecutable  string         `json:"hostExecutable,omitempty"` // paperplane 内核可执行文件
	AskpassPath     string         `json:"askpassPath,omitempty"`    // 可直接设给 GIT_ASKPASS / SSH_ASKPASS 的程序，会在界面弹框向用户要密码
	APIBase         string         `json:"apiBase,omitempty"`        // 内核 HTTP API 地址，如 http://127.0.0.1:47213
	Mirrors         Mirrors        `json:"mirrors"`
	Settings        map[string]any `json:"settings,omitempty"` // 插件设置项当前值
}

type InitializeResult struct {
	ProtocolVersion int    `json:"protocolVersion"`
	Name            string `json:"name,omitempty"`
	Version         string `json:"version,omitempty"`
}

// Mirrors 是当前生效的镜像地址。空字符串表示用官方源。
type Mirrors struct {
	Region             string `json:"region"` // "china" | "global"
	NpmRegistry        string `json:"npmRegistry,omitempty"`
	NodeDist           string `json:"nodeDist,omitempty"`
	MavenCentral       string `json:"mavenCentral,omitempty"`
	GoogleMaven        string `json:"googleMaven,omitempty"` // Android/AGP 用的 Google Maven 仓库
	GradlePluginPortal string `json:"gradlePluginPortal,omitempty"`
	GradleDist         string `json:"gradleDist,omitempty"`
	PypiIndex          string `json:"pypiIndex,omitempty"`
	PythonDist         string `json:"pythonDist,omitempty"`
	GoProxy            string `json:"goProxy,omitempty"`
	GoSumDB            string `json:"goSumDB,omitempty"`
	GoDist             string `json:"goDist,omitempty"`
	JdkAdoptium        string `json:"jdkAdoptium,omitempty"`
}

// ---------------------------------------------------------------------------
// 公共对象

// ProjectInfo 是工程的只读快照。
type ProjectInfo struct {
	ID   string `json:"id"`
	Name string `json:"name"`
	Root string `json:"root"` // 绝对路径
}

// ModuleInfo 是模块的只读快照。Models 里是各插件 analyze/sync 产出的模型（按插件 id）。
type ModuleInfo struct {
	ID     string                     `json:"id"`
	Name   string                     `json:"name"`
	Dir    string                     `json:"dir"`    // 绝对路径
	RelDir string                     `json:"relDir"` // 相对工程根，根模块为 ""
	Facets []Facet                    `json:"facets"`
	Models map[string]json.RawMessage `json:"models,omitempty"`
}

// HasFacet 判断模块是否带某个 facet。
func (m *ModuleInfo) HasFacet(id string) bool {
	for _, f := range m.Facets {
		if f.ID == id {
			return true
		}
	}
	return false
}

// Facet 返回指定 facet，没有则 nil。
func (m *ModuleInfo) Facet(id string) *Facet {
	for i := range m.Facets {
		if m.Facets[i].ID == id {
			return &m.Facets[i]
		}
	}
	return nil
}

// Model 把某个插件的模型解码到 v。没有该模型返回 false。
func (m *ModuleInfo) Model(pluginID string, v any) bool {
	raw, ok := m.Models[pluginID]
	if !ok || len(raw) == 0 {
		return false
	}
	return json.Unmarshal(raw, v) == nil
}

// Facet 是模块的一个技术特征，如 java、gradle、spring-boot。
type Facet struct {
	ID     string         `json:"id"`
	Plugin string         `json:"plugin,omitempty"` // 内核填写
	Data   map[string]any `json:"data,omitempty"`
}

// 框架插件通过 facet Data 影响 nodejs 插件生成的 npm 脚本运行配置（nodejs 是唯一生成 npm 运行配置的插件）。
const (
	FacetDataPreferScripts = "preferScripts" // []string：默认脚本候选，按优先级
	FacetDataScriptTitles  = "scriptTitles"  // map[脚本名]显示名
	FacetDataHideScripts   = "hideScripts"   // []string：不生成运行配置的脚本
)

// Toolchain 是一个可用的运行时/构建工具安装（IDEA 里叫 SDK）。
type Toolchain struct {
	ID          string            `json:"id,omitempty"` // 内核分配，形如 "jdk:<path>"
	Kind        string            `json:"kind"`         // jdk | node | python | go | gradle | maven | ...
	Version     string            `json:"version"`      // 规范化后的版本，如 "17.0.16"、"22.20.0"
	Vendor      string            `json:"vendor,omitempty"`
	Arch        string            `json:"arch,omitempty"` // arm64 | amd64
	Path        string            `json:"path"`           // 安装根目录（JAVA_HOME / node 前缀 / GOROOT / python 前缀）
	Executable  string            `json:"executable,omitempty"`
	Env         map[string]string `json:"env,omitempty"`         // 例如 JAVA_HOME
	PathPrepend []string          `json:"pathPrepend,omitempty"` // 需要放到 PATH 最前面的目录
	Source      string            `json:"source,omitempty"`      // system | nvm | fnm | volta | homebrew | sdkman | ide | uv | conda | managed ...
	Managed     bool              `json:"managed,omitempty"`     // 由 Paperplane 下载管理
	Label       string            `json:"label,omitempty"`       // 界面显示名，如 "Temurin 17.0.16"
}

// ToolchainRequirement 是模块对某类工具链的版本要求。
// Constraint 使用 npm semver 范围语法：">=17 <24"、"16"、"^20.19 || >=22.12"、"3.12.x"。
type ToolchainRequirement struct {
	Kind       string `json:"kind"`
	Constraint string `json:"constraint,omitempty"`
	Preferred  string `json:"preferred,omitempty"` // 优先选择的版本，如 "17"
	Source     string `json:"source,omitempty"`    // 依据，如 ".nvmrc"、"build.gradle sourceCompatibility"
	Purpose    string `json:"purpose,omitempty"`   // 同一 kind 的不同用途，如 "gradle-jvm"
	Optional   bool   `json:"optional,omitempty"`
}

// RunConfiguration 是一条运行配置。
type RunConfiguration struct {
	ID       string            `json:"id,omitempty"` // 插件生成时给稳定 id（同输入同 id），内核据此在重新导入时合并
	Name     string            `json:"name"`
	Type     string            `json:"type"`             // 运行配置类型 id，在 manifest contributes.runConfigurationTypes 里声明
	Plugin   string            `json:"plugin,omitempty"` // 内核填写
	ModuleID string            `json:"moduleId,omitempty"`
	Options  map[string]any    `json:"options,omitempty"`
	Env      map[string]string `json:"env,omitempty"`
	Default  bool              `json:"default,omitempty"`  // 插件建议的默认运行项
	Source   string            `json:"source,omitempty"`   // auto | user | idea | vscode
	Priority int               `json:"priority,omitempty"` // 越大越靠前
}

// DependencyState 是模块依赖的安装状态。
type DependencyState struct {
	State   string `json:"state"`             // ok | missing | outdated | unknown | none
	Manager string `json:"manager,omitempty"` // npm | yarn | pnpm | bun | uv | pip | poetry | gradle | maven | go ...
	Message string `json:"message,omitempty"`
}

// InfoItem 是界面上显示的一条关键信息，如 {"label":"Gradle","value":"8.12"}。
type InfoItem struct {
	Label string `json:"label"`
	Value string `json:"value"`
	Icon  string `json:"icon,omitempty"`
}

// Option 是下拉框的一个选项。
type Option struct {
	Value string `json:"value"`
	Label string `json:"label,omitempty"`
}

// Diagnosis 是一次诊断结论。
type Diagnosis struct {
	ID       string `json:"id,omitempty"` // 内核分配
	RuleID   string `json:"ruleId,omitempty"`
	Plugin   string `json:"plugin,omitempty"`
	Severity string `json:"severity"` // error | warning | info
	Title    string `json:"title"`
	Message  string `json:"message,omitempty"`
	Detail   string `json:"detail,omitempty"` // 多行原始信息，如 Spring Boot 的 Description/Action
	Fixes    []Fix  `json:"fixes,omitempty"`
	Line     string `json:"line,omitempty"` // 命中的日志行
}

// Fix 是一个修复动作。Kind 取值见 docs/architecture.md「诊断与修复」。
type Fix struct {
	ID          string            `json:"id"`
	Title       string            `json:"title"`
	Kind        string            `json:"kind"`
	Params      map[string]string `json:"params,omitempty"`
	Recommended bool              `json:"recommended,omitempty"`
	Confirm     string            `json:"confirm,omitempty"` // 非空则执行前要用户确认，内容为确认文案
	Plugin      string            `json:"plugin,omitempty"`  // 内核填写
}

// 通用修复动作 kind。
const (
	FixSetToolchain        = "setToolchain"
	FixSetEnv              = "setEnv"
	FixAppendEnv           = "appendEnv"
	FixUnsetEnv            = "unsetEnv"
	FixSetRunOption        = "setRunOption"
	FixInstallDependencies = "installDependencies"
	FixKillPort            = "killPort"
	FixReimport            = "reimport"
	FixRestart             = "restart"
	FixOpenURL             = "openUrl"
	FixCustom              = "custom"
)

// ---------------------------------------------------------------------------
// project/detect

type DetectParams struct {
	Root   string   `json:"root"`   // 工程根，绝对路径
	Dir    string   `json:"dir"`    // 候选目录，绝对路径
	RelDir string   `json:"relDir"` // 候选目录相对工程根
	Files  []string `json:"files"`  // 候选目录下的文件名与子目录名（子目录名以 "/" 结尾）
	Facets []Facet  `json:"facets"` // 本目录已被前序插件识别出的 facet
}

type DetectResult struct {
	Facets []Facet `json:"facets,omitempty"`
	// Claims 是相对 Dir 的子目录，认领后内核不再把它们识别为独立模块。"**" 表示认领全部子目录。
	Claims []string `json:"claims,omitempty"`
	Name   string   `json:"name,omitempty"` // 建议的模块名
}

// ---------------------------------------------------------------------------
// module/analyze 与 module/sync

type AnalyzeParams struct {
	Project    ProjectInfo          `json:"project"`
	Module     ModuleInfo           `json:"module"`
	Toolchains map[string]Toolchain `json:"toolchains,omitempty"` // 当前为模块选定的工具链，按 kind；analyze 阶段可能为空
	IdeConfigs []IdeRunConfig       `json:"ideConfigs,omitempty"` // 从 IDEA/VS Code 读到的、本插件声明能处理的运行配置
	Env        map[string]string    `json:"env,omitempty"`        // 工程级环境变量（用户或修复设置的），可据此判断某个问题是否已被修复
	Settings   map[string]any       `json:"settings,omitempty"`
	TaskID     string               `json:"taskId,omitempty"` // sync 时有值，可作为 host/exec 的 Console
}

type AnalyzeResult struct {
	Model             json.RawMessage        `json:"model,omitempty"` // 插件自己的模型，内核原样保存并传给依赖它的插件
	Requirements      []ToolchainRequirement `json:"requirements,omitempty"`
	RunConfigurations []RunConfiguration     `json:"runConfigurations,omitempty"`
	Dependencies      *DependencyState       `json:"dependencies,omitempty"`
	Problems          []Diagnosis            `json:"problems,omitempty"`
	Info              []InfoItem             `json:"info,omitempty"`
	OptionValues      map[string][]Option    `json:"optionValues,omitempty"` // 动态下拉选项，key 对应 FormField.OptionsFrom
	NeedsSync         bool                   `json:"needsSync,omitempty"`    // 需要再做一次 module/sync 才能运行
}

// IdeRunConfig 是从 IDEA（.idea/workspace.xml、.run/*.run.xml）或 VS Code（.vscode/launch.json）读到的运行配置。
type IdeRunConfig struct {
	Source  string            `json:"source"` // idea | vscode
	Type    string            `json:"type"`   // IDEA 的 configuration type，或 VS Code 的 type
	Name    string            `json:"name"`
	Options map[string]string `json:"options,omitempty"` // IDEA 的 <option name value>；VS Code 的扁平化字段
	Env     map[string]string `json:"env,omitempty"`
	Module  string            `json:"module,omitempty"`
}

// ---------------------------------------------------------------------------
// dependencies/install

type InstallDepsParams struct {
	Project    ProjectInfo          `json:"project"`
	Module     ModuleInfo           `json:"module"`
	Toolchains map[string]Toolchain `json:"toolchains,omitempty"`
	Env        map[string]string    `json:"env,omitempty"` // 已合并镜像与工程环境变量的增量
	Settings   map[string]any       `json:"settings,omitempty"`
	TaskID     string               `json:"taskId"`          // 作为 host/exec 的 Console
	Clean      bool                 `json:"clean,omitempty"` // 先清理再装
}

type InstallDepsResult struct {
	Dependencies DependencyState `json:"dependencies"`
}

// ---------------------------------------------------------------------------
// run/prepare

type PrepareRunParams struct {
	RunID      string               `json:"runId"` // 作为 host/exec 的 Console，前置步骤的输出会进入这次运行的控制台
	Project    ProjectInfo          `json:"project"`
	Module     ModuleInfo           `json:"module"`
	Config     RunConfiguration     `json:"config"`
	Toolchains map[string]Toolchain `json:"toolchains,omitempty"`
	Env        map[string]string    `json:"env,omitempty"` // 镜像 + 工程 + 运行配置环境变量的合并增量（不含系统环境）
	Settings   map[string]any       `json:"settings,omitempty"`
}

// LaunchSpec 描述要由内核启动并托管的主进程。
type LaunchSpec struct {
	Command       string            `json:"command"` // 可执行文件绝对路径，或在合并后 PATH 中可找到的名字
	Args          []string          `json:"args,omitempty"`
	Cwd           string            `json:"cwd"`
	Env           map[string]string `json:"env,omitempty"` // 在合并环境之上再覆盖
	PathPrepend   []string          `json:"pathPrepend,omitempty"`
	URLPatterns   []URLPattern      `json:"urlPatterns,omitempty"`   // 本次运行额外的 URL 识别规则
	ReadyPatterns []string          `json:"readyPatterns,omitempty"` // 命中即视为就绪的正则
	Ports         []int             `json:"ports,omitempty"`         // 预期监听端口，用于启动前冲突检测
	StopSignal    string            `json:"stopSignal,omitempty"`    // SIGINT | SIGTERM（默认）| SIGKILL
	StopTimeoutMs int               `json:"stopTimeoutMs,omitempty"`
	Display       string            `json:"display,omitempty"` // 控制台第一行显示的命令行（可选，默认由内核拼）
}

// ---------------------------------------------------------------------------
// run/diagnose 与 fix/apply

type DiagnoseParams struct {
	RunID    string            `json:"runId,omitempty"`
	Stage    string            `json:"stage"` // run | install | sync | import
	Project  ProjectInfo       `json:"project"`
	Module   ModuleInfo        `json:"module"`
	Config   *RunConfiguration `json:"config,omitempty"`
	ExitCode *int              `json:"exitCode,omitempty"`
	Env      map[string]string `json:"env,omitempty"`     // 工程 + 运行配置的环境变量（与 PrepareRun 的 Env 同源，不含系统环境）
	LogTail  []string          `json:"logTail"`           // 最近若干行（已去 ANSI）
	Matched  []Diagnosis       `json:"matched,omitempty"` // 声明式规则已命中的
}

type DiagnoseResult struct {
	Diagnoses []Diagnosis `json:"diagnoses,omitempty"`
}

type ApplyFixParams struct {
	Fix        Fix                  `json:"fix"`
	Diagnosis  Diagnosis            `json:"diagnosis"`
	Project    ProjectInfo          `json:"project"`
	Module     ModuleInfo           `json:"module"`
	Config     *RunConfiguration    `json:"config,omitempty"`
	Toolchains map[string]Toolchain `json:"toolchains,omitempty"`
	Env        map[string]string    `json:"env,omitempty"`
	TaskID     string               `json:"taskId,omitempty"` // 修复里执行较长命令（装包、迁移）时作为 host/exec 的 Console，输出实时进任务控制台
}

type ApplyFixResult struct {
	Changes *ProjectChanges `json:"changes,omitempty"`
	Rerun   bool            `json:"rerun,omitempty"`
	Message string          `json:"message,omitempty"`
}

// ProjectChanges 是插件要求内核对工程设置做的变更（只改 Paperplane 自己保存的设置，不改用户文件）。
type ProjectChanges struct {
	Toolchains map[string]string  `json:"toolchains,omitempty"` // kind → 版本约束
	Env        map[string]*string `json:"env,omitempty"`        // nil 值表示删除
	RunOptions map[string]any     `json:"runOptions,omitempty"`
	Reimport   bool               `json:"reimport,omitempty"`
}

// ---------------------------------------------------------------------------
// action/invoke

type InvokeActionParams struct {
	Action     string               `json:"action"`
	Project    ProjectInfo          `json:"project"`
	Module     ModuleInfo           `json:"module"`
	Toolchains map[string]Toolchain `json:"toolchains,omitempty"`
	Env        map[string]string    `json:"env,omitempty"`
	TaskID     string               `json:"taskId"`
	Args       map[string]any       `json:"args,omitempty"`
	Run        *RunInfo             `json:"run,omitempty"` // 挂在 run.toolbar 的动作：触发时所在的运行
}

type InvokeActionResult struct {
	Message  string `json:"message,omitempty"`
	OpenURL  string `json:"openUrl,omitempty"`
	Reimport bool   `json:"reimport,omitempty"` // 动作结束后重新识别与分析
	Sync     bool   `json:"sync,omitempty"`     // 动作结束后执行 module/sync（如「同步 Gradle 工程」）
}

// ---------------------------------------------------------------------------
// toolchain/*

type DiscoverToolchainsParams struct {
	Kind  string   `json:"kind"`
	Paths []string `json:"paths,omitempty"` // 非空时只检查这些路径（例如刚下载解压的目录、用户手动添加的目录）
}

type DiscoverToolchainsResult struct {
	Toolchains []Toolchain `json:"toolchains"`
}

type AvailableToolchainsParams struct {
	Kind string `json:"kind"`
}

type AvailableToolchainsResult struct {
	Versions []AvailableVersion `json:"versions"`
}

type AvailableVersion struct {
	Version     string `json:"version"` // 可直接传给 toolchain/install
	Label       string `json:"label,omitempty"`
	Vendor      string `json:"vendor,omitempty"`
	LTS         bool   `json:"lts,omitempty"`
	Recommended bool   `json:"recommended,omitempty"`
}

type InstallToolchainParams struct {
	Kind    string `json:"kind"`
	Version string `json:"version"` // 具体版本，或约束（插件自行解析为具体版本）
	Vendor  string `json:"vendor,omitempty"`
	Dest    string `json:"dest"`   // 内核分配的安装目录（不存在）
	TaskID  string `json:"taskId"` // 作为 host/exec 的 Console / host/download 的进度
}

// InstallToolchainResult 二选一：返回下载计划由内核执行；或插件自己装好后直接返回 Toolchain。
type InstallToolchainResult struct {
	Plan      *InstallPlan `json:"plan,omitempty"`
	Toolchain *Toolchain   `json:"toolchain,omitempty"`
}

// InstallPlan：内核下载 Archive 到缓存、校验、解压到 Dest，再调用 toolchain/discover{paths:[Dest]} 得到 Toolchain。
type InstallPlan struct {
	Version         string   `json:"version"`
	URLs            []string `json:"urls"` // 依次尝试，镜像在前
	SHA256          string   `json:"sha256,omitempty"`
	Archive         string   `json:"archive"`                   // tar.gz | tar.xz | zip
	StripComponents int      `json:"stripComponents,omitempty"` // 解压时去掉的前导目录层数
	FileName        string   `json:"fileName,omitempty"`
}

// ---------------------------------------------------------------------------
// service/call（插件之间互调，经内核转发）

type ServiceCallParams struct {
	Method string          `json:"method"`
	Params json.RawMessage `json:"params,omitempty"`
	Caller string          `json:"caller,omitempty"` // 调用方插件 id，内核填写
}

// ---------------------------------------------------------------------------
// vcs/call（VCS 提供者，如 git）

type VCSCallParams struct {
	Op     string          `json:"op"`   // 见 vcs.go 里的 VCSOp* 常量
	Root   string          `json:"root"` // 仓库根目录
	Args   json.RawMessage `json:"args,omitempty"`
	TaskID string          `json:"taskId,omitempty"` // 推送/拉取等长操作的输出去向
}

// ---------------------------------------------------------------------------
// host/*（插件 → 内核）

type ExecParams struct {
	Command     string            `json:"command"`
	Args        []string          `json:"args,omitempty"`
	Cwd         string            `json:"cwd,omitempty"`
	Env         map[string]string `json:"env,omitempty"` // 在 daemon 进程环境之上覆盖
	PathPrepend []string          `json:"pathPrepend,omitempty"`
	Console     string            `json:"console,omitempty"` // runId 或 taskId；非空则输出实时进入该控制台
	Capture     bool              `json:"capture,omitempty"` // 返回 stdout/stderr（各最多 4 MB）
	Stdin       string            `json:"stdin,omitempty"`
	TimeoutMs   int               `json:"timeoutMs,omitempty"`
	Title       string            `json:"title,omitempty"`   // 控制台里显示的步骤名
	Display     string            `json:"display,omitempty"` // 控制台里「$ 」后显示的命令行（可选，默认由内核拼；经插件运行器代跑时用它显示真实命令）
}

type ExecResult struct {
	ExitCode   int    `json:"exitCode"`
	Stdout     string `json:"stdout,omitempty"`
	Stderr     string `json:"stderr,omitempty"`
	Cancelled  bool   `json:"cancelled,omitempty"`
	TimedOut   bool   `json:"timedOut,omitempty"`
	DurationMs int64  `json:"durationMs"`
}

type ConsoleParams struct {
	Console string `json:"console"`
	Text    string `json:"text"`
	Stream  string `json:"stream,omitempty"` // system（默认）| stdout | stderr
}

type ProgressParams struct {
	Token   string  `json:"token"` // taskId
	Message string  `json:"message,omitempty"`
	Percent float64 `json:"percent,omitempty"` // 0–100，<0 表示不确定
}

type LogParams struct {
	Level   string `json:"level"` // debug | info | warn | error
	Message string `json:"message"`
}

type HostCallParams struct {
	Plugin string          `json:"plugin"`
	Method string          `json:"method"`
	Params json.RawMessage `json:"params,omitempty"`
}

type HostToolchainsParams struct {
	Kind string `json:"kind"`
}

type HostToolchainsResult struct {
	Toolchains []Toolchain `json:"toolchains"`
}

type DownloadParams struct {
	URLs            []string `json:"urls"`
	SHA256          string   `json:"sha256,omitempty"`
	Dest            string   `json:"dest"`              // 文件路径；Extract 非空时为解压目标目录
	Extract         string   `json:"extract,omitempty"` // "" | tar.gz | tar.xz | zip
	StripComponents int      `json:"stripComponents,omitempty"`
	TaskID          string   `json:"taskId,omitempty"`
	Title           string   `json:"title,omitempty"`
}

type DownloadResult struct {
	Path string `json:"path"`
	URL  string `json:"url"` // 实际成功的地址
}

type NotifyParams struct {
	Level   string `json:"level"` // info | warning | error
	Title   string `json:"title"`
	Message string `json:"message,omitempty"`
}

type PromptParams struct {
	Title   string      `json:"title"`
	Message string      `json:"message,omitempty"`
	Fields  []FormField `json:"fields,omitempty"` // 为空时是确认框
	OKText  string      `json:"okText,omitempty"`
	Danger  bool        `json:"danger,omitempty"`
}

type PromptResult struct {
	Cancelled bool           `json:"cancelled"`
	Values    map[string]any `json:"values,omitempty"`
}

// ---------------------------------------------------------------------------
// view/render 与 view/event（插件扩展界面）

type ViewRenderParams struct {
	View    string         `json:"view"` // manifest contributes.views[].id
	Project *ProjectInfo   `json:"project,omitempty"`
	Module  *ModuleInfo    `json:"module,omitempty"`
	RunID   string         `json:"runId,omitempty"` // location 为 run.panel 时
	Run     *RunInfo       `json:"run,omitempty"`   // 这次运行的实际信息（run.panel）
	State   map[string]any `json:"state,omitempty"` // 上次 render 返回的 State，原样带回
}

// RunInfo 是一次运行的快照（给 run.panel 视图与 run.toolbar 动作用）。
type RunInfo struct {
	ID       string            `json:"id"`
	State    string            `json:"state"` // preparing | starting | running | stopping | exited | failed
	ConfigID string            `json:"configId"`
	Config   *RunConfiguration `json:"config,omitempty"`
	URLs     []string          `json:"urls,omitempty"`
	Ports    []int             `json:"ports,omitempty"`
	PID      int               `json:"pid,omitempty"`
}

type ViewRenderResult struct {
	Root      ViewNode       `json:"root"`
	State     map[string]any `json:"state,omitempty"`
	RefreshMs int            `json:"refreshMs,omitempty"` // >0 时界面按此间隔自动重新 render（最小 1000）
}

type ViewEventParams struct {
	View    string         `json:"view"`
	Event   string         `json:"event"`            // 触发事件的节点 Action
	Values  map[string]any `json:"values,omitempty"` // 表单值 / 被点击行的数据
	Project *ProjectInfo   `json:"project,omitempty"`
	Module  *ModuleInfo    `json:"module,omitempty"`
	RunID   string         `json:"runId,omitempty"`
	Run     *RunInfo       `json:"run,omitempty"`
	State   map[string]any `json:"state,omitempty"`
	TaskID  string         `json:"taskId,omitempty"`
}

type ViewEventResult struct {
	Refresh  bool           `json:"refresh,omitempty"` // 重新 render
	State    map[string]any `json:"state,omitempty"`   // 新状态（Refresh 时带回 render）
	Message  string         `json:"message,omitempty"` // 轻提示
	OpenURL  string         `json:"openUrl,omitempty"`
	Reimport bool           `json:"reimport,omitempty"`
}

// ---------------------------------------------------------------------------
// tool/call（插件声明的 MCP 工具 / API 操作）

type ToolCallParams struct {
	Tool    string          `json:"tool"`
	Args    json.RawMessage `json:"args,omitempty"`
	Project *ProjectInfo    `json:"project,omitempty"` // 工具声明 scope=project/module 时由内核解析填入
	Module  *ModuleInfo     `json:"module,omitempty"`
	TaskID  string          `json:"taskId,omitempty"`
}

// ---------------------------------------------------------------------------
// options/list：实时取表单下拉选项（FormField.Live）

type OptionsParams struct {
	Project ProjectInfo       `json:"project"`
	Module  ModuleInfo        `json:"module"`
	Keys    []string          `json:"keys"`          // 要取的 OptionsFrom 键（只取本插件声明为 live 的）
	Env     map[string]string `json:"env,omitempty"` // 工程环境变量（不含系统环境）
}

type OptionsResult struct {
	Values map[string][]Option `json:"values"`
}

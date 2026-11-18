package sdk

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
)

// Manifest 对应插件根目录下的 plugin.json。
// 以 % 包围的字符串（如 "%plugin.name%"）是本地化 key，由内核按 l10n/<locale>.json 解析。
type Manifest struct {
	ID          string  `json:"id"` // 内置插件用短 id（java、gradle）；第三方建议 "vendor.name"
	Name        string  `json:"name"`
	Version     string  `json:"version"`
	Vendor      string  `json:"vendor,omitempty"`
	Description string  `json:"description,omitempty"`
	Icon        string  `json:"icon,omitempty"` // 相对插件目录的 SVG
	Homepage    string  `json:"homepage,omitempty"`
	License     string  `json:"license,omitempty"`
	Category    string  `json:"category,omitempty"` // language | buildSystem | framework | vcs | tool
	Engines     Engines `json:"engines"`
	Protocol    int     `json:"protocol"`
	// Executable 相对插件目录、不带扩展名；Windows 上内核自动加 .exe。
	// 为空表示纯声明插件：只贡献编辑器的语言着色、快捷键方案、配色主题等静态文件，内核从不启动进程。
	Executable   string       `json:"executable,omitempty"`
	Dependencies []Dependency `json:"dependencies,omitempty"`
	Activation   Activation   `json:"activation"`
	Contributes  Contributes  `json:"contributes"`
}

// Engines 声明兼容的内核版本范围（npm semver 语法），对应 IDEA 的 since-build/until-build。
type Engines struct {
	Paperplane string `json:"paperplane"`
}

// Dependency 声明插件依赖。Optional 依赖缺失时插件仍可加载（IDEA 的 optional depends）。
type Dependency struct {
	ID       string `json:"id"`
	Version  string `json:"version,omitempty"`
	Optional bool   `json:"optional,omitempty"`
}

// Activation 决定内核何时拉起插件参与识别。
type Activation struct {
	// Files 是文件名 glob（只匹配名字，不含路径），目录名以 "/" 结尾，如 "build.gradle"、"*.csproj"、".git/"。
	Files []string `json:"files,omitempty"`
	// Facets：同一目录已被识别出其中任一 facet 时也参与识别（框架插件常用）。
	Facets []string `json:"facets,omitempty"`
}

// Contributes 是插件向内核贡献的扩展点（对应 IDEA 的 extension points）。
type Contributes struct {
	Facets                []FacetDef         `json:"facets,omitempty"`
	Toolchains            []ToolchainKindDef `json:"toolchains,omitempty"`
	RunConfigurationTypes []RunConfigTypeDef `json:"runConfigurationTypes,omitempty"`
	DependencyManagers    []string           `json:"dependencyManagers,omitempty"` // 本插件能安装依赖（实现 dependencies/install）
	Diagnostics           []DiagnosticRule   `json:"diagnostics,omitempty"`
	URLPatterns           []URLPattern       `json:"urlPatterns,omitempty"`
	ReadyPatterns         []ReadyPattern     `json:"readyPatterns,omitempty"`
	Actions               []ActionDef        `json:"actions,omitempty"`
	Settings              []FormField        `json:"settings,omitempty"`
	Services              []string           `json:"services,omitempty"`          // 可被其他插件 host/call 的方法名
	IdeRunConfigTypes     []string           `json:"ideRunConfigTypes,omitempty"` // 能转换的 IDEA/VS Code 运行配置类型
	VCS                   *VCSDef            `json:"vcs,omitempty"`
	Sync                  bool               `json:"sync,omitempty"`         // 实现了 module/sync
	Views                 []ViewDef          `json:"views,omitempty"`        // 扩展界面
	Tools                 []ToolDef          `json:"tools,omitempty"`        // 扩展 API / MCP / CLI 操作
	Languages             []LanguageDef      `json:"languages,omitempty"`    // 编辑器语言着色（docs/editor.md）
	Keymaps               []KeymapDef        `json:"keymaps,omitempty"`      // 编辑器快捷键方案
	EditorThemes          []EditorThemeDef   `json:"editorThemes,omitempty"` // 编辑器配色主题
}

// 界面挂载位置。
const (
	LocProjectTab     = "project.tab"     // 工程详情里的一个标签页
	LocProjectSection = "project.section" // 工程概览页的一个区块
	LocSidebar        = "sidebar"         // 主窗口左侧栏的全局页面（如 Docker、数据库）
	LocRunPanel       = "run.panel"       // 运行控制台旁的侧栏（如 Spring Boot 端点）
	LocVCSTab         = "vcs.tab"         // 版本控制页里的一个标签页
	LocSettings       = "settings"        // 设置里的一页
)

// 动作（按钮/菜单项）挂载位置。
const (
	ActProjectToolbar = "project.toolbar"
	ActProjectMenu    = "project.menu" // 工程详情「更多」菜单
	ActCardMenu       = "card.menu"    // 工程卡片右键/更多菜单
	ActRunToolbar     = "run.toolbar"  // 运行控制台工具栏
	ActVCSToolbar     = "vcs.toolbar"
	ActVCSFileMenu    = "vcs.fileMenu"   // 变更文件右键
	ActVCSCommitMenu  = "vcs.commitMenu" // 提交记录右键
	ActGlobalMenu     = "global.menu"    // 应用菜单
)

// ViewDef 声明一个插件界面。
type ViewDef struct {
	ID       string   `json:"id"`
	Title    string   `json:"title"`
	Icon     string   `json:"icon,omitempty"`
	Location string   `json:"location"`         // 见 Loc* 常量
	Facets   []string `json:"facets,omitempty"` // 模块带这些 facet 才显示；空表示本插件参与的模块都显示
	Kind     string   `json:"kind,omitempty"`   // declarative（默认）| webview
	Entry    string   `json:"entry,omitempty"`  // webview 的入口 HTML（相对插件目录）
	Order    int      `json:"order,omitempty"`
	RunState string   `json:"runState,omitempty"` // run.panel：仅在运行处于该状态时显示，如 running
}

// ToolDef 声明一个操作，内核把它同时暴露为 HTTP API、MCP 工具和 CLI 子命令。
// 操作名为 "<插件id>.<Name>"，MCP 工具名把 "." 和 "-" 换成 "_"。
type ToolDef struct {
	Name        string          `json:"name"`
	Title       string          `json:"title"`
	Description string          `json:"description"`           // 给 AI 看的说明，写清楚用途和参数
	Scope       string          `json:"scope,omitempty"`       // global | project | module：后两者由内核自动加 projectId/moduleId 参数并解析
	InputSchema json.RawMessage `json:"inputSchema,omitempty"` // JSON Schema（object）
	ReadOnly    bool            `json:"readOnly,omitempty"`
	Destructive bool            `json:"destructive,omitempty"`
}

type FacetDef struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Icon     string `json:"icon,omitempty"`
	Category string `json:"category,omitempty"` // language | buildSystem | framework | runtime | packageManager
	Priority int    `json:"priority,omitempty"` // 卡片上徽标排序，越大越靠前
}

type ToolchainKindDef struct {
	Kind        string `json:"kind"`
	Name        string `json:"name"`
	Icon        string `json:"icon,omitempty"`
	Installable bool   `json:"installable,omitempty"`
}

type RunConfigTypeDef struct {
	ID      string      `json:"id"`
	Name    string      `json:"name"`
	Icon    string      `json:"icon,omitempty"`
	Facets  []string    `json:"facets,omitempty"` // 模块带这些 facet 才能新建此类配置
	Options []FormField `json:"options,omitempty"`
	// AnyModule：任何模块（包括没识别出技术的工程根目录）都能新建此类配置，不要求插件参与该模块的识别分析
	// （如「脚本与可执行文件」）。这样的插件可以不声明 activation，平时不会因为分析而被拉起。
	AnyModule bool `json:"anyModule,omitempty"`
}

// FormField 是声明式表单字段，三端界面通用渲染，插件不写界面代码。
type FormField struct {
	Key   string `json:"key"`
	Title string `json:"title"`
	Type  string `json:"type"` // string | password | text | code | boolean | number | select | multiselect | keyvalue | path | list
	// Language 是 type=code 时代码输入框的语言（如 shell），界面用 CodeMirror 着色
	Language    string   `json:"language,omitempty"`
	Default     any      `json:"default,omitempty"`
	Options     []Option `json:"options,omitempty"`
	OptionsFrom string   `json:"optionsFrom,omitempty"` // 动态选项，取 AnalyzeResult.OptionValues[OptionsFrom]
	// Live 表示 OptionsFrom 的选项随时会变（如已连接的设备）：界面打开表单时及打开期间定期经 options/list 向插件实时取，
	// 分析时的 OptionValues 只作初始值
	Live        bool   `json:"live,omitempty"`
	Placeholder string `json:"placeholder,omitempty"`
	Info        string `json:"info,omitempty"` // 点 i 图标弹出的说明
	Required    bool   `json:"required,omitempty"`
	Advanced    bool   `json:"advanced,omitempty"` // 折叠在「更多」里
}

// DiagnosticRule 是声明式日志诊断规则。
type DiagnosticRule struct {
	ID       string   `json:"id"`
	Hint     string   `json:"hint,omitempty"`    // 行内必须包含的字面量（大小写敏感），用于快速预筛
	Pattern  string   `json:"pattern"`           // Go regexp 语法，对去掉 ANSI 的单行匹配
	Severity string   `json:"severity"`          // error | warning | info
	Title    string   `json:"title"`             // 可含 {1}、{2} 引用捕获组
	Message  string   `json:"message,omitempty"` // 同上
	Facets   []string `json:"facets,omitempty"`  // 仅对带这些 facet 的模块生效；空表示对本插件参与的模块生效
	Stages   []string `json:"stages,omitempty"`  // run | install | sync；空表示全部
	Fixes    []Fix    `json:"fixes,omitempty"`   // Params 值里也可用 {1}
}

// URLPattern：日志行命中时生成可点击的地址。URL 模板可用 {1}… 引用捕获组；为空则直接用第一个捕获组。
type URLPattern struct {
	Hint    string   `json:"hint,omitempty"`
	Pattern string   `json:"pattern"`
	URL     string   `json:"url,omitempty"`
	Ready   bool     `json:"ready,omitempty"` // 命中即视为就绪
	Facets  []string `json:"facets,omitempty"`
}

type ReadyPattern struct {
	Hint    string   `json:"hint,omitempty"`
	Pattern string   `json:"pattern"`
	Facets  []string `json:"facets,omitempty"`
}

// ActionDef 声明一个动作按钮/菜单项；点击时调用插件 action/invoke。动作也会自动暴露为 API/MCP（project.action）。
type ActionDef struct {
	ID        string      `json:"id"`
	Title     string      `json:"title"`
	Icon      string      `json:"icon,omitempty"`
	Facets    []string    `json:"facets,omitempty"`
	Confirm   string      `json:"confirm,omitempty"`
	Group     string      `json:"group,omitempty"`     // dependencies | build | open | other
	Locations []string    `json:"locations,omitempty"` // 见 Act* 常量；空表示 project.menu
	Form      []FormField `json:"form,omitempty"`      // 非空时先弹表单，值作为 InvokeActionParams.Args
}

type VCSDef struct {
	ID         string `json:"id"`         // git
	Name       string `json:"name"`       // Git
	RootMarker string `json:"rootMarker"` // ".git"（可以是目录或文件，worktree/submodule 时 .git 是文件）
}

// LoadManifest 读取 dir/plugin.json。
func LoadManifest(dir string) (*Manifest, error) {
	b, err := os.ReadFile(filepath.Join(dir, "plugin.json"))
	if err != nil {
		return nil, err
	}
	var m Manifest
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, fmt.Errorf("plugin.json: %w", err)
	}
	if m.ID == "" || m.Version == "" {
		return nil, fmt.Errorf("plugin.json: id/version 不能为空")
	}
	return &m, nil
}

// Declarative 表示纯声明插件（没有可执行文件，不会被启动）。
func (m *Manifest) Declarative() bool { return m.Executable == "" }

// LanguageDef 声明编辑器的一种语言着色。Module 是 ES module（相对插件目录），
// `export default function (cm) { return Extension }`，cm 为编辑器共享的 CodeMirror 命名空间。
type LanguageDef struct {
	ID               string    `json:"id"`
	Name             string    `json:"name"`
	Extensions       []string  `json:"extensions,omitempty"`       // 含点，如 ".java"
	Filenames        []string  `json:"filenames,omitempty"`        // 完整文件名，如 "Dockerfile"
	FilenamePatterns []string  `json:"filenamePatterns,omitempty"` // glob，如 "*.gradle.kts"、"Dockerfile.*"
	Module           string    `json:"module"`
	LineComment      string    `json:"lineComment,omitempty"`
	BlockComment     [2]string `json:"blockComment,omitempty"`
	Icon             string    `json:"icon,omitempty"`
	Priority         int       `json:"priority,omitempty"`
}

// KeymapDef 声明一套快捷键方案：File 为 JSON 键位表，或 Module 为 ES module（Vim/Emacs 这类模式编辑）。
type KeymapDef struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Platform string `json:"platform,omitempty"` // macos | windows | linux | any
	File     string `json:"file,omitempty"`
	Module   string `json:"module,omitempty"`
	Base     string `json:"base,omitempty"` // 在哪个方案之上叠加
}

// EditorThemeDef 声明一个编辑器配色主题（JSON）。
type EditorThemeDef struct {
	ID   string `json:"id"`
	Name string `json:"name"`
	Kind string `json:"kind"` // light | dark
	File string `json:"file"`
}

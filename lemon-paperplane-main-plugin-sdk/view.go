package sdk

// 插件界面描述语言（声明式）。
//
// 插件不写任何平台界面代码：view/render 返回一棵 ViewNode 树，Mac / Windows / Linux 三端用同一套
// 原生组件渲染，外观一致。用户操作（点按钮、提交表单、点表格行）通过 view/event 回到插件。
// 需要完全自定义的界面时，可在 manifest 里把 view 声明为 kind=webview（逃生舱，见 docs/plugin-protocol.md）。
//
// 节点类型（Type）：
//
//	布局    column  row  section(Title 可折叠)  divider  spacer  tabs(Children 每个是 tab，Title 为标签)
//	文本    heading  text(Tone, Mono)  code(只读等宽块)  markdown(仅粗体/链接/列表/行内代码)
//	信息    kv(Items)  badge(Text, Tone, Icon)  alert(Tone, Title, Text, Children 放按钮)  stat(Title, Text 大数字)
//	        progress(Value 0–100，<0 为不确定)  empty(Icon, Title, Text)  icon(Icon)
//	数据    table(Columns, Rows, Selectable)  list(Rows：每行 Cells[0] 标题、Cells[1] 副标题)
//	        tree(Rows 带 Children 与 Expanded)
//	交互    button(Text, Icon, Action, Style: primary|secondary|danger|link, Confirm)  link(Text, Href 或 Action)
//	        form(Fields, Values, Action 提交事件, SubmitText)  toolbar(Children 为 button)
//	        console(Console：绑定某个运行/任务的实时输出)
//
// Tone：neutral | info | success | warning | danger。
// Icon：插件目录内的 SVG 相对路径，或内置图标名 "builtin:<name>"（见 shared/icons）。
type ViewNode struct {
	Type       string            `json:"type"`
	ID         string            `json:"id,omitempty"`
	Title      string            `json:"title,omitempty"`
	Text       string            `json:"text,omitempty"`
	Icon       string            `json:"icon,omitempty"`
	Tone       string            `json:"tone,omitempty"`
	Info       string            `json:"info,omitempty"` // 旁边显示 i 图标，点开 popover
	Mono       bool              `json:"mono,omitempty"`
	Style      string            `json:"style,omitempty"`
	Action     string            `json:"action,omitempty"` // 事件名，触发 view/event
	Href       string            `json:"href,omitempty"`
	Confirm    string            `json:"confirm,omitempty"`
	Disabled   bool              `json:"disabled,omitempty"`
	Value      float64           `json:"value,omitempty"`
	Items      []KV              `json:"items,omitempty"`
	Columns    []TableColumn     `json:"columns,omitempty"`
	Rows       []TableRow        `json:"rows,omitempty"`
	Selectable bool              `json:"selectable,omitempty"`
	Fields     []FormField       `json:"fields,omitempty"`
	Values     map[string]any    `json:"values,omitempty"`
	SubmitText string            `json:"submitText,omitempty"`
	Console    string            `json:"console,omitempty"`
	Collapsed  bool              `json:"collapsed,omitempty"`
	Children   []ViewNode        `json:"children,omitempty"`
	Data       map[string]string `json:"data,omitempty"` // 随事件带回的附加数据
}

type KV struct {
	Key   string `json:"key"`
	Value string `json:"value"`
	Mono  bool   `json:"mono,omitempty"`
	Href  string `json:"href,omitempty"`
	Tone  string `json:"tone,omitempty"`
}

type TableColumn struct {
	Key   string `json:"key"`
	Title string `json:"title"`
	Width int    `json:"width,omitempty"` // 0 自适应
	Align string `json:"align,omitempty"` // left | right | center
	Mono  bool   `json:"mono,omitempty"`
}

type TableRow struct {
	ID       string            `json:"id"`
	Cells    map[string]string `json:"cells,omitempty"` // 表格：按列 key；list：按 "title"/"subtitle"
	Icon     string            `json:"icon,omitempty"`
	Tone     string            `json:"tone,omitempty"`
	Action   string            `json:"action,omitempty"`  // 点行触发的事件
	Actions  []ViewNode        `json:"actions,omitempty"` // 行尾按钮（button 节点）
	Children []TableRow        `json:"children,omitempty"`
	Expanded bool              `json:"expanded,omitempty"`
}

// 便捷构造函数。

func Column(children ...ViewNode) ViewNode { return ViewNode{Type: "column", Children: children} }
func Row(children ...ViewNode) ViewNode    { return ViewNode{Type: "row", Children: children} }
func Section(title string, children ...ViewNode) ViewNode {
	return ViewNode{Type: "section", Title: title, Children: children}
}
func Heading(text string) ViewNode     { return ViewNode{Type: "heading", Text: text} }
func Text(text string) ViewNode        { return ViewNode{Type: "text", Text: text} }
func Code(text string) ViewNode        { return ViewNode{Type: "code", Text: text, Mono: true} }
func KVList(items ...KV) ViewNode      { return ViewNode{Type: "kv", Items: items} }
func Badge(text, tone string) ViewNode { return ViewNode{Type: "badge", Text: text, Tone: tone} }
func Alert(tone, title, text string) ViewNode {
	return ViewNode{Type: "alert", Tone: tone, Title: title, Text: text}
}
func Button(text, action string) ViewNode {
	return ViewNode{Type: "button", Text: text, Action: action}
}
func Empty(title, text string) ViewNode { return ViewNode{Type: "empty", Title: title, Text: text} }
func Table(cols []TableColumn, rows []TableRow) ViewNode {
	return ViewNode{Type: "table", Columns: cols, Rows: rows}
}
func Form(action, submit string, fields []FormField, values map[string]any) ViewNode {
	return ViewNode{Type: "form", Action: action, SubmitText: submit, Fields: fields, Values: values}
}

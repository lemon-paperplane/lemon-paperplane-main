// Package ops 是操作注册表：每个功能定义一次，同时生成 HTTP API、MCP 工具与 CLI。
package ops

import (
	"context"
	"encoding/json"
	"errors"
	"sort"
	"strings"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/engine"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/schema"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// Op 是一个操作。
type Op struct {
	Name        string         `json:"name"`
	Title       string         `json:"title"`
	Description string         `json:"description"`
	ReadOnly    bool           `json:"readOnly"`
	Destructive bool           `json:"destructive"`
	Internal    bool           `json:"internal,omitempty"` // 不暴露给 MCP
	Plugin      string         `json:"plugin,omitempty"`
	InputSchema map[string]any `json:"inputSchema"`

	handler func(ctx context.Context, raw json.RawMessage) (any, error)
}

// Registry 持有全部操作。
type Registry struct {
	e      *engine.Engine
	ops    map[string]*Op
	order  []string
	Extras Extras
}

// Extras 是由 API 层提供的信息（MCP 地址等）。
type Extras struct {
	PID      int
	MCPURL   string
	Shutdown func(stopRuns bool)
}

// New 创建注册表并注册全部内置操作。
func New(e *engine.Engine) *Registry {
	r := &Registry{e: e, ops: map[string]*Op{}}
	r.registerAll()
	return r
}

func (r *Registry) add(op *Op) {
	r.ops[op.Name] = op
	r.order = append(r.order, op.Name)
}

// def 注册一个强类型操作。
func def[T any](r *Registry, name, title, desc string, flags string, fn func(ctx context.Context, in *T) (any, error)) *Op {
	var zero T
	op := &Op{Name: name, Title: title, Description: desc, InputSchema: schema.Of(zero)}
	op.ReadOnly = strings.Contains(flags, "r")
	op.Destructive = strings.Contains(flags, "d")
	op.Internal = strings.Contains(flags, "i")
	op.handler = func(ctx context.Context, raw json.RawMessage) (any, error) {
		in := new(T)
		if len(raw) > 0 && string(raw) != "null" {
			if err := json.Unmarshal(raw, in); err != nil {
				return nil, &BadRequest{Msg: err.Error()}
			}
		}
		return fn(ctx, in)
	}
	r.add(op)
	return op
}

// BadRequest 表示输入无法解析。
type BadRequest struct{ Msg string }

func (b *BadRequest) Error() string { return b.Msg }

// All 返回全部操作（含插件工具），按名字排序。
func (r *Registry) All() []*Op {
	var out []*Op
	for _, n := range r.order {
		out = append(out, r.ops[n])
	}
	out = append(out, r.pluginOps()...)
	sort.SliceStable(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// Get 取操作。
func (r *Registry) Get(name string) *Op {
	if op := r.ops[name]; op != nil {
		return op
	}
	for _, op := range r.pluginOps() {
		if op.Name == name {
			return op
		}
	}
	return nil
}

// ErrUnknownOp 表示没有该操作。
var ErrUnknownOp = errors.New("unknown operation")

// Call 执行操作。
func (r *Registry) Call(ctx context.Context, name string, raw json.RawMessage) (any, error) {
	op := r.Get(name)
	if op == nil {
		return nil, ErrUnknownOp
	}
	return op.handler(ctx, raw)
}

// pluginOps 把插件声明的工具包装成操作。
func (r *Registry) pluginOps() []*Op {
	var out []*Op
	for _, t := range r.e.PluginTools() {
		plugID, def := t.Plugin.ID(), t.Def
		s := map[string]any{"type": "object", "properties": map[string]any{}}
		if len(def.InputSchema) > 0 {
			_ = json.Unmarshal(def.InputSchema, &s)
		}
		props, _ := s["properties"].(map[string]any)
		if props == nil {
			props = map[string]any{}
			s["properties"] = props
		}
		if def.Scope == "project" || def.Scope == "module" {
			props["projectId"] = map[string]any{"type": "string", "description": "Project id (from project_list)."}
			req, _ := s["required"].([]any)
			s["required"] = append(req, "projectId")
		}
		if def.Scope == "module" {
			props["moduleId"] = map[string]any{"type": "string", "description": "Module id (optional, defaults to the root module)."}
		}
		op := &Op{Name: plugID + "." + def.Name, Title: t.Plugin.T(def.Title), Description: def.Description, ReadOnly: def.ReadOnly, Destructive: def.Destructive, Plugin: plugID, InputSchema: s}
		op.handler = func(ctx context.Context, raw json.RawMessage) (any, error) {
			return r.e.CallTool(ctx, plugID, def.Name, raw)
		}
		out = append(out, op)
	}
	return out
}

// MCPName 把操作名转为 MCP 工具名。
func MCPName(op string) string {
	return strings.NewReplacer(".", "_", "-", "_").Replace(op)
}

// IsNotSupported 判断插件不支持。
func IsNotSupported(err error) bool { return sdk.IsCode(err, sdk.ErrCodeNotSupported) }

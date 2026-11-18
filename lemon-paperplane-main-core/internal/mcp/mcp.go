// Package mcp 实现 Model Context Protocol 服务端（只用到 tools），传输层由调用方提供：
// HTTP（Streamable HTTP，响应用 application/json）与 stdio 桥共用 Handle。
package mcp

import (
	"context"
	"encoding/json"
	"errors"
	"sort"
	"strings"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/ops"
)

var supportedVersions = []string{"2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"}

// Instructions 是给 AI 的使用说明。
const Instructions = `Lemon Paperplane manages local software projects without an editor (like IntelliJ IDEA's project, run and Git features).
Typical flow: project_list (or project_import a folder) -> run_start {projectId, wait: 60} -> run_logs {consoleId: runId} -> if the run failed, run_get shows diagnoses with fixes -> fix_apply {runId, fixId}.
Toolchains (JDK/Node/Python/Go) and dependencies are resolved and installed automatically; project_problems lists what is missing.
Git: vcs_status, vcs_diff, vcs_commit, vcs_push, vcs_pull, vcs_branches, vcs_log and more, all taking projectId.
Long operations return a task; poll task_get {taskId, wait} and read task output with run_logs {consoleId: taskId}.`

// Handler 处理 MCP JSON-RPC 消息。
type Handler struct {
	Reg     *ops.Registry
	Version string
}

type rpcMsg struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id,omitempty"`
	Method  string          `json:"method,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
}

type rpcErr struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

type rpcResp struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id"`
	Result  any             `json:"result,omitempty"`
	Error   *rpcErr         `json:"error,omitempty"`
}

// Handle 处理一条消息（或批量数组），返回要写回的 JSON；通知返回 nil。
func (h *Handler) Handle(ctx context.Context, body []byte) []byte {
	trim := strings.TrimSpace(string(body))
	if strings.HasPrefix(trim, "[") {
		var batch []json.RawMessage
		if err := json.Unmarshal(body, &batch); err != nil {
			return mustJSON(rpcResp{JSONRPC: "2.0", ID: json.RawMessage("null"), Error: &rpcErr{-32700, "parse error"}})
		}
		var out []json.RawMessage
		for _, m := range batch {
			if r := h.handleOne(ctx, m); r != nil {
				out = append(out, r)
			}
		}
		if len(out) == 0 {
			return nil
		}
		return mustJSON(out)
	}
	return h.handleOne(ctx, body)
}

func (h *Handler) handleOne(ctx context.Context, body []byte) []byte {
	var m rpcMsg
	if err := json.Unmarshal(body, &m); err != nil {
		return mustJSON(rpcResp{JSONRPC: "2.0", ID: json.RawMessage("null"), Error: &rpcErr{-32700, "parse error"}})
	}
	if len(m.ID) == 0 {
		return nil // 通知
	}
	res, err := h.dispatch(ctx, m.Method, m.Params)
	resp := rpcResp{JSONRPC: "2.0", ID: m.ID}
	if err != nil {
		var re *rpcErr
		if errors.As(err, &re) {
			resp.Error = re
		} else {
			resp.Error = &rpcErr{-32603, err.Error()}
		}
	} else {
		resp.Result = res
	}
	return mustJSON(resp)
}

func (e *rpcErr) Error() string { return e.Message }

func (h *Handler) dispatch(ctx context.Context, method string, params json.RawMessage) (any, error) {
	switch method {
	case "initialize":
		var p struct {
			ProtocolVersion string `json:"protocolVersion"`
		}
		_ = json.Unmarshal(params, &p)
		ver := supportedVersions[0]
		for _, v := range supportedVersions {
			if v == p.ProtocolVersion {
				ver = v
			}
		}
		return map[string]any{
			"protocolVersion": ver,
			"capabilities":    map[string]any{"tools": map[string]any{"listChanged": false}},
			"serverInfo":      map[string]any{"name": "lemon-paperplane", "title": "Lemon Paperplane", "version": h.Version},
			"instructions":    Instructions,
		}, nil
	case "ping":
		return map[string]any{}, nil
	case "tools/list":
		var tools []map[string]any
		for _, op := range h.Reg.All() {
			if op.Internal {
				continue
			}
			tools = append(tools, map[string]any{
				"name":        ops.MCPName(op.Name),
				"title":       op.Title,
				"description": op.Description,
				"inputSchema": op.InputSchema,
				"annotations": map[string]any{"title": op.Title, "readOnlyHint": op.ReadOnly, "destructiveHint": op.Destructive, "openWorldHint": false},
			})
		}
		return map[string]any{"tools": tools}, nil
	case "tools/call":
		var p struct {
			Name      string          `json:"name"`
			Arguments json.RawMessage `json:"arguments"`
		}
		if err := json.Unmarshal(params, &p); err != nil {
			return nil, &rpcErr{-32602, err.Error()}
		}
		var target *ops.Op
		for _, op := range h.Reg.All() {
			if !op.Internal && ops.MCPName(op.Name) == p.Name {
				target = op
			}
		}
		if target == nil {
			return nil, &rpcErr{-32602, "unknown tool " + p.Name}
		}
		if msg := checkArgs(target.InputSchema, p.Arguments); msg != "" {
			return map[string]any{"content": []map[string]any{{"type": "text", "text": "Error: " + msg}}, "isError": true}, nil
		}
		res, err := h.Reg.Call(ctx, target.Name, p.Arguments)
		if err != nil {
			return map[string]any{"content": []map[string]any{{"type": "text", "text": "Error: " + err.Error()}}, "isError": true}, nil
		}
		b, _ := json.MarshalIndent(res, "", " ")
		out := map[string]any{"content": []map[string]any{{"type": "text", "text": string(b)}}}
		var obj map[string]any
		if json.Unmarshal(b, &obj) == nil && obj != nil {
			out["structuredContent"] = obj
		}
		return out, nil
	case "resources/list":
		return map[string]any{"resources": []any{}}, nil
	case "prompts/list":
		return map[string]any{"prompts": []any{}}, nil
	}
	return nil, &rpcErr{-32601, "method not found: " + method}
}

// checkArgs 按工具的输入 schema 检查必填参数与未知参数，出错时告诉 AI 正确的参数名
// （否则写错参数名会被当成空值，得到「找不到对象」这类让人摸不着头脑的结果）。
func checkArgs(schema any, raw json.RawMessage) string {
	var s struct {
		Properties           map[string]json.RawMessage `json:"properties"`
		Required             []string                   `json:"required"`
		AdditionalProperties json.RawMessage            `json:"additionalProperties"`
	}
	b, err := json.Marshal(schema)
	if err != nil || json.Unmarshal(b, &s) != nil || s.Properties == nil {
		return ""
	}
	args := map[string]json.RawMessage{}
	if len(raw) > 0 && string(raw) != "null" {
		if json.Unmarshal(raw, &args) != nil {
			return "arguments must be a JSON object"
		}
	}
	names := make([]string, 0, len(s.Properties))
	for k := range s.Properties {
		names = append(names, k)
	}
	sort.Strings(names)
	var unknown, missing []string
	openSchema := len(s.AdditionalProperties) > 0 && string(s.AdditionalProperties) != "false"
	for k := range args {
		if _, ok := s.Properties[k]; !ok && !openSchema {
			unknown = append(unknown, k)
		}
	}
	for _, k := range s.Required {
		if v, ok := args[k]; !ok || string(v) == "null" {
			missing = append(missing, k)
		}
	}
	if len(unknown) == 0 && len(missing) == 0 {
		return ""
	}
	sort.Strings(unknown)
	var parts []string
	if len(missing) > 0 {
		parts = append(parts, "missing required parameter(s): "+strings.Join(missing, ", "))
	}
	if len(unknown) > 0 {
		parts = append(parts, "unknown parameter(s): "+strings.Join(unknown, ", "))
	}
	return strings.Join(parts, "; ") + ". Accepted parameters: " + strings.Join(names, ", ")
}

func mustJSON(v any) []byte {
	b, _ := json.Marshal(v)
	return b
}

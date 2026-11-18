package mcp

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestCheckArgs(t *testing.T) {
	schema := map[string]any{"type": "object", "properties": map[string]any{"pluginId": map[string]any{"type": "string"}, "force": map[string]any{"type": "boolean"}}, "required": []any{"pluginId"}}
	if msg := checkArgs(schema, json.RawMessage(`{"pluginId":"x"}`)); msg != "" {
		t.Fatal(msg)
	}
	msg := checkArgs(schema, json.RawMessage(`{"id":"x"}`))
	if !strings.Contains(msg, "missing required parameter(s): pluginId") || !strings.Contains(msg, "unknown parameter(s): id") || !strings.Contains(msg, "Accepted parameters: force, pluginId") {
		t.Fatal(msg)
	}
	open := map[string]any{"type": "object", "properties": map[string]any{"a": map[string]any{}}, "additionalProperties": true}
	if msg := checkArgs(open, json.RawMessage(`{"b":1}`)); msg != "" {
		t.Fatal(msg)
	}
	if msg := checkArgs(map[string]any{"type": "object"}, json.RawMessage(`{"anything":1}`)); msg != "" {
		t.Fatal(msg)
	}
}

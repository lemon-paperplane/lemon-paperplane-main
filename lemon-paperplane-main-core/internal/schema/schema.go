// Package schema 用反射从 Go 结构生成 JSON Schema（给 MCP 工具与 /api/v1/ops 用）。
// 字段描述写在 `desc:"..."` tag 里；`required:"true"` 标必填。
package schema

import (
	"encoding/json"
	"reflect"
	"strings"
	"time"
)

// Of 生成类型 t 的 schema（object）。
func Of(v any) map[string]any {
	if v == nil {
		return map[string]any{"type": "object", "properties": map[string]any{}}
	}
	return gen(reflect.TypeOf(v), 0)
}

var rawMessage = reflect.TypeOf(json.RawMessage{})
var timeType = reflect.TypeOf(time.Time{})

func gen(t reflect.Type, depth int) map[string]any {
	for t.Kind() == reflect.Pointer {
		t = t.Elem()
	}
	if depth > 6 {
		return map[string]any{}
	}
	if t == rawMessage {
		return map[string]any{}
	}
	if t == timeType {
		return map[string]any{"type": "string", "format": "date-time"}
	}
	switch t.Kind() {
	case reflect.String:
		return map[string]any{"type": "string"}
	case reflect.Bool:
		return map[string]any{"type": "boolean"}
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64, reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return map[string]any{"type": "integer"}
	case reflect.Float32, reflect.Float64:
		return map[string]any{"type": "number"}
	case reflect.Slice, reflect.Array:
		return map[string]any{"type": "array", "items": gen(t.Elem(), depth+1)}
	case reflect.Map:
		return map[string]any{"type": "object", "additionalProperties": gen(t.Elem(), depth+1)}
	case reflect.Interface:
		return map[string]any{}
	case reflect.Struct:
		props := map[string]any{}
		var req []string
		addFields(t, props, &req, depth)
		s := map[string]any{"type": "object", "properties": props}
		if len(req) > 0 {
			s["required"] = req
		}
		return s
	}
	return map[string]any{}
}

func addFields(t reflect.Type, props map[string]any, req *[]string, depth int) {
	for i := 0; i < t.NumField(); i++ {
		f := t.Field(i)
		if !f.IsExported() {
			continue
		}
		tag := f.Tag.Get("json")
		if tag == "-" {
			continue
		}
		name, _, _ := strings.Cut(tag, ",")
		if f.Anonymous && name == "" {
			ft := f.Type
			for ft.Kind() == reflect.Pointer {
				ft = ft.Elem()
			}
			if ft.Kind() == reflect.Struct {
				addFields(ft, props, req, depth)
				continue
			}
		}
		if name == "" {
			name = f.Name
		}
		s := gen(f.Type, depth+1)
		if d := f.Tag.Get("desc"); d != "" {
			s["description"] = d
		}
		if e := f.Tag.Get("enum"); e != "" {
			s["enum"] = strings.Split(e, "|")
		}
		props[name] = s
		if f.Tag.Get("required") == "true" {
			*req = append(*req, name)
		}
	}
}

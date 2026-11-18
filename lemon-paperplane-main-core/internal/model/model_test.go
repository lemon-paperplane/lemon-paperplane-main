package model

import (
	"encoding/json"
	"strings"
	"testing"

	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// 插件给的 nil 切片（如 pom 里没写 profiles）不能输出成 null：Mac 界面按数组解析，会让整个导入结果解析失败。
func TestModuleAnalysisOptionValuesNeverNull(t *testing.T) {
	a := &ModuleAnalysis{OptionValues: map[string][]sdk.Option{"maven.profiles": nil, "jvm.modules": {{Value: ":"}}}}
	for _, v := range []any{a, *a, map[string]*ModuleAnalysis{"maven": a}} {
		b, err := json.Marshal(v)
		if err != nil {
			t.Fatal(err)
		}
		if strings.Contains(string(b), "null") || !strings.Contains(string(b), `"maven.profiles":[]`) {
			t.Fatalf("got %s", b)
		}
	}
	if a.OptionValues["maven.profiles"] != nil {
		t.Fatal("marshal must not mutate the stored map")
	}
}

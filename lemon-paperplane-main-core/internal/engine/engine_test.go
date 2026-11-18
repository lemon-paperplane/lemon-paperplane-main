package engine

import (
	"path/filepath"
	"runtime"
	"testing"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

func TestPickToolchain(t *testing.T) {
	tcs := []sdk.Toolchain{
		{ID: "a", Version: "21.0.12", Arch: runtime.GOARCH},
		{ID: "b", Version: "17.0.16", Arch: runtime.GOARCH},
		{ID: "c", Version: "8.0.492", Arch: runtime.GOARCH},
		{ID: "d", Version: "23.0.1-ea", Arch: runtime.GOARCH},
		{ID: "e", Version: "17.0.20", Arch: "x86_64_other"},
	}
	cases := []struct {
		cons []string
		pref string
		want string
	}{
		{[]string{">=17"}, "17", "b"},
		{[]string{">=17"}, "", "a"},
		{[]string{">=8 <=17"}, "", "b"},
		{[]string{">=17", "<=17"}, "", "b"},
		{[]string{">=24"}, "", ""},
		{[]string{"8"}, "", "c"},
	}
	for _, c := range cases {
		got := pickToolchain(tcs, c.cons, c.pref)
		id := ""
		if got != nil {
			id = got.ID
		}
		if id != c.want {
			t.Errorf("pick(%v,%q)=%q want %q", c.cons, c.pref, id, c.want)
		}
	}
}

func TestMergeConfigs(t *testing.T) {
	m := &model.Module{ID: "m1"}
	mergeConfigs(m, []sdk.RunConfiguration{{ID: "x", Name: "dev", Type: "npm-script", Default: true}, {ID: "y", Name: "build", Type: "npm-script"}}, []string{"nodejs", "nodejs"})
	if len(m.RunConfigs) != 2 || !m.RunConfigs[0].Default {
		t.Fatalf("unexpected %+v", m.RunConfigs)
	}
	devID := m.RunConfigs[0].ID
	// 用户编辑 dev，新增用户配置
	m.RunConfigs[0].Edited = true
	m.RunConfigs[0].Name = "dev (mine)"
	m.RunConfigs = append(m.RunConfigs, &model.RunConfig{RunConfiguration: sdk.RunConfiguration{ID: "u1", Name: "custom", Source: "user"}})
	// 重新分析：build 消失，dev 仍在
	mergeConfigs(m, []sdk.RunConfiguration{{ID: "x", Name: "dev", Type: "npm-script", Default: true}}, []string{"nodejs"})
	if len(m.RunConfigs) != 2 {
		t.Fatalf("want 2 configs, got %d", len(m.RunConfigs))
	}
	if m.RunConfigs[0].ID != devID || m.RunConfigs[0].Name != "dev (mine)" {
		t.Fatalf("edited config not preserved: %+v", m.RunConfigs[0])
	}
	if m.RunConfigs[1].ID != "u1" {
		t.Fatalf("user config lost")
	}
	// 已有 id 原样传回不应被二次哈希
	mergeConfigs(m, []sdk.RunConfiguration{m.RunConfigs[0].RunConfiguration}, []string{"nodejs"})
	if m.RunConfigs[0].ID != devID {
		t.Fatalf("id changed on re-merge")
	}
}

func TestReadIdeConfigs(t *testing.T) {
	cs := readIdeConfigs(filepath.Join("testdata", "ideproj"))
	var boot, vsc *sdk.IdeRunConfig
	for i := range cs {
		switch cs[i].Type {
		case "SpringBootApplicationConfigurationType":
			boot = &cs[i]
		case "node":
			vsc = &cs[i]
		case "Application":
			t.Errorf("temporary config should be skipped")
		}
	}
	if boot == nil || boot.Options["SPRING_BOOT_MAIN_CLASS"] != "online.vrtalk.api.starter.App" || boot.Options["VM_PARAMETERS"] != `-Xmx1g -Dfoo="a b"` || boot.Env["TZ"] != "Asia/Shanghai" || boot.Module != "vrtalk-api.vrtalk-api-starter.main" {
		t.Fatalf("spring boot config: %+v", boot)
	}
	if vsc == nil || vsc.Name != "Dev" || vsc.Env["PORT"] != "3000" || vsc.Options["runtimeExecutable"] != "npm" {
		t.Fatalf("vscode config: %+v", vsc)
	}
}

func TestNormalizeURL(t *testing.T) {
	for in, want := range map[string]string{
		"http://0.0.0.0:8000":       "http://localhost:8000",
		"http://localhost:5173/.":   "http://localhost:5173/",
		"http://127.0.0.1:8080/api": "http://127.0.0.1:8080/api",
	} {
		if got := normalizeURL(in); got != want {
			t.Errorf("normalize(%q)=%q want %q", in, got, want)
		}
	}
	if u := coreURLRe.FindString("  ➜  Local:   http://localhost:5173/ "); u != "http://localhost:5173/" {
		t.Errorf("core url: %q", u)
	}
}

func TestReadIdeConfigsRecentTemporary(t *testing.T) {
	cs := readIdeConfigs(filepath.Join("testdata", "pyproj"))
	if len(cs) != 1 || cs[0].Name != "main" || cs[0].Options["SCRIPT_NAME"] != "$PROJECT_DIR$/main.py" {
		t.Fatalf("got %+v", cs)
	}
}

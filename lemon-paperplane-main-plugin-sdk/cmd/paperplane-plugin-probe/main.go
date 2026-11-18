// paperplane-plugin-probe 是插件开发探针：一个最小化的宿主，不依赖 daemon，
// 直接加载若干插件目录，对一个工程目录跑 识别 → 分析 → 选工具链 →（同步）→ 准备运行 → 运行。
//
// 用法：
//
//	paperplane-plugin-probe -plugins ../lemon-paperplane-plugin-java,../lemon-paperplane-plugin-gradle analyze <工程目录>
//	paperplane-plugin-probe -plugins ... sync <工程目录>
//	paperplane-plugin-probe -plugins ... install <工程目录> [模块相对路径]
//	paperplane-plugin-probe -plugins ... run <工程目录> [-module rel] [-config 名称或序号] [-exec]
//	paperplane-plugin-probe -plugins ... toolchains <kind>
//	paperplane-plugin-probe -plugins ... available <kind>
//	paperplane-plugin-probe -plugins ... install-toolchain <kind> <version> <目标目录>
//	paperplane-plugin-probe -plugins ... action <工程目录> <模块相对路径> <actionId>
//	paperplane-plugin-probe -plugins ... vcs <仓库目录> <op> [argsJSON]
//
// 插件目录里需要已经构建好 plugin.json 中 executable 指向的可执行文件。
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk/archive"
)

type plugin struct {
	dir  string
	m    *sdk.Manifest
	once sync.Once
	conn *sdk.Conn
	err  error
}

type probe struct {
	plugins []*plugin
	byID    map[string]*plugin
	mirrors sdk.Mirrors
	locale  string
	dataDir string
	verbose bool
	tcMu    sync.Mutex
	tcCache map[string][]sdk.Toolchain
}

type module struct {
	info     sdk.ModuleInfo
	results  map[string]*sdk.AnalyzeResult
	selected map[string]sdk.Toolchain
	missing  []sdk.ToolchainRequirement
	configs  []sdk.RunConfiguration
}

func main() {
	pluginsFlag := flag.String("plugins", "", "逗号分隔的插件目录")
	global := flag.Bool("global", false, "使用官方源（默认国内镜像）")
	locale := flag.String("locale", "zh-Hans", "界面语言")
	verbose := flag.Bool("v", false, "打印插件日志")
	flag.Parse()
	args := flag.Args()
	if len(args) < 1 || *pluginsFlag == "" {
		flag.Usage()
		os.Exit(2)
	}
	p := &probe{byID: map[string]*plugin{}, locale: *locale, verbose: *verbose, tcCache: map[string][]sdk.Toolchain{}}
	p.mirrors = chinaMirrors()
	if *global {
		p.mirrors = sdk.Mirrors{Region: "global"}
	}
	home, _ := os.UserHomeDir()
	p.dataDir = filepath.Join(home, ".paperplane-probe")
	for _, d := range strings.Split(*pluginsFlag, ",") {
		d = strings.TrimSpace(d)
		if d == "" {
			continue
		}
		abs, _ := filepath.Abs(d)
		m, err := sdk.LoadManifest(abs)
		if err != nil {
			fatal("加载插件 %s: %v", d, err)
		}
		pl := &plugin{dir: abs, m: m}
		p.plugins = append(p.plugins, pl)
		p.byID[m.ID] = pl
	}
	p.plugins = topoSort(p.plugins, p.byID)
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	defer p.shutdown()

	cmd, rest := args[0], args[1:]
	switch cmd {
	case "detect":
		need(rest, 1)
		mods := p.scan(ctx, rest[0])
		printJSON(mods)
	case "analyze", "sync":
		need(rest, 1)
		mods := p.analyzeAll(ctx, rest[0], cmd == "sync")
		p.report(mods)
	case "install":
		need(rest, 1)
		mods := p.analyzeAll(ctx, rest[0], false)
		m := pickModule(mods, arg(rest, 1))
		for _, pl := range p.plugins {
			if !contains(pl.m.Contributes.DependencyManagers, "") && len(pl.m.Contributes.DependencyManagers) > 0 && p.participates(pl, m) {
				var r sdk.InstallDepsResult
				err := p.call(ctx, pl, sdk.MethodInstallDeps, &sdk.InstallDepsParams{
					Project: projectInfo(rest[0]), Module: m.info, Toolchains: m.selected, Env: p.mirrorEnv(), TaskID: "task-install",
				}, &r)
				fmt.Printf("== %s install: %v %+v\n", pl.m.ID, err, r.Dependencies)
			}
		}
	case "run":
		fs := flag.NewFlagSet("run", flag.ExitOnError)
		modFlag := fs.String("module", "", "模块相对路径")
		cfgFlag := fs.String("config", "", "运行配置名称或序号")
		doExec := fs.Bool("exec", false, "真正启动并输出日志（Ctrl-C 停止）")
		need(rest, 1)
		dir := rest[0]
		_ = fs.Parse(rest[1:])
		mods := p.analyzeAll(ctx, dir, false)
		m := pickModule(mods, *modFlag)
		if len(m.configs) == 0 {
			fatal("模块没有运行配置")
		}
		cfg := m.configs[0]
		for i, c := range m.configs {
			if *cfgFlag == c.Name || *cfgFlag == strconv.Itoa(i) || (*cfgFlag == "" && c.Default) {
				cfg = c
				break
			}
		}
		pl := p.byID[cfg.Plugin]
		fmt.Printf("== 准备运行 %q（%s / %s）\n", cfg.Name, cfg.Plugin, cfg.Type)
		var spec sdk.LaunchSpec
		if err := p.call(ctx, pl, sdk.MethodPrepareRun, &sdk.PrepareRunParams{
			RunID: "run-1", Project: projectInfo(dir), Module: m.info, Config: cfg, Toolchains: m.selected, Env: sdk.MergeEnv(p.mirrorEnv(), cfg.Env),
		}, &spec); err != nil {
			fatal("run/prepare: %v", err)
		}
		printJSON(spec)
		if *doExec {
			env := sdk.MergeEnv(p.mirrorEnv(), cfg.Env, spec.Env)
			tenv, tpath := toolchainEnvAll(m.selected)
			env = sdk.MergeEnv(tenv, env)
			code := p.execStream(ctx, spec.Command, spec.Args, spec.Cwd, env, append(spec.PathPrepend, tpath...), "run")
			fmt.Printf("== 进程退出 %d\n", code)
		}
	case "toolchains":
		need(rest, 1)
		printJSON(p.toolchains(ctx, rest[0]))
	case "available":
		need(rest, 1)
		pl := p.toolchainPlugin(rest[0])
		var r sdk.AvailableToolchainsResult
		check(p.call(ctx, pl, sdk.MethodAvailableToolchain, &sdk.AvailableToolchainsParams{Kind: rest[0]}, &r))
		printJSON(r)
	case "install-toolchain":
		need(rest, 3)
		pl := p.toolchainPlugin(rest[0])
		dest, _ := filepath.Abs(rest[2])
		var r sdk.InstallToolchainResult
		check(p.call(ctx, pl, sdk.MethodInstallToolchain, &sdk.InstallToolchainParams{Kind: rest[0], Version: rest[1], Dest: dest, TaskID: "task-tc"}, &r))
		if r.Plan != nil {
			printJSON(r.Plan)
			file := filepath.Join(p.dataDir, "cache", fileName(r.Plan))
			u, err := archive.Download(ctx, r.Plan.URLs, file, r.Plan.SHA256, func(d, t int64) { fmt.Printf("\r下载 %d/%d", d, t) })
			fmt.Println()
			check(err)
			fmt.Println("来源:", u)
			check(archive.Extract(file, dest, r.Plan.Archive, r.Plan.StripComponents))
			var dr sdk.DiscoverToolchainsResult
			check(p.call(ctx, pl, sdk.MethodDiscoverToolchains, &sdk.DiscoverToolchainsParams{Kind: rest[0], Paths: []string{dest}}, &dr))
			printJSON(dr)
		} else {
			printJSON(r.Toolchain)
		}
	case "action":
		need(rest, 3)
		mods := p.analyzeAll(ctx, rest[0], false)
		m := pickModule(mods, rest[1])
		for _, pl := range p.plugins {
			for _, a := range pl.m.Contributes.Actions {
				if a.ID == rest[2] {
					var r sdk.InvokeActionResult
					check(p.call(ctx, pl, sdk.MethodInvokeAction, &sdk.InvokeActionParams{Action: a.ID, Project: projectInfo(rest[0]), Module: m.info, Toolchains: m.selected, Env: p.mirrorEnv(), TaskID: "task-action"}, &r))
					printJSON(r)
					return
				}
			}
		}
		fatal("没有找到 action %s", rest[2])
	case "vcs":
		need(rest, 2)
		var pl *plugin
		for _, x := range p.plugins {
			if x.m.Contributes.VCS != nil {
				pl = x
			}
		}
		if pl == nil {
			fatal("没有 VCS 插件")
		}
		root, _ := filepath.Abs(rest[0])
		argsJSON := json.RawMessage(arg(rest, 2))
		if len(argsJSON) == 0 {
			argsJSON = json.RawMessage("{}")
		}
		var r json.RawMessage
		check(p.call(ctx, pl, sdk.MethodVCS, &sdk.VCSCallParams{Op: rest[1], Root: root, Args: argsJSON, TaskID: "task-vcs"}, &r))
		var pretty any
		_ = json.Unmarshal(r, &pretty)
		printJSON(pretty)
	default:
		fatal("未知命令 %s", cmd)
	}
}

// ---------------------------------------------------------------------------
// 识别

type scanned struct {
	RelDir string      `json:"relDir"`
	Name   string      `json:"name"`
	Facets []sdk.Facet `json:"facets"`
}

func (p *probe) scan(ctx context.Context, root string) []scanned {
	root, _ = filepath.Abs(root)
	var out []scanned
	claimedAll := map[string]bool{}
	claimed := map[string]bool{}
	var walk func(dir string, depth int)
	walk = func(dir string, depth int) {
		rel, _ := filepath.Rel(root, dir)
		if rel == "." {
			rel = ""
		}
		entries, err := os.ReadDir(dir)
		if err != nil {
			return
		}
		var names []string
		var subdirs []string
		for _, e := range entries {
			if e.IsDir() {
				names = append(names, e.Name()+"/")
				if !sdk.DefaultSkipDirs[e.Name()] && !strings.HasPrefix(e.Name(), ".") {
					subdirs = append(subdirs, e.Name())
				}
			} else {
				names = append(names, e.Name())
			}
		}
		var facets []sdk.Facet
		name := ""
		if !claimed[rel] || rel == "" {
			for _, pl := range p.plugins {
				if !activates(pl.m, names, facets) {
					continue
				}
				var r sdk.DetectResult
				if err := p.call(ctx, pl, sdk.MethodDetect, &sdk.DetectParams{Root: root, Dir: dir, RelDir: rel, Files: names, Facets: facets}, &r); err != nil {
					if !sdk.IsCode(err, sdk.ErrCodeNotSupported) {
						fmt.Fprintf(os.Stderr, "detect %s @%s: %v\n", pl.m.ID, rel, err)
					}
					continue
				}
				for _, f := range r.Facets {
					f.Plugin = pl.m.ID
					facets = append(facets, f)
				}
				if r.Name != "" && name == "" {
					name = r.Name
				}
				for _, c := range r.Claims {
					if c == "**" {
						claimedAll[rel] = true
					} else {
						claimed[filepath.ToSlash(filepath.Join(rel, c))] = true
					}
				}
			}
		}
		if len(facets) > 0 && !claimed[rel] {
			if name == "" {
				name = filepath.Base(dir)
			}
			out = append(out, scanned{RelDir: filepath.ToSlash(rel), Name: name, Facets: facets})
		}
		if claimedAll[rel] || depth >= 4 {
			return
		}
		for _, s := range subdirs {
			walk(filepath.Join(dir, s), depth+1)
		}
	}
	walk(root, 0)
	return out
}

func activates(m *sdk.Manifest, names []string, facets []sdk.Facet) bool {
	for _, n := range names {
		for _, pat := range m.Activation.Files {
			if ok, _ := filepath.Match(pat, n); ok {
				return true
			}
		}
	}
	for _, f := range facets {
		if contains(m.Activation.Facets, f.ID) {
			return true
		}
	}
	return false
}

// ---------------------------------------------------------------------------
// 分析与工具链

func (p *probe) analyzeAll(ctx context.Context, root string, doSync bool) []*module {
	root, _ = filepath.Abs(root)
	var mods []*module
	for _, s := range p.scan(ctx, root) {
		m := &module{results: map[string]*sdk.AnalyzeResult{}, selected: map[string]sdk.Toolchain{}}
		m.info = sdk.ModuleInfo{ID: sdk.StableID(s.RelDir), Name: s.Name, Dir: filepath.Join(root, s.RelDir), RelDir: s.RelDir, Facets: s.Facets, Models: map[string]json.RawMessage{}}
		var reqs []sdk.ToolchainRequirement
		ide := readIdeaConfigs(root)
		for _, pl := range p.plugins {
			if !p.participates(pl, m) {
				continue
			}
			var r sdk.AnalyzeResult
			var mine []sdk.IdeRunConfig
			for _, c := range ide {
				if contains(pl.m.Contributes.IdeRunConfigTypes, c.Type) {
					mine = append(mine, c)
				}
			}
			if err := p.call(ctx, pl, sdk.MethodAnalyze, &sdk.AnalyzeParams{Project: projectInfo(root), Module: m.info, IdeConfigs: mine}, &r); err != nil {
				if !sdk.IsCode(err, sdk.ErrCodeNotSupported) {
					fmt.Fprintf(os.Stderr, "analyze %s: %v\n", pl.m.ID, err)
				}
				continue
			}
			m.results[pl.m.ID] = &r
			if len(r.Model) > 0 {
				m.info.Models[pl.m.ID] = r.Model
			}
			reqs = append(reqs, r.Requirements...)
		}
		p.selectToolchains(ctx, m, reqs)
		if doSync {
			for _, pl := range p.plugins {
				if !pl.m.Contributes.Sync || !p.participates(pl, m) {
					continue
				}
				fmt.Printf("== 同步 %s ...\n", pl.m.ID)
				var r sdk.AnalyzeResult
				if err := p.call(ctx, pl, sdk.MethodSync, &sdk.AnalyzeParams{Project: projectInfo(root), Module: m.info, Toolchains: m.selected, TaskID: "task-sync"}, &r); err != nil {
					fmt.Fprintf(os.Stderr, "sync %s: %v\n", pl.m.ID, err)
					continue
				}
				m.results[pl.m.ID] = mergeResult(m.results[pl.m.ID], &r)
				if len(r.Model) > 0 {
					m.info.Models[pl.m.ID] = r.Model
				}
			}
			// 同步后再让依赖插件重新分析一遍，以便看到新模型
			for _, pl := range p.plugins {
				if pl.m.Contributes.Sync || !p.participates(pl, m) {
					continue
				}
				var r sdk.AnalyzeResult
				if err := p.call(ctx, pl, sdk.MethodAnalyze, &sdk.AnalyzeParams{Project: projectInfo(root), Module: m.info, Toolchains: m.selected}, &r); err == nil {
					m.results[pl.m.ID] = &r
					if len(r.Model) > 0 {
						m.info.Models[pl.m.ID] = r.Model
					}
				}
			}
		}
		for id, r := range m.results {
			for _, c := range r.RunConfigurations {
				c.Plugin = id
				c.ModuleID = m.info.ID
				m.configs = append(m.configs, c)
			}
		}
		sort.SliceStable(m.configs, func(i, j int) bool {
			if m.configs[i].Default != m.configs[j].Default {
				return m.configs[i].Default
			}
			return m.configs[i].Priority > m.configs[j].Priority
		})
		mods = append(mods, m)
	}
	return mods
}

func mergeResult(a, b *sdk.AnalyzeResult) *sdk.AnalyzeResult {
	if a == nil {
		return b
	}
	if len(b.RunConfigurations) > 0 {
		a.RunConfigurations = b.RunConfigurations
	}
	if len(b.Model) > 0 {
		a.Model = b.Model
	}
	a.Problems = append(a.Problems, b.Problems...)
	if len(b.Info) > 0 {
		a.Info = b.Info
	}
	if b.Dependencies != nil {
		a.Dependencies = b.Dependencies
	}
	return a
}

func (p *probe) participates(pl *plugin, m *module) bool {
	for _, f := range m.info.Facets {
		if f.Plugin == pl.m.ID || contains(pl.m.Activation.Facets, f.ID) {
			return true
		}
	}
	return false
}

func (p *probe) selectToolchains(ctx context.Context, m *module, reqs []sdk.ToolchainRequirement) {
	byKind := map[string][]sdk.ToolchainRequirement{}
	for _, r := range reqs {
		byKind[r.Kind] = append(byKind[r.Kind], r)
	}
	for kind, rs := range byKind {
		var cons []string
		pref := ""
		for _, r := range rs {
			if r.Constraint != "" {
				cons = append(cons, r.Constraint)
			}
			if pref == "" {
				pref = r.Preferred
			}
		}
		tcs := p.toolchains(ctx, kind)
		var vs []string
		for _, t := range tcs {
			vs = append(vs, t.Version)
		}
		// 多个约束取交集：逐个过滤
		idx := -1
		best := -1
		for i, v := range vs {
			ok := true
			for _, c := range cons {
				if !sdk.MatchVersion(c, v) {
					ok = false
				}
			}
			if !ok {
				continue
			}
			isPref := pref != "" && sdk.MatchVersion(pref, v)
			if idx < 0 || (isPref && best != 1) || (boolInt(isPref) == best && sdk.CompareVersions(v, vs[idx]) > 0) {
				idx = i
				best = boolInt(isPref)
			}
		}
		if idx >= 0 {
			m.selected[kind] = tcs[idx]
		} else {
			m.missing = append(m.missing, rs...)
		}
	}
}

func boolInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

func (p *probe) toolchainPlugin(kind string) *plugin {
	for _, pl := range p.plugins {
		for _, k := range pl.m.Contributes.Toolchains {
			if k.Kind == kind {
				return pl
			}
		}
	}
	fatal("没有提供工具链 %s 的插件", kind)
	return nil
}

func (p *probe) toolchains(ctx context.Context, kind string) []sdk.Toolchain {
	p.tcMu.Lock()
	if c, ok := p.tcCache[kind]; ok {
		p.tcMu.Unlock()
		return c
	}
	p.tcMu.Unlock()
	var out []sdk.Toolchain
	for _, pl := range p.plugins {
		for _, k := range pl.m.Contributes.Toolchains {
			if k.Kind != kind {
				continue
			}
			var r sdk.DiscoverToolchainsResult
			if err := p.call(ctx, pl, sdk.MethodDiscoverToolchains, &sdk.DiscoverToolchainsParams{Kind: kind}, &r); err != nil {
				fmt.Fprintf(os.Stderr, "discover %s: %v\n", kind, err)
				continue
			}
			for _, t := range r.Toolchains {
				t.ID = kind + ":" + t.Path
				out = append(out, t)
			}
		}
	}
	p.tcMu.Lock()
	p.tcCache[kind] = out
	p.tcMu.Unlock()
	return out
}

func (p *probe) report(mods []*module) {
	for _, m := range mods {
		fmt.Printf("\n## 模块 %q  (%s)\n", m.info.Name, orDot(m.info.RelDir))
		var fs []string
		for _, f := range m.info.Facets {
			fs = append(fs, f.ID)
		}
		fmt.Println("facets:", strings.Join(fs, ", "))
		ids := make([]string, 0, len(m.results))
		for id := range m.results {
			ids = append(ids, id)
		}
		sort.Strings(ids)
		for _, id := range ids {
			r := m.results[id]
			fmt.Printf("- [%s]", id)
			for _, i := range r.Info {
				fmt.Printf(" %s=%s", i.Label, i.Value)
			}
			if r.Dependencies != nil {
				fmt.Printf(" deps=%s(%s) %s", r.Dependencies.State, r.Dependencies.Manager, r.Dependencies.Message)
			}
			if r.NeedsSync {
				fmt.Print(" needsSync")
			}
			fmt.Println()
			for _, q := range r.Requirements {
				fmt.Printf("    需要 %s %s (偏好 %s，依据 %s %s)\n", q.Kind, q.Constraint, q.Preferred, q.Source, q.Purpose)
			}
			for _, d := range r.Problems {
				fmt.Printf("    问题[%s] %s %s\n", d.Severity, d.Title, d.Message)
			}
			for k, v := range r.OptionValues {
				fmt.Printf("    选项 %s: %d 个\n", k, len(v))
			}
		}
		for k, t := range m.selected {
			fmt.Printf("工具链 %s → %s %s (%s) %s\n", k, t.Version, t.Vendor, t.Source, t.Path)
		}
		for _, r := range m.missing {
			fmt.Printf("缺少工具链 %s %s\n", r.Kind, r.Constraint)
		}
		for i, c := range m.configs {
			def := ""
			if c.Default {
				def = " (默认)"
			}
			o, _ := json.Marshal(c.Options)
			fmt.Printf("运行配置 #%d %q [%s/%s]%s %s\n", i, c.Name, c.Plugin, c.Type, def, o)
		}
	}
}

// ---------------------------------------------------------------------------
// 插件进程

func (p *probe) start(pl *plugin) error {
	if pl.m.Declarative() {
		return sdk.ErrNotSupported // 纯声明插件没有进程
	}
	pl.once.Do(func() {
		exe := filepath.Join(pl.dir, filepath.FromSlash(pl.m.Executable))
		if runtime.GOOS == "windows" {
			exe += ".exe"
		}
		cmd := exec.Command(exe)
		stdin, _ := cmd.StdinPipe()
		stdout, _ := cmd.StdoutPipe()
		stderr, _ := cmd.StderrPipe()
		if err := cmd.Start(); err != nil {
			pl.err = fmt.Errorf("启动 %s: %w", exe, err)
			return
		}
		go func() {
			sc := bufio.NewScanner(stderr)
			for sc.Scan() {
				fmt.Fprintf(os.Stderr, "[%s stderr] %s\n", pl.m.ID, sc.Text())
			}
		}()
		pl.conn = sdk.NewConn(stdout, stdin, func(ctx context.Context, method string, params json.RawMessage) (any, error) {
			return p.hostHandle(ctx, pl, method, params)
		})
		go pl.conn.Run()
		dataDir := filepath.Join(p.dataDir, "plugin-data", pl.m.ID)
		cacheDir := filepath.Join(p.dataDir, "cache", pl.m.ID)
		_ = os.MkdirAll(dataDir, 0o755)
		_ = os.MkdirAll(cacheDir, 0o755)
		var r sdk.InitializeResult
		pl.err = pl.conn.Call(context.Background(), sdk.MethodInitialize, &sdk.InitializeParams{
			ProtocolVersion: sdk.ProtocolVersion, HostVersion: "0.1.0-probe", Locale: p.locale, OS: runtime.GOOS, Arch: runtime.GOARCH,
			PluginDir: pl.dir, DataDir: dataDir, CacheDir: cacheDir, HostDataDir: p.dataDir, Mirrors: p.mirrors,
		}, &r)
	})
	return pl.err
}

func (p *probe) call(ctx context.Context, pl *plugin, method string, params, result any) error {
	if pl == nil {
		return fmt.Errorf("plugin not loaded")
	}
	if err := p.start(pl); err != nil {
		return err
	}
	return pl.conn.Call(ctx, method, params, result)
}

func (p *probe) shutdown() {
	for _, pl := range p.plugins {
		if pl.conn != nil {
			ctx, cancel := context.WithTimeout(context.Background(), time.Second)
			_ = pl.conn.Call(ctx, sdk.MethodShutdown, nil, nil)
			cancel()
			pl.conn.Close()
		}
	}
}

func (p *probe) hostHandle(ctx context.Context, from *plugin, method string, raw json.RawMessage) (any, error) {
	switch method {
	case sdk.HostLog:
		var l sdk.LogParams
		_ = json.Unmarshal(raw, &l)
		if p.verbose || l.Level == "error" || l.Level == "warn" {
			fmt.Fprintf(os.Stderr, "[%s %s] %s\n", from.m.ID, l.Level, l.Message)
		}
		return nil, nil
	case sdk.HostConsole:
		var c sdk.ConsoleParams
		_ = json.Unmarshal(raw, &c)
		fmt.Printf("[%s] %s\n", c.Console, c.Text)
		return nil, nil
	case sdk.HostProgress:
		var pr sdk.ProgressParams
		_ = json.Unmarshal(raw, &pr)
		fmt.Printf("[进度 %s] %.0f%% %s\n", pr.Token, pr.Percent, pr.Message)
		return nil, nil
	case sdk.HostNotify:
		var n sdk.NotifyParams
		_ = json.Unmarshal(raw, &n)
		fmt.Printf("[通知 %s] %s %s\n", n.Level, n.Title, n.Message)
		return nil, nil
	case sdk.HostToolchains:
		var q sdk.HostToolchainsParams
		_ = json.Unmarshal(raw, &q)
		return &sdk.HostToolchainsResult{Toolchains: p.toolchains(ctx, q.Kind)}, nil
	case sdk.HostCall:
		var q sdk.HostCallParams
		_ = json.Unmarshal(raw, &q)
		target := p.byID[q.Plugin]
		if target == nil {
			return nil, sdk.NewError(sdk.ErrCodeNotSupported, "plugin %s not loaded", q.Plugin)
		}
		var r json.RawMessage
		if err := p.call(ctx, target, sdk.MethodServiceCall, &sdk.ServiceCallParams{Method: q.Method, Params: q.Params, Caller: from.m.ID}, &r); err != nil {
			return nil, err
		}
		return r, nil
	case sdk.HostDownload:
		var d sdk.DownloadParams
		_ = json.Unmarshal(raw, &d)
		file := d.Dest
		if d.Extract != "" {
			file = filepath.Join(p.dataDir, "cache", "dl-"+sdk.StableID(d.URLs...)+"."+d.Extract)
		}
		u, err := archive.Download(ctx, d.URLs, file, d.SHA256, nil)
		if err != nil {
			return nil, err
		}
		if d.Extract != "" {
			if err := archive.Extract(file, d.Dest, d.Extract, d.StripComponents); err != nil {
				return nil, err
			}
		}
		return &sdk.DownloadResult{Path: d.Dest, URL: u}, nil
	case sdk.HostExec:
		var e sdk.ExecParams
		if err := json.Unmarshal(raw, &e); err != nil {
			return nil, err
		}
		return p.exec(ctx, &e), nil
	}
	return nil, sdk.NewError(sdk.ErrCodeMethodNotFound, "%s", method)
}

func (p *probe) exec(ctx context.Context, e *sdk.ExecParams) *sdk.ExecResult {
	start := time.Now()
	if e.TimeoutMs > 0 {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, time.Duration(e.TimeoutMs)*time.Millisecond)
		defer cancel()
	}
	env := buildEnv(sdk.MergeEnv(p.mirrorEnv(), e.Env), e.PathPrepend)
	cmd := exec.Command(lookPath(e.Command, env), e.Args...)
	cmd.Dir = e.Cwd
	cmd.Env = env
	setPgid(cmd)
	if e.Stdin != "" {
		cmd.Stdin = strings.NewReader(e.Stdin)
	}
	var outBuf, errBuf limitedBuf
	if e.Console != "" {
		fmt.Printf("[%s] $ %s %s\n", e.Console, e.Command, strings.Join(e.Args, " "))
		pr, pw := io.Pipe()
		cmd.Stdout = io.MultiWriter(pw, &outBuf)
		cmd.Stderr = io.MultiWriter(pw, &errBuf)
		go func() {
			sc := bufio.NewScanner(pr)
			sc.Buffer(make([]byte, 1024*1024), 1024*1024)
			for sc.Scan() {
				fmt.Printf("[%s] %s\n", e.Console, sc.Text())
			}
		}()
		defer pw.Close()
	} else {
		cmd.Stdout = &outBuf
		cmd.Stderr = &errBuf
	}
	res := &sdk.ExecResult{}
	if err := cmd.Start(); err != nil {
		res.ExitCode = 127
		res.Stderr = err.Error()
		return res
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	select {
	case err := <-done:
		res.ExitCode = exitCode(err)
	case <-ctx.Done():
		killGroup(cmd)
		<-done
		res.ExitCode = -1
		if ctx.Err() == context.DeadlineExceeded {
			res.TimedOut = true
		} else {
			res.Cancelled = true
		}
	}
	if e.Capture {
		res.Stdout = outBuf.String()
		res.Stderr = errBuf.String()
	} else if res.ExitCode != 0 {
		res.Stderr = tail(errBuf.String(), 4000)
	}
	res.DurationMs = time.Since(start).Milliseconds()
	return res
}

func (p *probe) execStream(ctx context.Context, command string, args []string, cwd string, env map[string]string, pathPrepend []string, console string) int {
	r := p.exec(ctx, &sdk.ExecParams{Command: command, Args: args, Cwd: cwd, Env: env, PathPrepend: pathPrepend, Console: console})
	return r.ExitCode
}

type limitedBuf struct {
	mu sync.Mutex
	b  []byte
}

func (l *limitedBuf) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	if len(l.b) < 4<<20 {
		l.b = append(l.b, p...)
	}
	return len(p), nil
}

func (l *limitedBuf) String() string {
	l.mu.Lock()
	defer l.mu.Unlock()
	return string(l.b)
}

// ---------------------------------------------------------------------------
// 杂项

func (p *probe) mirrorEnv() map[string]string {
	m := p.mirrors
	env := map[string]string{}
	if m.NpmRegistry != "" {
		env["npm_config_registry"] = m.NpmRegistry
		env["YARN_REGISTRY"] = m.NpmRegistry
		env["YARN_NPM_REGISTRY_SERVER"] = m.NpmRegistry
		env["NPM_CONFIG_REGISTRY"] = m.NpmRegistry
		env["COREPACK_NPM_REGISTRY"] = m.NpmRegistry
	}
	if m.NodeDist != "" {
		env["NODEJS_ORG_MIRROR"] = m.NodeDist
		env["NVM_NODEJS_ORG_MIRROR"] = m.NodeDist
	}
	if m.PypiIndex != "" {
		env["PIP_INDEX_URL"] = m.PypiIndex
		env["UV_DEFAULT_INDEX"] = m.PypiIndex
		env["UV_INDEX_URL"] = m.PypiIndex
	}
	if m.PythonDist != "" {
		env["UV_PYTHON_INSTALL_MIRROR"] = m.PythonDist
	}
	if m.GoProxy != "" {
		env["GOPROXY"] = m.GoProxy
	}
	if m.GoSumDB != "" {
		env["GOSUMDB"] = m.GoSumDB
	}
	return env
}

func chinaMirrors() sdk.Mirrors {
	return sdk.Mirrors{
		Region:             "china",
		NpmRegistry:        "https://registry.npmmirror.com",
		NodeDist:           "https://cdn.npmmirror.com/binaries/node",
		MavenCentral:       "https://maven.aliyun.com/repository/public",
		GradlePluginPortal: "https://maven.aliyun.com/repository/gradle-plugin",
		GradleDist:         "https://mirrors.huaweicloud.com/gradle",
		GoogleMaven:        "https://maven.aliyun.com/repository/google",
		PypiIndex:          "https://mirrors.aliyun.com/pypi/simple/",
		PythonDist:         "https://registry.npmmirror.com/-/binary/python-build-standalone",
		GoProxy:            "https://goproxy.cn,direct",
		GoSumDB:            "sum.golang.google.cn",
		GoDist:             "https://mirrors.aliyun.com/golang",
		JdkAdoptium:        "https://mirrors.tuna.tsinghua.edu.cn/Adoptium",
	}
}

func buildEnv(overlay map[string]string, pathPrepend []string) []string {
	env := map[string]string{}
	for _, kv := range os.Environ() {
		if k, v, ok := strings.Cut(kv, "="); ok {
			env[k] = v
		}
	}
	for k, v := range overlay {
		env[k] = v
	}
	if len(pathPrepend) > 0 {
		env["PATH"] = strings.Join(pathPrepend, string(os.PathListSeparator)) + string(os.PathListSeparator) + env["PATH"]
	}
	out := make([]string, 0, len(env))
	for k, v := range env {
		out = append(out, k+"="+v)
	}
	return out
}

func lookPath(cmd string, env []string) string {
	if filepath.IsAbs(cmd) || strings.Contains(cmd, string(os.PathSeparator)) {
		return cmd
	}
	for _, kv := range env {
		if strings.HasPrefix(kv, "PATH=") {
			for _, d := range filepath.SplitList(kv[5:]) {
				c := filepath.Join(d, cmd)
				if st, err := os.Stat(c); err == nil && !st.IsDir() {
					return c
				}
				if runtime.GOOS == "windows" {
					for _, ext := range []string{".exe", ".cmd", ".bat"} {
						if st, err := os.Stat(c + ext); err == nil && !st.IsDir() {
							return c + ext
						}
					}
				}
			}
		}
	}
	return cmd
}

func toolchainEnvAll(sel map[string]sdk.Toolchain) (map[string]string, []string) {
	var tcs []sdk.Toolchain
	for _, t := range sel {
		tcs = append(tcs, t)
	}
	return sdk.ToolchainEnv(tcs...)
}

func exitCode(err error) int {
	if err == nil {
		return 0
	}
	if ee, ok := err.(*exec.ExitError); ok {
		return ee.ExitCode()
	}
	return 1
}

func tail(s string, n int) string {
	if len(s) > n {
		return s[len(s)-n:]
	}
	return s
}

func topoSort(pls []*plugin, byID map[string]*plugin) []*plugin {
	var out []*plugin
	seen := map[string]int{}
	var visit func(pl *plugin)
	visit = func(pl *plugin) {
		if seen[pl.m.ID] != 0 {
			return
		}
		seen[pl.m.ID] = 1
		for _, d := range pl.m.Dependencies {
			if dep := byID[d.ID]; dep != nil {
				visit(dep)
			}
		}
		seen[pl.m.ID] = 2
		out = append(out, pl)
	}
	for _, pl := range pls {
		visit(pl)
	}
	return out
}

var ideaOptionRe = regexp.MustCompile(`<option name="([^"]+)" value="([^"]*)"`)

// readIdeaConfigs 粗读 .idea/workspace.xml 与 .run/*.run.xml 的运行配置（探针用简化版）。
func readIdeaConfigs(root string) []sdk.IdeRunConfig {
	var out []sdk.IdeRunConfig
	files := []string{filepath.Join(root, ".idea", "workspace.xml")}
	if m, _ := filepath.Glob(filepath.Join(root, ".run", "*.run.xml")); m != nil {
		files = append(files, m...)
	}
	cfgRe := regexp.MustCompile(`(?s)<configuration ([^>]*)>(.*?)</configuration>`)
	attrRe := regexp.MustCompile(`(\w+)="([^"]*)"`)
	modRe := regexp.MustCompile(`<module name="([^"]+)"`)
	for _, f := range files {
		data := sdk.ReadText(f, 0)
		for _, m := range cfgRe.FindAllStringSubmatch(data, -1) {
			attrs := map[string]string{}
			for _, a := range attrRe.FindAllStringSubmatch(m[1], -1) {
				attrs[a[1]] = a[2]
			}
			if attrs["temporary"] == "true" {
				continue
			}
			c := sdk.IdeRunConfig{Source: "idea", Type: attrs["type"], Name: attrs["name"], Options: map[string]string{}}
			for _, o := range ideaOptionRe.FindAllStringSubmatch(m[2], -1) {
				c.Options[o[1]] = o[2]
			}
			if mm := modRe.FindStringSubmatch(m[2]); mm != nil {
				c.Module = mm[1]
			}
			out = append(out, c)
		}
	}
	return out
}

func projectInfo(root string) sdk.ProjectInfo {
	abs, _ := filepath.Abs(root)
	return sdk.ProjectInfo{ID: sdk.StableID(abs), Name: filepath.Base(abs), Root: abs}
}

func pickModule(mods []*module, rel string) *module {
	if len(mods) == 0 {
		fatal("没有识别到模块")
	}
	for _, m := range mods {
		if m.info.RelDir == rel {
			return m
		}
	}
	if rel != "" {
		fatal("没有模块 %s", rel)
	}
	return mods[0]
}

func fileName(plan *sdk.InstallPlan) string {
	if plan.FileName != "" {
		return plan.FileName
	}
	if len(plan.URLs) > 0 {
		return filepath.Base(plan.URLs[0])
	}
	return "download"
}

func contains(xs []string, s string) bool {
	for _, x := range xs {
		if x == s {
			return true
		}
	}
	return false
}

func orDot(s string) string {
	if s == "" {
		return "."
	}
	return s
}

func arg(xs []string, i int) string {
	if i < len(xs) {
		return xs[i]
	}
	return ""
}

func need(xs []string, n int) {
	if len(xs) < n {
		fatal("参数不足")
	}
}

func check(err error) {
	if err != nil {
		fatal("%v", err)
	}
}

func printJSON(v any) {
	b, _ := json.MarshalIndent(v, "", "  ")
	fmt.Println(string(b))
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, format+"\n", args...)
	os.Exit(1)
}

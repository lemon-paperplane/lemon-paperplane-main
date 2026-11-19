package main

import (
	"errors"
	"flag"
	"fmt"
	"go/format"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

const sdkModule = "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"

type pluginsOptions struct {
	paths
	out   string
	goos  string
	archs []string
	link  string // 插件 bin 下链接指向多入口程序的相对路径
	quiet bool
}

// cmdPlugins：把插件仓库编进一个多入口程序 paperplane-plugins（按程序名分发），并整理插件目录。
//
//	<out>/paperplane-plugins[.exe]   多个架构时（仅 macOS）用 lipo 合成通用二进制
//	<out>/plugins/<id>/…             plugin.json、icons、l10n…，bin/ 下是指向多入口程序的链接
//	                                 （Windows 没有符号链接：写 <程序名>.bundled，内容是相对路径）
func cmdPlugins(args []string) error {
	var o pluginsOptions
	var archs string
	fs := flag.NewFlagSet("plugins", flag.ExitOnError)
	o.register(fs)
	fs.StringVar(&o.out, "out", "", "输出目录")
	fs.StringVar(&o.goos, "os", "", "目标系统（GOOS）")
	fs.StringVar(&archs, "arch", "", "目标架构（GOARCH，多个用逗号分隔）")
	fs.StringVar(&o.link, "link", "../../../paperplane-plugins", "插件 bin 下链接指向的相对路径")
	_ = fs.Parse(args)
	if o.out == "" || o.goos == "" || archs == "" {
		return errors.New("plugins 需要 -out、-os、-arch")
	}
	o.archs = splitList(archs)
	if err := o.resolve(); err != nil {
		return err
	}
	return buildPlugins(o)
}

func buildPlugins(o pluginsOptions) error {
	out, err := filepath.Abs(o.out)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(out, 0o755); err != nil {
		return err
	}
	gen := filepath.Join(o.core(), "bundled-plugins")
	if err := os.MkdirAll(gen, 0o755); err != nil {
		return err
	}

	skip := map[string]bool{}
	for _, id := range o.storeOnly() {
		skip[id] = true
	}

	type entry struct {
		dir string
		m   *manifest
	}
	var entries []entry
	sdkRel, err := relSlash(gen, o.sdk())
	if err != nil {
		return err
	}
	var gomod, imports, table strings.Builder
	gomod.WriteString("module github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/bundled-plugins\n\ngo 1.23\n\n")
	fmt.Fprintf(&gomod, "require %s v0.0.0\nreplace %s => %s\n", sdkModule, sdkModule, sdkRel)
	n := 0
	for _, dir := range o.pluginDirs() {
		m, err := readManifest(dir)
		if err != nil {
			return err
		}
		if skip[m.ID] || !m.supports(o.goos) {
			continue
		}
		if m.Executable == "" { // 纯声明插件：只整理文件，不进多入口程序
			entries = append(entries, entry{dir, m})
			continue
		}
		if !isDir(filepath.Join(dir, "plugin")) {
			continue
		}
		mod, err := moduleName(dir)
		if err != nil {
			return err
		}
		rel, err := relSlash(gen, dir)
		if err != nil {
			return err
		}
		fmt.Fprintf(&gomod, "require %s v0.0.0\nreplace %s => %s\n", mod, mod, rel)
		alias := fmt.Sprintf("p%d", n)
		fmt.Fprintf(&imports, "\t%s %q\n", alias, mod+"/plugin")
		fmt.Fprintf(&table, "\t%q: func() any { return %s.New() },\n", m.ID, alias)
		entries = append(entries, entry{dir, m})
		n++
	}
	if len(entries) == 0 {
		return fmt.Errorf("%s 下没有插件仓库（%s*）", o.root, pluginPrefix)
	}
	if err := os.WriteFile(filepath.Join(gen, "go.mod"), []byte(gomod.String()), 0o644); err != nil {
		return err
	}
	src := `// 由 scripts/ppbuild（scripts/build-plugins.sh）生成，不要手改。
// 内置插件多入口程序：通过名为 paperplane-plugin-<id> 的链接启动时运行对应插件（每个插件仍是独立进程）。
package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	sdk "` + sdkModule + `"
` + imports.String() + `)

var plugins = map[string]func() any{
` + table.String() + `}

func main() {
	name := strings.TrimSuffix(filepath.Base(os.Args[0]), ".exe")
	id := strings.TrimPrefix(name, "paperplane-plugin-")
	if id == name && len(os.Args) > 1 {
		id = os.Args[1]
	}
	f := plugins[id]
	if f == nil {
		fmt.Fprintln(os.Stderr, "unknown plugin:", id)
		os.Exit(2)
	}
	sdk.Serve(f())
}
`
	formatted, err := format.Source([]byte(src))
	if err != nil {
		return fmt.Errorf("生成的 main.go 有误：%w", err)
	}
	if err := os.WriteFile(filepath.Join(gen, "main.go"), formatted, 0o644); err != nil {
		return err
	}
	tidy := exec.Command("go", "mod", "tidy")
	tidy.Dir = gen
	tidy.Env = append(os.Environ(), "GOFLAGS=-mod=mod")
	if b, err := tidy.CombinedOutput(); err != nil {
		fmt.Fprintf(os.Stderr, "warning: go mod tidy（bundled-plugins）：%v\n%s", err, b)
	}

	// 编译
	ext := exeSuffix(o.goos)
	final := filepath.Join(out, "paperplane-plugins"+ext)
	var bins []string
	for _, arch := range o.archs {
		b := filepath.Join(out, fmt.Sprintf(".paperplane-plugins-%s-%s%s", o.goos, arch, ext))
		if err := goBuild(gen, o.goos, arch, b, "", "."); err != nil {
			return err
		}
		bins = append(bins, b)
	}
	if len(bins) > 1 {
		if o.goos != "darwin" {
			return fmt.Errorf("只有 macOS 能把多个架构合成一个程序（%s）", o.goos)
		}
		lipo := exec.Command("lipo", append([]string{"-create", "-output", final}, bins...)...)
		lipo.Stderr = os.Stderr
		if err := lipo.Run(); err != nil {
			return fmt.Errorf("lipo：%w", err)
		}
		for _, b := range bins {
			_ = os.Remove(b)
		}
	} else {
		_ = os.Remove(final)
		if err := os.Rename(bins[0], final); err != nil {
			return err
		}
	}

	// 整理插件目录
	pdir := filepath.Join(out, "plugins")
	if err := os.RemoveAll(pdir); err != nil {
		return err
	}
	for _, e := range entries {
		dst := filepath.Join(pdir, e.m.ID)
		if err := copyPluginFiles(e.dir, dst); err != nil {
			return err
		}
		exe := e.m.exeName()
		if exe == "" {
			continue
		}
		bin := filepath.Join(dst, "bin")
		if err := os.MkdirAll(bin, 0o755); err != nil {
			return err
		}
		if o.goos == "windows" {
			if err := os.WriteFile(filepath.Join(bin, exe+".bundled"), []byte(o.link+".exe"), 0o644); err != nil {
				return err
			}
		} else if err := os.Symlink(o.link, filepath.Join(bin, exe)); err != nil {
			return err
		}
	}
	if !o.quiet {
		fmt.Printf("bundled %d plugins → %s\n", len(entries), out)
	}
	return nil
}

func moduleName(dir string) (string, error) {
	b, err := os.ReadFile(filepath.Join(dir, "go.mod"))
	if err != nil {
		return "", err
	}
	for _, l := range strings.Split(string(b), "\n") {
		if f := strings.Fields(l); len(f) >= 2 && f[0] == "module" {
			return f[1], nil
		}
	}
	return "", fmt.Errorf("%s/go.mod 没有 module", dir)
}

package main

import (
	"errors"
	"flag"
	"fmt"
	"go/format"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
)

// 默认架构：Go 支持、creack/pty 也支持的 Linux 架构
const defaultHeadlessArchs = "amd64,arm64,arm/7,arm/6,386,riscv64,loong64,ppc64le,s390x,mips64le"

const coreModule = "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core"

// cmdHeadless：Linux 无头版——内核 + 全部内置插件编进一个可执行文件，插件静态文件与离线商店嵌在里面。
//
//	<out>/paperplane-<版本>-linux-<架构>   每个架构一个文件
//	<out>/SHA256SUMS
func cmdHeadless(args []string) error {
	var p paths
	var out, version, archs string
	fs := flag.NewFlagSet("headless", flag.ExitOnError)
	p.register(fs)
	fs.StringVar(&out, "out", "", "输出目录")
	fs.StringVar(&version, "version", "0.1.0", "版本号")
	fs.StringVar(&archs, "arch", defaultHeadlessArchs, "架构（逗号分隔；arm/7 表示 GOARM=7）")
	_ = fs.Parse(args)
	if out == "" {
		return errors.New("headless 需要 -out")
	}
	if err := p.resolve(); err != nil {
		return err
	}
	out, err := filepath.Abs(out)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(out, 0o755); err != nil {
		return err
	}
	gen := filepath.Join(p.core(), "headless-gen")
	if err := os.RemoveAll(gen); err != nil {
		return err
	}
	emb := filepath.Join(gen, "embedded")
	if err := os.MkdirAll(filepath.Join(emb, "plugins"), 0o755); err != nil {
		return err
	}

	// 1. 插件目录（bin 下写 .bundled = @self）与入口程序的插件表
	skip := map[string]bool{}
	for _, id := range p.storeOnly() {
		skip[id] = true
	}
	coreRel, _ := relSlash(gen, p.core())
	sdkRel, _ := relSlash(gen, p.sdk())
	var gomod, imports, table strings.Builder
	gomod.WriteString("module " + coreModule + "/headless-gen\n\ngo 1.23\n\n")
	fmt.Fprintf(&gomod, "require %s v0.0.0\nreplace %s => %s\n", coreModule, coreModule, coreRel)
	fmt.Fprintf(&gomod, "require %s v0.0.0\nreplace %s => %s\n", sdkModule, sdkModule, sdkRel)
	n, count := 0, 0
	for _, dir := range p.pluginDirs() {
		m, err := readManifest(dir)
		if err != nil {
			return err
		}
		if skip[m.ID] || !m.supports("linux") {
			continue
		}
		dst := filepath.Join(emb, "plugins", m.ID)
		if m.Executable != "" {
			if !isDir(filepath.Join(dir, "plugin")) {
				continue
			}
			mod, err := moduleName(dir)
			if err != nil {
				return err
			}
			rel, _ := relSlash(gen, dir)
			fmt.Fprintf(&gomod, "require %s v0.0.0\nreplace %s => %s\n", mod, mod, rel)
			alias := fmt.Sprintf("p%d", n)
			fmt.Fprintf(&imports, "\t%s %q\n", alias, mod+"/plugin")
			fmt.Fprintf(&table, "\t%q: func() any { return %s.New() },\n", m.ID, alias)
			n++
		}
		if err := copyPluginFiles(dir, dst); err != nil {
			return err
		}
		if exe := m.exeName(); exe != "" {
			if err := os.MkdirAll(filepath.Join(dst, "bin"), 0o755); err != nil {
				return err
			}
			if err := os.WriteFile(filepath.Join(dst, "bin", exe+".bundled"), []byte("@self"), 0o644); err != nil {
				return err
			}
		}
		count++
	}
	if count == 0 {
		return fmt.Errorf("%s 下没有插件仓库（%s*）", p.root, pluginPrefix)
	}
	if err := os.WriteFile(filepath.Join(gen, "go.mod"), []byte(gomod.String()), 0o644); err != nil {
		return err
	}
	src := `// 由 scripts/ppbuild headless 生成，不要手改。
// 柠檬纸飞机 Linux 无头版：内核 + 全部内置插件在一个可执行文件里；插件仍是独立进程（` + "`<自己> __plugin <id>`" + `）。
package main

import (
	"embed"
	"fmt"
	"io/fs"
	"os"

	"` + coreModule + `/app"
	sdk "` + sdkModule + `"
` + imports.String() + `)

//go:embed all:embedded
var embedded embed.FS

// Version 由构建时 -ldflags "-X main.Version=…" 设置。
var Version = "0.1.0"

var plugins = map[string]func() any{
` + table.String() + `}

func main() {
	if len(os.Args) > 2 && os.Args[1] == "__plugin" {
		f := plugins[os.Args[2]]
		if f == nil {
			fmt.Fprintln(os.Stderr, "unknown plugin:", os.Args[2])
			os.Exit(2)
		}
		sdk.Serve(f())
		return
	}
	sub, err := fs.Sub(embedded, "embedded")
	if err != nil {
		panic(err)
	}
	app.Version = Version
	app.Embedded = sub
	app.Main()
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
		return fmt.Errorf("go mod tidy（headless-gen）：%v\n%s", err, b)
	}

	// 2. 每个架构：离线商店（按这个架构打包）→ 编译
	var built []string
	for _, a := range splitList(archs) {
		goarch, goarm, _ := strings.Cut(a, "/")
		label := goarch
		if goarch == "arm" {
			if goarm == "" {
				goarm = "7"
			}
			label = "armv" + goarm
		}
		storeDir := filepath.Join(emb, "store")
		_ = os.RemoveAll(storeDir)
		if len(p.storeOnly()) > 0 {
			if err := buildStore(storeOptions{paths: p, out: storeDir, base: ".", platforms: []string{"linux/" + goarch}, quiet: true}); err != nil {
				return err
			}
		}
		name := fmt.Sprintf("paperplane-%s-linux-%s", version, label)
		dst := filepath.Join(out, name)
		cmd := exec.Command("go", "build", "-trimpath", "-ldflags", "-s -w -X main.Version="+version, "-o", dst, ".")
		cmd.Dir = gen
		cmd.Env = append(os.Environ(), "CGO_ENABLED=0", "GOOS=linux", "GOARCH="+goarch)
		if goarm != "" {
			cmd.Env = append(cmd.Env, "GOARM="+goarm)
		}
		cmd.Stdout, cmd.Stderr = os.Stderr, os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("编译 linux/%s 失败：%w", label, err)
		}
		st, _ := os.Stat(dst)
		fmt.Printf("  %-34s %5.1f MB\n", name, float64(st.Size())/(1<<20))
		built = append(built, dst)
	}

	// 3. 校验和
	sort.Strings(built)
	var sums strings.Builder
	for _, f := range built {
		sum, _, err := fileSHA256(f)
		if err != nil {
			return err
		}
		fmt.Fprintf(&sums, "%s  %s\n", sum, filepath.Base(f))
	}
	if err := os.WriteFile(filepath.Join(out, "SHA256SUMS"), []byte(sums.String()), 0o644); err != nil {
		return err
	}
	fmt.Printf("linux 无头版：%d 个架构，内置插件 %d 个 → %s\n", len(built), count, out)
	return nil
}

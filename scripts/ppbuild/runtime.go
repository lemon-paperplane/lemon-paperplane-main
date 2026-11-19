package main

import (
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
)

// cmdRuntime：Windows / Linux 安装目录里内核相关的部分。
//
//	<out>/paperplane[.exe]            内核 + 命令行
//	<out>/paperplane-plugins[.exe]    内置插件多入口程序
//	<out>/plugins/<id>/…              插件目录（bin 下的链接 / .bundled 指向 ../../../paperplane-plugins）
//	<out>/store/…                     随 App 附带的离线商店（scripts/store-only.txt 里的插件）
func cmdRuntime(args []string) error {
	var p paths
	var out, goos, goarch, version string
	fs := flag.NewFlagSet("runtime", flag.ExitOnError)
	p.register(fs)
	fs.StringVar(&out, "out", "", "输出目录")
	fs.StringVar(&goos, "os", "", "目标系统（windows / linux）")
	fs.StringVar(&goarch, "arch", "", "目标架构（amd64 / arm64）")
	fs.StringVar(&version, "version", "0.1.0", "版本号")
	_ = fs.Parse(args)
	if out == "" || goos == "" || goarch == "" {
		return errors.New("runtime 需要 -out、-os、-arch")
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
	ext := exeSuffix(goos)

	if err := goBuild(p.core(), goos, goarch, filepath.Join(out, "paperplane"+ext),
		"-s -w -X main.Version="+version, "./cmd/paperplane"); err != nil {
		return err
	}

	tmp, err := os.MkdirTemp("", "ppbuild-runtime-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(tmp)
	if err := buildPlugins(pluginsOptions{paths: p, out: tmp, goos: goos, archs: []string{goarch}, link: "../../../paperplane-plugins", quiet: true}); err != nil {
		return err
	}
	if err := copyFile(filepath.Join(tmp, "paperplane-plugins"+ext), filepath.Join(out, "paperplane-plugins"+ext)); err != nil {
		return err
	}
	if err := os.RemoveAll(filepath.Join(out, "plugins")); err != nil {
		return err
	}
	if err := copyTree(filepath.Join(tmp, "plugins"), filepath.Join(out, "plugins")); err != nil {
		return err
	}

	if len(p.storeOnly()) > 0 {
		if err := os.RemoveAll(filepath.Join(out, "store")); err != nil {
			return err
		}
		if err := buildStore(storeOptions{paths: p, out: filepath.Join(out, "store"), base: ".", platforms: []string{goos + "/" + goarch}, quiet: true}); err != nil {
			return err
		}
	}
	entries, _ := os.ReadDir(filepath.Join(out, "plugins"))
	fmt.Printf("%s/%s：paperplane%s + %d 个内置插件 → %s\n", goos, goarch, ext, len(entries), out)
	return nil
}

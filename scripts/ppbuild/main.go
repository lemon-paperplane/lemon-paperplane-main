// ppbuild：柠檬纸飞机的构建工具，只用标准库，Mac / Windows / Linux 上都能直接 go run。
//
//	plugins        编译内置插件多入口程序并整理插件目录（scripts/build-plugins.sh 调用）
//	store          为插件商店打各平台安装包与索引（scripts/build-store.sh 调用）
//	runtime        内核 + 内置插件 + 离线商店，即 Windows / Linux 安装目录里内核相关的部分
//	fetch-plugins  旁边没有插件源码时，从 GitHub 下载全部插件仓库
//	headless       Linux 无头版：内核 + 全部内置插件编成一个文件（多架构）
//
// 用 go -C <本目录> run . <命令> 调用时当前目录会变成本目录，路径参数一律给绝对路径。
package main

import (
	"fmt"
	"os"
)

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	var err error
	switch os.Args[1] {
	case "plugins":
		err = cmdPlugins(os.Args[2:])
	case "store":
		err = cmdStore(os.Args[2:])
	case "runtime":
		err = cmdRuntime(os.Args[2:])
	case "fetch-plugins":
		err = cmdFetchPlugins(os.Args[2:])
	case "headless":
		err = cmdHeadless(os.Args[2:])
	default:
		usage()
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "ppbuild:", err)
		os.Exit(1)
	}
}

func usage() {
	fmt.Fprintln(os.Stderr, `用法：ppbuild <命令> [参数]
  plugins       -out <目录> -os <GOOS> -arch <a[,b]> [-link <相对路径>]
  store         -out <目录> -base <下载根地址|.> [-platforms "os/arch ..."] [插件目录...]
  runtime       -out <目录> -os <GOOS> -arch <GOARCH> [-version <版本>]
  fetch-plugins [-org lemon-paperplane]
  headless      -out <目录> [-version 0.1.0] [-arch amd64,arm64,arm/7,…]
公共参数：-main <lemon-paperplane-main 目录>  -root <插件仓库所在目录，默认 main 的上一级>`)
}

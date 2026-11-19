// paperplane：柠檬纸飞机内核与命令行（实现在 app 包，Linux 无头版共用）。
package main

import "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/app"

// Version 由构建脚本设置（-ldflags "-X main.Version=…"）。
var Version = "0.1.0"

func main() {
	app.Version = Version
	app.Main()
}

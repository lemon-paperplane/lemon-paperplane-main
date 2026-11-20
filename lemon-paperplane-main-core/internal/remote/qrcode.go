package remote

import (
	"fmt"
	"strings"

	"rsc.io/qr"
)

// 连接码的二维码：手机客户端「扫码添加服务器」用。内容就是连接码本身（ppc1_…）。

// QRSVG 返回二维码 SVG（黑色模块、白底、四格静区，viewBox 按模块数，界面按需缩放）。
func QRSVG(text string) (string, error) {
	c, err := qr.Encode(text, qr.L)
	if err != nil {
		return "", err
	}
	const quiet = 4
	n := c.Size + 2*quiet
	var path strings.Builder
	for y := 0; y < c.Size; y++ {
		for x := 0; x < c.Size; x++ {
			if !c.Black(x, y) {
				continue
			}
			// 一行里连续的黑块合成一段，路径短很多
			run := 1
			for x+run < c.Size && c.Black(x+run, y) {
				run++
			}
			fmt.Fprintf(&path, "M%d %dh%dv1h-%dz", x+quiet, y+quiet, run, run)
			x += run - 1
		}
	}
	return fmt.Sprintf(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" shape-rendering="crispEdges"><rect width="%d" height="%d" fill="#ffffff"/><path fill="#000000" d="%s"/></svg>`,
		n, n, n, n, path.String()), nil
}

// QRTerminal 用半格字符把二维码画进终端（一个字符 = 上下两个模块），明暗都用显式颜色，深浅色终端都能扫。
func QRTerminal(text string) (string, error) {
	c, err := qr.Encode(text, qr.L)
	if err != nil {
		return "", err
	}
	const quiet = 2
	black := func(x, y int) bool {
		x, y = x-quiet, y-quiet
		return x >= 0 && y >= 0 && x < c.Size && y < c.Size && c.Black(x, y)
	}
	n := c.Size + 2*quiet
	var b strings.Builder
	for y := 0; y < n; y += 2 {
		for x := 0; x < n; x++ {
			top, bottom := black(x, y), y+1 < n && black(x, y+1)
			fg, bg := 97, 107 // 亮白前景 / 亮白背景
			if top {
				fg = 30
			}
			if bottom {
				bg = 40
			}
			fmt.Fprintf(&b, "\x1b[%d;%dm▀", fg, bg)
		}
		b.WriteString("\x1b[0m\n")
	}
	return b.String(), nil
}

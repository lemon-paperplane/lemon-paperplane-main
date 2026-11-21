package app

import (
	"errors"
	"fmt"
	"os"
	"strings"
	"unicode/utf8"

	"golang.org/x/term"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/console"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
)

// 终端里的逐级菜单：方向键选择、回车确认、Esc 返回、Ctrl+C 退出。只在原始模式下读按键，不依赖第三方库。

var errQuit = errors.New("quit")

type menuItem struct {
	key   string
	label string
	hint  string
}

type tui struct {
	fd  int // 标准输入
	out *os.File
}

func newTUI() *tui { return &tui{fd: int(os.Stdin.Fd()), out: os.Stdout} }

const (
	sBold  = "\x1b[1m"
	sDim   = "\x1b[2m"
	sCyan  = "\x1b[36m"
	sGreen = "\x1b[32m"
	sRed   = "\x1b[31m"
	sReset = "\x1b[0m"
)

func dim(s string) string   { return sDim + s + sReset }
func bold(s string) string  { return sBold + s + sReset }
func green(s string) string { return sGreen + s + sReset }
func red(s string) string   { return sRed + s + sReset }

func (t *tui) cols() int {
	if w, _, err := term.GetSize(int(t.out.Fd())); err == nil && w > 0 {
		return w
	}
	return 80
}

// rowsOf：一段已排好的文本在终端里占几行（长行会折行）。
func rowsOf(lines []string, cols int) int {
	n := 0
	for _, l := range lines {
		w := console.DisplayWidth(l)
		if w <= cols {
			n++
		} else {
			n += (w + cols - 1) / cols
		}
	}
	return n
}

func padRight(s string, w int) string {
	if d := w - console.DisplayWidth(s); d > 0 {
		return s + strings.Repeat(" ", d)
	}
	return s
}

// keys 读一次输入并拆成单个按键：转义序列（方向键等）算一个键，其余按字符拆。
// 粘贴、远程终端合并发送时一次能读到好几个键。
func (t *tui) keys() ([]string, error) {
	buf := make([]byte, 512)
	n, err := os.Stdin.Read(buf)
	if err != nil {
		return nil, err
	}
	return splitKeys(string(buf[:n])), nil
}

func splitKeys(s string) []string {
	var out []string
	for len(s) > 0 {
		n := 1
		switch {
		case s[0] == 0x1b && len(s) >= 3 && s[1] == '[':
			n = 2
			for n < len(s) && (s[n] < 0x40 || s[n] > 0x7e) {
				n++
			}
			n = min(n+1, len(s))
		case s[0] == 0x1b && len(s) >= 3 && s[1] == 'O':
			n = 3
		case s[0] != 0x1b:
			_, n = utf8.DecodeRuneInString(s)
		}
		out = append(out, s[:n])
		s = s[n:]
	}
	return out
}

// choose 显示菜单，返回选中项的 key；Esc / q / ← 返回空串（回上一级）；Ctrl+C 返回 errQuit。
func (t *tui) choose(title string, items []menuItem, sel int, top bool) (string, error) {
	if len(items) == 0 {
		return "", nil
	}
	old, err := term.MakeRaw(t.fd)
	if err != nil {
		return "", err
	}
	defer term.Restore(t.fd, old)
	fmt.Fprint(t.out, "\x1b[?25l")
	defer fmt.Fprint(t.out, "\x1b[?25h")
	sel = max(0, min(sel, len(items)-1))
	labelW := 0
	for _, it := range items {
		labelW = max(labelW, console.DisplayWidth(it.label))
	}
	help := i18n.T("cli.keysHelp")
	if top {
		help = i18n.T("cli.keysHelpTop")
	}
	rows := 0
	draw := func() {
		var lines []string
		for _, l := range strings.Split(title, "\n") {
			lines = append(lines, l)
		}
		for i, it := range items {
			num := fmt.Sprintf("%d.", i+1)
			if i >= 9 {
				num = "  "
			}
			label := padRight(it.label, labelW)
			hint := ""
			if it.hint != "" {
				hint = "   " + dim(it.hint)
			}
			if i == sel {
				lines = append(lines, "  "+sCyan+"❯ "+num+" "+sBold+label+sReset+hint)
			} else {
				lines = append(lines, "    "+dim(num)+" "+label+hint)
			}
		}
		lines = append(lines, dim("  "+help))
		var b strings.Builder
		if rows > 1 {
			fmt.Fprintf(&b, "\x1b[%dA", rows-1)
		}
		b.WriteString("\r\x1b[J")
		b.WriteString(strings.Join(lines, "\r\n"))
		fmt.Fprint(t.out, b.String())
		rows = rowsOf(lines, t.cols())
	}
	clear := func() {
		if rows > 1 {
			fmt.Fprintf(t.out, "\x1b[%dA", rows-1)
		}
		fmt.Fprint(t.out, "\r\x1b[J")
	}
	draw()
	pick := func() (string, error) {
		clear()
		fmt.Fprint(t.out, sCyan+"› "+sReset+items[sel].label+"\r\n")
		return items[sel].key, nil
	}
	for {
		ks, err := t.keys()
		if err != nil {
			clear()
			return "", err
		}
		for _, k := range ks {
			switch k {
			case "\x03", "\x04":
				clear()
				return "", errQuit
			case "\r", "\n", "\x1b[C", "\x1bOC":
				return pick()
			case "\x1b[A", "\x1bOA", "k", "\x10":
				sel = (sel - 1 + len(items)) % len(items)
			case "\x1b[B", "\x1bOB", "j", "\x0e", "\t":
				sel = (sel + 1) % len(items)
			case "\x1b[H", "\x1bOH", "\x1b[1~":
				sel = 0
			case "\x1b[F", "\x1bOF", "\x1b[4~":
				sel = len(items) - 1
			case "\x1b", "q", "Q", "\x1b[D", "\x1bOD", "\x7f", "\b":
				if !top || k == "q" || k == "Q" {
					clear()
					return "", nil
				}
			default:
				if len(k) == 1 && k[0] >= '1' && k[0] <= '9' {
					if i := int(k[0] - '1'); i < len(items) {
						sel = i
						return pick()
					}
				}
			}
		}
		draw()
	}
}

// input 读一行文字：def 是直接回车时的默认值；Esc 返回 ok=false。
func (t *tui) input(label, def, hint string) (string, bool, error) {
	if hint != "" {
		fmt.Fprintln(t.out, dim("  "+hint))
	}
	old, err := term.MakeRaw(t.fd)
	if err != nil {
		return "", false, err
	}
	defer term.Restore(t.fd, old)
	var val []rune
	rows := 0
	draw := func(done bool) {
		line := "  " + bold(label) + ": "
		switch {
		case len(val) > 0:
			line += string(val)
		case def != "" && !done:
			line += dim(def)
		case done:
			line += def
		}
		if !done {
			line += "  " + dim(i18n.T("cli.inputHelp"))
		}
		var b strings.Builder
		if rows > 1 {
			fmt.Fprintf(&b, "\x1b[%dA", rows-1)
		}
		b.WriteString("\r\x1b[J" + line)
		fmt.Fprint(t.out, b.String())
		rows = rowsOf([]string{line}, t.cols())
	}
	draw(false)
	done := func(ok bool) (string, bool, error) {
		draw(true)
		fmt.Fprint(t.out, "\r\n")
		if !ok {
			return "", false, nil
		}
		if len(val) == 0 {
			return def, true, nil
		}
		return strings.TrimSpace(string(val)), true, nil
	}
	for {
		ks, err := t.keys()
		if err != nil {
			return "", false, err
		}
		for _, k := range ks {
			switch {
			case k == "\x03" || k == "\x04":
				fmt.Fprint(t.out, "\r\n")
				return "", false, errQuit
			case k == "\x1b":
				return done(false)
			case strings.HasPrefix(k, "\x1b"): // 方向键等：忽略
			case k == "\r" || k == "\n":
				return done(true)
			case k == "\x7f" || k == "\b":
				if len(val) > 0 {
					val = val[:len(val)-1]
				}
			case k == "\x15": // Ctrl+U
				val = val[:0]
			default:
				if r, _ := utf8.DecodeRuneInString(k); r != utf8.RuneError && r >= 0x20 {
					val = append(val, r)
				}
			}
		}
		draw(false)
	}
}

// confirm 是 / 否；默认选中「否」。
func (t *tui) confirm(question string) (bool, error) {
	k, err := t.choose(bold(question), []menuItem{{key: "yes", label: i18n.T("cli.yes")}, {key: "no", label: i18n.T("cli.no")}}, 1, false)
	return k == "yes", err
}

// pause 等用户按回车（或 Esc）再回菜单。
func (t *tui) pause() error {
	fmt.Fprint(t.out, dim("  "+i18n.T("cli.pause")))
	old, err := term.MakeRaw(t.fd)
	if err != nil {
		return err
	}
	defer term.Restore(t.fd, old)
	for {
		ks, err := t.keys()
		if err != nil {
			return err
		}
		for _, k := range ks {
			switch k {
			case "\x03", "\x04":
				fmt.Fprint(t.out, "\r\n")
				return errQuit
			case "\r", "\n", "\x1b", "q", " ":
				fmt.Fprint(t.out, "\r\x1b[K")
				return nil
			}
		}
	}
}

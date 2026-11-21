package console

import (
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf8"

	"golang.org/x/text/width"
)

// 伪终端里运行的程序（docker、gradle、npm…）认为自己在终端里：画进度条、把光标移回去原地重画。
// TermWriter 在内核里模拟一块终端屏幕，把输出还原成两部分交给控制台：
//   - 定型行：已经不会再变的行，照旧带行号、落盘、触发 URL / 就绪 / 诊断识别；
//   - 实时尾部：屏幕上还在被原地改写的行（进度条、当前行），随时整体替换，只推给界面。
//
// 与真终端不同的地方：不按宽度折行（日志里一行就是一行，便于搜索和识别地址）；
// 屏幕上方已经定型的行仍留在屏幕里，程序光标移上去改写时，改写后的内容作为新行再定型一次。

// TermCols、TermRows 是运行进程看到的终端尺寸。宽一些，少让程序把行截断。
const (
	TermCols = 160
	TermRows = 50

	maxTermCols = 4096
)

var (
	// termGrace：光标离开一行后多久定型。给「先换行、下一帧再上移擦掉重画」的程序（log-update、ora 一类）留时间。
	termGrace = 200 * time.Millisecond
	// termQuiet：程序最后一次把光标往上移之后多久才把屏幕上的行定型。进度条还在刷新时不定型，避免中间状态进日志。
	termQuiet = 3 * time.Second
)

const (
	attrBold uint16 = 1 << iota
	attrDim
	attrItalic
	attrUnderline
	attrBlink
	attrInverse
	attrHidden
	attrStrike
)

// sgrStyle 是一格字符的显示样式（输出时重新拼成 SGR 序列，界面照常解析）。
type sgrStyle struct {
	fg, bg string // 空 = 默认；否则是 SGR 参数，如 31、91、38;5;208、38;2;1;2;3
	attr   uint16
}

func (s sgrStyle) params() string {
	var b strings.Builder
	b.WriteString("0")
	for i, code := range []string{"1", "2", "3", "4", "5", "7", "8", "9"} {
		if s.attr&(1<<i) != 0 {
			b.WriteString(";" + code)
		}
	}
	if s.fg != "" {
		b.WriteString(";" + s.fg)
	}
	if s.bg != "" {
		b.WriteString(";" + s.bg)
	}
	return b.String()
}

type tcell struct {
	ch string // "" 表示宽字符的后半格
	st sgrStyle
}

var blankCell = tcell{ch: " "}

type trow struct {
	cells     []tcell
	committed bool      // 当前内容已经作为定型行输出过
	nl        bool      // 光标是换行离开这一行的（空行只有这样才算真实的空行，被擦掉的不算）
	touched   time.Time // 内容最后一次变化
}

func (r *trow) blank() bool {
	for _, c := range r.cells {
		if (c.ch != " " && c.ch != "") || c.st.bg != "" || c.st.attr&attrInverse != 0 {
			return false
		}
	}
	return true
}

// text 把一行拼成带 SGR 的文本：从默认样式开始，有样式的结尾带重置，每行自成一体。
func (r *trow) text() string {
	end := len(r.cells)
	for end > 0 {
		c := r.cells[end-1]
		if (c.ch == " " || c.ch == "") && c.st.bg == "" && c.st.attr&(attrInverse|attrUnderline|attrStrike) == 0 {
			end--
			continue
		}
		break
	}
	var b strings.Builder
	cur := sgrStyle{}
	for _, c := range r.cells[:end] {
		if c.ch == "" {
			continue
		}
		if c.st != cur {
			if c.st == (sgrStyle{}) {
				b.WriteString("\x1b[0m")
			} else {
				b.WriteString("\x1b[" + c.st.params() + "m")
			}
			cur = c.st
		}
		b.WriteString(c.ch)
	}
	if cur != (sgrStyle{}) {
		b.WriteString("\x1b[0m")
	}
	return b.String()
}

const (
	stGround = iota
	stEsc
	stEscSkip
	stCSI
	stString
	stStringEsc
)

type screen struct {
	rows     []*trow
	cr, cc   int
	top, bot int
}

// TermWriter 是一块模拟终端屏幕，接在伪终端主端的读取后面。并发安全。
type TermWriter struct {
	c       *Console
	stream  string
	respond func([]byte) // 回答程序的查询（光标位置、终端类型），写回伪终端

	mu sync.Mutex
	screen
	main  *screen // 备用屏幕（less、vim 一类）期间保存的主屏幕
	st    sgrStyle
	saved struct {
		r, c int
		st   sgrStyle
	}
	lastUp  time.Time
	state   int
	params  []byte
	utf     []byte
	out     []string
	live    []string
	liveSet bool
	timer   *time.Timer
	closed  bool
	now     func() time.Time
}

// Terminal 返回接在这个控制台上的终端模拟。respond 用来回答程序的终端查询，可为空。
func (c *Console) Terminal(respond func([]byte)) *TermWriter {
	w := &TermWriter{c: c, stream: "stdout", respond: respond, now: time.Now}
	w.rows = make([]*trow, TermRows)
	for i := range w.rows {
		w.rows[i] = &trow{}
	}
	w.bot = TermRows - 1
	return w
}

// Write 解析一段伪终端输出。
func (w *TermWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.closed {
		return len(p), nil
	}
	for _, b := range p {
		w.feed(b)
	}
	w.settleLocked(false)
	return len(p), nil
}

// Close 在进程退出、输出读完后调用：屏幕上剩下的内容全部定型，清空实时尾部。
func (w *TermWriter) Close() {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.closed {
		return
	}
	if len(w.utf) > 0 {
		w.utf = w.utf[:0]
		w.put("�", 1)
	}
	if w.main != nil {
		w.screen = *w.main
		w.main = nil
	}
	w.settleLocked(true)
	w.closed = true
	if w.timer != nil {
		w.timer.Stop()
	}
}

func (w *TermWriter) tick() {
	w.mu.Lock()
	defer w.mu.Unlock()
	if !w.closed {
		w.settleLocked(false)
	}
}

// ---------------------------------------------------------------- 定型与实时尾部

func (w *TermWriter) commit(r *trow) {
	w.out = append(w.out, r.text())
	r.committed = true
}

// settleLocked 把可以定型的行交给控制台，并更新实时尾部。final 时全部定型。
func (w *TermWriter) settleLocked(final bool) {
	now := w.now()
	if w.main == nil {
		redraw := now.Sub(w.lastUp) < termQuiet
		var held []*trow
		limit := w.cr
		if final {
			limit = len(w.rows)
		}
		for i := 0; i < limit; i++ {
			r := w.rows[i]
			if r.committed {
				continue
			}
			if !final && (redraw || now.Sub(r.touched) < termGrace) {
				break
			}
			if r.blank() {
				// 空行先攒着，后面有内容定型时才一起定型（末尾的空行和被擦掉的行不进日志）
				if r.nl && i < w.cr {
					held = append(held, r)
				}
				continue
			}
			for _, h := range held {
				w.commit(h)
			}
			held = nil
			w.commit(r)
		}
	}
	live := w.liveLocked()
	if final {
		live = nil
	}
	changed := !w.liveSet || !equalStrings(live, w.live)
	if changed || len(w.out) > 0 {
		out := w.out
		w.out = nil
		w.live = live
		w.liveSet = true
		w.c.update(w.stream, out, live, changed)
	}
	if !final {
		w.scheduleLocked(now)
	}
}

// liveLocked：屏幕上还没定型的行。光标下方只算有内容的行（屏幕空白区不算），被擦掉的空行不算，末尾空行去掉。
func (w *TermWriter) liveLocked() []string {
	var lines []string
	last := 0
	for i, r := range w.rows {
		if r.committed {
			continue
		}
		if r.blank() {
			if i > w.cr || (!r.nl && i != w.cr) {
				continue
			}
			lines = append(lines, "")
			continue
		}
		lines = append(lines, r.text())
		last = len(lines)
	}
	return lines[:last]
}

// scheduleLocked：光标上方还有没定型的行时，到点再检查一次（程序可能一直不再输出）。
func (w *TermWriter) scheduleLocked(now time.Time) {
	if w.main != nil {
		return
	}
	var next time.Time
	if now.Sub(w.lastUp) < termQuiet {
		next = w.lastUp.Add(termQuiet)
	}
	pending := false
	for i := 0; i < w.cr; i++ {
		r := w.rows[i]
		if r.committed || (r.blank() && !r.nl) {
			continue
		}
		pending = true
		if t := r.touched.Add(termGrace); t.After(next) {
			next = t
		}
		break
	}
	if !pending {
		return
	}
	d := next.Sub(now) + time.Millisecond
	if d < time.Millisecond {
		d = time.Millisecond
	}
	if w.timer == nil {
		w.timer = time.AfterFunc(d, w.tick)
	} else {
		w.timer.Reset(d)
	}
}

func equalStrings(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

// ---------------------------------------------------------------- 解析

func (w *TermWriter) feed(b byte) {
	switch w.state {
	case stGround:
		if len(w.utf) > 0 || b >= 0x80 {
			w.feedUTF8(b)
			return
		}
		switch {
		case b == 0x1b:
			w.state = stEsc
		case b < 0x20 || b == 0x7f:
			w.control(b)
		default:
			w.put(string(rune(b)), 1)
		}
	case stEsc:
		w.state = stGround
		w.esc(b)
	case stEscSkip:
		w.state = stGround
	case stCSI:
		switch {
		case b >= 0x40 && b <= 0x7e:
			w.state = stGround
			w.csi(b)
		case b == 0x1b:
			w.state = stEsc
		case b < 0x20:
			w.control(b)
		default:
			if len(w.params) < 64 {
				w.params = append(w.params, b)
			}
		}
	case stString: // OSC / DCS / APC / PM / SOS：内容丢弃（超链接 OSC 8 只留文字）
		switch b {
		case 0x07:
			w.state = stGround
		case 0x1b:
			w.state = stStringEsc
		}
	case stStringEsc:
		w.state = stGround
		if b != '\\' {
			w.esc(b)
		}
	}
}

func (w *TermWriter) feedUTF8(b byte) {
	if b < 0x80 { // 不完整的序列被打断
		w.utf = w.utf[:0]
		w.put("�", 1)
		w.feed(b)
		return
	}
	w.utf = append(w.utf, b)
	if !utf8.FullRune(w.utf) {
		return
	}
	r, n := utf8.DecodeRune(w.utf)
	rest := append([]byte(nil), w.utf[n:]...)
	w.utf = w.utf[:0]
	w.putRune(r)
	for _, x := range rest {
		w.feed(x)
	}
}

func runeWidth(r rune) int {
	if r == 0x200b || r == 0x200c || r == 0x200d || r == 0xfeff || (r >= 0xfe00 && r <= 0xfe0f) ||
		unicode.In(r, unicode.Mn, unicode.Me, unicode.Cf) {
		return 0
	}
	switch width.LookupRune(r).Kind() {
	case width.EastAsianWide, width.EastAsianFullwidth:
		return 2
	}
	return 1
}

func (w *TermWriter) putRune(r rune) {
	if r < 0x20 || (r >= 0x80 && r < 0xa0) { // C1 控制字符不显示
		return
	}
	wd := runeWidth(r)
	if wd == 0 {
		// 组合字符、变体选择符：并到前一格
		row := w.rows[w.cr]
		i := w.cc - 1
		if i >= 0 && i < len(row.cells) && row.cells[i].ch == "" {
			i--
		}
		if i >= 0 && i < len(row.cells) {
			row.cells[i].ch += string(r)
			w.dirty(row)
		}
		return
	}
	w.put(string(r), wd)
}

func (w *TermWriter) dirty(r *trow) {
	r.touched = w.now()
	r.committed = false
}

func ensure(r *trow, n int) {
	for len(r.cells) < n {
		r.cells = append(r.cells, blankCell)
	}
}

func (w *TermWriter) put(ch string, wd int) {
	if w.cc+wd > maxTermCols {
		return
	}
	row := w.rows[w.cr]
	ensure(row, w.cc+wd)
	if row.cells[w.cc].ch == "" && w.cc > 0 { // 覆盖了宽字符的后半格：前半格也清掉
		row.cells[w.cc-1] = blankCell
	}
	row.cells[w.cc] = tcell{ch: ch, st: w.st}
	if wd == 2 {
		row.cells[w.cc+1] = tcell{ch: "", st: w.st}
	}
	if j := w.cc + wd; j < len(row.cells) && row.cells[j].ch == "" { // 覆盖了宽字符的前半格：后半格清掉
		row.cells[j] = blankCell
	}
	w.cc += wd
	w.dirty(row)
}

func (w *TermWriter) control(b byte) {
	switch b {
	case '\n', 0x0b, 0x0c:
		w.lineFeed()
	case '\r':
		w.cc = 0
	case '\b':
		if w.cc > 0 {
			w.cc--
		}
	case '\t':
		w.cc = min((w.cc/8+1)*8, maxTermCols-1)
	}
}

// setRow 移动光标所在行；往上移记下时间（说明程序在原地重画）。
func (w *TermWriter) setRow(r int) {
	r = max(0, min(r, len(w.rows)-1))
	if r < w.cr {
		w.lastUp = w.now()
	}
	w.cr = r
}

func (w *TermWriter) lineFeed() {
	w.rows[w.cr].nl = true
	switch {
	case w.cr == w.bot:
		w.scrollUp(1)
	case w.cr < len(w.rows)-1:
		w.cr++
	}
}

// scrollUp：滚动区域内上卷 n 行。区域从屏幕顶部开始时，卷出去的行进历史（没定型的就此定型）。
func (w *TermWriter) scrollUp(n int) {
	n = min(n, w.bot-w.top+1)
	for i := 0; i < n; i++ {
		gone := w.rows[w.top]
		if w.top == 0 && w.main == nil && !gone.committed && (!gone.blank() || gone.nl) {
			w.commit(gone)
		}
		copy(w.rows[w.top:w.bot], w.rows[w.top+1:w.bot+1])
		w.rows[w.bot] = &trow{touched: w.now()}
	}
}

// scrollDown：滚动区域内下卷 n 行（顶部插入空行，底部的行丢掉，和真终端一样）。
func (w *TermWriter) scrollDown(n int) {
	n = min(n, w.bot-w.top+1)
	for i := 0; i < n; i++ {
		copy(w.rows[w.top+1:w.bot+1], w.rows[w.top:w.bot])
		w.rows[w.top] = &trow{touched: w.now()}
	}
	w.lastUp = w.now()
}

func (w *TermWriter) clearRow(r *trow) {
	if len(r.cells) == 0 && !r.nl {
		return
	}
	r.cells = r.cells[:0]
	r.nl = false
	w.dirty(r)
}

// eraseCells 把 [from, to) 清成空格。
func (w *TermWriter) eraseCells(r *trow, from, to int) {
	to = min(to, len(r.cells))
	if from >= to {
		return
	}
	if to == len(r.cells) {
		r.cells = r.cells[:from]
	} else {
		for i := from; i < to; i++ {
			r.cells[i] = blankCell
		}
	}
	if r.blank() {
		r.nl = false
	}
	w.dirty(r)
}

func (w *TermWriter) reset() {
	for _, r := range w.rows {
		w.clearRow(r)
	}
	w.st = sgrStyle{}
	w.top, w.bot = 0, len(w.rows)-1
	w.setRow(0)
	w.cc = 0
}

func (w *TermWriter) esc(b byte) {
	switch b {
	case '[':
		w.state = stCSI
		w.params = w.params[:0]
	case ']', 'P', 'X', '^', '_':
		w.state = stString
	case '(', ')', '*', '+', '-', '.', '/', '#', '%', ' ':
		w.state = stEscSkip
	case '7':
		w.saveCursor()
	case '8':
		w.restoreCursor()
	case 'M': // 反向换行
		if w.cr == w.top {
			w.scrollDown(1)
		} else {
			w.setRow(w.cr - 1)
		}
	case 'D':
		w.lineFeed()
	case 'E':
		w.cc = 0
		w.lineFeed()
	case 'c':
		w.reset()
	}
}

func (w *TermWriter) saveCursor() {
	w.saved.r, w.saved.c, w.saved.st = w.cr, w.cc, w.st
}

func (w *TermWriter) restoreCursor() {
	w.setRow(w.saved.r)
	w.cc, w.st = w.saved.c, w.saved.st
}

// parseArgs 把 CSI 参数切成整数；空参数为 0。冒号写法（38:2::r:g:b）按分号处理。
func parseArgs(p []byte) []int {
	s := strings.ReplaceAll(string(p), "::", ":")
	if s == "" {
		return nil
	}
	var out []int
	cur, has := 0, false
	for i := 0; i <= len(s); i++ {
		if i == len(s) || s[i] == ';' || s[i] == ':' {
			if has {
				out = append(out, cur)
			} else {
				out = append(out, 0)
			}
			cur, has = 0, false
			continue
		}
		if s[i] >= '0' && s[i] <= '9' {
			if cur < 100000 {
				cur = cur*10 + int(s[i]-'0')
			}
			has = true
		}
	}
	return out
}

func (w *TermWriter) csi(final byte) {
	p := w.params
	var priv byte
	if len(p) > 0 && (p[0] == '?' || p[0] == '>' || p[0] == '=' || p[0] == '<') {
		priv, p = p[0], p[1:]
	}
	// 带中间字节的（如设置光标形状 CSI 2 SP q）一律忽略
	for _, b := range p {
		if b >= 0x20 && b <= 0x2f {
			return
		}
	}
	args := parseArgs(p)
	arg := func(i, def int) int {
		if i < len(args) && args[i] > 0 {
			return args[i]
		}
		return def
	}
	if priv != 0 {
		switch {
		case priv == '?' && (final == 'h' || final == 'l'):
			for _, a := range args {
				if a == 1049 || a == 1047 || a == 47 {
					w.altScreen(final == 'h', a == 1049)
				}
			}
		case priv == '>' && final == 'c':
			w.reply("\x1b[>0;10;1c")
		}
		return
	}
	row := w.rows[w.cr]
	switch final {
	case 'A':
		lo := 0
		if w.cr >= w.top {
			lo = w.top
		}
		w.setRow(max(lo, w.cr-arg(0, 1)))
	case 'B', 'e':
		hi := len(w.rows) - 1
		if w.cr <= w.bot {
			hi = w.bot
		}
		w.setRow(min(hi, w.cr+arg(0, 1)))
	case 'C', 'a':
		w.cc = min(w.cc+arg(0, 1), maxTermCols-1)
	case 'D':
		w.cc = max(0, w.cc-arg(0, 1))
	case 'E':
		w.cc = 0
		w.setRow(w.cr + arg(0, 1))
	case 'F':
		w.cc = 0
		w.setRow(w.cr - arg(0, 1))
	case 'G', '`':
		w.cc = min(arg(0, 1)-1, maxTermCols-1)
	case 'd':
		w.setRow(arg(0, 1) - 1)
	case 'H', 'f':
		w.setRow(arg(0, 1) - 1)
		w.cc = min(arg(1, 1)-1, maxTermCols-1)
	case 'J':
		switch arg(0, 0) {
		case 0:
			w.eraseCells(row, w.cc, maxTermCols)
			for _, r := range w.rows[w.cr+1:] {
				w.clearRow(r)
			}
		case 1:
			for _, r := range w.rows[:w.cr] {
				w.clearRow(r)
			}
			w.eraseCells(row, 0, w.cc+1)
		default: // 2、3：整屏（3 还要清回滚区，历史日志保留不动）
			for _, r := range w.rows {
				w.clearRow(r)
			}
		}
	case 'K':
		switch arg(0, 0) {
		case 0:
			w.eraseCells(row, w.cc, maxTermCols)
		case 1:
			w.eraseCells(row, 0, w.cc+1)
		default:
			w.clearRow(row)
		}
	case 'X':
		w.eraseCells(row, w.cc, w.cc+arg(0, 1))
	case 'P':
		if w.cc < len(row.cells) {
			n := min(arg(0, 1), len(row.cells)-w.cc)
			row.cells = append(row.cells[:w.cc], row.cells[w.cc+n:]...)
			w.dirty(row)
		}
	case '@':
		if w.cc < len(row.cells) {
			n := min(arg(0, 1), maxTermCols)
			ins := make([]tcell, n)
			for i := range ins {
				ins[i] = blankCell
			}
			row.cells = append(row.cells[:w.cc], append(ins, row.cells[w.cc:]...)...)
			if len(row.cells) > maxTermCols {
				row.cells = row.cells[:maxTermCols]
			}
			w.dirty(row)
		}
	case 'L': // 插入行
		if w.cr >= w.top && w.cr <= w.bot {
			top := w.top
			w.top = w.cr
			w.scrollDown(arg(0, 1))
			w.top = top
		}
	case 'M': // 删除行
		if w.cr >= w.top && w.cr <= w.bot {
			n := min(arg(0, 1), w.bot-w.cr+1)
			copy(w.rows[w.cr:w.bot+1-n], w.rows[w.cr+n:w.bot+1])
			for i := w.bot + 1 - n; i <= w.bot; i++ {
				w.rows[i] = &trow{touched: w.now()}
			}
			w.lastUp = w.now()
		}
	case 'S':
		w.scrollUp(arg(0, 1))
	case 'T':
		w.scrollDown(arg(0, 1))
	case 'm':
		w.sgr(args)
	case 'r':
		top, bot := arg(0, 1)-1, arg(1, len(w.rows))-1
		if top < bot && bot < len(w.rows) {
			w.top, w.bot = top, bot
		} else {
			w.top, w.bot = 0, len(w.rows)-1
		}
		w.setRow(0)
		w.cc = 0
	case 's':
		if len(args) == 0 {
			w.saveCursor()
		}
	case 'u':
		w.restoreCursor()
	case 'n':
		switch arg(0, 0) {
		case 5:
			w.reply("\x1b[0n")
		case 6:
			w.reply("\x1b[" + strconv.Itoa(w.cr+1) + ";" + strconv.Itoa(w.cc+1) + "R")
		}
	case 'c':
		w.reply("\x1b[?1;2c")
	case 't':
		if arg(0, 0) == 18 {
			w.reply("\x1b[8;" + strconv.Itoa(len(w.rows)) + ";" + strconv.Itoa(TermCols) + "t")
		}
	}
}

// reply 回答终端查询。异步写：程序不读输入时伪终端写满会阻塞，不能卡住读输出的协程。
func (w *TermWriter) reply(s string) {
	if w.respond != nil {
		go w.respond([]byte(s))
	}
}

// altScreen 进出备用屏幕：里面的内容只作为实时尾部显示，不进日志；退出后回到原来的屏幕。
func (w *TermWriter) altScreen(on, cursor bool) {
	if on == (w.main != nil) {
		return
	}
	if on {
		if cursor {
			w.saveCursor()
		}
		saved := w.screen
		saved.rows = append([]*trow(nil), w.rows...)
		w.main = &saved
		rows := make([]*trow, len(w.rows))
		for i := range rows {
			rows[i] = &trow{committed: false}
		}
		w.screen = screen{rows: rows, cr: 0, cc: 0, top: 0, bot: len(rows) - 1}
		return
	}
	w.screen = *w.main
	w.main = nil
	w.lastUp = w.now()
	if cursor {
		w.restoreCursor()
	}
}

func (w *TermWriter) sgr(args []int) {
	if len(args) == 0 {
		w.st = sgrStyle{}
		return
	}
	for i := 0; i < len(args); i++ {
		a := args[i]
		switch {
		case a == 0:
			w.st = sgrStyle{}
		case a >= 1 && a <= 9 && a != 6:
			bit := map[int]uint16{1: attrBold, 2: attrDim, 3: attrItalic, 4: attrUnderline, 5: attrBlink, 7: attrInverse, 8: attrHidden, 9: attrStrike}[a]
			w.st.attr |= bit
		case a == 6:
			w.st.attr |= attrBlink
		case a == 21 || a == 22:
			w.st.attr &^= attrBold | attrDim
		case a == 23:
			w.st.attr &^= attrItalic
		case a == 24:
			w.st.attr &^= attrUnderline
		case a == 25:
			w.st.attr &^= attrBlink
		case a == 27:
			w.st.attr &^= attrInverse
		case a == 28:
			w.st.attr &^= attrHidden
		case a == 29:
			w.st.attr &^= attrStrike
		case (a >= 30 && a <= 37) || (a >= 90 && a <= 97):
			w.st.fg = strconv.Itoa(a)
		case a == 39:
			w.st.fg = ""
		case (a >= 40 && a <= 47) || (a >= 100 && a <= 107):
			w.st.bg = strconv.Itoa(a)
		case a == 49:
			w.st.bg = ""
		case a == 38 || a == 48:
			var v string
			switch {
			case i+2 < len(args) && args[i+1] == 5:
				v = strconv.Itoa(a) + ";5;" + strconv.Itoa(min(args[i+2], 255))
				i += 2
			case i+4 < len(args) && args[i+1] == 2:
				v = strconv.Itoa(a) + ";2;" + strconv.Itoa(min(args[i+2], 255)) + ";" + strconv.Itoa(min(args[i+3], 255)) + ";" + strconv.Itoa(min(args[i+4], 255))
				i += 4
			default:
				i = len(args) // 写法不认识，后面的参数也不可信
				continue
			}
			if a == 38 {
				w.st.fg = v
			} else {
				w.st.bg = v
			}
		}
	}
}

// DisplayWidth 返回文本在终端里占的列数（去掉 ANSI 序列，中文等宽字符算两列）。
func DisplayWidth(s string) int {
	n := 0
	for _, r := range StripANSI(s) {
		n += runeWidth(r)
	}
	return n
}

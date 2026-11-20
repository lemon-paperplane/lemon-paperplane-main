package engine

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
)

// 交互终端：每个终端是伪终端里的一个登录 shell（和系统终端、VS Code 一样 -l 启动，读用户的 .zprofile/.zshrc）。
// 输出保存在内存环形缓冲里（最后 termBacklog 字节），界面断开再连（切走再切回、重开窗口）时从缓冲补齐。
// 输入经一个写协程按顺序写入，粘贴大段文字时不会阻塞调用方。

const (
	termBacklog   = 1 << 20 // 每个终端保留的输出字节数
	termReadBuf   = 32 << 10
	termInputQ    = 512
	termSubBuffer = 256
)

type ptyHandle interface {
	io.ReadWriteCloser
	Resize(cols, rows int) error
	Foreground() int // 前台进程组（Windows：shell 的子进程），0 表示取不到
	Hangup()         // 和关掉终端窗口一样通知 shell 退出（Unix 发 SIGHUP，Windows 关闭伪控制台）
}

// termProc 是伪终端里运行的 shell 进程。
type termProc interface {
	Pid() int
	Wait() (code int, err error) // err 只表示等待本身失败；被信号结束时 code 为 129
}

// TermChunk 是推给订阅者的一段输出；Info 非空表示终端状态变了（退出、重新启动）。
type TermChunk struct {
	Off  int64
	Data []byte
	Info *model.Terminal
}

type termState struct {
	mu      sync.Mutex
	info    model.Terminal
	env     []string
	pty     ptyHandle
	proc    termProc
	buf     []byte // 最后 termBacklog 字节输出
	end     int64  // 累计输出字节数
	subs    map[chan TermChunk]struct{}
	input   chan []byte
	exited  chan struct{} // 当前进程结束
	closing bool
	removed bool
	log     *termLog // 完整输出落盘（termlog.go）
}

// TerminalCreateParams 是 terminal.create 的参数。
type TerminalCreateParams struct {
	WorkspaceID string `json:"workspaceId"`
	ProjectID   string `json:"projectId"`
	Cwd         string `json:"cwd"`
	Name        string `json:"name"`
	Cols        int    `json:"cols"`
	Rows        int    `json:"rows"`
}

func (e *Engine) termMap() map[string]*termState {
	if e.terms == nil {
		e.terms = map[string]*termState{}
	}
	return e.terms
}

// TerminalCreate 新建终端：起始目录默认是工程根目录，名字默认是工程名（同工作区重名时加序号）。
func (e *Engine) TerminalCreate(in TerminalCreateParams) (*model.Terminal, error) {
	if !terminalSupported() {
		return nil, Errf("err.terminalUnsupported")
	}
	var overlay map[string]string
	cwd, name, wsID := strings.TrimSpace(in.Cwd), strings.TrimSpace(in.Name), in.WorkspaceID
	if in.ProjectID != "" {
		p, ok := e.Store.Project(in.ProjectID)
		if !ok {
			return nil, ErrNotFound
		}
		overlay = p.Settings.Env
		wsID = p.WorkspaceID
		if name == "" {
			name = p.Name
		}
		if cwd == "" {
			cwd = p.Path
		} else if !filepath.IsAbs(cwd) {
			cwd = filepath.Join(p.Path, cwd)
		}
	}
	if cwd == "" {
		cwd, _ = os.UserHomeDir()
	}
	if st, err := os.Stat(cwd); err != nil || !st.IsDir() {
		return nil, Errf("err.dirMissing", cwd)
	}
	env, shell := e.terminalEnv(overlay, cwd)
	if name == "" {
		name = filepath.Base(shell)
	}
	cols, rows := clampSize(in.Cols, 100), clampSize(in.Rows, 30)

	e.mu.Lock()
	name = e.uniqueTermNameLocked(wsID, name)
	t := &termState{
		info: model.Terminal{ID: store.NewID(), WorkspaceID: wsID, ProjectID: in.ProjectID, Name: name, Cwd: cwd, Shell: shell, Cols: cols, Rows: rows},
		env:  env, subs: map[chan TermChunk]struct{}{}, input: make(chan []byte, termInputQ),
		log: &termLog{dir: e.termLogDir()},
	}
	e.termMap()[t.info.ID] = t
	e.termOrder = append(e.termOrder, t.info.ID)
	e.mu.Unlock()

	if err := e.startTerm(t); err != nil {
		e.removeTerm(t.info.ID)
		return nil, err
	}
	go e.termWriter(t)
	e.publishTerminals()
	info := t.snapshot()
	return &info, nil
}

func clampSize(v, def int) int {
	if v <= 0 {
		return def
	}
	if v > 1000 {
		return 1000
	}
	return v
}

func (e *Engine) uniqueTermNameLocked(wsID, name string) string {
	used := map[string]bool{}
	for _, t := range e.terms {
		t.mu.Lock()
		if t.info.WorkspaceID == wsID {
			used[t.info.Name] = true
		}
		t.mu.Unlock()
	}
	if !used[name] {
		return name
	}
	for i := 2; ; i++ {
		if n := fmt.Sprintf("%s %d", name, i); !used[n] {
			return n
		}
	}
}

// terminalEnv 组装终端环境：和运行一样的基础环境（登录 shell 的 PATH、镜像、工程环境变量），
// 加上终端类型，去掉启动 daemon 的那个终端留下的会话变量。
func (e *Engine) terminalEnv(overlay map[string]string, cwd string) ([]string, string) {
	m := map[string]string{}
	for _, kv := range e.buildEnv(overlay, nil) {
		if k, v, ok := strings.Cut(kv, "="); ok {
			m[k] = v
		}
	}
	for k := range m {
		if strings.HasPrefix(k, "ITERM_") || strings.HasPrefix(k, "VSCODE_") || strings.HasPrefix(k, "KITTY_") ||
			strings.HasPrefix(k, "WEZTERM_") || strings.HasPrefix(k, "ALACRITTY_") || strings.HasPrefix(k, "GHOSTTY_") ||
			(strings.HasPrefix(k, "PAPERPLANE_") && k != "PAPERPLANE_DATA_DIR") { // 留下数据目录：终端里的 paperplane 命令连的就是这个 daemon
			delete(m, k)
		}
	}
	for _, k := range []string{"TERM_SESSION_ID", "TMUX", "TMUX_PANE", "STY", "INSIDE_EMACS", "COLUMNS", "LINES", "SHLVL", "OLDPWD", "_"} {
		delete(m, k)
	}
	m["TERM"] = "xterm-256color"
	m["COLORTERM"] = "truecolor"
	m["TERM_PROGRAM"] = "LemonPaperplane"
	m["TERM_PROGRAM_VERSION"] = e.cfg.Version
	m["PWD"] = cwd
	if m["LANG"] == "" && m["LC_ALL"] == "" && m["LC_CTYPE"] == "" {
		// 从图形界面启动的 daemon 没有语言环境，shell 会按 ASCII 处理中文
		if strings.HasPrefix(i18n.Locale(), "zh") {
			m["LANG"] = "zh_CN.UTF-8"
		} else {
			m["LANG"] = "en_US.UTF-8"
		}
	}
	shell := defaultShell(m)
	out := make([]string, 0, len(m))
	for k, v := range m {
		out = append(out, k+"="+v)
	}
	return out, shell
}

func isExecutable(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir() && (runtime.GOOS == "windows" || st.Mode().Perm()&0o111 != 0)
}

// startTerm 启动（或重新启动）终端里的 shell。
func (e *Engine) startTerm(t *termState) error {
	t.mu.Lock()
	p, tp, err := startPTY(t.info.Shell, shellArgs(t.info.Shell), t.info.Cwd, t.env, t.info.Cols, t.info.Rows)
	if err != nil {
		t.mu.Unlock()
		return Errf("err.terminalStart", err.Error())
	}
	t.pty, t.proc = p, tp
	t.exited = make(chan struct{})
	t.info.PID = tp.Pid()
	t.info.StartedAt = time.Now()
	t.info.Exited, t.info.ExitCode = false, 0
	exited := t.exited
	t.mu.Unlock()

	readDone := make(chan struct{})
	go e.termReader(t, p, readDone)
	go e.termWaiter(t, tp, p, exited, readDone)
	return nil
}

func (e *Engine) termReader(t *termState, p ptyHandle, done chan struct{}) {
	defer close(done)
	b := make([]byte, termReadBuf)
	for {
		n, err := p.Read(b)
		if n > 0 {
			t.appendOutput(b[:n])
		}
		if err != nil {
			return
		}
	}
}

func (e *Engine) termWaiter(t *termState, tp termProc, p ptyHandle, exited, readDone chan struct{}) {
	code, err := tp.Wait()
	if err != nil {
		code = -1
	}
	// shell 退出后把剩下的输出读完；后台任务还占着终端时最多等 500ms
	select {
	case <-readDone:
	case <-time.After(500 * time.Millisecond):
	}
	_ = p.Close()
	<-readDone

	t.mu.Lock()
	closing := t.closing
	t.info.Exited, t.info.ExitCode, t.info.PID = true, code, 0
	close(exited)
	t.mu.Unlock()
	if closing {
		return
	}
	if code == 0 {
		// 和 VS Code 一样：正常退出（输入 exit）就关掉这个终端
		e.removeTerm(t.info.ID)
		e.publishTerminals()
		return
	}
	t.appendOutput([]byte("\r\n\x1b[2m" + i18n.T("terminal.exited", code) + "\x1b[0m\r\n"))
	info := t.snapshot()
	t.broadcast(TermChunk{Info: &info})
	e.publishTerminals()
}

// termWriter 按顺序把输入写进伪终端（写满时会阻塞，放在单独的协程里）。
func (e *Engine) termWriter(t *termState) {
	for b := range t.input {
		t.mu.Lock()
		p, done := t.pty, t.info.Exited
		t.mu.Unlock()
		if p == nil || done {
			continue
		}
		_, _ = p.Write(b)
	}
}

func (t *termState) appendOutput(b []byte) {
	t.mu.Lock()
	off := t.end
	t.log.write(t.info.ID, off, b)
	t.end += int64(len(b))
	t.buf = append(t.buf, b...)
	if over := len(t.buf) - termBacklog; over > 0 {
		// 环形缓冲：丢掉最前面的部分（多留一点余量，避免每次都搬动）
		cut := over + termBacklog/8
		if cut > len(t.buf) {
			cut = len(t.buf)
		}
		t.buf = append(t.buf[:0:0], t.buf[cut:]...)
	}
	data := append([]byte(nil), b...)
	t.mu.Unlock()
	t.broadcast(TermChunk{Off: off, Data: data})
}

// broadcast 推给所有订阅者；跟不上的订阅者直接断开，它重连时按偏移从缓冲补齐。
func (t *termState) broadcast(c TermChunk) {
	t.mu.Lock()
	defer t.mu.Unlock()
	for ch := range t.subs {
		select {
		case ch <- c:
		default:
			delete(t.subs, ch)
			close(ch)
		}
	}
}

func (t *termState) snapshot() model.Terminal {
	t.mu.Lock()
	defer t.mu.Unlock()
	i := t.info
	i.Output = t.end
	return i
}

func (e *Engine) term(id string) *termState {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.terms[id]
}

// TerminalList 返回终端（按创建顺序），workspaceID 为空时返回全部。
func (e *Engine) TerminalList(workspaceID string) []model.Terminal {
	e.mu.Lock()
	ts := make([]*termState, 0, len(e.termOrder))
	for _, id := range e.termOrder {
		if t := e.terms[id]; t != nil {
			ts = append(ts, t)
		}
	}
	e.mu.Unlock()
	out := []model.Terminal{}
	for _, t := range ts {
		if i := t.snapshot(); workspaceID == "" || i.WorkspaceID == workspaceID {
			out = append(out, i)
		}
	}
	return out
}

func (e *Engine) publishTerminals() { e.Bus.Publish(model.EvTerminals, e.TerminalList("")) }

// TerminalInput 把键盘输入（UTF-8 文本或控制字符）写进终端。
func (e *Engine) TerminalInput(id, data string) error {
	t := e.term(id)
	if t == nil {
		return ErrNotFound
	}
	if data == "" {
		return nil
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.info.Exited || t.removed {
		return nil
	}
	select { // 在锁内发送：removeTerm 关闭 input 也在锁内
	case t.input <- []byte(data):
		return nil
	default:
		return Errf("err.terminalBusy")
	}
}

// TerminalResize 调整终端行列数（界面尺寸变化时调用）。
func (e *Engine) TerminalResize(id string, cols, rows int) error {
	t := e.term(id)
	if t == nil {
		return ErrNotFound
	}
	cols, rows = clampSize(cols, 100), clampSize(rows, 30)
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.info.Cols == cols && t.info.Rows == rows {
		return nil
	}
	t.info.Cols, t.info.Rows = cols, rows
	if t.pty != nil && !t.info.Exited {
		return t.pty.Resize(cols, rows)
	}
	return nil
}

// TerminalRename 改终端名字。
func (e *Engine) TerminalRename(id, name string) error {
	t := e.term(id)
	if t == nil {
		return ErrNotFound
	}
	name = strings.TrimSpace(name)
	if name == "" {
		return Errf("err.emptyName")
	}
	if utf8.RuneCountInString(name) > 80 {
		name = string([]rune(name)[:80])
	}
	t.mu.Lock()
	t.info.Name = name
	t.mu.Unlock()
	e.publishTerminals()
	return nil
}

// TerminalRestart 在原来的目录重新启动已退出的终端（输出接在原来的后面）。
func (e *Engine) TerminalRestart(id string) (*model.Terminal, error) {
	t := e.term(id)
	if t == nil {
		return nil, ErrNotFound
	}
	t.mu.Lock()
	running := !t.info.Exited
	t.mu.Unlock()
	if !running {
		t.appendOutput([]byte("\x1b[0m\r\n"))
		if err := e.startTerm(t); err != nil {
			return nil, err
		}
		info := t.snapshot()
		t.broadcast(TermChunk{Info: &info})
		e.publishTerminals()
	}
	info := t.snapshot()
	return &info, nil
}

// TerminalClose 关闭终端：给 shell 所在进程组发 SIGHUP（和关掉终端窗口一样），2 秒后还在就强制结束。
func (e *Engine) TerminalClose(id string) error {
	t := e.term(id)
	if t == nil {
		return ErrNotFound
	}
	e.closeTerm(t, 2*time.Second)
	e.removeTerm(id)
	e.publishTerminals()
	return nil
}

func (e *Engine) closeTerm(t *termState, grace time.Duration) {
	t.mu.Lock()
	t.closing = true
	pid, exited, p := t.info.PID, t.exited, t.pty
	alive := !t.info.Exited
	t.mu.Unlock()
	if !alive {
		return
	}
	if p != nil {
		p.Hangup()
	}
	go func() {
		select {
		case <-exited:
		case <-time.After(grace):
			proc.KillGroup(pid)
			if p != nil {
				_ = p.Close()
			}
		}
	}()
}

func (e *Engine) removeTerm(id string) {
	e.mu.Lock()
	t := e.terms[id]
	delete(e.terms, id)
	for i, x := range e.termOrder {
		if x == id {
			e.termOrder = append(e.termOrder[:i:i], e.termOrder[i+1:]...)
			break
		}
	}
	e.mu.Unlock()
	if t == nil {
		return
	}
	t.mu.Lock()
	t.log.close(id)
	if !t.removed {
		t.removed = true
		close(t.input)
		for ch := range t.subs {
			close(ch)
		}
		t.subs = map[chan TermChunk]struct{}{}
	}
	t.mu.Unlock()
}

// closeAllTerminals 在 daemon 退出时结束全部终端。
func (e *Engine) closeAllTerminals() {
	e.mu.Lock()
	ts := make([]*termState, 0, len(e.terms))
	for _, t := range e.terms {
		ts = append(ts, t)
	}
	e.mu.Unlock()
	for _, t := range ts {
		e.closeTerm(t, 500*time.Millisecond)
	}
}

// TerminalSubscribe 订阅终端输出：先返回从 since 起缓冲里还有的部分（start 是它的起始偏移），之后的输出从 ch 推送。
// full 为 true 时内存缓冲之前的部分从磁盘补（完整回放）；否则只回放内存里的最后 termBacklog 字节。
// ch 被关闭表示终端已关闭，或者订阅者跟不上被断开（这时终端还在，重新订阅即可）。
func (e *Engine) TerminalSubscribe(id string, since int64, full bool) (backlog []byte, start int64, info model.Terminal, ch chan TermChunk, cancel func(), err error) {
	t := e.term(id)
	if t == nil {
		return nil, 0, info, nil, nil, ErrNotFound
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.removed {
		return nil, 0, info, nil, nil, ErrNotFound
	}
	first := t.end - int64(len(t.buf))
	if since > t.end {
		since = first
	}
	if since < first {
		if d := t.log.firstOffset(); full && d >= 0 && d < first {
			if since < d {
				since = d
			}
			backlog = t.log.read(id, since, first)
			start = since
			since = first
		} else {
			since = first
		}
	}
	if backlog == nil {
		start = since
	}
	backlog = append(backlog, t.buf[since-first:]...)
	info = t.info
	info.Output = t.end
	ch = make(chan TermChunk, termSubBuffer)
	t.subs[ch] = struct{}{}
	cancel = func() {
		t.mu.Lock()
		if _, ok := t.subs[ch]; ok {
			delete(t.subs, ch)
			close(ch)
		}
		t.mu.Unlock()
	}
	return backlog, start, info, ch, cancel, nil
}

// TerminalHistory 是 terminal.history 的结果。
type TerminalHistory struct {
	Start int64  `json:"start"` // 这段输出的起始偏移
	End   int64  `json:"end"`   // 这段输出的结束偏移（下次从这里接着读）
	First int64  `json:"first"` // 最早还能读到的偏移
	Total int64  `json:"total"` // 累计输出字节数
	Data  string `json:"data"`
}

// TerminalHistory 按偏移读一段完整输出（磁盘 + 内存）；strip 时去掉颜色和光标控制，只留文本。
func (e *Engine) TerminalHistory(id string, offset int64, limit int, strip bool) (*TerminalHistory, error) {
	t := e.term(id)
	if t == nil {
		return nil, ErrNotFound
	}
	if limit <= 0 || limit > 1<<20 {
		limit = 256 << 10
	}
	t.mu.Lock()
	first := t.end - int64(len(t.buf))
	earliest := first
	if d := t.log.firstOffset(); d >= 0 && d < earliest {
		earliest = d
	}
	if offset < earliest {
		offset = earliest
	}
	if offset > t.end {
		offset = t.end
	}
	to := min64(offset+int64(limit), t.end)
	var b []byte
	if offset < first {
		b = t.log.read(id, offset, min64(to, first))
	}
	if to > first {
		b = append(b, t.buf[max64(offset, first)-first:to-first]...)
	}
	total := t.end
	t.mu.Unlock()
	// 不在多字节字符中间截断（下次从 End 接着读）
	if to < total {
		for i := len(b) - 1; i >= 0 && i >= len(b)-utf8.UTFMax; i-- {
			if utf8.RuneStart(b[i]) {
				if !utf8.FullRune(b[i:]) {
					to -= int64(len(b) - i)
					b = b[:i]
				}
				break
			}
		}
	}
	s := string(b)
	if strip {
		s = stripTerminal(b)
	}
	return &TerminalHistory{Start: offset, End: to, First: earliest, Total: total, Data: s}, nil
}

// TerminalBusy 判断终端里是否有命令正在前台运行（不是 shell 自己在等输入），返回该命令的名字。
// 关闭终端前用它决定要不要先确认。
func (e *Engine) TerminalBusy(id string) (bool, string, error) {
	t := e.term(id)
	if t == nil {
		return false, "", ErrNotFound
	}
	t.mu.Lock()
	p, pid, exited := t.pty, t.info.PID, t.info.Exited
	t.mu.Unlock()
	if exited || p == nil || pid == 0 {
		return false, "", nil
	}
	fg := p.Foreground()
	if fg == 0 || fg == pid {
		return false, "", nil
	}
	name := filepath.Base(proc.ProcessName(fg))
	return true, strings.TrimPrefix(name, "-"), nil
}

// TerminalExists 判断终端是否还在。
func (e *Engine) TerminalExists(id string) bool { return e.term(id) != nil }

// TerminalRead 返回终端最近的输出（去掉颜色等控制序列的纯文本），给 AI / 命令行看。
func (e *Engine) TerminalRead(id string, maxBytes int) (string, error) {
	t := e.term(id)
	if t == nil {
		return "", ErrNotFound
	}
	if maxBytes <= 0 || maxBytes > termBacklog {
		maxBytes = 16 << 10
	}
	t.mu.Lock()
	b := t.buf
	if len(b) > maxBytes*2 {
		b = b[len(b)-maxBytes*2:]
	}
	b = append([]byte(nil), b...)
	t.mu.Unlock()
	s := stripTerminal(b)
	if len(s) > maxBytes {
		s = s[len(s)-maxBytes:]
		for len(s) > 0 && !utf8.RuneStart(s[0]) {
			s = s[1:]
		}
	}
	return s, nil
}

// stripTerminal 去掉 ANSI 控制序列，按回车/退格粗略还原每一行最终显示的样子。
func stripTerminal(b []byte) string {
	var lines []string
	var cur []rune
	col := 0
	s := []rune(strings.ToValidUTF8(string(b), ""))
	put := func(r rune) {
		if col < len(cur) {
			cur[col] = r
		} else {
			cur = append(cur, r)
		}
		col++
	}
	for i := 0; i < len(s); i++ {
		r := s[i]
		switch {
		case r == 0x1b && i+1 < len(s):
			i++
			switch s[i] {
			case '[': // CSI：参数直到 0x40–0x7E 结束
				for i+1 < len(s) {
					i++
					if s[i] >= 0x40 && s[i] <= 0x7e {
						if s[i] == 'K' { // 清到行尾
							if col < len(cur) {
								cur = cur[:col]
							}
						}
						break
					}
				}
			case ']', 'P', '_', '^': // OSC / DCS 等：到 BEL 或 ST 结束
				for i+1 < len(s) {
					i++
					if s[i] == 0x07 {
						break
					}
					if s[i] == 0x1b && i+1 < len(s) && s[i+1] == '\\' {
						i++
						break
					}
				}
			case '(', ')', '*', '+':
				i++
			}
		case r == '\r':
			col = 0
		case r == '\n':
			lines = append(lines, strings.TrimRight(string(cur), " "))
			cur, col = cur[:0:0], 0
		case r == '\b':
			if col > 0 {
				col--
			}
		case r == '\t':
			for {
				put(' ')
				if col%8 == 0 {
					break
				}
			}
		case r < 0x20 || r == 0x7f:
		default:
			put(r)
		}
	}
	if len(cur) > 0 {
		lines = append(lines, strings.TrimRight(string(cur), " "))
	}
	return strings.Join(lines, "\n")
}

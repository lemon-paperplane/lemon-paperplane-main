// Package console 是运行/任务的日志控制台：内存环形缓冲 + 落盘 + 订阅。
package console

import (
	"bufio"
	"os"
	"regexp"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
)

const maxLineBytes = 64 * 1024

// maxDisplayBytes 是控制台里一行最多显示多少字节。更长的（如 xcodebuild 失败时几千字符的编译命令）
// 只在内存/界面里截断，日志文件里保留完整内容。
const maxDisplayBytes = 4000

// displayText 截断超长行（按 UTF-8 字符边界）。
func displayText(text string) string {
	if len(text) <= maxDisplayBytes {
		return text
	}
	cut := maxDisplayBytes
	for cut > 0 && !utf8.RuneStart(text[cut]) {
		cut--
	}
	return text[:cut] + " " + i18n.T("console.lineTruncated", utf8.RuneCountInString(text[cut:]))
}

// Console 是一个控制台。
type Console struct {
	ID string

	mu     sync.Mutex
	buf    []model.LogLine
	head   int // 环形缓冲起点
	n      int
	cap    int
	seq    int64
	subs   map[chan model.LogLine]struct{}
	hooks  []func(model.LogLine)
	file   *os.File
	fileW  *bufio.Writer
	closed bool
	done   chan struct{}
}

// New 创建控制台；logFile 非空时同时落盘。
func New(id string, capacity int, logFile string) *Console {
	if capacity <= 0 {
		capacity = 20000
	}
	c := &Console{ID: id, cap: capacity, buf: make([]model.LogLine, 0, 256), subs: map[chan model.LogLine]struct{}{}, done: make(chan struct{})}
	if logFile != "" {
		if f, err := os.Create(logFile); err == nil {
			c.file = f
			c.fileW = bufio.NewWriterSize(f, 32*1024)
			go c.flusher()
		}
	}
	return c
}

func (c *Console) flusher() {
	t := time.NewTicker(time.Second)
	defer t.Stop()
	for {
		select {
		case <-t.C:
			c.mu.Lock()
			if c.fileW != nil {
				_ = c.fileW.Flush()
			}
			c.mu.Unlock()
		case <-c.done:
			return
		}
	}
}

// OnLine 注册每行回调（诊断、URL 识别），在追加的 goroutine 中同步调用，须快速返回。
func (c *Console) OnLine(fn func(model.LogLine)) {
	c.mu.Lock()
	c.hooks = append(c.hooks, fn)
	c.mu.Unlock()
}

// Append 追加一行（text 不含换行）。
func (c *Console) Append(stream, text string) {
	if len(text) > maxLineBytes {
		text = text[:maxLineBytes] + "…"
	}
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return
	}
	c.seq++
	line := model.LogLine{Seq: c.seq, Time: time.Now().UnixMilli(), Stream: stream, Text: displayText(text)}
	if c.n < c.cap {
		if len(c.buf) < c.cap {
			c.buf = append(c.buf, line)
		} else {
			c.buf[(c.head+c.n)%c.cap] = line
		}
		c.n++
	} else {
		c.buf[c.head] = line
		c.head = (c.head + 1) % c.cap
	}
	if c.fileW != nil {
		_, _ = c.fileW.WriteString(text)
		_ = c.fileW.WriteByte('\n')
	}
	hooks := c.hooks
	for ch := range c.subs {
		select {
		case ch <- line:
		default:
		}
	}
	c.mu.Unlock()
	for _, h := range hooks {
		h(line)
	}
}

// System 追加一行系统信息。
func (c *Console) System(text string) {
	for _, l := range strings.Split(text, "\n") {
		c.Append("system", l)
	}
}

// Writer 返回按行切分的写入器。
func (c *Console) Writer(stream string) *LineWriter {
	return &LineWriter{c: c, stream: stream}
}

// LastSeq 返回最新行号。
func (c *Console) LastSeq() int64 {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.seq
}

// Since 返回 seq 大于 since 的缓冲行。
func (c *Console) Since(since int64) []model.LogLine {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.sinceLocked(since)
}

func (c *Console) sinceLocked(since int64) []model.LogLine {
	var out []model.LogLine
	for i := 0; i < c.n; i++ {
		l := c.buf[(c.head+i)%len(c.buf)]
		if l.Seq > since {
			out = append(out, l)
		}
	}
	return out
}

// Tail 返回最后 n 行。
func (c *Console) Tail(n int) []model.LogLine {
	c.mu.Lock()
	defer c.mu.Unlock()
	if n <= 0 || n > c.n {
		n = c.n
	}
	out := make([]model.LogLine, 0, n)
	for i := c.n - n; i < c.n; i++ {
		out = append(out, c.buf[(c.head+i)%len(c.buf)])
	}
	return out
}

// Subscribe 先返回 since 之后的缓冲行，再推送新行。done 在控制台关闭后关闭。
func (c *Console) Subscribe(since int64) (backlog []model.LogLine, ch <-chan model.LogLine, cancel func(), done <-chan struct{}) {
	c.mu.Lock()
	defer c.mu.Unlock()
	backlog = c.sinceLocked(since)
	sub := make(chan model.LogLine, 4096)
	if !c.closed {
		c.subs[sub] = struct{}{}
	}
	return backlog, sub, func() {
		c.mu.Lock()
		delete(c.subs, sub)
		c.mu.Unlock()
	}, c.done
}

// Clear 清空内存缓冲（不影响落盘与行号）。
func (c *Console) Clear() {
	c.mu.Lock()
	c.buf = c.buf[:0]
	c.head, c.n = 0, 0
	c.mu.Unlock()
}

// Close 结束控制台。
func (c *Console) Close() {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return
	}
	c.closed = true
	if c.fileW != nil {
		_ = c.fileW.Flush()
		_ = c.file.Close()
	}
	close(c.done)
}

// Closed 判断是否已结束。
func (c *Console) Closed() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.closed
}

// LineWriter 把字节流切成行写入控制台；处理 \r 进度条（只保留最后一段）。
type LineWriter struct {
	c       *Console
	stream  string
	mu      sync.Mutex
	partial []byte
}

func (w *LineWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	data := append(w.partial, p...)
	for {
		i := indexByte(data, '\n')
		if i < 0 {
			break
		}
		line := data[:i]
		data = data[i+1:]
		w.emit(line)
	}
	if len(data) > maxLineBytes {
		w.emit(data)
		data = nil
	}
	w.partial = append(w.partial[:0], data...)
	return len(p), nil
}

// Flush 输出残留的不完整行。
func (w *LineWriter) Flush() {
	w.mu.Lock()
	defer w.mu.Unlock()
	if len(w.partial) > 0 {
		w.emit(w.partial)
		w.partial = nil
	}
}

func (w *LineWriter) emit(line []byte) {
	s := strings.TrimRight(decodeLine(line), "\r")
	if i := strings.LastIndex(s, "\r"); i >= 0 {
		s = s[i+1:]
	}
	w.c.Append(w.stream, s)
}

func indexByte(b []byte, c byte) int {
	for i, x := range b {
		if x == c {
			return i
		}
	}
	return -1
}

var ansiRe = regexp.MustCompile(`\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[@-Z\\-_]`)

// StripANSI 去掉 ANSI 转义序列。
func StripANSI(s string) string {
	if !strings.Contains(s, "\x1b") {
		return s
	}
	return ansiRe.ReplaceAllString(s, "")
}

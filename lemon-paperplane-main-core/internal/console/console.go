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

	mu    sync.Mutex
	buf   []model.LogLine
	head  int // 环形缓冲起点
	n     int
	cap   int
	seq   int64
	subs  map[chan model.LogLine]struct{}
	hooks []func(model.LogLine)
	file  *os.File
	fileW *bufio.Writer
	path  string
	// 日志文件第 N 行就是第 N 条（seq = N）；index[k] 是第 k*indexStep+1 行在文件里的字节偏移，
	// 内存缓冲之外的旧行按它从文件里直接读（完整日志不受内存行数限制）
	fileOff int64
	index   []int64
	closed  bool
	done    chan struct{}
}

const indexStep = 1000

// New 创建控制台；logFile 非空时同时落盘。
func New(id string, capacity int, logFile string) *Console {
	if capacity <= 0 {
		capacity = 20000
	}
	c := &Console{ID: id, cap: capacity, buf: make([]model.LogLine, 0, 256), subs: map[chan model.LogLine]struct{}{}, done: make(chan struct{})}
	if logFile != "" {
		if f, err := os.Create(logFile); err == nil {
			c.file = f
			c.path = logFile
			c.fileW = bufio.NewWriterSize(f, 32*1024)
			go c.flusher()
		}
	}
	return c
}

// Open 从已有的日志文件恢复一个只读控制台（内核重启后的历史运行）：最后 capacity 行进内存，其余按需从文件读。
func Open(id string, capacity int, logFile string) (*Console, error) {
	if capacity <= 0 {
		capacity = 20000
	}
	f, err := os.Open(logFile)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	c := &Console{ID: id, cap: capacity, buf: make([]model.LogLine, 0, 256), subs: map[chan model.LogLine]struct{}{}, done: make(chan struct{}), path: logFile, closed: true}
	close(c.done)
	r := bufio.NewReaderSize(f, 64*1024)
	for {
		raw, err := r.ReadString('\n')
		if raw == "" && err != nil {
			break
		}
		if c.seq%indexStep == 0 {
			c.index = append(c.index, c.fileOff)
		}
		c.fileOff += int64(len(raw))
		c.seq++
		c.pushLocked(model.LogLine{Seq: c.seq, Stream: "stdout", Text: displayText(strings.TrimRight(raw, "\n"))})
		if err != nil {
			break
		}
	}
	return c, nil
}

// pushLocked 把一行放进环形缓冲。
func (c *Console) pushLocked(line model.LogLine) {
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
	// 一条一行：文件第 N 行必须对应第 N 条
	if strings.ContainsRune(text, '\n') {
		text = strings.ReplaceAll(text, "\n", " ")
	}
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return
	}
	c.seq++
	line := model.LogLine{Seq: c.seq, Time: time.Now().UnixMilli(), Stream: stream, Text: displayText(text)}
	c.pushLocked(line)
	if c.fileW != nil {
		if (c.seq-1)%indexStep == 0 {
			c.index = append(c.index, c.fileOff)
		}
		_, _ = c.fileW.WriteString(text)
		_ = c.fileW.WriteByte('\n')
		c.fileOff += int64(len(text)) + 1
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

// FirstSeq 返回能读到的最早一行：有日志文件时是 1，否则是内存缓冲里最早的一行（没有内容时为 0）。
func (c *Console) FirstSeq() int64 {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.path != "" && c.seq > 0 {
		return 1
	}
	if c.n == 0 {
		return 0
	}
	return c.buf[c.head].Seq
}

// Range 返回第 from 条起最多 n 条：内存缓冲里有的直接给（带输出流和时间），更早的从日志文件读（只有文本）。
func (c *Console) Range(from int64, n int) []model.LogLine {
	if from < 1 {
		from = 1
	}
	if n <= 0 {
		n = 500
	}
	c.mu.Lock()
	memFirst := int64(0)
	if c.n > 0 {
		memFirst = c.buf[c.head].Seq
	}
	var out []model.LogLine
	if c.path == "" || (memFirst > 0 && from >= memFirst) {
		for _, l := range c.sinceLocked(from - 1) {
			if len(out) >= n {
				break
			}
			out = append(out, l)
		}
		c.mu.Unlock()
		return out
	}
	if c.fileW != nil {
		_ = c.fileW.Flush()
	}
	path, last := c.path, c.seq
	k := int((from - 1) / indexStep)
	if k >= len(c.index) {
		k = len(c.index) - 1
	}
	var off int64
	if k >= 0 {
		off = c.index[k]
	} else {
		k = 0
	}
	c.mu.Unlock()

	stop := last + 1
	if memFirst > 0 {
		stop = memFirst
	}
	if f, err := os.Open(path); err == nil {
		if _, err := f.Seek(off, 0); err == nil {
			r := bufio.NewReaderSize(f, 64*1024)
			seq := int64(k)*indexStep + 1
			for seq < stop && len(out) < n {
				raw, err := r.ReadString('\n')
				if raw == "" && err != nil {
					break
				}
				if seq >= from {
					out = append(out, model.LogLine{Seq: seq, Stream: "stdout", Text: displayText(strings.TrimRight(raw, "\n"))})
				}
				seq++
				if err != nil {
					break
				}
			}
		}
		f.Close()
	}
	// 文件读到内存缓冲的起点了：剩下的从内存补
	if len(out) < n && memFirst > 0 {
		c.mu.Lock()
		for _, l := range c.sinceLocked(memFirst - 1) {
			if len(out) >= n {
				break
			}
			out = append(out, l)
		}
		c.mu.Unlock()
	}
	return out
}

// Path 返回日志文件路径。
func (c *Console) Path() string { return c.path }

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

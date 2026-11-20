package engine

import (
	"os"
	"path/filepath"
)

// 终端输出落盘：内存只留最后 termBacklog 字节，完整输出追加写进 logs/terminals/<id>.log，
// 超过 termLogMax 时转存为 .log.1（最多两个文件），客户端断开很久再回来也能看到完整过程。
// 终端关闭时删除；内核重启后终端进程不在了，启动时清理残留文件。

const termLogMax = 32 << 20

type termLog struct {
	dir       string
	f         *os.File
	start     int64 // 当前文件第一个字节的累计偏移
	prevStart int64 // .log.1 的起始偏移；-1 表示没有
}

func (e *Engine) termLogDir() string { return filepath.Join(e.Layout.Logs, "terminals") }

func (e *Engine) cleanTerminalLogs() { _ = os.RemoveAll(e.termLogDir()) }

func (l *termLog) path(id string) string { return filepath.Join(l.dir, id+".log") }

// write 在锁内调用：off 是这段输出的累计起始偏移。
func (l *termLog) write(id string, off int64, b []byte) {
	if l == nil {
		return
	}
	if l.f == nil {
		if err := os.MkdirAll(l.dir, 0o700); err != nil {
			return
		}
		f, err := os.OpenFile(l.path(id), os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600)
		if err != nil {
			return
		}
		l.f, l.start, l.prevStart = f, off, -1
	}
	if off-l.start >= termLogMax {
		_ = l.f.Close()
		_ = os.Rename(l.path(id), l.path(id)+".1")
		f, err := os.OpenFile(l.path(id), os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600)
		if err != nil {
			l.f = nil
			return
		}
		l.f, l.prevStart, l.start = f, l.start, off
	}
	_, _ = l.f.Write(b)
}

// firstOffset 是磁盘上最早还留着的字节偏移。
func (l *termLog) firstOffset() int64 {
	if l == nil || l.f == nil {
		return -1
	}
	if l.prevStart >= 0 {
		return l.prevStart
	}
	return l.start
}

// read 读 [from, to) 的输出（只读磁盘部分；调用方保证 to 不超过已写入的位置）。
func (l *termLog) read(id string, from, to int64) []byte {
	if l == nil || l.f == nil || to <= from {
		return nil
	}
	var out []byte
	readFile := func(path string, fileStart, a, b int64) {
		if b <= a {
			return
		}
		f, err := os.Open(path)
		if err != nil {
			return
		}
		defer f.Close()
		buf := make([]byte, b-a)
		n, _ := f.ReadAt(buf, a-fileStart)
		out = append(out, buf[:n]...)
	}
	if l.prevStart >= 0 && from < l.start {
		readFile(l.path(id)+".1", l.prevStart, max64(from, l.prevStart), min64(to, l.start))
	}
	readFile(l.path(id), l.start, max64(from, l.start), to)
	return out
}

func (l *termLog) close(id string) {
	if l == nil {
		return
	}
	if l.f != nil {
		_ = l.f.Close()
		l.f = nil
	}
	_ = os.Remove(l.path(id))
	_ = os.Remove(l.path(id) + ".1")
}

func max64(a, b int64) int64 {
	if a > b {
		return a
	}
	return b
}

func min64(a, b int64) int64 {
	if a < b {
		return a
	}
	return b
}

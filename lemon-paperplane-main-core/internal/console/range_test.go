package console

import (
	"fmt"
	"path/filepath"
	"testing"
)

// 内存只留 100 行，日志文件保留全部：任意一段都能读回来，内核重启后从文件恢复。
func TestRangeBeyondMemoryAndReopen(t *testing.T) {
	path := filepath.Join(t.TempDir(), "r.log")
	c := New("r", 100, path)
	for i := 1; i <= 3500; i++ {
		c.Append("stdout", fmt.Sprintf("line %d", i))
	}
	c.System("multi\nline") // 系统信息按行拆开，每行一条
	if c.FirstSeq() != 1 || c.LastSeq() != 3502 {
		t.Fatalf("first/last %d %d", c.FirstSeq(), c.LastSeq())
	}
	check := func(c *Console, from int64, n int, want []string) {
		t.Helper()
		got := c.Range(from, n)
		if len(got) != len(want) {
			t.Fatalf("Range(%d,%d) len %d want %d", from, n, len(got), len(want))
		}
		for i, l := range got {
			if l.Text != want[i] || l.Seq != from+int64(i) {
				t.Fatalf("Range(%d,%d)[%d] = %d %q want %q", from, n, i, l.Seq, l.Text, want[i])
			}
		}
	}
	check(c, 1, 2, []string{"line 1", "line 2"})
	check(c, 1999, 3, []string{"line 1999", "line 2000", "line 2001"})
	// 跨过内存起点（3403）：前半从文件、后半从内存
	check(c, 3401, 4, []string{"line 3401", "line 3402", "line 3403", "line 3404"})
	check(c, 3500, 5, []string{"line 3500", "multi", "line"})
	c.Close()

	r, err := Open("r", 100, path)
	if err != nil {
		t.Fatal(err)
	}
	if r.LastSeq() != 3502 || !r.Closed() {
		t.Fatalf("reopen last %d closed %v", r.LastSeq(), r.Closed())
	}
	check(r, 1000, 2, []string{"line 1000", "line 1001"})
	check(r, 3501, 9, []string{"multi", "line"})
	if tail := r.Tail(1); len(tail) != 1 || tail[0].Text != "line" {
		t.Fatalf("tail %+v", tail)
	}
}

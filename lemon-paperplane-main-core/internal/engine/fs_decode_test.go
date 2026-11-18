package engine

import (
	"testing"

	"golang.org/x/text/encoding/simplifiedchinese"
)

func TestDecodeAsForcedEncoding(t *testing.T) {
	gbk, _ := simplifiedchinese.GBK.NewEncoder().Bytes([]byte("中文"))
	// 自动识别会按 GB18030 解出来；强制按 latin1 重开得到的是逐字节字符
	if s, enc, _, err := decodeAs(gbk, "latin1"); err != nil || enc != "latin1" || len([]rune(s)) != len(gbk) {
		t.Fatalf("latin1: %q %s %v", s, enc, err)
	}
	if s, enc, _, err := decodeAs(gbk, "GBK"); err != nil || enc != "gbk" || s != "中文" {
		t.Fatalf("gbk: %q %s %v", s, enc, err)
	}
	if s, _, bom, err := decodeAs([]byte{0xEF, 0xBB, 0xBF, 'a'}, "utf-8"); err != nil || !bom || s != "a" {
		t.Fatalf("utf-8 bom: %q %v %v", s, bom, err)
	}
	if s, _, bom, err := decodeAs([]byte{0xFF, 0xFE, 'a', 0}, "utf-16le"); err != nil || !bom || s != "a" {
		t.Fatalf("utf-16le: %q %v %v", s, bom, err)
	}
	if _, _, _, err := decodeAs([]byte("x"), "ebcdic"); err == nil {
		t.Fatal("unknown encoding should fail")
	}
}

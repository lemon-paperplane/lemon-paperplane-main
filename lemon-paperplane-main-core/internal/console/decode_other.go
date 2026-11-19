//go:build !windows

package console

func decodeLine(b []byte) string { return string(b) }

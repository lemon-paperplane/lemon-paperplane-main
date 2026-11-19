//go:build !windows

package ops

func installCLIWindows(exe string) (any, error) { return nil, nil }

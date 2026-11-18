// Package archive 提供下载与解压工具，供内核与开发探针共用。
package archive

import (
	"archive/tar"
	"archive/zip"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// ProgressFunc 汇报下载进度：已下载字节、总字节（未知为 -1）。
type ProgressFunc func(done, total int64)

// Download 依次尝试 urls，下载到 dest（先写 .part 再改名），校验 sha256（为空不校验）。返回成功的 url。
func Download(ctx context.Context, urls []string, dest, sha string, progress ProgressFunc) (string, error) {
	if err := os.MkdirAll(filepath.Dir(dest), 0o755); err != nil {
		return "", err
	}
	var errs []string
	for _, u := range urls {
		if u == "" {
			continue
		}
		err := downloadOne(ctx, u, dest, sha, progress)
		if err == nil {
			return u, nil
		}
		if ctx.Err() != nil {
			return "", ctx.Err()
		}
		errs = append(errs, fmt.Sprintf("%s: %v", u, err))
	}
	return "", errors.New(strings.Join(errs, "; "))
}

func downloadOne(ctx context.Context, url, dest, sha string, progress ProgressFunc) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	// 不设自定义 UA：曾用 "LemonPaperplane"，腾讯云 EdgeOne 等 CDN 会把它当机器人返回 200 的 JS 挑战页（下到的是网页不是文件）
	client := &http.Client{Timeout: 0, Transport: &http.Transport{Proxy: http.ProxyFromEnvironment, ResponseHeaderTimeout: 30 * time.Second}}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	// 一些镜像对非浏览器客户端返回 200 的反爬页面
	if ct := strings.ToLower(resp.Header.Get("Content-Type")); strings.HasPrefix(ct, "text/html") && !strings.HasSuffix(strings.ToLower(dest), ".html") {
		return fmt.Errorf("unexpected HTML response (mirror blocked the download)")
	}
	part := dest + ".part"
	f, err := os.Create(part)
	if err != nil {
		return err
	}
	h := sha256.New()
	total := resp.ContentLength
	var done int64
	buf := make([]byte, 256*1024)
	last := time.Now()
	for {
		n, rerr := resp.Body.Read(buf)
		if n > 0 {
			if _, werr := f.Write(buf[:n]); werr != nil {
				f.Close()
				return werr
			}
			h.Write(buf[:n])
			done += int64(n)
			if progress != nil && time.Since(last) > 200*time.Millisecond {
				progress(done, total)
				last = time.Now()
			}
		}
		if rerr == io.EOF {
			break
		}
		if rerr != nil {
			f.Close()
			os.Remove(part)
			return rerr
		}
	}
	if progress != nil {
		progress(done, total)
	}
	if err := f.Close(); err != nil {
		return err
	}
	if sha != "" && !strings.EqualFold(hex.EncodeToString(h.Sum(nil)), sha) {
		os.Remove(part)
		return fmt.Errorf("sha256 mismatch")
	}
	return os.Rename(part, dest)
}

// Extract 解压 archive（按 kind 或扩展名：tar.gz/tgz、tar.xz（调用系统 tar）、zip）到 dest，去掉前 strip 层目录。
func Extract(file, dest, kind string, strip int) error {
	if kind == "" {
		switch {
		case strings.HasSuffix(file, ".tar.gz"), strings.HasSuffix(file, ".tgz"):
			kind = "tar.gz"
		case strings.HasSuffix(file, ".tar.xz"):
			kind = "tar.xz"
		case strings.HasSuffix(file, ".zip"):
			kind = "zip"
		}
	}
	if err := os.MkdirAll(dest, 0o755); err != nil {
		return err
	}
	switch kind {
	case "tar.gz", "tgz":
		f, err := os.Open(file)
		if err != nil {
			return err
		}
		defer f.Close()
		gz, err := gzip.NewReader(f)
		if err != nil {
			return err
		}
		return untar(gz, dest, strip)
	case "tar.xz":
		return extractWithSystemTar(file, dest, strip)
	case "zip":
		return unzip(file, dest, strip)
	}
	return fmt.Errorf("unknown archive kind %q", kind)
}

func stripPath(name string, strip int) (string, bool) {
	name = strings.TrimPrefix(filepath.ToSlash(name), "./")
	parts := strings.Split(name, "/")
	if len(parts) <= strip {
		return "", false
	}
	rel := strings.Join(parts[strip:], "/")
	if rel == "" {
		return "", false
	}
	return rel, true
}

func safeJoin(dest, rel string) (string, error) {
	p := filepath.Join(dest, filepath.FromSlash(rel))
	if !strings.HasPrefix(p, filepath.Clean(dest)+string(filepath.Separator)) && p != filepath.Clean(dest) {
		return "", fmt.Errorf("illegal path %q", rel)
	}
	return p, nil
}

func untar(r io.Reader, dest string, strip int) error {
	tr := tar.NewReader(r)
	type link struct{ path, target string }
	var hardlinks []link
	for {
		hdr, err := tr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return err
		}
		rel, ok := stripPath(hdr.Name, strip)
		if !ok {
			continue
		}
		p, err := safeJoin(dest, rel)
		if err != nil {
			return err
		}
		switch hdr.Typeflag {
		case tar.TypeDir:
			if err := os.MkdirAll(p, 0o755); err != nil {
				return err
			}
		case tar.TypeReg, tar.TypeRegA:
			if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
				return err
			}
			f, err := os.OpenFile(p, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, os.FileMode(hdr.Mode)&0o777|0o200)
			if err != nil {
				return err
			}
			if _, err := io.Copy(f, tr); err != nil {
				f.Close()
				return err
			}
			f.Close()
		case tar.TypeSymlink:
			if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
				return err
			}
			_ = os.Remove(p)
			if err := os.Symlink(hdr.Linkname, p); err != nil {
				return err
			}
		case tar.TypeLink:
			trel, ok := stripPath(hdr.Linkname, strip)
			if ok {
				hardlinks = append(hardlinks, link{p, trel})
			}
		}
	}
	for _, l := range hardlinks {
		t, err := safeJoin(dest, l.target)
		if err != nil {
			continue
		}
		_ = os.Remove(l.path)
		if err := os.Link(t, l.path); err != nil {
			// 回退为复制
			if b, rerr := os.ReadFile(t); rerr == nil {
				_ = os.WriteFile(l.path, b, 0o755)
			}
		}
	}
	return nil
}

func unzip(file, dest string, strip int) error {
	zr, err := zip.OpenReader(file)
	if err != nil {
		return err
	}
	defer zr.Close()
	for _, zf := range zr.File {
		rel, ok := stripPath(zf.Name, strip)
		if !ok {
			continue
		}
		p, err := safeJoin(dest, rel)
		if err != nil {
			return err
		}
		if zf.FileInfo().IsDir() {
			if err := os.MkdirAll(p, 0o755); err != nil {
				return err
			}
			continue
		}
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			return err
		}
		mode := zf.Mode()
		if mode&os.ModeSymlink != 0 {
			rc, err := zf.Open()
			if err != nil {
				return err
			}
			target, _ := io.ReadAll(rc)
			rc.Close()
			_ = os.Remove(p)
			if err := os.Symlink(string(target), p); err != nil {
				return err
			}
			continue
		}
		rc, err := zf.Open()
		if err != nil {
			return err
		}
		perm := mode.Perm() | 0o200
		if perm&0o444 == 0 {
			perm |= 0o644
		}
		f, err := os.OpenFile(p, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, perm)
		if err != nil {
			rc.Close()
			return err
		}
		_, err = io.Copy(f, rc)
		rc.Close()
		f.Close()
		if err != nil {
			return err
		}
	}
	return nil
}

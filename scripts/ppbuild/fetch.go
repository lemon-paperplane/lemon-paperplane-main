package main

import (
	"archive/zip"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// cmdFetchPlugins：插件仓库和主仓库并排放（<root>/lemon-paperplane-plugin-*，插件的 go.mod 用
// ../lemon-paperplane-main 引用 SDK）。只拷了主仓库时，从 GitHub 下载全部公开的插件仓库放到旁边。
// 已经有插件源码时什么都不做。
func cmdFetchPlugins(args []string) error {
	var p paths
	var org string
	fs := flag.NewFlagSet("fetch-plugins", flag.ExitOnError)
	p.register(fs)
	fs.StringVar(&org, "org", "lemon-paperplane", "GitHub 组织")
	_ = fs.Parse(args)
	if err := p.resolve(); err != nil {
		return err
	}
	if have := p.pluginDirs(); len(have) > 0 {
		fmt.Printf("插件源码 %d 个（%s）\n", len(have), p.root)
		return nil
	}
	if filepath.Base(p.main) != "lemon-paperplane-main" {
		fmt.Fprintf(os.Stderr, "warning: 主仓库目录名不是 lemon-paperplane-main（%s），插件的 go.mod 找不到 SDK，商店插件会编译失败\n", filepath.Base(p.main))
	}
	repos, err := listRepos(org)
	if err != nil {
		return fmt.Errorf("列出 GitHub 上的插件仓库失败（%v）。可以把插件仓库（%s*）拷到 %s 下再构建", err, pluginPrefix, p.root)
	}
	if len(repos) == 0 {
		return fmt.Errorf("GitHub 组织 %s 下没有 %s* 仓库", org, pluginPrefix)
	}
	fmt.Printf("从 GitHub 下载 %d 个插件仓库 → %s\n", len(repos), p.root)
	for _, r := range repos {
		url := fmt.Sprintf("https://codeload.github.com/%s/%s/zip/refs/heads/%s", org, r.Name, r.Branch)
		var err error
		for attempt := 1; attempt <= 4; attempt++ {
			if err = downloadRepo(url, filepath.Join(p.root, r.Name)); err == nil {
				break
			}
			fmt.Fprintf(os.Stderr, "  %s 第 %d 次失败：%v\n", r.Name, attempt, err)
			time.Sleep(time.Duration(attempt) * 2 * time.Second)
		}
		if err != nil {
			return fmt.Errorf("下载 %s 失败：%w", r.Name, err)
		}
		fmt.Printf("  %s\n", r.Name)
	}
	return nil
}

type repo struct{ Name, Branch string }

var httpClient = &http.Client{Timeout: 10 * time.Minute}

func listRepos(org string) ([]repo, error) {
	var out []repo
	for page := 1; page <= 10; page++ {
		req, _ := http.NewRequest("GET", fmt.Sprintf("https://api.github.com/orgs/%s/repos?type=public&per_page=100&page=%d", org, page), nil)
		req.Header.Set("Accept", "application/vnd.github+json")
		req.Header.Set("User-Agent", "lemon-paperplane-build")
		if t := os.Getenv("GITHUB_TOKEN"); t != "" {
			req.Header.Set("Authorization", "Bearer "+t)
		}
		resp, err := httpClient.Do(req)
		if err != nil {
			return nil, err
		}
		var items []struct {
			Name          string `json:"name"`
			DefaultBranch string `json:"default_branch"`
			Archived      bool   `json:"archived"`
		}
		err = json.NewDecoder(resp.Body).Decode(&items)
		resp.Body.Close()
		if resp.StatusCode != 200 {
			return nil, fmt.Errorf("GitHub API %s", resp.Status)
		}
		if err != nil {
			return nil, err
		}
		for _, it := range items {
			if strings.HasPrefix(it.Name, pluginPrefix) && !it.Archived {
				b := it.DefaultBranch
				if b == "" {
					b = "main"
				}
				out = append(out, repo{it.Name, b})
			}
		}
		if len(items) < 100 {
			break
		}
	}
	return out, nil
}

// downloadRepo：下载仓库 zip，去掉最外层目录后解压到 dst。
func downloadRepo(url, dst string) error {
	req, _ := http.NewRequest("GET", url, nil)
	req.Header.Set("User-Agent", "lemon-paperplane-build")
	resp, err := httpClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return fmt.Errorf("%s：%s", url, resp.Status)
	}
	tmp, err := os.CreateTemp("", "ppbuild-repo-*.zip")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	if _, err := io.Copy(tmp, resp.Body); err != nil {
		tmp.Close()
		return err
	}
	tmp.Close()
	zr, err := zip.OpenReader(tmp.Name())
	if err != nil {
		return err
	}
	defer zr.Close()
	part := dst + ".part"
	_ = os.RemoveAll(part)
	for _, f := range zr.File {
		name := f.Name
		if i := strings.Index(name, "/"); i >= 0 {
			name = name[i+1:]
		} else {
			continue
		}
		if name == "" || strings.Contains(name, "..") {
			continue
		}
		t := filepath.Join(part, filepath.FromSlash(name))
		if f.FileInfo().IsDir() {
			if err := os.MkdirAll(t, 0o755); err != nil {
				return err
			}
			continue
		}
		if err := os.MkdirAll(filepath.Dir(t), 0o755); err != nil {
			return err
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		mode := f.Mode().Perm() | 0o600
		out, err := os.OpenFile(t, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, mode)
		if err != nil {
			rc.Close()
			return err
		}
		_, err = io.Copy(out, rc)
		rc.Close()
		if cerr := out.Close(); err == nil {
			err = cerr
		}
		if err != nil {
			return err
		}
	}
	_ = os.RemoveAll(dst)
	return os.Rename(part, dst)
}

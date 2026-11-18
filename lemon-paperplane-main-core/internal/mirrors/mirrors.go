// Package mirrors 决定镜像区域并给出镜像地址与环境变量。
package mirrors

import (
	"context"
	"net/http"
	"os"
	"runtime"
	"strings"
	"sync"
	"time"

	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// China 是国内镜像集合。
func China() sdk.Mirrors {
	return sdk.Mirrors{
		Region:             "china",
		NpmRegistry:        "https://registry.npmmirror.com",
		NodeDist:           "https://cdn.npmmirror.com/binaries/node",
		MavenCentral:       "https://maven.aliyun.com/repository/public",
		GradlePluginPortal: "https://maven.aliyun.com/repository/gradle-plugin",
		// 腾讯云 Gradle 镜像对非浏览器客户端返回反爬页面，用华为云（目录结构与官方一致）
		GradleDist:  "https://mirrors.huaweicloud.com/gradle",
		GoogleMaven: "https://maven.aliyun.com/repository/google",
		PypiIndex:   "https://mirrors.aliyun.com/pypi/simple/",
		PythonDist:  "https://registry.npmmirror.com/-/binary/python-build-standalone",
		GoProxy:     "https://goproxy.cn,direct",
		GoSumDB:     "sum.golang.google.cn",
		GoDist:      "https://mirrors.aliyun.com/golang",
		JdkAdoptium: "https://mirrors.tuna.tsinghua.edu.cn/Adoptium",
	}
}

// Global 表示全部用官方源。
func Global() sdk.Mirrors { return sdk.Mirrors{Region: "global"} }

// Env 返回所有进程都应注入的镜像环境变量（npm / yarn / pnpm / corepack / pip / uv / go）。
func Env(m sdk.Mirrors) map[string]string {
	env := map[string]string{}
	if m.NpmRegistry != "" {
		env["npm_config_registry"] = m.NpmRegistry
		env["NPM_CONFIG_REGISTRY"] = m.NpmRegistry
		env["YARN_REGISTRY"] = m.NpmRegistry
		env["YARN_NPM_REGISTRY_SERVER"] = m.NpmRegistry
		env["COREPACK_NPM_REGISTRY"] = m.NpmRegistry
	}
	if m.NodeDist != "" {
		// node-gyp 下载头文件、各类二进制包（sharp/electron 等）走镜像
		env["NODEJS_ORG_MIRROR"] = m.NodeDist
		env["NVM_NODEJS_ORG_MIRROR"] = m.NodeDist
	}
	if m.PypiIndex != "" {
		env["PIP_INDEX_URL"] = m.PypiIndex
		env["UV_DEFAULT_INDEX"] = m.PypiIndex
		env["UV_INDEX_URL"] = m.PypiIndex
	}
	if m.PythonDist != "" {
		env["UV_PYTHON_INSTALL_MIRROR"] = m.PythonDist
	}
	if m.GoProxy != "" {
		env["GOPROXY"] = m.GoProxy
	}
	if m.GoSumDB != "" {
		env["GOSUMDB"] = m.GoSumDB
	}
	return env
}

// InChina 根据时区判断是否在中国大陆。
func InChina() bool {
	tz := os.Getenv("TZ")
	if tz == "" {
		if l, err := os.Readlink("/etc/localtime"); err == nil {
			tz = l
		}
	}
	if tz == "" {
		tz = time.Now().Location().String()
	}
	for _, z := range []string{"Asia/Shanghai", "Asia/Chongqing", "Asia/Harbin", "Asia/Urumqi", "PRC", "Asia/Chungking"} {
		if strings.Contains(tz, z) {
			return true
		}
	}
	_, off := time.Now().Zone()
	return runtime.GOOS == "windows" && off == 8*3600
}

// Probe 是一次测速结果。
type Probe struct {
	Name string `json:"name"`
	URL  string `json:"url"`
	MS   int64  `json:"ms"`
	OK   bool   `json:"ok"`
}

// Status 是 mirrors.status 的返回。
type Status struct {
	Configured string    `json:"configured"`
	Effective  string    `json:"effective"`
	Probes     []Probe   `json:"probes"`
	At         time.Time `json:"at,omitempty"`
}

// Detector 用测速决定 auto 模式下的区域，结果缓存 24 小时。
type Detector struct {
	mu     sync.Mutex
	region string
	probes []Probe
	at     time.Time
}

var probeTargets = []struct{ name, url string }{
	{"npm (official)", "https://registry.npmjs.org/-/ping"},
	{"npm (npmmirror)", "https://registry.npmmirror.com/-/ping"},
	{"maven (central)", "https://repo.maven.apache.org/maven2/"},
	{"maven (aliyun)", "https://maven.aliyun.com/repository/public/"},
	{"google", "https://www.google.com/generate_204"},
}

// Seed 用上次保存的测速结果预置缓存（启动时不必再等测速）。
func (d *Detector) Seed(region string, probes []Probe, at time.Time) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.region == "" {
		d.region, d.probes, d.at = region, probes, at
	}
}

// Snapshot 返回当前缓存的测速结果。
func (d *Detector) Snapshot() (region string, probes []Probe, at time.Time) {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.region, d.probes, d.at
}

// Region 返回 auto 模式下的区域（首次调用会测速，最多约 3 秒）。
func (d *Detector) Region(ctx context.Context, force bool) (string, []Probe) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if !force && d.region != "" && time.Since(d.at) < 24*time.Hour {
		return d.region, d.probes
	}
	d.probes = probeAll(ctx)
	official, mirror, google := d.probes[0], d.probes[1], d.probes[4]
	region := "global"
	switch {
	case !mirror.OK:
	case !google.OK:
		region = "china"
	case InChina():
		// 国内用户即使能翻墙，大文件走国内镜像通常也更快更稳；官方源明显更快时才用官方
		if !official.OK || official.MS*2 > mirror.MS {
			region = "china"
		}
	case !official.OK || official.MS > 3*mirror.MS+300:
		region = "china"
	}
	d.region, d.at = region, time.Now()
	return region, d.probes
}

func probeAll(ctx context.Context) []Probe {
	out := make([]Probe, len(probeTargets))
	var wg sync.WaitGroup
	client := &http.Client{Timeout: 3 * time.Second, Transport: &http.Transport{Proxy: http.ProxyFromEnvironment}}
	for i, t := range probeTargets {
		wg.Add(1)
		go func(i int, name, url string) {
			defer wg.Done()
			start := time.Now()
			req, _ := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
			resp, err := client.Do(req)
			p := Probe{Name: name, URL: url, MS: time.Since(start).Milliseconds()}
			if err == nil {
				resp.Body.Close()
				p.OK = resp.StatusCode < 500
			}
			out[i] = p
		}(i, t.name, t.url)
	}
	wg.Wait()
	return out
}

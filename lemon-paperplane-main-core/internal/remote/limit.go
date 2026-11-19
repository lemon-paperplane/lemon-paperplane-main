package remote

import (
	"sync"
	"time"
)

// 防暴力破解：每个 IP 15 分钟内认证失败 5 次封禁 15 分钟，再犯翻倍，最长 24 小时；
// 全局登录尝试每分钟最多 30 次；同时最多 2 个密码哈希在算（每个占 19 MiB）。
const (
	failWindow    = 15 * time.Minute
	failThreshold = 5
	banBase       = 15 * time.Minute
	banMax        = 24 * time.Hour
	loginPerMin   = 30
)

type ipState struct {
	fails       []time.Time
	bannedUntil time.Time
	bans        int
	lastSeen    time.Time
}

type limiter struct {
	mu     sync.Mutex
	ips    map[string]*ipState
	logins []time.Time
	hashes chan struct{}
	now    func() time.Time
}

func newLimiter() *limiter {
	return &limiter{ips: map[string]*ipState{}, hashes: make(chan struct{}, 2), now: time.Now}
}

func (l *limiter) state(ip string) *ipState {
	s := l.ips[ip]
	if s == nil {
		s = &ipState{}
		l.ips[ip] = s
	}
	s.lastSeen = l.now()
	return s
}

// banned 返回这个 IP 是否被封禁及剩余时间。
func (l *limiter) banned(ip string) (bool, time.Duration) {
	l.mu.Lock()
	defer l.mu.Unlock()
	s := l.ips[ip]
	if s == nil {
		return false, 0
	}
	if d := s.bannedUntil.Sub(l.now()); d > 0 {
		return true, d
	}
	return false, 0
}

// fail 记一次认证失败；达到阈值时封禁，返回本次是否刚被封禁和封禁时长。
func (l *limiter) fail(ip string) (bool, time.Duration) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.now()
	s := l.state(ip)
	kept := s.fails[:0]
	for _, t := range s.fails {
		if now.Sub(t) < failWindow {
			kept = append(kept, t)
		}
	}
	s.fails = append(kept, now)
	if len(s.fails) < failThreshold {
		return false, 0
	}
	s.fails = nil
	s.bans++
	d := banBase << (s.bans - 1)
	if d > banMax || d <= 0 {
		d = banMax
	}
	s.bannedUntil = now.Add(d)
	l.gc(now)
	return true, d
}

// success 清掉这个 IP 的失败计数（封禁次数保留，再犯还是翻倍）。
func (l *limiter) success(ip string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	if s := l.ips[ip]; s != nil {
		s.fails = nil
	}
}

// allowLogin：全局登录频率。
func (l *limiter) allowLogin() bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.now()
	kept := l.logins[:0]
	for _, t := range l.logins {
		if now.Sub(t) < time.Minute {
			kept = append(kept, t)
		}
	}
	l.logins = kept
	if len(l.logins) >= loginPerMin {
		return false
	}
	l.logins = append(l.logins, now)
	return true
}

// gc 清理很久没出现、也没在封禁中的 IP（防止记录无限增长）。
func (l *limiter) gc(now time.Time) {
	if len(l.ips) < 4096 {
		return
	}
	for ip, s := range l.ips {
		if now.After(s.bannedUntil) && now.Sub(s.lastSeen) > banMax {
			delete(l.ips, ip)
		}
	}
}

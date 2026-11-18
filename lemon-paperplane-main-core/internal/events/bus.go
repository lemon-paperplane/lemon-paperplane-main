// Package events 是进程内的发布订阅总线，供 SSE 推送。
package events

import (
	"sync"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
)

type Bus struct {
	mu   sync.RWMutex
	subs map[chan model.Event]struct{}
}

func New() *Bus { return &Bus{subs: map[chan model.Event]struct{}{}} }

// Subscribe 返回事件通道与取消函数。慢订阅者会丢事件（界面收到后会按需重新拉取）。
func (b *Bus) Subscribe() (<-chan model.Event, func()) {
	ch := make(chan model.Event, 256)
	b.mu.Lock()
	b.subs[ch] = struct{}{}
	b.mu.Unlock()
	return ch, func() {
		b.mu.Lock()
		if _, ok := b.subs[ch]; ok {
			delete(b.subs, ch)
			close(ch)
		}
		b.mu.Unlock()
	}
}

// Publish 广播事件，不阻塞。
func (b *Bus) Publish(typ string, data any) {
	ev := model.Event{Type: typ, Data: data}
	b.mu.RLock()
	defer b.mu.RUnlock()
	for ch := range b.subs {
		select {
		case ch <- ev:
		default:
		}
	}
}

// Subscribers 返回当前订阅数（判断是否有界面连着）。
func (b *Bus) Subscribers() int {
	b.mu.RLock()
	defer b.mu.RUnlock()
	return len(b.subs)
}

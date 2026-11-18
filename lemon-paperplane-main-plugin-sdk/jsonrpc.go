package sdk

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
)

// JSON-RPC 错误码。
const (
	ErrCodeParse          = -32700
	ErrCodeInvalidRequest = -32600
	ErrCodeMethodNotFound = -32601
	ErrCodeInvalidParams  = -32602
	ErrCodeInternal       = -32603
	ErrCodeCancelled      = -32800 // 与 LSP 相同
	ErrCodeNotSupported   = -33001 // 插件不支持该操作/该 kind
	ErrCodeUserError      = -33002 // 可展示给用户的业务错误（Message 已本地化）
)

// Error 是 JSON-RPC 错误对象。
type Error struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    any    `json:"data,omitempty"`
}

func (e *Error) Error() string { return fmt.Sprintf("jsonrpc %d: %s", e.Code, e.Message) }

// NewError 构造一个 JSON-RPC 错误。
func NewError(code int, format string, args ...any) *Error {
	return &Error{Code: code, Message: fmt.Sprintf(format, args...)}
}

// UserError 返回一条会原样展示给用户的错误。
func UserError(format string, args ...any) *Error {
	return NewError(ErrCodeUserError, format, args...)
}

// ErrNotSupported 表示插件不支持请求的操作。
var ErrNotSupported = &Error{Code: ErrCodeNotSupported, Message: "not supported"}

// IsCode 判断 err 是否为指定错误码的 JSON-RPC 错误。
func IsCode(err error, code int) bool {
	var e *Error
	return errors.As(err, &e) && e.Code == code
}

// Handler 处理对端发来的请求或通知。通知的返回值被忽略。
type Handler func(ctx context.Context, method string, params json.RawMessage) (any, error)

type wireMessage struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id,omitempty"`
	Method  string          `json:"method,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
	Result  json.RawMessage `json:"result,omitempty"`
	Error   *Error          `json:"error,omitempty"`
}

type callResult struct {
	result json.RawMessage
	err    error
}

// Conn 是一条双向 JSON-RPC 2.0 连接，帧格式同 LSP（Content-Length）。
// 请求并发处理；Call 的 ctx 取消时自动向对端发送 $/cancelRequest。
type Conn struct {
	r       *bufio.Reader
	w       io.Writer
	wmu     sync.Mutex
	handler Handler

	nextID   atomic.Int64
	mu       sync.Mutex
	pending  map[int64]chan callResult
	incoming map[string]context.CancelFunc

	closeOnce sync.Once
	done      chan struct{}
	err       error
}

// NewConn 创建连接。调用 Run 开始读循环。
func NewConn(r io.Reader, w io.Writer, h Handler) *Conn {
	return &Conn{
		r:        bufio.NewReaderSize(r, 64*1024),
		w:        w,
		handler:  h,
		pending:  map[int64]chan callResult{},
		incoming: map[string]context.CancelFunc{},
		done:     make(chan struct{}),
	}
}

// Done 在连接关闭后关闭。
func (c *Conn) Done() <-chan struct{} { return c.done }

// Err 返回连接关闭原因。
func (c *Conn) Err() error { return c.err }

// Run 读取并分发消息，直到对端关闭或出错。
func (c *Conn) Run() error {
	for {
		body, err := c.readFrame()
		if err != nil {
			c.shutdown(err)
			if errors.Is(err, io.EOF) {
				return nil
			}
			return err
		}
		var msg wireMessage
		if err := json.Unmarshal(body, &msg); err != nil {
			continue
		}
		switch {
		case msg.Method != "" && len(msg.ID) > 0:
			c.handleRequest(msg)
		case msg.Method != "":
			c.handleNotification(msg)
		case len(msg.ID) > 0:
			c.handleResponse(msg)
		}
	}
}

func (c *Conn) readFrame() ([]byte, error) {
	length := -1
	for {
		line, err := c.r.ReadString('\n')
		if err != nil {
			return nil, err
		}
		line = strings.TrimRight(line, "\r\n")
		if line == "" {
			if length < 0 {
				continue
			}
			break
		}
		if k, v, ok := strings.Cut(line, ":"); ok && strings.EqualFold(strings.TrimSpace(k), "Content-Length") {
			n, err := strconv.Atoi(strings.TrimSpace(v))
			if err != nil {
				return nil, fmt.Errorf("bad Content-Length: %q", v)
			}
			length = n
		}
	}
	buf := make([]byte, length)
	if _, err := io.ReadFull(c.r, buf); err != nil {
		return nil, err
	}
	return buf, nil
}

func (c *Conn) write(msg *wireMessage) error {
	msg.JSONRPC = "2.0"
	b, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	c.wmu.Lock()
	defer c.wmu.Unlock()
	select {
	case <-c.done:
		return io.ErrClosedPipe
	default:
	}
	if _, err := fmt.Fprintf(c.w, "Content-Length: %d\r\n\r\n", len(b)); err != nil {
		return err
	}
	_, err = c.w.Write(b)
	return err
}

func (c *Conn) handleRequest(msg wireMessage) {
	ctx, cancel := context.WithCancel(context.Background())
	key := string(msg.ID)
	c.mu.Lock()
	c.incoming[key] = cancel
	c.mu.Unlock()
	go func() {
		defer func() {
			c.mu.Lock()
			delete(c.incoming, key)
			c.mu.Unlock()
			cancel()
		}()
		var result any
		var err error
		func() {
			defer func() {
				if r := recover(); r != nil {
					err = NewError(ErrCodeInternal, "panic: %v", r)
				}
			}()
			result, err = c.handler(ctx, msg.Method, msg.Params)
		}()
		resp := &wireMessage{ID: msg.ID}
		if err != nil {
			var e *Error
			if !errors.As(err, &e) {
				if ctx.Err() != nil {
					e = &Error{Code: ErrCodeCancelled, Message: "cancelled"}
				} else {
					e = &Error{Code: ErrCodeInternal, Message: err.Error()}
				}
			}
			resp.Error = e
		} else {
			b, merr := json.Marshal(result)
			if merr != nil {
				resp.Error = &Error{Code: ErrCodeInternal, Message: merr.Error()}
			} else {
				resp.Result = b
			}
		}
		_ = c.write(resp)
	}()
}

func (c *Conn) handleNotification(msg wireMessage) {
	if msg.Method == MethodCancelRequest {
		var p struct {
			ID json.RawMessage `json:"id"`
		}
		if json.Unmarshal(msg.Params, &p) == nil {
			c.mu.Lock()
			cancel := c.incoming[string(p.ID)]
			c.mu.Unlock()
			if cancel != nil {
				cancel()
			}
		}
		return
	}
	go func() {
		defer func() { _ = recover() }()
		_, _ = c.handler(context.Background(), msg.Method, msg.Params)
	}()
}

func (c *Conn) handleResponse(msg wireMessage) {
	id, err := strconv.ParseInt(string(msg.ID), 10, 64)
	if err != nil {
		return
	}
	c.mu.Lock()
	ch := c.pending[id]
	delete(c.pending, id)
	c.mu.Unlock()
	if ch == nil {
		return
	}
	if msg.Error != nil {
		ch <- callResult{err: msg.Error}
	} else {
		ch <- callResult{result: msg.Result}
	}
}

// Call 发送请求并等待结果，result 可为 nil。
func (c *Conn) Call(ctx context.Context, method string, params any, result any) error {
	id := c.nextID.Add(1)
	ch := make(chan callResult, 1)
	c.mu.Lock()
	c.pending[id] = ch
	c.mu.Unlock()
	raw, err := marshalParams(params)
	if err != nil {
		return err
	}
	idRaw := json.RawMessage(strconv.FormatInt(id, 10))
	if err := c.write(&wireMessage{ID: idRaw, Method: method, Params: raw}); err != nil {
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		return err
	}
	select {
	case r := <-ch:
		if r.err != nil {
			return r.err
		}
		if result != nil && len(r.result) > 0 && string(r.result) != "null" {
			return json.Unmarshal(r.result, result)
		}
		return nil
	case <-ctx.Done():
		c.mu.Lock()
		delete(c.pending, id)
		c.mu.Unlock()
		_ = c.Notify(MethodCancelRequest, map[string]any{"id": idRaw})
		return ctx.Err()
	case <-c.done:
		if c.err != nil {
			return c.err
		}
		return io.ErrClosedPipe
	}
}

// Notify 发送通知（不等待结果）。
func (c *Conn) Notify(method string, params any) error {
	raw, err := marshalParams(params)
	if err != nil {
		return err
	}
	return c.write(&wireMessage{Method: method, Params: raw})
}

// Close 关闭连接，所有等待中的调用返回错误。
func (c *Conn) Close() { c.shutdown(io.ErrClosedPipe) }

func (c *Conn) shutdown(err error) {
	c.closeOnce.Do(func() {
		if err != nil && !errors.Is(err, io.EOF) {
			c.err = err
		} else {
			c.err = io.ErrClosedPipe
		}
		close(c.done)
		c.mu.Lock()
		for id, ch := range c.pending {
			ch <- callResult{err: c.err}
			delete(c.pending, id)
		}
		for _, cancel := range c.incoming {
			cancel()
		}
		c.mu.Unlock()
	})
}

func marshalParams(params any) (json.RawMessage, error) {
	if params == nil {
		return nil, nil
	}
	if raw, ok := params.(json.RawMessage); ok {
		return raw, nil
	}
	return json.Marshal(params)
}

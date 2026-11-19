using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;

namespace PaperplaneKit;

/// <summary>一条 Server-Sent Events 消息。</summary>
public sealed record SSEMessage(string? Event, string Data, string? Id = null);

/// <summary>按行喂入的 SSE 解析器（不含换行符）。空行结束一条消息；以 ":" 开头的是注释（如 ": ping"）。非线程安全。</summary>
public sealed class SSEParser
{
    private string? _event;
    private readonly List<string> _data = new();
    private string? _id;
    private bool _hasField;

    /// <summary>返回完整消息（遇到空行时），或 null。</summary>
    public SSEMessage? Feed(string line)
    {
        if (line.Length == 0)
        {
            SSEMessage? msg = null;
            if (_hasField && !(_data.Count == 0 && _event == null))
                msg = new SSEMessage(_event, string.Join("\n", _data), _id);
            _event = null;
            _data.Clear();
            _hasField = false;
            return msg;
        }
        if (line[0] == ':') return null;
        string field, value;
        var colon = line.IndexOf(':');
        if (colon >= 0)
        {
            field = line.Substring(0, colon);
            value = line.Substring(colon + 1);
            if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);
        }
        else
        {
            field = line;
            value = "";
        }
        switch (field)
        {
            case "data": _data.Add(value); _hasField = true; break;
            case "event": _event = value; _hasField = true; break;
            case "id": _id = value; break;
        }
        return null;
    }
}

/// <summary>按字节块切行（处理 \r\n 与跨块的半行；按 UTF-8 解码，非法字节换成 U+FFFD）。非线程安全。</summary>
public sealed class LineSplitter
{
    private byte[] _buf = new byte[4096];
    private int _len;
    private int _scan; // _scan 之前没有换行符

    public List<string> Append(byte[] chunk) => Append(chunk, 0, chunk.Length);

    public List<string> Append(byte[] chunk, int offset, int count)
    {
        if (_len + count > _buf.Length)
        {
            var nb = new byte[Math.Max(_buf.Length * 2, _len + count)];
            Buffer.BlockCopy(_buf, 0, nb, 0, _len);
            _buf = nb;
        }
        Buffer.BlockCopy(chunk, offset, _buf, _len, count);
        _len += count;

        var lines = new List<string>();
        var start = 0;
        var nl = Array.IndexOf(_buf, (byte)'\n', _scan, _len - _scan);
        while (nl >= 0)
        {
            var end = nl;
            if (end > start && _buf[end - 1] == (byte)'\r') end--;
            lines.Add(Encoding.UTF8.GetString(_buf, start, end - start));
            start = nl + 1;
            nl = start < _len ? Array.IndexOf(_buf, (byte)'\n', start, _len - start) : -1;
        }
        if (start > 0)
        {
            Buffer.BlockCopy(_buf, start, _buf, 0, _len - start);
            _len -= start;
        }
        _scan = _len;
        return lines;
    }
}

/// <summary>
/// 自动重连的 SSE 连接（对应 Mac 的 SSEConnection）。一个连接一个后台线程，用 HttpWebRequest 阻塞读（停止时 Abort）。
/// <list type="bullet">
/// <item>回调都在这个后台线程上串行执行；界面层自己切回 UI 线程。回调里抛的异常会被吞掉（记到 Trace），不影响连接。</item>
/// <item>断线后指数退避重连（0.5s 起，最长 15s），连上（HTTP 200）后重置退避；IdleTimeout（默认 45s）没有任何字节视为断线（daemon 每 15s 发 ping）。</item>
/// <item>收到 endEvent（控制台流是 "end"）后：先回调这条消息，再停止并回调 Closed(null)，不再重连。</item>
/// <item>非 200 响应：endOnStatus 返回 true 时同 endEvent（如终端已关闭的 404），否则当作断线重连。</item>
/// <item>一次性：Stop 之后不能再 Start，要重连就新建一个。</item>
/// </list>
/// </summary>
public sealed class SSEConnection
{
    public abstract record Event
    {
        private Event() { }
        /// <summary>连上了（HTTP 200）</summary>
        public sealed record Open : Event;
        public sealed record Message(SSEMessage Value) : Event;
        /// <summary>断开。Reason 为 null 表示正常结束（endEvent / endOnStatus），不会再重连。</summary>
        public sealed record Closed(string? Reason) : Event;
    }

    private readonly Func<HttpWebRequest> _makeRequest;
    private readonly Action<Event> _onEvent;
    private readonly bool _reconnect;
    private readonly string? _endEvent;
    private readonly Func<int, bool>? _endOnStatus;
    private readonly object _lock = new();
    private readonly ManualResetEvent _wake = new(false);
    private HttpWebRequest? _current;
    private Thread? _thread;
    private volatile bool _stopped;
    private int _attempt;

    /// <param name="makeRequest">每次（重）连时生成请求，可带上最新的 since</param>
    /// <param name="endEvent">收到这个 event 名后停止且不再重连</param>
    /// <param name="endOnStatus">非 200 响应时调用，返回 true 表示正常结束、不再重连</param>
    public SSEConnection(Func<HttpWebRequest> makeRequest, Action<Event> onEvent, bool reconnect = true, string? endEvent = null,
        TimeSpan? idleTimeout = null, Func<int, bool>? endOnStatus = null)
    {
        _makeRequest = makeRequest;
        _onEvent = onEvent;
        _reconnect = reconnect;
        _endEvent = endEvent;
        _endOnStatus = endOnStatus;
        IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(45);
    }

    public TimeSpan IdleTimeout { get; }

    public bool IsStopped => _stopped;

    public void Start()
    {
        lock (_lock)
        {
            if (_thread != null || _stopped) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "paperplane.sse" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        HttpWebRequest? r;
        lock (_lock)
        {
            _stopped = true;
            r = _current;
            _current = null;
        }
        _wake.Set();
        try { r?.Abort(); }
        catch (Exception) { /* 已经结束 */ }
    }

    private void Emit(Event e)
    {
        try { _onEvent(e); }
        catch (Exception ex) { Trace.TraceError("SSE 回调异常：" + ex); }
    }

    private void Loop()
    {
        var buf = new byte[16 * 1024];
        while (!_stopped)
        {
            string? reason = null;
            HttpWebRequest? req = null;
            try
            {
                req = _makeRequest();
                req.Accept = "text/event-stream";
                req.Timeout = (int)(IdleTimeout + TimeSpan.FromSeconds(15)).TotalMilliseconds;
                req.ReadWriteTimeout = (int)IdleTimeout.TotalMilliseconds; // 同步读超过这么久没有字节就抛出 = 断线
                lock (_lock)
                {
                    if (_stopped) return;
                    _current = req;
                }
                using var resp = (HttpWebResponse)req.GetResponse();
                var status = (int)resp.StatusCode;
                if (status != 200)
                {
                    if (EndOnStatus(status)) return;
                    reason = "HTTP " + status;
                }
                else
                {
                    _attempt = 0;
                    Emit(new Event.Open());
                    using var stream = resp.GetResponseStream()!;
                    var splitter = new LineSplitter();
                    var parser = new SSEParser();
                    int n;
                    while (!_stopped && (n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        foreach (var line in splitter.Append(buf, 0, n))
                        {
                            var msg = parser.Feed(line);
                            if (msg == null) continue;
                            if (_stopped) return;
                            Emit(new Event.Message(msg));
                            if (_endEvent != null && msg.Event == _endEvent)
                            {
                                Stop();
                                Emit(new Event.Closed(null));
                                return;
                            }
                        }
                    }
                    reason = "connection closed";
                }
            }
            catch (WebException e) when (e.Response is HttpWebResponse r)
            {
                var status = (int)r.StatusCode;
                r.Dispose();
                if (EndOnStatus(status)) return;
                reason = "HTTP " + status;
            }
            catch (Exception e)
            {
                reason = e.Message;
            }
            finally
            {
                lock (_lock)
                {
                    if (_current == req) _current = null;
                }
            }
            if (_stopped) return;
            // 非正常结束一律带原因，null 只用于 endEvent
            Emit(new Event.Closed(reason ?? "connection closed"));
            if (!_reconnect) return;
            var delay = Math.Min(15, 0.5 * Math.Pow(2, Math.Min(_attempt, 6)));
            _attempt++;
            _wake.WaitOne(TimeSpan.FromSeconds(delay));
        }
    }

    private bool EndOnStatus(int status)
    {
        if (_stopped || _endOnStatus == null || !_endOnStatus(status)) return false;
        Stop();
        Emit(new Event.Closed(null));
        return true;
    }
}

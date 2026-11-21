using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit;

/// <summary>
/// 全局事件流 GET /api/v1/events（对应 Mac 的 EventStream）。断线自动重连；每次（重）连上后先收到 hello（data 为 SystemInfo）。
/// <para>
/// 界面层（AppModel）的用法与 Mac 一致：收到 Connected 标记已连接；收到 Disconnected 标记重连中，2 秒后还没恢复就重新
/// DaemonManager.ConnectAsync（daemon 可能换了端口/令牌，必要时重新拉起），换了地址就用新客户端重新 Start；
/// 收到 hello 事件（含断线重连后的 hello）就重新拉取全部状态（refreshAll），补上断线期间漏掉的事件。
/// </para>
/// </summary>
public sealed class EventStream
{
    public abstract record Update
    {
        private Update() { }
        public sealed record Connected : Update;
        public sealed record Event(DaemonEvent Value) : Update;
        public sealed record Disconnected(string? Reason) : Update;
    }

    private readonly object _lock = new();
    private SSEConnection? _connection;

    /// <summary>handler 在后台线程串行调用，界面层自行切回 UI 线程。重复 Start 会先停掉旧连接。</summary>
    public void Start(ApiClient client, Action<Update> handler)
    {
        var conn = new SSEConnection(() => client.AuthorizedWebRequest("/api/v1/events"), ev =>
        {
            switch (ev)
            {
                case SSEConnection.Event.Open:
                    handler(new Update.Connected());
                    break;
                case SSEConnection.Event.Closed c:
                    handler(new Update.Disconnected(c.Reason));
                    break;
                case SSEConnection.Event.Message m:
                    DaemonEvent e;
                    try { e = Json.Deserialize<DaemonEvent>(m.Value.Data); }
                    catch (Exception) { return; }
                    handler(new Update.Event(e));
                    break;
            }
        });
        SSEConnection? old;
        lock (_lock)
        {
            old = _connection;
            _connection = conn;
        }
        old?.Stop();
        conn.Start();
    }

    public void Stop()
    {
        SSEConnection? old;
        lock (_lock)
        {
            old = _connection;
            _connection = null;
        }
        old?.Stop();
    }
}

/// <summary>
/// 控制台日志流 GET /api/v1/consoles/&lt;id&gt;/stream?since=&lt;seq&gt;（对应 Mac 的 ConsoleStream）。
/// 断线后从最后收到的 seq 续传；收到 event: end 后结束（Ended）。
/// <para>
/// 每条 data 是 {"lines": [已定型的行], "live": [实时尾部]}：lines 与以前相同（有 seq，落盘，只追加）；
/// live 是伪终端里还在被原地改写的屏幕末尾几行（seq 为 0，不推进续传位置），键存在时整体替换实时尾部
/// （空数组或 null = 清空），键缺省时实时尾部不变。新内核每次（重）连上后的第一条一定带 live；老内核从不发。
/// </para>
/// </summary>
public sealed class ConsoleStream
{
    public abstract record Update
    {
        private Update() { }
        /// <summary>
        /// 一条消息的内容，按顺序原子地应用：先追加 Value（已定型的行，可能为空），再用 Live 整体替换实时尾部。
        /// Live 为 null 表示这条消息没带 live 键（实时尾部不变），空列表表示清空。
        /// </summary>
        public sealed record Lines(List<LogLine> Value, List<LogLine>? Live = null) : Update;
        public sealed record Ended : Update;
        public sealed record Disconnected(string? Reason) : Update;
    }

    /// <summary>
    /// 解码一条 data（lines 缺省或为 null 当空；live 键缺省得 null，键存在为 null 当空列表）。
    /// 不是 JSON 对象或解析失败返回 false。
    /// </summary>
    private static bool TryDecode(string data, out List<LogLine> lines, out List<LogLine>? live)
    {
        lines = new List<LogLine>();
        live = null;
        try
        {
            if (Json.Parse(data) is not JObject obj) return false;
            lines = Json.Decode<List<LogLine>>(obj["lines"]);
            if (obj.TryGetValue("live", out var lt)) live = Json.Decode<List<LogLine>>(lt);
        }
        catch (Exception)
        {
            lines = new List<LogLine>();
            live = null;
            return false;
        }
        return true;
    }

    private readonly object _lock = new();
    private SSEConnection? _connection;
    private long _lastSeq;

    public ConsoleStream(string consoleId, long since = 0)
    {
        ConsoleId = consoleId;
        _lastSeq = since;
    }

    public string ConsoleId { get; }

    /// <summary>已收到的最后一行的 seq（重连时从这里续传）</summary>
    public long LastSeq => Interlocked.Read(ref _lastSeq);

    /// <summary>handler 在后台线程串行调用。</summary>
    public void Start(ApiClient client, Action<Update> handler)
    {
        var id = Uri.EscapeDataString(ConsoleId);
        var conn = new SSEConnection(
            () => client.AuthorizedWebRequest($"/api/v1/consoles/{id}/stream?since={LastSeq}"),
            ev =>
            {
                switch (ev)
                {
                    case SSEConnection.Event.Closed c:
                        handler(c.Reason == null ? new Update.Ended() : new Update.Disconnected(c.Reason));
                        break;
                    case SSEConnection.Event.Message m:
                        if (m.Value.Event == "end") return;
                        if (!TryDecode(m.Value.Data, out var lines, out var live)) return;
                        if (lines.Count == 0 && live == null) return;
                        // 只有已定型的行推进续传位置（实时行 seq 为 0）
                        if (lines.Count > 0) Advance(lines[lines.Count - 1].Seq);
                        handler(new Update.Lines(lines, live));
                        break;
                }
            },
            reconnect: true, endEvent: "end");
        SSEConnection? old;
        lock (_lock)
        {
            old = _connection;
            _connection = conn;
        }
        old?.Stop();
        conn.Start();
    }

    public void Stop()
    {
        SSEConnection? old;
        lock (_lock)
        {
            old = _connection;
            _connection = null;
        }
        old?.Stop();
    }

    private void Advance(long seq)
    {
        long cur;
        do
        {
            cur = Interlocked.Read(ref _lastSeq);
            if (seq <= cur) return;
        } while (Interlocked.CompareExchange(ref _lastSeq, seq, cur) != cur);
    }
}

/// <summary>
/// 交互终端的输出流 GET /api/v1/terminals/&lt;id&gt;/stream?since=&lt;字节偏移&gt;（docs/api.md 第 3 节）。
/// Mac 由终端网页（xterm.js）直接连；这里给原生终端或宿主用，规则与 editor/src/terminal.ts 一致：
/// 每条 data 是 {"o": 起始偏移, "d": base64 原始字节}，重连补发的重叠部分去掉（已写过的不再给出）；
/// event: state 是终端状态（退出、重新启动）；event: closed 或 404（终端已不存在）时结束（Closed），不再重连。
/// </summary>
public sealed class TerminalStream
{
    public abstract record Update
    {
        private Update() { }
        public sealed record State(TerminalInfo Value) : Update;
        /// <summary>新的输出字节（UTF-8 可能跨条切开，按字节写进终端模拟器）；Offset 是 Data 第一个字节的偏移</summary>
        public sealed record Output(long Offset, byte[] Data) : Update;
        public sealed record Closed : Update;
        public sealed record Disconnected(string? Reason) : Update;
    }

    private sealed record Chunk
    {
        public long O { get; set; }
        public string D { get; set; } = "";
    }

    private readonly object _lock = new();
    private SSEConnection? _connection;
    private long _offset;

    public TerminalStream(string terminalId, long since = 0)
    {
        TerminalId = terminalId;
        _offset = since;
    }

    public string TerminalId { get; }

    /// <summary>已给出的输出末尾偏移（重连时从这里续传）</summary>
    public long Offset => Interlocked.Read(ref _offset);

    /// <summary>handler 在后台线程串行调用。</summary>
    public void Start(ApiClient client, Action<Update> handler)
    {
        var id = Uri.EscapeDataString(TerminalId);
        var conn = new SSEConnection(
            () => client.AuthorizedWebRequest($"/api/v1/terminals/{id}/stream?since={Offset}"),
            ev =>
            {
                switch (ev)
                {
                    case SSEConnection.Event.Closed c:
                        handler(c.Reason == null ? new Update.Closed() : new Update.Disconnected(c.Reason));
                        break;
                    case SSEConnection.Event.Message m:
                        switch (m.Value.Event)
                        {
                            case "closed":
                                return; // 随后由 Closed(null) 通知
                            case "state":
                                TerminalInfo info;
                                try { info = Json.Deserialize<TerminalInfo>(m.Value.Data); }
                                catch (Exception) { return; }
                                handler(new Update.State(info));
                                return;
                            case null:
                            case "message":
                                Chunk ch;
                                byte[] data;
                                try
                                {
                                    ch = Json.Deserialize<Chunk>(m.Value.Data);
                                    data = Convert.FromBase64String(ch.D);
                                }
                                catch (Exception) { return; }
                                var cur = Offset;
                                var end = ch.O + data.Length;
                                if (end <= cur) return; // 已经给过
                                var start = ch.O;
                                if (start < cur)
                                {
                                    var skip = (int)(cur - start);
                                    var rest = new byte[data.Length - skip];
                                    Buffer.BlockCopy(data, skip, rest, 0, rest.Length);
                                    data = rest;
                                    start = cur;
                                }
                                Interlocked.Exchange(ref _offset, end);
                                handler(new Update.Output(start, data));
                                return;
                        }
                        break;
                }
            },
            reconnect: true, endEvent: "closed", endOnStatus: status => status == 404);
        SSEConnection? old;
        lock (_lock)
        {
            old = _connection;
            _connection = conn;
        }
        old?.Stop();
        conn.Start();
    }

    public void Stop()
    {
        SSEConnection? old;
        lock (_lock)
        {
            old = _connection;
            _connection = null;
        }
        old?.Stop();
    }
}

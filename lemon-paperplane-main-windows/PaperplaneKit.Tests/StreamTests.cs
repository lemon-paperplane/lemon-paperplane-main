using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PaperplaneKit.Tests;

/// <summary>
/// 事件流 / 控制台流 / 终端流的断线重连与续传（本机起一个极简 HTTP 服务模拟 daemon：每个连接按顺序给一段响应后断开）。
/// </summary>
public class StreamTests
{
    /// <summary>极简 HTTP 服务：第 i 个连接返回 responses[i]（越界用最后一个），记下请求行；发完就关连接。</summary>
    private sealed class MiniServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<int, string, string> _respond;
        private readonly Thread _thread;
        private volatile bool _stop;

        public MiniServer(Func<int, string, string> respond)
        {
            _respond = respond;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        public int Port { get; }
        public ConcurrentQueue<string> Requests { get; } = new();
        /// <summary>设了就在发完响应后等它再关连接（模拟一直不发数据的长连接）</summary>
        public ManualResetEventSlim? HoldOpen { get; set; }

        private void Loop()
        {
            var i = 0;
            while (!_stop)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                using (c)
                {
                    try
                    {
                        var s = c.GetStream();
                        var head = new StringBuilder();
                        var b = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(b, 0, 1) == 1) head.Append((char)b[0]);
                        var headText = head.ToString();
                        // 读掉请求体（不读完就关连接会变成 RST，客户端可能收不到响应）
                        var m = System.Text.RegularExpressions.Regex.Match(headText, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        var bodyLen = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                        var body = new byte[bodyLen];
                        for (var got = 0; got < bodyLen;)
                        {
                            var n = s.Read(body, got, bodyLen - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        var requestLine = headText.Split('\n')[0].Trim();
                        Requests.Enqueue(requestLine + " | auth=" + (headText.Contains("Authorization: Bearer tok") ? "ok" : "missing"));
                        var resp = Encoding.UTF8.GetBytes(_respond(i++, requestLine));
                        s.Write(resp, 0, resp.Length);
                        s.Flush();
                        HoldOpen?.Wait(10000);
                        Thread.Sleep(50);
                    }
                    catch (IOException) { }
                }
            }
        }

        public ApiClient Client => new(new Uri($"http://127.0.0.1:{Port}"), "tok");

        public void Dispose()
        {
            _stop = true;
            _listener.Stop();
        }
    }

    private static string Sse(string body) =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n" + body;

    private static string Resp(int status, string body) =>
        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private static string NotFound() => Resp(404, "{}");

    private static void WaitFor(Func<bool> cond, int ms, string what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("超时：" + what);
            Thread.Sleep(20);
        }
    }

    [Test]
    public void EventStreamReconnectsAndDecodes()
    {
        using var srv = new MiniServer((i, _) => Sse(
            $"data: {{\"type\":\"hello\",\"data\":{{\"version\":\"0.1.{i}\"}}}}\n\n: ping\n\ndata: not json\n\n"));
        var updates = new ConcurrentQueue<EventStream.Update>();
        var es = new EventStream();
        es.Start(srv.Client, updates.Enqueue);
        WaitFor(() => updates.OfType<EventStream.Update.Connected>().Count() >= 2, 5000, "第二次连上");
        es.Stop();
        var list = updates.ToList();
        Assert.True(list[0] is EventStream.Update.Connected);
        var hellos = list.OfType<EventStream.Update.Event>().Select(e => e.Value.Decode<SystemInfo>().Version).ToList();
        Assert.Equal("0.1.0", hellos[0]);
        Assert.Equal("0.1.1", hellos[1]);
        // 第一次断开带原因
        var d = list.OfType<EventStream.Update.Disconnected>().First();
        Assert.NotNull(d.Reason);
        Assert.True(srv.Requests.All(r => r.StartsWith("GET /api/v1/events ") && r.EndsWith("auth=ok")));
    }

    [Test]
    public void ConsoleStreamResumesFromLastSeqAndEnds()
    {
        using var srv = new MiniServer((i, _) => i == 0
            ? Sse("data: {\"lines\":[{\"seq\":1,\"t\":5,\"s\":\"stdout\",\"text\":\"a\"},{\"seq\":2,\"text\":\"b\"}]}\n\n")
            : Sse("data: {\"lines\":[{\"seq\":3,\"text\":\"c\"}]}\n\nevent: end\ndata: {}\n\n"));
        var updates = new ConcurrentQueue<ConsoleStream.Update>();
        var cs = new ConsoleStream("run 1", since: 0);
        cs.Start(srv.Client, updates.Enqueue);
        WaitFor(() => updates.Any(u => u is ConsoleStream.Update.Ended), 5000, "Ended");
        var texts = updates.OfType<ConsoleStream.Update.Lines>().SelectMany(l => l.Value).Select(l => l.Text);
        KitAssert.Seq(new[] { "a", "b", "c" }, texts);
        Assert.Equal(3L, cs.LastSeq);
        var reqs = srv.Requests.ToList();
        Assert.True(reqs[0].StartsWith("GET /api/v1/consoles/run%201/stream?since=0 "));
        Assert.True(reqs[1].StartsWith("GET /api/v1/consoles/run%201/stream?since=2 "));
        // 结束后不再重连
        Thread.Sleep(800);
        Assert.Equal(2, srv.Requests.Count);
        cs.Stop();
    }

    [Test]
    public void TerminalStreamDedupsOverlapAndCloses()
    {
        string Chunk(long o, string text) => $"data: {{\"o\":{o},\"d\":\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\"}}\n\n";
        using var srv = new MiniServer((i, _) => i switch
        {
            0 => Sse("event: state\ndata: {\"id\":\"t1\",\"name\":\"sh\",\"cols\":80,\"rows\":24}\n\n" + Chunk(0, "hello")),
            1 => Sse(Chunk(0, "hel") + Chunk(3, "lo world")),
            _ => Sse("event: closed\ndata: {}\n\n"),
        });
        var updates = new ConcurrentQueue<TerminalStream.Update>();
        var ts = new TerminalStream("t1");
        ts.Start(srv.Client, updates.Enqueue);
        WaitFor(() => updates.Any(u => u is TerminalStream.Update.Closed), 8000, "Closed");
        var outs = updates.OfType<TerminalStream.Update.Output>().ToList();
        Assert.Equal("hello world", string.Concat(outs.Select(o => Encoding.UTF8.GetString(o.Data))));
        KitAssert.Seq(new long[] { 0, 5 }, outs.Select(o => o.Offset));
        Assert.Equal(11L, ts.Offset);
        Assert.Equal(80, updates.OfType<TerminalStream.Update.State>().First().Value.Cols);
        var reqs = srv.Requests.ToList();
        Assert.True(reqs[0].Contains("/api/v1/terminals/t1/stream?since=0 "));
        Assert.True(reqs[1].Contains("since=5 "));
        Assert.True(reqs[2].Contains("since=11 "));
        ts.Stop();
    }

    [Test]
    public void TerminalStream404IsClosed()
    {
        using var srv = new MiniServer((_, _) => NotFound());
        var updates = new ConcurrentQueue<TerminalStream.Update>();
        var ts = new TerminalStream("gone");
        ts.Start(srv.Client, updates.Enqueue);
        WaitFor(() => updates.Any(u => u is TerminalStream.Update.Closed), 5000, "Closed");
        Thread.Sleep(800);
        Assert.Equal(1, srv.Requests.Count);
        Assert.False(updates.Any(u => u is TerminalStream.Update.Disconnected));
    }

    [Test]
    public void StopAbortsBlockedRead()
    {
        // 服务端发完一条消息后一直不发数据：Stop 要能立刻打断阻塞的读，且之后不再回调
        var gate = new ManualResetEventSlim();
        using var srv = new MiniServer((_, _) => Sse("data: x\n\n")) { HoldOpen = gate };
        var updates = new ConcurrentQueue<SSEConnection.Event>();
        var conn = new SSEConnection(() => srv.Client.AuthorizedWebRequest("/x"), updates.Enqueue);
        conn.Start();
        WaitFor(() => updates.Any(u => u is SSEConnection.Event.Message), 3000, "收到消息");
        conn.Stop();
        Thread.Sleep(500);
        gate.Set();
        Thread.Sleep(300);
        Assert.True(updates.First() is SSEConnection.Event.Open);
        Assert.False(updates.Any(u => u is SSEConnection.Event.Closed)); // 主动停止不回调 Closed
        Assert.Equal(1, srv.Requests.Count); // 不再重连
        Assert.True(conn.IsStopped);

        // 阻塞在等响应头时 Stop 也能打断
        var gate2 = new ManualResetEventSlim();
        using var srv2 = new MiniServer((_, _) => { gate2.Wait(5000); return Sse(""); });
        var updates2 = new ConcurrentQueue<SSEConnection.Event>();
        var conn2 = new SSEConnection(() => srv2.Client.AuthorizedWebRequest("/x"), updates2.Enqueue);
        conn2.Start();
        WaitFor(() => srv2.Requests.Count == 1, 3000, "请求到达");
        conn2.Stop();
        Thread.Sleep(300);
        gate2.Set();
        Thread.Sleep(300);
        Assert.Equal(0, updates2.Count);
    }

    [Test]
    public void CallAsyncAgainstFakeServer()
    {
        using var srv = new MiniServer((i, req) => i == 0
            ? Resp(200, "{\"result\":[{\"id\":\"w\",\"name\":\"W\",\"x\":1}]}")
            : Resp(404, "{\"error\":{\"code\":\"not_found\",\"message\":\"no such op\"}}"));
        var api = srv.Client;
        var ws = api.WorkspacesAsync().GetAwaiter().GetResult();
        Assert.Equal("W", ws.Single().Name);
        var e = Assert.Throws<ApiException>(() => api.CallAsync<object>("nope.op").GetAwaiter().GetResult());
        Assert.Equal("not_found", e.Code);
        Assert.Equal(404, e.Status);
        var reqs = srv.Requests.ToList();
        Assert.True(reqs[0].StartsWith("POST /api/v1/call/workspace.list ") && reqs[0].EndsWith("auth=ok"));
    }

    [Test]
    public void NetworkErrorAndTimeout()
    {
        // 没有服务在听的端口
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var api = new ApiClient(new Uri($"http://127.0.0.1:{port}"), "tok");
        var e = Assert.Throws<ApiException>(() => api.SystemInfoAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
        Assert.Equal(ApiException.CodeNetwork, e.Code);
        // 连上了但不回：超时
        var gate = new ManualResetEventSlim();
        using var srv = new MiniServer((_, _) => { gate.Wait(5000); return NotFound(); });
        var t = Assert.Throws<ApiException>(() => srv.Client.SystemInfoAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult());
        gate.Set();
        Assert.Equal(ApiException.CodeNetwork, t.Code);
        // 调用方取消：抛 OperationCanceledException 而不是 ApiException
        var gate2 = new ManualResetEventSlim();
        using var srv2 = new MiniServer((_, _) => { gate2.Wait(5000); return NotFound(); });
        using var cts = new CancellationTokenSource(300);
        Assert.Throws<OperationCanceledException>(() => srv2.Client.CallAsync<SystemInfo>("system.info", ct: cts.Token).GetAwaiter().GetResult());
        gate2.Set();
    }
}

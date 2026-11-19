using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaperplaneKit.Tests;

/// <summary>
/// 对真实 daemon 的联调测试（对应 Mac 的 LiveDaemonTests.swift）。默认跳过（直接通过）；
/// 设置 PAPERPLANE_LIVE_DATA_DIR=&lt;独立实例的数据目录&gt; 后运行。设了 PAPERPLANE_BIN 时，daemon 没在跑会用它拉起（测 DaemonManager.ConnectAsync）。
/// 会在数据目录里建一个工作区和一个示例工程（%TEMP%\pp-kit-live\sample）。
/// </summary>
public class LiveDaemonTests
{
    private static string? LiveDir => Environment.GetEnvironmentVariable("PAPERPLANE_LIVE_DATA_DIR") is { Length: > 0 } d ? d : null;

    private static DaemonManager Manager(string dir) => new(dataDir: dir, appVersion: "");

    private static async Task<ApiClient> ConnectAsync(string dir)
    {
        var m = Manager(dir);
        var probe = await m.ProbeAsync();
        if (probe != null) return probe.Value.Client;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DaemonManager.BinaryPathEnvKey)))
            throw new AssertFailed("daemon 不可达，且没有设置 PAPERPLANE_BIN");
        return (await m.ConnectAsync(locale: "zh-Hans")).Client;
    }

    [Test]
    public async Task A1_ConnectAndCalls()
    {
        if (LiveDir is not { } dir) return;
        var m = Manager(dir);
        var (api, info) = await m.ConnectAsync(locale: "zh-Hans");
        Assert.True(info.Version.Length > 0);
        Assert.True(info.Plugins > 0);
        Assert.Equal("windows", info.Os);
        Console.WriteLine($"        daemon {info.Version} pid {info.Pid} {info.ApiBase} plugins={info.Plugins} exe={info.Executable}");
        await api.WorkspacesAsync();
        await api.ProjectsAsync();
        await api.RunsAsync();
        await api.TasksAsync();
        await api.SettingsAsync();
        var contrib = await api.ContributionsAsync();
        Assert.Equal("eclipse-windows", contrib.DefaultKeymap ?? "eclipse-windows");
        var plugins = await api.PluginsAsync();
        Assert.True(plugins.Count > 0);
        await api.ToolchainKindsAsync();
        await api.ToolchainsAsync();
        await api.MirrorsStatusAsync();
        await api.McpConfigAsync();
        await api.PendingPromptsAsync();
        await api.TerminalListAsync();
        var ops = await api.OpsAsync();
        Assert.True((ops.ArrayValue()?.Count ?? 0) > 50);
        // 错误信封
        try
        {
            await api.ProjectAsync("no-such-project");
            Assert.Fail("应当报错");
        }
        catch (ApiException e)
        {
            Assert.Equal("not_found", e.Code);
        }
        // 插件图标
        var withIcon = plugins.FirstOrDefault(p => p.Icon?.StartsWith("plugin:") == true);
        if (withIcon != null && IconSource.Parse(withIcon.Icon) is IconSource.Plugin pi)
        {
            var svg = Encoding.UTF8.GetString(await api.PluginFileAsync(pi.Id, pi.Path));
            Assert.True(svg.Contains("<svg"));
        }
        // 再连一次：直接复用正在跑的 daemon（同一个 pid）
        var (_, again) = await Manager(dir).ConnectAsync();
        Assert.Equal(info.Pid, again.Pid);
    }

    [Test]
    public async Task A2_EventStreamHello()
    {
        if (LiveDir is not { } dir) return;
        var api = await ConnectAsync(dir);
        using var got = new ManualResetEventSlim();
        var stream = new EventStream();
        SystemInfo? hello = null;
        stream.Start(api, u =>
        {
            if (u is EventStream.Update.Event { Value.Type: EventType.Hello } e)
            {
                hello = e.Value.Decode<SystemInfo>();
                got.Set();
            }
        });
        Assert.True(got.Wait(5000));
        stream.Stop();
        Assert.True(hello!.Version.Length > 0);
    }

    /// <summary>准备一个示例工程（一个 .cmd 脚本），返回 projectId</summary>
    private static async Task<string> SampleProjectAsync(ApiClient api)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pp-kit-live", "sample");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hello.cmd"), "@echo off\r\necho line-1\r\necho line-2\r\necho line-3\r\necho line-4\r\n");
        var existing = (await api.ProjectsAsync()).FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Path).TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing.Id;
        var ws = (await api.WorkspacesAsync()).FirstOrDefault() ?? await api.CreateWorkspaceAsync("pp-kit");
        var p = await api.ImportProjectAsync(dir, ws.Id, "pp-kit-sample");
        return p.Id;
    }

    [Test]
    public async Task A3_ConsoleStreamReplaysAndEnds()
    {
        if (LiveDir is not { } dir) return;
        var api = await ConnectAsync(dir);
        var pid = await SampleProjectAsync(api);
        // 指定 cmd：Windows 上默认 shell 是 PowerShell，& 在那里不能连接命令
        var added = await api.LaunchAddCommandAsync(pid, "pp-kit echo", "echo line-1 & echo line-2 & echo line-3 & echo 行-4", "", shell: "cmd");
        Assert.True(added.ConfigId.Length > 0);
        var run = await api.StartRunAsync(pid, added.ConfigId);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (run.IsActive && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            run = await api.RunAsync(run.Id);
        }
        Assert.False(run.IsActive);
        Assert.Equal(0, run.ExitCode);
        Console.WriteLine($"        run {run.Id} state={run.State} exit={run.ExitCode} lines={run.Lines}");

        var lines = new ConcurrentQueue<LogLine>();
        using var ended = new ManualResetEventSlim();
        var s = new ConsoleStream(run.Id, since: 0);
        s.Start(api, u =>
        {
            switch (u)
            {
                case ConsoleStream.Update.Lines l: foreach (var x in l.Value) lines.Enqueue(x); break;
                case ConsoleStream.Update.Ended: ended.Set(); break;
            }
        });
        Assert.True(ended.Wait(10000));
        s.Stop();
        var texts = lines.Where(l => l.S == "stdout").Select(l => ANSIParser.Strip(l.Text)).ToList();
        Assert.True(lines.Any(l => l.S == "system")); // 内核自己的「$ 命令」「进程已退出」行
        Assert.True(texts.Any(t => t.Trim() == "line-1"));
        Assert.True(texts.Any(t => t.Trim().EndsWith("-4")));
        // 已知内核问题（不归界面管）：中文 Windows（代码页 936）上 cmd/PowerShell 的输出没有从 GBK 转成 UTF-8，中文变成 U+FFFD
        if (!texts.Any(t => t.Trim() == "行-4"))
            Console.WriteLine("        注意：控制台中文输出乱码（内核未按控制台代码页转码）：" + texts.FirstOrDefault(t => t.Trim().EndsWith("-4")));
        Assert.Equal(s.LastSeq, lines.Last().Seq);
        Assert.True(s.LastSeq > 0);
        // run.logs 尾部
        var logs = await api.LogsAsync(run.Id, tail: 3);
        Assert.True(logs.Lines.Count <= 3);
        // 资源统计
        var stats = await api.RunStatsAsync(run.Id);
        Assert.Equal(run.Id, stats.RunId);
        // 清理
        await api.LaunchRemoveAsync(pid, new[] { added.ConfigId });
    }

    [Test]
    public async Task A4_TerminalStream()
    {
        if (LiveDir is not { } dir) return;
        var api = await ConnectAsync(dir);
        var pid = await SampleProjectAsync(api);
        TerminalInfo term;
        try
        {
            term = await api.TerminalCreateAsync(pid, 100, 30);
        }
        catch (ApiException e) when (e.Code == ApiException.CodeNotSupported)
        {
            Console.WriteLine("        终端暂不支持：" + e.Message);
            return;
        }
        var output = new StringBuilder();
        var outLock = new object();
        using var gotState = new ManualResetEventSlim();
        using var closed = new ManualResetEventSlim();
        var ts = new TerminalStream(term.Id);
        ts.Start(api, u =>
        {
            switch (u)
            {
                case TerminalStream.Update.State: gotState.Set(); break;
                case TerminalStream.Update.Output o: lock (outLock) output.Append(Encoding.UTF8.GetString(o.Data)); break;
                case TerminalStream.Update.Closed: closed.Set(); break;
            }
        });
        Assert.True(gotState.Wait(5000));
        await Task.Delay(1500); // 等 shell 起来
        await api.TerminalInputAsync(term.Id, "echo pp-kit-term-ok\r");
        // 命令回显一次、输出一次
        int Occurrences()
        {
            lock (outLock) return output.ToString().Split(new[] { "pp-kit-term-ok" }, StringSplitOptions.None).Length - 1;
        }
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Occurrences() < 2 && DateTime.UtcNow < deadline) await Task.Delay(100);
        string text;
        lock (outLock) text = output.ToString();
        Assert.True(text.Contains("pp-kit-term-ok"));
        Assert.True(ts.Offset > 0);
        var busy = await api.TerminalBusyAsync(term.Id);
        Console.WriteLine($"        terminal {term.Id} shell={term.Shell} bytes={ts.Offset} busy={busy.Busy}");
        await api.TerminalCloseAsync(term.Id);
        Assert.True(closed.Wait(10000));
        ts.Stop();
    }
}

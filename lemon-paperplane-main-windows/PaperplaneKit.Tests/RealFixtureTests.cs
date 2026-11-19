using System.Collections.Generic;
using System.Linq;
using static PaperplaneKit.Tests.FixtureFiles;

namespace PaperplaneKit.Tests;

/// <summary>用真实 daemon（0.1.0）返回的响应核对模型解码（对应 Mac 的 RealFixtureTests.swift，夹具见 Fixtures/real）。</summary>
public class RealFixtureTests
{
    [Test]
    public void AllRealResponsesDecode()
    {
        Assert.Equal("http://127.0.0.1:47292", Real<SystemInfo>("system.info.json").ApiBase);
        var ws = Real<List<Workspace>>("workspace.list.json");
        Assert.Equal("默认工作区", ws.FirstOrDefault()?.Name);
        var list = Real<List<ProjectSummary>>("project.list.json");
        Assert.True(list.Count > 0);
        Assert.True(list[0].Facets.Any(f => f.Icon?.StartsWith("plugin:") == true));
        var running = Real<List<ProjectSummary>>("project.list.running.json");
        Assert.True(running.Any(p => p.HasActiveRun));
        var p = Real<Project>("project.get.json");
        KitAssert.JsonEq("\"main.py\"", p.Modules.First().RunConfigs.First().Options.GetValueOrDefault("script"));
        Assert.Equal("3.12.12", p.Modules.First().Toolchains["python"].Version);
        Assert.Equal(3, p.Modules.First().Info.Count);
        var c = Real<Contributions>("ui.contributions.json");
        Assert.True(c.RunConfigTypes.Count > 0);
        Assert.True(c.ActionsAt(ActionLocation.ProjectMenu).Count > 0);
        Assert.Equal(true, c.Actions.FirstOrDefault(a => a.Id == "installPackage")?.Form.First().Required);
        Assert.True(Real<List<PluginInfo>>("plugin.list.json").Count > 0);
        Assert.Equal(47292, Real<AppSettings>("settings.get.json").ApiPort);
        var tcs = Real<List<Toolchain>>("toolchain.list.json");
        Assert.True(tcs.Count > 0);
        Assert.True(Real<List<ToolchainKind>>("toolchain.kinds.json").Count > 0);
        var ms = Real<MirrorStatus>("mirrors.status.json");
        Assert.Equal("china", ms.Effective);
        Assert.True(ms.Probes.Count > 0);
        var mcp = Real<MCPConfig>("mcp.config.json");
        Assert.True(mcp.ClaudeCode.StartsWith("claude mcp add"));
        Assert.NotNull(mcp.ClaudeDesktop.Get("mcpServers"));
        Assert.Equal(0, Real<List<Diagnosis>>("project.problems.json").Count);
        var types = Real<List<RunConfigTypeChoice>>("runconfig.types.json");
        Assert.True(types.First().Type.Options.Count > 0);
        var scan = Real<List<ScanEntry>>("project.scan.json");
        Assert.True(scan.Any(s => s.Imported));
        var avail = Real<List<AvailableVersion>>("toolchain.available.json");
        Assert.True(avail.Any(a => a.Recommended));
        Assert.Equal(0, Real<List<StoreEntry>>("store.list.json").Count);
        Assert.Equal(0, Real<List<TaskInfo>>("task.list.json").Count);
        var view = Real<ViewRenderResult>("view.render.json");
        Assert.Equal("column", view.Root.Type);
        Assert.Equal("primary", view.Root.Children.First().Children.First().Style);
        var run = Real<Run>("run.get.json");
        KitAssert.Seq(new[] { 47311 }, run.Ports);
        var start = Real<Run>("run.start.json");
        Assert.Equal("preparing", start.State);
        var logs = Real<LogsResult>("run.logs.json");
        Assert.Equal(5, logs.Lines.Count);
        Assert.True(logs.Truncated);
    }

    [Test]
    public void RealEventStream()
    {
        var types = new List<string>();
        foreach (var e in Events(Bytes("real/events.sse.txt")))
        {
            types.Add(e.Type);
            switch (e.Type)
            {
                case EventType.Hello: e.Decode<SystemInfo>(); break;
                case EventType.Run: e.Decode<Run>(); break;
                case EventType.Project: e.Decode<ProjectSummary>(); break;
                case EventType.Stats: Assert.True(e.Decode<List<RunBrief>>().Count > 0); break;
            }
        }
        Assert.Equal(EventType.Hello, types.First());
        Assert.True(types.Contains(EventType.Stats));
    }
}

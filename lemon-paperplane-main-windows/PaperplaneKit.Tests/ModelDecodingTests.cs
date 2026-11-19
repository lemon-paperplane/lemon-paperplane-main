using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using static PaperplaneKit.Tests.FixtureFiles;

namespace PaperplaneKit.Tests;

/// <summary>对应 Mac 的 ModelDecodingTests.swift。</summary>
public class ModelDecodingTests
{
    [Test]
    public void SystemInfoDecodes()
    {
        var info = Decode<SystemInfo>("system.info.json");
        Assert.Equal("0.1.0", info.Version);
        Assert.Equal(2, info.PluginProcs);
        Assert.Near(21.4, info.MemoryMB, 0.001);
        // 纳秒精度、带时区
        Assert.NotNull(info.StartedAt);
        Assert.Near(1_794_965_952.123, info.StartedAt!.Value.ToUnixTimeMilliseconds() / 1000.0, 0.01);
    }

    [Test]
    public void WorkspacesWithNullSliceAndZeroTime()
    {
        var ws = Decode<List<Workspace>>("workspace.list.json");
        Assert.Equal(2, ws.Count);
        KitAssert.Seq(new[] { "p1", "p2" }, ws[0].ProjectIds);
        KitAssert.Seq(new string[0], ws[1].ProjectIds);
        Assert.True(GoTime.IsZero(ws[1].CreatedAt));
        Assert.False(GoTime.IsZero(ws[0].CreatedAt));
    }

    [Test]
    public void ProjectSummaries()
    {
        var list = Decode<List<ProjectSummary>>("project.list.json");
        Assert.Equal(2, list.Count);
        var p = list[0];
        KitAssert.Seq(new[] { "gradle", "spring-boot" }, p.Facets.Select(f => f.Id));
        KitAssert.Seq(new[] { "http://localhost:8080" }, p.Runs.First().Urls);
        Assert.Equal(1, p.Vcs?.Ahead);
        Assert.Equal(0, p.Vcs?.Conflicts);
        KitAssert.Seq(new[] { "jdk 17" }, p.MissingToolchains);
        Assert.True(p.HasActiveRun);
        var q = list[1];
        Assert.Equal(0, q.Facets.Count);
        Assert.Equal(0, q.Configs.Count);
        Assert.Null(q.Vcs);
        Assert.False(q.HasActiveRun);
    }

    [Test]
    public void ProjectDetail()
    {
        var p = Decode<Project>("project.get.json");
        Assert.Equal(1, p.Modules.Count);
        var m = p.Modules[0];
        Assert.Equal(3, m.Facets.Count);
        Assert.Equal("8.12", m.Facets[1].Data?.GetValueOrDefault("version").StringValue());
        Assert.Equal("8.12", m.Analysis["gradle"].Info.First().Value);
        Assert.Equal(0, m.Analysis["spring-boot"].Info.Count);
        KitAssert.Seq(new[] { "dev", "prod" }, m.OptionValues["profiles"].Select(o => o.Value));
        Assert.Equal(1, m.Problems.Count);
        Assert.True(m.Toolchains["gradle"].Missing);
        Assert.Equal(2, m.RunConfigs.Count);
        var c1 = m.RunConfigs[0];
        Assert.True(c1.Default);
        Assert.Equal(8080, c1.Options["port"].IntValue());
        KitAssert.Seq(new[] { "dev" }, c1.Options["profiles"].StringArray());
        Assert.Equal(0, m.RunConfigs[1].Options.Count);
        Assert.True(m.RunConfigs[1].Edited);
        Assert.Equal("17", p.Settings?.Toolchains["jdk"].Constraint);
        Assert.Equal(2, p.AllRunConfigs.Count);
        KitAssert.SetEq(new[] { "java", "gradle", "spring-boot" }, p.PluginIds);
        Assert.True(GoTime.IsZero(p.AnalyzedAt));
        // 查找辅助
        Assert.Equal("m1", p.Module("m1")?.Id);
        Assert.Equal("c2", p.RunConfig("c2")?.Id);
        Assert.Null(p.RunConfig(null));
    }

    [Test]
    public void RunsDecode()
    {
        var runs = Decode<List<Run>>("run.list.json");
        Assert.Equal(2, runs.Count);
        Assert.Equal("8080", runs[0].Diagnoses.First().Fixes.First().Params["port"]);
        Assert.True(runs[0].Diagnoses.First().Fixes.First().Recommended);
        Assert.NotNull(runs[0].ReadyAt);
        Assert.Null(runs[0].ExitCode);
        Assert.Equal(1, runs[1].ExitCode);
        Assert.True(runs[0].IsActive);
        Assert.False(runs[1].IsActive);
    }

    [Test]
    public void TasksDecode()
    {
        var tasks = Decode<List<TaskInfo>>("task.list.json");
        Assert.Near(22.1, tasks[0].Percent, 0.001);
        Assert.True(tasks[0].IsRunning);
        Assert.Equal("已清理", tasks[1].Result.Get("message").StringValue());
    }

    [Test]
    public void ContributionsDecode()
    {
        var c = Decode<Contributions>("ui.contributions.json");
        Assert.Equal(4, c.RunConfigTypes.First().Options.Count);
        Assert.Equal(8080, c.RunConfigTypes.First().Options[3].Default.IntValue());
        Assert.Equal("running", c.ViewsAt(ViewLocation.RunPanel).First().RunState);
        Assert.True(c.ViewsAt(ViewLocation.Sidebar).First().IsWebview);
        // locations 为空的动作默认挂 project.menu
        KitAssert.Seq(new[] { "clean", "deps" }, c.ActionsAt(ActionLocation.ProjectMenu).Select(a => a.Id));
        KitAssert.Seq(new[] { "clean" }, c.ActionsAt(ActionLocation.CardMenu).Select(a => a.Id));
        Assert.Equal(true, c.Settings.First().Values["daemon"].BoolValue());
    }

    [Test]
    public void PluginsDecode()
    {
        var p = Decode<List<PluginInfo>>("plugin.list.json");
        Assert.Equal(12, p[0].Contributes?.Diagnostics);
        Assert.Equal("engine mismatch", p[1].Error);
        Assert.Equal(0, p[1].Contributes?.Facets.Count);
    }

    [Test]
    public void SettingsDecode()
    {
        var s = Decode<AppSettings>("settings.get.json");
        Assert.Equal("dark", s.Theme);
        KitAssert.JsonEq("true", s.PluginSettings["gradle"]["daemon"]);
        Assert.Equal(0, s.DisabledPlugins.Count);
        Assert.Null(s.Terminal);
    }

    [Test]
    public void ViewRender()
    {
        var r = Decode<ViewRenderResult>("view.render.json");
        Assert.Equal(5000, r.RefreshMs);
        Assert.Equal(8, r.Root.Children.Count);
        var table = r.Root.Children[2];
        Assert.True(table.Selectable);
        Assert.True(table.Columns[1].Mono);
        Assert.Equal("link", table.Rows[0].Actions.First().Style);
        Assert.Equal(-1.0, r.Root.Children[4].Value);
        KitAssert.JsonEq("\"x\"", r.Root.Children[5].Values?.GetValueOrDefault("name"));
        Assert.Equal(1, r.Root.Children[6].Rows[0].Children.Count);
        Assert.Equal(1, r.State?.GetValueOrDefault("page").IntValue());
    }

    [Test]
    public void ErrorAndNullEnvelope()
    {
        var err = KitAssert.ThrowsApi(() => ApiClient.DecodeEnvelope<Project>(Bytes("error.json"), 400));
        Assert.Equal("user_error", err.Code);
        Assert.Equal("工程不存在", err.Message);
        Assert.Equal(400, err.Status);
        var list = Decode<List<ProjectSummary>>("null.json");
        Assert.Equal(0, list.Count);
        var none = Decode<EmptyResult>("null.json");
        Assert.Equal(new EmptyResult(), none);
        var v = Decode<JToken>("null.json");
        Assert.True(v.IsNull());
        // 非 JSON 的错误响应
        var e401 = KitAssert.ThrowsApi(() => ApiClient.DecodeEnvelope<JToken>(Encoding.UTF8.GetBytes("unauthorized"), 401));
        Assert.Equal("unauthorized", e401.Code);
        // 结果缺失且不能为空的类型
        Assert.Equal(ApiException.CodeBadResponse, KitAssert.ThrowsApi(() => ApiClient.DecodeEnvelope<Project>("{\"result\":null}", 200)).Code);
        // 结果类型对不上
        Assert.Equal(ApiException.CodeBadResponse, KitAssert.ThrowsApi(() => ApiClient.DecodeEnvelope<Project>("{\"result\":[1]}", 200)).Code);
        Assert.Equal("http_500", KitAssert.ThrowsApi(() => ApiClient.DecodeEnvelope<Project>("<html>", 500)).Code);
    }

    [Test]
    public void SSEEventsFixture()
    {
        var events = Events(Bytes("events.sse.txt"));
        KitAssert.Seq(new[] { "hello", "stats", "prompt", "project.removed", "toolchains.changed" }, events.Select(e => e.Type));
        var hello = events[0].Decode<SystemInfo>();
        Assert.Equal("en", hello.Locale);
        var stats = events[1].Decode<List<RunBrief>>();
        Assert.Equal(2.5, stats.First().Cpu);
        var prompt = events[2].Decode<Prompt>();
        Assert.Equal(2, prompt.Fields.Count);
        Assert.True(prompt.Fields[1].IsSecret);
        Assert.False(prompt.Fields[0].IsSecret);
        Assert.Equal("p9", events[3].Decode<Dictionary<string, string>>()["id"]);
        Assert.True(events[4].Data == null || events[4].Data.IsNull());
    }

    [Test]
    public void RunConfigRoundTrip()
    {
        var p = Decode<Project>("project.get.json");
        var c = p.Modules[0].RunConfigs[0];
        var text = Json.Serialize(c);
        var back = Json.Deserialize<RunConfig>(text);
        Assert.True(Json.DeepEquals(back, c));
        // 平铺：没有嵌套的 RunConfiguration 对象
        var obj = Json.Parse(text);
        Assert.Null(obj.Get("mainClass"));
        KitAssert.JsonEq("\"com.example.App\"", obj.Get("options").Get("mainClass"));
        KitAssert.JsonEq("true", obj.Get("default"));
        // 键名 camelCase
        Assert.NotNull(obj.Get("moduleId"));
        // 深拷贝
        var copy = Json.Clone(c);
        Assert.False(ReferenceEquals(copy.Options, c.Options));
        Assert.True(Json.DeepEquals(copy, c));
    }

    private sealed class A
    {
        public string Path { get; set; } = "";
        public int? Context { get; set; }
    }

    [Test]
    public void JSONValueHelpers()
    {
        var v = Json.Object(("a", 1), ("b", new[] { 1, 2 }), ("c", "x"), ("d", JSONValue.Null), ("e", true));
        Assert.Equal(1, v.Get("a").IntValue());
        Assert.Equal(2, v.Get("b").ArrayValue()?.Count);
        Assert.True(v.Get("d").IsNull());
        Assert.False(v.Get("missing").IsNull());
        Assert.Equal(true, v.Get("e").BoolValue());
        Assert.Equal("3", JSONValue.Of(3).JsonString());
        Assert.Equal("2.5", JSONValue.Of(2.5).JsonString());
        Assert.Equal("3", JSONValue.Of(3.0).JsonString());
        var o = Json.Object(("x", "1"), ("y", null));
        KitAssert.Seq(new[] { "x" }, o.ObjectValue()!.Properties().Select(p => p.Name));
        var flat = JSONValue.Encode(new A { Path = "a.txt", Context = null });
        KitAssert.JsonEq("{\"path\":\"a.txt\"}", flat);
    }

    [Test]
    public void VCSModels()
    {
        var json = "{\"commits\":[{\"hash\":\"abc\",\"short\":\"abc\",\"parents\":[\"def\"],\"author\":\"me\",\"date\":\"2026-11-18T09:00:00+08:00\",\"subject\":\"init\",\"refs\":[{\"name\":\"main\",\"kind\":\"head\"}],\"graph\":{\"lane\":0,\"lanes\":2,\"edges\":[{\"from\":0,\"to\":0,\"color\":0},{\"from\":1,\"to\":0,\"color\":1}],\"color\":0}}],\"hasMore\":true}";
        var log = Json.Deserialize<VCSLog>(json);
        Assert.Equal(2, log.Commits.First().Graph?.Edges.Count);
        Assert.NotNull(log.Commits.First().ParsedDate);
        var args = JSONValue.Encode(new VCSSubmoduleUpdateArgs { Initialize = true });
        KitAssert.JsonEq("true", args.Get("init"));
    }

    private sealed class Box
    {
        public Dictionary<string, string> M { get; set; } = new();
    }

    /// <summary>插件给的 nil 切片会变成 null（如 pom 里没写 profiles 时的 maven.profiles）：不能让整个导入结果解析失败</summary>
    [Test]
    public void OptionValuesTolerateNullEntries()
    {
        var json = "{\"info\": null, \"problems\": [], \"optionValues\": {\"maven.profiles\": null, \"jvm.modules\": [{\"value\": \":\", \"label\": \"root\"}]}, \"at\": \"2026-11-18T15:00:00.123456789+08:00\"}";
        var a = Json.Deserialize<ModuleAnalysis>(json);
        Assert.Equal(0, a.OptionValues["maven.profiles"].Count);
        Assert.Equal(1, a.OptionValues["jvm.modules"].Count);
        Assert.Equal(0, a.Info.Count);
        Assert.NotNull(a.At);

        // 值不是数组/字典时，null 那一项直接丢掉
        var b = Json.Deserialize<Box>("{\"m\": {\"a\": \"x\", \"b\": null}}");
        Assert.Equal(1, b.M.Count);
        Assert.Equal("x", b.M["a"]);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit.Tests;

/// <summary>对应 Mac ParserTests.swift 的 ANSITests。</summary>
public class ANSITests
{
    [Test]
    public void PlainText()
    {
        var p = new ANSIParser();
        KitAssert.Seq(new[] { new ANSISpan("hello", ANSIStyle.Plain) }, p.Parse("hello"));
    }

    [Test]
    public void BasicColorsAndReset()
    {
        var p = new ANSIParser();
        var spans = p.Parse("\u001B[31mred\u001B[0m plain \u001B[1;92mbold\u001B[22m");
        Assert.Equal(3, spans.Count);
        Assert.Equal(ANSIColor.Palette(1), spans[0].Style.Fg);
        Assert.Equal(ANSIStyle.Plain, spans[1].Style);
        Assert.Equal(ANSIColor.Palette(10), spans[2].Style.Fg);
        Assert.True(spans[2].Style.Bold);
        Assert.False(p.Style.Bold); // 22 关掉粗体
        Assert.Equal(ANSIColor.Palette(10), p.Style.Fg); // 颜色跨行保留
    }

    [Test]
    public void Color256AndTrueColor()
    {
        var p = new ANSIParser();
        var s = p.Parse("\u001B[38;5;208ma\u001B[48;2;10;20;30mb\u001B[38:2::1:2:3mc\u001B[38:5:33md");
        Assert.Equal(ANSIColor.Palette(208), s[0].Style.Fg);
        Assert.Equal(ANSIColor.Rgb(10, 20, 30), s[1].Style.Bg);
        Assert.Equal(ANSIColor.Rgb(1, 2, 3), s[2].Style.Fg);
        Assert.Equal(ANSIColor.Palette(33), s[3].Style.Fg);
    }

    [Test]
    public void UnderlineItalicInverse()
    {
        var p = new ANSIParser();
        var s = p.Parse("\u001B[3;4;7mx\u001B[23;24;27my");
        Assert.True(s[0].Style.Italic && s[0].Style.Underline && s[0].Style.Inverse);
        Assert.Equal(ANSIStyle.Plain, s[1].Style);
    }

    [Test]
    public void StripsCursorControlAndOSC()
    {
        var p = new ANSIParser();
        var s = p.Parse("\u001B[2K\u001B[1Gdone \u001B]8;;http://x\u0007link\u001B]8;;\u001B\\ end\u001B[?25h");
        Assert.Equal("done link end", string.Concat(s.Select(x => x.Text)));
    }

    [Test]
    public void CarriageReturnProgress()
    {
        var p = new ANSIParser();
        Assert.Equal("100%", string.Concat(p.Parse("10%\r50%\r100%").Select(x => x.Text)));
        Assert.Equal("line", string.Concat(p.Parse("line\r").Select(x => x.Text)));
    }

    [Test]
    public void Strip()
    {
        Assert.Equal("ok\t1", ANSIParser.Strip("\u001B[32mok\u001B[0m\t1"));
    }

    [Test]
    public void BackspaceAndControlChars()
    {
        // 退格删掉前一个字（代理对算一个），其他控制字符丢弃
        Assert.Equal("ab", ANSIParser.Strip("abc\b\u0001"));
        Assert.Equal("a", ANSIParser.Strip("a\U0001F600\b"));
        var p = new ANSIParser();
        var s = p.Parse("\u001B[31mx\u001B[0m\b");
        Assert.Equal(0, s.Count); // 退格删到前一段里
        // 下划线子参数 4:0 关掉
        var q = new ANSIParser();
        q.Parse("\u001B[4mu\u001B[4:0m");
        Assert.False(q.Style.Underline);
        // 超出范围的值截到 0–255
        var r = new ANSIParser();
        Assert.Equal(ANSIColor.Rgb(255, 0, 0), r.Parse("\u001B[38;2;300;-5;0mz")[0].Style.Fg);
    }

    [Test]
    public void Palette256()
    {
        Assert.Null(ANSIPalette.Rgb(7));
        Assert.Equal(((byte)0, (byte)0, (byte)0), ANSIPalette.Rgb(16)!.Value);
        Assert.Equal(((byte)255, (byte)255, (byte)255), ANSIPalette.Rgb(231)!.Value);
        Assert.Equal(((byte)255, (byte)0, (byte)0), ANSIPalette.Rgb(196)!.Value);
        Assert.Equal(((byte)8, (byte)8, (byte)8), ANSIPalette.Rgb(232)!.Value);
    }
}

/// <summary>对应 Mac ParserTests.swift 的 SSETests。</summary>
public class SSETests
{
    [Test]
    public void ParserFraming()
    {
        var p = new SSEParser();
        Assert.Null(p.Feed("event: end"));
        Assert.Null(p.Feed("data: {}"));
        Assert.Equal(new SSEMessage("end", "{}"), p.Feed(""));
        Assert.Null(p.Feed(": ping"));
        Assert.Null(p.Feed(""));
        Assert.Null(p.Feed("data:a"));
        Assert.Null(p.Feed("data: b"));
        Assert.Equal(new SSEMessage(null, "a\nb"), p.Feed(""));
    }

    [Test]
    public void LineSplitterAcrossChunks()
    {
        var s = new LineSplitter();
        KitAssert.Seq(new string[0], s.Append(Encoding.UTF8.GetBytes("data: 中")));
        KitAssert.Seq(new[] { "data: 中文", "", "data: x" }, s.Append(Encoding.UTF8.GetBytes("文\r\n\r\ndata: x\n")));
        // 多字节字符被切在两块之间
        var bytes = Encoding.UTF8.GetBytes("é\n");
        Assert.Equal(0, s.Append(bytes, 0, 1).Count);
        KitAssert.Seq(new[] { "é" }, s.Append(bytes, 1, bytes.Length - 1));
    }
}

/// <summary>对应 Mac ParserTests.swift 的 MiscTests（提交图、图标引用、格式化、Go 时间），外加 C# 版 JSON 规则的检查。</summary>
public class MiscTests
{
    [Test]
    public void CommitGraph()
    {
        var g = new CommitGraphGeometry(laneWidth: 10, rowHeight: 20, leftPadding: 0);
        var prev = new VCSGraphRow { Lane = 0, Lanes = 2, Color = 0, Edges = { new VCSGraphEdge { From = 0, To = 0, Color = 0 }, new VCSGraphEdge { From = 1, To = 0, Color = 1 } } };
        var cur = new VCSGraphRow { Lane = 0, Lanes = 1, Color = 0, Edges = { new VCSGraphEdge { From = 0, To = 0, Color = 0 } } };
        var row = g.RowFor(cur, prev);
        Assert.Equal(new KitPoint(5, 10), row.Node);
        Assert.Equal(3, row.Segments.Count);
        // 上一行 1→0 的连线：在本行上半段从两列中点到本列中线
        Assert.Equal(new KitPoint(10, 0), row.Segments[1].From);
        Assert.Equal(new KitPoint(5, 10), row.Segments[1].To);
        Assert.Equal(new KitPoint(5, 20), row.Segments[2].To);
        Assert.Equal(20.0, row.Width);
        Assert.Equal(1, CommitGraphGeometry.PaletteIndex(9));
        Assert.Equal(7, CommitGraphGeometry.PaletteIndex(-1));
        Assert.Equal(20.0, g.Width(new[] { prev, cur }));
        var d = new CommitGraphGeometry();
        Assert.Equal(14.0, d.LaneWidth);
        Assert.Equal(28.0, d.RowHeight);
    }

    [Test]
    public void IconRef()
    {
        Assert.Equal(new IconSource.Builtin("play"), IconSource.Parse("builtin:play"));
        Assert.Equal(new IconSource.Plugin("spring-boot", "icons/a.svg"), IconSource.Parse("plugin:spring-boot/icons/a.svg"));
        Assert.Equal(new IconSource.Plugin("gradle", "icons/x.svg"), IconSource.Parse("icons/x.svg", pluginId: "gradle"));
        Assert.Equal(new IconSource.Builtin("play"), IconSource.Parse("play"));
        Assert.Equal(new IconSource.Builtin("play"), IconSource.Parse("play.svg"));
        Assert.Null(IconSource.Parse(""));
        Assert.Null(IconSource.Parse(null));
        Assert.Null(IconSource.Parse("plugin:noslash"));
        Assert.Equal("plugin:a/b.svg", IconSource.Parse("plugin:a/b.svg")!.Key);
    }

    [Test]
    public void FormatHelpers()
    {
        Assert.Equal("~/Code/x", Format.AbbreviatePath("/Users/me/Code/x", home: "/Users/me"));
        Assert.Equal("/Users/meme/x", Format.AbbreviatePath("/Users/meme/x", home: "/Users/me"));
        Assert.Equal("~\\Code\\x", Format.AbbreviatePath("C:\\Users\\Me\\Code\\x", home: "C:\\Users\\me"));
        Assert.Equal("~", Format.AbbreviatePath("C:\\Users\\me", home: "C:\\Users\\me"));
        Assert.Equal("2.0 GB", Format.Memory(2048));
        Assert.Equal("512 MB", Format.Memory(512.4));
        Assert.Equal("12.5 MB", Format.Memory(12.5));
        Assert.Equal("localhost:8080", Format.ShortURL("http://localhost:8080/"));
        Assert.Equal("example.com/a", Format.ShortURL("https://example.com/a"));
        Assert.Equal("2m 5s", Format.Duration(125));
        Assert.Equal("3s", Format.Duration(3.9));
        Assert.Equal("0s", Format.Duration(-5));
        Assert.Equal("1h 4m", Format.Duration(3840));
        Assert.Equal("12%", Format.Cpu(12.4));
        Assert.Equal("3.5%", Format.Cpu(3.46));
        // 与 printf 一致的舍入（二进制值精确舍入、恰好一半取偶）
        Assert.Equal("0.12", Format.Fixed(0.125, 2));
        Assert.Equal("2.67", Format.Fixed(2.675, 2));
        Assert.Equal("0.1", Format.Fixed(0.05, 1));
        Assert.Equal("2", Format.Fixed(2.5, 0));
        Assert.Equal("4", Format.Fixed(3.5, 0));
        Assert.Equal("-0.0", Format.Fixed(-0.04, 1));
        Assert.Equal("Zero KB", Format.Bytes(0));
        Assert.Equal("999 bytes", Format.Bytes(999));
        Assert.Equal("1.5 MB", Format.Bytes(1_500_000));
        Assert.Equal("1.23 GB", Format.Bytes(1_234_567_890));
    }

    [Test]
    public void GoTimeParsing()
    {
        Assert.NotNull(GoTime.Parse("2026-11-18T09:39:12Z"));
        Assert.NotNull(GoTime.Parse("2026-11-18T09:39:12.5+08:00"));
        Assert.NotNull(GoTime.Parse("2026-11-18T09:39:12.123456789-07:00"));
        Assert.True(GoTime.IsZero(GoTime.Parse("0001-01-01T00:00:00Z")));
        Assert.Null(GoTime.Parse("not a time"));
        Assert.Null(GoTime.Parse(null));
        var t = GoTime.Parse("2026-11-18T09:39:12.123456789+08:00")!.Value;
        Assert.Equal("2026-11-18T01:39:12.123Z", GoTime.Format(t));
    }

    private sealed class Lenient
    {
        public int N { get; set; }
        public long L { get; set; }
        public int? Opt { get; set; }
        public string S { get; set; } = "";
        public List<int> Xs { get; set; } = new();
        public DateTimeOffset? At { get; set; }
        public JToken? Raw { get; set; }
        public Dictionary<string, JToken> Opts { get; set; } = new();
    }

    /// <summary>C# 版宽松解析规则（等价 Mac 的 DefInt/DefString/DefArray/DefDict 与 Go 时间）。</summary>
    [Test]
    public void JsonLeniency()
    {
        var a = Json.Deserialize<Lenient>("{\"n\": 2.9, \"l\": null, \"opt\": null, \"s\": null, \"xs\": null, \"unknown\": [1], \"at\": \"bad\", \"raw\": {\"t\": \"2026-11-18T09:00:00Z\"}, \"opts\": {\"a\": null, \"b\": 1}}");
        Assert.Equal(2, a.N); // 小数截断
        Assert.Equal(0L, a.L);
        Assert.Null(a.Opt);
        Assert.Equal("", a.S);
        Assert.Equal(0, a.Xs.Count);
        Assert.Null(a.At); // 解析不了的时间给 null
        Assert.Equal(JTokenType.String, a.Raw.Get("t")!.Type); // 动态 JSON 里的时间保持字符串
        KitAssert.Seq(new[] { "b" }, a.Opts.Keys);
        var b = Json.Deserialize<Lenient>("{}");
        Assert.Equal(0, b.Xs.Count);
        Assert.Equal(-3, Json.Deserialize<Lenient>("{\"n\": \"-3\"}").N);

        // 计算属性不进 JSON；字典键不改大小写；null 不写
        var text = Json.Serialize(new Run { Id = "r", State = RunState.Running, Urls = { "http://x" } });
        Assert.False(text.Contains("isActive"));
        Assert.False(text.Contains("exitCode"));
        Assert.True(text.Contains("\"rssMB\":0"));
        var env = Json.Serialize(new ProjectSettings { Env = { ["FOO_BAR"] = "1", ["Path"] = "x" } });
        Assert.True(env.Contains("\"FOO_BAR\"") && env.Contains("\"Path\""));
        // 时间写回 RFC 3339
        Assert.True(Json.Serialize(new Workspace { CreatedAt = GoTime.Parse("2026-11-18T01:00:00Z") }).Contains("\"createdAt\":\"2026-11-18T01:00:00.000Z\""));
        // 事件 data 为 null 时：集合给空，模型报错
        Assert.Equal(0, new DaemonEvent { Type = "x" }.Decode<List<RunBrief>>().Count);
        Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => new DaemonEvent { Type = "x" }.Decode<Run>());
    }

    [Test]
    public void ModelHelpers()
    {
        var s = new ProjectSummary { Runs = { new RunBrief { State = RunState.Exited }, new RunBrief { State = RunState.Stopping } } };
        Assert.True(s.HasActiveRun);
        Assert.Equal(3, Severity.Rank("error"));
        Assert.Equal(0, Severity.Rank(null));
        Assert.Equal("v", new Option { Value = "v" }.DisplayLabel);
        Assert.Equal("L", new Option { Value = "v", Label = "L" }.DisplayLabel);
        Assert.Equal("Temurin 17", new Toolchain { Vendor = "Temurin", Version = "17" }.DisplayName);
        Assert.True(new FormField { Key = "apiToken", Type = "password" }.IsSecret);
        Assert.True(new FormField { Key = "token", Type = "string" }.IsSecret);
        Assert.False(new FormField { Key = "tokenizer", Type = "string" }.IsSecret);
        Assert.Equal("r|t|l", new Diagnosis { RuleId = "r", Title = "t", Line = "l" }.StableKey);
        var f = new RunnableFile { Path = "scripts/dev/run.cmd" };
        Assert.Equal("run.cmd", f.Name);
        Assert.Equal("scripts/dev", f.Directory);
        Assert.Equal("", new RunnableFile { Path = "a.sh" }.Directory);
        Assert.True(new FSEntry { Name = "a.cmd", Runnable = "script" }.IsLaunchable);
        Assert.Equal("", new FSEntry { Name = ".gitignore" }.Ext);
        // with 浅拷贝（Swift 值语义的 var n = r; n.x = …）
        var r = new RunBrief { Id = "a", Urls = { "u" } };
        var n = r with { Urls = new List<string> { "v" } };
        Assert.Equal("u", r.Urls[0]);
        Assert.Equal("v", n.Urls[0]);
    }
}

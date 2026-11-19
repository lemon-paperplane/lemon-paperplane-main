using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit.Tests;

/// <summary>夹具（Mac 的 Tests/PaperplaneKitTests/Fixtures，经 csproj 链接到输出目录 Fixtures\）。</summary>
internal static class FixtureFiles
{
    public static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures");

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(Dir, name.Replace('/', Path.DirectorySeparatorChar)));

    public static string Text(string name) => Encoding.UTF8.GetString(Bytes(name));

    /// <summary>按信封解码（对应 Mac 测试里的 decodeEnvelope(fixture, status: 200)）</summary>
    public static T Decode<T>(string name, int status = 200) => ApiClient.DecodeEnvelope<T>(Bytes(name), status);

    public static T Real<T>(string name) => Decode<T>("real/" + name);

    /// <summary>把 SSE 文本切成事件（LineSplitter + SSEParser）</summary>
    public static List<DaemonEvent> Events(byte[] data)
    {
        var splitter = new LineSplitter();
        var parser = new SSEParser();
        var o = new List<DaemonEvent>();
        foreach (var line in splitter.Append(data))
            if (parser.Feed(line) is { } m) o.Add(Json.Deserialize<DaemonEvent>(m.Data));
        return o;
    }
}

internal static class KitAssert
{
    public static void Seq<T>(IEnumerable<T> expected, IEnumerable<T>? actual, [CallerArgumentExpression(nameof(actual))] string expr = "")
    {
        if (actual == null) throw new AssertFailed($"{expr}：期望 [{string.Join(", ", expected)}]，实际 null");
        var a = actual.ToList();
        var e = expected.ToList();
        if (!e.SequenceEqual(a)) throw new AssertFailed($"{expr}：期望 [{string.Join(", ", e)}]，实际 [{string.Join(", ", a)}]");
    }

    public static void SetEq<T>(IEnumerable<T> expected, IEnumerable<T>? actual, [CallerArgumentExpression(nameof(actual))] string expr = "")
    {
        if (actual == null || !new HashSet<T>(expected).SetEquals(actual))
            throw new AssertFailed($"{expr}：期望集合 {{{string.Join(", ", expected)}}}，实际 {{{(actual == null ? "null" : string.Join(", ", actual))}}}");
    }

    /// <summary>JSON 内容相等（expectedJson 是 JSON 文本）</summary>
    public static void JsonEq(string expectedJson, JToken? actual, [CallerArgumentExpression(nameof(actual))] string expr = "")
    {
        var e = Json.Parse(expectedJson);
        if (actual == null || !JToken.DeepEquals(e, actual))
            throw new AssertFailed($"{expr}：期望 {e.ToString(Newtonsoft.Json.Formatting.None)}，实际 {(actual == null ? "null(缺失)" : actual.ToString(Newtonsoft.Json.Formatting.None))}");
    }

    public static ApiException ThrowsApi(Action a) => Assert.Throws<ApiException>(a);
}

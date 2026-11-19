using System;
using System.Collections.Generic;
using System.Linq;
using static PaperplaneKit.Tests.FixtureFiles;

namespace PaperplaneKit.Tests;

/// <summary>对应 Mac 的 FileModelTests.swift。</summary>
public class FileModelTests
{
    [Test]
    public void FSDecoding()
    {
        var list = Decode<FSListResult>("fs.list.json");
        KitAssert.Seq(new[] { ".github", "node_modules", "src", "package.json", "link" }, list.Entries.Select(e => e.Name));
        Assert.True(list.Entries[0].Hidden);
        Assert.True(list.Entries[1].Ignored && list.Entries[1].Dir);
        Assert.Equal(1234L, list.Entries[3].Size);
        Assert.Equal("json", list.Entries[3].Ext);
        Assert.True(list.Entries[4].Symlink);
        Assert.Equal("2026-11-18T09:00:00.123456789+08:00", list.Entries[0].Mtime); // 原样保留，回传给 expectedMtime
        var read = Decode<FSReadResult>("fs.read.json");
        Assert.Equal("crlf", read.LineEnding);
        Assert.Equal("gb18030", read.Encoding);
        Assert.False(read.TooLarge);
        var hits = Decode<List<FSSearchHit>>("fs.search.json");
        Assert.Equal("src/main", hits.First().Directory);
        Assert.Equal("", hits.Last().Directory);
        var grep = Decode<FSGrepResult>("fs.grep.json");
        Assert.Equal(7, grep.Matches.First().Column);
        Assert.True(grep.Truncated);
    }

    [Test]
    public void WriteConflictErrorCarriesData()
    {
        var e = KitAssert.ThrowsApi(() => ApiClient.DecodeEnvelope<FSWriteResult>(Bytes("fs.write.conflict.json"), 400));
        Assert.Equal("user_error", e.Code);
        KitAssert.JsonEq("\"modifiedExternally\"", e.Data.Get("reason"));
        Assert.Equal("modifiedExternally", e.Reason);
    }

    [Test]
    public void EditorContributionsAndSettings()
    {
        var c = Decode<Contributions>("editor.contributions.json");
        Assert.Equal(5, c.Languages.Count);
        KitAssert.Seq(new[] { "/*", "*/" }, c.Languages[0].BlockComment);
        KitAssert.Seq(new[] { "macos", "windows", "any" }, c.Keymaps.Select(k => k.Platform));
        Assert.Equal("eclipse-macos", c.Keymaps[2].Base);
        Assert.Equal("dark", c.EditorThemes.First().Kind);
        // 老内核没有这些字段时为空数组
        var old = Json.Deserialize<Contributions>("{}");
        Assert.Equal(0, old.Languages.Count);

        var s = Decode<AppSettings>("settings.editor.json");
        Assert.True(s.AutoSyncOnImport);
        Assert.Equal(14, s.Editor?.FontSize);
        Assert.Equal("onFocusChange", s.Editor?.AutoSave);
        // keymap 为空：本平台默认（Mac 是 eclipse-macos，Windows 是 eclipse-windows）
        Assert.Equal("eclipse-windows", s.Editor?.EffectiveKeymap);
        Assert.Equal(4, new EditorSettings().TabSize);
        Assert.Equal(13, new EditorSettings().FontSize);
        Assert.Equal("off", new EditorSettings().AutoSave);
    }

    [Test]
    public void LanguageResolverRules()
    {
        var langs = Decode<Contributions>("editor.contributions.json").Languages;
        Assert.Equal("java", LanguageResolver.Language("App.java", langs)?.Id);
        Assert.Equal("typescript", LanguageResolver.Language("index.ts", langs)?.Id); // priority 高的
        Assert.Equal("tsdecl", LanguageResolver.Language("types.d.ts", langs)?.Id); // 最长扩展名
        Assert.Equal("dockerfile", LanguageResolver.Language("Dockerfile", langs)?.Id);
        Assert.Equal("dockerfile", LanguageResolver.Language("Dockerfile.dev", langs)?.Id);
        Assert.Equal("dockerfile", LanguageResolver.Language("app.dockerfile", langs)?.Id);
        Assert.Null(LanguageResolver.Language("notes.txt", langs));
        Assert.Null(LanguageResolver.Language(".ts", langs)); // 只有扩展名不算
        Assert.True(LanguageResolver.Glob("*.config.*", "vite.config.ts"));
        Assert.False(LanguageResolver.Glob("a?c", "abbc"));
    }

    /// <summary>shared/icons 已作为嵌入资源编进 PaperplaneKit（shared/icons/&lt;名字&gt;.svg）</summary>
    private static readonly HashSet<string> EmbeddedIcons = new(typeof(Json).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("shared/icons/", StringComparison.Ordinal)));

    private static bool IconExists(string name) => EmbeddedIcons.Contains($"shared/icons/{name}.svg");

    [Test]
    public void FileIconsExistInSharedIcons()
    {
        Assert.True(EmbeddedIcons.Count > 100);
        var names = new[]
        {
            "App.java", "package.json", "README.md", "logo.png", "run.sh", "docker-compose.yml", "data.csv",
            "server.pem", "font.woff2", "x.sqlite", "a.zip", "Dockerfile", ".gitignore", ".env.local", "LICENSE",
            "clip.mp4", "song.mp3", "noext", "a.patch", "build.bat", "x.ps1",
        };
        foreach (var n in names)
        {
            var icon = FileIcon.BuiltinName(n);
            if (!IconExists(icon)) Assert.Fail($"{n} → {icon} 不在 shared/icons");
        }
        foreach (var e in new[] { new FSEntry { Name = "src", Path = "src", Dir = true }, new FSEntry { Name = ".git", Path = ".git", Dir = true } })
            foreach (var expanded in new[] { false, true })
            {
                var icon = FileIcon.BuiltinName(e, expanded);
                if (!IconExists(icon)) Assert.Fail(icon + " 不在 shared/icons");
            }
    }

    [Test]
    public void EditorURLs()
    {
        var u = EditorURL.Make(new Uri("http://127.0.0.1:47213"), "ab12", "p1", "src/my file#1.ts", 12, "zh-Hans", "dark");
        Assert.Equal("http://127.0.0.1:47213/editor/index.html#token=ab12&projectId=p1&file=src/my%20file%231.ts&line=12&locale=zh-Hans&theme=dark", u);
        var v = EditorURL.Make(new Uri("http://127.0.0.1:1"), "t", "p", null, null, "en", "light");
        Assert.Equal("token=t&projectId=p&locale=en&theme=light", v.Substring(v.IndexOf('#') + 1));
        // 非 ASCII 按 UTF-8 转义
        var w = EditorURL.Make(new Uri("http://127.0.0.1:1"), "t", "p", "中.txt", 0, "en", "light");
        Assert.True(w.Contains("file=%E4%B8%AD.txt&locale"));
    }
}

/// <summary>真实 daemon 的 fs.* / 编辑器相关响应（Fixtures/real）</summary>
public class RealFileFixtureTests
{
    [Test]
    public void RealFS()
    {
        var list = Real<FSListResult>("fs.list.json");
        var venv = list.Entries.FirstOrDefault(e => e.Name == ".venv");
        Assert.NotNull(venv);
        Assert.True(venv!.Dir && venv.Hidden && venv.Ignored);
        Assert.Equal("py", list.Entries.FirstOrDefault(e => e.Name == "main.py")?.Ext);
        var read = Real<FSReadResult>("fs.read.json");
        Assert.True(read.Content.StartsWith("import time"));
        var stat = Real<List<FSStat>>("fs.stat.json");
        KitAssert.Seq(new[] { true, false }, stat.Select(s => s.Exists));
        var hits = Real<List<FSSearchHit>>("fs.search.json");
        Assert.Equal("main.py", hits.First().Path);
        var grep = Real<FSGrepResult>("fs.grep.json");
        Assert.Equal(10, grep.Matches.First().Line);
        var e = KitAssert.ThrowsApi(() => ApiClient.DecodeEnvelope<FSWriteResult>(Bytes("real/fs.write.conflict.json"), 400));
        Assert.Equal("modifiedExternally", e.Reason);
    }

    [Test]
    public void RealEditorContributionsAndSettings()
    {
        var c = Real<Contributions>("ui.contributions.json");
        Assert.Equal("eclipse-macos", c.DefaultKeymap); // 夹具是 Mac 内核抓的
        Assert.Equal(0, c.Keymaps.Count); // 快捷键插件尚未提供时为空
        var s = Real<AppSettings>("settings.get.json");
        Assert.Equal(4, s.Editor?.TabSize);
        Assert.Equal("", s.Editor?.FontFamily);
        Assert.Equal("eclipse-windows", s.Editor?.EffectiveKeymap);
    }
}

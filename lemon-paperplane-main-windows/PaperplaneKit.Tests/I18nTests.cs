using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PaperplaneKit.Tests;

/// <summary>文案（对应 Mac ParserTests.swift 的 DesignAndI18nTests 里的 i18n 部分）。</summary>
public class I18nTests
{
    [Test]
    public void FilesHaveSameKeysAndPlaceholders()
    {
        var zh = Localizer.LoadEmbedded("zh-Hans");
        var en = Localizer.LoadEmbedded("en");
        Assert.True(zh.Count > 100);
        var missingEn = zh.Keys.Except(en.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var missingZh = en.Keys.Except(zh.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (missingEn.Count > 0) Assert.Fail("en.json 缺少：" + string.Join(", ", missingEn));
        if (missingZh.Count > 0) Assert.Fail("zh-Hans.json 缺少：" + string.Join(", ", missingZh));
        foreach (var kv in zh)
        {
            var a = Placeholders(kv.Value);
            var b = Placeholders(en[kv.Key]);
            if (!a.SetEquals(b)) Assert.Fail($"{kv.Key} 占位符不一致：zh [{string.Join(",", a)}] en [{string.Join(",", b)}]");
        }
        // 文案里不得出现 emoji
        foreach (var kv in zh.Concat(en))
            if (ContainsEmoji(kv.Value)) Assert.Fail(kv.Key + " 含 emoji");
    }

    [Test]
    public void EmbeddedMatchesDisk()
    {
        foreach (var l in Localizer.Supported)
        {
            var disk = Localizer.Load(DesignTestPaths.Shared("i18n", l + ".json"));
            var emb = Localizer.LoadEmbedded(l);
            Assert.Equal(disk.Count, emb.Count);
        }
    }

    [Test]
    public void LocalizerLookupAndResolve()
    {
        var l = new Localizer("zh-Hans", new Dictionary<string, string> { ["hi"] = "你好 {0}" },
            new Dictionary<string, string> { ["hi"] = "Hi {0}", ["only.en"] = "E" });
        Assert.Equal("你好 A", l.T("hi", "A"));
        Assert.Equal("E", l.T("only.en"));
        Assert.Equal("missing.key", l.T("missing.key"));
        Assert.Equal("{0} {1}", Localizer.Format("{0} {1}", null));
        Assert.Equal("a b", Localizer.Format("{0} {1}", new[] { "a", "b" }));
        Assert.Equal("zh-Hans", Localizer.Resolve("system", new[] { "zh-Hant-TW" }));
        Assert.Equal("zh-Hans", Localizer.Resolve("system", new[] { "zh-CN" }));
        Assert.Equal("en", Localizer.Resolve("system", new[] { "ja-JP", "en-US" }));
        Assert.Equal("en", Localizer.Resolve("en", new[] { "zh-Hans" }));
        Assert.Equal("en", Localizer.Resolve(null, Array.Empty<string>()));
    }

    [Test]
    public void EmbeddedLocalizerSwitches()
    {
        var l = Localizer.Embedded("zh-Hans");
        Assert.Equal("柠檬纸飞机", l.T("app.name"));
        l.SetLocale("en");
        Assert.Equal("Lemon Paperplane", l.T("app.name"));
        Assert.True(l.Has("common.ok"));
    }

    private static HashSet<string> Placeholders(string s)
    {
        var output = new HashSet<string>();
        for (int i = 0; i < 10; i++)
            if (s.Contains("{" + i + "}")) output.Add("{" + i + "}");
        return output;
    }

    /// <summary>默认以 emoji 形式显示的字符（Emoji_Presentation 的常用区段）。</summary>
    private static bool ContainsEmoji(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            if ((cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x2600 && cp <= 0x27BF && IsEmojiPresentationBmp(cp)) || (cp >= 0x1F000 && cp <= 0x1F2FF))
                return true;
        }
        return false;
    }

    private static bool IsEmojiPresentationBmp(int cp) =>
        cp is 0x231A or 0x231B or 0x23E9 or 0x23EA or 0x23EB or 0x23EC or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE
            or 0x2614 or 0x2615 or 0x2648 or 0x2649 or 0x264A or 0x264B or 0x264C or 0x264D or 0x264E or 0x264F
            or 0x2650 or 0x2651 or 0x2652 or 0x2653 or 0x267F or 0x2693 or 0x26A1 or 0x26AA or 0x26AB or 0x26BD
            or 0x26BE or 0x26C4 or 0x26C5 or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5 or 0x26FA
            or 0x26FD or 0x2705 or 0x270A or 0x270B or 0x2728 or 0x274C or 0x274E or 0x2753 or 0x2754 or 0x2755
            or 0x2757 or 0x2795 or 0x2796 or 0x2797 or 0x27B0 or 0x27BF;
}

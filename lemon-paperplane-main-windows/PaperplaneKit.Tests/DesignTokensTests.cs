using System.Linq;

namespace PaperplaneKit.Tests;

/// <summary>设计变量（对应 Mac ParserTests.swift 的 testDesignTokensComplete）。</summary>
public class DesignTokensTests
{
    [Test]
    public void EmbeddedTokensComplete()
    {
        var t = DesignTokens.LoadEmbedded();
        var missing = t.MissingColors();
        if (missing.Count > 0) Assert.Fail("缺少颜色：" + string.Join(", ", missing));
        Assert.Equal(0.75, t.Icon.StrokeScaleForSize(14));
        Assert.Equal(0.75, t.Icon.StrokeScaleForSize(16));
        Assert.Equal(0.875, t.Icon.StrokeScaleForSize(20));
        Assert.Equal(1.0, t.Icon.StrokeScaleForSize(32));
        Assert.Equal(40.0, t.Size["toolbarHeight"]);
        Assert.Equal(8.0, t.Spacing["md"]);
        Assert.Equal(6.0, t.Radius["md"]);
        Assert.Equal(13.0, t.Typography["body"].Size);
        Assert.Equal(600, t.Typography["title"].Weight);
        Assert.Equal(16.0, t.Shadow["popup"].Blur);
        Assert.Equal(4.0, t.Motion["toastSeconds"]);
        foreach (var theme in new[] { "light", "dark" })
        {
            foreach (var kv in t.Color[theme])
                if (SvgParser.ParseColor(kv.Value) == null) Assert.Fail($"{theme}.{kv.Key}={kv.Value}");
            foreach (var v in t.Ansi[theme]) Assert.NotNull(SvgParser.ParseColor(v));
        }
    }

    [Test]
    public void FontsAndDefaults()
    {
        var t = DesignTokens.LoadEmbedded();
        var ui = t.FontFamilies("ui", "windows");
        Assert.True(ui.Length >= 2);
        Assert.Equal("Segoe UI", ui[1]);
        Assert.Equal("Consolas", t.FontFamilies("mono", "windows").Last());
        Assert.Equal(0, t.FontFamilies("ui", "nope").Length);
        Assert.Equal(7.0, t.SizeOf("nope", 7));
        Assert.Equal("#3574F0", t.ColorHex("light", "accent"));
        Assert.True(ReferenceEquals(DesignTokens.Shared, DesignTokens.Shared));
    }

    [Test]
    public void DiskMatchesEmbedded()
    {
        var disk = DesignTokens.Load(DesignTestPaths.Shared("design-tokens.json"));
        var emb = DesignTokens.LoadEmbedded();
        Assert.Equal(disk.Color["dark"]["bgContent"], emb.Color["dark"]["bgContent"]);
        Assert.Equal(disk.Size.Count, emb.Size.Count);
    }
}

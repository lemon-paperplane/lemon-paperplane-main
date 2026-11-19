using System;
using System.IO;
using System.Linq;

namespace PaperplaneKit.Tests;

/// <summary>测试里找仓库目录（lemon-paperplane-main，含 shared/design-tokens.json）。</summary>
internal static class DesignTestPaths
{
    private static string? _root;

    public static string RepoRoot
    {
        get
        {
            if (_root != null) return _root;
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "shared", "design-tokens.json"))) dir = dir.Parent;
            return _root = dir?.FullName ?? throw new AssertFailed("找不到 shared/design-tokens.json（仓库目录）");
        }
    }

    public static string Shared(params string[] parts) => Path.Combine(new[] { RepoRoot, "shared" }.Concat(parts).ToArray());
}

/// <summary>SVG 子集渲染器（对应 Mac ParserTests.swift 的 SVGTests 与 PluginIconTests）。</summary>
public class SvgTests
{
    private static void AssertNear(double expected, double actual, double eps, string what)
    {
        if (Math.Abs(expected - actual) > eps) throw new AssertFailed($"{what}：期望 ≈{expected}，实际 {actual}");
    }

    [Test]
    public void AllSharedIconsParse()
    {
        // 嵌入资源（界面实际读取的就是这些）
        var names = SharedAssets.Files("icons", ".svg").ToList();
        Assert.True(names.Count > 100);
        // 与磁盘上的 shared/icons 一一对应
        var onDisk = Directory.GetFiles(DesignTestPaths.Shared("icons"), "*.svg").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(string.Join(",", onDisk), string.Join(",", names.OrderBy(n => n, StringComparer.Ordinal)));
        var frame = new SvgRect(-1, -1, 26, 26);
        foreach (var f in names)
        {
            var doc = SvgParser.Parse(SharedAssets.ReadBytes("icons/" + f)!);
            if (doc.ViewBox != new SvgRect(0, 0, 24, 24)) Assert.Fail($"{f} viewBox {doc.ViewBox}");
            if (doc.Shapes.Count == 0) Assert.Fail(f + " 没有图形");
            if (!doc.IsMonochrome) Assert.Fail(f + " 不是单色");
            foreach (var s in doc.Shapes)
            {
                if (s.Style.Stroke != SvgPaint.CurrentColor) Assert.Fail($"{f} stroke {s.Style.Stroke}");
                if (s.Style.StrokeWidth != 2) Assert.Fail($"{f} stroke-width {s.Style.StrokeWidth}");
                var box = s.Path.Bounds();
                if (!frame.Contains(box)) Assert.Fail($"{f} 超出画布 {box}");
            }
        }
    }

    [Test]
    public void BrandingParses()
    {
        foreach (var f in new[] { "app-icon.svg", "menubar.svg" })
        {
            var doc = SvgParser.Parse(SharedAssets.ReadBytes("branding/" + f)!);
            Assert.True(doc.Shapes.Count > 0);
        }
        var menubar = SvgParser.Parse(SharedAssets.ReadBytes("branding/menubar.svg")!);
        Assert.True(menubar.IsMonochrome);
        var app = SvgParser.Parse(SharedAssets.ReadBytes("branding/app-icon.svg")!);
        Assert.False(app.IsMonochrome);
        Assert.Equal(new SvgRect(0, 0, 1024, 1024), app.ViewBox);
    }

    [Test]
    public void ArcCircleBoundingBox()
    {
        // 用两段弧画一个圆心 (12,12) 半径 10 的圆
        var b = SvgPathParser.Parse("M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0z").Bounds();
        AssertNear(2, b.MinX, 0.05, "minX");
        AssertNear(22, b.MaxX, 0.05, "maxX");
        AssertNear(2, b.MinY, 0.05, "minY");
        AssertNear(22, b.MaxY, 0.05, "maxY");
    }

    [Test]
    public void CompactNumbersAndFlags()
    {
        // 紧凑写法：.5.5、紧挨的旗标 011
        Assert.Equal("1.5,0.5,-20,3", string.Join(",", SvgParser.Numbers("1.5.5-2e1,3").Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var p = SvgPathParser.Parse("M0 0a1 1 0 011 1");
        AssertNear(1, p.CurrentPoint.X, 0.001, "x");
        AssertNear(1, p.CurrentPoint.Y, 0.001, "y");
        var q = SvgPathParser.Parse("M1 1h4v4H1z m10 0 2 2");
        Assert.Equal(new SvgPoint(13, 3), q.CurrentPoint);
    }

    [Test]
    public void SmoothCurvesAndRelative()
    {
        var p = SvgPathParser.Parse("M0 0c1 1 2 1 3 0s2-1 3 0q1 1 2 0t2 0");
        Assert.Equal(new SvgPoint(10, 0), p.CurrentPoint);
    }

    [Test]
    public void ShapesAndTransforms()
    {
        const string svg = @"
<svg viewBox=""0 0 100 50"" fill=""#ff0000"" stroke-width=""3"">
  <g transform=""translate(10 5) scale(2)"" opacity=""0.5"">
    <rect x=""0"" y=""0"" width=""10"" height=""5"" rx=""1""/>
    <circle cx=""5"" cy=""5"" r=""2"" fill=""currentColor""/>
  </g>
  <polygon points=""0,0 10,0 10,10"" fill-rule=""evenodd"" stroke=""#00f"" fill=""none""/>
  <line x1=""0"" y1=""0"" x2=""5"" y2=""5"" stroke=""black"" style=""stroke-linecap:round""/>
  <ellipse cx=""50"" cy=""25"" rx=""10"" ry=""5"" transform=""rotate(90 50 25)""/>
  <defs><path d=""M0 0L100 100""/></defs>
</svg>";
        var doc = SvgParser.Parse(svg);
        Assert.Equal(new SvgRect(0, 0, 100, 50), doc.ViewBox);
        Assert.Equal(5, doc.Shapes.Count); // defs 内的被跳过
        var rect = doc.Shapes[0];
        var rb = rect.Path.Bounds();
        AssertNear(10, rb.X, 1e-9, "rect.x");
        AssertNear(5, rb.Y, 1e-9, "rect.y");
        AssertNear(20, rb.Width, 1e-9, "rect.w");
        AssertNear(10, rb.Height, 1e-9, "rect.h");
        Assert.Equal(0.5, rect.Style.Opacity);
        AssertNear(6, rect.Style.StrokeWidth, 0.001, "strokeWidth 随缩放");
        Assert.Equal(SvgPaint.CurrentColor, doc.Shapes[1].Style.Fill);
        Assert.True(doc.Shapes[2].Style.EvenOdd);
        Assert.Equal(SvgPaint.None, doc.Shapes[2].Style.Fill);
        Assert.Equal(SvgLineCap.Round, doc.Shapes[3].Style.LineCap);
        var e = doc.Shapes[4].Path.Bounds();
        AssertNear(10, e.Width, 0.01, "旋转 90° 后宽高互换 w");
        AssertNear(20, e.Height, 0.01, "旋转 90° 后宽高互换 h");
        Assert.False(doc.IsMonochrome);
    }

    [Test]
    public void Colors()
    {
        Assert.Equal(SvgPaint.Color(1, 1, 1, 1), SvgParser.Paint("#fff"));
        Assert.Equal(SvgPaint.Color(1, 0, 0, 1), SvgParser.Paint("rgb(255,0,0)"));
        Assert.Equal(SvgPaint.CurrentColor, SvgParser.Paint("currentColor"));
        Assert.Equal(SvgPaint.None, SvgParser.Paint("none"));
        Assert.Null(SvgParser.Paint("url(#grad)"));
        var c = SvgParser.ParseColor("#3574F080");
        Assert.NotNull(c);
        AssertNear(128.0 / 255, c!.Value.A, 0.001, "alpha");
    }

    [Test]
    public void XmlPrologCommentsEntitiesAndErrors()
    {
        var doc = SvgParser.Parse("﻿<?xml version=\"1.0\"?>\n<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"x\" [ <!ENTITY a \"b\"> ]>\n" +
                                  "<!-- c --><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"8\"><title>a &amp; b</title>" +
                                  "<svg:path d=\"M0 0H4\" stroke=\"&#x23;000\"/></svg>");
        Assert.Equal(new SvgRect(0, 0, 16, 8), doc.ViewBox);
        Assert.Equal(1, doc.Shapes.Count);
        Assert.Equal(SvgPaint.Color(0, 0, 0, 1), doc.Shapes[0].Style.Stroke);
        Assert.Throws<SvgException>(() => SvgParser.Parse("<svg><path d=\"M0 0\"></svg>"));
        Assert.Throws<SvgException>(() => SvgParser.Parse("<html/>"));
        Assert.Throws<SvgException>(() => SvgParser.Parse("not xml"));
    }

    /// <summary>用本机已有的插件仓库里的图标检验（同步到 Windows 时要一起带上 lemon-paperplane-plugin-*/icons；没有时跳过）。</summary>
    [Test]
    public void SiblingPluginIconsParse()
    {
        var parent = Directory.GetParent(DesignTestPaths.RepoRoot)!.FullName;
        int count = 0;
        foreach (var d in Directory.GetDirectories(parent, "lemon-paperplane-plugin-*"))
        {
            var icons = Path.Combine(d, "icons");
            if (!Directory.Exists(icons)) continue;
            foreach (var f in Directory.GetFiles(icons, "*.svg"))
            {
                SvgDocument doc;
                try { doc = SvgParser.Parse(File.ReadAllBytes(f)); }
                catch (Exception e) { throw new AssertFailed($"{Path.GetFileName(d)}/{Path.GetFileName(f)}：{e.Message}"); }
                if (doc.Shapes.Count == 0) Assert.Fail($"{Path.GetFileName(d)}/{Path.GetFileName(f)} 没有图形");
                if (!(doc.ViewBox.Width > 0)) Assert.Fail($"{Path.GetFileName(d)}/{Path.GetFileName(f)} viewBox");
                foreach (var s in doc.Shapes)
                    if (s.Path.Bounds().IsNull && s.Path.Segments.Count > 0) Assert.Fail($"{Path.GetFileName(d)}/{Path.GetFileName(f)} 路径无效");
                count++;
            }
        }
        Console.WriteLine($"        插件图标 {count} 个");
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PaperplaneKit;

// 只支持 docs/design-system.md 第 5 节规定的 SVG 子集：
// 元素 svg g path circle ellipse rect line polyline polygon；
// 属性 viewBox fill stroke stroke-width stroke-linecap stroke-linejoin fill-rule opacity transform
// （另外容忍 fill-opacity / stroke-opacity / style 里的同名声明）。defs 等未知容器整体跳过。
// 对应 Mac 的 PaperplaneKit/SVG/SVGParser.swift，行为以那边的单元测试为准。

public enum SvgPaintKind { None, CurrentColor, Color }

/// <summary>填充或描边：无、currentColor、固定颜色（0–1 的 RGBA）。</summary>
public readonly struct SvgPaint : IEquatable<SvgPaint>
{
    public readonly SvgPaintKind Kind;
    public readonly double R, G, B, A;

    private SvgPaint(SvgPaintKind kind, double r, double g, double b, double a) { Kind = kind; R = r; G = g; B = b; A = a; }

    public static readonly SvgPaint None = new(SvgPaintKind.None, 0, 0, 0, 0);
    public static readonly SvgPaint CurrentColor = new(SvgPaintKind.CurrentColor, 0, 0, 0, 1);
    public static SvgPaint Color(double r, double g, double b, double a) => new(SvgPaintKind.Color, r, g, b, a);

    public bool Equals(SvgPaint o) => Kind == o.Kind && (Kind != SvgPaintKind.Color || (R == o.R && G == o.G && B == o.B && A == o.A));
    public override bool Equals(object? obj) => obj is SvgPaint p && Equals(p);
    public override int GetHashCode() => Kind == SvgPaintKind.Color ? (Kind, R, G, B, A).GetHashCode() : Kind.GetHashCode();
    public static bool operator ==(SvgPaint a, SvgPaint b) => a.Equals(b);
    public static bool operator !=(SvgPaint a, SvgPaint b) => !a.Equals(b);
    public override string ToString() => Kind == SvgPaintKind.Color
        ? string.Format(CultureInfo.InvariantCulture, "rgba({0:0.###},{1:0.###},{2:0.###},{3:0.###})", R, G, B, A)
        : Kind.ToString();
}

public enum SvgLineCap { Butt, Round, Square }
public enum SvgLineJoin { Miter, Round, Bevel }

public struct SvgShapeStyle
{
    public SvgPaint Fill;
    public SvgPaint Stroke;
    public double StrokeWidth;
    public SvgLineCap LineCap;
    public SvgLineJoin LineJoin;
    public bool EvenOdd;
    public double Opacity;
    public double FillOpacity;
    public double StrokeOpacity;

    /// <summary>SVG 默认值：填充黑色、无描边、线宽 1。</summary>
    public static SvgShapeStyle Default => new()
    {
        Fill = SvgPaint.Color(0, 0, 0, 1),
        Stroke = SvgPaint.None,
        StrokeWidth = 1,
        LineCap = SvgLineCap.Butt,
        LineJoin = SvgLineJoin.Miter,
        EvenOdd = false,
        Opacity = 1,
        FillOpacity = 1,
        StrokeOpacity = 1,
    };
}

public sealed class SvgShape
{
    public SvgPath Path { get; }
    public SvgShapeStyle Style { get; }
    public SvgShape(SvgPath path, SvgShapeStyle style) { Path = path; Style = style; }
}

public sealed class SvgDocument
{
    public SvgRect ViewBox { get; }
    public IReadOnlyList<SvgShape> Shapes { get; }

    public SvgDocument(SvgRect viewBox, IReadOnlyList<SvgShape> shapes) { ViewBox = viewBox; Shapes = shapes; }

    /// <summary>只用 currentColor（或无色）着色：可以作为模板图随前景色着色。</summary>
    public bool IsMonochrome => Shapes.All(s => s.Style.Fill.Kind != SvgPaintKind.Color && s.Style.Stroke.Kind != SvgPaintKind.Color);
}

public static class SvgParser
{
    public static SvgDocument Parse(byte[] data) => Parse(SharedAssets.DecodeUtf8(data));

    public static SvgDocument Parse(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(ms.ToArray());
    }

    public static SvgDocument Parse(string text)
    {
        var shapes = new List<SvgShape>();
        var stack = new List<Context>();
        SvgRect? viewBox = null;
        int skipDepth = 0;

        new SvgXmlReader(text).Read((name, attrs) =>
        {
            var local = LocalName(name);
            if (skipDepth > 0) { skipDepth++; return; }
            var ctx = stack.Count > 0 ? stack[stack.Count - 1] : new Context(SvgShapeStyle.Default, SvgMatrix.Identity);
            if (local == "svg" && stack.Count == 0)
            {
                viewBox = ParseViewBox(attrs);
                ctx = new Context(SvgShapeStyle.Default, SvgMatrix.Identity);
            }
            var style = ctx.Style;
            ApplyPresentation(attrs, ref style);
            var transform = ctx.Transform;
            if (attrs.TryGetValue("transform", out var t)) transform = ParseTransform(t).Then(transform);
            ctx = new Context(style, transform);
            switch (local)
            {
                case "svg":
                case "g":
                    stack.Add(ctx);
                    break;
                case "path":
                case "circle":
                case "ellipse":
                case "rect":
                case "line":
                case "polyline":
                case "polygon":
                    stack.Add(ctx);
                    var path = Geometry(local, attrs);
                    if (path != null)
                    {
                        var tm = ctx.Transform;
                        var s = ctx.Style;
                        double det = Math.Abs(tm.Determinant);
                        if (det > 0) s.StrokeWidth *= Math.Sqrt(det);
                        shapes.Add(new SvgShape(path.Transformed(tm), s));
                    }
                    break;
                default:
                    skipDepth = 1; // 不认识的容器（defs、title、mask…）内部全部跳过
                    break;
            }
        }, _ =>
        {
            if (skipDepth > 0) { skipDepth--; return; }
            if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
        });

        if (viewBox == null) throw new SvgException("不是 SVG（根元素不是 svg）");
        return new SvgDocument(viewBox.Value, shapes);
    }

    private readonly struct Context
    {
        public readonly SvgShapeStyle Style;
        public readonly SvgMatrix Transform;
        public Context(SvgShapeStyle style, SvgMatrix transform) { Style = style; Transform = transform; }
    }

    private static string LocalName(string name)
    {
        int i = name.LastIndexOf(':');
        return i >= 0 ? name.Substring(i + 1) : name;
    }

    // MARK: 属性

    internal static SvgRect ParseViewBox(Dictionary<string, string> attrs)
    {
        if (attrs.TryGetValue("viewBox", out var vb))
        {
            var n = Numbers(vb);
            if (n.Count == 4 && n[2] > 0 && n[3] > 0) return new SvgRect(n[0], n[1], n[2], n[3]);
        }
        double w = attrs.TryGetValue("width", out var ws) && Numbers(ws).Count > 0 ? Numbers(ws)[0] : 24;
        double h = attrs.TryGetValue("height", out var hs) && Numbers(hs).Count > 0 ? Numbers(hs)[0] : 24;
        return new SvgRect(0, 0, w, h);
    }

    internal static void ApplyPresentation(Dictionary<string, string> attrs, ref SvgShapeStyle style)
    {
        var all = attrs;
        if (attrs.TryGetValue("style", out var st))
        {
            all = new Dictionary<string, string>(attrs, StringComparer.Ordinal);
            foreach (var decl in st.Split(';'))
            {
                int colon = decl.IndexOf(':');
                if (colon <= 0) continue;
                all[decl.Substring(0, colon).Trim()] = decl.Substring(colon + 1).Trim();
            }
        }
        if (all.TryGetValue("fill", out var f) && Paint(f) is SvgPaint fp) style.Fill = fp;
        if (all.TryGetValue("stroke", out var s) && Paint(s) is SvgPaint sp) style.Stroke = sp;
        if (all.TryGetValue("stroke-width", out var sw) && FirstNumber(sw) is double swn) style.StrokeWidth = swn;
        if (all.TryGetValue("stroke-linecap", out var cap))
            style.LineCap = cap.Trim() switch { "round" => SvgLineCap.Round, "square" => SvgLineCap.Square, _ => SvgLineCap.Butt };
        if (all.TryGetValue("stroke-linejoin", out var join))
            style.LineJoin = join.Trim() switch { "round" => SvgLineJoin.Round, "bevel" => SvgLineJoin.Bevel, _ => SvgLineJoin.Miter };
        if (all.TryGetValue("fill-rule", out var fr)) style.EvenOdd = fr.Trim() == "evenodd";
        if (all.TryGetValue("opacity", out var op) && FirstNumber(op) is double opn) style.Opacity *= opn;
        if (all.TryGetValue("fill-opacity", out var fo) && FirstNumber(fo) is double fon) style.FillOpacity = fon;
        if (all.TryGetValue("stroke-opacity", out var so) && FirstNumber(so) is double son) style.StrokeOpacity = son;
    }

    private static double? FirstNumber(string s)
    {
        var n = Numbers(s);
        return n.Count > 0 ? n[0] : null;
    }

    private static readonly Dictionary<string, (double, double, double)> NamedColors = new()
    {
        ["black"] = (0, 0, 0), ["white"] = (1, 1, 1), ["red"] = (1, 0, 0), ["green"] = (0, 0.5, 0), ["lime"] = (0, 1, 0),
        ["blue"] = (0, 0, 1), ["yellow"] = (1, 1, 0), ["orange"] = (1, 0.647, 0), ["gray"] = (0.5, 0.5, 0.5),
        ["grey"] = (0.5, 0.5, 0.5), ["silver"] = (0.75, 0.75, 0.75), ["purple"] = (0.5, 0, 0.5), ["navy"] = (0, 0, 0.5),
        ["teal"] = (0, 0.5, 0.5), ["maroon"] = (0.5, 0, 0), ["olive"] = (0.5, 0.5, 0),
    };

    /// <summary>解析 fill / stroke 的值；认不出时返回 null（保持继承值）。</summary>
    public static SvgPaint? Paint(string raw)
    {
        var lower = raw.Trim().ToLowerInvariant();
        if (lower == "none" || lower == "transparent") return SvgPaint.None;
        if (lower == "currentcolor") return SvgPaint.CurrentColor;
        var c = ParseColor(lower);
        if (c.HasValue) return SvgPaint.Color(c.Value.R, c.Value.G, c.Value.B, c.Value.A);
        return null;
    }

    /// <summary>解析颜色（#rgb #rgba #rrggbb #rrggbbaa rgb() rgba() 常见颜色名），返回 0–1 的 RGBA。</summary>
    public static (double R, double G, double B, double A)? ParseColor(string raw)
    {
        var v = raw.Trim().ToLowerInvariant();
        if (v.StartsWith("#", StringComparison.Ordinal))
        {
            var hex = v.Substring(1);
            if (hex.Length == 3 || hex.Length == 4)
            {
                var sb = new StringBuilder();
                foreach (var c in hex) sb.Append(c).Append(c);
                return ParseColor("#" + sb);
            }
            if (hex.Length == 6 || hex.Length == 8)
            {
                double? H(int i) => int.TryParse(hex.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x) ? x / 255.0 : null;
                if (H(0) is not double r || H(2) is not double g || H(4) is not double b) return null;
                double a = hex.Length == 8 ? (H(6) ?? 1) : 1;
                return (r, g, b, a);
            }
            return null;
        }
        if (v.StartsWith("rgb", StringComparison.Ordinal))
        {
            var n = Numbers(v.Replace("%", ""));
            if (n.Count < 3) return null;
            return (n[0] / 255, n[1] / 255, n[2] / 255, n.Count >= 4 ? n[3] : 1);
        }
        if (NamedColors.TryGetValue(v, out var nc)) return (nc.Item1, nc.Item2, nc.Item3, 1);
        return null;
    }

    // MARK: 变换

    /// <summary>transform 列表（translate scale rotate matrix skewX skewY），从左到右书写、最右边的最先应用。</summary>
    public static SvgMatrix ParseTransform(string raw)
    {
        var result = SvgMatrix.Identity;
        int idx = 0;
        while (idx < raw.Length)
        {
            int open = raw.IndexOf('(', idx);
            if (open < 0) break;
            int close = raw.IndexOf(')', open);
            if (close < 0) break;
            var name = raw.Substring(idx, open - idx).Trim(' ', ',', '\t', '\n', '\r');
            var args = Numbers(raw.Substring(open + 1, close - open - 1));
            double Arg(int i, double d) => args.Count > i ? args[i] : d;
            var t = SvgMatrix.Identity;
            switch (name)
            {
                case "translate":
                    t = SvgMatrix.Translate(Arg(0, 0), Arg(1, 0));
                    break;
                case "scale":
                {
                    double sx = Arg(0, 1);
                    t = SvgMatrix.Scale(sx, Arg(1, sx));
                    break;
                }
                case "rotate":
                {
                    double a = Arg(0, 0) * Math.PI / 180;
                    if (args.Count >= 3)
                        t = SvgMatrix.Translate(-args[1], -args[2]).Then(SvgMatrix.Rotate(a)).Then(SvgMatrix.Translate(args[1], args[2]));
                    else
                        t = SvgMatrix.Rotate(a);
                    break;
                }
                case "matrix":
                    if (args.Count == 6) t = new SvgMatrix(args[0], args[1], args[2], args[3], args[4], args[5]);
                    break;
                case "skewX":
                    t = new SvgMatrix(1, 0, Math.Tan(Arg(0, 0) * Math.PI / 180), 1, 0, 0);
                    break;
                case "skewY":
                    t = new SvgMatrix(1, Math.Tan(Arg(0, 0) * Math.PI / 180), 0, 1, 0, 0);
                    break;
            }
            result = t.Then(result);
            idx = close + 1;
        }
        return result;
    }

    // MARK: 几何

    internal static SvgPath? Geometry(string name, Dictionary<string, string> a)
    {
        double? Num(string k) => a.TryGetValue(k, out var v) ? FirstNumber(v) : null;
        switch (name)
        {
            case "path":
                return a.TryGetValue("d", out var d) ? SvgPathParser.Parse(d) : null;
            case "circle":
            {
                if (Num("r") is not double r || r <= 0) return null;
                double cx = Num("cx") ?? 0, cy = Num("cy") ?? 0;
                var p = new SvgPath();
                p.AddEllipse(new SvgRect(cx - r, cy - r, r * 2, r * 2));
                return p;
            }
            case "ellipse":
            {
                if (Num("rx") is not double rx || Num("ry") is not double ry || rx <= 0 || ry <= 0) return null;
                double cx = Num("cx") ?? 0, cy = Num("cy") ?? 0;
                var p = new SvgPath();
                p.AddEllipse(new SvgRect(cx - rx, cy - ry, rx * 2, ry * 2));
                return p;
            }
            case "rect":
            {
                if (Num("width") is not double w || Num("height") is not double h || w <= 0 || h <= 0) return null;
                var rect = new SvgRect(Num("x") ?? 0, Num("y") ?? 0, w, h);
                double? rx = Num("rx"), ry = Num("ry");
                rx ??= ry;
                ry ??= rx;
                double crx = Math.Min(Math.Max(rx ?? 0, 0), w / 2), cry = Math.Min(Math.Max(ry ?? 0, 0), h / 2);
                var p = new SvgPath();
                if (crx > 0 && cry > 0) p.AddRoundedRect(rect, crx, cry);
                else p.AddRect(rect);
                return p;
            }
            case "line":
            {
                var p = new SvgPath();
                p.MoveTo(new SvgPoint(Num("x1") ?? 0, Num("y1") ?? 0));
                p.LineTo(new SvgPoint(Num("x2") ?? 0, Num("y2") ?? 0));
                return p;
            }
            case "polyline":
            case "polygon":
            {
                var n = Numbers(a.TryGetValue("points", out var pts) ? pts : "");
                if (n.Count < 4) return null;
                var p = new SvgPath();
                p.MoveTo(new SvgPoint(n[0], n[1]));
                for (int i = 2; i + 1 < n.Count; i += 2) p.LineTo(new SvgPoint(n[i], n[i + 1]));
                if (name == "polygon") p.Close();
                return p;
            }
            default:
                return null;
        }
    }

    /// <summary>从字符串里取出全部数字（容忍逗号、空白、紧挨的负号与小数点，如 "1.5.5-2"）。</summary>
    public static List<double> Numbers(string s)
    {
        var scanner = new SvgNumberScanner(s);
        var output = new List<double>();
        while (scanner.Next() is double n) output.Add(n);
        return output;
    }
}

/// <summary>SVG 数字扫描器（与 Mac 的 NumberScanner 相同）。</summary>
internal sealed class SvgNumberScanner
{
    private readonly string _s;
    public int Pos;

    public SvgNumberScanner(string s) { _s = s; }

    public bool AtEnd => Pos >= _s.Length;
    public char Current => _s[Pos];

    public void SkipSeparators()
    {
        while (Pos < _s.Length)
        {
            char c = _s[Pos];
            if (c == ' ' || c == ',' || c == '\t' || c == '\n' || c == '\r') Pos++;
            else break;
        }
    }

    /// <summary>读一个旗标（arc 的 large-arc / sweep 可以写成紧挨的 0/1）。</summary>
    public bool? NextFlag()
    {
        SkipSeparators();
        if (Pos >= _s.Length) return null;
        char c = _s[Pos];
        if (c == '0') { Pos++; return false; }
        if (c == '1') { Pos++; return true; }
        return null;
    }

    private static bool IsNumberStart(char c) => (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.';

    public double? Next()
    {
        // 跳过非数字字符（单位、括号等）
        while (Pos < _s.Length && !IsNumberStart(_s[Pos])) Pos++;
        return ReadNumber();
    }

    public bool PeekNumberAvailable()
    {
        SkipSeparators();
        return Pos < _s.Length && IsNumberStart(_s[Pos]);
    }

    public double? ReadNumber()
    {
        SkipSeparators();
        if (Pos >= _s.Length) return null;
        int start = Pos;
        if (_s[Pos] == '-' || _s[Pos] == '+') Pos++;
        bool sawDigit = false, sawDot = false;
        while (Pos < _s.Length)
        {
            char c = _s[Pos];
            if (c >= '0' && c <= '9') { sawDigit = true; Pos++; }
            else if (c == '.' && !sawDot) { sawDot = true; Pos++; }
            else break;
        }
        if (Pos < _s.Length && sawDigit && (_s[Pos] == 'e' || _s[Pos] == 'E')) // 指数
        {
            int p = Pos + 1;
            if (p < _s.Length && (_s[p] == '-' || _s[p] == '+')) p++;
            if (p < _s.Length && _s[p] >= '0' && _s[p] <= '9')
            {
                Pos = p;
                while (Pos < _s.Length && _s[Pos] >= '0' && _s[Pos] <= '9') Pos++;
            }
        }
        if (!sawDigit) { Pos = start; return null; }
        return double.TryParse(_s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

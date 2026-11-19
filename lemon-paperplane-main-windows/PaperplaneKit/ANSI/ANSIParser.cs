using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PaperplaneKit;

public enum ANSIColorKind : byte
{
    Default,
    Palette,
    Rgb,
}

/// <summary>终端颜色：默认 / 调色板序号（0–255）/ 真彩。default(ANSIColor) 就是默认色。</summary>
public readonly record struct ANSIColor(ANSIColorKind Kind, byte Index, byte R, byte G, byte B)
{
    public static ANSIColor Default => default;
    public static ANSIColor Palette(byte index) => new(ANSIColorKind.Palette, index, 0, 0, 0);
    public static ANSIColor Rgb(byte r, byte g, byte b) => new(ANSIColorKind.Rgb, 0, r, g, b);

    public bool IsDefault => Kind == ANSIColorKind.Default;

    public override string ToString() => Kind switch
    {
        ANSIColorKind.Palette => $"palette({Index})",
        ANSIColorKind.Rgb => $"rgb({R},{G},{B})",
        _ => "default",
    };
}

/// <summary>文字样式。default(ANSIStyle) 就是无样式（Plain）。</summary>
public record struct ANSIStyle
{
    public ANSIColor Fg { get; set; }
    public ANSIColor Bg { get; set; }
    public bool Bold { get; set; }
    public bool Dim { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Inverse { get; set; }
    public bool Strikethrough { get; set; }

    public static ANSIStyle Plain => default;
    public bool IsPlain => this == Plain;
}

public sealed record ANSISpan
{
    public string Text { get; set; }
    public ANSIStyle Style { get; set; }
    public ANSISpan(string text, ANSIStyle style) { Text = text; Style = style; }
}

/// <summary>
/// ANSI 转义解析：支持 SGR（16/256/真彩、粗体、暗淡、斜体、下划线、反显、删除线），
/// 丢弃光标控制等其他 CSI、OSC（含超链接）以及单字符 ESC 序列。
/// 样式状态跨行保留（与终端一致），每个控制台各用一个解析器实例（非线程安全）。
/// 行为与 Mac 的 ANSIParser.swift 逐条一致（docs/design-system.md 第 8 节）。
/// </summary>
public sealed class ANSIParser
{
    public ANSIStyle Style { get; set; }

    public void Reset() => Style = default;

    /// <summary>解析一行（不含换行）。行内的 \r 视为回到行首：只保留最后一段非空内容（进度条）。</summary>
    public List<ANSISpan> Parse(string line)
    {
        var spans = new List<ANSISpan>();
        var current = new StringBuilder();
        var curStyle = Style;
        var segmentStart = 0; // \r 之后的 spans 起点
        var keptBeforeCR = new List<ANSISpan>();

        void Flush()
        {
            if (current.Length == 0) return;
            if (spans.Count > 0 && spans[spans.Count - 1].Style == curStyle && spans.Count > segmentStart)
                spans[spans.Count - 1].Text += current.ToString();
            else
                spans.Add(new ANSISpan(current.ToString(), curStyle));
            current.Clear();
        }

        var n = line.Length;
        var i = 0;
        while (i < n)
        {
            var c = line[i];
            switch (c)
            {
                case '\u001B': // ESC
                {
                    if (i + 1 >= n) { i++; continue; }
                    var next = line[i + 1];
                    if (next == '[') // CSI: ESC [
                    {
                        var j = i + 2;
                        var prm = new StringBuilder();
                        var final = '\0';
                        while (j < n)
                        {
                            var v = line[j];
                            if (v >= '@' && v <= '~') { final = v; break; }
                            prm.Append(v);
                            j++;
                        }
                        if (final == 'm') // SGR
                        {
                            Flush();
                            ApplySGR(prm.ToString());
                            curStyle = Style;
                        }
                        i = j + 1;
                    }
                    else if (next == ']' || next == 'P' || next == 'X' || next == '^' || next == '_')
                    {
                        // OSC: ESC ] ... (BEL | ESC \)；DCS/SOS/PM/APC 同样跳到终止符
                        var j = i + 2;
                        while (j < n)
                        {
                            if (line[j] == '\u0007') { j++; break; }
                            if (line[j] == '\u001B' && j + 1 < n && line[j + 1] == '\\') { j += 2; break; }
                            j++;
                        }
                        i = j;
                    }
                    else
                    {
                        // ESC + 中间字节(0x20–0x2F)* + 终结字节
                        var j = i + 1;
                        while (j < n && line[j] >= ' ' && line[j] <= '/') j++;
                        i = Math.Min(j + 1, n);
                    }
                    break;
                }
                case '\r':
                    Flush();
                    if (spans.Count > segmentStart) keptBeforeCR = spans.GetRange(segmentStart, spans.Count - segmentStart);
                    spans.RemoveRange(segmentStart, spans.Count - segmentStart);
                    segmentStart = spans.Count;
                    i++;
                    break;
                case '\b': // 退格
                    if (current.Length > 0) RemoveLastTextElement(current);
                    else if (spans.Count > segmentStart && spans[spans.Count - 1].Text.Length > 0)
                    {
                        var last = spans[spans.Count - 1];
                        var sb = new StringBuilder(last.Text);
                        RemoveLastTextElement(sb);
                        last.Text = sb.ToString();
                    }
                    i++;
                    break;
                case '\t': // tab 保留
                    current.Append(c);
                    i++;
                    break;
                default:
                    if (c <= '\u001F' || c == '\u007F') { i++; break; } // 其他控制字符丢弃
                    current.Append(c);
                    i++;
                    break;
            }
        }
        Flush();
        // 行尾的 \r（CRLF 残留）不应清空内容
        if (spans.Count == segmentStart && keptBeforeCR.Count > 0) spans.AddRange(keptBeforeCR);
        spans.RemoveAll(s => s.Text.Length == 0);
        return spans;
    }

    /// <summary>去掉全部转义序列，只留文本。</summary>
    public static string Strip(string line) => string.Concat(new ANSIParser().Parse(line).Select(s => s.Text));

    /// <summary>删掉最后一个字（代理对、组合字符算一个，对应 Swift 的 removeLast）。</summary>
    private static void RemoveLastTextElement(StringBuilder sb)
    {
        var starts = StringInfo.ParseCombiningCharacters(sb.ToString());
        if (starts.Length == 0) return;
        var from = starts[starts.Length - 1];
        sb.Remove(from, sb.Length - from);
    }

    private static int? ParseInt(string s) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static byte Clamp(int v) => (byte)Math.Max(0, Math.Min(255, v));

    private void ApplySGR(string raw)
    {
        // 参数用 ; 分隔，子参数可用 :（如 38:2::r:g:b）
        var groups = raw.Length == 0 ? new[] { "" } : raw.Split(';');
        int? IntAt(int k) => k < groups.Length ? ParseInt(groups[k]) : null;
        var s = Style;
        var idx = 0;
        while (idx < groups.Length)
        {
            var g = groups[idx];
            if (g.IndexOf(':') >= 0)
            {
                var sub = g.Split(':').Select(ParseInt).ToList();
                var first = sub[0];
                if (first != null)
                {
                    if ((first == 38 || first == 48) && ExtendedColor(sub.Skip(1).ToList()) is ANSIColor color)
                    {
                        if (first == 38) s.Fg = color; else s.Bg = color;
                    }
                    else if (first == 4)
                    {
                        s.Underline = (sub.Count > 1 ? (sub[1] ?? 1) : 1) != 0;
                    }
                }
                idx++;
                continue;
            }
            var code = ParseInt(g) ?? 0;
            switch (code)
            {
                case 0: s = default; break;
                case 1: s.Bold = true; break;
                case 2: s.Dim = true; break;
                case 3: s.Italic = true; break;
                case 4: case 21: s.Underline = true; break;
                case 7: s.Inverse = true; break;
                case 9: s.Strikethrough = true; break;
                case 22: s.Bold = false; s.Dim = false; break;
                case 23: s.Italic = false; break;
                case 24: s.Underline = false; break;
                case 27: s.Inverse = false; break;
                case 29: s.Strikethrough = false; break;
                case >= 30 and <= 37: s.Fg = ANSIColor.Palette((byte)(code - 30)); break;
                case 39: s.Fg = ANSIColor.Default; break;
                case >= 40 and <= 47: s.Bg = ANSIColor.Palette((byte)(code - 40)); break;
                case 49: s.Bg = ANSIColor.Default; break;
                case >= 90 and <= 97: s.Fg = ANSIColor.Palette((byte)(code - 90 + 8)); break;
                case >= 100 and <= 107: s.Bg = ANSIColor.Palette((byte)(code - 100 + 8)); break;
                case 38:
                case 48:
                    if (IntAt(idx + 1) == 5 && IntAt(idx + 2) is int n)
                    {
                        var c = ANSIColor.Palette(Clamp(n));
                        if (code == 38) s.Fg = c; else s.Bg = c;
                        idx += 2;
                    }
                    else if (IntAt(idx + 1) == 2 && IntAt(idx + 2) is int r && IntAt(idx + 3) is int gg && IntAt(idx + 4) is int b)
                    {
                        var c = ANSIColor.Rgb(Clamp(r), Clamp(gg), Clamp(b));
                        if (code == 38) s.Fg = c; else s.Bg = c;
                        idx += 4;
                    }
                    break;
            }
            idx++;
        }
        Style = s;
    }

    private static ANSIColor? ExtendedColor(List<int?> rest)
    {
        if (rest.Count == 0 || rest[0] is not int mode) return null;
        if (mode == 5 && rest.Count >= 2 && rest[1] is int n) return ANSIColor.Palette(Clamp(n));
        if (mode == 2)
        {
            // 38:2:r:g:b 或 38:2:<色彩空间>:r:g:b
            var vals = rest.Skip(1).ToList();
            var rgb = vals.Count >= 4 ? vals.Skip(vals.Count - 3).ToList() : vals;
            if (rgb.Count == 3 && rgb[0] is int r && rgb[1] is int g && rgb[2] is int b)
                return ANSIColor.Rgb(Clamp(r), Clamp(g), Clamp(b));
        }
        return null;
    }
}

public static class ANSIPalette
{
    /// <summary>256 色调色板中 16–255 的 RGB（0–15 由设计变量的 ansi 数组提供，返回 null）。</summary>
    public static (byte R, byte G, byte B)? Rgb(byte index)
    {
        int i = index;
        if (i < 16) return null;
        if (i < 232)
        {
            var v = i - 16;
            byte[] levels = { 0, 95, 135, 175, 215, 255 };
            return (levels[v / 36], levels[v / 6 % 6], levels[v % 6]);
        }
        var gray = (byte)(8 + (i - 232) * 10);
        return (gray, gray, gray);
    }
}

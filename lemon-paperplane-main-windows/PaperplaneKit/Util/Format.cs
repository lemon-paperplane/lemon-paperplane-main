using System;
using System.Globalization;

namespace PaperplaneKit;

/// <summary>
/// 图标引用（docs/api.md 第 4 节）：builtin:&lt;name&gt; 或 plugin:&lt;pluginId&gt;/&lt;相对路径&gt;。
/// 用 <c>switch (icon) { case IconSource.Builtin b: … case IconSource.Plugin p: … }</c> 区分。
/// </summary>
public abstract record IconSource
{
    private IconSource() { }

    public sealed record Builtin(string Name) : IconSource
    {
        public override string Key => "builtin:" + Name;
    }

    public sealed record Plugin(string Id, string Path) : IconSource
    {
        public override string Key => $"plugin:{Id}/{Path}";
    }

    /// <summary>缓存用的键。</summary>
    public abstract string Key { get; }

    /// <summary>解析引用。没有前缀时：给了 pluginId 就当作该插件目录里的相对路径，否则当作内置图标名（去掉 .svg）。</summary>
    public static IconSource? Parse(string? raw, string? pluginId = null)
    {
        var s = raw?.Trim(' ', '\t');
        if (string.IsNullOrEmpty(s)) return null;
        if (s!.StartsWith("builtin:", StringComparison.Ordinal)) return new Builtin(s.Substring(8));
        if (s.StartsWith("plugin:", StringComparison.Ordinal))
        {
            var rest = s.Substring(7);
            var slash = rest.IndexOf('/');
            if (slash < 0) return null;
            return new Plugin(rest.Substring(0, slash), rest.Substring(slash + 1));
        }
        if (!string.IsNullOrEmpty(pluginId)) return new Plugin(pluginId!, s);
        return new Builtin(s.EndsWith(".svg", StringComparison.Ordinal) ? s.Substring(0, s.Length - 4) : s);
    }
}

/// <summary>界面上的数值格式化（对应 Mac 的 Format；数字按 C printf 规则舍入，输出与 Mac 一致）。</summary>
public static class Format
{
    /// <summary>
    /// 把用户主目录缩写为 ~。Windows 上不区分大小写，主目录后面可以是 \ 或 /。
    /// home 默认 %USERPROFILE%。
    /// </summary>
    public static string AbbreviatePath(string path, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home) || !path.StartsWith(home, StringComparison.OrdinalIgnoreCase)) return path;
        var rest = path.Substring(home.Length);
        if (rest.Length == 0) return "~";
        if (rest[0] == '/' || rest[0] == '\\') return "~" + rest;
        return path;
    }

    /// <summary>内存：MB → 「512 MB」「1.2 GB」「12.5 MB」。</summary>
    public static string Memory(double mb)
    {
        if (mb >= 1024) return Fixed(mb / 1024, 1) + " GB";
        if (mb >= 100) return Fixed(mb, 0) + " MB";
        return Fixed(mb, 1) + " MB";
    }

    /// <summary>CPU 百分比：「12%」「3.5%」。</summary>
    public static string Cpu(double v) => v >= 10 ? Fixed(v, 0) + "%" : Fixed(v, 1) + "%";

    /// <summary>
    /// 文件大小（十进制单位，对应 ByteCountFormatter 的 .file 样式的英文输出）：
    /// 「Zero KB」「1 byte」「999 bytes」「12 KB」「1.5 MB」「1.23 GB」。
    /// </summary>
    public static string Bytes(long n)
    {
        if (n == 0) return "Zero KB";
        var a = Math.Abs((double)n);
        if (a < 1000) return n + (a == 1 ? " byte" : " bytes");
        string[] units = { "KB", "MB", "GB", "TB", "PB", "EB" };
        var v = a / 1000;
        var u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        var digits = u == 0 ? 0 : u == 1 ? 1 : 2;
        var s = Fixed(v, digits);
        if (s.IndexOf('.') >= 0) s = s.TrimEnd('0').TrimEnd('.');
        return (n < 0 ? "-" : "") + s + " " + units[u];
    }

    /// <summary>持续时长：「3s」「2m 5s」「1h 4m」。</summary>
    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(0, Math.Truncate(seconds));
        if (s < 60) return s + "s";
        if (s < 3600) return $"{s / 60}m {s % 60}s";
        return $"{s / 3600}h {s % 3600 / 60}m";
    }

    public static string Duration(TimeSpan t) => Duration(t.TotalSeconds);

    /// <summary>地址显示：去掉 http:// / https:// 前缀与末尾斜杠。</summary>
    public static string ShortURL(string url)
    {
        var s = url;
        foreach (var p in new[] { "http://", "https://" })
            if (s.StartsWith(p, StringComparison.Ordinal)) s = s.Substring(p.Length);
        if (s.EndsWith("/", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
        return s;
    }

    /// <summary>
    /// 等价 C 的 printf("%.Nf")：按二进制值精确舍入、恰好一半时取偶（.NET Framework 的 "F" 格式先截到 15 位再四舍五入，会和 Mac 差一位）。
    /// </summary>
    public static string Fixed(double v, int digits)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        decimal d;
        try
        {
            // G17 足以分辨相邻的 double，据此判断舍入方向
            d = decimal.Parse(v.ToString("G17", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return v.ToString("F" + digits, CultureInfo.InvariantCulture);
        }
        d = Math.Round(d, digits, MidpointRounding.ToEven);
        var s = d.ToString("F" + digits, CultureInfo.InvariantCulture);
        if (v < 0 && !s.StartsWith("-", StringComparison.Ordinal)) s = "-" + s;
        return s;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace PaperplaneKit;

/// <summary>
/// 三端共用资源（shared/ 目录）。编译时作为嵌入资源打进 PaperplaneKit.dll，资源名是 shared/ 下的相对路径，
/// 例如 shared/design-tokens.json、shared/icons/play.svg、shared/i18n/zh-Hans.json（见 PaperplaneKit.csproj）。
/// 对应 Mac 的 SharedResources（Mac 是拷到 App 包里的目录）。
/// </summary>
public static class SharedAssets
{
    private static readonly Assembly Asm = typeof(SharedAssets).Assembly;
    private static string[]? _names;

    /// <summary>全部嵌入资源名（shared/...）。</summary>
    public static IReadOnlyList<string> Names => _names ??= Asm.GetManifestResourceNames().Where(n => n.StartsWith("shared/", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>打开 shared/ 下的资源（relative 例如 "icons/play.svg"），没有时返回 null。</summary>
    public static Stream? Open(string relative) => Asm.GetManifestResourceStream("shared/" + relative.TrimStart('/'));

    public static bool Exists(string relative) => Names.Contains("shared/" + relative.TrimStart('/'));

    public static byte[]? ReadBytes(string relative)
    {
        using var s = Open(relative);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static string? ReadText(string relative)
    {
        var b = ReadBytes(relative);
        return b == null ? null : DecodeUtf8(b);
    }

    /// <summary>某个子目录下的资源文件名（不含目录），例如 Files("icons", ".svg") → ["activity.svg", …]。</summary>
    public static IEnumerable<string> Files(string dir, string? extension = null)
    {
        var prefix = "shared/" + dir.Trim('/') + "/";
        return Names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n.Substring(prefix.Length))
            .Where(n => n.IndexOf('/') < 0 && (extension == null || n.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>UTF-8 解码（去掉 BOM）。</summary>
    public static string DecodeUtf8(byte[] b)
    {
        int start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(b, start, b.Length - start);
    }
}

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PaperplaneKit;

/// <summary>
/// 服务器路径（docs/remote.md 5.5）：一律按服务器的分隔符（system.info 的 pathSeparator）处理，不用本机的 System.IO.Path
/// （Windows 连 Mac / Linux 服务器时是 /home/…，Mac 连 Windows 服务器时是 C:\…）。
/// sep 为 "\\" 时按 Windows 规则（\ 和 / 都算分隔符、盘符、不区分大小写），否则按 POSIX 规则。
/// </summary>
public static class ServerPath
{
    public static bool IsWindows(string sep) => sep == "\\";

    private static bool IsSep(char c, string sep) => c == '/' || (IsWindows(sep) && c == '\\');

    private static readonly Regex DriveRoot = new(@"^[A-Za-z]:[\\/]?$", RegexOptions.Compiled);

    /// <summary>是不是根（"/"、"C:\"、"C:"、"\\server\share\"）。</summary>
    public static bool IsRoot(string path, string sep)
    {
        if (path.Length == 0) return false;
        if (!IsWindows(sep)) return path == "/";
        if (DriveRoot.IsMatch(path)) return true;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 2;
        }
        return false;
    }

    /// <summary>是不是绝对路径（POSIX 以 / 开头；Windows 有盘符或是 UNC）。</summary>
    public static bool IsAbsolute(string path, string sep)
    {
        if (path.Length == 0) return false;
        if (!IsWindows(sep)) return path[0] == '/';
        return Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") || path.StartsWith(@"\\", StringComparison.Ordinal) || DriveRoot.IsMatch(path);
    }

    /// <summary>去掉末尾分隔符（根保留，"C:" 补成 "C:\"）。</summary>
    public static string Trim(string path, string sep)
    {
        if (path.Length == 0) return path;
        if (IsWindows(sep) && Regex.IsMatch(path, @"^[A-Za-z]:$")) return path + "\\";
        var p = path;
        while (p.Length > 1 && IsSep(p[p.Length - 1], sep) && !IsRoot(p, sep)) p = p.Substring(0, p.Length - 1);
        return p;
    }

    /// <summary>拼接：dir + 分隔符 + name（name 里的 / 换成服务器分隔符；name 是绝对路径时直接用它）。</summary>
    public static string Join(string dir, string name, string sep)
    {
        var n = IsWindows(sep) ? name.Replace('/', '\\') : name;
        if (n.Length == 0) return dir;
        if (IsAbsolute(n, sep) || dir.Length == 0) return n;
        n = n.TrimStart('/', IsWindows(sep) ? '\\' : '/');
        var d = Trim(dir, sep);
        return d.Length > 0 && IsSep(d[d.Length - 1], sep) ? d + n : d + sep + n;
    }

    /// <summary>最后一段（根返回自身）。</summary>
    public static string Name(string path, string sep)
    {
        var p = Trim(path, sep);
        if (IsRoot(p, sep)) return p;
        for (var i = p.Length - 1; i >= 0; i--)
            if (IsSep(p[i], sep)) return p.Substring(i + 1);
        return p;
    }

    /// <summary>上一级（已在根时返回 null）。</summary>
    public static string? Parent(string path, string sep)
    {
        var p = Trim(path, sep);
        if (p.Length == 0 || IsRoot(p, sep)) return null;
        for (var i = p.Length - 1; i >= 0; i--)
        {
            if (!IsSep(p[i], sep)) continue;
            var head = p.Substring(0, i);
            if (head.Length == 0) return IsWindows(sep) ? null : "/";
            if (IsWindows(sep) && Regex.IsMatch(head, @"^[A-Za-z]:$")) return head + "\\";
            if (IsWindows(sep) && head.StartsWith(@"\\", StringComparison.Ordinal) && IsRoot(head, sep)) return head + "\\";
            return head;
        }
        return null;
    }

    /// <summary>比较用的规范形式（去末尾分隔符；Windows 统一成 \ 并转小写）。</summary>
    public static string Normalize(string path, string sep)
    {
        var p = Trim(path.Trim(), sep);
        return IsWindows(sep) ? p.Replace('/', '\\').ToLowerInvariant() : p;
    }

    public static bool Same(string a, string b, string sep) => Normalize(a, sep) == Normalize(b, sep);

    /// <summary>full 在 root 下面时返回相对路径（服务器分隔符；相同时为 ""），否则 null。</summary>
    public static string? Relative(string root, string full, string sep)
    {
        var r = Normalize(root, sep);
        var f = Normalize(full, sep);
        if (r == f) return "";
        var prefix = r.EndsWith(sep, StringComparison.Ordinal) ? r : r + sep;
        if (!f.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var original = Trim(full.Trim(), sep);
        if (IsWindows(sep)) original = original.Replace('/', '\\');
        return original.Substring(prefix.Length);
    }

    /// <summary>主目录缩写成 ~（Windows 不区分大小写）。</summary>
    public static string Abbreviate(string path, string home, string sep)
    {
        if (home.Length == 0 || path.Length == 0) return path;
        var h = Trim(home, sep);
        var cmp = IsWindows(sep) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(h, cmp)) return path;
        var rest = path.Substring(h.Length);
        if (rest.Length == 0) return "~";
        return IsSep(rest[0], sep) ? "~" + rest : path;
    }

    /// <summary>路径栏的各级（从根到自身）：(名称, 完整路径)。</summary>
    public static List<(string Name, string Path)> Breadcrumbs(string path, string sep)
    {
        var o = new List<(string, string)>();
        string? p = Trim(path, sep);
        while (!string.IsNullOrEmpty(p))
        {
            o.Insert(0, (IsRoot(p!, sep) ? p! : Name(p!, sep), p!));
            p = Parent(p!, sep);
        }
        return o;
    }
}

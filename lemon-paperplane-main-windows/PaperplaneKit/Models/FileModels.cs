using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PaperplaneKit;

// 文件操作（fs.*）与编辑器相关的数据结构，契约见 docs/editor.md。
// 路径一律是相对工程根、"/" 分隔；mtime 是 RFC3339Nano 字符串，原样回传给 expectedMtime。

/// <summary>fs.list 的一项</summary>
public sealed record FSEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Dir { get; set; }
    public long Size { get; set; }
    public string? Mtime { get; set; }
    public bool Symlink { get; set; }
    public bool Hidden { get; set; }
    /// <summary>node_modules / build / .venv 等：界面淡化显示，仍可展开</summary>
    public bool Ignored { get; set; }
    /// <summary>普通文件且有执行权限（Windows 上恒为 false）</summary>
    public bool Exec { get; set; }
    /// <summary>能否作为启动项运行（内核判定）：script | binary | source；空表示不能（文档、数据、图片等）</summary>
    public string? Runnable { get; set; }

    public string Id => Path;

    /// <summary>
    /// 文件树右键里提供「添加到启动」：脚本和可执行程序（和「添加脚本或程序」选择框的默认列表一致；
    /// 没有 #! 的 .py/.js 等源码多是模块或配置文件，要加就在选择框里打开「也显示其他源码文件」）
    /// </summary>
    public bool IsLaunchable => !Dir && (Runnable == "script" || Runnable == "binary");

    /// <summary>扩展名（小写，不带点）</summary>
    public string Ext
    {
        get
        {
            var dot = Name.LastIndexOf('.');
            if (dot <= 0) return "";
            return Name.Substring(dot + 1).ToLowerInvariant();
        }
    }
}

/// <summary>fs.list 输出</summary>
public sealed record FSListResult
{
    public List<FSEntry> Entries { get; set; } = new();
    public bool Truncated { get; set; }
}

/// <summary>fs.read 输出</summary>
public sealed record FSReadResult
{
    public string Content { get; set; } = "";
    public string? Encoding { get; set; }
    public bool Bom { get; set; }
    /// <summary>lf | crlf | mixed</summary>
    public string? LineEnding { get; set; }
    public long Size { get; set; }
    public string? Mtime { get; set; }
    public bool Binary { get; set; }
    public bool TooLarge { get; set; }
    public bool ReadOnly { get; set; }
}

/// <summary>fs.write 输出</summary>
public sealed record FSWriteResult
{
    public string? Mtime { get; set; }
    public long Size { get; set; }
}

/// <summary>fs.stat 的一项</summary>
public sealed record FSStat
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public bool Dir { get; set; }
    public long Size { get; set; }
    public string? Mtime { get; set; }
}

/// <summary>fs.delete 输出（移到回收站）</summary>
public sealed record FSDeleteResult
{
    public List<string> Trashed { get; set; } = new();
}

/// <summary>fs.search 的一项（按文件名模糊匹配）</summary>
public sealed record FSSearchHit
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public double Score { get; set; }
    public string Id => Path;

    /// <summary>所在目录（相对工程根）</summary>
    public string Directory
    {
        get
        {
            var slash = Path.LastIndexOf('/');
            return slash < 0 ? "" : Path.Substring(0, slash);
        }
    }
}

/// <summary>fs.grep 输出</summary>
public sealed record FSGrepResult
{
    public List<FSGrepMatch> Matches { get; set; } = new();
    public bool Truncated { get; set; }
}

public sealed record FSGrepMatch
{
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Text { get; set; } = "";
}

// MARK: - 编辑器设置与插件贡献

/// <summary>settings.editor</summary>
public sealed record EditorSettings
{
    /// <summary>空表示按平台默认（Windows 上 eclipse-windows）</summary>
    public string Keymap { get; set; } = "";
    /// <summary>空表示跟随 App 明暗；否则为插件配色主题 id</summary>
    public string Theme { get; set; } = "";
    public int FontSize { get; set; } = 13;
    public string FontFamily { get; set; } = "";
    public int TabSize { get; set; } = 4;
    public bool InsertSpaces { get; set; } = true;
    public bool WordWrap { get; set; }
    /// <summary>off | onFocusChange | afterDelay</summary>
    public string AutoSave { get; set; } = "off";
    public bool TrimTrailingWhitespace { get; set; }
    public bool InsertFinalNewline { get; set; } = true;
    /// <summary>显示空格与制表符</summary>
    public bool ShowWhitespace { get; set; }

    /// <summary>本平台的默认快捷键方案（Mac 是 eclipse-macos）</summary>
    public const string PlatformDefaultKeymap = "eclipse-windows";

    public string EffectiveKeymap => string.IsNullOrEmpty(Keymap) ? PlatformDefaultKeymap : Keymap;
}

/// <summary>语言贡献（着色模块）</summary>
public sealed record LanguageInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();
    public List<string> Filenames { get; set; } = new();
    public List<string> FilenamePatterns { get; set; } = new();
    public string? Module { get; set; }
    public string? LineComment { get; set; }
    public List<string> BlockComment { get; set; } = new();
    public int Priority { get; set; }
    public string Plugin { get; set; } = "";
}

/// <summary>快捷键方案贡献</summary>
public sealed record KeymapInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>macos | windows | linux | any</summary>
    public string Platform { get; set; } = "";
    public string? File { get; set; }
    public string? Module { get; set; }
    public string? Base { get; set; }
    public string Plugin { get; set; } = "";
}

/// <summary>编辑器配色主题贡献</summary>
public sealed record EditorThemeInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>light | dark</summary>
    public string Kind { get; set; } = "";
    public string? File { get; set; }
    public string Plugin { get; set; } = "";
}

/// <summary>按文件名找语言：精确文件名 &gt; 扩展名 &gt; 文件名模式（glob），同等匹配取 priority 高的（相同取先出现的）。</summary>
public static class LanguageResolver
{
    public static LanguageInfo? Language(string fileName, IReadOnlyList<LanguageInfo> languages)
    {
        var lower = fileName.ToLowerInvariant();
        static LanguageInfo? Best(IEnumerable<LanguageInfo> list)
        {
            LanguageInfo? best = null;
            foreach (var l in list)
                if (best == null || best.Priority < l.Priority) best = l;
            return best;
        }
        var byName = Best(languages.Where(l => l.Filenames.Any(f => f.ToLowerInvariant() == lower)));
        if (byName != null) return byName;
        // 扩展名：最长匹配优先（.d.ts 优先于 .ts）
        var extHits = new List<(int Len, LanguageInfo L)>();
        foreach (var l in languages)
            foreach (var e in l.Extensions)
            {
                var ext = (e.StartsWith(".", StringComparison.Ordinal) ? e : "." + e).ToLowerInvariant();
                if (lower.EndsWith(ext, StringComparison.Ordinal) && lower.Length > ext.Length) extHits.Add((ext.Length, l));
            }
        if (extHits.Count > 0)
        {
            var maxLen = extHits.Max(h => h.Len);
            var l = Best(extHits.Where(h => h.Len == maxLen).Select(h => h.L));
            if (l != null) return l;
        }
        return Best(languages.Where(l => l.FilenamePatterns.Any(p => Glob(p, lower))));
    }

    /// <summary>简单 glob：* 与 ?（不区分大小写）</summary>
    public static bool Glob(string pattern, string name)
    {
        var p = pattern.ToLowerInvariant();
        var s = name.ToLowerInvariant();
        int pi = 0, si = 0, star = -1, mark = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || p[pi] == s[si])) { pi++; si++; }
            else if (pi < p.Length && p[pi] == '*') { star = pi; mark = si; pi++; }
            else if (star >= 0) { pi = star + 1; mark++; si = mark; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }
}

/// <summary>文件类型图标（通用 SVG，shared/icons 里的名字）</summary>
public static class FileIcon
{
    public static string BuiltinName(FSEntry entry, bool expanded = false)
    {
        if (entry.Dir)
        {
            if (entry.Name == ".git") return "folder-git";
            return expanded ? "folder-open" : "folder";
        }
        return BuiltinName(entry.Name);
    }

    public static string BuiltinName(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        switch (lower)
        {
            case "dockerfile": case "makefile": case "procfile": case "jenkinsfile": case "vagrantfile": return "file-cog";
            case "license": case "licence": case "license.md": case "license.txt": case "notice": return "file-text";
            case ".gitignore": case ".gitattributes": case ".gitmodules": case ".dockerignore": case ".npmrc": case ".nvmrc":
            case ".editorconfig": case ".env": case ".env.local": case ".python-version": case ".tool-versions":
                return "file-cog";
        }
        if (lower.StartsWith(".env", StringComparison.Ordinal)) return "file-lock";
        var dot = lower.LastIndexOf('.');
        var ext = dot >= 0 ? lower.Substring(dot + 1) : "";
        switch (ext)
        {
            case "json": case "jsonc": case "json5": return "file-json";
            case "md": case "markdown": case "txt": case "rst": case "adoc": case "log": return "file-text";
            case "png": case "jpg": case "jpeg": case "gif": case "webp": case "svg": case "ico": case "bmp": case "tiff": case "heic": case "avif": return "file-image";
            case "mp4": case "mov": case "webm": case "avi": case "mkv": return "file-video";
            case "mp3": case "wav": case "ogg": case "flac": case "aac": case "m4a": return "file-music";
            case "zip": case "tar": case "gz": case "tgz": case "xz": case "bz2": case "7z": case "rar": case "jar": case "war": case "aar": case "whl": return "file-archive";
            case "sh": case "bash": case "zsh": case "fish": case "bat": case "cmd": case "ps1": return "file-terminal";
            case "yml": case "yaml": case "toml": case "ini": case "cfg": case "conf": case "properties": case "xml": case "plist": case "gradle": case "lock": return "file-cog";
            case "csv": case "tsv": case "xls": case "xlsx": case "ods": return "file-spreadsheet";
            case "pem": case "key": case "crt": case "cer": case "p12": case "jks": case "keystore": return "file-key";
            case "ttf": case "otf": case "woff": case "woff2": return "file-type";
            case "db": case "sqlite": case "sqlite3": case "sql": return "database";
            case "pdf": return "file-text";
            case "diff": case "patch": return "file-diff";
            case "": return "file";
            default: return "file-code";
        }
    }
}

/// <summary>编辑器窗口地址（docs/editor.md 第 5 节）：/editor/index.html#token=…&amp;projectId=…&amp;file=…&amp;line=…&amp;locale=…&amp;theme=…</summary>
public static class EditorURL
{
    /// <summary>返回完整地址文本（不经 Uri 规整，避免片段里的转义被改动）。</summary>
    public static string Make(Uri baseUrl, string token, string projectId, string? file, int? line, string locale, string theme)
    {
        var pairs = new List<(string, string)> { ("token", token), ("projectId", projectId) };
        if (!string.IsNullOrEmpty(file)) pairs.Add(("file", file!));
        if (line is > 0) pairs.Add(("line", line.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        pairs.Add(("locale", locale));
        pairs.Add(("theme", theme));
        var fragment = string.Join("&", pairs.Select(p => p.Item1 + "=" + Escape(p.Item2)));
        var b = baseUrl.AbsoluteUri;
        if (!b.EndsWith("/", StringComparison.Ordinal)) b += "/";
        return b + "editor/index.html#" + fragment;
    }

    /// <summary>百分号转义：保留 ASCII 字母数字与 -._~/，其余按 UTF-8 转成 %XX。</summary>
    internal static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~' || c == '/')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}

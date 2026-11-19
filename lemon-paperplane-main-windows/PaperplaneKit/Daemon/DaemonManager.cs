using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace PaperplaneKit;

/// <summary>
/// 找到并连上本机 daemon（对应 Mac 的 DaemonManager）：读 runtime.json → system.info；
/// 连不上就执行 <c>paperplane.exe daemon --detach</c>（不弹窗口）再轮询。
/// <para>Windows 规则：数据目录默认 %APPDATA%\LemonPaperplane（PAPERPLANE_DATA_DIR 覆盖，与内核一致）；
/// 内核路径按 PAPERPLANE_BIN → 界面程序所在目录的 paperplane.exe → runtime.json 里的 executable 的顺序找。</para>
/// </summary>
public sealed class DaemonManager
{
    public sealed class DaemonException : Exception
    {
        public enum Kind { BinaryNotFound, LaunchFailed, Timeout }

        public DaemonException(Kind kind, string message, IReadOnlyList<string>? tried = null, Exception? inner = null) : base(message, inner)
        {
            FailureKind = kind;
            Tried = tried ?? Array.Empty<string>();
        }

        public Kind FailureKind { get; }
        /// <summary>BinaryNotFound 时试过的路径</summary>
        public IReadOnlyList<string> Tried { get; }
    }

    /// <summary>开发时指定 daemon 路径的环境变量</summary>
    public const string BinaryPathEnvKey = "PAPERPLANE_BIN";
    /// <summary>独立实例的数据目录（与内核一致）</summary>
    public const string DataDirEnvKey = "PAPERPLANE_DATA_DIR";
    /// <summary>内核可执行文件名</summary>
    public const string BinaryName = "paperplane.exe";

    /// <param name="dataDir">默认 DefaultDataDir</param>
    /// <param name="appDirectory">界面程序所在目录（安装后 paperplane.exe 与界面同目录）；默认入口程序集所在目录</param>
    /// <param name="appVersion">界面版本，用来判断运行中的 daemon 要不要换成自带的；默认入口程序集的
    /// AssemblyInformationalVersion（没有就用文件版本）；传 "" 表示不比较版本</param>
    public DaemonManager(string? dataDir = null, string? appDirectory = null, string? appVersion = null)
    {
        DataDir = dataDir ?? DefaultDataDir;
        AppDirectory = appDirectory ?? DefaultAppDirectory();
        AppVersion = appVersion ?? DefaultAppVersion();
    }

    public string DataDir { get; }
    public string? AppDirectory { get; }
    public string AppVersion { get; }
    /// <summary>拉起后等多久（替换别处的 daemon 时再加 15 秒）</summary>
    public TimeSpan LaunchTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>%APPDATA%\LemonPaperplane（PAPERPLANE_DATA_DIR 覆盖，原样使用，与内核 paths.DataDir 一致）</summary>
    public static string DefaultDataDir
    {
        get
        {
            var d = Environment.GetEnvironmentVariable(DataDirEnvKey);
            if (!string.IsNullOrEmpty(d)) return d!;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
                appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
            return Path.Combine(appData, "LemonPaperplane");
        }
    }

    private static string? DefaultAppDirectory()
    {
        var loc = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>入口程序集的 AssemblyInformationalVersion（去掉 +提交号），没有就用文件版本（x.y.z.0 记作 x.y.z）。</summary>
    public static string DefaultAppVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        if (asm == null) return "";
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info!.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
        try
        {
            var fv = FileVersionInfo.GetVersionInfo(asm.Location);
            var v = fv.FileVersion;
            if (string.IsNullOrEmpty(v)) return "";
            return v!.EndsWith(".0", StringComparison.Ordinal) && v.Count(c => c == '.') == 3 ? v.Substring(0, v.Length - 2) : v;
        }
        catch (Exception)
        {
            return "";
        }
    }

    public string RuntimeFile => Path.Combine(DataDir, "runtime.json");

    /// <summary>读 runtime.json；没有、读不了或缺端口/令牌时为 null。</summary>
    public RuntimeInfo? ReadRuntime()
    {
        try
        {
            string text;
            using (var fs = new FileStream(RuntimeFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();
            var rt = Json.Deserialize<RuntimeInfo>(text);
            return rt.Port > 0 && !string.IsNullOrEmpty(rt.Token) ? rt : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>候选 daemon 路径（按优先级）：环境变量 PAPERPLANE_BIN → 界面程序目录 → runtime.json 的 executable</summary>
    public List<string> CandidateBinaries()
    {
        var o = new List<string>();
        var env = Environment.GetEnvironmentVariable(BinaryPathEnvKey);
        if (!string.IsNullOrEmpty(env)) o.Add(env!);
        if (!string.IsNullOrEmpty(AppDirectory)) o.Add(Path.Combine(AppDirectory, BinaryName));
        var exe = ReadRuntime()?.Executable;
        if (!string.IsNullOrEmpty(exe)) o.Add(exe!);
        return o;
    }

    public string? LocateBinary() => CandidateBinaries().FirstOrDefault(File.Exists);

    /// <summary>本 App 自带的 daemon（开发时环境变量指定的优先；界面程序目录里的要存在）。</summary>
    public string? OwnBinary
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(BinaryPathEnvKey);
            if (!string.IsNullOrEmpty(env)) return env;
            if (!string.IsNullOrEmpty(AppDirectory))
            {
                var p = Path.Combine(AppDirectory, BinaryName);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    /// <summary>尝试用现有 runtime.json 连接；成功返回客户端与系统信息，否则 null。</summary>
    public async Task<(ApiClient Client, SystemInfo Info)?> ProbeAsync(TimeSpan? timeout = null)
    {
        var rt = ReadRuntime();
        if (rt == null) return null;
        var client = new ApiClient(rt);
        try
        {
            var info = await client.SystemInfoAsync(timeout ?? TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return (client, info);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 正在运行的 daemon 是否应该换成本 App 自带的：
    /// 来自别处的安装（另一份 App、旧的构建目录）且（插件为 0 / 可执行文件已不存在 / 版本不同 / 没有正在运行的工程）。
    /// 别处安装的 daemon 正在跑工程、且版本一致、插件正常时保留它，避免把用户的服务停掉。
    /// </summary>
    internal async Task<bool> ShouldReplaceAsync(SystemInfo info, ApiClient client)
    {
        var own = OwnBinary;
        if (own == null) return false;
        if (string.IsNullOrEmpty(info.Executable) || SamePath(info.Executable, own)) return false;
        if (info.Plugins == 0) return true;
        if (!File.Exists(info.Executable)) return true;
        if (!string.IsNullOrEmpty(AppVersion) && AppVersion != info.Version) return true;
        List<Run> active;
        try { active = await client.RunsAsync(active: true).ConfigureAwait(false); }
        catch (Exception) { active = new List<Run>(); }
        return active.Count == 0;
    }

    /// <summary>
    /// 连接（必要时拉起 daemon；运行中的 daemon 不属于本 App 或不完整时换成自己的）。
    /// locale 是系统语言（zh-Hans / en），设置为「跟随系统」时 daemon 用它。失败抛 DaemonException。
    /// </summary>
    public async Task<(ApiClient Client, SystemInfo Info)> ConnectAsync(string? locale = null, CancellationToken ct = default)
    {
        var replace = false;
        var probe = await ProbeAsync().ConfigureAwait(false);
        if (probe is { } p)
        {
            if (await ShouldReplaceAsync(p.Info, p.Client).ConfigureAwait(false)) replace = true;
            else return p;
        }
        var bin = replace ? OwnBinary : OwnBinary ?? LocateBinary();
        if (bin == null)
        {
            var tried = CandidateBinaries();
            throw new DaemonException(DaemonException.Kind.BinaryNotFound, "paperplane binary not found: " + string.Join(", ", tried), tried);
        }
        var before = ReadRuntime()?.Pid;
        Launch(bin, locale, replace);
        var deadline = DateTime.UtcNow + LaunchTimeout + (replace ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            if (await ProbeAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false) is { } ok)
            {
                if (replace && ok.Info.Pid == before) continue;
                return ok;
            }
        }
        throw new DaemonException(DaemonException.Kind.Timeout, "daemon did not respond in time");
    }

    /// <summary>
    /// 执行 <c>paperplane.exe daemon --detach [--replace]</c>，不弹黑窗口、不等待（--detach 自己会很快返回，上层轮询 runtime.json）。
    /// locale 用来告诉 daemon 系统语言（设置为「跟随系统」时用）。
    /// </summary>
    public void Launch(string binary, string? locale, bool replace = false)
    {
        var psi = new ProcessStartInfo(binary, replace ? "daemon --detach --replace" : "daemon --detach")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
        };
        var dir = Path.GetDirectoryName(binary);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
        if (!string.IsNullOrEmpty(DataDir) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DataDirEnvKey))
            && !SamePath(DataDir, DefaultDataDir))
        {
            // 用非默认数据目录构造的管理器：让拉起的 daemon 用同一个目录
            psi.EnvironmentVariables[DataDirEnvKey] = DataDir;
        }
        if (!string.IsNullOrEmpty(locale))
        {
            psi.EnvironmentVariables["PAPERPLANE_SYSTEM_LOCALE"] = locale;
            if (string.IsNullOrEmpty(psi.EnvironmentVariables["LANG"]))
                psi.EnvironmentVariables["LANG"] = (locale!.StartsWith("zh", StringComparison.Ordinal) ? "zh_CN" : "en_US") + ".UTF-8";
        }
        try
        {
            using var p = Process.Start(psi);
        }
        catch (Exception e)
        {
            throw new DaemonException(DaemonException.Kind.LaunchFailed, "failed to launch daemon: " + e.Message, inner: e);
        }
    }
}

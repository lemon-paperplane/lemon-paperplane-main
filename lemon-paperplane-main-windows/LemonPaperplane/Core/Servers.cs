using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>
/// 已添加的远程服务器（docs/remote.md 5.2）：{id, name, url, fingerprint, trusted, os, hostname} 存界面偏好，
/// 访问密钥存 Windows 凭据管理器（目标名 LemonPaperplane:server:&lt;id&gt;），不落普通文件。
/// </summary>
public sealed record ServerRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    /// <summary>证书由受信任的 CA 签发（按证书链校验，不看指纹）</summary>
    public bool Trusted { get; set; }
    public string Os { get; set; } = "";
    public string Hostname { get; set; } = "";

    /// <summary>地址的「主机:端口」部分（行里显示）</summary>
    public string Address => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? (u.IsDefaultPort ? u.Host : u.Host + ":" + u.Port) : Url;

    /// <summary>服务器的主机（直连运行地址时把 localhost 换成它）</summary>
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url;
}

/// <summary>服务器列表与当前选择（对应 Mac ServerStore）。只在 UI 线程使用。</summary>
public sealed class ServerStore : PPObservable
{
    public static readonly ServerStore Shared = new();

    private const string PrefsList = "Servers";
    private const string PrefsCurrent = "Server";
    private const string CredentialPrefix = "LemonPaperplane:server:";

    /// <summary>调试桥（自动化测试）运行时不写凭据管理器（同 UiPrefs 不写注册表）：密钥只留在内存里。</summary>
    private static readonly bool ReadOnly = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAPERPLANE_DEBUG_BRIDGE"));

    private List<ServerRecord> _servers;
    private readonly Dictionary<string, string> _memorySecrets = new();
    private readonly Dictionary<string, ServerSummary> _summaries = new();

    /// <summary>本机（当前在远程时）的概况键。</summary>
    public const string LocalKey = "local";

    /// <summary>切换器里每台服务器的概况（打开切换器时刷新）：服务器 id（本机为 LocalKey）→ 概况。</summary>
    public IReadOnlyDictionary<string, ServerSummary> Summaries => _summaries;

    public ServerSummary Summary(string id) => _summaries.TryGetValue(id, out var s) ? s : new ServerSummary();

    public void SetSummary(string id, ServerSummary s)
    {
        _summaries[id] = s;
        Notify(nameof(Summaries));
    }

    private ServerStore()
    {
        _servers = Load();
    }

    public IReadOnlyList<ServerRecord> Servers => _servers;

    public ServerRecord? Find(string? id) => id == null ? null : _servers.FirstOrDefault(s => s.Id == id);

    /// <summary>上次选的服务器（null = 本机；记录已删时也是本机）。</summary>
    public ServerRecord? Remembered => Find(UiPrefs.Get(PrefsCurrent));

    public void Remember(ServerRecord? s) => UiPrefs.Set(PrefsCurrent, s?.Id);

    private static List<ServerRecord> Load()
    {
        try
        {
            var raw = UiPrefs.Get(PrefsList);
            if (string.IsNullOrEmpty(raw)) return new List<ServerRecord>();
            return Json.Deserialize<List<ServerRecord>>(raw!).Where(s => s.Id.Length > 0 && s.Url.Length > 0).ToList();
        }
        catch
        {
            return new List<ServerRecord>();
        }
    }

    private void Save()
    {
        UiPrefs.Set(PrefsList, Json.Serialize(_servers));
        Notify(nameof(Servers));
    }

    /// <summary>新建一条记录的 id。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

    /// <summary>新增或替换（同 id）。</summary>
    public void Put(ServerRecord s)
    {
        var i = _servers.FindIndex(x => x.Id == s.Id);
        if (i >= 0) _servers[i] = s; else _servers.Add(s);
        Save();
    }

    /// <summary>删除本机记录和密钥（不影响那台服务器）。</summary>
    public void Remove(string id)
    {
        _servers.RemoveAll(s => s.Id == id);
        _summaries.Remove(id);
        DeleteSecret(id);
        if (UiPrefs.Get(PrefsCurrent) == id) UiPrefs.Set(PrefsCurrent, null);
        Save();
    }

    /// <summary>同一台服务器（同地址或同证书指纹）已经添加过的记录。</summary>
    public ServerRecord? Duplicate(string url, string fingerprint, string? exceptId = null) =>
        _servers.FirstOrDefault(s => s.Id != exceptId &&
            (string.Equals(s.Url.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
             || (fingerprint.Length > 0 && string.Equals(s.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))));

    // MARK: 访问密钥

    public string? Secret(string id)
    {
        if (_memorySecrets.TryGetValue(id, out var m)) return m;
        try { return CredentialStore.Read(CredentialPrefix + id); }
        catch (Win32Exception e)
        {
            DebugBridge.LogError(e);
            return null;
        }
    }

    /// <summary>保存访问密钥。写凭据管理器失败时抛出（界面显示原因）。</summary>
    public void SetSecret(string id, string token, string name)
    {
        if (ReadOnly)
        {
            _memorySecrets[id] = token;
            return;
        }
        CredentialStore.Write(CredentialPrefix + id, token, comment: "Lemon Paperplane · " + name);
        _memorySecrets.Remove(id);
    }

    public void DeleteSecret(string id)
    {
        _memorySecrets.Remove(id);
        if (ReadOnly) return;
        try { CredentialStore.Delete(CredentialPrefix + id); }
        catch (Win32Exception e) { DebugBridge.LogError(e); }
    }
}

/// <summary>切换器里一台服务器的概况。</summary>
public sealed record ServerSummary
{
    public const string Unknown = "unknown";
    public const string Connecting = "connecting";
    public const string Online = "online";
    public const string Failed = "failed";

    public string State { get; init; } = Unknown;
    public int? Runs { get; init; }
    public DateTime At { get; init; }
}

/// <summary>
/// 打开切换器时刷新每台服务器的状态与运行中数量（对应 Mac ServerSummaries）：本机（当前在远程时）直接问本机内核；
/// 其他远程服务器经网关问一次，问完关掉临时网关。同一台 15 秒内不重复问（避免反复认证失败被服务器封 IP）。
/// </summary>
public static class ServerSummaries
{
    private static readonly HashSet<string> InFlight = new();

    public static void Refresh()
    {
        var model = AppModel.Shared;
        var store = ServerStore.Shared;
        if (model.LocalApi is not { } local) return;
        if (model.IsRemote && InFlight.Add(ServerStore.LocalKey)) _ = RefreshLocal(local);
        foreach (var s in store.Servers)
        {
            if (s.Id == model.Server?.Id || InFlight.Contains(s.Id)) continue;
            var cur = store.Summary(s.Id);
            if ((DateTime.UtcNow - cur.At).TotalSeconds < 15 && cur.State != ServerSummary.Unknown) continue;
            InFlight.Add(s.Id);
            if (cur.State != ServerSummary.Online) store.SetSummary(s.Id, cur with { State = ServerSummary.Connecting });
            _ = Probe(s, local);
        }
    }

    private static async System.Threading.Tasks.Task RefreshLocal(ApiClient local)
    {
        try
        {
            var runs = await local.RunsAsync(active: true);
            ServerStore.Shared.SetSummary(ServerStore.LocalKey, new ServerSummary { State = ServerSummary.Online, Runs = runs.Count, At = DateTime.UtcNow });
        }
        catch
        {
            ServerStore.Shared.SetSummary(ServerStore.LocalKey, new ServerSummary { State = ServerSummary.Failed, At = DateTime.UtcNow });
        }
        finally { InFlight.Remove(ServerStore.LocalKey); }
    }

    private static async System.Threading.Tasks.Task Probe(ServerRecord s, ApiClient local)
    {
        var store = ServerStore.Shared;
        try
        {
            if (store.Secret(s.Id) is not { Length: > 0 } token) throw new ApiException(RemoteErrorCode.Unauthorized, "");
            var gw = await local.GatewayOpenAsync(s.Url, s.Fingerprint, s.Trusted, token);
            if (gw.Info.Os.Length > 0 && (gw.Info.Os != s.Os || gw.Info.Hostname != s.Hostname) && store.Find(s.Id) is { } rec)
                store.Put(rec with { Os = gw.Info.Os, Hostname = gw.Info.Hostname });
            int? count = null;
            try { count = (await new ApiClient(new Uri("http://127.0.0.1:" + gw.Port), gw.Token, TimeSpan.FromSeconds(15)).RunsAsync(active: true)).Count; }
            catch { }
            // 只是问一下：不是当前服务器的网关用完就关
            if (gw.Id != AppModel.Shared.GatewayId && AppModel.Shared.Server?.Id != s.Id)
            {
                try { await local.GatewayCloseAsync(gw.Id); } catch { }
            }
            store.SetSummary(s.Id, new ServerSummary { State = ServerSummary.Online, Runs = count, At = DateTime.UtcNow });
        }
        catch
        {
            store.SetSummary(s.Id, new ServerSummary { State = ServerSummary.Failed, At = DateTime.UtcNow });
        }
        finally { InFlight.Remove(s.Id); }
    }
}

/// <summary>服务器的显示：系统图标、系统名、错误码对应的说明。</summary>
public static class ServerDisplay
{
    /// <summary>系统图标（共用图标库里没有品牌图标，同 Mac：macOS 用显示器、Windows 用窗口、Linux 用服务器）。</summary>
    public static string Icon(string? os) => os switch
    {
        "darwin" => "monitor",
        "windows" => "app-window",
        _ => "server",
    };

    public static string OsName(string? os) => os switch
    {
        "darwin" => L.T("server.os.darwin"),
        "windows" => L.T("server.os.windows"),
        "linux" => L.T("server.os.linux"),
        null or "" => "",
        _ => os!,
    };

    /// <summary>「主机名 · 系统」</summary>
    public static string HostLine(string? hostname, string? os)
    {
        var parts = new[] { hostname ?? "", OsName(os) }.Where(x => x.Length > 0);
        return string.Join(" · ", parts);
    }

    /// <summary>网关错误 → 给用户看的原因。</summary>
    public static string ErrorMessage(string? code, string? message, Newtonsoft.Json.Linq.JToken? data = null) => code switch
    {
        RemoteErrorCode.FingerprintMismatch => L.T("server.err.fingerprint"),
        RemoteErrorCode.Unauthorized or RemoteErrorCode.RemoteUnauthorized => L.T("server.err.unauthorized"),
        RemoteErrorCode.RateLimited => L.T("server.err.rateLimited"),
        RemoteErrorCode.Incompatible => L.T("server.err.incompatible",
            data.Get("serverVersion").StringValue() ?? "?", data.Get("clientVersion").StringValue() ?? "?"),
        RemoteErrorCode.PasswordDisabled => L.T("server.err.passwordDisabled"),
        RemoteErrorCode.Unreachable or "http_502" or ApiException.CodeNetwork => L.T("server.err.unreachable", message ?? ""),
        _ => message ?? "",
    };

    public static string ErrorMessage(Exception e) =>
        e is ApiException a ? ErrorMessage(a.Code, a.Message, a.Data) : e.Message;

    /// <summary>需要重新登录（换密钥）的错误。</summary>
    public static bool NeedsLogin(string? code) =>
        code is RemoteErrorCode.Unauthorized or RemoteErrorCode.RemoteUnauthorized or RemoteErrorCode.FingerprintMismatch;
}

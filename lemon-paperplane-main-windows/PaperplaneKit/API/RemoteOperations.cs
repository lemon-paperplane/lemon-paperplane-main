using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit;

// 远程服务器的操作（docs/remote.md 第 4.2 节）。
// gateway.* 只能在本机内核上调（客户端功能）；remote.* 在哪台服务器上调就管哪台（经网关调就是远程那台）。

public sealed partial class ApiClient
{
    // MARK: 客户端网关（本机）

    public Task<GatewayCode> GatewayDecodeAsync(string code) =>
        CallAsync<GatewayCode>("gateway.decode", Json.Object(("code", code)));

    /// <summary>连上服务器取证书指纹（未校验）和 hello。address 可以是 host、host:port 或 https://…</summary>
    public Task<GatewayProbe> GatewayProbeAsync(string address) =>
        CallAsync<GatewayProbe>("gateway.probe", Json.Object(("address", address)), S(40));

    /// <summary>账号密码换一条这台设备专用的访问密钥（密码不保存）。</summary>
    public async Task<string> GatewayLoginAsync(string url, string fingerprint, bool trusted, string user, string password, string device)
    {
        var r = await CallAsync<JToken>("gateway.login", Json.Object(("url", url), ("fingerprint", fingerprint), ("trusted", trusted),
            ("user", user), ("password", password), ("device", device)), S(60)).ConfigureAwait(false);
        return r.Get("token").StringValue() ?? throw new ApiException(ApiException.CodeBadResponse, "missing token");
    }

    /// <summary>给远程服务器开一个回环端口：返回的 port / token 用法和本机 runtime.json 一样。</summary>
    public Task<GatewayOpen> GatewayOpenAsync(string url, string fingerprint, bool trusted, string token) =>
        CallAsync<GatewayOpen>("gateway.open", Json.Object(("url", url), ("fingerprint", fingerprint), ("trusted", trusted), ("token", token)), S(40));

    public Task GatewayCloseAsync(string id) => SendAsync("gateway.close", Json.Object(("id", id)));

    /// <summary>把服务器 localhost:port 转到本机（优先同一个端口号；已转发则直接返回）。</summary>
    public Task<GatewayForward> GatewayForwardAsync(string id, int port) =>
        CallAsync<GatewayForward>("gateway.forward", Json.Object(("id", id), ("port", port)));

    public Task GatewayUnforwardAsync(string id, int port) => SendAsync("gateway.unforward", Json.Object(("id", id), ("port", port)));

    // MARK: 作为服务器（remote.*）

    public Task<RemoteStatus> RemoteStatusAsync() => CallAsync<RemoteStatus>("remote.status");

    /// <summary>只传要改的字段：enabled / port / passwordLogin / autostart / allowCIDRs。</summary>
    public Task<RemoteStatus> RemoteConfigureAsync(bool? enabled = null, int? port = null, bool? passwordLogin = null, bool? autostart = null,
        IEnumerable<string>? allowCIDRs = null) =>
        CallAsync<RemoteStatus>("remote.configure", Json.Object(("enabled", enabled), ("port", port), ("passwordLogin", passwordLogin),
            ("autostart", autostart), ("allowCIDRs", allowCIDRs)), S(30));

    public Task<List<RemoteKey>> RemoteKeysAsync() => CallAsync<List<RemoteKey>>("remote.keys");

    /// <summary>生成访问密钥；token 与连接码只返回这一次。address 是额外的连接地址（外网域名等）。</summary>
    public Task<RemoteKeyCreated> RemoteKeyCreateAsync(string name, string? address = null) =>
        CallAsync<RemoteKeyCreated>("remote.keyCreate", Json.Object(("name", name), ("address", string.IsNullOrWhiteSpace(address) ? null : address)));

    public Task RemoteKeyRevokeAsync(string id) => SendAsync("remote.keyRevoke", Json.Object(("id", id)));

    public Task<List<RemoteUser>> RemoteUsersAsync() => CallAsync<List<RemoteUser>>("remote.users");

    /// <summary>新建账号或改密码（至少 10 位）。</summary>
    public Task RemoteUserSetAsync(string name, string password) =>
        SendAsync("remote.userSet", Json.Object(("name", name), ("password", password)));

    /// <summary>删除账号，同时吊销用它登录过的设备。</summary>
    public Task RemoteUserRemoveAsync(string name) => SendAsync("remote.userRemove", Json.Object(("name", name)));

    public Task<List<RemoteAuditEntry>> RemoteAuditAsync(int limit = 50) =>
        CallAsync<List<RemoteAuditEntry>>("remote.audit", Json.Object(("limit", limit)));

    // MARK: 其他

    /// <summary>列出服务器上任意文件夹（选择服务器上的文件夹用）；path 为空是主目录。</summary>
    public Task<FSBrowseResult> FsBrowseAsync(string? path = null) =>
        CallAsync<FSBrowseResult>("fs.browse", Json.Object(("path", string.IsNullOrEmpty(path) ? null : path)));

    /// <summary>新建文件夹（绝对路径，上一级必须存在），返回新文件夹的路径；选择服务器上的文件夹时用。</summary>
    public async Task<string> FsMkdirAsync(string path)
    {
        var r = await CallAsync<JToken>("fs.mkdir", Json.Object(("path", path))).ConfigureAwait(false);
        return r.Get("path").StringValue() ?? path;
    }

    /// <summary>连接远程服务器时运行地址的打开方式（至少一个为 true；两个都 true 等于默认）。</summary>
    public Task SetRemoteLinksAsync(string projectId, string configId, bool forward, bool direct) =>
        SendAsync("runconfig.setRemoteLinks", Json.Object(("projectId", projectId), ("configId", configId), ("forward", forward), ("direct", direct)));
}

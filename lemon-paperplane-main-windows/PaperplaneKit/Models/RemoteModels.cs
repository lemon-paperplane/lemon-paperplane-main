using System;
using System.Collections.Generic;

namespace PaperplaneKit;

// 远程服务器（docs/remote.md 第 4 节）：客户端网关 gateway.*、服务器端 remote.*、fs.browse、runconfig.setRemoteLinks 的输入输出。
// 字段名以 Go json tag 为准（internal/gateway、internal/remote、internal/engine/remote.go）。

/// <summary>连接远程服务器时运行地址的打开方式：端口转发到本机（Forward）和 / 或用服务器地址直接打开（Direct）。</summary>
public sealed record RemoteLinks
{
    public bool Forward { get; set; } = true;
    public bool Direct { get; set; } = true;
}

/// <summary>gateway.decode：连接码里的地址、证书指纹、访问密钥、服务器名。</summary>
public sealed record GatewayCode
{
    public List<string> Urls { get; set; } = new();
    public string Fingerprint { get; set; } = "";
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>远程监听 GET /api/v1/auth/hello 的内容（gateway.probe 带回）。</summary>
public sealed record RemoteHello
{
    public string Name { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Version { get; set; } = "";
    public int ApiVersion { get; set; }
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
    public bool PasswordLogin { get; set; }
}

/// <summary>gateway.probe：服务器地址、证书指纹（未校验，给用户核对）、是否 CA 证书、hello。</summary>
public sealed record GatewayProbe
{
    public string Url { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string FingerprintDisplay { get; set; } = "";
    public bool Trusted { get; set; }
    public RemoteHello Hello { get; set; } = new();
}

/// <summary>gateway.open：回环端口与令牌（用法和本机 runtime.json 一样），info 是服务器的 system.info。</summary>
public sealed record GatewayOpen
{
    public string Id { get; set; } = "";
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public SystemInfo Info { get; set; } = new();
}

/// <summary>gateway.forward：本机端口与地址。</summary>
public sealed record GatewayForward
{
    public int LocalPort { get; set; }
    public string Url { get; set; } = "";
}

/// <summary>remote.status / remote.configure：本机（或当前服务器）作为服务器的状态。</summary>
public sealed record RemoteStatus
{
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    /// <summary>没能开启的原因（端口被占用等）</summary>
    public string Error { get; set; } = "";
    public string Listen { get; set; } = "";
    public int Port { get; set; }
    public List<string> Addresses { get; set; } = new();
    public string Fingerprint { get; set; } = "";
    public string FingerprintDisplay { get; set; } = "";
    public bool CustomCert { get; set; }
    public bool PasswordLogin { get; set; }
    public bool Autostart { get; set; }
    public bool AutostartSupported { get; set; }
    public List<string> AllowCIDRs { get; set; } = new();
    public int Keys { get; set; }
    public int Users { get; set; }
    public string Hostname { get; set; } = "";
}

/// <summary>访问密钥的种类。</summary>
public static class RemoteKeyKind
{
    /// <summary>手动生成</summary>
    public const string Key = "key";
    /// <summary>账号密码登录时发给设备的</summary>
    public const string Device = "device";
}

/// <summary>remote.keys 的一项（不含密钥本身）。</summary>
public sealed record RemoteKey
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string User { get; set; } = "";
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string LastIP { get; set; } = "";
    /// <summary>是不是当前请求用的这条（「本设备」）</summary>
    public bool Current { get; set; }
}

/// <summary>remote.keyCreate：token 与 code 只返回这一次。</summary>
public sealed record RemoteKeyCreated
{
    public RemoteKey Key { get; set; } = new();
    public string Token { get; set; } = "";
    public string Code { get; set; } = "";
}

/// <summary>remote.users 的一项。</summary>
public sealed record RemoteUser
{
    public string Name { get; set; } = "";
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>remote.audit 的一项（新的在前）。event：login.ok / login.fail / auth.fail / ban / key.create / key.revoke / user.set / user.remove / config / op / tunnel。</summary>
public sealed record RemoteAuditEntry
{
    public DateTimeOffset? Time { get; set; }
    public string Event { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Who { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>fs.browse 的一项。</summary>
public sealed record FSBrowseEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Dir { get; set; }
    public bool Hidden { get; set; }
    public long Size { get; set; }
    public DateTimeOffset? ModTime { get; set; }
}

/// <summary>fs.browse：任意文件夹的内容（选择服务器上的文件夹用）。</summary>
public sealed record FSBrowseResult
{
    public string Path { get; set; } = "";
    /// <summary>已在根目录时为空</summary>
    public string Parent { get; set; } = "";
    public string Home { get; set; } = "";
    public string Sep { get; set; } = "";
    /// <summary>Windows 的盘符（C:\ …）；其他系统为 ["/"]</summary>
    public List<string> Roots { get; set; } = new();
    public List<FSBrowseEntry> Entries { get; set; } = new();
}

/// <summary>网关与远程相关的错误码（ApiException.Code）。</summary>
public static class RemoteErrorCode
{
    public const string Unreachable = "unreachable";
    public const string FingerprintMismatch = "fingerprint_mismatch";
    public const string Unauthorized = "unauthorized";
    public const string RateLimited = "rate_limited";
    public const string Incompatible = "incompatible";
    public const string PasswordDisabled = "password_disabled";
    /// <summary>经网关调用时服务器拒绝了访问密钥（网关回 403，不是 401，免得当成本机令牌失效）</summary>
    public const string RemoteUnauthorized = "remote_unauthorized";
    public const string BadRequest = "bad_request";
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PaperplaneKit;

/// <summary>daemon 返回的错误，或网络错误（Code = "network"）。Message 可直接展示（daemon 已本地化）。</summary>
public sealed class ApiException : Exception
{
    public ApiException(string code, string message, int? status = null, JToken? data = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Status = status;
        Data = data;
    }

    public string Code { get; }
    public int? Status { get; }
    /// <summary>插件给出的结构化信息，如 {"reason": "identityMissing", "missing": ["user.email"]}</summary>
    public new JToken? Data { get; }

    /// <summary>Data.reason 的快捷访问</summary>
    public string? Reason => Data.Get("reason") is JValue { Type: JTokenType.String } r ? (string?)r.Value : null;

    public const string CodeNetwork = "network";
    public const string CodeBadResponse = "bad_response";
    public const string CodeNotFound = "not_found";
    public const string CodeCancelled = "cancelled";
    public const string CodeUserError = "user_error";
    public const string CodeNotSupported = "not_supported";
    public const string CodeUnauthorized = "unauthorized";
}

/// <summary>
/// 内核 HTTP API 客户端：POST /api/v1/call/&lt;op&gt;，Bearer token（对应 Mac 的 APIClient）。
/// 线程安全，可在任意线程调用；内部 await 都不回到调用方的同步上下文以外的地方做事（ConfigureAwait(false)），
/// 界面层 await 后照常回到 UI 线程。
/// </summary>
public sealed partial class ApiClient
{
    static ApiClient() => NetConfig.Ensure();

    // 所有客户端共用一个 HttpClient（连接复用）；本机连接不走系统代理（用户机器上常开着代理软件）
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        NetConfig.Ensure();
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        var c = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.ExpectContinue = false;
        return c;
    }

    public ApiClient(Uri baseUrl, string token, TimeSpan? timeout = null)
    {
        BaseUrl = baseUrl;
        Token = token;
        Timeout = timeout ?? TimeSpan.FromSeconds(300);
    }

    public ApiClient(RuntimeInfo runtime) : this(new Uri($"http://127.0.0.1:{runtime.Port}"), runtime.Token) { }

    public Uri BaseUrl { get; }
    public string Token { get; }
    /// <summary>默认超时（300 秒），单次调用可以覆盖。</summary>
    public TimeSpan Timeout { get; }

    // MARK: 请求构造

    public Uri Url(string path) => new Uri(BaseUrl, path);

    /// <summary>带鉴权头的请求（HttpClient 用）。</summary>
    public HttpRequestMessage AuthorizedRequest(string path, HttpMethod? method = null)
    {
        var r = new HttpRequestMessage(method ?? HttpMethod.Get, Url(path));
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return r;
    }

    /// <summary>带鉴权头的 HttpWebRequest（事件流等长连接用：可以随时 Abort）。</summary>
    public HttpWebRequest AuthorizedWebRequest(string path, string method = "GET")
    {
        var r = (HttpWebRequest)WebRequest.Create(Url(path));
        r.Method = method;
        r.Proxy = null;
        r.Headers[HttpRequestHeader.Authorization] = "Bearer " + Token;
        r.AllowReadStreamBuffering = false;
        r.AutomaticDecompression = DecompressionMethods.None;
        r.KeepAlive = true;
        return r;
    }

    // MARK: 调用

    /// <summary>
    /// 调用一个操作并解码结果。input 可以是 JToken（JObject）、模型对象或 null（发 {}）。
    /// 结果为 null 时：集合给空、JToken 给 null 值、EmptyResult 给实例，其他类型报 bad_response。
    /// </summary>
    public async Task<T> CallAsync<T>(string op, object? input = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var body = input == null ? "{}" : Json.Serialize(input);
        using var req = AuthorizedRequest("/api/v1/call/" + op, HttpMethod.Post);
        req.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
        var (data, status) = await PerformAsync(req, timeout ?? Timeout, ct).ConfigureAwait(false);
        try
        {
            return DecodeEnvelope<T>(data, status);
        }
        catch (ApiException e) when (e.Code == "remote_unauthorized")
        {
            // 经网关调用时远程服务器拒绝了访问密钥（被吊销）：通知界面进入「连不上」状态（已建立的事件流不一定会断）
            RemoteRejected?.Invoke(this, e);
            throw;
        }
    }

    /// <summary>经网关的调用被远程服务器拒绝了访问密钥（403 remote_unauthorized）。在调用线程上触发。</summary>
    public static event Action<ApiClient, ApiException>? RemoteRejected;

    /// <summary>调用但不关心输出。</summary>
    public Task SendAsync(string op, object? input = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        CallAsync<JToken>(op, input, timeout, ct);

    /// <summary>解析 {"result": ...} / {"error": {...}} 信封（单元测试也用它）。</summary>
    public static T DecodeEnvelope<T>(byte[] data, int status)
    {
        var text = Encoding.UTF8.GetString(data);
        Envelope<T> env;
        try
        {
            env = Json.Deserialize<Envelope<T>>(text);
        }
        catch (Exception e)
        {
            if (status >= 400)
            {
                var head = Encoding.UTF8.GetString(data, 0, Math.Min(500, data.Length));
                throw new ApiException(status == 401 ? CodeOf401 : $"http_{status}", head, status);
            }
            throw new ApiException(ApiException.CodeBadResponse, e.Message, status, inner: e);
        }
        if (env.Error != null)
            throw new ApiException(env.Error.Code ?? "internal", env.Error.Message ?? "", status, env.Error.Data);
        if (status >= 400)
            throw new ApiException($"http_{status}", $"HTTP {status}", status);
        if (env.HasResult) return env.Result!;
        if (Json.TryNullValue(typeof(T), out var nv)) return (T)nv!;
        throw new ApiException(ApiException.CodeBadResponse, "empty result", status);
    }

    public static T DecodeEnvelope<T>(string text, int status) => DecodeEnvelope<T>(Encoding.UTF8.GetBytes(text), status);

    private const string CodeOf401 = ApiException.CodeUnauthorized;

    private sealed class Envelope<T>
    {
        private T? _result;
        public bool HasResult { get; private set; }
        // NullValueHandling.Ignore：result 为 null 时不调 setter，HasResult 保持 false
        public T? Result { get => _result; set { _result = value; HasResult = true; } }
        public ErrorBody? Error { get; set; }
    }

    private sealed class ErrorBody
    {
        public string? Code { get; set; }
        public string? Message { get; set; }
        public JToken? Data { get; set; }
    }

    private async Task<(byte[] Data, int Status)> PerformAsync(HttpRequestMessage req, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero && timeout.TotalMilliseconds < int.MaxValue) cts.CancelAfter(timeout);
        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            var data = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return (data, (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException e)
        {
            throw new ApiException(ApiException.CodeNetwork, "The request timed out.", inner: e);
        }
        catch (HttpRequestException e)
        {
            throw new ApiException(ApiException.CodeNetwork, Innermost(e).Message, inner: e);
        }
        catch (Exception e) when (e is WebException || e is IOException || e is ObjectDisposedException)
        {
            throw new ApiException(ApiException.CodeNetwork, e.Message, inner: e);
        }
    }

    private static Exception Innermost(Exception e)
    {
        while (e.InnerException != null) e = e.InnerException;
        return e;
    }

    private async Task<(byte[] Data, int Status)> GetAsync(string path, CancellationToken ct)
    {
        using var req = AuthorizedRequest(path);
        return await PerformAsync(req, Timeout, ct).ConfigureAwait(false);
    }

    // MARK: 其他端点

    /// <summary>GET /api/v1/ops：全部操作 [{name, title, description, readOnly, destructive, inputSchema}]</summary>
    public async Task<JToken> OpsAsync(CancellationToken ct = default)
    {
        var (data, status) = await GetAsync("/api/v1/ops", ct).ConfigureAwait(false);
        JToken? raw = null;
        try { raw = Json.Parse(Encoding.UTF8.GetString(data)); }
        catch (Exception) { /* 交给信封解析报错 */ }
        if (raw is JObject o && (o.ContainsKey("result") || o.ContainsKey("error")) || raw == null || status >= 400)
            return DecodeEnvelope<JToken>(data, status);
        return raw;
    }

    /// <summary>GET /api/v1/plugins/&lt;pluginId&gt;/files/&lt;path&gt;（插件图标等）</summary>
    public async Task<byte[]> PluginFileAsync(string pluginId, string path, CancellationToken ct = default)
    {
        var escaped = string.Join("/", path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var (data, status) = await GetAsync($"/api/v1/plugins/{Uri.EscapeDataString(pluginId)}/files/{escaped}", ct).ConfigureAwait(false);
        if (status < 200 || status >= 300)
            throw new ApiException($"http_{status}", $"plugin file {pluginId}/{path}: HTTP {status}", status);
        return data;
    }
}

/// <summary>.NET Framework 的连接设置：同一主机默认只有 2 个连接（事件流、控制台流各占一个），调大；不发 Expect: 100-continue。</summary>
internal static class NetConfig
{
    static NetConfig()
    {
        if (ServicePointManager.DefaultConnectionLimit < 64) ServicePointManager.DefaultConnectionLimit = 64;
        ServicePointManager.Expect100Continue = false;
        ServicePointManager.UseNagleAlgorithm = false;
    }

    public static void Ensure() { }
}

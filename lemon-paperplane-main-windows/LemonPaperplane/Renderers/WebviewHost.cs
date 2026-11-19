using System;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>
/// 插件 webview 视图（逃生舱）：只在用户打开时创建 WebView2，离开即释放。对应 Mac WebviewHost。
/// 地址由 view.render 返回（webviewUrl，带 token 与上下文参数，页面可同源 fetch /api/v1/call/&lt;op&gt;）；
/// 另外注入 window.paperplane.call(op, args) → Promise，经原生 API 客户端转发（只接受内核同源页面的调用），方便插件页面直接调用。
/// </summary>
public sealed class WebviewHost : PPView
{
    private readonly ViewContext _context;
    private readonly PPWebView _web;

    /// <summary>注入的桥：postMessage({__pp, id, op, args}) → 宿主回 {__pp, id, result | error}。</summary>
    private const string BridgeScript = @"(function () {
  if (!window.chrome || !window.chrome.webview || window.paperplane) return;
  var seq = 0, pending = {};
  window.chrome.webview.addEventListener('message', function (e) {
    var m = e.data;
    if (!m || !m.__pp || !pending[m.id]) return;
    var p = pending[m.id];
    delete pending[m.id];
    if (m.error != null) p.reject(new Error(m.error)); else p.resolve(m.result);
  });
  window.paperplane = {
    call: function (op, args) {
      return new Promise(function (resolve, reject) {
        var id = ++seq;
        pending[id] = { resolve: resolve, reject: reject };
        window.chrome.webview.postMessage({ __pp: true, id: id, op: op, args: args || {} });
      });
    }
  };
})();";

    public WebviewHost(Uri url, ViewContext context)
    {
        _context = context;
        _web = new PPWebView("webview", () => url.AbsoluteUri, ColorToken.BgContent)
        {
            RestrictToOrigin = false, // 插件页面自己决定导航（和 Mac 一样）
            ReadyOnNavigation = true,
            ErrorIcon = "puzzle",
            Configure = async core => await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript),
        };
        _web.Message += OnMessage;
    }

    protected override UIElement Build() => _web;

    private async void OnMessage(JObject m)
    {
        if (m["__pp"]?.Type != JTokenType.Boolean || !(bool)m["__pp"]!) return;
        var id = m["id"];
        var op = (string?)m["op"];
        var reply = new JObject { ["__pp"] = true, ["id"] = id };
        if (string.IsNullOrEmpty(op))
        {
            reply["error"] = "bad request";
            _web.PostJson(reply);
            return;
        }
        var args = m["args"] as JObject ?? new JObject();
        if (args["projectId"] == null && _context.ProjectId != null) args["projectId"] = _context.ProjectId;
        if (args["moduleId"] == null && _context.ModuleId != null) args["moduleId"] = _context.ModuleId;
        try
        {
            var result = await AppModel.Shared.Client().CallAsync<JToken>(op!, args);
            reply["result"] = result ?? JValue.CreateNull();
        }
        catch (Exception e)
        {
            reply["error"] = e.Message;
        }
        _web.PostJson(reply);
    }
}

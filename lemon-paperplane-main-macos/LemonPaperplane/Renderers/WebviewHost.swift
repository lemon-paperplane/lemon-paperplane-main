import PaperplaneKit
import SwiftUI
import WebKit

/// 插件 webview 视图（逃生舱）：只在用户打开时创建 WKWebView。
/// 地址由 view.render 返回（webviewUrl，带 token 与上下文参数，页面可同源 fetch /api/v1/call/<op>）；
/// 另外注入 window.paperplane.call(op, args)，经原生 API 客户端转发，方便插件页面直接调用。
struct WebviewHost: NSViewRepresentable {
    let url: URL
    let context: ViewContext

    final class Coordinator: NSObject, WKScriptMessageHandlerWithReply {
        let context: ViewContext
        init(context: ViewContext) { self.context = context }

        func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage,
                                   replyHandler: @escaping (Any?, String?) -> Void) {
            guard let body = message.body as? [String: Any], let op = body["op"] as? String else {
                replyHandler(nil, "bad request")
                return
            }
            var args = body["args"] as? [String: Any] ?? [:]
            if args["projectId"] == nil, let p = context.projectId { args["projectId"] = p }
            if args["moduleId"] == nil, let m = context.moduleId { args["moduleId"] = m }
            Task { @MainActor in
                do {
                    let data = try JSONSerialization.data(withJSONObject: args)
                    let input = try JSONCoding.decoder.decode(JSONValue.self, from: data)
                    let out: JSONValue = try await AppModel.shared.client().call(op, input)
                    let outData = try JSONCoding.encoder.encode(out)
                    replyHandler(try JSONSerialization.jsonObject(with: outData, options: [.fragmentsAllowed]), nil)
                } catch {
                    replyHandler(nil, error.localizedDescription)
                }
            }
        }
    }

    func makeCoordinator() -> Coordinator { Coordinator(context: context) }

    func makeNSView(context ctx: Context) -> WKWebView {
        let cfg = WKWebViewConfiguration()
        let ucc = WKUserContentController()
        ucc.addScriptMessageHandler(ctx.coordinator, contentWorld: .page, name: "paperplane")
        let script = """
        window.paperplane = { call: function(op, args) { return window.webkit.messageHandlers.paperplane.postMessage({op: op, args: args || {}}); } };
        """
        ucc.addUserScript(WKUserScript(source: script, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        cfg.userContentController = ucc
        let wv = WKWebView(frame: .zero, configuration: cfg)
        wv.setValue(false, forKey: "drawsBackground")
        wv.load(URLRequest(url: url))
        return wv
    }

    func updateNSView(_ nsView: WKWebView, context: Context) {}

    static func dismantleNSView(_ nsView: WKWebView, coordinator: Coordinator) {
        nsView.configuration.userContentController.removeAllScriptMessageHandlers()
    }
}

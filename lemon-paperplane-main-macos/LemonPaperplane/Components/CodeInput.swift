import PaperplaneKit
import SwiftUI
import WebKit

/// 代码输入框（CodeMirror，带语法着色、行号、撤销、括号补全）：用于「命令」这类多行代码字段。
/// 网页是编辑器工程的 snippet.html（内核 /editor/ 提供，和编辑窗口共用 CodeMirror 内核）。
/// 内存：每个输入框一个 WKWebView + 独占的 nonPersistent 存储，视图消失即释放，网页进程随之退出。
/// 固定高度，内容多了在输入框内部滚动（不随内容伸缩：伸缩会让所在的对话框一跳一跳地变高）。
struct PPCodeInput: View {
    @Binding var text: String
    var language = "shell"
    var placeholder = ""
    var height: CGFloat = 160
    var invalid = false
    /// ⌘↩
    var onSubmit: (() -> Void)?
    /// 在输入框里按 Esc
    var onEscape: (() -> Void)?

    @State private var focused = false
    @Environment(\.theme) private var theme

    var body: some View {
        CodeInputWebView(text: $text, language: language, placeholder: placeholder, dark: theme.isDark,
                         focused: $focused, onSubmit: onSubmit, onEscape: onEscape)
            .frame(height: height)
            .padding(.horizontal, 2)
            .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgInput))
            .clipShape(RoundedRectangle(cornerRadius: M.radiusMD))
            .overlay(RoundedRectangle(cornerRadius: M.radiusMD)
                .strokeBorder(invalid ? theme.danger : (focused ? theme.borderFocus : theme.borderStrong), lineWidth: 1))
            .overlay(RoundedRectangle(cornerRadius: M.radiusMD + 2).stroke(focused ? theme.focusRing : .clear, lineWidth: 2).padding(-2))
    }
}

private struct CodeInputWebView: NSViewRepresentable {
    @Binding var text: String
    let language: String
    let placeholder: String
    let dark: Bool
    @Binding var focused: Bool
    let onSubmit: (() -> Void)?
    let onEscape: (() -> Void)?

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> WKWebView {
        let cfg = WKWebViewConfiguration()
        cfg.websiteDataStore = .nonPersistent() // 只在内存里，输入框消失即销毁
        let ucc = WKUserContentController()
        ucc.add(CodeInputMessageProxy(context.coordinator), name: "paperplane")
        cfg.userContentController = ucc
        let wv = WKWebView(frame: .zero, configuration: cfg)
        wv.setValue(false, forKey: "drawsBackground") // 透明，底色由外框决定
        wv.allowsMagnification = false
        wv.alphaValue = 0 // 网页就绪前不显示，避免闪白
        context.coordinator.webView = wv
        if let url = Self.url(language: language, placeholder: placeholder, dark: dark) {
            wv.load(URLRequest(url: url))
        }
        return wv
    }

    func updateNSView(_ wv: WKWebView, context: Context) {
        let c = context.coordinator
        c.parent = self
        if c.ready {
            if c.lastSent != text { c.setValue(text) }
            if c.dark != dark {
                c.dark = dark
                wv.evaluateJavaScript("window.paperplaneSnippet.setTheme(\(dark ? "'dark'" : "'light'"))")
            }
        }
    }

    static func dismantleNSView(_ wv: WKWebView, coordinator: Coordinator) {
        wv.configuration.userContentController.removeScriptMessageHandler(forName: "paperplane")
        wv.stopLoading()
        coordinator.webView = nil
    }

    static func url(language: String, placeholder: String, dark: Bool) -> URL? {
        guard let base = AppModel.shared.api?.baseURL else { return nil }
        var frag = URLComponents()
        frag.queryItems = [.init(name: "lang", value: language), .init(name: "theme", value: dark ? "dark" : "light"),
                           .init(name: "placeholder", value: placeholder)]
        return URL(string: base.appendingPathComponent("editor/snippet.html").absoluteString + "#" + (frag.percentEncodedQuery ?? ""))
    }

    @MainActor
    final class Coordinator: NSObject {
        var parent: CodeInputWebView
        weak var webView: WKWebView?
        var ready = false
        var dark: Bool
        /// 最近一次和网页同步过的值（避免回写循环）
        var lastSent: String?

        init(_ p: CodeInputWebView) {
            parent = p
            dark = p.dark
        }

        func setValue(_ v: String) {
            lastSent = v
            guard let data = try? JSONSerialization.data(withJSONObject: [v]), let arr = String(data: data, encoding: .utf8) else { return }
            webView?.evaluateJavaScript("window.paperplaneSnippet.setValue(\(arr)[0])")
        }

        func handle(_ body: Any) {
            guard let m = body as? [String: Any], let type = m["type"] as? String else { return }
            switch type {
            case "ready":
                ready = true
                setValue(parent.text)
                webView?.alphaValue = 1
            case "change":
                let v = m["value"] as? String ?? ""
                lastSent = v
                if parent.text != v { parent.text = v }
            case "focus":
                parent.focused = (m["value"] as? Bool) ?? false
            case "submit":
                parent.onSubmit?()
            case "escape":
                parent.onEscape?()
            default:
                break
            }
        }
    }
}

/// 弱引用转发，避免 WKUserContentController 强持有 Coordinator 造成循环引用
private final class CodeInputMessageProxy: NSObject, WKScriptMessageHandler {
    weak var target: CodeInputWebView.Coordinator?
    init(_ t: CodeInputWebView.Coordinator) { target = t }
    func userContentController(_ ucc: WKUserContentController, didReceive message: WKScriptMessage) {
        let body = message.body
        Task { @MainActor [weak target] in target?.handle(body) }
    }
}

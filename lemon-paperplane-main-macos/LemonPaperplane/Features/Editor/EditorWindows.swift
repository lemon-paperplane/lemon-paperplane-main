import AppKit
import Combine
import Darwin
import PaperplaneKit
import SwiftUI
import WebKit

// 编辑窗口（docs/editor.md）：每个工程一个 NSWindow + 一个 WKWebView（CodeMirror 6 网页，标签页在网页里）。
// 用得很少，所以：平时零占用；打开时才创建；关闭后把 WebView 及其网页进程全部释放，不缓存、不预热。
//
// 释放链（EditorWindowController.teardown，所有关闭路径最终都走 windowWillClose → teardown）：
//   1. 恢复被暂停的菜单快捷键；停掉主题/语言订阅与就绪计时器
//   2. userContentController.removeScriptMessageHandler + removeAllUserScripts
//      （消息处理器本来就是弱引用代理，这一步再断开 WebKit → 代理的强引用）
//   3. webView.stopLoading()，navigationDelegate / uiDelegate 置空
//   4. webView 从视图层级移除，window.contentView 置空
//   5. 控制器放掉 webView 与 WKWebsiteDataStore（每个窗口独占的 nonPersistent 存储）的唯一强引用
//      → WKWebView 释放 → WebKit 关闭页面，这个页面专用的 WebContent 进程退出；
//        nonPersistent 存储释放时其会话（含网页进程缓存）一起销毁，不会被别的窗口复用
//   6. EditorWindows 从表里移除控制器（窗口 isReleasedWhenClosed=false，由控制器持有）→ 控制器、窗口释放
//   7. 稍后调用 malloc_zone_pressure_relief 把本进程已释放的堆内存还给系统；
//      Debug 构建下 2 秒后检查 WKWebView 是否真的被释放，没有则打日志
// 注：WKProcessPool 从 macOS 12 起已废弃且「创建多个不再有任何效果」（部署目标 13.0 用它只会产生弃用警告），
//     所以这里不用它；网页进程隔离与回收靠「每个窗口独立的 nonPersistent 数据存储 + 释放 WKWebView」保证。

@MainActor
final class EditorWindows {
    static let shared = EditorWindows()

    private var controllers: [String: EditorWindowController] = [:]

    var isEmpty: Bool { controllers.isEmpty }
    var hasUnsaved: Bool { controllers.values.contains { $0.isDirty } }

    /// 打开（或聚焦）工程的编辑窗口，可同时打开某个文件
    func open(projectId: String, file: String? = nil, line: Int? = nil) {
        if let c = controllers[projectId] {
            c.focus()
            if let file { c.openFile(file, line: line) }
            return
        }
        let model = AppModel.shared
        guard let api = model.api else {
            model.toasts.show("error", tr("connection.notConnected"))
            return
        }
        let summary = model.data.projects[projectId]
        let c = EditorWindowController(projectId: projectId, projectName: summary?.name ?? projectId,
                                       projectPath: summary?.path ?? "", api: api, file: file, line: line)
        controllers[projectId] = c
        AppDelegate.shared?.updateActivationPolicy()
        c.focus()
    }

    func closed(_ projectId: String) {
        controllers.removeValue(forKey: projectId)
        AppDelegate.shared?.updateActivationPolicy()
        // 把刚释放的堆内存还给系统（网页进程由 WebKit 回收）
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 1) {
            _ = malloc_zone_pressure_relief(nil, 0)
        }
    }

    /// 退出 App 前：没有未保存的就全部关掉并返回 true；有未保存的让这些窗口各自提示，返回 false
    func prepareForQuit() -> Bool {
        let dirty = controllers.values.filter(\.isDirty)
        if dirty.isEmpty {
            for c in controllers.values { c.forceClose() }
            return true
        }
        for c in dirty {
            c.focus()
            c.requestClose()
        }
        return false
    }

    /// 某个工程被移除时关掉它的窗口（网页自己处理未保存）
    func requestClose(projectId: String) {
        controllers[projectId]?.requestClose()
    }
}

/// WKUserContentController 会强引用消息处理器；用弱引用代理避免 WebKit ↔ 控制器循环引用
private final class WeakScriptMessageHandler: NSObject, WKScriptMessageHandler {
    weak var target: WKScriptMessageHandler?

    init(_ target: WKScriptMessageHandler) { self.target = target }

    func userContentController(_ ucc: WKUserContentController, didReceive message: WKScriptMessage) {
        target?.userContentController(ucc, didReceive: message)
    }
}

@MainActor
final class EditorWindowController: NSObject, NSWindowDelegate, WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler {
    let projectId: String
    private let projectName: String
    private let projectPath: String
    private let baseURL: URL

    private var window: NSWindow?
    private var container: NSView?
    private var webView: WKWebView?
    private var dataStore: WKWebsiteDataStore?
    private var errorHost: NSView?

    private(set) var isDirty = false
    private var ready = false
    private var closing = false
    private var pending: [String] = []
    private var revealTimer: Timer?
    private var bag: Set<AnyCancellable> = []
    private var appearanceObservation: NSKeyValueObservation?

    private static let handlerName = "paperplane"

    init(projectId: String, projectName: String, projectPath: String, api: APIClient, file: String?, line: Int?) {
        self.projectId = projectId
        self.projectName = projectName
        self.projectPath = projectPath
        self.baseURL = api.baseURL
        super.init()
        buildWindow()
        load(url: EditorURL.make(base: api.baseURL, token: api.token, projectId: projectId, file: file, line: line,
                                 locale: L10n.shared.locale, theme: Self.isDark ? "dark" : "light"))
        // App 明暗或语言变化时通知网页
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.new]) { [weak self] _, _ in
            Task { @MainActor in self?.appearanceChanged() }
        }
        L10n.shared.$locale.dropFirst().removeDuplicates()
            .sink { [weak self] l in self?.call("setLocale", [l]) }
            .store(in: &bag)
    }

    deinit {
        #if DEBUG
        NSLog("[Editor] window controller released: %@", projectId)
        #endif
    }

    static var isDark: Bool {
        NSApp.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
    }

    // MARK: 窗口

    private func buildWindow() {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 760),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable],
                         backing: .buffered, defer: false)
        w.isReleasedWhenClosed = false
        w.titlebarAppearsTransparent = true
        w.title = projectName + " — " + tr("editorWindow.title")
        w.tabbingMode = .disallowed
        w.minSize = NSSize(width: 560, height: 360)
        w.delegate = self
        w.backgroundColor = DesignSystem.shared.theme(dark: Self.isDark).ns(.bgContent)
        // 窗口尺寸与位置按工程记住
        let name = "PaperplaneEditor-" + projectId
        if !w.setFrameUsingName(name) { w.center() }
        w.setFrameAutosaveName(name)

        let c = NSView()
        c.wantsLayer = true
        w.contentView = c
        container = c

        let cfg = WKWebViewConfiguration()
        let store = WKWebsiteDataStore.nonPersistent() // 每个窗口独占、只在内存里、关闭即销毁
        cfg.websiteDataStore = store
        cfg.suppressesIncrementalRendering = false
        let ucc = WKUserContentController()
        ucc.add(WeakScriptMessageHandler(self), name: Self.handlerName)
        cfg.userContentController = ucc
        let wv = WKWebView(frame: c.bounds, configuration: cfg)
        wv.autoresizingMask = [.width, .height]
        wv.navigationDelegate = self
        wv.uiDelegate = self
        wv.allowsBackForwardNavigationGestures = false
        wv.allowsMagnification = false
        wv.underPageBackgroundColor = w.backgroundColor
        wv.alphaValue = 0 // 网页就绪前不显示，避免白屏闪烁
        #if DEBUG
        if #available(macOS 13.3, *) { wv.isInspectable = true }
        #endif
        c.addSubview(wv)
        webView = wv
        dataStore = store
        window = w
    }

    private func load(url: URL) {
        ready = false
        webView?.load(URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30))
    }

    func focus() {
        guard let w = window else { return }
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }

    /// 已打开时切到某个文件
    func openFile(_ path: String, line: Int?) {
        call("openFile", [path, line.map { $0 as Any } ?? NSNull()])
    }

    /// 让网页处理未保存（确认后网页发 closeWindow）
    func requestClose() {
        guard ready, let wv = webView else { forceClose(); return }
        // 末尾的 undefined：requestClose 若返回 Promise，evaluateJavaScript 会报「结果类型不支持」被误判为失败
        wv.evaluateJavaScript("paperplane.requestClose(); undefined") { [weak self] _, error in
            // 网页没有实现或已出错：直接关闭
            if error != nil { Task { @MainActor in self?.forceClose() } }
        }
    }

    func forceClose() {
        closing = true
        window?.close()
    }

    private func appearanceChanged() {
        let dark = Self.isDark
        window?.backgroundColor = DesignSystem.shared.theme(dark: dark).ns(.bgContent)
        webView?.underPageBackgroundColor = window?.backgroundColor
        call("setTheme", [dark ? "dark" : "light"])
    }

    // MARK: 调用网页

    /// paperplane.<fn>(args…)；网页就绪前排队
    private func call(_ fn: String, _ args: [Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: args, options: [.fragmentsAllowed]),
              var list = String(data: data, encoding: .utf8) else { return }
        list = String(list.dropFirst().dropLast()) // [a, b] → a, b
        let js = "window.paperplane && paperplane.\(fn)(\(list))"
        guard ready, let wv = webView else {
            pending.append(js)
            return
        }
        wv.evaluateJavaScript(js, completionHandler: nil)
    }

    private func becameReady() {
        guard !ready else { return }
        ready = true
        revealTimer?.invalidate()
        revealTimer = nil
        hideError()
        webView?.animator().alphaValue = 1
        let queued = pending
        pending.removeAll()
        for js in queued { webView?.evaluateJavaScript(js, completionHandler: nil) }
    }

    // MARK: 网页 → 宿主

    func userContentController(_ ucc: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.name == Self.handlerName, let body = message.body as? [String: Any],
              let type = body["type"] as? String else { return }
        switch type {
        case "ready":
            becameReady()
        case "title":
            if let t = body["title"] as? String, !t.isEmpty { window?.title = t }
        case "dirty":
            isDirty = body["dirty"] as? Bool ?? false
            window?.isDocumentEdited = isDirty
        case "closeWindow":
            forceClose()
        case "closeCancelled":
            // 用户在网页的未保存提示里点了取消：窗口保持打开，宿主没有挂起的关闭状态需要恢复
            break
        case "reveal":
            if let p = body["path"] as? String { FileActions.revealInFinder(p, projectPath: projectPath) }
        case "openExternal":
            if let s = body["url"] as? String, let u = URL(string: s), ["http", "https", "mailto"].contains(u.scheme?.lowercased() ?? "") {
                NSWorkspace.shared.open(u)
            }
        default:
            break
        }
    }

    // MARK: NSWindowDelegate

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        // 红绿灯关闭：先让网页处理未保存；网页没就绪（加载失败、崩溃）时直接关
        if closing || !ready { return true }
        requestClose()
        return false
    }

    func windowWillClose(_ notification: Notification) {
        teardown()
    }

    func windowDidBecomeKey(_ notification: Notification) {
        // 编辑窗口在前时，原生菜单不截获编辑器的快捷键（⌘S、⌘F、⌘W…交给网页）
        MenuShortcutGuard.shared.suspend()
    }

    func windowDidResignKey(_ notification: Notification) {
        MenuShortcutGuard.shared.restore()
    }

    // MARK: 释放（见文件开头的释放链）

    private func teardown() {
        guard window != nil else { return }
        MenuShortcutGuard.shared.restore()
        appearanceObservation?.invalidate()
        appearanceObservation = nil
        bag.removeAll()
        revealTimer?.invalidate()
        revealTimer = nil
        pending.removeAll()

        #if DEBUG
        let leakCheck = WeakRef(webView)
        #endif
        if let wv = webView {
            let ucc = wv.configuration.userContentController
            ucc.removeScriptMessageHandler(forName: Self.handlerName)
            ucc.removeAllUserScripts()
            wv.stopLoading()
            wv.navigationDelegate = nil
            wv.uiDelegate = nil
            wv.removeFromSuperview()
        }
        webView = nil
        dataStore = nil
        errorHost?.removeFromSuperview()
        errorHost = nil
        container = nil
        window?.contentView = nil
        window?.delegate = nil
        window = nil
        EditorWindows.shared.closed(projectId)

        #if DEBUG
        let pid = projectId
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
            if leakCheck.value != nil { NSLog("[Editor] WKWebView still alive 2s after closing %@ (leak)", pid) }
        }
        #endif
    }

    // MARK: WKNavigationDelegate

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping @MainActor (WKNavigationActionPolicy) -> Void) {
        // 只允许本机编辑器页面；其他链接交给系统浏览器
        if let u = action.request.url, u.host == baseURL.host, u.port == baseURL.port {
            decisionHandler(.allow)
            return
        }
        if let u = action.request.url, ["http", "https", "mailto"].contains(u.scheme?.lowercased() ?? "") {
            NSWorkspace.shared.open(u)
        }
        decisionHandler(action.request.url?.scheme == "about" ? .allow : .cancel)
    }

    func webView(_ webView: WKWebView, decidePolicyFor response: WKNavigationResponse,
                 decisionHandler: @escaping @MainActor (WKNavigationResponsePolicy) -> Void) {
        if response.isForMainFrame, let http = response.response as? HTTPURLResponse, http.statusCode >= 400 {
            decisionHandler(.cancel)
            showError(tr("editorWindow.loadFailed") + " (HTTP \(http.statusCode))")
            return
        }
        decisionHandler(.allow)
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        // 网页没有发 ready（旧版本或出错）时，几秒后也显示出来
        revealTimer?.invalidate()
        revealTimer = Timer.scheduledTimer(withTimeInterval: 4, repeats: false) { [weak self] _ in
            Task { @MainActor in
                guard let self, !self.ready else { return }
                self.webView?.animator().alphaValue = 1
            }
        }
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        if (error as NSError).code == NSURLErrorCancelled { return }
        showError(tr("editorWindow.loadFailed") + "\n" + error.localizedDescription)
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        if (error as NSError).code == NSURLErrorCancelled { return }
        showError(tr("editorWindow.loadFailed") + "\n" + error.localizedDescription)
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        ready = false
        isDirty = false
        window?.isDocumentEdited = false
        showError(tr("editorWindow.crashed"))
    }

    // MARK: WKUIDelegate（window.open 交给系统浏览器；alert/confirm 兜底用表单）

    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let u = navigationAction.request.url, ["http", "https", "mailto"].contains(u.scheme?.lowercased() ?? "") {
            NSWorkspace.shared.open(u)
        }
        return nil
    }

    func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping @MainActor () -> Void) {
        guard let w = window else { completionHandler(); return }
        let a = NSAlert()
        a.messageText = message
        a.addButton(withTitle: tr("common.ok"))
        a.beginSheetModal(for: w) { _ in completionHandler() }
    }

    func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping @MainActor (Bool) -> Void) {
        guard let w = window else { completionHandler(false); return }
        let a = NSAlert()
        a.messageText = message
        a.addButton(withTitle: tr("common.ok"))
        a.addButton(withTitle: tr("common.cancel"))
        a.beginSheetModal(for: w) { r in completionHandler(r == .alertFirstButtonReturn) }
    }

    // MARK: 错误层

    private func showError(_ message: String) {
        guard let c = container else { return }
        hideError()
        webView?.alphaValue = 0
        let host = NSHostingView(rootView: Themed {
            EditorErrorView(message: message) { [weak self] in self?.reload() }
        })
        host.frame = c.bounds
        host.autoresizingMask = [.width, .height]
        c.addSubview(host)
        errorHost = host
    }

    private func hideError() {
        errorHost?.removeFromSuperview()
        errorHost = nil
    }

    private func reload() {
        hideError()
        guard let api = AppModel.shared.api else { return }
        load(url: EditorURL.make(base: api.baseURL, token: api.token, projectId: projectId, file: nil, line: nil,
                                 locale: L10n.shared.locale, theme: Self.isDark ? "dark" : "light"))
    }
}

private struct EditorErrorView: View {
    let message: String
    let retry: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        PPEmptyState(icon: "file-pen", title: message, actionTitle: tr("editorWindow.reload"), actionIcon: "refresh-cw", action: retry)
            .background(theme.bgContent)
    }
}

/// 编辑窗口在前时暂停原生菜单的快捷键，让按键交给网页里的编辑器（快捷键方案由插件决定）。
/// 保留：退出、隐藏、最小化、全屏、剪切/复制/粘贴/全选/撤销/重做（这些经响应链交给 WKWebView）。
@MainActor
final class MenuShortcutGuard {
    static let shared = MenuShortcutGuard()

    private var saved: [(item: NSMenuItem, key: String, mask: NSEvent.ModifierFlags)] = []

    private let keep: Set<String> = [
        "terminate:", "hide:", "hideOtherApplications:", "unhideAllApplications:", "performMiniaturize:",
        "toggleFullScreen:", "cut:", "copy:", "paste:", "pasteAsPlainText:", "selectAll:", "undo:", "redo:", "delete:",
    ]

    func suspend() {
        guard saved.isEmpty, let menu = NSApp.mainMenu else { return }
        walk(menu)
    }

    func restore() {
        for s in saved {
            s.item.keyEquivalent = s.key
            s.item.keyEquivalentModifierMask = s.mask
        }
        saved.removeAll()
    }

    private func walk(_ menu: NSMenu) {
        for item in menu.items {
            if let sub = item.submenu { walk(sub) }
            guard !item.keyEquivalent.isEmpty else { continue }
            if let a = item.action, keep.contains(NSStringFromSelector(a)) { continue }
            saved.append((item, item.keyEquivalent, item.keyEquivalentModifierMask))
            item.keyEquivalent = ""
        }
    }
}

/// 弱引用盒子（Debug 下检查 WebView 是否真的释放）
private final class WeakRef<T: AnyObject> {
    weak var value: T?
    init(_ v: T?) { value = v }
}

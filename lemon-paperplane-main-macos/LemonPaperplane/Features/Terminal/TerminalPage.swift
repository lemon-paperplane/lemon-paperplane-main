import AppKit
import PaperplaneKit
import SwiftUI
import WebKit

// 工作区「终端」：像 VS Code 一样开若干个终端，右侧是终端列表，左侧显示当前终端。
// 终端在内核里（伪终端中的登录 shell，terminal.* 接口），切走或关掉界面不影响，回来按偏移补齐输出。
// 终端区域是编辑器工程的 terminal.html（xterm.js），一个网页装下这个工作区的全部终端；离开这个分段即释放网页。

// MARK: - 操作

extension Actions {
    /// 在工程根目录新建终端，并切换过去
    static func newTerminal(workspaceId: String, project: ProjectSummary, size: (cols: Int, rows: Int)) {
        model.perform(tr("terminal.createFailed")) { api in
            let t = try await api.terminalCreate(projectId: project.id, cols: size.cols, rows: size.rows)
            model.terminals.upsert(t)
            model.terminals.selected[workspaceId] = t.id
        }
    }

    static func renameTerminal(_ t: TerminalInfo) {
        let field = FormField(key: "name", title: tr("terminal.name"), type: "string", placeholder: t.name, required: true)
        let id = t.id
        Dialogs.form(title: tr("terminal.rename"), fields: [field], values: ["name": .string(t.name)], ok: tr("common.save")) { v in
            try await AppModel.shared.client().terminalRename(id: id, name: v["name"]?.stringValue ?? "")
        }
    }

    static func restartTerminal(_ t: TerminalInfo) {
        model.perform(tr("terminal.restartFailed")) { api in try await api.terminalRestart(id: t.id) }
    }

    /// 关闭终端；里面还有命令在跑（开发服务器等）时先确认
    static func closeTerminal(_ t: TerminalInfo) {
        model.perform { api in
            let b = try? await api.terminalBusy(id: t.id)
            if let b, b.busy {
                let name = b.process.isEmpty ? tr("terminal.aProcess") : b.process
                Dialogs.confirm(title: tr("terminal.close.title", t.name), message: tr("terminal.close.busy", name),
                                ok: tr("terminal.close"), danger: true) {
                    model.perform { api in try await api.terminalClose(id: t.id) }
                }
                return
            }
            try await api.terminalClose(id: t.id)
        }
    }
}

// MARK: - 页面

struct TerminalPage: View {
    let workspaceId: String
    @ObservedObject private var store = AppModel.shared.terminals
    @ObservedObject private var data = AppModel.shared.data
    @StateObject private var web = TerminalWebController()
    @Environment(\.theme) private var theme

    var body: some View {
        let list = store.terminals(in: workspaceId)
        let current = store.current(in: workspaceId)
        VStack(spacing: 0) {
            toolbar(current)
            PPDivider(subtle: true)
            HStack(spacing: 0) {
                // 网页一直在（没有终端时被空状态盖住），新建第一个终端时也能量出行列数
                ZStack {
                    TerminalWebView(controller: web, dark: theme.isDark)
                    if list.isEmpty { emptyState }
                }
                if !list.isEmpty {
                    PPDivider(vertical: true, subtle: true)
                    TerminalList(workspaceId: workspaceId, list: list, currentId: current?.id) { t in
                        store.selected[workspaceId] = t.id
                        web.show(t.id)
                    }
                    .frame(width: 220)
                }
            }
        }
        .background(theme.consoleBg)
        .onAppear {
            let ws = workspaceId
            web.onReady = { [weak web] in
                let s = AppModel.shared.terminals
                web?.sync(ids: s.terminals(in: ws).map(\.id), show: s.current(in: ws)?.id)
            }
        }
        .onChange(of: list.map(\.id)) { ids in web.sync(ids: ids, show: store.current(in: workspaceId)?.id) }
        .onChange(of: current?.id) { id in if let id { web.show(id) } }
    }

    private func toolbar(_ t: TerminalInfo?) -> some View {
        HStack(spacing: M.sm) {
            if let t {
                PPIcon("square-terminal", size: M.iconSmall, color: theme.iconSecondary)
                Text(t.name).font(.pp(.bodyStrong)).lineLimit(1)
                Text(Format.abbreviatePath(t.cwd)).font(.pp(.small)).foregroundColor(theme.textTertiary)
                    .lineLimit(1).truncationMode(.middle)
                if t.exited {
                    PPBadge(text: tr("terminal.exited", t.exitCode), tone: "danger")
                    PPButton(tr("terminal.restart"), icon: "rotate-ccw", kind: .ghost, size: .small) { Actions.restartTerminal(t) }
                }
            } else {
                Text(tr("terminal.none")).font(.pp(.small)).foregroundColor(theme.textSecondary)
            }
            PPInfoButton(text: tr("terminal.info"))
            Spacer(minLength: M.md)
            if let t {
                PPIconButton("pencil", help: tr("terminal.rename"), size: M.iconButtonSmall, iconSize: M.iconSmall) { Actions.renameTerminal(t) }
                PPIconButton("trash-2", help: tr("terminal.close"), size: M.iconButtonSmall, iconSize: M.iconSmall) { Actions.closeTerminal(t) }
            }
            PPButtonMenu(title: tr("terminal.new"), icon: "plus", kind: .primary, size: .small, alignment: .trailing) { projectMenu }
        }
        .padding(.horizontal, M.xl)
        .frame(height: M.toolbarHeight)
        .background(theme.bgContent)
    }

    private var emptyState: some View {
        VStack(spacing: M.lg) {
            PPIcon("square-terminal", size: M.iconEmpty, color: theme.iconSecondary)
            Text(tr("terminal.empty")).font(.pp(.body)).foregroundColor(theme.textSecondary)
            PPButtonMenu(title: tr("terminal.new"), icon: "plus", kind: .primary) { projectMenu }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(theme.bgContent)
    }

    /// 「新建终端」菜单：选工作区里的一个工程，终端从它的根目录开始
    private var projectMenu: [MenuEntry] {
        let projects = data.projects(in: workspaceId)
        guard !projects.isEmpty else {
            return [.item(tr("terminal.noProjects"), disabled: true) {}]
        }
        let ws = workspaceId
        var out: [MenuEntry] = [.header(tr("terminal.pickProject"))]
        for p in projects {
            out.append(.item(MenuItem(p.name, iconRef: IconSource.parse(p.facets.first?.icon ?? "") ?? .builtin("folder")) { [web] in
                web.size { size in Actions.newTerminal(workspaceId: ws, project: p, size: size) }
            }))
        }
        return out
    }
}

// MARK: - 右侧终端列表

private struct TerminalList: View {
    let workspaceId: String
    let list: [TerminalInfo]
    let currentId: String?
    let select: (TerminalInfo) -> Void
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        ScrollView {
            VStack(spacing: 1) {
                ForEach(list) { t in
                    TerminalRow(terminal: t, project: data.projects[t.projectId], selected: t.id == currentId) { select(t) }
                }
            }
            .padding(M.sm)
        }
        .background(theme.bgContent)
    }
}

private struct TerminalRow: View {
    let terminal: TerminalInfo
    let project: ProjectSummary?
    let selected: Bool
    let select: () -> Void
    @State private var hover = false
    @Environment(\.theme) private var theme

    var body: some View {
        let t = terminal
        HStack(spacing: M.sm) {
            PPIcon("square-terminal", size: M.icon, color: t.exited ? theme.danger : (selected ? theme.iconPrimary : theme.iconSecondary))
            VStack(alignment: .leading, spacing: 1) {
                Text(t.name).font(.pp(.body)).foregroundColor(theme.textPrimary).lineLimit(1).truncationMode(.middle)
                Text(subtitle).font(.pp(.caption)).foregroundColor(t.exited ? theme.danger : theme.textTertiary)
                    .lineLimit(1).truncationMode(.head)
            }
            Spacer(minLength: 0)
            if hover {
                PPIconButton("x", help: tr("terminal.close"), size: M.iconButtonSmall, iconSize: M.iconSmall) { Actions.closeTerminal(t) }
            }
        }
        .padding(.horizontal, M.sm)
        .frame(height: 36)
        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(selected ? theme.bgSelected : (hover ? theme.bgHover : .clear)))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture(perform: select)
        .help(t.cwd)
        .ppContextMenu { menu }
    }

    /// 名字就是工程名时显示目录，否则显示所在工程
    private var subtitle: String {
        if terminal.exited { return tr("terminal.exited", terminal.exitCode) }
        if let p = project, p.name != terminal.name { return p.name }
        return Format.abbreviatePath(terminal.cwd)
    }

    private var menu: [MenuEntry] {
        let t = terminal
        var out: [MenuEntry] = [.item(tr("terminal.rename"), icon: "pencil") { Actions.renameTerminal(t) }]
        if t.exited { out.append(.item(tr("terminal.restart"), icon: "rotate-ccw") { Actions.restartTerminal(t) }) }
        out.append(.item(tr("terminal.reveal"), icon: "folder-open") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: t.cwd)]) })
        out.append(.divider)
        out.append(.item(tr("terminal.close"), icon: "trash-2", destructive: true) { Actions.closeTerminal(t) })
        return out
    }
}

// MARK: - 网页

/// 控制终端网页（window.paperplaneTerm）。网页没就绪时记下要显示的终端，就绪后补上。
@MainActor
final class TerminalWebController: ObservableObject {
    weak var webView: WKWebView?
    var ready = false
    var onReady: (() -> Void)?
    private var shown: String?

    func didBecomeReady() {
        ready = true
        onReady?()
    }

    func show(_ id: String) {
        shown = id
        guard ready, let wv = webView else { return }
        wv.window?.makeFirstResponder(wv)
        wv.evaluateJavaScript("window.paperplaneTerm.show(\(Self.js(id)))")
    }

    func sync(ids: [String], show id: String?) {
        guard ready, let wv = webView else { return }
        wv.evaluateJavaScript("window.paperplaneTerm.sync(\(Self.js(ids)))")
        if let id { show(id) }
    }

    func setTheme(dark: Bool) {
        guard ready else { return }
        webView?.evaluateJavaScript("window.paperplaneTerm.setTheme(\(dark ? "'dark'" : "'light'"))")
    }

    /// 当前区域能放下的行列数（新建终端按它启动 shell）
    func size(_ done: @escaping ((cols: Int, rows: Int)) -> Void) {
        guard ready, let wv = webView else { done((100, 30)); return }
        wv.evaluateJavaScript("window.paperplaneTerm.size()") { v, _ in
            let d = v as? [String: Any]
            let cols = (d?["cols"] as? NSNumber)?.intValue ?? 100
            let rows = (d?["rows"] as? NSNumber)?.intValue ?? 30
            done((max(20, cols), max(5, rows)))
        }
    }

    static func js(_ v: Any) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: [v]), let s = String(data: data, encoding: .utf8) else { return "null" }
        return "(\(s))[0]"
    }
}

private struct TerminalWebView: NSViewRepresentable {
    let controller: TerminalWebController
    let dark: Bool

    func makeCoordinator() -> Coordinator { Coordinator(controller: controller, dark: dark) }

    func makeNSView(context: Context) -> WKWebView {
        let cfg = WKWebViewConfiguration()
        cfg.websiteDataStore = .nonPersistent() // 只在内存里，离开「终端」分段即销毁
        let ucc = WKUserContentController()
        ucc.add(TerminalMessageProxy(context.coordinator), name: "paperplane")
        cfg.userContentController = ucc
        let wv = WKWebView(frame: .zero, configuration: cfg)
        wv.setValue(false, forKey: "drawsBackground")
        wv.allowsMagnification = false
        controller.webView = wv
        if let url = Self.url(dark: dark) { wv.load(URLRequest(url: url)) }
        return wv
    }

    func updateNSView(_ wv: WKWebView, context: Context) {
        let c = context.coordinator
        if c.dark != dark {
            c.dark = dark
            controller.setTheme(dark: dark)
        }
    }

    static func dismantleNSView(_ wv: WKWebView, coordinator: Coordinator) {
        wv.configuration.userContentController.removeScriptMessageHandler(forName: "paperplane")
        wv.stopLoading()
        coordinator.controller.webView = nil
        coordinator.controller.ready = false
    }

    static func url(dark: Bool) -> URL? {
        guard let api = AppModel.shared.api else { return nil }
        var frag = URLComponents()
        frag.queryItems = [.init(name: "token", value: api.token), .init(name: "theme", value: dark ? "dark" : "light")]
        return URL(string: api.baseURL.appendingPathComponent("editor/terminal.html").absoluteString + "#" + (frag.percentEncodedQuery ?? ""))
    }

    @MainActor
    final class Coordinator: NSObject {
        let controller: TerminalWebController
        var dark: Bool

        init(controller: TerminalWebController, dark: Bool) {
            self.controller = controller
            self.dark = dark
        }

        func handle(_ body: Any) {
            guard let m = body as? [String: Any], let type = m["type"] as? String else { return }
            switch type {
            case "ready":
                controller.didBecomeReady()
            case "closed":
                if let id = m["id"] as? String { AppModel.shared.terminals.list.removeAll { $0.id == id } }
            case "openExternal":
                if let s = m["url"] as? String, let url = URL(string: s), ["http", "https"].contains(url.scheme?.lowercased() ?? "") {
                    NSWorkspace.shared.open(url)
                }
            default:
                break
            }
        }
    }
}

/// 弱引用转发，避免 WKUserContentController 强持有 Coordinator
private final class TerminalMessageProxy: NSObject, WKScriptMessageHandler {
    weak var target: TerminalWebView.Coordinator?
    init(_ t: TerminalWebView.Coordinator) { target = t }
    func userContentController(_ ucc: WKUserContentController, didReceive message: WKScriptMessage) {
        let body = message.body
        Task { @MainActor [weak target] in target?.handle(body) }
    }
}

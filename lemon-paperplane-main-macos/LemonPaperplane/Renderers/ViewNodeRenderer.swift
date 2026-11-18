import AppKit
import PaperplaneKit
import SwiftUI

// 插件界面描述语言（sdk.ViewNode）渲染器。全部节点类型见 lemon-paperplane-main-plugin-sdk/view.go。
// 事件走 view.event；处理 Refresh / State / Message / OpenURL / Reimport；RefreshMs 定时刷新（不可见时暂停）。

@MainActor
final class PluginViewModel: ObservableObject {
    @Published var root: ViewNode?
    @Published var error: String?
    @Published var loading = false
    @Published var busyAction: String?

    /// 表格勾选：表格 id → 行 id
    @Published var selections: [String: Set<String>] = [:]
    /// 表单值：表单 id（或 action）→ 值
    @Published var formValues: [String: [String: JSONValue]] = [:]
    /// 标签页节点选中的序号、树节点展开状态、区块折叠状态
    @Published var tabIndex: [String: Int] = [:]
    @Published var expanded: [String: Bool] = [:]

    private(set) var context: ViewContext?
    private var state: [String: JSONValue]?
    private var timer: Timer?
    private var refreshMs = 0
    private var visible = false

    func start(_ ctx: ViewContext) {
        let changed = ctx != context
        context = ctx
        visible = true
        if changed || root == nil {
            if changed { state = ctx.initialState; formValues = [:]; selections = [:] }
            Task { await render() }
        } else {
            scheduleTimer()
        }
    }

    func stop() {
        visible = false
        timer?.invalidate()
        timer = nil
    }

    func render() async {
        guard let ctx = context, let api = AppModel.shared.api else { return }
        loading = root == nil
        do {
            let r = try await api.renderView(ctx, state: state)
            guard ctx == context else { return }
            root = r.root
            if let s = r.state { state = s }
            refreshMs = r.refreshMs > 0 ? max(1000, r.refreshMs) : 0
            error = nil
        } catch {
            if root == nil { self.error = error.localizedDescription }
        }
        loading = false
        scheduleTimer()
    }

    private func scheduleTimer() {
        timer?.invalidate()
        timer = nil
        guard visible, refreshMs > 0 else { return }
        timer = Timer.scheduledTimer(withTimeInterval: Double(refreshMs) / 1000, repeats: false) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.visible else { return }
                await self.render()
            }
        }
    }

    /// 触发事件；有 confirm 时先确认
    func fire(_ action: String?, values: [String: JSONValue] = [:], confirm: String? = nil, title: String? = nil) {
        guard let action, !action.isEmpty else { return }
        if let confirm, !confirm.isEmpty {
            Dialogs.confirm(title: title ?? confirm, message: title == nil ? nil : confirm, ok: title ?? tr("common.ok")) { [weak self] in
                try await self?.send(action, values: values)
            }
            return
        }
        Task {
            do { try await send(action, values: values) } catch { AppModel.shared.toasts.error(error) }
        }
    }

    private func send(_ action: String, values: [String: JSONValue]) async throws {
        guard let ctx = context else { return }
        var v = values
        // 带上勾选状态
        let sel = selections.filter { !$0.value.isEmpty }
        if !sel.isEmpty {
            v["selection"] = .object(sel.mapValues { JSONValue(Array($0).sorted()) })
            if v["selected"] == nil { v["selected"] = JSONValue(sel.values.flatMap { $0 }.sorted()) }
        }
        busyAction = action
        defer { busyAction = nil }
        let r = try await AppModel.shared.client().viewEvent(ctx, event: action, values: v, state: state)
        if let s = r.state { state = s }
        if let m = r.message, !m.isEmpty { AppModel.shared.toasts.show("info", m) }
        if let u = r.openUrl, !u.isEmpty { Actions.openURL(u) }
        if r.reimport, let pid = ctx.projectId { Actions.reimport(pid) }
        if r.refresh { await render() }
    }
}

/// 插件视图宿主（标签页、区块、运行侧栏、全局页、设置页通用）
struct PluginViewHost: View {
    let view: ViewInfo
    let context: ViewContext
    var scrolls = true
    @StateObject private var model = PluginViewModel()
    @State private var webviewURL: URL?
    @State private var webviewLoading = false
    @Environment(\.theme) private var theme

    var body: some View {
        Group {
            if view.isWebview {
                webview
            } else if let root = model.root {
                if scrolls {
                    ScrollView {
                        NodeView(node: root, model: model, path: "r")
                            .padding(M.xl)
                            .frame(maxWidth: .infinity, alignment: .topLeading)
                    }
                } else {
                    NodeView(node: root, model: model, path: "r")
                }
            } else if let e = model.error {
                PPAlert(tone: "danger", title: tr("plugin.viewFailed"), message: e) {
                    PPButton(tr("connection.retry"), size: .small) { Task { await model.render() } }
                }
                .padding(M.xl)
                .frame(maxWidth: .infinity, maxHeight: scrolls ? .infinity : nil, alignment: .top)
            } else {
                PPSpinner().padding(M.xl).frame(maxWidth: .infinity, maxHeight: scrolls ? .infinity : nil)
            }
        }
        .onAppear { if !view.isWebview { model.start(context) } }
        .onDisappear { model.stop() }
        .onChange(of: context) { c in if !view.isWebview { model.start(c) } }
    }

    @ViewBuilder private var webview: some View {
        if let url = webviewURL {
            WebviewHost(url: url, context: context)
        } else {
            // 只有用户点了「打开」才创建 WebView
            PPEmptyState(icon: "app-window", title: view.title, actionTitle: tr("plugin.openWebview"), actionIcon: "external-link") {
                guard !webviewLoading else { return }
                webviewLoading = true
                Task {
                    do {
                        webviewURL = try await AppModel.shared.client().webviewURL(context)
                        if webviewURL == nil { AppModel.shared.toasts.show("error", tr("plugin.viewFailed")) }
                    } catch {
                        AppModel.shared.toasts.error(error, title: tr("plugin.viewFailed"))
                    }
                    webviewLoading = false
                }
            }
        }
    }
}

/// 递归渲染一个节点
struct NodeView: View {
    let node: ViewNode
    @ObservedObject var model: PluginViewModel
    let path: String
    @Environment(\.theme) private var theme

    private var key: String { node.id ?? path }

    var body: some View { content }

    private func children(_ spacing: CGFloat = M.md) -> some View {
        ForEach(Array(node.children.enumerated()), id: \.offset) { i, c in
            NodeView(node: c, model: model, path: "\(path).\(i)")
        }
    }

    private func icon(_ raw: String?, size: CGFloat = M.icon, color: Color? = nil) -> PPIcon {
        PPIcon(ref: IconSource.parse(raw, pluginId: model.context?.plugin), size: size, color: color)
    }

    @ViewBuilder private var content: some View {
        switch node.type {
        case "column":
            VStack(alignment: .leading, spacing: M.md) { children() }
                .frame(maxWidth: .infinity, alignment: .leading)
        case "row":
            HStack(alignment: .center, spacing: M.md) { children() }
        case "section":
            PPSection(node.title ?? "", info: node.info, collapsible: true, collapsed: node.collapsed) {
                VStack(alignment: .leading, spacing: M.md) { children() }
            }
            .padding(.top, M.xs)
        case "divider":
            PPDivider(subtle: true)
        case "spacer":
            Spacer(minLength: M.md)
        case "tabs":
            tabs
        case "heading":
            HStack(spacing: M.sm) {
                Text(node.text ?? node.title ?? "").font(.pp(.heading))
                if let info = node.info { PPInfoButton(text: info) }
            }
        case "text":
            HStack(alignment: .firstTextBaseline, spacing: M.sm) {
                Text(node.text ?? "")
                    .font(node.mono ? .pp(.mono) : .pp(.body))
                    .foregroundColor(node.tone == nil ? theme.textPrimary : theme.tone(node.tone).fg)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                if let info = node.info { PPInfoButton(text: info) }
            }
        case "code":
            ScrollView(.horizontal) {
                Text(node.text ?? "").font(.pp(.mono)).textSelection(.enabled).padding(M.md)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgCode))
            .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(theme.borderSubtle, lineWidth: 1))
        case "markdown":
            MarkdownText(text: node.text ?? "")
        case "kv":
            PPKeyValueList(rows: node.items.map { .init(key: $0.key, value: $0.value, mono: $0.mono, tone: $0.tone, href: $0.href) })
        case "badge":
            PPBadge(text: node.text ?? "", tone: node.tone, icon: IconSource.parse(node.icon, pluginId: model.context?.plugin))
        case "alert":
            PPAlert(tone: node.tone ?? "info", title: node.title ?? node.text ?? "", message: node.title == nil ? nil : node.text) {
                HStack(spacing: M.sm) { children() }
            }
        case "stat":
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: M.xs) {
                    Text(node.title ?? "").font(.pp(.small)).foregroundColor(theme.textSecondary)
                    if let info = node.info { PPInfoButton(text: info) }
                }
                Text(node.text ?? "").font(.pp(.stat)).foregroundColor(node.tone == nil ? theme.textPrimary : theme.tone(node.tone).fg)
            }
            .padding(M.md)
            .frame(minWidth: 120, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgSubtle))
        case "progress":
            VStack(alignment: .leading, spacing: M.xs) {
                if node.title != nil || node.text != nil {
                    HStack {
                        Text(node.title ?? "")
                        Spacer()
                        Text(node.text ?? (node.value >= 0 ? "\(Int(node.value))%" : "")).foregroundColor(theme.textSecondary)
                    }
                    .font(.pp(.small))
                }
                PPProgressBar(value: node.value, tone: node.tone)
            }
        case "empty":
            VStack(spacing: M.md) {
                icon(node.icon ?? "builtin:inbox", size: M.iconEmpty, color: theme.iconSecondary)
                if let t = node.title { Text(t).foregroundColor(theme.textSecondary) }
                if let t = node.text { Text(t).font(.pp(.small)).foregroundColor(theme.textTertiary) }
            }
            .frame(maxWidth: .infinity)
            .padding(M.xxl)
        case "icon":
            icon(node.icon, color: node.tone == nil ? nil : theme.tone(node.tone).fg)
        case "table":
            ViewTable(node: node, model: model, key: key)
        case "list":
            ViewList(node: node, model: model)
        case "tree":
            VStack(alignment: .leading, spacing: 0) {
                ForEach(node.rows) { r in TreeRowView(row: r, depth: 0, model: model, plugin: model.context?.plugin) }
            }
        case "button":
            nodeButton(node)
        case "link":
            PPLink(title: node.text ?? node.href ?? "", icon: node.icon == nil ? nil : "external-link") {
                if let h = node.href, !h.isEmpty { Actions.openURL(h) } else { model.fire(node.action, values: dataValues(node)) }
            }
        case "form":
            ViewForm(node: node, model: model, key: key)
        case "toolbar":
            HStack(spacing: M.sm) { children(M.sm) }
        case "console":
            ConsolePanel(consoleId: node.console, runId: nil, logFile: nil, compact: true)
                .frame(height: 320)
                .clipShape(RoundedRectangle(cornerRadius: M.radiusMD))
                .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(theme.border, lineWidth: 1))
        default:
            EmptyView()
        }
    }

    private var tabs: some View {
        let idx = model.tabIndex[key] ?? 0
        return VStack(alignment: .leading, spacing: M.md) {
            PPTabBar(tabs: node.children.enumerated().map { i, c in
                PPTabBar<Int>.Tab(value: i, title: c.title ?? "\(i + 1)", icon: IconSource.parse(c.icon, pluginId: model.context?.plugin))
            }, selection: Binding(get: { idx }, set: { model.tabIndex[key] = $0 }))
            PPDivider(subtle: true)
            if node.children.indices.contains(idx) {
                let c = node.children[idx]
                VStack(alignment: .leading, spacing: M.md) {
                    ForEach(Array(c.children.enumerated()), id: \.offset) { i, n in
                        NodeView(node: n, model: model, path: "\(path).\(idx).\(i)")
                    }
                }
            }
        }
    }

    private func nodeButton(_ n: ViewNode) -> some View {
        let kind: PPButtonKind
        switch n.style {
        case "primary": kind = .primary
        case "danger": kind = .danger
        case "link": kind = .link
        default: kind = .secondary
        }
        return PPButton(n.text ?? n.title ?? "", iconRef: IconSource.parse(n.icon, pluginId: model.context?.plugin), kind: kind,
                        size: .small, busy: model.busyAction == n.action && n.action != nil) {
            model.fire(n.action, values: dataValues(n), confirm: n.confirm, title: n.text)
        }
        .disabled(n.disabled)
        .help(n.info ?? "")
    }
}

func dataValues(_ n: ViewNode) -> [String: JSONValue] {
    (n.data ?? [:]).mapValues { .string($0) }
}

/// 只支持粗体、链接、列表、行内代码
struct MarkdownText: View {
    let text: String
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: M.xs) {
            ForEach(Array(text.components(separatedBy: "\n").enumerated()), id: \.offset) { _, line in
                let trimmed = line.trimmingCharacters(in: .whitespaces)
                if trimmed.hasPrefix("- ") || trimmed.hasPrefix("* ") {
                    HStack(alignment: .firstTextBaseline, spacing: M.sm) {
                        Text("•").foregroundColor(theme.textSecondary)
                        inline(String(trimmed.dropFirst(2)))
                    }
                } else if !trimmed.isEmpty {
                    inline(line)
                } else {
                    Spacer().frame(height: M.xs)
                }
            }
        }
    }

    private func inline(_ s: String) -> some View {
        let attr = (try? AttributedString(markdown: s, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(s)
        return Text(attr).tint(theme.textLink).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
    }
}

/// table 节点
private struct ViewTable: View {
    let node: ViewNode
    @ObservedObject var model: PluginViewModel
    let key: String
    @Environment(\.theme) private var theme

    var body: some View {
        let sel = model.selections[key] ?? []
        let hasActions = node.rows.contains { !$0.actions.isEmpty }
        VStack(spacing: 0) {
            HStack(spacing: M.md) {
                if node.selectable {
                    PPCheckbox(isOn: Binding(get: { !node.rows.isEmpty && sel.count == node.rows.count }, set: { on in
                        model.selections[key] = on ? Set(node.rows.map(\.id)) : []
                    }))
                }
                ForEach(node.columns, id: \.key) { c in
                    cell(Text(c.title).font(.pp(.label)).foregroundColor(theme.textSecondary), c)
                }
                if hasActions { Spacer().frame(width: 80) }
            }
            .padding(.horizontal, M.md)
            .frame(height: M.rowHeight)
            .background(theme.bgSubtle)
            PPDivider(subtle: true)
            LazyVStack(spacing: 0) {
                ForEach(node.rows) { r in
                    TableRowView(row: r, node: node, model: model, key: key, hasActions: hasActions)
                    PPDivider(subtle: true)
                }
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: M.radiusMD))
        .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(theme.border, lineWidth: 1))
    }

    @ViewBuilder private func cell<V: View>(_ v: V, _ c: ViewTableColumn) -> some View {
        let align: Alignment = c.align == "right" ? .trailing : (c.align == "center" ? .center : .leading)
        if c.width > 0 {
            v.frame(width: CGFloat(c.width), alignment: align)
        } else {
            v.frame(maxWidth: .infinity, alignment: align)
        }
    }
}

private struct TableRowView: View {
    let row: ViewTableRow
    let node: ViewNode
    @ObservedObject var model: PluginViewModel
    let key: String
    let hasActions: Bool
    @Environment(\.theme) private var theme
    @State private var hover = false

    var body: some View {
        let selected = model.selections[key]?.contains(row.id) ?? false
        HStack(spacing: M.md) {
            if node.selectable {
                PPCheckbox(isOn: Binding(get: { selected }, set: { on in
                    var s = model.selections[key] ?? []
                    if on { s.insert(row.id) } else { s.remove(row.id) }
                    model.selections[key] = s
                }))
            }
            ForEach(Array(node.columns.enumerated()), id: \.offset) { i, c in
                let text = row.cells[c.key] ?? ""
                let content = HStack(spacing: M.xs) {
                    if i == 0, let icon = row.icon {
                        PPIcon(ref: IconSource.parse(icon, pluginId: model.context?.plugin), size: M.iconSmall)
                    }
                    Text(text)
                        .font(c.mono ? .pp(.mono) : .pp(.body))
                        .foregroundColor(row.tone == nil ? theme.textPrimary : theme.tone(row.tone).fg)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                let align: Alignment = c.align == "right" ? .trailing : (c.align == "center" ? .center : .leading)
                if c.width > 0 {
                    content.frame(width: CGFloat(c.width), alignment: align)
                } else {
                    content.frame(maxWidth: .infinity, alignment: align)
                }
            }
            if hasActions {
                HStack(spacing: M.xs) {
                    ForEach(Array(row.actions.enumerated()), id: \.offset) { _, a in
                        PPButton(a.text ?? "", kind: a.style == "primary" ? .primary : (a.style == "danger" ? .danger : (a.style == "link" ? .link : .ghost)),
                                 size: .small) {
                            var v = dataValues(a)
                            v["id"] = .string(row.id)
                            model.fire(a.action, values: v, confirm: a.confirm, title: a.text)
                        }
                        .disabled(a.disabled)
                    }
                }
                .frame(minWidth: 80, alignment: .trailing)
            }
        }
        .padding(.horizontal, M.md)
        .frame(minHeight: M.rowHeight)
        .background(selected ? theme.bgSelected : (hover && row.action != nil ? theme.bgHover : Color.clear))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture {
            guard let a = row.action else { return }
            var v: [String: JSONValue] = ["id": .string(row.id)]
            for (k, val) in row.cells { v[k] = .string(val) }
            model.fire(a, values: v)
        }
    }
}

/// list 节点：Cells["title"] 标题、Cells["subtitle"] 副标题
private struct ViewList: View {
    let node: ViewNode
    @ObservedObject var model: PluginViewModel
    @Environment(\.theme) private var theme

    var body: some View {
        LazyVStack(alignment: .leading, spacing: 0) {
            ForEach(node.rows) { r in
                PPRow(height: nil, action: r.action == nil ? nil : {
                    var v: [String: JSONValue] = ["id": .string(r.id)]
                    for (k, val) in r.cells { v[k] = .string(val) }
                    model.fire(r.action, values: v)
                }) {
                    HStack(spacing: M.md) {
                        if let icon = r.icon { PPIcon(ref: IconSource.parse(icon, pluginId: model.context?.plugin), size: M.icon) }
                        VStack(alignment: .leading, spacing: 2) {
                            Text(r.cells["title"] ?? "").foregroundColor(r.tone == nil ? theme.textPrimary : theme.tone(r.tone).fg)
                            if let s = r.cells["subtitle"], !s.isEmpty {
                                Text(s).font(.pp(.small)).foregroundColor(theme.textSecondary)
                            }
                        }
                        Spacer(minLength: M.sm)
                        ForEach(Array(r.actions.enumerated()), id: \.offset) { _, a in
                            PPButton(a.text ?? "", kind: a.style == "primary" ? .primary : .ghost, size: .small) {
                                var v = dataValues(a)
                                v["id"] = .string(r.id)
                                model.fire(a.action, values: v, confirm: a.confirm, title: a.text)
                            }
                        }
                    }
                    .padding(.vertical, M.sm)
                }
            }
        }
    }
}

private struct TreeRowView: View {
    let row: ViewTableRow
    let depth: Int
    @ObservedObject var model: PluginViewModel
    let plugin: String?
    @Environment(\.theme) private var theme

    var body: some View {
        let open = model.expanded[row.id] ?? row.expanded
        VStack(alignment: .leading, spacing: 0) {
            PPRow(action: {
                if !row.children.isEmpty { model.expanded[row.id] = !open }
                if let a = row.action {
                    var v: [String: JSONValue] = ["id": .string(row.id)]
                    for (k, val) in row.cells { v[k] = .string(val) }
                    model.fire(a, values: v)
                }
            }) {
                HStack(spacing: M.sm) {
                    Spacer().frame(width: CGFloat(depth) * 16)
                    if row.children.isEmpty {
                        Spacer().frame(width: M.iconSmall)
                    } else {
                        PPIcon(open ? "chevron-down" : "chevron-right", size: M.iconSmall, color: theme.iconSecondary)
                    }
                    if let icon = row.icon { PPIcon(ref: IconSource.parse(icon, pluginId: plugin), size: M.icon) }
                    Text(row.cells["title"] ?? row.cells.values.first ?? row.id)
                        .foregroundColor(row.tone == nil ? theme.textPrimary : theme.tone(row.tone).fg)
                    if let s = row.cells["subtitle"] { Text(s).font(.pp(.small)).foregroundColor(theme.textTertiary) }
                    Spacer(minLength: 0)
                }
            }
            if open {
                ForEach(row.children) { c in TreeRowView(row: c, depth: depth + 1, model: model, plugin: plugin) }
            }
        }
    }
}

/// form 节点
private struct ViewForm: View {
    let node: ViewNode
    @ObservedObject var model: PluginViewModel
    let key: String
    @State private var showErrors = false

    var body: some View {
        let values = Binding<[String: JSONValue]>(
            get: { model.formValues[key] ?? FormView.initialValues(node.fields, node.values ?? [:]) },
            set: { model.formValues[key] = $0 })
        VStack(alignment: .leading, spacing: M.md) {
            if let t = node.title { Text(t).font(.pp(.title)) }
            FormView(fields: node.fields, values: values, showErrors: showErrors, labelWidth: 120)
            HStack {
                Spacer().frame(width: 120 + M.md)
                PPButton(node.submitText ?? tr("common.submit"), kind: .primary, size: .small,
                         busy: model.busyAction == node.action && node.action != nil) {
                    if !FormView.missingRequired(node.fields, values.wrappedValue).isEmpty { showErrors = true; return }
                    model.fire(node.action, values: FormView.normalized(node.fields, values.wrappedValue), confirm: node.confirm)
                }
                .disabled(node.disabled)
            }
        }
    }
}

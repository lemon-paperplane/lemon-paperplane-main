import AppKit
import PaperplaneKit
import SwiftUI
import UniformTypeIdentifiers

/// 插件：已安装（列表 + 详情 + 设置）与商店。
struct PluginsPage: View {
    enum Tab: Hashable { case installed, store }

    @ObservedObject private var data = AppModel.shared.data
    @State private var tab: Tab = .installed
    @State private var selected: String?
    @State private var filter = ""
    @State private var listWidth: CGFloat = 300
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: M.lg) {
                PPTabBar(tabs: [.init(value: Tab.installed, title: tr("plugin.installed"), icon: .builtin("puzzle"), badge: data.plugins.count),
                                .init(value: Tab.store, title: tr("plugin.store"), icon: .builtin("store"))],
                         selection: $tab)
                Spacer()
                PPButton(tr("plugin.installFromFile"), icon: "upload", kind: .ghost, size: .small) { installFromFile() }
            }
            .padding(.horizontal, M.lg)
            PPDivider(subtle: true)
            switch tab {
            case .installed: installed
            case .store: StoreView()
            }
        }
    }

    private var installed: some View {
        let list = data.plugins.filter { filter.isEmpty || $0.name.localizedCaseInsensitiveContains(filter) || $0.id.localizedCaseInsensitiveContains(filter) }
        let current = data.plugins.first { $0.id == selected } ?? list.first
        return PPSplitView(leftWidth: $listWidth, minLeft: 240, maxLeft: 440) {
            VStack(spacing: 0) {
                PPTextField(tr("common.filter"), text: $filter, icon: "search", clearable: true, height: M.controlHeightSmall + 2)
                    .padding(M.sm)
                ScrollView {
                    LazyVStack(spacing: 1) {
                        ForEach(list) { p in
                            PPRow(selected: p.id == current?.id, height: 48, action: { selected = p.id }) {
                                HStack(spacing: M.md) {
                                    PPIcon(raw: p.icon, plugin: p.id, fallback: "puzzle", size: M.iconLarge)
                                        .opacity(p.enabled ? 1 : 0.45)
                                    VStack(alignment: .leading, spacing: 2) {
                                        HStack(spacing: M.xs) {
                                            Text(p.name).font(.pp(.bodyStrong)).lineLimit(1)
                                            if p.running { Circle().fill(theme.statusRunning).frame(width: 6, height: 6).help(tr("plugin.running")) }
                                        }
                                        Text(p.version + (p.vendor.map { "  ·  " + $0 } ?? ""))
                                            .font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1)
                                    }
                                    Spacer(minLength: M.xs)
                                    if p.error != nil { PPIcon("circle-alert", size: M.iconSmall, color: theme.danger) }
                                    if !p.enabled { PPBadge(text: tr("plugin.disabled")) }
                                }
                            }
                        }
                    }
                    .padding(.horizontal, M.sm)
                }
            }
            .background(theme.bgContent)
        } right: {
            if let current {
                PluginDetail(plugin: current).id(current.id)
            } else {
                PPEmptyState(icon: "puzzle", title: tr("plugin.none"), actionTitle: tr("plugin.installFromFile"), actionIcon: "upload") {
                    installFromFile()
                }
            }
        }
    }

    private func installFromFile() {
        let p = NSOpenPanel()
        p.canChooseFiles = true
        p.canChooseDirectories = true
        p.allowsMultipleSelection = false
        p.allowedContentTypes = [.zip, .folder]
        p.message = tr("plugin.installFromFile")
        guard p.runModal() == .OK, let url = p.url else { return }
        AppModel.shared.perform(tr("plugin.installFailed")) { api in
            let info = try await api.installPlugin(path: url.path)
            await AppModel.shared.reloadPlugins()
            selected = info.id
            tab = .installed
            AppModel.shared.toasts.show("success", tr("plugin.installedToast", info.name, info.version))
        }
    }
}

private struct PluginDetail: View {
    let plugin: PluginInfo
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        let p = plugin
        ScrollView {
            VStack(alignment: .leading, spacing: M.xl) {
                HStack(alignment: .top, spacing: M.lg) {
                    PPIcon(raw: p.icon, plugin: p.id, fallback: "puzzle", size: 40)
                    VStack(alignment: .leading, spacing: M.xs) {
                        HStack(spacing: M.sm) {
                            Text(p.name).font(.pp(.display))
                            PPBadge(text: p.version, mono: true)
                            PPBadge(text: sourceTitle(p.source), tone: p.source == "bundled" ? nil : "info")
                            if p.running { PPBadge(text: tr("plugin.running"), tone: "success") }
                        }
                        HStack(spacing: M.sm) {
                            Text(p.id).font(.pp(.mono))
                            if let v = p.vendor { Text("·"); Text(v) }
                            if let c = p.category { Text("·"); Text(c) }
                        }
                        .font(.pp(.small))
                        .foregroundColor(theme.textSecondary)
                        if let d = p.description, !d.isEmpty {
                            Text(d).foregroundColor(theme.textSecondary).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    Spacer()
                    VStack(alignment: .trailing, spacing: M.sm) {
                        HStack(spacing: M.sm) {
                            Text(tr("plugin.enabled")).foregroundColor(theme.textSecondary)
                            PPSwitch(isOn: Binding(get: { p.enabled }, set: { on in
                                AppModel.shared.perform { api in
                                    try await api.setPluginEnabled(p.id, on)
                                    await AppModel.shared.reloadPlugins()
                                }
                            }))
                        }
                        if p.source != "bundled" {
                            PPButton(p.bundledVersion != nil ? tr("plugin.revert", p.bundledVersion!) : tr("plugin.uninstall"),
                                     icon: "trash-2", kind: .ghost, size: .small) {
                                Dialogs.confirm(title: tr("plugin.uninstall.title", p.name), ok: tr("plugin.uninstall"), danger: true) {
                                    try await AppModel.shared.client().uninstallPlugin(p.id)
                                    await AppModel.shared.reloadPlugins()
                                }
                            }
                        }
                        if let h = p.homepage, !h.isEmpty {
                            PPLink(title: tr("plugin.homepage"), icon: "external-link") { Actions.openURL(h) }
                        }
                    }
                }
                if let e = p.error, !e.isEmpty {
                    PPAlert(tone: "danger", title: tr("plugin.error"), message: e)
                }
                if let c = p.contributes {
                    PPSection(tr("plugin.contributes")) {
                        PPCard {
                            VStack(alignment: .leading, spacing: M.sm) {
                                contribRow(tr("plugin.c.facets"), c.facets)
                                contribRow(tr("plugin.c.toolchains"), c.toolchains)
                                contribRow(tr("plugin.c.runConfigTypes"), c.runConfigTypes)
                                contribRow(tr("plugin.c.views"), c.views)
                                contribRow(tr("plugin.c.actions"), c.actions)
                                contribRow(tr("plugin.c.tools"), c.tools)
                                if let v = c.vcs, !v.isEmpty { contribRow(tr("plugin.c.vcs"), [v]) }
                                if c.diagnostics > 0 {
                                    HStack(spacing: M.md) {
                                        Text(tr("plugin.c.diagnostics")).foregroundColor(theme.textSecondary).frame(width: 140, alignment: .leading)
                                        Text("\(c.diagnostics)").font(.pp(.mono))
                                    }
                                }
                            }
                        }
                    }
                }
                if !p.dependencies.isEmpty {
                    PPSection(tr("plugin.dependencies")) {
                        FlowRow(spacing: M.xs) {
                            ForEach(p.dependencies, id: \.id) { d in
                                PPBadge(text: d.id + (d.version.map { " " + $0 } ?? "") + (d.optional ? " · " + tr("plugin.optional") : ""),
                                        tone: data.plugin(d.id) == nil ? (d.optional ? nil : "danger") : nil)
                            }
                        }
                    }
                }
                ForEach(data.contributions.settings.filter { $0.plugin == p.id && !$0.fields.isEmpty }, id: \.plugin) { s in
                    PPSection(tr("plugin.settings")) { PluginSettingsCard(info: s) }
                }
                ForEach(data.contributions.views(at: ViewLocation.settings).filter { $0.plugin == p.id }) { v in
                    PPSection(v.title) {
                        PluginViewHost(view: v, context: ViewContext(plugin: v.plugin, view: v.id), scrolls: false)
                    }
                }
            }
            .padding(M.xl)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    @ViewBuilder private func contribRow(_ title: String, _ items: [String]) -> some View {
        if !items.isEmpty {
            HStack(alignment: .top, spacing: M.md) {
                Text(title).foregroundColor(theme.textSecondary).frame(width: 140, alignment: .leading)
                FlowRow(spacing: M.xs) { ForEach(items, id: \.self) { PPBadge(text: $0) } }
            }
        }
    }

    private func sourceTitle(_ s: String) -> String {
        switch s {
        case "bundled": return tr("plugin.source.bundled")
        case "dev": return tr("plugin.source.dev")
        default: return tr("plugin.source.user")
        }
    }
}

/// 商店（store.list）
private struct StoreView: View {
    @State private var entries: [StoreEntry]?
    @State private var error: String?
    @State private var loading = false
    @ObservedObject private var runs = AppModel.shared.runs
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Spacer()
                PPButton(tr("plugin.storeRefresh"), icon: "refresh-cw", kind: .ghost, size: .small, busy: loading) {
                    Task { await load(refresh: true) }
                }
            }
            .padding(.horizontal, M.lg)
            .padding(.vertical, M.sm)
            if let entries {
                if entries.isEmpty {
                    PPEmptyState(icon: "store", title: tr("plugin.storeEmpty"))
                } else {
                    ScrollView {
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 300, maximum: 460), spacing: M.lg)], spacing: M.lg) {
                            ForEach(entries) { e in StoreCard(entry: e) { Task { await load(refresh: false) } } }
                        }
                        .padding(M.xl)
                    }
                }
            } else if let error {
                PPEmptyState(icon: "circle-alert", title: error, actionTitle: tr("connection.retry")) { Task { await load(refresh: true) } }
            } else {
                PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .task { await load(refresh: false) }
    }

    private func load(refresh: Bool) async {
        loading = true
        defer { loading = false }
        do {
            entries = try await AppModel.shared.client().store(refresh: refresh)
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }
}

private struct StoreCard: View {
    let entry: StoreEntry
    var reload: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        let e = entry
        let canUpdate = e.installed != nil && e.installed != e.version
        PPCard {
            VStack(alignment: .leading, spacing: M.sm) {
                HStack(spacing: M.sm) {
                    PPIcon("puzzle", size: M.iconLarge, color: theme.iconSecondary)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(e.name).font(.pp(.title))
                        Text(e.version + (e.vendor.map { "  ·  " + $0 } ?? "")).font(.pp(.small)).foregroundColor(theme.textTertiary)
                    }
                    Spacer()
                    if !e.compatible {
                        PPBadge(text: tr("plugin.incompatible"), tone: "warning")
                    } else if let inst = e.installed, !canUpdate {
                        PPBadge(text: tr("plugin.installedVersion", inst), tone: "success")
                    } else {
                        PPButton(canUpdate ? tr("plugin.update") : tr("plugin.install"), icon: "download", kind: .primary, size: .small) {
                            AppModel.shared.perform(tr("plugin.installFailed")) { api in
                                AppModel.shared.track(try await api.installFromStore(e.id))
                            }
                        }
                    }
                }
                if let d = e.description, !d.isEmpty {
                    Text(d).font(.pp(.small)).foregroundColor(theme.textSecondary).lineLimit(3).fixedSize(horizontal: false, vertical: true)
                }
                if let eng = e.engines?.paperplane, !eng.isEmpty {
                    Text(tr("plugin.engines", eng)).font(.pp(.caption)).foregroundColor(theme.textTertiary)
                }
            }
        }
    }
}

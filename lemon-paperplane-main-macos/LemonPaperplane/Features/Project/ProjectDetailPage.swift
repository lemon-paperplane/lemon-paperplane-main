import Combine
import PaperplaneKit
import SwiftUI

/// 工程详情的数据：project.get，收到 project.changed 时合并刷新。
@MainActor
final class ProjectDetailModel: ObservableObject {
    let projectId: String
    @Published var project: Project?
    @Published var error: String?
    private var bag: Set<AnyCancellable> = []

    init(projectId: String) {
        self.projectId = projectId
        AppModel.shared.bus.projectChanged
            .filter { $0 == projectId }
            .debounce(for: .milliseconds(300), scheduler: RunLoop.main)
            .sink { [weak self] _ in Task { await self?.reload() } }
            .store(in: &bag)
    }

    func reload() async {
        do {
            project = try await AppModel.shared.client().project(projectId)
            error = nil
        } catch {
            if project == nil { self.error = error.localizedDescription }
        }
    }

    /// 修改工具链覆盖：nil 表示改回自动
    func setToolchainOverride(kind: String, _ o: ToolchainOverride?) {
        AppModel.shared.perform { api in
            let p = try await api.updateProjectSettings(self.projectId, toolchains: [kind: o])
            self.project = p
        }
    }
}

struct ProjectDetailPage: View {
    let projectId: String
    @StateObject private var model: ProjectDetailModel
    @ObservedObject private var nav = AppModel.shared.nav
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    init(projectId: String) {
        self.projectId = projectId
        _model = StateObject(wrappedValue: ProjectDetailModel(projectId: projectId))
    }

    var body: some View {
        VStack(spacing: 0) {
            if let summary = data.projects[projectId] {
                ProjectHeader(summary: summary, project: model.project)
                tabBar(summary)
                PPDivider(subtle: true)
                tabContent(summary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                PPEmptyState(icon: "folder-open", title: tr("project.missing"), actionTitle: tr("common.back")) {
                    nav.openProjectId = nil
                }
            }
        }
        .task { await model.reload() }
    }

    private var pluginTabs: [ViewInfo] {
        guard let p = model.project else { return [] }
        let facets = p.facetIds
        let plugins = p.pluginIds
        return data.contributions.views(at: ViewLocation.projectTab).filter { v in
            v.facets.isEmpty ? plugins.contains(v.plugin) : !facets.isDisjoint(with: v.facets)
        }
    }

    private func tabBar(_ s: ProjectSummary) -> some View {
        var tabs: [PPTabBar<NavStore.ProjectTab>.Tab] = [
            .init(value: .overview, title: tr("project.tab.overview"), icon: .builtin("layout-grid"), badge: s.problems > 0 ? s.problems : nil),
            .init(value: .run, title: tr("project.tab.run"), icon: .builtin("play"),
                  badge: s.runs.filter { RunState.isActive($0.state) }.count),
            .init(value: .files, title: tr("files.tab"), icon: .builtin("folder-tree")),
            .init(value: .vcs, title: tr("project.tab.vcs"), icon: .builtin("git-branch"), badge: s.vcs?.changes),
            .init(value: .config, title: tr("project.tab.config"), icon: .builtin("sliders-horizontal")),
        ]
        for v in pluginTabs {
            tabs.append(.init(value: .plugin(v.key), title: v.title, icon: IconSource.parse(v.icon, pluginId: v.plugin)))
        }
        return HStack {
            PPTabBar(tabs: tabs, selection: $nav.projectTab)
            Spacer()
        }
        .padding(.horizontal, M.lg)
    }

    @ViewBuilder private func tabContent(_ s: ProjectSummary) -> some View {
        if let p = model.project {
            switch nav.projectTab {
            case .overview:
                OverviewTab(project: p, model: model)
            case .run:
                RunTab(project: p, summary: s)
            case .files:
                FilesTab(project: p)
            case .vcs:
                VCSTabView(projectId: p.id)
            case .config:
                ConfigTab(project: p, model: model)
            case .plugin(let key):
                if let v = pluginTabs.first(where: { $0.key == key }) {
                    PluginTabContent(view: v, project: p)
                } else {
                    PPEmptyState(icon: "puzzle", title: tr("plugin.viewMissing"))
                }
            }
        } else if let e = model.error {
            PPEmptyState(icon: "circle-alert", title: e, actionTitle: tr("connection.retry")) { Task { await model.reload() } }
        } else {
            PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }
}

/// 工程标签页插件视图：多个模块符合时可切换模块
private struct PluginTabContent: View {
    let view: ViewInfo
    let project: Project
    @State private var moduleId = ""

    var body: some View {
        let modules = project.modules.filter { m in
            view.facets.isEmpty ? (m.facets.contains { $0.plugin == view.plugin } || m.analysis[view.plugin] != nil) : m.hasAnyFacet(view.facets)
        }
        let mid = modules.contains { $0.id == moduleId } ? moduleId : (modules.first?.id ?? "")
        VStack(spacing: 0) {
            if modules.count > 1 {
                HStack {
                    PPDropdown(options: modules.map { .init(value: $0.id, title: $0.name, detail: $0.relDir.isEmpty ? nil : $0.relDir) },
                               selection: Binding(get: { mid }, set: { moduleId = $0 }), size: .small, minWidth: 200)
                    Spacer()
                }
                .padding(.horizontal, M.xl)
                .padding(.top, M.md)
            }
            PluginViewHost(view: view, context: ViewContext(plugin: view.plugin, view: view.id, projectId: project.id,
                                                            moduleId: mid.isEmpty ? nil : mid))
                .id(mid)
        }
    }
}

/// 详情页头部：返回、名称、路径、徽标、运行控件、插件工具栏动作、更多菜单
struct ProjectHeader: View {
    let summary: ProjectSummary
    let project: Project?
    @ObservedObject private var nav = AppModel.shared.nav
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    private var configId: String? {
        if let c = nav.selectedConfig[summary.id], summary.configs.contains(where: { $0.id == c }) { return c }
        return summary.defaultConfigId ?? summary.configs.first?.id
    }

    private static let dotSize: CGFloat = 10

    var body: some View {
        VStack(alignment: .leading, spacing: M.sm) {
            HStack(alignment: .center, spacing: M.md) {
                PPIconButton("arrow-left", help: tr("common.back")) { nav.openProjectId = nil }
                StatusDot(state: summary.status, size: Self.dotSize)
                VStack(alignment: .leading, spacing: 3) {
                    Text(summary.name).font(.pp(.display)).lineLimit(1)
                    if Actions.isRemote {
                        Text(Format.abbreviatePath(summary.path)).font(.pp(.monoSmall)).foregroundColor(theme.textSecondary)
                            .lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                    } else {
                        PPLink(title: Format.abbreviatePath(summary.path), mono: true) { Actions.revealInFinder(summary.path) }
                            .help(tr("open.finder"))
                    }
                }
                Spacer(minLength: M.lg)
                runControls
                toolbarActions
                PPMenuButton(alignment: .trailing, entries: { moreMenu }) {
                    PPIcon("ellipsis", size: M.icon, color: theme.iconPrimary)
                        .frame(width: M.iconButton, height: M.iconButton)
                        .contentShape(Rectangle())
                }
                .help(tr("common.more"))
            }
            // 技术标签单独占一行，跟工程名左对齐，放不下时换行（和运行控件挤在一行时会被压成一两个字母）
            if !summary.facets.isEmpty {
                FlowRow(spacing: M.xs) { ForEach(summary.facets) { FacetBadgeView(facet: $0) } }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.leading, M.iconButton + M.md + Self.dotSize + M.md)
            }
        }
        .padding(.horizontal, M.lg)
        .padding(.vertical, M.md)
    }

    @ViewBuilder private var runControls: some View {
        if !summary.configs.isEmpty {
            let cid = configId
            let active = summary.runs.first { $0.configId == cid && RunState.isActive($0.state) }
            HStack(spacing: M.xs) {
                if let active, let url = active.urls.first {
                    RunURLLinks(url: url, projectId: summary.id, configId: active.configId, style: .button)
                }
                PPDropdown(options: summary.configs.map { .init(value: $0.id, title: $0.name, icon: IconSource.parse($0.icon)) },
                           selection: Binding(get: { cid ?? "" }, set: { nav.selectedConfig[summary.id] = $0 }),
                           minWidth: 140, maxWidth: 240)
                if let active {
                    PPIconButton("rotate-cw", help: tr("action.restart")) { Actions.restart(runId: active.id) }
                    PPIconButton("square", help: tr("action.stop") + "  ⌘.", tint: theme.danger) { Actions.stop(runId: active.id) }
                } else {
                    PPIconButton("play", help: tr("action.run") + "  ⌘R", tint: theme.success) {
                        Actions.run(projectId: summary.id, configId: cid)
                    }
                }
            }
        }
    }

    @ViewBuilder private var toolbarActions: some View {
        let acts = Actions.applicable(data.contributions.actions(at: ActionLocation.projectToolbar),
                                      facets: project?.facetIds ?? Set(summary.facets.map(\.id)),
                                      plugins: project?.pluginIds ?? Set(summary.facets.map(\.plugin)))
        if !acts.isEmpty {
            PPDivider(vertical: true).frame(height: 20)
            ForEach(acts, id: \.key) { a in
                PPIconButton(ref: IconSource.parse(a.icon, pluginId: a.plugin) ?? .builtin("zap"), help: a.title) {
                    Actions.invoke(a, projectId: summary.id)
                }
            }
        }
    }

    private var moreMenu: [MenuEntry] {
        let acts = Actions.applicable(data.contributions.actions(at: ActionLocation.projectMenu),
                                      facets: project?.facetIds ?? Set(summary.facets.map(\.id)),
                                      plugins: project?.pluginIds ?? Set(summary.facets.map(\.plugin)))
        var e: [MenuEntry] = [
            .item(tr("project.reimport"), icon: "refresh-cw") { Actions.reimport(summary.id) },
            .item(tr("project.sync"), icon: "refresh-cw") { Actions.sync(summary.id) },
            .item(tr("editorWindow.open"), icon: "file-pen") { EditorWindows.shared.open(projectId: summary.id) },
        ]
        if !Actions.isRemote {
            e.append(.submenu(tr("project.openWith"), icon: "external-link", Actions.openWithEntries(summary.id)))
        }
        if summary.hasActiveRun {
            e.append(.item(tr("card.stopAll"), icon: "square") { Actions.stopProject(summary.id) })
        }
        if !acts.isEmpty {
            e.append(.divider)
            e += Actions.menuEntries(acts, projectId: summary.id)
        }
        e.append(.divider)
        e.append(.item(tr("project.rename"), icon: "pencil") { Actions.rename(summary) })
        e.append(.item(tr("project.remove"), icon: "trash-2", destructive: true) { Actions.remove(summary) })
        return e
    }
}

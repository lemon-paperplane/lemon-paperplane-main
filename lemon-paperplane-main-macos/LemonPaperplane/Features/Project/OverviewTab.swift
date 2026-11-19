import PaperplaneKit
import SwiftUI

/// 概览：问题（可一键修复）、模块（facet、关键信息、工具链、依赖）、插件 project.section 区块。
struct OverviewTab: View {
    let project: Project
    @ObservedObject var model: ProjectDetailModel
    @ObservedObject private var data = AppModel.shared.data
    @State private var problems: [Diagnosis] = []
    @Environment(\.theme) private var theme

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: M.xxl) {
                if !problems.isEmpty {
                    PPSection(tr("problems.title")) {
                        VStack(alignment: .leading, spacing: M.sm) {
                            ForEach(problems, id: \.stableKey) { d in
                                DiagnosisRow(diagnosis: d, runId: nil, projectId: project.id) {
                                    Task { await loadProblems() }
                                }
                            }
                        }
                    }
                }
                PPSection(tr("project.modules"), accessory: {
                    PPButton(tr("project.reimport"), icon: "refresh-cw", kind: .ghost, size: .small) { Actions.reimport(project.id) }
                }) {
                    VStack(alignment: .leading, spacing: M.lg) {
                        ForEach(project.modules) { m in
                            ModuleCard(project: project, module: m, model: model)
                        }
                    }
                }
                ForEach(sections) { sec in
                    PPSection(sec.view.title + (project.modules.count > 1 ? "  ·  \(sec.module.name)" : "")) {
                        PluginViewHost(view: sec.view, context: ViewContext(plugin: sec.view.plugin, view: sec.view.id, projectId: project.id,
                                                                            moduleId: sec.module.id), scrolls: false)
                            .padding(M.md)
                            .background(RoundedRectangle(cornerRadius: M.radiusLG).fill(theme.bgSurface))
                            .overlay(RoundedRectangle(cornerRadius: M.radiusLG).strokeBorder(theme.border, lineWidth: 1))
                    }
                }
            }
            .padding(M.xl)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .task(id: project.analyzedAt) { await loadProblems() }
    }

    private struct SectionItem: Identifiable {
        let view: ViewInfo
        let module: Module
        var id: String { view.key + "|" + module.id }
    }

    private var sections: [SectionItem] {
        var out: [SectionItem] = []
        for v in data.contributions.views(at: ViewLocation.projectSection) {
            for m in project.modules where v.facets.isEmpty ? (m.facets.contains { $0.plugin == v.plugin } || m.analysis[v.plugin] != nil) : m.hasAnyFacet(v.facets) {
                out.append(SectionItem(view: v, module: m))
            }
        }
        return out
    }

    private func loadProblems() async {
        if let p = try? await AppModel.shared.client().problems(project.id) { problems = p }
    }
}

/// 一个模块
struct ModuleCard: View {
    let project: Project
    let module: Module
    @ObservedObject var model: ProjectDetailModel
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        PPCard {
            VStack(alignment: .leading, spacing: M.md) {
                HStack(spacing: M.md) {
                    PPIcon("box", size: M.icon, color: theme.iconSecondary)
                    Text(module.name).font(.pp(.title))
                    if !module.relDir.isEmpty {
                        Text(module.relDir).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                    }
                    Spacer()
                    if module.needsSync && !module.synced {
                        PPButton(tr("project.sync"), icon: "refresh-cw", size: .small) { Actions.sync(project.id, moduleId: module.id) }
                    }
                    if !Actions.isRemote {
                        PPMenuButton(alignment: .trailing, entries: {
                            Actions.openWithEntries(project.id, moduleId: module.id)
                        }) {
                            PPIcon("ellipsis", size: M.icon, color: theme.iconSecondary)
                                .frame(width: M.iconButtonSmall, height: M.iconButtonSmall).contentShape(Rectangle())
                        }
                    }
                }
                if !module.facets.isEmpty {
                    FlowRow(spacing: M.xs) {
                        ForEach(module.facets, id: \.id) { f in
                            FacetBadgeView(facet: data.contributions.facet(f.id)
                                ?? FacetBadge(id: f.id, name: f.id, icon: nil, category: nil, plugin: f.plugin ?? ""))
                        }
                    }
                }
                if !module.info.isEmpty {
                    PPKeyValueList(rows: module.info.map { .init(key: $0.label, value: $0.value) }, keyWidth: 120)
                }
                if !module.toolchains.isEmpty {
                    PPDivider(subtle: true)
                    VStack(alignment: .leading, spacing: M.sm) {
                        ForEach(module.toolchains.keys.sorted(), id: \.self) { kind in
                            if let sel = module.toolchains[kind] {
                                ToolchainSelectionRow(project: project, selection: sel, model: model)
                            }
                        }
                    }
                }
                if let deps = module.dependencies, deps.state != "none" {
                    PPDivider(subtle: true)
                    DependencyRow(projectId: project.id, moduleId: module.id, deps: deps)
                }
            }
        }
    }
}

/// 一类工具链：当前选择 + 修改（自动 / 已安装 / 自定义约束）
struct ToolchainSelectionRow: View {
    let project: Project
    let selection: ToolchainSelection
    @ObservedObject var model: ProjectDetailModel
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.md) {
            Text(data.kindName(selection.kind)).foregroundColor(theme.textSecondary).frame(width: 120, alignment: .leading)
            if selection.missing {
                PPBadge(text: tr("toolchain.missing"), tone: "danger")
                if let c = selection.constraint, !c.isEmpty { Text(c).font(.pp(.mono)).foregroundColor(theme.textSecondary) }
            } else {
                Text(selection.version ?? "").font(.pp(.mono))
                if let tc = data.toolchains.first(where: { $0.id == selection.toolchainId }), let v = tc.vendor {
                    Text(v).foregroundColor(theme.textTertiary)
                }
            }
            if selection.override { PPBadge(text: tr("toolchain.override"), tone: "info") }
            if let s = selection.source, !s.isEmpty {
                PPInfoButton(text: tr("toolchain.source", s, selection.constraint ?? "-"))
            }
            Spacer(minLength: M.md)
            ToolchainOverridePicker(project: project, kind: selection.kind, model: model)
        }
        .frame(minHeight: M.rowHeight)
    }
}

/// 工具链覆盖选择（project.settings 的 toolchains）
struct ToolchainOverridePicker: View {
    let project: Project
    let kind: String
    @ObservedObject var model: ProjectDetailModel
    @ObservedObject private var data = AppModel.shared.data

    private static let auto = "__auto"
    private static let custom = "__custom"

    var body: some View {
        let current = project.settings?.toolchains[kind]
        let installed = data.toolchains(kind: kind)
        var opts: [PPDropdown<String>.Option] = [.init(value: Self.auto, title: tr("toolchain.auto"))]
        opts += installed.map { .init(value: $0.id, title: $0.displayName, detail: $0.source) }
        if let c = current?.constraint, !c.isEmpty {
            opts.append(.init(value: "c:" + c, title: tr("toolchain.constraintValue", c)))
        }
        opts.append(.init(value: Self.custom, title: tr("toolchain.customConstraint")))
        let sel: String = {
            if let id = current?.id, !id.isEmpty { return id }
            if let c = current?.constraint, !c.isEmpty { return "c:" + c }
            return Self.auto
        }()
        return PPDropdown(options: opts, selection: Binding(get: { sel }, set: { v in
            switch v {
            case Self.auto: model.setToolchainOverride(kind: kind, nil)
            case Self.custom:
                Dialogs.input(title: tr("toolchain.customConstraint"), label: tr("toolchain.constraintLabel"),
                              initial: current?.constraint ?? "", ok: tr("common.save")) { c in
                    let p = try await AppModel.shared.client().updateProjectSettings(project.id, toolchains: [kind: ToolchainOverride(constraint: c)])
                    model.project = p
                }
            case let s where s.hasPrefix("c:"): break
            default: model.setToolchainOverride(kind: kind, ToolchainOverride(id: v))
            }
        }), size: .small, minWidth: 160, maxWidth: 260)
    }
}

/// 依赖状态 + 安装
struct DependencyRow: View {
    let projectId: String
    let moduleId: String
    let deps: DependencyState
    @ObservedObject private var runs = AppModel.shared.runs
    @Environment(\.theme) private var theme

    var body: some View {
        let installing = runs.activeTasks.contains { $0.projectId == projectId && $0.moduleId == moduleId && $0.kind == "install" }
        HStack(spacing: M.md) {
            Text(tr("deps.title")).foregroundColor(theme.textSecondary).frame(width: 120, alignment: .leading)
            PPBadge(text: Self.title(deps.state), tone: Self.tone(deps.state))
            if let m = deps.manager { Text(m).font(.pp(.mono)).foregroundColor(theme.textSecondary) }
            if let msg = deps.message, !msg.isEmpty { PPInfoButton(text: msg) }
            Spacer(minLength: M.md)
            PPButton(deps.state == "ok" ? tr("deps.reinstall") : tr("deps.install"), icon: "download",
                     kind: deps.state == "ok" ? .ghost : .primary, size: .small, busy: installing) {
                Actions.installDeps(projectId: projectId, moduleId: moduleId)
            }
            PPMenuButton(alignment: .trailing, entries: {
                [.item(tr("deps.cleanInstall"), icon: "eraser") {
                    Dialogs.confirm(title: tr("deps.cleanInstall"), message: tr("deps.cleanInstall.confirm"), ok: tr("deps.cleanInstall")) {
                        Actions.installDeps(projectId: projectId, moduleId: moduleId, clean: true)
                    }
                }]
            }) {
                PPIcon("chevron-down", size: M.iconSmall).frame(width: M.iconButtonSmall, height: M.iconButtonSmall).contentShape(Rectangle())
            }
        }
    }

    static func title(_ s: String) -> String {
        switch s {
        case "ok": return tr("deps.ok")
        case "missing": return tr("deps.missing")
        case "outdated": return tr("deps.outdated")
        default: return tr("deps.unknown")
        }
    }

    static func tone(_ s: String) -> String {
        switch s {
        case "ok": return "success"
        case "missing": return "danger"
        case "outdated": return "warning"
        default: return "neutral"
        }
    }
}

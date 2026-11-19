import PaperplaneKit
import SwiftUI

/// 运行：左侧运行配置列表，右侧控制台（诊断条、工具栏、ANSI 日志、可切换的插件 run.panel 侧栏）。
struct RunTab: View {
    let project: Project
    let summary: ProjectSummary
    @ObservedObject private var nav = AppModel.shared.nav
    @ObservedObject private var runs = AppModel.shared.runs
    @ObservedObject private var data = AppModel.shared.data
    @State private var listWidth: CGFloat = 240
    @Environment(\.theme) private var theme

    private var configs: [RunConfig] { project.allRunConfigs }

    private var selectedId: String? {
        if let c = nav.selectedConfig[project.id], configs.contains(where: { $0.id == c }) { return c }
        return project.defaultConfigId ?? configs.first?.id
    }

    var body: some View {
        PPSplitView(leftWidth: $listWidth, minLeft: 180, maxLeft: 420) {
            configList
        } right: {
            if let cid = selectedId, let cfg = project.runConfig(cid) {
                RunConsoleArea(project: project, config: cfg, run: runs.latestRun(projectId: project.id, configId: cid))
                    .id(cid)
            } else {
                PPEmptyState(icon: "play", title: tr("run.noConfig"), actionTitle: tr("runconfig.new"), actionIcon: "plus") {
                    RunConfigDialog.presentNew(project: project)
                }
            }
        }
    }

    private var configList: some View {
        VStack(spacing: 0) {
            HStack {
                Text(tr("runconfig.title")).font(.pp(.label)).foregroundColor(theme.textSecondary)
                Spacer()
                PPIconButton("plus", help: tr("runconfig.new"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    RunConfigDialog.presentNew(project: project)
                }
            }
            .padding(.leading, M.lg)
            .padding(.trailing, M.sm)
            .frame(height: M.toolbarHeight)
            PPDivider(subtle: true)
            ScrollView {
                VStack(alignment: .leading, spacing: 1) {
                    ForEach(project.modules) { m in
                        if project.modules.count > 1 && !m.runConfigs.isEmpty {
                            Text(m.name).font(.pp(.caption)).foregroundColor(theme.textTertiary)
                                .padding(.horizontal, M.md).padding(.top, M.md).padding(.bottom, M.xxs)
                        }
                        ForEach(m.runConfigs) { c in configRow(c, module: m) }
                    }
                }
                .padding(M.sm)
            }
        }
        .background(theme.bgContent)
    }

    private func configRow(_ c: RunConfig, module: Module) -> some View {
        let active = runs.activeRun(projectId: project.id, configId: c.id)
        let latest = active ?? runs.latestRun(projectId: project.id, configId: c.id)
        let isDefault = c.id == project.defaultConfigId
        let inLaunch = summary.launch.contains(c.id)
        let type = data.contributions.runConfigType(plugin: c.plugin, type: c.type)
        return PPRow(selected: c.id == selectedId, action: { nav.selectedConfig[project.id] = c.id }) {
            HStack(spacing: M.sm) {
                ZStack(alignment: .bottomTrailing) {
                    PPIcon(raw: type?.icon, plugin: c.plugin, fallback: "play", size: M.icon)
                    if let latest { StatusDot(state: latest.state, size: 6).offset(x: 2, y: 2) }
                }
                Text(c.name).lineLimit(1).truncationMode(.middle)
                if isDefault { PPIcon("pin", size: 12, color: theme.iconSecondary).help(tr("runconfig.default")) }
                if c.stale { PPIcon("triangle-alert", size: 12, color: theme.warning).help(tr("runconfig.stale")) }
                Spacer(minLength: M.xs)
                // 加入/移出工作区「启动」面板（已加入的常亮）
                PPIconButton("rocket", help: inLaunch ? tr("launch.remove") : tr("launch.add"), size: M.iconButtonSmall, iconSize: M.iconSmall,
                             tint: inLaunch ? theme.accent : theme.iconSecondary, active: inLaunch) {
                    Actions.setLaunch(projectId: project.id, configIds: [c.id], on: !inLaunch)
                }
                if let active {
                    PPIconButton("square", help: tr("action.stop"), size: M.iconButtonSmall, iconSize: M.iconSmall, tint: theme.danger) {
                        Actions.stop(runId: active.id)
                    }
                } else {
                    PPIconButton("play", help: tr("action.run"), size: M.iconButtonSmall, iconSize: M.iconSmall, tint: theme.success) {
                        nav.selectedConfig[project.id] = c.id
                        Actions.run(projectId: project.id, configId: c.id)
                    }
                }
            }
        }
        .ppContextMenu { menu(for: c, isDefault: isDefault, inLaunch: inLaunch) }
    }

    private func menu(for c: RunConfig, isDefault: Bool, inLaunch: Bool) -> [MenuEntry] {
        var e: [MenuEntry] = [
            .item(tr("action.run"), icon: "play") { Actions.run(projectId: project.id, configId: c.id) },
            .item(tr("runconfig.edit"), icon: "pencil") { RunConfigDialog.presentEdit(project: project, config: c) },
            .item(tr("runconfig.duplicate"), icon: "copy") { RunConfigDialog.presentDuplicate(project: project, config: c) },
            .item(tr("runconfig.setDefault"), icon: "pin", disabled: isDefault) {
                AppModel.shared.perform { api in
                    try await api.setDefaultRunConfig(projectId: project.id, configId: c.id)
                    AppModel.shared.bus.projectChanged.send(project.id)
                }
            },
            .item(inLaunch ? tr("launch.remove") : tr("launch.add"), icon: "rocket") {
                Actions.setLaunch(projectId: project.id, configIds: [c.id], on: !inLaunch)
            },
        ]
        if c.edited && c.source != "user" {
            e.append(.item(tr("runconfig.reset"), icon: "rotate-ccw") {
                AppModel.shared.perform { api in
                    _ = try await api.resetRunConfig(projectId: project.id, configId: c.id)
                    AppModel.shared.bus.projectChanged.send(project.id)
                }
            })
        }
        e.append(.divider)
        e.append(.item(tr("common.delete"), icon: "trash-2", destructive: true) {
            Dialogs.confirm(title: tr("runconfig.delete.title", c.name), ok: tr("common.delete"), danger: true) {
                try await AppModel.shared.client().deleteRunConfig(projectId: project.id, configId: c.id)
                AppModel.shared.bus.projectChanged.send(project.id)
            }
        })
        return e
    }
}

/// 控制台区域：诊断条 + 控制台 + 插件侧栏
struct RunConsoleArea: View {
    let project: Project
    let config: RunConfig
    let run: Run?
    @ObservedObject private var data = AppModel.shared.data
    @State private var panelOpen = UserDefaults.standard.bool(forKey: "PaperplaneRunPanelOpen")
    @State private var panelKey: String?
    @State private var panelWidth: CGFloat = 320
    @State private var dismissed: Set<String> = []
    @Environment(\.theme) private var theme

    private var module: Module? { project.module(config.moduleId) }

    /// 当前运行状态下可显示的插件侧栏
    private var panels: [ViewInfo] {
        guard let run, let m = module else { return [] }
        return data.contributions.views(at: ViewLocation.runPanel).filter { v in
            let facetOK = v.facets.isEmpty ? (v.plugin == config.plugin || m.facets.contains { $0.plugin == v.plugin }) : m.hasAnyFacet(v.facets)
            let stateOK = v.runState == nil || v.runState == "" || v.runState == run.state
            return facetOK && stateOK
        }
    }

    var body: some View {
        let ps = panels
        HStack(spacing: 0) {
            VStack(spacing: 0) {
                diagnoses
                if let run {
                    RunResourcePanel(run: run)
                        .id(run.id)
                    PPDivider(subtle: true)
                    ConsolePanel(consoleId: run.id, runId: run.id, logFile: run.logFile, inputEnabled: run.isActive) {
                        controls(run: run, panels: ps)
                    }
                } else {
                    VStack(spacing: 0) {
                        HStack { controls(run: nil, panels: []); Spacer() }
                            .padding(.horizontal, M.sm)
                            .frame(height: M.toolbarHeight)
                        PPDivider(subtle: true)
                        PPEmptyState(icon: "terminal", title: tr("run.notStarted"), actionTitle: tr("action.run"), actionIcon: "play") {
                            Actions.run(projectId: project.id, configId: config.id)
                        }
                        .background(theme.consoleBg)
                    }
                }
            }
            if panelOpen, let run, !ps.isEmpty {
                let current = ps.first { $0.key == panelKey } ?? ps[0]
                PPDivider(vertical: true)
                VStack(spacing: 0) {
                    if ps.count > 1 {
                        PPTabBar(tabs: ps.map { .init(value: $0.key, title: $0.title, icon: IconSource.parse($0.icon, pluginId: $0.plugin)) },
                                 selection: Binding(get: { current.key }, set: { panelKey = $0 }))
                            .padding(.horizontal, M.sm)
                        PPDivider(subtle: true)
                    } else {
                        HStack {
                            Text(current.title).font(.pp(.label))
                            Spacer()
                        }
                        .padding(.horizontal, M.md)
                        .frame(height: M.toolbarHeight)
                        PPDivider(subtle: true)
                    }
                    PluginViewHost(view: current, context: ViewContext(plugin: current.plugin, view: current.id, projectId: project.id,
                                                                       moduleId: config.moduleId, runId: run.id))
                        .id(current.key + run.id)
                }
                .frame(width: panelWidth)
                .background(theme.bgContent)
            }
        }
    }

    @ViewBuilder private var diagnoses: some View {
        if let run {
            let list = run.diagnoses.filter { !dismissed.contains($0.stableKey) }
            if !list.isEmpty || (run.state == RunState.failed && run.error?.isEmpty == false) {
                VStack(alignment: .leading, spacing: M.sm) {
                    if run.state == RunState.failed, let e = run.error, !e.isEmpty, list.isEmpty {
                        PPAlert(tone: "danger", title: e)
                    }
                    ForEach(list, id: \.stableKey) { d in
                        DiagnosisRow(diagnosis: d, runId: run.id, projectId: project.id)
                            .overlay(alignment: .topTrailing) {
                                PPIconButton("x", help: tr("common.close"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                                    dismissed.insert(d.stableKey)
                                }
                                .padding(M.xs)
                            }
                    }
                }
                .padding(M.md)
                .frame(maxHeight: 260)
                .fixedSize(horizontal: false, vertical: true)
                PPDivider(subtle: true)
            }
        }
    }

    @ViewBuilder private func controls(run: Run?, panels: [ViewInfo]) -> some View {
        let active = run?.isActive == true
        if active, let run {
            PPIconButton("rotate-cw", help: tr("action.restart")) { Actions.restart(runId: run.id) }
            PPIconButton("square", help: tr("action.stop") + "  ⌘.", tint: theme.danger) { Actions.stop(runId: run.id) }
        } else {
            PPIconButton("play", help: tr("action.run") + "  ⌘R", tint: theme.success) {
                Actions.run(projectId: project.id, configId: config.id)
            }
        }
        if let run {
            HStack(spacing: M.sm) {
                StatusDot(state: run.state)
                Text(statusText(run)).font(.pp(.small)).foregroundColor(theme.textSecondary).lineLimit(1)
            }
            .padding(.horizontal, M.sm)
            ForEach(run.urls.prefix(2), id: \.self) { u in
                RunURLLinks(url: u, projectId: run.projectId, configId: run.configId)
                    .padding(.trailing, M.sm)
            }
            if active {
                RunResourceText(runId: run.id, fallback: (run.cpu, run.rssMB))
            }
        }
        let acts = Actions.applicable(data.contributions.actions(at: ActionLocation.runToolbar),
                                      facets: Set(module?.facets.map(\.id) ?? []), plugins: [config.plugin ?? ""])
        ForEach(acts, id: \.key) { a in
            PPIconButton(ref: IconSource.parse(a.icon, pluginId: a.plugin) ?? .builtin("zap"), help: a.title) {
                Actions.invoke(a, projectId: project.id, moduleId: config.moduleId, runId: run?.id)
            }
        }
        if !panels.isEmpty {
            PPIconButton(panelOpen ? "panel-right-close" : "panel-right", help: tr("run.panel"), active: panelOpen) {
                panelOpen.toggle()
                UserDefaults.standard.set(panelOpen, forKey: "PaperplaneRunPanelOpen")
            }
        }
    }

    private func statusText(_ r: Run) -> String {
        // 用户主动停止的不显示退出码（被信号结束时退出码没有意义）
        if !r.isActive && r.stoppedByUser { return tr("status.stopped") }
        var s = RunStatusStyle.title(r.state)
        if !r.isActive, let code = r.exitCode { s += " · " + tr("run.exitCode", code) }
        if r.isActive, let start = r.startedAt { s += " · " + Format.duration(Date().timeIntervalSince(start)) }
        return s
    }
}

import PaperplaneKit
import SwiftUI

// 工作区「启动」：用户从各工程挑出要一起跑的运行配置（ProjectSummary.launch），按工程分组，
// 每项可单独运行/停止/看日志，也可一键全部运行、全部停止。数据存在内核（launch.* 接口，AI 也能用）。

// MARK: - 操作

extension Actions {
    /// 加入或移出启动面板
    static func setLaunch(projectId: String, configIds: [String], on: Bool) {
        model.perform { api in
            if on {
                try await api.launchAdd(projectId: projectId, configIds: configIds)
            } else {
                try await api.launchRemove(projectId: projectId, configIds: configIds)
            }
        }
    }

    /// 运行启动面板里还没在运行的项（可限定到一个工程）
    static func launchRun(workspaceId: String, projectId: String? = nil) {
        model.perform(tr("action.run.failed")) { api in
            let res = try await api.launchRun(workspaceId: workspaceId, projectId: projectId)
            for r in res.started { model.runs.runs[r.id] = r }
            reportLaunchErrors(res.errors)
        }
    }

    /// 停止启动面板里正在运行的项（可限定到一个工程）
    static func launchStop(workspaceId: String, projectId: String? = nil) {
        model.perform(tr("action.stop.failed")) { api in
            let res = try await api.launchStop(workspaceId: workspaceId, projectId: projectId)
            reportLaunchErrors(res.errors)
        }
    }

    private static func reportLaunchErrors(_ errors: [LaunchRunError]) {
        guard let first = errors.first else { return }
        let name = model.data.projects[first.projectId]?.configs.first { $0.id == first.configId }?.name ?? first.configId
        model.toasts.show("danger", tr("launch.someFailed", errors.count), message: name + "：" + first.error)
    }
}

// MARK: - 数据

/// 工作区里的启动项：按工作区内工程顺序，每个工程按加入顺序
struct LaunchGroupData: Identifiable {
    let project: ProjectSummary
    let items: [ConfigBrief]
    var id: String { project.id }

    @MainActor static func groups(workspaceId: String, data: DataStore) -> [LaunchGroupData] {
        data.projects(in: workspaceId).compactMap { p in
            let items = p.launch.compactMap { id in p.configs.first { $0.id == id } }
            return items.isEmpty ? nil : LaunchGroupData(project: p, items: items)
        }
    }

    /// 正在运行的启动项个数（「启动」分段上的数字）
    @MainActor static func activeCount(workspaceId: String, data: DataStore, runs: RunStore) -> Int {
        groups(workspaceId: workspaceId, data: data).reduce(0) { n, g in
            n + g.items.filter { runs.activeRun(projectId: g.project.id, configId: $0.id) != nil }.count
        }
    }
}

// MARK: - 页面

struct LaunchBoardPage: View {
    let workspaceId: String
    @ObservedObject private var data = AppModel.shared.data
    @ObservedObject private var runs = AppModel.shared.runs
    @ObservedObject private var nav = AppModel.shared.nav
    @Environment(\.theme) private var theme

    private var allGroups: [LaunchGroupData] { LaunchGroupData.groups(workspaceId: workspaceId, data: data) }

    /// 工具栏搜索：匹配工程名或运行配置名
    private var groups: [LaunchGroupData] {
        let q = nav.search.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return allGroups }
        return allGroups.compactMap { g in
            if g.project.name.localizedCaseInsensitiveContains(q) { return g }
            let items = g.items.filter { $0.name.localizedCaseInsensitiveContains(q) || $0.moduleName.localizedCaseInsensitiveContains(q) }
            return items.isEmpty ? nil : LaunchGroupData(project: g.project, items: items)
        }
    }

    var body: some View {
        let all = allGroups
        VStack(spacing: 0) {
            if all.isEmpty {
                PPEmptyState(icon: "rocket", title: tr("launch.empty"), actionTitle: tr("launch.choose"), actionIcon: "list-checks") {
                    LaunchPickerDialog.present(workspaceId: workspaceId)
                }
            } else {
                toolbar(all)
                PPDivider(subtle: true)
                let list = groups
                if list.isEmpty {
                    PPEmptyState(icon: "search", title: tr("workspace.noMatch"))
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: M.lg) {
                            ForEach(list) { g in LaunchGroupView(workspaceId: workspaceId, group: g) }
                        }
                        .padding(M.xl)
                    }
                }
            }
        }
    }

    private func toolbar(_ all: [LaunchGroupData]) -> some View {
        let total = all.reduce(0) { $0 + $1.items.count }
        let active = all.reduce(0) { n, g in n + g.items.filter { runs.activeRun(projectId: g.project.id, configId: $0.id) != nil }.count }
        return HStack(spacing: M.sm) {
            Text(tr("launch.summary", total, active)).font(.pp(.small)).foregroundColor(theme.textSecondary)
            PPInfoButton(text: tr("launch.info"))
            Spacer()
            PPButton(tr("launch.choose"), icon: "list-checks", kind: .ghost, size: .small) {
                LaunchPickerDialog.present(workspaceId: workspaceId)
            }
            PPButton(tr("launch.stopAll"), icon: "square", size: .small) { Actions.launchStop(workspaceId: workspaceId) }
                .disabled(active == 0)
            PPButton(tr("launch.runAll"), icon: "play", kind: .primary, size: .small) { Actions.launchRun(workspaceId: workspaceId) }
                .disabled(active == total)
        }
        .padding(.horizontal, M.xl)
        .frame(height: M.toolbarHeight)
    }
}

// MARK: - 分组

private struct LaunchGroupView: View {
    let workspaceId: String
    let group: LaunchGroupData
    @ObservedObject private var runs = AppModel.shared.runs
    @ObservedObject private var nav = AppModel.shared.nav
    @Environment(\.theme) private var theme

    var body: some View {
        let p = group.project
        let active = group.items.filter { runs.activeRun(projectId: p.id, configId: $0.id) != nil }.count
        VStack(spacing: 0) {
            HStack(spacing: M.sm) {
                StatusDot(state: p.status)
                Text(p.name).font(.pp(.heading)).lineLimit(1)
                Text(Format.abbreviatePath(p.path)).font(.pp(.small)).foregroundColor(theme.textTertiary)
                    .lineLimit(1).truncationMode(.middle)
                Spacer(minLength: M.md)
                PPIconButton("play", help: tr("launch.runGroup"), size: M.iconButtonSmall, iconSize: M.iconSmall, tint: theme.success) {
                    Actions.launchRun(workspaceId: workspaceId, projectId: p.id)
                }
                .disabled(active == group.items.count)
                PPIconButton("square", help: tr("launch.stopGroup"), size: M.iconButtonSmall, iconSize: M.iconSmall, tint: theme.danger) {
                    Actions.launchStop(workspaceId: workspaceId, projectId: p.id)
                }
                .disabled(active == 0)
                PPIconButton("arrow-up-right", help: tr("launch.openProject"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    nav.openProject(p.id, tab: .run)
                }
            }
            .padding(.horizontal, M.lg)
            .frame(height: M.toolbarHeight)
            PPDivider(subtle: true)
            VStack(spacing: 0) {
                ForEach(Array(group.items.enumerated()), id: \.element.id) { i, c in
                    if i > 0 { PPDivider(subtle: true).padding(.leading, M.lg) }
                    LaunchItemRow(project: p, config: c, showModule: Set(group.items.map(\.moduleId)).count > 1 || !c.moduleName.isEmpty)
                }
            }
            .padding(.vertical, M.xxs)
        }
        .background(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous).fill(theme.bgSurface))
        .overlay(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous).strokeBorder(theme.border, lineWidth: 1))
    }
}

// MARK: - 行

private struct LaunchItemRow: View {
    let project: ProjectSummary
    let config: ConfigBrief
    let showModule: Bool
    @ObservedObject private var runs = AppModel.shared.runs
    @ObservedObject private var nav = AppModel.shared.nav
    @State private var hover = false
    @Environment(\.theme) private var theme

    var body: some View {
        let active = runs.activeRun(projectId: project.id, configId: config.id)
        let latest = active ?? runs.latestRun(projectId: project.id, configId: config.id)
        HStack(spacing: M.md) {
            ZStack(alignment: .bottomTrailing) {
                PPIcon(ref: IconSource.parse(config.icon) ?? .builtin("play"), size: M.icon)
                if let latest { StatusDot(state: latest.state, size: 6).offset(x: 2, y: 2) }
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(config.name).font(.pp(.body)).lineLimit(1).truncationMode(.middle)
                if showModule, !config.moduleName.isEmpty {
                    Text(config.moduleName).font(.pp(.caption)).foregroundColor(theme.textTertiary).lineLimit(1)
                }
            }
            .frame(minWidth: 120, alignment: .leading)
            Spacer(minLength: M.md)
            status(latest)
                .frame(width: 156, alignment: .leading)
            Group {
                if let active, let u = active.urls.first {
                    PPLink(title: Format.shortURL(u), mono: true, icon: "external-link") { Actions.openURL(u) }
                } else {
                    Color.clear
                }
            }
            // 地址列在窗口窄时可以收缩（其余列定宽，各行对齐）
            .frame(minWidth: 0, maxWidth: 150, alignment: .leading)
            Group {
                if let active {
                    RunResourceText(runId: active.id, fallback: (active.cpu, active.rssMB))
                } else {
                    Color.clear
                }
            }
            .frame(width: 120, alignment: .leading)
            actions(active)
        }
        .padding(.horizontal, M.lg)
        .frame(height: 44)
        .background(hover ? theme.bgHover : .clear)
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture(count: 2) { nav.openRunConsole(projectId: project.id, configId: config.id) }
        .ppContextMenu { menu(active) }
    }

    @ViewBuilder private func status(_ r: Run?) -> some View {
        if let r {
            if r.isActive {
                // 运行时长每秒刷新（只在这一小块里）
                TimelineView(.periodic(from: .now, by: 1)) { _ in
                    Text(statusText(r)).font(.pp(.small)).foregroundColor(theme.textSecondary).lineLimit(1)
                }
            } else {
                Text(statusText(r)).font(.pp(.small)).foregroundColor(r.state == RunState.failed ? theme.danger : theme.textTertiary).lineLimit(1)
            }
        } else {
            Text(tr("launch.notRun")).font(.pp(.small)).foregroundColor(theme.textTertiary)
        }
    }

    private func statusText(_ r: Run) -> String {
        if !r.isActive && r.stoppedByUser { return tr("status.stopped") }
        var s = RunStatusStyle.title(r.state)
        if !r.isActive, let code = r.exitCode { s += " · " + tr("run.exitCode", code) }
        if r.isActive, let start = r.startedAt { s += " · " + Format.duration(Date().timeIntervalSince(start)) }
        return s
    }

    @ViewBuilder private func actions(_ active: Run?) -> some View {
        HStack(spacing: M.xxs) {
            if let active {
                PPIconButton("rotate-cw", help: tr("action.restart"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    Actions.restart(runId: active.id)
                }
                PPIconButton("square", help: tr("action.stop"), size: M.iconButtonSmall, iconSize: M.iconSmall, tint: theme.danger) {
                    Actions.stop(runId: active.id)
                }
            } else {
                Color.clear.frame(width: M.iconButtonSmall, height: M.iconButtonSmall)
                PPIconButton("play", help: tr("action.run"), size: M.iconButtonSmall, iconSize: M.iconSmall, tint: theme.success) {
                    Actions.run(projectId: project.id, configId: config.id)
                }
            }
            PPIconButton("terminal", help: tr("launch.logs"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                nav.openRunConsole(projectId: project.id, configId: config.id)
            }
            PPIconButton("x", help: tr("launch.remove"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                Actions.setLaunch(projectId: project.id, configIds: [config.id], on: false)
            }
            .opacity(hover ? 1 : 0)
        }
    }

    private func menu(_ active: Run?) -> [MenuEntry] {
        var e: [MenuEntry] = []
        if let active {
            e.append(.item(tr("action.restart"), icon: "rotate-cw") { Actions.restart(runId: active.id) })
            e.append(.item(tr("action.stop"), icon: "square") { Actions.stop(runId: active.id) })
        } else {
            e.append(.item(tr("action.run"), icon: "play") { Actions.run(projectId: project.id, configId: config.id) })
        }
        e.append(.item(tr("launch.logs"), icon: "terminal") { nav.openRunConsole(projectId: project.id, configId: config.id) })
        e.append(.divider)
        e.append(.item(tr("launch.remove"), icon: "x") {
            Actions.setLaunch(projectId: project.id, configIds: [config.id], on: false)
        })
        return e
    }
}

// MARK: - 选择启动项

struct LaunchPickerDialog: View {
    let id: String
    let workspaceId: String
    @ObservedObject private var data = AppModel.shared.data
    /// "工程id/配置id"
    @State private var selected: Set<String> = []
    @State private var busy = false
    @Environment(\.theme) private var theme

    static func present(workspaceId: String) {
        let id = "launch-picker-" + workspaceId
        AppModel.shared.dialogs.present(id: id) { LaunchPickerDialog(id: id, workspaceId: workspaceId) }
    }

    private var projects: [ProjectSummary] { data.projects(in: workspaceId).filter { !$0.configs.isEmpty } }

    var body: some View {
        PPDialog(title: tr("launch.choose"), width: M.dialogWidthLarge, onClose: close) {
            if projects.isEmpty {
                Text(tr("launch.choose.none")).foregroundColor(theme.textSecondary)
                    .frame(maxWidth: .infinity, minHeight: 120)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: M.lg) {
                        ForEach(projects) { p in projectSection(p) }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 440)
            }
        } footer: {
            PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
            PPButton(tr("common.ok"), kind: .primary, busy: busy, action: save)
                .keyboardShortcut(.defaultAction)
                .disabled(projects.isEmpty)
        }
        .onAppear {
            selected = Set(data.projects(in: workspaceId).flatMap { p in p.launch.map { p.id + "/" + $0 } })
        }
    }

    private func projectSection(_ p: ProjectSummary) -> some View {
        let modules = Dictionary(grouping: p.configs, by: \.moduleId)
        let order = p.configs.map(\.moduleId).reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
        let all = p.configs.map { p.id + "/" + $0.id }
        let allOn = all.allSatisfy { selected.contains($0) }
        return VStack(alignment: .leading, spacing: M.xs) {
            HStack(spacing: M.sm) {
                Text(p.name).font(.pp(.label))
                Spacer()
                PPButton(allOn ? tr("launch.choose.clear") : tr("launch.choose.all"), kind: .link, size: .small) {
                    if allOn { selected.subtract(all) } else { selected.formUnion(all) }
                }
            }
            ForEach(order, id: \.self) { mid in
                let list = modules[mid] ?? []
                if order.count > 1, let name = list.first?.moduleName, !name.isEmpty {
                    Text(name).font(.pp(.caption)).foregroundColor(theme.textTertiary).padding(.top, M.xxs)
                }
                ForEach(list) { c in
                    let key = p.id + "/" + c.id
                    HStack(spacing: M.sm) {
                        PPCheckbox(isOn: Binding(get: { selected.contains(key) }, set: { on in
                            if on { selected.insert(key) } else { selected.remove(key) }
                        }))
                        PPIcon(ref: IconSource.parse(c.icon) ?? .builtin("play"), size: M.iconSmall)
                        Text(c.name).lineLimit(1)
                    }
                    .padding(.leading, M.sm)
                    .contentShape(Rectangle())
                    .onTapGesture { if selected.contains(key) { selected.remove(key) } else { selected.insert(key) } }
                }
            }
        }
    }

    private func close() { AppModel.shared.dialogs.dismiss(id) }

    private func save() {
        busy = true
        let ps = data.projects(in: workspaceId)
        let chosen = selected
        Task {
            do {
                let api = try AppModel.shared.client()
                for p in ps {
                    let cur = p.launch
                    let want = p.configs.map(\.id).filter { chosen.contains(p.id + "/" + $0) }
                    let add = want.filter { !cur.contains($0) }
                    let remove = cur.filter { !want.contains($0) }
                    if !add.isEmpty { try await api.launchAdd(projectId: p.id, configIds: add) }
                    if !remove.isEmpty { try await api.launchRemove(projectId: p.id, configIds: remove) }
                }
                close()
            } catch {
                AppModel.shared.toasts.error(error)
            }
            busy = false
        }
    }
}

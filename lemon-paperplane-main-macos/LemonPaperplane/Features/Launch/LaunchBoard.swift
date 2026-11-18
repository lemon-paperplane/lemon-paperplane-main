import AppKit
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

    /// 把脚本/可执行文件加到启动
    static func launchAddFile(projectId: String, path: String) {
        model.perform { api in
            let r = try await api.launchAddFile(projectId: projectId, path: path)
            model.toasts.show("success", r.created ? tr("launch.fileAdded", r.name) : tr("launch.fileAlready", r.name))
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
            let items = g.items.filter {
                $0.name.localizedCaseInsensitiveContains(q) || $0.moduleName.localizedCaseInsensitiveContains(q)
                    || (g.project.launchAlias[$0.id] ?? "").localizedCaseInsensitiveContains(q)
            }
            return items.isEmpty ? nil : LaunchGroupData(project: g.project, items: items)
        }
    }

    var body: some View {
        let all = allGroups
        VStack(spacing: 0) {
            if all.isEmpty {
                VStack(spacing: M.lg) {
                    PPIcon("rocket", size: M.iconEmpty, color: theme.iconSecondary)
                    Text(tr("launch.empty")).font(.pp(.body)).foregroundColor(theme.textSecondary)
                    HStack(spacing: M.sm) {
                        PPButton(tr("launch.choose"), icon: "list-checks", kind: .primary) { LaunchPickerDialog.present(workspaceId: workspaceId) }
                        PPButton(tr("launch.addExecutable"), icon: "file-terminal") { AddRunnableDialog.present(workspaceId: workspaceId) }
                        PPButton(tr("launch.addCommand"), icon: "square-terminal") { AddCommandDialog.present(workspaceId: workspaceId) }
                    }
                    PPInfoButton(text: tr("launch.info"))
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
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
            PPButton(tr("launch.addExecutable"), icon: "file-terminal", kind: .ghost, size: .small) {
                AddRunnableDialog.present(workspaceId: workspaceId)
            }
            PPButton(tr("launch.addCommand"), icon: "square-terminal", kind: .ghost, size: .small) {
                AddCommandDialog.present(workspaceId: workspaceId)
            }
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
                Text(alias ?? config.name).font(.pp(.body)).lineLimit(1).truncationMode(.middle)
                if let sub = subtitle {
                    Text(sub).font(.pp(.caption)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.middle)
                }
            }
            .help(alias == nil ? "" : tr("launch.alias.original", config.name))
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

    private var alias: String? {
        let a = project.launchAlias[config.id] ?? ""
        return a.isEmpty ? nil : a
    }

    /// 第二行：有别名时是「原名 · 模块」，否则是模块名
    private var subtitle: String? {
        let module = showModule && !config.moduleName.isEmpty ? config.moduleName : nil
        if alias != nil { return [config.name, module].compactMap { $0 }.joined(separator: " · ") }
        return module
    }

    private func editAlias() {
        let field = FormField(key: "alias", title: tr("launch.alias"), type: "string", placeholder: config.name, info: tr("launch.alias.info"))
        let pid = project.id, cid = config.id
        Dialogs.form(title: tr("launch.alias.title", config.name), fields: [field], values: ["alias": .string(alias ?? "")],
                     ok: tr("common.save")) { v in
            try await AppModel.shared.client().launchSetAlias(projectId: pid, configId: cid, alias: v["alias"]?.stringValue ?? "")
        }
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
            PPIconButton("pencil", help: tr("launch.alias.set"), size: M.iconButtonSmall, iconSize: M.iconSmall) { editAlias() }
                .opacity(hover ? 1 : 0)
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
        e.append(.item(tr("launch.alias.set"), icon: "pencil") { editAlias() })
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

// MARK: - 添加命令

/// 手动输入一段命令（可多行）作为启动项：名称、所属工程、执行目录、命令（CodeMirror 输入框）
struct AddCommandDialog: View {
    let id: String
    let workspaceId: String
    @ObservedObject private var data = AppModel.shared.data
    @State private var projectId = ""
    @State private var name = ""
    @State private var cwd = ""
    @State private var command = ""
    @State private var busy = false
    @State private var showErrors = false
    @State private var error: String?
    @Environment(\.theme) private var theme

    static func present(workspaceId: String) {
        let id = "launch-add-command-" + workspaceId
        AppModel.shared.dialogs.present(id: id) { AddCommandDialog(id: id, workspaceId: workspaceId) }
    }

    private var projects: [ProjectSummary] { data.projects(in: workspaceId) }
    private var project: ProjectSummary? { projects.first { $0.id == projectId } }

    var body: some View {
        PPDialog(title: tr("launch.addCommand"), width: M.dialogWidthLarge, onClose: close) {
            VStack(alignment: .leading, spacing: M.md) {
                if projects.isEmpty {
                    Text(tr("launch.addCommand.noProject")).foregroundColor(theme.textSecondary)
                } else {
                    row(tr("common.name")) {
                        PPTextField(tr("launch.addCommand.namePlaceholder"), text: $name)
                    }
                    row(tr("launch.addCommand.project")) {
                        PPDropdown(options: projects.map { .init(value: $0.id, title: $0.name, detail: Format.abbreviatePath($0.path)) },
                                   selection: $projectId, fullWidth: true)
                    }
                    row(tr("launch.addCommand.cwd")) {
                        HStack(spacing: M.sm) {
                            PPTextField(tr("launch.addCommand.cwdPlaceholder"), text: $cwd, mono: true)
                            PPButton(tr("form.browse")) { chooseDir() }
                        }
                    }
                    HStack(alignment: .top, spacing: M.md) {
                        HStack(spacing: M.xs) {
                            Text(tr("launch.addCommand.command"))
                            Text("*").foregroundColor(theme.danger)
                            PPInfoButton(text: tr("launch.addCommand.commandInfo"))
                        }
                        .frame(width: M.formLabelWidth, alignment: .leading)
                        .padding(.top, 6)
                        PPCodeInput(text: $command, language: "shell", placeholder: tr("launch.addCommand.commandPlaceholder"),
                                    height: 220,
                                    invalid: showErrors && command.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                                    onSubmit: submit, onEscape: close)
                    }
                }
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
            PPButton(tr("launch.addCommand.add"), kind: .primary, busy: busy, action: submit)
                .keyboardShortcut(.defaultAction)
                .disabled(projects.isEmpty)
        }
        .onAppear {
            if projectId.isEmpty {
                projectId = AppModel.shared.nav.openProjectId.flatMap { id in projects.first { $0.id == id }?.id } ?? projects.first?.id ?? ""
            }
        }
    }

    private func row<C: View>(_ title: String, @ViewBuilder _ c: () -> C) -> some View {
        HStack(spacing: M.md) {
            Text(title).frame(width: M.formLabelWidth, alignment: .leading)
            c()
        }
    }

    /// 选执行目录：从工程目录开始；工程内的目录显示成相对路径
    private func chooseDir() {
        guard let p = project else { return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.directoryURL = URL(fileURLWithPath: cwd.isEmpty ? p.path : (cwd.hasPrefix("/") ? cwd : p.path + "/" + cwd))
        guard panel.runModal() == .OK, let path = panel.url?.path else { return }
        let root = (p.path as NSString).standardizingPath
        let chosen = (path as NSString).standardizingPath
        if chosen == root {
            cwd = ""
        } else if chosen.hasPrefix(root + "/") {
            cwd = String(chosen.dropFirst(root.count + 1))
        } else {
            cwd = chosen
        }
    }

    private func close() { AppModel.shared.dialogs.dismiss(id) }

    private func submit() {
        guard !busy else { return }
        guard !command.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !projectId.isEmpty else {
            showErrors = true
            return
        }
        busy = true
        error = nil
        let pid = projectId, n = name, c = command, dir = cwd
        Task {
            do {
                let r = try await AppModel.shared.client().launchAddCommand(projectId: pid, name: n, command: c, cwd: dir)
                close()
                AppModel.shared.toasts.show("success", tr("launch.commandAdded", r.name))
            } catch {
                self.error = error.localizedDescription
            }
            busy = false
        }
    }
}

// MARK: - 添加脚本或程序

/// 自己的文件选择框：只列内核判定能运行的文件（脚本、程序；可选加上其他源码），按目录分组，可筛选、多选
struct AddRunnableDialog: View {
    let id: String
    let workspaceId: String
    @ObservedObject private var data = AppModel.shared.data
    @State private var projectId = ""
    @State private var includeSources = false
    @State private var filter = ""
    @State private var list: RunnableList?
    @State private var loading = false
    @State private var selected: Set<String> = []
    @State private var busy = false
    @State private var error: String?
    @Environment(\.theme) private var theme

    static func present(workspaceId: String) {
        let id = "launch-add-runnable-" + workspaceId
        AppModel.shared.dialogs.present(id: id) { AddRunnableDialog(id: id, workspaceId: workspaceId) }
    }

    private var projects: [ProjectSummary] { data.projects(in: workspaceId) }

    /// 筛选后按目录分组（根目录在前）
    private var groups: [(dir: String, files: [RunnableFile])] {
        let q = filter.trimmingCharacters(in: .whitespaces)
        let files = (list?.files ?? []).filter { q.isEmpty || $0.path.localizedCaseInsensitiveContains(q) }
        var order: [String] = []
        var map: [String: [RunnableFile]] = [:]
        for f in files {
            if map[f.directory] == nil { order.append(f.directory) }
            map[f.directory, default: []].append(f)
        }
        return order.map { ($0, map[$0] ?? []) }
    }

    var body: some View {
        PPDialog(title: tr("launch.pick.title"), width: M.dialogWidthLarge, onClose: close) {
            VStack(alignment: .leading, spacing: M.md) {
                if projects.isEmpty {
                    Text(tr("launch.addCommand.noProject")).foregroundColor(theme.textSecondary)
                } else {
                    HStack(spacing: M.sm) {
                        if projects.count > 1 {
                            PPDropdown(options: projects.map { .init(value: $0.id, title: $0.name) }, selection: $projectId, minWidth: 180)
                        }
                        PPTextField(tr("launch.pick.filter"), text: $filter, icon: "search")
                    }
                    HStack(spacing: M.sm) {
                        PPSwitch(isOn: $includeSources)
                        Text(tr("launch.pick.sources")).font(.pp(.small)).foregroundColor(theme.textSecondary)
                        PPInfoButton(text: tr("launch.pick.sources.info"))
                        Spacer()
                        if list?.truncated == true {
                            Text(tr("launch.pick.truncated", list?.files.count ?? 0)).font(.pp(.small)).foregroundColor(theme.textTertiary)
                        }
                    }
                    fileList
                        .frame(height: 360) // 固定高度：加载、筛选时对话框不跳
                        .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgSubtle))
                        .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(theme.border, lineWidth: 1))
                }
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
            PPButton(tr("launch.pick.add", selected.count), kind: .primary, busy: busy, action: submit)
                .keyboardShortcut(.defaultAction)
                .disabled(selected.isEmpty)
        }
        .onAppear {
            if projectId.isEmpty {
                projectId = projects.first { $0.id == AppModel.shared.nav.openProjectId }?.id ?? projects.first?.id ?? ""
            }
        }
        .onChange(of: projectId) { _ in
            selected = []
            list = nil
        }
        .task(id: projectId + (includeSources ? "|src" : "")) { await load() }
    }

    @ViewBuilder private var fileList: some View {
        if loading && list == nil {
            PPSpinner(size: M.icon).frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if groups.isEmpty {
            Text(filter.isEmpty ? tr("launch.pick.empty") : tr("workspace.noMatch"))
                .foregroundColor(theme.textSecondary).frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(groups, id: \.dir) { g in
                        HStack(spacing: M.xs) {
                            PPIcon("folder", size: M.iconSmall, color: theme.iconSecondary)
                            Text(g.dir.isEmpty ? tr("launch.pick.root") : g.dir).font(.pp(.caption)).foregroundColor(theme.textTertiary)
                                .lineLimit(1).truncationMode(.head)
                        }
                        .padding(.horizontal, M.md).padding(.top, M.sm).padding(.bottom, M.xxs)
                        ForEach(g.files) { f in fileRow(f) }
                    }
                }
                .padding(.vertical, M.xs)
            }
        }
    }

    private func fileRow(_ f: RunnableFile) -> some View {
        let on = f.launch || selected.contains(f.path)
        return HStack(spacing: M.sm) {
            PPCheckbox(isOn: Binding(get: { on }, set: { v in
                guard !f.launch else { return }
                if v { selected.insert(f.path) } else { selected.remove(f.path) }
            }))
            .disabled(f.launch)
            FileIconView(entry: FSEntry(name: f.name, path: f.path, dir: false), expanded: false)
            Text(f.name).lineLimit(1).truncationMode(.middle)
            PPBadge(text: tr("launch.pick.kind." + f.kind))
            Spacer(minLength: M.sm)
            if f.launch {
                Text(tr("launch.pick.inLaunch")).font(.pp(.small)).foregroundColor(theme.textTertiary)
            }
        }
        .padding(.horizontal, M.md)
        .padding(.leading, M.sm)
        .frame(height: M.rowHeightCompact)
        .contentShape(Rectangle())
        .onTapGesture {
            guard !f.launch else { return }
            if selected.contains(f.path) { selected.remove(f.path) } else { selected.insert(f.path) }
        }
        .opacity(f.launch ? 0.6 : 1)
    }

    private func load() async {
        guard !projectId.isEmpty, let api = AppModel.shared.api else { return }
        loading = true
        defer { loading = false }
        do {
            let r = try await api.launchCandidates(projectId: projectId, includeSources: includeSources)
            if Task.isCancelled { return }
            list = r
            let paths = Set(r.files.map(\.path))
            selected = selected.filter { paths.contains($0) }
            error = nil
        } catch {
            if !Task.isCancelled { self.error = error.localizedDescription }
        }
    }

    private func close() { AppModel.shared.dialogs.dismiss(id) }

    private func submit() {
        guard !selected.isEmpty, !busy else { return }
        busy = true
        let pid = projectId
        let paths = (list?.files ?? []).map(\.path).filter { selected.contains($0) }
        Task {
            do {
                let api = try AppModel.shared.client()
                for p in paths { _ = try await api.launchAddFile(projectId: pid, path: p) }
                close()
                AppModel.shared.toasts.show("success", tr("launch.pick.added", paths.count))
            } catch {
                self.error = error.localizedDescription
            }
            busy = false
        }
    }
}

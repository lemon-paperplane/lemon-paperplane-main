import PaperplaneKit
import SwiftUI

/// 主工具栏（40px）：侧栏开关、当前位置、搜索、导入、运行中指示、后台任务指示。
struct MainToolbar: View {
    @ObservedObject private var model = AppModel.shared
    @ObservedObject private var nav = AppModel.shared.nav
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme
    @FocusState private var searchFocused: Bool

    var body: some View {
        HStack(spacing: M.xs) {
            Color.clear.frame(width: nav.isFullScreen ? M.sm : TrafficLights.reservedWidth)
            PPIconButton(nav.sidebarVisible ? "panel-left-close" : "panel-left-open", help: tr("toolbar.toggleSidebar")) {
                withAnimation(.easeOut(duration: 0.15)) { nav.sidebarVisible.toggle() }
            }
            Text(title)
                .font(.pp(.title))
                .lineLimit(1)
                .padding(.leading, M.xs)
            if model.connection == .reconnecting {
                HStack(spacing: M.xs) {
                    PPSpinner(size: M.iconSmall, color: theme.warning)
                    Text(tr("connection.reconnecting")).font(.pp(.small)).foregroundColor(theme.warning)
                }
                .padding(.leading, M.md)
            }
            WindowDragArea().frame(maxWidth: .infinity, maxHeight: .infinity)
            if showsSearch { searchField }
            importMenu
            RunsIndicator()
            TasksIndicator()
        }
        .padding(.trailing, M.md)
        .frame(height: M.toolbarHeight)
        .background(WindowDragArea())
        .onReceive(nav.focusSearch) { _ in searchFocused = true }
    }

    private var title: String {
        if nav.openProjectId != nil {
            if case .workspace(let id) = nav.page { return data.workspace(id)?.name ?? "" }
            return tr("sidebar.running")
        }
        switch nav.page {
        case .workspace(let id): return data.workspace(id)?.name ?? ""
        case .running: return tr("sidebar.running")
        case .toolchains: return tr("sidebar.toolchains")
        case .plugins: return tr("sidebar.plugins")
        case .settings: return tr("sidebar.settings")
        case .pluginView(let key): return data.contributions.views.first { $0.key == key }?.title ?? ""
        }
    }

    private var showsSearch: Bool {
        guard nav.openProjectId == nil else { return false }
        switch nav.page {
        case .workspace, .running: return true
        default: return false
        }
    }

    private var searchField: some View {
        InputChrome(focused: searchFocused, height: M.controlHeightSmall + 2) {
            HStack(spacing: M.sm) {
                PPIcon("search", size: M.iconSmall, color: theme.iconSecondary)
                TextField(tr("toolbar.search"), text: $nav.search)
                    .textFieldStyle(.plain)
                    .focused($searchFocused)
                    .onExitCommand { nav.search = ""; searchFocused = false }
                if !nav.search.isEmpty {
                    Button { nav.search = "" } label: { PPIcon("x", size: M.iconSmall, color: theme.iconSecondary) }
                        .buttonStyle(.plain)
                }
            }
        }
        .frame(width: 220)
        .padding(.trailing, M.xs)
    }

    private var importMenu: some View {
        PPMenuButton(alignment: .trailing, entries: {
            var e: [MenuEntry] = [
                .item(tr("import.folder"), icon: "folder-input", shortcut: "⌘N") { ImportFlow.importFolder() },
                .item(tr("import.scan"), icon: "scan-search", shortcut: "⇧⌘N") { ImportFlow.scanFolder() },
            ]
            if let clone = VCSHooks.cloneAction {
                e.append(.divider)
                e.append(.item(tr("import.clone"), icon: "git-branch") { clone() })
            }
            return e
        }) {
            HStack(spacing: M.xs) {
                PPIcon("plus", size: M.icon)
                Text(tr("toolbar.import")).font(.pp(.label))
                PPIcon("chevron-down", size: 12)
            }
            .padding(.horizontal, M.md)
            .frame(height: M.controlHeightSmall + 2)
            .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.accent))
            .foregroundColor(theme.textOnAccent)
        }
        .help(tr("toolbar.import"))
    }
}

/// 运行中数量：点开列出全部运行，可停止、看日志、打开地址
struct RunsIndicator: View {
    @ObservedObject private var runs = AppModel.shared.runs
    @State private var open = false
    @Environment(\.theme) private var theme

    var body: some View {
        let active = runs.activeRuns
        Button { open.toggle() } label: {
            HStack(spacing: M.xs) {
                PPIcon("activity", size: M.icon, color: active.isEmpty ? theme.iconSecondary : theme.statusRunning)
                Text("\(active.count)").font(.pp(.label).monospacedDigit())
            }
            .padding(.horizontal, M.sm)
            .frame(height: M.iconButton)
            .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(open ? theme.bgPressed : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(tr("toolbar.runs"))
        .ppPopover(isPresented: $open, alignment: .trailing) { RunsPopover { open = false } }
    }
}

struct RunsPopover: View {
    var close: () -> Void
    @ObservedObject private var runs = AppModel.shared.runs
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        let active = runs.activeRuns
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(tr("toolbar.runs")).font(.pp(.title))
                Spacer()
                if !active.isEmpty {
                    PPButton(tr("menu.stopAll"), icon: "square", kind: .ghost, size: .small) { Actions.stopAll() }
                }
            }
            .padding(.horizontal, M.md)
            .frame(height: 36)
            PPDivider(subtle: true)
            if active.isEmpty {
                Text(tr("runs.none")).foregroundColor(theme.textSecondary).padding(M.lg)
            } else {
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(active) { r in RunRow(run: r, projectName: data.projects[r.projectId]?.name, close: close) }
                    }
                    .padding(M.xs)
                }
                .frame(maxHeight: 360)
            }
        }
        .frame(width: 380)
    }
}

/// 一个运行：状态、名称、工程、地址、停止/日志
struct RunRow: View {
    let run: Run
    var projectName: String?
    var close: (() -> Void)?
    @Environment(\.theme) private var theme

    var body: some View {
        PPRow(height: nil) {
            HStack(spacing: M.md) {
                StatusDot(state: run.state)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: M.xs) {
                        Text(run.name).font(.pp(.bodyStrong)).lineLimit(1)
                        if let projectName { Text(projectName).foregroundColor(theme.textSecondary).lineLimit(1) }
                    }
                    HStack(spacing: M.md) {
                        if let url = run.urls.first {
                            PPLink(title: Format.shortURL(url), mono: true) { Actions.openURL(url) }
                        } else {
                            Text(RunStatusStyle.title(run.state)).font(.pp(.small)).foregroundColor(theme.textTertiary)
                        }
                        if RunState.isActive(run.state) {
                            RunResourceInline(runId: run.id, startedAt: run.startedAt, sparkWidth: 48)
                        }
                    }
                }
                Spacer(minLength: M.sm)
                PPIconButton("scroll-text", help: tr("runs.openLog"), size: M.iconButtonSmall) {
                    close?()
                    AppDelegate.shared?.showMainWindow()
                    AppModel.shared.nav.openRunConsole(projectId: run.projectId, configId: run.configId)
                }
                PPIconButton("square", help: tr("action.stop"), size: M.iconButtonSmall, tint: theme.danger) {
                    Actions.stop(runId: run.id)
                }
            }
            .padding(.vertical, M.sm)
        }
    }
}

/// 后台任务：转圈 + 数量，点开看进度、取消、看日志
struct TasksIndicator: View {
    @ObservedObject private var runs = AppModel.shared.runs
    @State private var open = false
    @Environment(\.theme) private var theme

    var body: some View {
        let active = runs.activeTasks
        if !runs.recentTasks.isEmpty {
            Button { open.toggle() } label: {
                HStack(spacing: M.xs) {
                    if active.isEmpty {
                        PPIcon("circle-check", size: M.icon, color: theme.iconSecondary)
                    } else {
                        PPSpinner(size: M.icon, color: theme.accent)
                        Text("\(active.count)").font(.pp(.label).monospacedDigit())
                    }
                }
                .padding(.horizontal, M.sm)
                .frame(height: M.iconButton)
                .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(open ? theme.bgPressed : .clear))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(tr("toolbar.tasks"))
            .ppPopover(isPresented: $open, alignment: .trailing) { TasksPopover { open = false } }
        }
    }
}

struct TasksPopover: View {
    var close: () -> Void
    @ObservedObject private var runs = AppModel.shared.runs
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(tr("toolbar.tasks")).font(.pp(.title)).padding(.horizontal, M.md).frame(height: 36)
            PPDivider(subtle: true)
            ScrollView {
                VStack(spacing: M.xs) {
                    ForEach(runs.recentTasks) { t in TaskRow(task: t, close: close) }
                }
                .padding(M.xs)
            }
            .frame(maxHeight: 400)
        }
        .frame(width: 380)
    }
}

struct TaskRow: View {
    let task: TaskInfo
    var close: (() -> Void)?
    @Environment(\.theme) private var theme

    var body: some View {
        PPRow(height: nil) {
            VStack(alignment: .leading, spacing: M.xs) {
                HStack(spacing: M.sm) {
                    icon
                    Text(task.title).font(.pp(.bodyStrong)).lineLimit(1)
                    Spacer(minLength: M.sm)
                    PPIconButton("scroll-text", help: tr("task.viewLog"), size: M.iconButtonSmall) {
                        close?()
                        Actions.showTaskLog(task.id)
                    }
                    if task.isRunning {
                        PPIconButton("x", help: tr("common.cancel"), size: M.iconButtonSmall) { Actions.cancelTask(task.id) }
                    }
                }
                if task.isRunning {
                    PPProgressBar(value: task.percent)
                }
                if let m = task.isRunning ? task.message : (task.error ?? task.message), !m.isEmpty {
                    Text(m).font(.pp(.small)).foregroundColor(task.state == TaskState.failed ? theme.danger : theme.textSecondary)
                        .lineLimit(2)
                }
            }
            .padding(.vertical, M.sm)
        }
    }

    @ViewBuilder private var icon: some View {
        switch task.state {
        case TaskState.running: PPSpinner(size: M.iconSmall, color: theme.accent)
        case TaskState.succeeded: PPIcon("circle-check", size: M.iconSmall, color: theme.success)
        case TaskState.failed: PPIcon("circle-x", size: M.iconSmall, color: theme.danger)
        default: PPIcon("circle-minus", size: M.iconSmall, color: theme.iconSecondary)
        }
    }
}

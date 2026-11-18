import PaperplaneKit
import SwiftUI
import UniformTypeIdentifiers

/// 左侧栏：工作区（可拖动排序、右键重命名/删除）、运行中、工具链、插件、设置、插件 sidebar 视图。
struct Sidebar: View {
    @ObservedObject private var data = AppModel.shared.data
    @ObservedObject private var runs = AppModel.shared.runs
    @ObservedObject private var nav = AppModel.shared.nav
    @Environment(\.theme) private var theme
    @State private var dragging: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 1) {
                HStack {
                    Text(tr("sidebar.workspaces")).font(.pp(.caption)).foregroundColor(theme.textTertiary)
                    Spacer()
                    PPIconButton("plus", help: tr("workspace.new"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                        Actions.createWorkspace()
                    }
                }
                .padding(.leading, M.md)
                .padding(.bottom, M.xxs)

                ForEach(data.workspaces) { ws in
                    workspaceRow(ws)
                        .onDrag {
                            dragging = ws.id
                            return NSItemProvider(object: ws.id as NSString)
                        }
                        .onDrop(of: [UTType.text], delegate: ReorderDropDelegate(
                            target: ws.id, dragging: $dragging,
                            ids: { data.workspaces.map(\.id) },
                            move: { from, to in
                                guard let i = data.workspaces.firstIndex(where: { $0.id == from }),
                                      let j = data.workspaces.firstIndex(where: { $0.id == to }) else { return }
                                let item = data.workspaces.remove(at: i)
                                data.workspaces.insert(item, at: j)
                            },
                            commit: { ids in AppModel.shared.perform { api in try await api.reorderWorkspaces(ids) } }))
                }

                SidebarItem(icon: "activity", title: tr("sidebar.running"), selected: nav.page == .running && nav.openProjectId == nil,
                            count: runs.activeRuns.count, countTone: runs.activeRuns.isEmpty ? nil : "success") {
                    nav.page = .running
                }
                .padding(.top, M.md)

                PPDivider(subtle: true).padding(.vertical, M.md).padding(.horizontal, M.md)

                SidebarItem(icon: "boxes", title: tr("sidebar.toolchains"), selected: nav.page == .toolchains) { nav.page = .toolchains }
                SidebarItem(icon: "puzzle", title: tr("sidebar.plugins"), selected: nav.page == .plugins) { nav.page = .plugins }
                SidebarItem(icon: "settings", title: tr("sidebar.settings"), selected: nav.page == .settings) { nav.page = .settings }

                let views = data.contributions.views(at: ViewLocation.sidebar)
                if !views.isEmpty {
                    PPDivider(subtle: true).padding(.vertical, M.md).padding(.horizontal, M.md)
                    ForEach(views) { v in
                        SidebarItem(iconRef: IconSource.parse(v.icon, pluginId: v.plugin) ?? .builtin("puzzle"), title: v.title,
                                    selected: nav.page == .pluginView(v.key)) { nav.page = .pluginView(v.key) }
                    }
                }
            }
            .padding(.horizontal, M.sm)
            .padding(.vertical, M.sm)
        }
        .background(theme.bgChrome)
    }

    private func workspaceRow(_ ws: Workspace) -> some View {
        let projects = data.projects(in: ws.id)
        let running = projects.filter(\.hasActiveRun).count
        return SidebarItem(icon: "layers", title: ws.name, selected: nav.page == .workspace(ws.id) && nav.openProjectId == nil,
                           count: projects.count, runningDot: running > 0) {
            nav.page = .workspace(ws.id)
        }
        .ppContextMenu {
            [
                .item(tr("workspace.rename"), icon: "pencil") { Actions.renameWorkspace(ws) },
                .item(tr("menu.stopAll"), icon: "square", disabled: running == 0) {
                    AppModel.shared.perform { api in _ = try await api.stopAll(workspaceId: ws.id) }
                },
                .divider,
                .item(tr("workspace.delete"), icon: "trash-2", destructive: true) { Actions.deleteWorkspace(ws) },
            ]
        }
    }
}

struct SidebarItem: View {
    let iconRef: IconSource
    let title: String
    var selected = false
    var count: Int?
    var countTone: String?
    var runningDot = false
    let action: () -> Void
    @Environment(\.theme) private var theme
    @State private var hover = false

    init(icon: String, title: String, selected: Bool = false, count: Int? = nil, countTone: String? = nil,
         runningDot: Bool = false, action: @escaping () -> Void) {
        self.iconRef = .builtin(icon)
        self.title = title
        self.selected = selected
        self.count = count
        self.countTone = countTone
        self.runningDot = runningDot
        self.action = action
    }

    init(iconRef: IconSource, title: String, selected: Bool = false, action: @escaping () -> Void) {
        self.iconRef = iconRef
        self.title = title
        self.selected = selected
        self.action = action
    }

    var body: some View {
        HStack(spacing: M.md) {
            PPIcon(ref: iconRef, size: M.icon, color: selected ? theme.accent : theme.iconPrimary)
            Text(title).lineLimit(1).truncationMode(.tail)
                .font(.pp(selected ? .bodyStrong : .body))
            Spacer(minLength: M.xs)
            if runningDot {
                Circle().fill(theme.statusRunning).frame(width: 6, height: 6)
            }
            if let count {
                Text("\(count)").font(.pp(.small).monospacedDigit())
                    .foregroundColor(countTone == nil ? theme.textTertiary : theme.tone(countTone).fg)
            }
        }
        .padding(.horizontal, M.md)
        .frame(height: M.rowHeight)
        .background(RoundedRectangle(cornerRadius: M.radiusMD)
            .fill(selected ? theme.bgSelected : (hover ? theme.bgHover : .clear)))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture(perform: action)
    }
}

/// 拖动排序：拖进目标时立即移动（实时预览），放下时提交新顺序。
struct ReorderDropDelegate: DropDelegate {
    let target: String
    @Binding var dragging: String?
    let ids: () -> [String]
    let move: (String, String) -> Void
    let commit: ([String]) -> Void

    func dropEntered(info: DropInfo) {
        guard let d = dragging, d != target else { return }
        withAnimation(.easeOut(duration: 0.12)) { move(d, target) }
    }

    func dropUpdated(info: DropInfo) -> DropProposal? { DropProposal(operation: .move) }

    func performDrop(info: DropInfo) -> Bool {
        guard dragging != nil else { return false }
        dragging = nil
        commit(ids())
        return true
    }
}

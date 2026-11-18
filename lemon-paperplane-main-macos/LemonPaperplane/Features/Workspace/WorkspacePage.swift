import PaperplaneKit
import SwiftUI
import UniformTypeIdentifiers

/// 工作区页：工程卡片网格（自适应列数，可拖动排序）。workspaceId 为 nil 时是「运行中」聚合页。
struct WorkspacePage: View {
    let workspaceId: String?
    @ObservedObject private var data = AppModel.shared.data
    @ObservedObject private var nav = AppModel.shared.nav
    @ObservedObject private var runs = AppModel.shared.runs
    @State private var dragging: String?
    @State private var order: [String] = []
    /// 「运行中」页：按内存排序（每 10 秒按最新数值重排一次，避免网格频繁跳动）
    @AppStorage("PaperplaneRunningSortByMemory") private var sortByMemory = false
    @State private var sortTick = 0
    @Environment(\.theme) private var theme

    var body: some View {
        if let workspaceId {
            // 顶部「工程 | 版本控制」切换（版本控制视图在 Features/VCS/VCSOverviewView.swift）
            VCSWorkspaceSwitcher(workspaceId: workspaceId) { grid }
        } else {
            VStack(spacing: 0) {
                RunningSummaryBar(sortByMemory: $sortByMemory)
                PPDivider(subtle: true)
                grid
            }
            .onReceive(Timer.publish(every: 10, on: .main, in: .common).autoconnect()) { _ in
                if sortByMemory { sortTick &+= 1 }
            }
        }
    }

    private var grid: some View {
        let list = projects
        return Group {
            if list.isEmpty {
                if !nav.search.isEmpty {
                    PPEmptyState(icon: "search", title: tr("workspace.noMatch"))
                } else if workspaceId == nil {
                    PPEmptyState(icon: "activity", title: tr("running.empty"))
                } else {
                    PPEmptyState(icon: "folder-open", title: tr("workspace.empty"), actionTitle: tr("toolbar.import"),
                                 actionIcon: "folder-input") { ImportFlow.importFolder() }
                }
            } else {
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: M.cardMinWidth, maximum: M.cardMaxWidth), spacing: M.lg)],
                              alignment: .leading, spacing: M.lg) {
                        ForEach(list) { p in
                            card(p)
                        }
                    }
                    .padding(M.xl)
                }
            }
        }
    }

    @ViewBuilder private func card(_ p: ProjectSummary) -> some View {
        if let wsId = workspaceId, nav.search.isEmpty {
            ProjectCard(project: p)
                .onDrag {
                    dragging = p.id
                    return NSItemProvider(object: p.id as NSString)
                }
                .onDrop(of: [UTType.text], delegate: ReorderDropDelegate(
                    target: p.id, dragging: $dragging,
                    ids: { data.workspace(wsId)?.projectIds ?? [] },
                    move: { from, to in
                        guard let i = data.workspaces.firstIndex(where: { $0.id == wsId }) else { return }
                        var ids = data.workspaces[i].projectIds
                        guard let a = ids.firstIndex(of: from), let b = ids.firstIndex(of: to) else { return }
                        ids.insert(ids.remove(at: a), at: b)
                        data.workspaces[i].projectIds = ids
                    },
                    commit: { ids in
                        AppModel.shared.perform { api in try await api.reorderProjects(workspaceId: wsId, projectIds: ids) }
                    }))
        } else {
            ProjectCard(project: p, showWorkspace: workspaceId == nil)
        }
    }

    private var projects: [ProjectSummary] {
        var list: [ProjectSummary]
        if let workspaceId {
            list = data.projects(in: workspaceId)
        } else {
            let activeIds = Set(runs.activeRuns.map(\.projectId))
            list = data.allProjects.filter { $0.hasActiveRun || activeIds.contains($0.id) || $0.status == RunState.failed && !$0.runs.isEmpty }
            if sortByMemory {
                _ = sortTick
                let mem = Dictionary(list.map { ($0.id, ResourceStore.shared.memory(projectId: $0.id)) }, uniquingKeysWith: { a, _ in a })
                list.sort { (mem[$0.id] ?? 0) > (mem[$1.id] ?? 0) }
            }
        }
        let q = nav.search.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return list }
        return list.filter { p in
            p.name.localizedCaseInsensitiveContains(q) || p.path.localizedCaseInsensitiveContains(q)
                || p.facets.contains { $0.name.localizedCaseInsensitiveContains(q) }
        }
    }
}

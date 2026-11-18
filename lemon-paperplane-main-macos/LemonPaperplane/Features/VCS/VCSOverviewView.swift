import AppKit
import PaperplaneKit
import SwiftUI

// 工作区「版本控制」视图：顶部统计 / 过滤 / 批量操作，下面每个仓库一段（可折叠），段内提交信息、提交、改动文件。

/// 工作区页顶部「工程 | 版本控制」切换（按工作区记住）
struct VCSWorkspaceSwitcher<Projects: View>: View {
    let workspaceId: String
    @ViewBuilder let projects: () -> Projects
    @State private var mode: String

    init(workspaceId: String, @ViewBuilder projects: @escaping () -> Projects) {
        self.workspaceId = workspaceId
        self.projects = projects
        self._mode = State(initialValue: UserDefaults.standard.string(forKey: "PaperplaneWorkspaceMode:" + workspaceId) ?? "projects")
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                VCSWorkspaceModePicker(workspaceId: workspaceId, mode: Binding(get: { mode }, set: { v in
                    mode = v
                    UserDefaults.standard.set(v, forKey: "PaperplaneWorkspaceMode:" + workspaceId)
                }))
                Spacer()
            }
            .padding(.horizontal, M.xl)
            .padding(.top, M.lg)
            .padding(.bottom, mode == "vcs" ? M.sm : 0)
            if mode == "vcs" {
                VCSOverviewPage(workspaceId: workspaceId)
            } else if mode == "launch" {
                LaunchBoardPage(workspaceId: workspaceId)
            } else {
                projects()
            }
        }
    }
}

/// 分段控件：工程 | 启动（带运行中的启动项数）| 版本控制（带有改动的仓库数）
private struct VCSWorkspaceModePicker: View {
    let workspaceId: String
    @Binding var mode: String
    @ObservedObject private var model: VCSOverviewModel
    @ObservedObject private var data = AppModel.shared.data
    @ObservedObject private var runs = AppModel.shared.runs
    @Environment(\.theme) private var theme

    init(workspaceId: String, mode: Binding<String>) {
        self.workspaceId = workspaceId
        self._mode = mode
        self._model = ObservedObject(wrappedValue: VCSOverviewModel.of(workspaceId))
    }

    /// 总览取过就用它（含嵌套仓库），否则用工程卡片的 Git 摘要（主仓库）
    private var changedCount: Int {
        if model.loaded { return model.stats.changed }
        return data.projects(in: workspaceId).filter { p in
            guard let v = p.vcs else { return false }
            return v.changes > 0 || v.conflicts > 0 || (v.operation?.isEmpty == false)
        }.count
    }

    var body: some View {
        HStack(spacing: 2) {
            segment("projects", tr("vcs.overview.modeProjects"), icon: "layout-grid", count: 0)
            segment("launch", tr("launch.mode"), icon: "rocket",
                    count: LaunchGroupData.activeCount(workspaceId: workspaceId, data: data, runs: runs), tone: "success")
            segment("vcs", tr("vcs.overview.modeVCS"), icon: "git-branch", count: changedCount)
        }
        .padding(2)
        .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.neutralSubtle))
    }

    private func segment(_ value: String, _ title: String, icon: String, count: Int, tone: String = "info") -> some View {
        let on = mode == value
        return Button { mode = value } label: {
            HStack(spacing: M.sm) {
                PPIcon(icon, size: M.iconSmall)
                Text(title).font(.pp(.label))
                if count > 0 { PPCountBadge(count: count, tone: tone) }
            }
            .foregroundColor(on ? theme.textPrimary : theme.textSecondary)
            .padding(.horizontal, M.lg)
            .frame(height: M.controlHeight - 4)
            .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(on ? theme.bgSurface : .clear)
                .shadow(color: on ? .black.opacity(theme.isDark ? 0.3 : 0.08) : .clear, radius: 1, y: 0.5))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

// MARK: - 页面

struct VCSOverviewPage: View {
    let workspaceId: String
    @ObservedObject private var model: VCSOverviewModel
    @ObservedObject private var nav = AppModel.shared.nav
    @Environment(\.theme) private var theme

    init(workspaceId: String) {
        self.workspaceId = workspaceId
        self._model = ObservedObject(wrappedValue: VCSOverviewModel.of(workspaceId))
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            if let b = model.batch {
                VCSBatchPanel(model: model, batch: b).padding(.horizontal, M.xl).padding(.bottom, M.sm)
            }
            PPDivider(subtle: true)
            content
        }
        .onAppear {
            model.visible = true
            Task { await model.reload() }
        }
        .onDisappear { model.visible = false }
    }

    // MARK: 工具栏

    private var toolbar: some View {
        let s = model.stats
        let running = model.batch?.running == true
        return VStack(spacing: M.sm) {
            HStack(spacing: M.md) {
                Text(tr("vcs.overview.stats", s.repos, s.changed)).font(.pp(.bodyStrong))
                if s.toPush > 0 {
                    HStack(spacing: 2) { PPIcon("arrow-up", size: 12); Text(tr("vcs.overview.toPush", s.toPush)) }
                        .font(.pp(.small)).foregroundColor(theme.success)
                }
                if s.toPull > 0 {
                    HStack(spacing: 2) { PPIcon("arrow-down", size: 12); Text(tr("vcs.overview.toPull", s.toPull)) }
                        .font(.pp(.small)).foregroundColor(theme.info)
                }
                if model.loading { PPSpinner(size: M.iconSmall) }
                Spacer(minLength: M.md)
                PPButton(tr("vcs.overview.fetchAll"), icon: "cloud-download", size: .small) { model.startBatch(.fetch) }
                    .disabled(running || model.entries.isEmpty)
                PPButton(tr("vcs.overview.pullAll"), icon: "arrow-down-to-line", size: .small) { model.startBatch(.pull) }
                    .disabled(running || model.entries.isEmpty)
                PPButton(tr("vcs.overview.pushAll"), icon: "arrow-up", size: .small) { model.startBatch(.push) }
                    .disabled(running || model.entries.isEmpty)
                PPInfoButton(text: tr("vcs.overview.batchInfo"))
                PPIconButton("refresh-cw", help: tr("vcs.refresh"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    Task { await model.reload() }
                }
            }
            HStack(spacing: M.md) {
                PPTextField(tr("vcs.overview.search"), text: $model.query, icon: "search", clearable: true, height: M.controlHeightSmall)
                    .frame(maxWidth: 260)
                PPCheckbox(isOn: $model.onlyChanged, label: tr("vcs.overview.onlyChanged")).font(.pp(.small))
                Spacer()
                PPIconButton("chevrons-up-down", help: tr("vcs.changes.expandAll"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    model.expanded = Set(model.entries.map(\.root))
                }
                PPIconButton("chevrons-down-up", help: tr("vcs.changes.collapseAll"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    model.expanded = []
                }
            }
        }
        .padding(.horizontal, M.xl)
        .padding(.vertical, M.sm)
    }

    // MARK: 内容

    @ViewBuilder private var content: some View {
        if !model.loaded {
            PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if let e = model.error, model.entries.isEmpty {
            PPAlert(tone: "danger", title: tr("vcs.overview.failed"), message: e) {
                PPButton(tr("connection.retry"), size: .small) { Task { await model.reload() } }
            }
            .padding(M.xl)
            .frame(maxHeight: .infinity, alignment: .top)
        } else if model.entries.isEmpty {
            PPEmptyState(icon: "folder-git", title: tr("vcs.overview.empty"))
        } else {
            let list = model.filtered(globalSearch: nav.search)
            if list.isEmpty {
                PPEmptyState(icon: "circle-check", title: model.onlyChanged ? tr("vcs.overview.allClean") : tr("vcs.overview.noMatch"))
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: M.md) {
                        ForEach(list) { e in
                            VCSOverviewSection(entry: e, repo: model.repo(e), expanded: model.expanded.contains(e.root),
                                               showAll: model.showAllFiles.contains(e.root),
                                               toggle: { model.toggle(e.root) },
                                               showAllFiles: { model.showAllFiles.insert(e.root) })
                        }
                    }
                    .padding(.horizontal, M.xl)
                    .padding(.vertical, M.lg)
                }
            }
        }
    }
}

// MARK: - 批量进度

private struct VCSBatchPanel: View {
    @ObservedObject var model: VCSOverviewModel
    let batch: VCSBatch
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: M.sm) {
            HStack(spacing: M.sm) {
                if batch.running { PPSpinner(size: M.iconSmall) }
                Text(batch.kind.title).font(.pp(.bodyStrong))
                Text("\(batch.done)/\(batch.items.count)").font(.pp(.monoSmall)).foregroundColor(theme.textSecondary)
                PPProgressBar(value: batch.items.isEmpty ? 0 : Double(batch.done) * 100 / Double(batch.items.count))
                    .frame(maxWidth: 220)
                Spacer()
                if !batch.running {
                    PPIconButton("x", help: tr("common.close"), size: M.iconButtonSmall, iconSize: M.iconSmall) { model.batch = nil }
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                ForEach(batch.items) { item in
                    HStack(spacing: M.sm) {
                        stateIcon(item.state)
                        Text(item.name).lineLimit(1)
                        if let m = item.message, !m.isEmpty {
                            Text(m).font(.pp(.small)).foregroundColor(item.state == .failed ? theme.danger : theme.textTertiary)
                                .lineLimit(1).truncationMode(.tail)
                        }
                        Spacer(minLength: 0)
                    }
                    .frame(height: 20)
                    .contentShape(Rectangle())
                    .onTapGesture { model.expanded.insert(item.root) }
                }
            }
        }
        .padding(M.md)
        .background(RoundedRectangle(cornerRadius: M.radiusLG).fill(theme.bgSubtle))
        .overlay(RoundedRectangle(cornerRadius: M.radiusLG).strokeBorder(theme.border, lineWidth: 1))
    }

    @ViewBuilder private func stateIcon(_ s: VCSBatch.State) -> some View {
        switch s {
        case .pending: PPIcon("circle", size: M.iconSmall, color: theme.iconSecondary)
        case .running: PPSpinner(size: M.iconSmall)
        case .ok: PPIcon("circle-check", size: M.iconSmall, color: theme.success)
        case .failed: PPIcon("circle-x", size: M.iconSmall, color: theme.danger)
        case .skipped: PPIcon("circle-minus", size: M.iconSmall, color: theme.iconSecondary)
        }
    }
}

// MARK: - 一个仓库

private struct VCSOverviewSection: View {
    let entry: VCSOverviewModel.Entry
    @ObservedObject var repo: VCSRepo
    let expanded: Bool
    let showAll: Bool
    let toggle: () -> Void
    let showAllFiles: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VCSOverviewHeader(entry: entry, repo: repo, expanded: expanded, toggle: toggle)
            if let n = repo.notice {
                VCSNoticeBar(repo: repo, notice: n).padding(.horizontal, M.md).padding(.bottom, M.sm)
            }
            if expanded {
                PPDivider(subtle: true)
                if let st = repo.status {
                    VCSOverviewBody(repo: repo, changes: repo.changes, draft: repo.changes.draft, status: st,
                                    showAll: showAll, showAllFiles: showAllFiles)
                } else if let e = entry.error {
                    PPAlert(tone: "danger", title: tr("vcs.status.failed"), message: e.isEmpty ? nil : e).padding(M.md)
                } else {
                    PPSpinner(size: M.iconSmall).frame(maxWidth: .infinity).padding(M.md)
                }
            }
        }
        .background(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous).fill(theme.bgSurface))
        .overlay(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous).strokeBorder(theme.border, lineWidth: 1))
    }
}

/// 段头：仓库名、分支、领先落后、进行中操作、冲突与改动数、获取 / 拉取 / 推送 / 刷新 / 更多
private struct VCSOverviewHeader: View {
    let entry: VCSOverviewModel.Entry
    @ObservedObject var repo: VCSRepo
    let expanded: Bool
    let toggle: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        let s = repo.status
        let busy = repo.activity != nil
        HStack(spacing: M.sm) {
            PPIcon(expanded ? "chevron-down" : "chevron-right", size: 12, color: theme.iconSecondary)
            PPIcon("folder-git", size: M.icon, color: theme.iconSecondary)
            Text(entry.name).font(.pp(.bodyStrong)).lineLimit(1)
            if let sub = entry.subtitle {
                Text(sub).font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.middle)
            }
            if s != nil {
                VCSBranchButton(repo: repo, showCounts: false, compact: true)
            }
            if let s {
                if s.ahead > 0 {
                    HStack(spacing: 2) { PPIcon("arrow-up", size: 12); Text("\(s.ahead)").font(.pp(.monoSmall)) }
                        .foregroundColor(theme.success).help(tr("vcs.overview.aheadHelp", s.ahead))
                }
                if s.behind > 0 {
                    HStack(spacing: 2) { PPIcon("arrow-down", size: 12); Text("\(s.behind)").font(.pp(.monoSmall)) }
                        .foregroundColor(theme.info).help(tr("vcs.overview.behindHelp", s.behind))
                }
                if s.upstreamGone {
                    PPIcon("triangle-alert", size: 12, color: theme.warning).help(tr("vcs.branch.upstreamGone"))
                } else if s.upstream == nil && !s.detached && !s.unborn {
                    PPIcon("cloud-off", size: 12, color: theme.textTertiary).help(tr("vcs.overview.noUpstream"))
                }
                if let op = s.operation {
                    PPBadge(text: tr("vcs.overview.inProgress", VCSStyle.operationTitle(op)), tone: "warning")
                }
                if s.conflicts > 0 {
                    PPBadge(text: tr("vcs.overview.conflicts", s.conflicts), tone: "danger")
                }
                let changed = s.files.filter { !$0.conflicted }.count
                if changed > 0 {
                    PPBadge(text: tr("vcs.overview.changes", changed), tone: "info")
                }
            } else if let e = entry.error {
                PPIcon("circle-alert", size: M.iconSmall, color: theme.danger).help(e)
            }
            Spacer(minLength: M.md)
            if let a = repo.activity {
                PPSpinner(size: M.iconSmall)
                Text(a.title).font(.pp(.small)).foregroundColor(theme.textSecondary).lineLimit(1)
                if repo.runningTask != nil {
                    PPLink(title: tr("vcs.output")) { repo.showOutput(taskId: nil) }
                }
            }
            PPIconButton("cloud-download", help: tr("vcs.fetch"), size: M.iconButtonSmall, iconSize: M.iconSmall,
                         busy: repo.activity?.op == VCSOp.fetch) {
                VCSRemoteActions.fetch(repo: repo)
            }
            .disabled(busy || s == nil)
            PPIconButton("arrow-down-to-line", help: tr("vcs.pull"), size: M.iconButtonSmall, iconSize: M.iconSmall,
                         busy: repo.activity?.op == VCSOp.pull) {
                VCSOverviewActions.pull(repo)
            }
            .disabled(busy || s == nil)
            PPIconButton("arrow-up", help: tr("vcs.push"), size: M.iconButtonSmall, iconSize: M.iconSmall,
                         busy: repo.activity?.op == VCSOp.push) {
                VCSOverviewActions.push(repo)
            }
            .disabled(busy || s == nil)
            PPIconButton("refresh-cw", help: tr("vcs.refresh"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                Task { await repo.loadStatus() }
            }
            PPMenuButton(alignment: .trailing, entries: menu) {
                PPIcon("ellipsis", size: M.iconSmall, color: theme.iconPrimary)
                    .frame(width: M.iconButtonSmall, height: M.iconButtonSmall)
                    .help(tr("common.more"))
            }
        }
        .padding(.horizontal, M.md)
        .frame(height: 40)
        .contentShape(Rectangle())
        .onTapGesture(perform: toggle)
    }

    private func menu() -> [MenuEntry] {
        let files = (repo.status?.files ?? []).filter { !$0.conflicted }
        var e: [MenuEntry] = [
            .item(tr("vcs.overview.openInProject"), icon: "git-branch") {
                VCSSession.of(entry.projectId).reveal(root: entry.root, page: .changes)
            },
            .item(tr("vcs.revealInFinder"), icon: "folder-open") { Actions.revealInFinder(entry.root) },
            .item(tr("vcs.overview.openInTerminal"), icon: "square-terminal") { VCSOverviewActions.openInTerminal(entry.root) },
            .item(tr("vcs.copyRepoPath"), icon: "copy") { Actions.copy(entry.root) },
            .divider,
            .item(tr("vcs.pull.ellipsis"), icon: "arrow-down-to-line") { VCSPullDialog.present(repo: repo) },
            .item(tr("vcs.push.ellipsis"), icon: "arrow-up") { VCSPushDialog.present(repo: repo) },
            .item(tr("vcs.branch.new"), icon: "git-branch") { VCSBranchActions.newBranch(repo: repo, start: nil, startTitle: nil) },
            .item(tr("vcs.stash.new"), icon: "archive", disabled: files.isEmpty) { VCSStashDialogs.presentNew(repo: repo, paths: nil) },
            .divider,
            .item(tr("vcs.overview.discardAll"), icon: "undo-2", destructive: true, disabled: files.isEmpty) {
                VCSDiscardDialog.present(repo: repo, files: files, mode: nil)
            },
        ]
        let plugin = VCSPluginActions.entries(ActionLocation.vcsToolbar, repo: repo, args: [:])
        if !plugin.isEmpty {
            e.append(.divider)
            e += plugin
        }
        return e
    }
}

/// 展开后：提交信息、提交按钮、改动文件（冲突 / 改动 / 未跟踪）
private struct VCSOverviewBody: View {
    let repo: VCSRepo
    @ObservedObject var changes: VCSChangesModel
    @ObservedObject var draft: VCSCommitDraft
    let status: VCSStatus
    let showAll: Bool
    let showAllFiles: () -> Void
    @FocusState private var focused: Bool
    @Environment(\.theme) private var theme

    private static let fileLimit = 200

    var body: some View {
        let g = changes.groups(status.files)
        let plan = changes.commitPlan(status.files)
        let selected = plan.paths.count + plan.hunks.count
        VStack(alignment: .leading, spacing: M.sm) {
            if status.files.isEmpty {
                HStack(spacing: M.sm) {
                    PPIcon("circle-check", size: M.iconSmall, color: theme.success)
                    Text(tr("vcs.changes.clean")).foregroundColor(theme.textSecondary)
                }
                .padding(.vertical, M.xs)
            } else {
                VCSMessageEditor(text: $draft.message, placeholder: tr("vcs.commit.placeholder"), focused: $focused)
                    .frame(height: 60)
                HStack(spacing: M.sm) {
                    PPButton(tr("vcs.commit"), kind: .primary, size: .small, busy: draft.committing) { commit(push: false) }
                        .disabled(!canCommit(selected))
                        .help(tr("vcs.commit") + "  ⌘↩")
                    PPButton(tr("vcs.commitAndPush"), size: .small) { commit(push: true) }
                        .disabled(!canCommit(selected) || draft.committing)
                    Text(tr("vcs.commit.selectedCount", selected)).font(.pp(.small)).foregroundColor(theme.textTertiary)
                    Spacer()
                }
                files(g)
            }
        }
        .padding(M.md)
        .background(
            // ⌘↩ 只给正在编辑信息的这一段
            Group {
                if focused {
                    Button("") { if canCommit(selected) { commit(push: false) } }
                        .keyboardShortcut(.return, modifiers: .command)
                        .frame(width: 0, height: 0).opacity(0).accessibilityHidden(true)
                }
            }
        )
    }

    private func canCommit(_ selected: Int) -> Bool {
        guard !draft.committing, status.conflicts == 0 else { return false }
        let hasMessage = !draft.message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        let merging = status.operation == "merge"
        return (hasMessage || merging) && (selected > 0 || merging)
    }

    private func commit(push: Bool) {
        repo.commit(andPush: push, byPaths: true, push: push ? { VCSOverviewActions.push(repo) } : nil)
    }

    @ViewBuilder private func files(_ g: VCSChangesModel.Groups) -> some View {
        let limit = showAll ? Int.max : Self.fileLimit
        let total = g.conflicts.count + g.tracked.count + g.untracked.count
        VStack(alignment: .leading, spacing: 0) {
            group(tr("vcs.changes.conflicts"), g.conflicts, side: .conflict, checkable: false, budget: limit)
            group(tr("vcs.changes.changes"), g.tracked, side: .head, checkable: true, budget: max(0, limit - g.conflicts.count))
            group(tr("vcs.changes.untracked"), g.untracked, side: .untracked, checkable: true,
                  budget: max(0, limit - g.conflicts.count - g.tracked.count))
            if status.untrackedTruncated {
                HStack(spacing: M.sm) {
                    PPIcon("triangle-alert", size: M.iconSmall, color: theme.warning)
                    Text(tr("vcs.changes.untrackedTruncated", status.untrackedCount)).font(.pp(.small)).foregroundColor(theme.textSecondary)
                    PPInfoButton(text: tr("vcs.changes.untrackedTruncated.info"))
                }
                .padding(.leading, M.sm)
                .frame(height: M.rowHeightCompact)
            }
            if total > limit {
                PPButton(tr("vcs.overview.showAllFiles", total), kind: .link, action: showAllFiles)
                    .padding(.leading, M.sm)
                    .padding(.top, M.xs)
            }
        }
    }

    @ViewBuilder
    private func group(_ title: String, _ files: [VCSFileChange], side: VCSChangeSide, checkable: Bool, budget: Int) -> some View {
        if !files.isEmpty {
            VCSGroupHeader(title: title, count: files.count) {
                if checkable { VCSTriCheckbox(state: changes.groupState(files)) { changes.toggleGroup(files) } }
            }
            ForEach(files.prefix(max(budget, 0))) { f in
                VCSOverviewFileRow(repo: repo, changes: changes, file: f, side: side, checkable: checkable)
            }
        }
    }
}

private struct VCSOverviewFileRow: View {
    let repo: VCSRepo
    @ObservedObject var changes: VCSChangesModel
    let file: VCSFileChange
    let side: VCSChangeSide
    let checkable: Bool
    @State private var hover = false
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.sm) {
            if checkable {
                VCSTriCheckbox(state: changes.isIncluded(file)) { changes.setIncluded(file, changes.isIncluded(file) != true) }
            } else {
                Color.clear.frame(width: 14, height: 14)
            }
            VCSStatusLetter(status: file.status)
            Text(VCSPath.name(file.path))
                .foregroundColor(VCSStyle.color(file.status, theme: theme))
                .strikethrough(file.status == "D", color: theme.textTertiary)
                .lineLimit(1)
            if let old = file.oldPath, old != file.path {
                Text("← " + old).font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.middle)
            }
            Text(VCSPath.dir(file.path)).font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.middle)
            if side == .conflict {
                Text(VCSStyle.conflictTitle(file.conflictType)).font(.pp(.caption)).foregroundColor(theme.danger).lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, M.sm)
        .frame(height: M.rowHeightCompact)
        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(hover ? theme.bgHover : .clear))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture { VCSDiffDialog.presentWorking(repo: repo, file: file, side: side) }
        .help(file.path)
        .ppContextMenu { VCSFileMenu.entries(repo: repo, scope: [VCSChangeSelection(path: file.path, side: side)]) }
    }
}

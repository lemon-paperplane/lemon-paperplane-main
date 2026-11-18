import PaperplaneKit
import SwiftUI

/// 工程详情「版本控制」标签页：仓库切换、工具栏（分支、获取、拉取、推送、插件动作）、进行中操作横幅、
/// 子页（提交、日志、分支、储藏、冲突、插件 vcs.tab）。键盘：⌘K 提交、⇧⌘K 推送、⌘T 拉取。
struct VCSTabView: View {
    let projectId: String
    @ObservedObject private var session: VCSSession

    init(projectId: String) {
        self.projectId = projectId
        self._session = ObservedObject(wrappedValue: VCSSession.of(projectId))
    }

    var body: some View {
        Group {
            if !session.rootsLoaded {
                PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if session.roots.isEmpty {
                VStack(spacing: M.md) {
                    PPEmptyState(icon: "folder-git", title: tr("vcs.notRepo"), actionTitle: tr("vcs.init"), actionIcon: "plus") {
                        session.initRepository()
                    }
                    if let e = session.rootsError {
                        PPAlert(tone: "danger", title: e).padding(M.xl)
                    }
                }
            } else if let repo = session.repo {
                VCSRepoView(session: session, repo: repo).id(repo.root)
            }
        }
        .task(id: projectId) {
            if !session.rootsLoaded || session.roots.isEmpty { await session.loadRoots() }
        }
        .onAppear { session.visible = true }
        .onDisappear { session.visible = false }
    }
}

private struct VCSRepoView: View {
    @ObservedObject var session: VCSSession
    @ObservedObject var repo: VCSRepo
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    private var pluginViews: [ViewInfo] { data.contributions.views(at: ViewLocation.vcsTab) }

    var body: some View {
        VStack(spacing: 0) {
            VCSToolbar(session: session, repo: repo)
            PPDivider(subtle: true)
            if let s = repo.status, s.operation != nil {
                VCSOperationBanner(repo: repo, status: s).padding(.horizontal, M.md).padding(.top, M.sm)
            }
            if let n = repo.notice {
                VCSNoticeBar(repo: repo, notice: n).padding(.horizontal, M.md).padding(.top, M.sm)
            }
            HStack(spacing: 0) {
                PPTabBar(tabs: tabs, selection: $session.page)
                Spacer()
            }
            .padding(.horizontal, M.md)
            PPDivider(subtle: true)
            page.frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .background(shortcuts)
        .task {
            await repo.refresh()
            await repo.loadRemotes()
            await repo.loadStashes()
        }
        .onChange(of: repo.status?.conflicts ?? 0) { n in
            // 冲突解决完后冲突页消失：回到提交页
            if n == 0, session.page == .conflicts, repo.status?.operation == nil { session.page = .changes }
        }
    }

    private var tabs: [PPTabBar<VCSPage>.Tab] {
        let s = repo.status
        var t: [PPTabBar<VCSPage>.Tab] = [
            .init(value: .changes, title: tr("vcs.page.changes"), icon: .builtin("git-commit-horizontal"),
                  badge: s.map { $0.files.count }),
            .init(value: .log, title: tr("vcs.page.log"), icon: .builtin("git-graph")),
            .init(value: .branches, title: tr("vcs.page.branches"), icon: .builtin("git-branch")),
            .init(value: .stashes, title: tr("vcs.page.stashes"), icon: .builtin("archive"), badge: s?.stashCount),
        ]
        if (s?.conflicts ?? 0) > 0 || s?.operation != nil || session.page == .conflicts {
            t.append(.init(value: .conflicts, title: tr("vcs.page.conflicts"), icon: .builtin("triangle-alert"), badge: s?.conflicts))
        }
        t += pluginViews.map { .init(value: .plugin($0.key), title: $0.title, icon: IconSource.parse($0.icon, pluginId: $0.plugin)) }
        return t
    }

    @ViewBuilder private var page: some View {
        switch session.page {
        case .changes: VCSChangesPage(repo: repo)
        case .log: VCSLogPage(repo: repo)
        case .branches: VCSBranchesPage(repo: repo)
        case .stashes: VCSStashPage(repo: repo)
        case .conflicts: VCSConflictsPage(repo: repo)
        case .plugin(let key):
            if let v = pluginViews.first(where: { $0.key == key }) {
                PluginViewHost(view: v, context: ViewContext(plugin: v.plugin, view: v.id, projectId: repo.projectId, initialState: ["root": .string(repo.root)]))
                    .id(key + "|" + repo.root)
            } else {
                PPEmptyState(icon: "puzzle", title: tr("vcs.page.pluginMissing"))
            }
        }
    }

    /// ⌘K 提交、⇧⌘K 推送、⌘T 拉取（与 IDEA 一致）；对话框打开时不响应
    private var shortcuts: some View {
        ZStack {
            Button("") {
                guard AppModel.shared.dialogs.stack.isEmpty else { return }
                session.page = .changes
                DispatchQueue.main.async { repo.changes.focusMessage.send() }
            }
            .keyboardShortcut("k", modifiers: .command)
            Button("") {
                guard AppModel.shared.dialogs.stack.isEmpty else { return }
                VCSPushDialog.present(repo: repo)
            }
            .keyboardShortcut("k", modifiers: [.command, .shift])
            Button("") {
                guard AppModel.shared.dialogs.stack.isEmpty else { return }
                VCSPullDialog.present(repo: repo)
            }
            .keyboardShortcut("t", modifiers: .command)
        }
        .frame(width: 0, height: 0)
        .opacity(0)
        .accessibilityHidden(true)
    }
}

// MARK: - 工具栏

private struct VCSToolbar: View {
    @ObservedObject var session: VCSSession
    @ObservedObject var repo: VCSRepo
    @ObservedObject private var data = AppModel.shared.data
    @ObservedObject private var runs = AppModel.shared.runs
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.xs) {
            if session.roots.count > 1 {
                PPDropdown(options: session.roots.map { r in
                    .init(value: r.path, title: r.relPath == "." || r.relPath.isEmpty ? VCSPath.name(r.path) : r.relPath,
                          icon: .builtin("folder-git"))
                }, selection: Binding(get: { session.selectedRoot ?? "" }, set: { session.selectedRoot = $0 }),
                           size: .small, minWidth: 120, maxWidth: 220)
                .help(tr("vcs.repository"))
                PPDivider(vertical: true).frame(height: 16).padding(.horizontal, M.xs)
            }
            VCSBranchButton(repo: repo)
            PPDivider(vertical: true).frame(height: 16).padding(.horizontal, M.xs)
            PPIconButton("cloud-download", help: tr("vcs.fetch"), busy: repo.activity?.op == VCSOp.fetch) {
                VCSRemoteActions.fetch(repo: repo)
            }
            .disabled(repo.activity != nil)
            PPIconButton("arrow-down-to-line", help: tr("vcs.pull.ellipsis") + "  ⌘T", busy: repo.activity?.op == VCSOp.pull) {
                VCSPullDialog.present(repo: repo)
            }
            .disabled(repo.activity != nil)
            PPIconButton("arrow-up", help: tr("vcs.push.ellipsis") + "  ⇧⌘K", busy: repo.activity?.op == VCSOp.push) {
                VCSPushDialog.present(repo: repo)
            }
            .disabled(repo.activity != nil)
            PPIconButton("check", help: tr("vcs.commit") + "  ⌘K") {
                session.page = .changes
                DispatchQueue.main.async { repo.changes.focusMessage.send() }
            }
            PPIconButton("history", help: tr("vcs.page.log")) { session.page = .log }
            if let a = repo.activity {
                HStack(spacing: M.sm) {
                    PPSpinner(size: M.iconSmall)
                    Text(a.title).font(.pp(.small)).foregroundColor(theme.textSecondary).lineLimit(1)
                    if repo.runningTask != nil {
                        PPLink(title: tr("vcs.output")) { repo.showOutput(taskId: nil) }
                    }
                }
                .padding(.leading, M.md)
                .transition(.opacity)
            }
            Spacer(minLength: M.md)
            PPIconButton("refresh-cw", help: tr("vcs.refresh")) {
                Task {
                    await session.loadRoots()
                    await repo.refresh()
                    repo.changes.diff.reload()
                }
            }
            PPMenuButton(alignment: .trailing, entries: moreEntries) {
                PPIcon("ellipsis", size: M.icon, color: theme.iconPrimary)
                    .frame(width: M.iconButton, height: M.iconButton)
                    .help(tr("common.more"))
            }
        }
        .padding(.horizontal, M.md)
        .frame(height: M.toolbarHeight)
        .animation(.easeOut(duration: 0.12), value: repo.activity)
    }

    private func moreEntries() -> [MenuEntry] {
        var e: [MenuEntry] = [
            .item(tr("vcs.stash.new"), icon: "archive") { VCSStashDialogs.presentNew(repo: repo, paths: nil) },
            .item(tr("vcs.branch.new"), icon: "git-branch") { VCSBranchActions.newBranch(repo: repo, start: nil, startTitle: nil) },
            .item(tr("vcs.checkoutRef"), icon: "circle-dot") { VCSBranchActions.checkoutRef(repo: repo) },
            .item(tr("vcs.remote.add"), icon: "server") { VCSRemoteActions.addRemote(repo: repo) },
            .divider,
            .item(tr("vcs.changes.stagingArea"), icon: "layers", checked: repo.changes.stagingArea) { repo.changes.stagingArea.toggle() },
            .divider,
            .item(tr("vcs.revealRepo"), icon: "folder-open") { Actions.revealInFinder(repo.root) },
            .item(tr("vcs.copyRepoPath"), icon: "copy") { Actions.copy(repo.root) },
            .item(tr("vcs.clone.ellipsis"), icon: "download") { VCSCloneDialog.present() },
        ]
        let plugin = VCSPluginActions.entries(ActionLocation.vcsToolbar, repo: repo, args: [:])
        if !plugin.isEmpty {
            e.append(.divider)
            e += plugin
        }
        return e
    }
}

// MARK: - 进行中的操作

private struct VCSOperationBanner: View {
    @ObservedObject var repo: VCSRepo
    let status: VCSStatus

    var body: some View {
        let op = status.operation ?? ""
        let canSkip = op == "rebase" || op == "cherry-pick" || op == "revert" || op == "am"
        PPAlert(tone: status.conflicts > 0 ? "warning" : "info", title: title, message: detail) {
            HStack(spacing: M.sm) {
                if status.conflicts > 0 {
                    PPButton(tr("vcs.banner.conflicts", status.conflicts), icon: "triangle-alert", kind: .primary, size: .small) {
                        repo.session.page = .conflicts
                    }
                }
                PPButton(tr("vcs.op.continue"), kind: status.conflicts > 0 ? .secondary : .primary, size: .small,
                         busy: repo.activity?.op == VCSOp.continue) {
                    VCSConflictActions.continueOp(repo: repo)
                }
                .disabled(status.conflicts > 0)
                if canSkip {
                    PPButton(tr("vcs.op.skip"), size: .small) { VCSConflictActions.skip(repo: repo) }
                }
                PPButton(tr("vcs.op.abort"), kind: .ghost, size: .small) { VCSConflictActions.abort(repo: repo) }
            }
        }
    }

    private var title: String {
        let d = status.operationDetail
        switch status.operation {
        case "rebase":
            var t = tr("vcs.banner.rebase", d?.headName ?? status.branch, VCSStyle.shortHash(d?.onto))
            if let step = d?.step, let total = d?.total, total > 0 { t += "  " + tr("vcs.banner.step", step, total) }
            return t
        case "merge":
            return tr("vcs.banner.merge", status.branch.isEmpty ? VCSStyle.shortHash(status.headHash) : status.branch)
        case "cherry-pick":
            return tr("vcs.banner.cherryPick", VCSStyle.shortHash(d?.current))
        case "revert":
            return tr("vcs.banner.revert", VCSStyle.shortHash(d?.current))
        case "am":
            return tr("vcs.banner.am")
        default:
            return VCSStyle.operationTitle(status.operation)
        }
    }

    private var detail: String? {
        guard let d = status.operationDetail else { return nil }
        if let m = d.message, !m.isEmpty { return m }
        if status.operation == "rebase", let c = d.current, !c.isEmpty { return tr("vcs.banner.applying", VCSStyle.shortHash(c)) }
        return nil
    }
}

/// 失败 / 需要下一步的提示条：结论 + 下一步按钮 + 查看输出
struct VCSNoticeBar: View {
    @ObservedObject var repo: VCSRepo
    let notice: VCSNotice
    @State private var showOutput = false
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: M.sm) {
            PPAlert(tone: notice.tone, title: notice.title, message: notice.message) {
                HStack(spacing: M.sm) {
                    ForEach(notice.actions) { a in
                        PPButton(a.title, kind: a.danger ? .danger : (a.primary ? .primary : .secondary), size: .small, action: a.run)
                    }
                    if let t = notice.taskId {
                        PPButton(tr("vcs.output"), kind: .ghost, size: .small) { Actions.showTaskLog(t) }
                    } else if let o = notice.output, !o.isEmpty {
                        PPButton(showOutput ? tr("vcs.hideOutput") : tr("vcs.output"), kind: .ghost, size: .small) { showOutput.toggle() }
                    }
                    PPIconButton("x", help: tr("common.close"), size: M.iconButtonSmall, iconSize: M.iconSmall) { repo.notice = nil }
                }
            }
            if showOutput, let o = notice.output {
                ScrollView {
                    Text(o).font(.pp(.monoSmall)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(M.md)
                }
                .frame(maxHeight: 180)
                .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgCode))
            }
        }
    }
}

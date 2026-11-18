import AppKit
import PaperplaneKit
import SwiftUI

// 分支：工具栏分支弹出框、分支页（本地 / 远程 / 标签）、分支操作。

@MainActor
enum VCSBranchActions {
    /// 签出（本地修改会被覆盖时给「智能签出」「强制签出」）
    static func checkout(repo: VCSRepo, ref: String, title: String? = nil, then: (() -> Void)? = nil) {
        let t = title ?? tr("vcs.checkout.title", ref)
        repo.perform(VCSOp.checkout, VCSCheckoutArgs(ref: ref), title: t, extra: { r in
            guard r.reason == VCSReason.localChanges else { return [] }
            return [
                .init(title: tr("vcs.checkout.smart"), primary: true) {
                    repo.notice = nil
                    repo.perform(VCSOp.checkout, VCSCheckoutArgs(ref: ref, smart: true), title: t, then: { r in if r.ok { then?() } })
                },
                .init(title: tr("vcs.checkout.force"), danger: true) {
                    Dialogs.confirm(title: tr("vcs.checkout.forceTitle"), message: tr("vcs.checkout.forceMessage"),
                                    ok: tr("vcs.checkout.force"), danger: true) {
                        repo.notice = nil
                        try await repo.runThrowing(VCSOp.checkout, VCSCheckoutArgs(ref: ref, force: true), title: t)
                    }
                },
            ]
        }, then: { r in if r.ok { then?() } })
    }

    static func newBranch(repo: VCSRepo, start: String?, startTitle: String?) {
        let fields = [
            FormField(key: "name", title: tr("vcs.branch.name"), type: "string", required: true),
            FormField(key: "checkout", title: tr("vcs.branch.checkoutAfter"), type: "boolean", default: .bool(true)),
        ]
        let title = startTitle.map { tr("vcs.branch.newFromTitle", $0) } ?? tr("vcs.branch.new")
        Dialogs.form(title: title, fields: fields, ok: tr("common.create")) { v in
            let name = (v["name"]?.stringValue ?? "").trimmingCharacters(in: .whitespaces)
            if v["checkout"]?.boolValue ?? true {
                try await repo.runThrowing(VCSOp.checkout, VCSCheckoutArgs(ref: name, create: true, start: start),
                                           title: tr("vcs.branch.new"), extra: { r in
                                               guard r.reason == VCSReason.localChanges else { return [] }
                                               return [.init(title: tr("vcs.checkout.smart"), primary: true) {
                                                   repo.notice = nil
                                                   repo.perform(VCSOp.checkout, VCSCheckoutArgs(ref: name, create: true, start: start, smart: true),
                                                                title: tr("vcs.branch.new"))
                                               }]
                                           })
            } else {
                try await repo.runThrowing(VCSOp.branchCreate, VCSBranchCreateArgs(name: name, start: start), title: tr("vcs.branch.new"))
            }
            await repo.loadBranches()
        }
    }

    static func checkoutAndRebase(repo: VCSRepo, branch: String) {
        guard let cur = repo.currentBranch else { return }
        checkout(repo: repo, ref: branch) {
            repo.perform(VCSOp.rebase, VCSRefArgs(ref: cur), title: tr("vcs.branch.rebaseOnto", branch, cur), long: true)
        }
    }

    static func merge(repo: VCSRepo, ref: String) {
        let cur = repo.headTitle
        let title = tr("vcs.branch.mergeInto", ref, cur)
        repo.perform(VCSOp.merge, VCSRefArgs(ref: ref), title: title, long: true, extra: { r in
            guard r.reason == VCSReason.unrelatedHistories else { return [] }
            return [.init(title: tr("vcs.merge.allowUnrelated"), danger: true) {
                repo.notice = nil
                repo.perform(VCSOp.merge, VCSRefArgs(ref: ref, allowUnrelated: true), title: title, long: true)
            }]
        })
    }

    static func rebase(repo: VCSRepo, onto ref: String) {
        repo.perform(VCSOp.rebase, VCSRefArgs(ref: ref), title: tr("vcs.branch.rebaseOnto", repo.headTitle, ref), long: true)
    }

    static func rename(repo: VCSRepo, branch: String) {
        Dialogs.input(title: tr("vcs.branch.rename"), label: tr("vcs.branch.name"), initial: branch, ok: tr("common.save")) { name in
            try await repo.runThrowing(VCSOp.branchRename, VCSBranchRenameArgs(old: branch, new: name), title: tr("vcs.branch.rename"))
            await repo.loadBranches()
        }
    }

    static func delete(repo: VCSRepo, branch: String) {
        Dialogs.confirm(title: tr("vcs.branch.deleteTitle", branch), ok: tr("common.delete"), danger: true) {
            let r = try await repo.runThrowing(VCSOp.branchDelete, VCSBranchDeleteArgs(name: branch), title: tr("vcs.branch.delete"),
                                               quietSuccess: true, extra: { r in
                                                   guard r.reason == VCSReason.notFullyMerged else { return [] }
                                                   return [.init(title: tr("vcs.branch.forceDelete"), danger: true) {
                                                       repo.notice = nil
                                                       deleteForced(repo: repo, branch: branch)
                                                   }]
                                               })
            if r.ok { deleted(repo: repo, branch: branch, hash: r.hash) }
            await repo.loadBranches()
        }
    }

    private static func deleteForced(repo: VCSRepo, branch: String) {
        repo.perform(VCSOp.branchDelete, VCSBranchDeleteArgs(name: branch, force: true), title: tr("vcs.branch.delete"),
                     quietSuccess: true, then: { r in
                         if r.ok { deleted(repo: repo, branch: branch, hash: r.hash) }
                     })
    }

    /// 删除成功：提示里给「恢复」
    private static func deleted(repo: VCSRepo, branch: String, hash: String?) {
        guard let hash, !hash.isEmpty else {
            AppModel.shared.toasts.show("success", tr("vcs.branch.deleted", branch))
            return
        }
        AppModel.shared.toasts.show("success", tr("vcs.branch.deleted", branch), actionTitle: tr("vcs.branch.restore")) {
            repo.perform(VCSOp.branchCreate, VCSBranchCreateArgs(name: branch, start: hash), title: tr("vcs.branch.restore"))
        }
    }

    static func deleteRemote(repo: VCSRepo, branch: String) {
        Dialogs.confirm(title: tr("vcs.branch.deleteRemoteTitle", branch), message: tr("vcs.branch.deleteRemoteMessage"),
                        ok: tr("common.delete"), danger: true) {
            try await repo.runThrowing(VCSOp.branchDelete, VCSBranchDeleteArgs(name: branch, remote: true),
                                       title: tr("vcs.branch.deleteRemote"), long: true)
            await repo.loadBranches()
        }
    }

    static func setUpstream(repo: VCSRepo, branch: String) {
        Task {
            if repo.branches == nil { await repo.loadBranches() }
            let remotes = repo.branches?.remote.map(\.name) ?? []
            let current = repo.branches?.local.first { $0.name == branch }?.upstream ?? ""
            let field = FormField(key: "upstream", title: tr("vcs.branch.upstream"), type: "select",
                                  options: [Option(value: "", label: tr("vcs.branch.noUpstream"))] + remotes.map { Option(value: $0) })
            let guess = current.isEmpty ? (remotes.first { $0.hasSuffix("/" + branch) } ?? "") : current
            Dialogs.form(title: tr("vcs.branch.setUpstreamTitle", branch), fields: [field], values: ["upstream": .string(guess)],
                         ok: tr("common.save")) { v in
                try await repo.runThrowing(VCSOp.setUpstream, VCSSetUpstreamArgs(branch: branch, upstream: v["upstream"]?.stringValue ?? ""),
                                           title: tr("vcs.branch.setUpstream"))
                await repo.loadBranches()
            }
        }
    }

    /// 签出标签或修订（输入引用）
    static func checkoutRef(repo: VCSRepo) {
        Dialogs.input(title: tr("vcs.checkoutRef"), label: tr("vcs.checkoutRef.label"), ok: tr("vcs.checkout")) { ref in
            checkout(repo: repo, ref: ref)
        }
    }

    /// 分支右键 / 弹出框里的操作
    static func entries(repo: VCSRepo, branch b: VCSBranch, remote: Bool) -> [MenuEntry] {
        let cur = repo.currentBranch
        let isCurrent = !remote && b.name == cur
        var e: [MenuEntry] = []
        if !isCurrent {
            e.append(.item(tr("vcs.checkout"), icon: "check", disabled: b.worktree != nil) { checkout(repo: repo, ref: b.name) })
        }
        e.append(.item(tr("vcs.branch.newFromNamed", b.name), icon: "git-branch") {
            newBranch(repo: repo, start: b.name, startTitle: b.name)
        })
        if !isCurrent, !remote, let cur {
            e.append(.item(tr("vcs.branch.checkoutAndRebase", cur), icon: "git-pull-request-arrow", disabled: b.worktree != nil) {
                checkoutAndRebase(repo: repo, branch: b.name)
            })
        }
        if !isCurrent {
            e.append(.divider)
            e.append(.item(tr("vcs.branch.compareWith", repo.headTitle), icon: "git-compare") {
                VCSCompareDialog.present(repo: repo, base: b.name, head: nil, title: tr("vcs.compare.title", b.name, repo.headTitle))
            })
            e.append(.item(tr("vcs.branch.rebaseCurrentOnto", repo.headTitle, b.name), icon: "git-pull-request-arrow") {
                rebase(repo: repo, onto: b.name)
            })
            e.append(.item(tr("vcs.branch.mergeInto", b.name, repo.headTitle), icon: "git-merge") { merge(repo: repo, ref: b.name) })
        }
        e.append(.divider)
        if !remote {
            e.append(.item(tr("vcs.push.ellipsis"), icon: "arrow-up") { VCSPushDialog.present(repo: repo, branch: b.name) })
            e.append(.item(tr("vcs.branch.setUpstream"), icon: "link") { setUpstream(repo: repo, branch: b.name) })
            e.append(.item(tr("vcs.branch.rename"), icon: "pencil") { rename(repo: repo, branch: b.name) })
        }
        e.append(.item(tr("vcs.copyName"), icon: "copy") { Actions.copy(b.name) })
        if remote {
            e.append(.divider)
            e.append(.item(tr("vcs.branch.deleteRemote"), icon: "trash-2", destructive: true) { deleteRemote(repo: repo, branch: b.name) })
        } else if !isCurrent {
            e.append(.divider)
            e.append(.item(tr("vcs.branch.delete"), icon: "trash-2", destructive: true) { delete(repo: repo, branch: b.name) })
        }
        return e
    }
}

// MARK: - 工具栏分支按钮 + 弹出框

struct VCSBranchButton: View {
    @ObservedObject var repo: VCSRepo
    var showCounts = true
    var compact = false
    @State private var open = false
    @State private var hover = false
    @Environment(\.theme) private var theme

    var body: some View {
        let s = repo.status
        Button { open.toggle() } label: {
            HStack(spacing: M.sm) {
                PPIcon(s?.detached == true ? "circle-dot" : "git-branch", size: M.icon, color: theme.iconPrimary)
                Text(repo.headTitle.isEmpty ? "…" : repo.headTitle).font(.pp(compact ? .body : .bodyStrong)).lineLimit(1)
                    .truncationMode(.middle)
                if showCounts, let s {
                    if s.ahead > 0 { Text("↑\(s.ahead)").font(.pp(.monoSmall)).foregroundColor(theme.success) }
                    if s.behind > 0 { Text("↓\(s.behind)").font(.pp(.monoSmall)).foregroundColor(theme.info) }
                    if s.upstreamGone {
                        PPIcon("triangle-alert", size: 12, color: theme.warning).help(tr("vcs.branch.upstreamGone"))
                    }
                }
                PPIcon("chevron-down", size: 12, color: theme.iconSecondary)
            }
            .padding(.horizontal, compact ? M.sm : M.md)
            .frame(height: compact ? M.controlHeightSmall : M.controlHeight)
            .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(open ? theme.bgPressed : (hover ? theme.bgHover : .clear)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(s?.upstream.map { tr("vcs.branch.tracking", $0) } ?? "")
        .ppPopover(isPresented: $open, wantsKey: true) {
            VCSBranchPopover(repo: repo) { open = false }
        }
    }
}

private struct VCSBranchPopover: View {
    @ObservedObject var repo: VCSRepo
    let close: () -> Void
    @State private var query = ""
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            PPTextField(tr("vcs.branch.search"), text: $query, icon: "search", clearable: true, height: M.controlHeightSmall)
                .padding(M.sm)
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    if query.isEmpty {
                        actionRow(tr("vcs.branch.new"), icon: "plus") {
                            close()
                            VCSBranchActions.newBranch(repo: repo, start: nil, startTitle: nil)
                        }
                        actionRow(tr("vcs.checkoutRef"), icon: "circle-dot") {
                            close()
                            VCSBranchActions.checkoutRef(repo: repo)
                        }
                        PPDivider(subtle: true).padding(.vertical, M.xs)
                    }
                    if let b = repo.branches {
                        sections(b)
                    } else {
                        PPSpinner(size: M.iconSmall).frame(maxWidth: .infinity).padding(M.lg)
                    }
                }
                .padding(.horizontal, M.xs)
                .padding(.bottom, M.xs)
            }
            .frame(maxHeight: 460)
        }
        .frame(width: 360)
        .task { await repo.loadBranches() }
    }

    private func match(_ name: String) -> Bool { query.isEmpty || name.localizedCaseInsensitiveContains(query) }

    @ViewBuilder private func sections(_ b: VCSBranches) -> some View {
        let recent = query.isEmpty ? b.recent.filter { $0 != b.current }.compactMap { n in b.local.first { $0.name == n } } : []
        let local = b.local.filter { match($0.name) }
        let remote = b.remote.filter { match($0.name) }
        if !recent.isEmpty {
            header(tr("vcs.branch.recent"))
            ForEach(recent.prefix(5)) { br in branchRow(br, remote: false) }
        }
        if !local.isEmpty {
            header(tr("vcs.branch.local"))
            ForEach(local) { br in branchRow(br, remote: false) }
        }
        if !remote.isEmpty {
            header(tr("vcs.branch.remote"))
            ForEach(remote) { br in branchRow(br, remote: true) }
        }
        if local.isEmpty && remote.isEmpty {
            Text(tr("vcs.branch.noMatch")).foregroundColor(theme.textTertiary).padding(M.md)
        }
    }

    private func header(_ t: String) -> some View {
        Text(t).font(.pp(.caption)).foregroundColor(theme.textTertiary)
            .padding(.horizontal, M.md).padding(.top, M.sm).frame(height: M.rowHeightCompact, alignment: .bottomLeading)
    }

    private func actionRow(_ title: String, icon: String, action: @escaping () -> Void) -> some View {
        VCSPopoverRow(action: action) { _ in
            HStack(spacing: M.md) {
                PPIcon(icon, size: M.icon)
                Text(title)
                Spacer()
            }
        }
    }

    private func branchRow(_ br: VCSBranch, remote: Bool) -> some View {
        VCSPopoverRow(action: {
            MenuPresenter.showAtMouse(VCSBranchActions.entries(repo: repo, branch: br, remote: remote), parent: NSApp.keyWindow)
        }) { hl in
            HStack(spacing: M.sm) {
                PPIcon(br.current ? "check" : (remote ? "cloud" : "git-branch"), size: M.iconSmall,
                       color: hl ? theme.textOnAccent : (br.current ? theme.accent : theme.iconSecondary))
                Text(br.name).lineLimit(1).truncationMode(.middle).font(br.current ? .pp(.bodyStrong) : .pp(.body))
                if br.ahead > 0 { Text("↑\(br.ahead)").font(.pp(.monoSmall)) }
                if br.behind > 0 { Text("↓\(br.behind)").font(.pp(.monoSmall)) }
                if br.gone {
                    PPIcon("triangle-alert", size: 12, color: hl ? theme.textOnAccent : theme.warning).help(tr("vcs.branch.upstreamGone"))
                }
                if br.worktree != nil {
                    PPIcon("git-fork", size: 12).help(tr("vcs.branch.inWorktree", br.worktree ?? ""))
                }
                Spacer(minLength: M.md)
                PPIcon("chevron-right", size: 12, color: hl ? theme.textOnAccent : theme.iconSecondary)
            }
        }
        .help(br.subject ?? "")
    }
}

/// 弹出框里的可点击行（悬停强调色底）
struct VCSPopoverRow<Content: View>: View {
    let action: () -> Void
    @ViewBuilder let content: (Bool) -> Content
    @State private var hover = false
    @Environment(\.theme) private var theme

    var body: some View {
        content(hover)
            .foregroundColor(hover ? theme.textOnAccent : theme.textPrimary)
            .padding(.horizontal, M.md)
            .frame(height: M.rowHeight)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(hover ? theme.accent : .clear))
            .contentShape(Rectangle())
            .onHover { hover = $0 }
            .onTapGesture(perform: action)
    }
}

// MARK: - 分支页

struct VCSBranchesPage: View {
    @ObservedObject var repo: VCSRepo
    @State private var query = ""
    @State private var collapsed: Set<String> = []
    @State private var selected: String?
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: M.sm) {
                PPTextField(tr("vcs.branch.search"), text: $query, icon: "search", clearable: true, height: M.controlHeightSmall)
                    .frame(maxWidth: 280)
                PPButton(tr("vcs.branch.new"), icon: "plus", size: .small) {
                    VCSBranchActions.newBranch(repo: repo, start: nil, startTitle: nil)
                }
                PPButton(tr("vcs.fetch"), icon: "cloud-download", size: .small) { VCSRemoteActions.fetch(repo: repo) }
                Spacer()
                let tb = VCSPluginActions.actions(ActionLocation.vcsToolbar).filter { $0.id == "cleanupBranches" }
                ForEach(tb) { a in
                    PPButton(a.title, iconRef: IconSource.parse(a.icon, pluginId: a.plugin), size: .small) {
                        VCSPluginActions.invoke(a, repo: repo, args: [:])
                    }
                }
                PPIconButton("refresh-cw", help: tr("vcs.refresh"), size: M.iconButtonSmall, iconSize: M.iconSmall) { reload() }
            }
            .padding(.horizontal, M.md)
            .frame(height: M.toolbarHeight)
            PPDivider(subtle: true)
            header
            PPDivider(subtle: true)
            if let b = repo.branches {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        section("local", tr("vcs.branch.local"), count: b.local.count) {
                            ForEach(b.local.filter { match($0.name) }) { br in branchRow(br, remote: false) }
                        }
                        let byRemote = Dictionary(grouping: b.remote, by: { $0.remote ?? String($0.name.split(separator: "/").first ?? "") })
                        ForEach(byRemote.keys.sorted(), id: \.self) { rn in
                            section("remote:" + rn, tr("vcs.branch.remoteNamed", rn), count: byRemote[rn]?.count ?? 0) {
                                ForEach((byRemote[rn] ?? []).filter { match($0.name) }) { br in branchRow(br, remote: true) }
                            }
                        }
                        section("tags", tr("vcs.branch.tags"), count: repo.tags.count) {
                            ForEach(repo.tags.filter { match($0.name) }) { t in tagRow(t) }
                        }
                    }
                    .padding(M.xs)
                }
            } else {
                PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .task { reload() }
        .onChange(of: repo.revision) { _ in reload() }
    }

    private func reload() {
        Task {
            await repo.loadBranches()
            await repo.loadTags()
        }
    }

    private func match(_ n: String) -> Bool { query.isEmpty || n.localizedCaseInsensitiveContains(query) }

    private var header: some View {
        HStack(spacing: 0) {
            Text(tr("vcs.branch.name")).frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 30)
            Text(tr("vcs.branch.upstream")).frame(width: 200, alignment: .leading)
            Text(tr("vcs.branch.lastCommit")).frame(width: 280, alignment: .leading)
            Text(tr("vcs.log.date")).frame(width: 118, alignment: .leading)
        }
        .font(.pp(.label))
        .foregroundColor(theme.textSecondary)
        .frame(height: M.rowHeightCompact)
        .background(theme.bgSubtle)
    }

    @ViewBuilder
    private func section<C: View>(_ key: String, _ title: String, count: Int, @ViewBuilder content: () -> C) -> some View {
        let isCollapsed = collapsed.contains(key)
        VCSGroupHeader(title: title, count: count, collapsed: isCollapsed, onToggle: {
            if isCollapsed { collapsed.remove(key) } else { collapsed.insert(key) }
        })
        if !isCollapsed { content() }
    }

    private func branchRow(_ br: VCSBranch, remote: Bool) -> some View {
        let id = (remote ? "r:" : "l:") + br.name
        return VCSTableRow(selected: selected == id, onTap: { selected = id }, onDoubleTap: {
            if !br.current { VCSBranchActions.checkout(repo: repo, ref: br.name) }
        }) {
            HStack(spacing: 0) {
                HStack(spacing: M.sm) {
                    PPIcon(br.current ? "check" : (remote ? "cloud" : "git-branch"), size: M.iconSmall,
                           color: br.current ? theme.accent : theme.iconSecondary)
                    Text(remote ? String(br.name.dropFirst((br.remote ?? "").count + 1)) : br.name)
                        .font(br.current ? .pp(.bodyStrong) : .pp(.body)).lineLimit(1).truncationMode(.middle)
                    if br.gone { PPBadge(text: tr("vcs.branch.gone"), tone: "warning") }
                    if let wt = br.worktree { PPBadge(text: tr("vcs.branch.worktree"), icon: .builtin("git-fork")).help(wt) }
                }
                .padding(.leading, 18)
                .frame(maxWidth: .infinity, alignment: .leading)
                HStack(spacing: M.xs) {
                    Text(br.upstream ?? "").lineLimit(1).foregroundColor(theme.textSecondary)
                    if br.ahead > 0 { Text("↑\(br.ahead)").font(.pp(.monoSmall)).foregroundColor(theme.success) }
                    if br.behind > 0 { Text("↓\(br.behind)").font(.pp(.monoSmall)).foregroundColor(theme.info) }
                }
                .frame(width: 200, alignment: .leading)
                HStack(spacing: M.xs) {
                    Text(br.head ?? "").font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                    Text(br.subject ?? "").lineLimit(1).foregroundColor(theme.textSecondary)
                }
                .frame(width: 280, alignment: .leading)
                Text(VCSDate.short(br.parsedDate)).foregroundColor(theme.textSecondary).frame(width: 118, alignment: .leading)
            }
        }
        .ppContextMenu { VCSBranchActions.entries(repo: repo, branch: br, remote: remote) }
    }

    private func tagRow(_ t: VCSTag) -> some View {
        let id = "t:" + t.name
        return VCSTableRow(selected: selected == id, onTap: { selected = id }, onDoubleTap: nil) {
            HStack(spacing: 0) {
                HStack(spacing: M.sm) {
                    PPIcon("tag", size: M.iconSmall, color: theme.warning)
                    Text(t.name).lineLimit(1)
                    if t.annotated, let m = t.message, !m.isEmpty {
                        Text(m).font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1)
                    }
                }
                .padding(.leading, 18)
                .frame(maxWidth: .infinity, alignment: .leading)
                Text(t.tagger ?? "").lineLimit(1).foregroundColor(theme.textSecondary).frame(width: 200, alignment: .leading)
                HStack(spacing: M.xs) {
                    Text(String(t.hash.prefix(7))).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                    Text(t.subject ?? "").lineLimit(1).foregroundColor(theme.textSecondary)
                }
                .frame(width: 280, alignment: .leading)
                Text(VCSDate.short(t.date.flatMap(GoTime.parse))).foregroundColor(theme.textSecondary).frame(width: 118, alignment: .leading)
            }
        }
        .ppContextMenu { VCSTagActions.entries(repo: repo, tag: t) }
    }
}

@MainActor
enum VCSTagActions {
    static func entries(repo: VCSRepo, tag t: VCSTag) -> [MenuEntry] {
        [
            .item(tr("vcs.checkout"), icon: "check") { VCSBranchActions.checkout(repo: repo, ref: t.name) },
            .item(tr("vcs.branch.newFromNamed", t.name), icon: "git-branch") {
                VCSBranchActions.newBranch(repo: repo, start: t.name, startTitle: t.name)
            },
            .divider,
            .item(tr("vcs.tag.pushOne"), icon: "arrow-up") {
                repo.perform(VCSOp.tagPush, VCSTagPushArgs(ref: t.name), title: tr("vcs.tag.pushOne"), long: true)
            },
            .item(tr("vcs.copyName"), icon: "copy") { Actions.copy(t.name) },
            .divider,
            .item(tr("vcs.tag.delete"), icon: "trash-2", destructive: true) {
                Dialogs.confirm(title: tr("vcs.tag.deleteTitle", t.name), ok: tr("common.delete"), danger: true) {
                    try await repo.runThrowing(VCSOp.tagDelete, VCSRefArgs(ref: t.name), title: tr("vcs.tag.delete"))
                    await repo.loadTags()
                }
            },
            .item(tr("vcs.tag.deleteRemote"), icon: "trash-2", destructive: true) {
                Dialogs.confirm(title: tr("vcs.tag.deleteRemoteTitle", t.name), ok: tr("common.delete"), danger: true) {
                    try await repo.runThrowing(VCSOp.tagPush, VCSTagPushArgs(ref: t.name, delete: true), title: tr("vcs.tag.deleteRemote"), long: true)
                }
            },
        ]
    }
}

/// 表格行：选中 / 悬停底色、单击、双击
struct VCSTableRow<Content: View>: View {
    var selected = false
    let onTap: () -> Void
    let onDoubleTap: (() -> Void)?
    @ViewBuilder let content: Content
    @State private var hover = false
    @Environment(\.theme) private var theme

    var body: some View {
        content
            .frame(height: M.rowHeight)
            .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(selected ? theme.bgSelected : (hover ? theme.bgHover : .clear)))
            .contentShape(Rectangle())
            .onHover { hover = $0 }
            .gesture(TapGesture(count: 2).onEnded { onDoubleTap?() }, including: onDoubleTap == nil ? .subviews : .all)
            .simultaneousGesture(TapGesture().onEnded { onTap() })
    }
}

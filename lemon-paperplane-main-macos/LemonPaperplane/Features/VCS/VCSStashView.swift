import PaperplaneKit
import SwiftUI

// 储藏：列表（信息、分支、时间）、查看文件、应用 / 弹出 / 恢复为分支 / 删除、新建储藏。

@MainActor
enum VCSStashDialogs {
    /// 新建储藏；paths 非空时只储藏这些文件
    static func presentNew(repo: VCSRepo, paths: [String]?) {
        var fields = [FormField(key: "message", title: tr("vcs.stash.message"), type: "string")]
        if paths == nil {
            fields.append(FormField(key: "includeUntracked", title: tr("vcs.stash.includeUntracked"), type: "boolean"))
        }
        fields.append(FormField(key: "keepIndex", title: tr("vcs.stash.keepIndex"), type: "boolean", info: tr("vcs.stash.keepIndexInfo")))
        let title = paths.map { tr("vcs.stash.newFiles", $0.count) } ?? tr("vcs.stash.new")
        Dialogs.form(title: title, fields: fields, ok: tr("vcs.stash.save")) { v in
            let msg = (v["message"]?.stringValue ?? "").trimmingCharacters(in: .whitespaces)
            let args = VCSStashSaveArgs(message: msg.isEmpty ? nil : msg,
                                        includeUntracked: v["includeUntracked"]?.boolValue == true ? true : nil,
                                        paths: paths,
                                        keepIndex: v["keepIndex"]?.boolValue == true ? true : nil)
            try await repo.runThrowing(VCSOp.stashSave, args, title: tr("vcs.stash.new"))
            await repo.loadStashes()
        }
    }

    static func apply(repo: VCSRepo, _ s: VCSStash, pop: Bool, restoreIndex: Bool) {
        repo.perform(VCSOp.stashApply, VCSStashArgs(index: s.index, pop: pop ? true : nil, restoreIndex: restoreIndex ? true : nil),
                     title: pop ? tr("vcs.stash.pop") : tr("vcs.stash.apply"), then: { _ in Task { await repo.loadStashes() } })
    }

    static func asBranch(repo: VCSRepo, _ s: VCSStash) {
        Dialogs.input(title: tr("vcs.stash.asBranch"), label: tr("vcs.branch.name"), ok: tr("common.create")) { name in
            try await repo.runThrowing(VCSOp.stashApply, VCSStashArgs(index: s.index, branch: name), title: tr("vcs.stash.asBranch"))
            await repo.loadStashes()
        }
    }

    static func drop(repo: VCSRepo, _ s: VCSStash) {
        Dialogs.confirm(title: tr("vcs.stash.dropTitle", "stash@{\(s.index)}"), message: s.message, ok: tr("common.delete"), danger: true) {
            try await repo.runThrowing(VCSOp.stashDrop, VCSStashArgs(index: s.index), title: tr("vcs.stash.drop"), quietSuccess: true)
            await repo.loadStashes()
        }
    }

    static func clear(repo: VCSRepo) {
        Dialogs.confirm(title: tr("vcs.stash.clearTitle"), message: tr("vcs.stash.clearMessage", repo.stashes.count),
                        ok: tr("vcs.stash.clear"), danger: true) {
            try await repo.runThrowing(VCSOp.stashDrop, VCSStashArgs(index: 0, all: true), title: tr("vcs.stash.clear"), quietSuccess: true)
            await repo.loadStashes()
        }
    }
}

struct VCSStashPage: View {
    @ObservedObject var repo: VCSRepo
    @State private var selected: Int?
    @State private var show: VCSStashShow?
    @State private var showError: String?
    @State private var restoreIndex = false
    @State private var listWidth: CGFloat = 380
    @Environment(\.theme) private var theme

    private var current: VCSStash? { repo.stashes.first { $0.index == selected } }

    var body: some View {
        PPSplitView(leftWidth: $listWidth, minLeft: 260, maxLeft: 600) {
            VStack(spacing: 0) {
                HStack(spacing: M.xs) {
                    PPButton(tr("vcs.stash.new"), icon: "plus", size: .small) { VCSStashDialogs.presentNew(repo: repo, paths: nil) }
                        .disabled(repo.status?.files.isEmpty ?? true)
                    Spacer()
                    PPIconButton("trash-2", help: tr("vcs.stash.clear"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                        VCSStashDialogs.clear(repo: repo)
                    }
                    .disabled(repo.stashes.isEmpty)
                    PPIconButton("refresh-cw", help: tr("vcs.refresh"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                        Task { await repo.loadStashes() }
                    }
                }
                .padding(.horizontal, M.md)
                .frame(height: M.toolbarHeight)
                PPDivider(subtle: true)
                if repo.stashes.isEmpty {
                    PPEmptyState(icon: "archive", title: tr("vcs.stash.empty"))
                } else {
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 1) {
                            ForEach(repo.stashes) { s in row(s) }
                        }
                        .padding(M.xs)
                    }
                }
            }
        } right: {
            if let s = current {
                detail(s)
            } else {
                PPEmptyState(icon: "archive", title: tr("vcs.stash.select"))
            }
        }
        .task {
            await repo.loadStashes()
            if selected == nil, let f = repo.stashes.first { select(f) }
        }
        .onChange(of: repo.revision) { _ in Task { await repo.loadStashes() } }
        .onChange(of: repo.stashes) { list in
            if let sel = selected, !list.contains(where: { $0.index == sel }) { selected = nil; show = nil }
            if selected == nil, let f = list.first { select(f) }
        }
    }

    private func row(_ s: VCSStash) -> some View {
        VCSTableRow(selected: s.index == selected, onTap: { select(s) }, onDoubleTap: nil) {
            HStack(spacing: M.sm) {
                PPIcon("archive", size: M.iconSmall, color: theme.iconSecondary)
                VStack(alignment: .leading, spacing: 0) {
                    Text(s.message.isEmpty ? "stash@{\(s.index)}" : s.message).lineLimit(1)
                    HStack(spacing: M.xs) {
                        if let b = s.branch { Text(b).lineLimit(1) }
                        Text(VCSDate.short(s.parsedDate))
                    }
                    .font(.pp(.caption)).foregroundColor(theme.textTertiary)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, M.sm)
        }
        .frame(height: 38)
        .ppContextMenu {
            [
                .item(tr("vcs.stash.apply"), icon: "archive-restore") { VCSStashDialogs.apply(repo: repo, s, pop: false, restoreIndex: restoreIndex) },
                .item(tr("vcs.stash.pop"), icon: "arrow-up-from-line") { VCSStashDialogs.apply(repo: repo, s, pop: true, restoreIndex: restoreIndex) },
                .item(tr("vcs.stash.asBranch"), icon: "git-branch") { VCSStashDialogs.asBranch(repo: repo, s) },
                .divider,
                .item(tr("vcs.stash.drop"), icon: "trash-2", destructive: true) { VCSStashDialogs.drop(repo: repo, s) },
            ]
        }
    }

    private func select(_ s: VCSStash) {
        selected = s.index
        show = nil
        showError = nil
        Task {
            do {
                let r = try await repo.api().vcsStashShow(projectId: repo.projectId, root: repo.root, index: s.index)
                if selected == s.index { show = r }
            } catch {
                showError = error.localizedDescription
            }
        }
    }

    private func detail(_ s: VCSStash) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: M.sm) {
                Text(s.message.isEmpty ? "stash@{\(s.index)}" : s.message).font(.pp(.title)).textSelection(.enabled)
                HStack(spacing: M.md) {
                    Text("stash@{\(s.index)}").font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                    if let b = s.branch {
                        HStack(spacing: M.xs) { PPIcon("git-branch", size: 12); Text(b) }.foregroundColor(theme.textSecondary)
                    }
                    Text(VCSDate.full(s.parsedDate)).foregroundColor(theme.textSecondary)
                }
                .font(.pp(.small))
                HStack(spacing: M.sm) {
                    PPButton(tr("vcs.stash.apply"), icon: "archive-restore", kind: .primary, size: .small) {
                        VCSStashDialogs.apply(repo: repo, s, pop: false, restoreIndex: restoreIndex)
                    }
                    PPButton(tr("vcs.stash.pop"), icon: "arrow-up-from-line", size: .small) {
                        VCSStashDialogs.apply(repo: repo, s, pop: true, restoreIndex: restoreIndex)
                    }
                    PPButton(tr("vcs.stash.asBranch"), icon: "git-branch", size: .small) { VCSStashDialogs.asBranch(repo: repo, s) }
                    PPButton(tr("vcs.stash.drop"), icon: "trash-2", kind: .ghost, size: .small) { VCSStashDialogs.drop(repo: repo, s) }
                    PPCheckbox(isOn: $restoreIndex, label: tr("vcs.stash.restoreIndex")).font(.pp(.small))
                }
            }
            .padding(M.lg)
            PPDivider(subtle: true)
            if let e = showError {
                PPAlert(tone: "danger", title: e).padding(M.md)
            } else if let sh = show {
                ScrollView {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(tr("vcs.log.files", sh.files.count)).font(.pp(.label)).foregroundColor(theme.textSecondary)
                            .padding(.horizontal, M.sm).padding(.bottom, M.xs)
                        ForEach(sh.files) { f in
                            VCSFileLine(file: f) { VCSDiffDialog.presentStash(repo: repo, stash: s, show: sh, file: f) }
                        }
                        if !sh.untracked.isEmpty {
                            Text(tr("vcs.stash.untracked", sh.untracked.count)).font(.pp(.label)).foregroundColor(theme.textSecondary)
                                .padding(.horizontal, M.sm).padding(.top, M.md).padding(.bottom, M.xs)
                            ForEach(sh.untracked, id: \.self) { p in
                                HStack(spacing: M.sm) {
                                    VCSStatusLetter(status: "?")
                                    Text(p).lineLimit(1).truncationMode(.middle).foregroundColor(theme.textSecondary)
                                    Spacer()
                                }
                                .padding(.horizontal, M.sm)
                                .frame(height: M.rowHeightCompact)
                            }
                        }
                    }
                    .padding(M.md)
                }
            } else {
                PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

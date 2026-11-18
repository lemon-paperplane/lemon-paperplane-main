import PaperplaneKit
import SwiftUI

// 提交右键菜单与历史改写：签出修订、新建分支/标签、拣选、还原、重置、修改信息、交互式变基、比较、恢复文件。

@MainActor
enum VCSCommitMenu {
    /// commits：选中的提交（日志顺序，新的在前）
    static func entries(repo: VCSRepo, commits: [VCSCommit]) -> [MenuEntry] {
        guard let c = commits.first else { return [] }
        let single = commits.count == 1
        let hashes = commits.map(\.hash)
        let isHead = c.hash == repo.status?.headHash
        let merge = c.parents.count > 1
        var e: [MenuEntry] = []
        e.append(.item(tr("vcs.copyHash"), icon: "copy") { Actions.copy(hashes.joined(separator: "\n")) })
        e.append(.item(tr("vcs.copySubject"), icon: "copy", disabled: !single) { Actions.copy(c.subject) })
        e.append(.divider)
        e.append(.item(tr("vcs.checkoutRevision"), icon: "circle-dot", disabled: !single) {
            VCSBranchActions.checkout(repo: repo, ref: c.hash, title: tr("vcs.checkoutRevision"))
        })
        e.append(.item(tr("vcs.branch.newFrom"), icon: "git-branch", disabled: !single) {
            VCSBranchActions.newBranch(repo: repo, start: c.hash, startTitle: c.short)
        })
        e.append(.item(tr("vcs.tag.new"), icon: "tag", disabled: !single) { VCSHistoryActions.newTag(repo: repo, ref: c.hash) })
        e.append(.divider)
        e.append(.item(tr("vcs.cherryPick"), icon: "cherry") { VCSHistoryActions.cherryPick(repo: repo, hashes: hashes) })
        e.append(.item(tr("vcs.revertCommit"), icon: "undo-2") { VCSHistoryActions.revert(repo: repo, hashes: hashes) })
        e.append(.item(tr("vcs.reset.ellipsis"), icon: "rotate-ccw", disabled: !single || repo.currentBranch == nil && !(repo.status?.detached ?? false)) {
            VCSHistoryActions.reset(repo: repo, commit: c)
        })
        e.append(.item(tr("vcs.reword.ellipsis"), icon: "pencil", disabled: !single) { VCSHistoryActions.reword(repo: repo, commit: c) })
        e.append(.item(tr("vcs.rebaseInteractive.ellipsis"), icon: "list", disabled: !single || merge) {
            VCSRebaseEditor.present(repo: repo, from: c)
        })
        if isHead && single {
            e.append(.item(tr("vcs.undoCommit"), icon: "undo-2") { VCSCommitActions.undoLast(repo: repo) })
        }
        e.append(.divider)
        e.append(.item(tr("vcs.compareWithCurrent"), icon: "git-compare", disabled: !single || isHead) {
            VCSCompareDialog.present(repo: repo, base: c.hash, head: nil, title: tr("vcs.compare.title", c.short, repo.headTitle))
        })
        let plugin = VCSPluginActions.entries(ActionLocation.vcsCommitMenu, repo: repo,
                                              args: ["hash": .string(c.hash), "hashes": JSONValue(hashes)])
        if !plugin.isEmpty {
            e.append(.divider)
            e += plugin
        }
        return e
    }
}

@MainActor
enum VCSHistoryActions {
    static func cherryPick(repo: VCSRepo, hashes: [String]) {
        guard let first = hashes.first else { return }
        repo.perform(VCSOp.cherryPick, VCSRefArgs(ref: first, refs: hashes.count > 1 ? Array(hashes.dropFirst()) : nil),
                     title: tr("vcs.cherryPick"))
    }

    static func revert(repo: VCSRepo, hashes: [String]) {
        guard let first = hashes.first else { return }
        Dialogs.confirm(title: tr("vcs.revert.title", hashes.count), message: tr("vcs.revert.message"), ok: tr("vcs.revertCommit")) {
            try await repo.runThrowing(VCSOp.revert, VCSRefArgs(ref: first, refs: hashes.count > 1 ? Array(hashes.dropFirst()) : nil),
                                       title: tr("vcs.revertCommit"))
        }
    }

    static func newTag(repo: VCSRepo, ref: String) {
        let fields = [
            FormField(key: "name", title: tr("vcs.tag.name"), type: "string", required: true),
            FormField(key: "message", title: tr("vcs.tag.message"), type: "text", info: tr("vcs.tag.messageInfo")),
            FormField(key: "push", title: tr("vcs.tag.push"), type: "boolean"),
        ]
        Dialogs.form(title: tr("vcs.tag.new"), fields: fields, ok: tr("common.create")) { v in
            let name = v["name"]?.stringValue ?? ""
            let msg = v["message"]?.stringValue ?? ""
            let push = v["push"]?.boolValue ?? false
            try await repo.runThrowing(VCSOp.tagCreate, VCSTagCreateArgs(name: name, ref: ref, message: msg.isEmpty ? nil : msg,
                                                                         push: push ? true : nil),
                                       title: tr("vcs.tag.new"), long: push)
            await repo.loadTags()
        }
    }

    static func reset(repo: VCSRepo, commit: VCSCommit) {
        let id = "vcs-reset"
        AppModel.shared.dialogs.present(id: id) { ResetBody(id: id, repo: repo, commit: commit) }
    }

    private struct ResetBody: View {
        let id: String
        let repo: VCSRepo
        let commit: VCSCommit
        @State private var mode = "mixed"
        @State private var busy = false
        @State private var error: String?
        @Environment(\.theme) private var theme

        var body: some View {
            PPDialog(title: tr("vcs.reset.title", repo.headTitle, commit.short), width: 460, onClose: close) {
                VStack(alignment: .leading, spacing: M.md) {
                    Text(commit.subject).foregroundColor(theme.textSecondary).lineLimit(2)
                    ForEach(["soft", "mixed", "keep", "hard"], id: \.self) { m in
                        HStack(spacing: M.sm) {
                            PPRadio(selected: mode == m, label: tr("vcs.reset.\(m)")) { mode = m }
                            PPInfoButton(text: tr("vcs.reset.\(m).info"))
                        }
                    }
                    if mode == "hard" { PPAlert(tone: "danger", title: tr("vcs.reset.hardWarning")) }
                    if let error { PPAlert(tone: "danger", title: error) }
                }
            } footer: {
                PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
                PPButton(tr("vcs.reset"), kind: mode == "hard" ? .danger : .primary, busy: busy, action: submit)
                    .keyboardShortcut(.defaultAction)
            }
        }

        private func close() { AppModel.shared.dialogs.dismiss(id) }

        private func submit() {
            if mode == "hard" {
                // 二次确认：硬重置会丢掉未提交的修改
                close()
                Dialogs.confirm(title: tr("vcs.reset.hardConfirm"), message: tr("vcs.reset.hardConfirmMessage", commit.short),
                                ok: tr("vcs.reset"), danger: true) {
                    try await repo.runThrowing(VCSOp.reset, VCSResetArgs(ref: commit.hash, mode: "hard"), title: tr("vcs.reset"))
                }
                return
            }
            busy = true
            Task {
                do {
                    try await repo.runThrowing(VCSOp.reset, VCSResetArgs(ref: commit.hash, mode: mode), title: tr("vcs.reset"))
                    close()
                } catch {
                    self.error = error.localizedDescription
                }
                busy = false
            }
        }
    }

    static func reword(repo: VCSRepo, commit: VCSCommit) {
        let id = "vcs-reword"
        AppModel.shared.dialogs.present(id: id) { RewordBody(id: id, repo: repo, commit: commit) }
    }

    private struct RewordBody: View {
        let id: String
        let repo: VCSRepo
        let commit: VCSCommit
        @State private var text = ""
        @State private var loaded = false
        @State private var busy = false
        @State private var error: String?

        var body: some View {
            PPDialog(title: tr("vcs.reword.title", commit.short), width: 560, onClose: close) {
                VStack(alignment: .leading, spacing: M.md) {
                    PPTextArea(text: $text, minHeight: 160)
                    if let error { PPAlert(tone: "danger", title: error) }
                }
            } footer: {
                PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
                PPButton(tr("common.save"), kind: .primary, busy: busy, action: submit)
                    .disabled(!loaded || text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .task {
                if let d = try? await repo.api().vcsShow(projectId: repo.projectId, root: repo.root, hash: commit.hash) {
                    text = d.message ?? ([d.commit.subject] + (d.body.map { ["", $0] } ?? [])).joined(separator: "\n")
                } else {
                    text = commit.subject
                }
                loaded = true
            }
        }

        private func close() { AppModel.shared.dialogs.dismiss(id) }

        private func submit() {
            busy = true
            Task {
                do {
                    try await repo.runThrowing(VCSOp.reword, VCSRewordArgs(hash: commit.hash, message: text), title: tr("vcs.reword"))
                    close()
                } catch {
                    self.error = error.localizedDescription
                }
                busy = false
            }
        }
    }

    /// 把文件恢复到某个版本（工作区和暂存区）
    static func restoreFile(repo: VCSRepo, path: String, ref: String, short: String) {
        Dialogs.confirm(title: tr("vcs.checkoutFileVersion.title", VCSPath.name(path), short),
                        message: tr("vcs.checkoutFileVersion.message"), ok: tr("vcs.checkoutFileVersion"), danger: true) {
            try await repo.runThrowing(VCSOp.checkoutFile, VCSCheckoutFileArgs(path: path, ref: ref), title: tr("vcs.checkoutFileVersion"))
        }
    }
}

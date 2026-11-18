import PaperplaneKit
import SwiftUI

// 远程操作：获取、拉取对话框（merge/rebase/ff-only、自动储藏）、推送对话框（推送预览、远端领先提示、设置上游、
// --force-with-lease 二次确认、推送标签）、添加远程。

@MainActor
enum VCSRemoteActions {
    static func fetch(repo: VCSRepo) {
        repo.perform(VCSOp.fetch, VCSFetchArgs(), title: tr("vcs.fetch"), long: true)
    }

    static func addRemote(repo: VCSRepo, then: (() -> Void)? = nil) {
        let fields = [
            FormField(key: "name", title: tr("vcs.remote.name"), type: "string", default: .string("origin"), required: true),
            FormField(key: "url", title: tr("vcs.remote.url"), type: "string", placeholder: "https://… / git@…", required: true),
        ]
        Dialogs.form(title: tr("vcs.remote.add"), fields: fields, ok: tr("common.create")) { v in
            let r = VCSRemote(name: v["name"]?.stringValue ?? "origin", fetchUrl: v["url"]?.stringValue ?? "")
            try await repo.runThrowing(VCSOp.remoteSet, r, title: tr("vcs.remote.add"), quietSuccess: true)
            await repo.loadRemotes()
            then?()
        }
    }

    /// 拉取（带对话框里选的参数）
    static func pull(repo: VCSRepo, args: VCSPullArgs) {
        let title = tr("vcs.pull")
        repo.perform(VCSOp.pull, args, title: title, long: true, extra: { r in
            switch r.reason {
            case VCSReason.divergent:
                return [
                    .init(title: tr("vcs.pull.rebase"), primary: true) {
                        repo.notice = nil
                        var a = args
                        a.mode = "rebase"
                        pull(repo: repo, args: a)
                    },
                    .init(title: tr("vcs.pull.merge")) {
                        repo.notice = nil
                        var a = args
                        a.mode = "merge"
                        pull(repo: repo, args: a)
                    },
                ]
            case VCSReason.localChanges:
                return [.init(title: tr("vcs.pull.stashAndRetry"), primary: true) {
                    repo.notice = nil
                    var a = args
                    a.autostash = true
                    pull(repo: repo, args: a)
                }]
            case VCSReason.noUpstream:
                return [.init(title: tr("vcs.pull.chooseBranch"), primary: true) {
                    repo.notice = nil
                    VCSPullDialog.present(repo: repo)
                }]
            default:
                return []
            }
        })
    }

    static func push(repo: VCSRepo, args: VCSPushArgs) {
        repo.perform(VCSOp.push, args, title: tr("vcs.push"), long: true, extra: { r in
            guard r.reason == VCSReason.rejected || r.rejected else { return [] }
            return [.init(title: tr("vcs.push.forceEllipsis"), danger: true) {
                repo.notice = nil
                var a = args
                a.force = true
                confirmForce(repo: repo, args: a)
            }]
        })
    }

    static func confirmForce(repo: VCSRepo, args: VCSPushArgs) {
        Dialogs.confirm(title: tr("vcs.push.forceTitle"), message: tr("vcs.push.forceMessage"), ok: tr("vcs.push.force"), danger: true) {
            push(repo: repo, args: args)
        }
    }
}

// MARK: - 拉取

@MainActor
enum VCSPullDialog {
    static func present(repo: VCSRepo) {
        let id = "vcs-pull"
        AppModel.shared.dialogs.present(id: id) { Body(id: id, repo: repo) }
    }

    private struct Body: View {
        let id: String
        @ObservedObject var repo: VCSRepo
        @State private var target = ""
        @State private var mode = VCSPrefs.pullMode
        @State private var autostash = VCSPrefs.pullAutostash
        @Environment(\.theme) private var theme

        var body: some View {
            let remotes = repo.branches?.remote ?? []
            PPDialog(title: tr("vcs.pull.title", repo.headTitle), width: 480, onClose: close) {
                VStack(alignment: .leading, spacing: M.lg) {
                    if remotes.isEmpty && repo.branches != nil {
                        PPAlert(tone: "warning", title: tr("vcs.pull.noRemoteBranches")) {
                            PPButton(tr("vcs.remote.add"), size: .small) {
                                close()
                                VCSRemoteActions.addRemote(repo: repo)
                            }
                        }
                    } else {
                        labeled(tr("vcs.pull.from")) {
                            PPDropdown(options: remotes.map { .init(value: $0.name, title: $0.name, icon: .builtin("cloud")) },
                                       selection: $target, placeholder: tr("vcs.pull.chooseBranch"), fullWidth: true)
                        }
                    }
                    labeled(tr("vcs.pull.mode")) {
                        HStack(spacing: M.sm) {
                            PPSegmented(options: [("merge", tr("vcs.pull.merge")), ("rebase", tr("vcs.pull.rebase")),
                                                  ("ff-only", tr("vcs.pull.ffOnly"))], selection: $mode)
                            PPInfoButton(text: tr("vcs.pull.modeInfo"))
                        }
                    }
                    labeled("") {
                        HStack(spacing: M.sm) {
                            PPCheckbox(isOn: $autostash, label: tr("vcs.pull.autostash"))
                            PPInfoButton(text: tr("vcs.pull.autostashInfo"))
                        }
                    }
                }
            } footer: {
                PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
                PPButton(tr("vcs.pull"), icon: "arrow-down-to-line", kind: .primary, action: submit)
                    .keyboardShortcut(.defaultAction)
                    .disabled(target.isEmpty)
            }
            .task {
                if repo.branches == nil { await repo.loadBranches() }
                if target.isEmpty { target = defaultTarget() }
            }
        }

        private func defaultTarget() -> String {
            let remotes = repo.branches?.remote ?? []
            if let up = repo.status?.upstream, !(repo.status?.upstreamGone ?? false), remotes.contains(where: { $0.name == up }) { return up }
            if let cur = repo.currentBranch, let m = remotes.first(where: { $0.name.hasSuffix("/" + cur) }) { return m.name }
            return remotes.first?.name ?? ""
        }

        private func labeled<C: View>(_ label: String, @ViewBuilder _ c: () -> C) -> some View {
            HStack(spacing: M.md) {
                Text(label).foregroundColor(theme.textSecondary).frame(width: 80, alignment: .leading)
                c()
            }
        }

        private func close() { AppModel.shared.dialogs.dismiss(id) }

        private func submit() {
            guard !target.isEmpty else { return }
            VCSPrefs.pullMode = mode
            VCSPrefs.pullAutostash = autostash
            var args = VCSPullArgs(mode: mode, autostash: autostash ? true : nil)
            if target != repo.status?.upstream, let b = repo.branches?.remote.first(where: { $0.name == target }) {
                let remote = b.remote ?? String(target.split(separator: "/").first ?? "")
                args.remote = remote
                args.branch = String(target.dropFirst(remote.count + 1))
            }
            close()
            VCSRemoteActions.pull(repo: repo, args: args)
        }
    }
}

// MARK: - 推送

@MainActor
enum VCSPushDialog {
    static func present(repo: VCSRepo, branch: String? = nil) {
        let id = "vcs-push"
        AppModel.shared.dialogs.present(id: id) { Body(id: id, repo: repo, branch: branch) }
    }

    private struct Body: View {
        let id: String
        @ObservedObject var repo: VCSRepo
        let branch: String?
        @State private var remote = ""
        @State private var preview: VCSPushPreview?
        @State private var loading = true
        @State private var error: String?
        @State private var force = false
        @State private var tags = false
        @State private var tagsMode = "follow"
        @State private var setUpstream = true
        @State private var noVerify = false
        @Environment(\.theme) private var theme

        var body: some View {
            VCSLargeDialog(id: id, title: tr("vcs.push.title", branch ?? repo.headTitle), maxWidth: 680, maxHeight: 620) {
                VStack(alignment: .leading, spacing: 0) {
                    target
                    PPDivider(subtle: true)
                    commits
                    PPDivider(subtle: true)
                    options
                }
            } footer: {
                PPButton(tr("common.cancel")) { close() }.keyboardShortcut(.cancelAction)
                PPButton(force ? tr("vcs.push.force") : tr("vcs.push"), icon: "arrow-up", kind: force ? .danger : .primary, action: submit)
                    .keyboardShortcut(.defaultAction)
                    .disabled(preview == nil || remote.isEmpty)
            }
            .task { await load() }
        }

        private var target: some View {
            HStack(spacing: M.sm) {
                PPIcon("git-branch", size: M.icon, color: theme.iconSecondary)
                Text(preview?.branch ?? branch ?? repo.headTitle).font(.pp(.bodyStrong))
                PPIcon("arrow-right", size: M.iconSmall, color: theme.iconSecondary)
                if repo.remotes.count > 1 {
                    PPDropdown(options: repo.remotes.map { .init(value: $0.name, title: $0.name, icon: .builtin("cloud")) },
                               selection: Binding(get: { remote }, set: { remote = $0; Task { await loadPreview() } }),
                               size: .small, minWidth: 100)
                } else {
                    Text(remote).font(.pp(.bodyStrong))
                }
                Text(":").foregroundColor(theme.textTertiary)
                Text(preview.map { String($0.remoteRef.dropFirst("refs/heads/".count)) } ?? "").font(.pp(.bodyStrong))
                if preview?.newBranch == true { PPBadge(text: tr("vcs.push.newBranch"), tone: "success") }
                Spacer()
                if loading { PPSpinner(size: M.iconSmall) }
            }
            .padding(.horizontal, M.xl)
            .frame(height: 44)
        }

        @ViewBuilder private var commits: some View {
            if let error {
                VStack(alignment: .leading, spacing: M.md) {
                    PPAlert(tone: "danger", title: error)
                    if repo.remotes.isEmpty {
                        PPButton(tr("vcs.remote.add"), icon: "plus", size: .small) {
                            VCSRemoteActions.addRemote(repo: repo) { Task { await load() } }
                        }
                    }
                }
                .padding(M.xl)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else if let p = preview {
                VStack(alignment: .leading, spacing: 0) {
                    if p.behind > 0 {
                        PPAlert(tone: "warning", title: tr("vcs.push.behind", p.behind)) {
                            PPButton(tr("vcs.pull.ellipsis"), size: .small) {
                                close()
                                VCSPullDialog.present(repo: repo)
                            }
                        }
                        .padding(M.md)
                    }
                    if p.commits.isEmpty {
                        PPEmptyState(icon: "circle-check", title: p.newBranch ? tr("vcs.push.newBranchNoCommits") : tr("vcs.push.nothing"))
                    } else {
                        HStack {
                            Text(tr("vcs.push.commits", p.commits.count) + (p.truncated ? "+" : ""))
                                .font(.pp(.label)).foregroundColor(theme.textSecondary)
                            Spacer()
                        }
                        .padding(.horizontal, M.xl)
                        .frame(height: M.rowHeightCompact)
                        ScrollView {
                            LazyVStack(alignment: .leading, spacing: 0) {
                                ForEach(p.commits) { c in
                                    HStack(spacing: M.md) {
                                        Text(c.short).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary).frame(width: 64, alignment: .leading)
                                        Text(c.subject).lineLimit(1)
                                        Spacer(minLength: M.md)
                                        Text(c.author).foregroundColor(theme.textSecondary).lineLimit(1)
                                        Text(VCSDate.short(c.parsedDate)).foregroundColor(theme.textTertiary).frame(width: 110, alignment: .trailing)
                                    }
                                    .padding(.horizontal, M.xl)
                                    .frame(height: M.rowHeightCompact)
                                }
                            }
                        }
                    }
                }
                .frame(maxHeight: .infinity, alignment: .top)
            } else {
                Color.clear.frame(maxHeight: .infinity)
            }
        }

        private var options: some View {
            VStack(alignment: .leading, spacing: M.sm) {
                HStack(spacing: M.lg) {
                    PPCheckbox(isOn: $tags, label: tr("vcs.push.tags"))
                    if tags {
                        PPSegmented(options: [("follow", tr("vcs.push.tagsFollow")), ("all", tr("vcs.push.tagsAll"))],
                                    selection: $tagsMode, size: .small)
                    }
                    Spacer()
                }
                if offerUpstream {
                    PPCheckbox(isOn: $setUpstream, label: tr("vcs.push.setUpstream"))
                }
                HStack(spacing: M.sm) {
                    PPCheckbox(isOn: $force, label: tr("vcs.push.forceLease"))
                    PPInfoButton(text: tr("vcs.push.forceInfo"))
                    Spacer().frame(width: M.lg)
                    PPCheckbox(isOn: $noVerify, label: tr("vcs.commit.noVerify"))
                }
            }
            .padding(.horizontal, M.xl)
            .padding(.vertical, M.md)
        }

        /// 新分支或还没有上游时才给「设置上游」
        private var offerUpstream: Bool { preview?.newBranch == true || preview?.setUpstream == true }

        private func load() async {
            await repo.loadRemotes()
            if remote.isEmpty {
                let up = repo.status?.upstream.flatMap { u in repo.remotes.first { u.hasPrefix($0.name + "/") }?.name }
                remote = up ?? repo.remotes.first { $0.name == "origin" }?.name ?? repo.remotes.first?.name ?? ""
            }
            await loadPreview()
        }

        private func loadPreview() async {
            loading = true
            do {
                let p = try await repo.api().vcsPushPreview(projectId: repo.projectId, root: repo.root,
                                                            VCSPushArgs(remote: remote.isEmpty ? nil : remote, branch: branch))
                preview = p
                if remote.isEmpty { remote = p.remote }
                setUpstream = p.setUpstream || p.newBranch
                error = nil
            } catch {
                self.error = error.localizedDescription
                preview = nil
            }
            loading = false
        }

        private func close() { AppModel.shared.dialogs.dismiss(id) }

        private func submit() {
            guard preview != nil else { return }
            var args = VCSPushArgs(remote: remote.isEmpty ? nil : remote, branch: branch)
            if offerUpstream && setUpstream { args.setUpstream = true }
            if tags {
                args.tags = true
                args.tagsMode = tagsMode
            }
            if noVerify { args.noVerify = true }
            close()
            if force {
                args.force = true
                VCSRemoteActions.confirmForce(repo: repo, args: args)
            } else {
                VCSRemoteActions.push(repo: repo, args: args)
            }
        }
    }
}

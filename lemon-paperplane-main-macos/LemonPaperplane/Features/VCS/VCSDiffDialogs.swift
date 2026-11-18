import PaperplaneKit
import SwiftUI

// 大对话框：差异浏览（文件列表 + 差异）、文件历史、比较两个引用。

/// 文件列表 + 差异
struct VCSDiffBrowser: View {
    let repo: VCSRepo
    let files: [VCSFileChange]
    let target: (VCSFileChange) -> VCSDiffTarget
    @State var selected: String?
    @StateObject private var diff = VCSDiffModel()
    @State private var listWidth: CGFloat = 300
    @Environment(\.theme) private var theme

    var body: some View {
        Group {
            if files.count > 1 {
                PPSplitView(leftWidth: $listWidth, minLeft: 200, maxLeft: 520) {
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 1) {
                            ForEach(files) { f in
                                VCSFileLine(file: f, selected: f.path == selected) { select(f) }
                            }
                        }
                        .padding(M.xs)
                    }
                    .background(theme.bgContent)
                } right: {
                    VCSDiffView(model: diff, changes: repo.changes)
                }
            } else if files.isEmpty {
                PPEmptyState(icon: "file-diff", title: tr("vcs.diff.noFiles"))
            } else {
                VCSDiffView(model: diff, changes: repo.changes)
            }
        }
        .onAppear {
            if let f = files.first(where: { $0.path == selected }) ?? files.first { select(f) }
        }
    }

    private func select(_ f: VCSFileChange) {
        selected = f.path
        diff.set(target(f), repo: repo)
    }
}

@MainActor
enum VCSDiffDialog {
    static func present(repo: VCSRepo, title: String, files: [VCSFileChange], selected: String?,
                        target: @escaping (VCSFileChange) -> VCSDiffTarget) {
        let id = "vcs-diff"
        AppModel.shared.dialogs.present(id: id) {
            VCSLargeDialog(id: id, title: title) {
                VCSDiffBrowser(repo: repo, files: files, target: target, selected: selected)
            }
        }
    }

    /// 工作区文件（变更列表里「显示差异」）
    static func presentWorking(repo: VCSRepo, file: VCSFileChange, side: VCSChangeSide) {
        let t = repo.changes.target(VCSChangeSelection(path: file.path, side: side), file: file)
        present(repo: repo, title: file.path, files: [file], selected: file.path) { _ in t }
    }

    static func presentCommit(repo: VCSRepo, detail: VCSCommitDetail, file: VCSFileChange, parent: Int) {
        let c = detail.commit
        present(repo: repo, title: "\(c.short)  \(c.subject)", files: detail.files, selected: file.path) { f in
            VCSDiffTarget(path: f.path, oldPath: f.oldPath, kind: .commit(c.hash, parent: parent), status: f.status)
        }
    }

    static func presentStash(repo: VCSRepo, stash: VCSStash, show: VCSStashShow, file: VCSFileChange) {
        present(repo: repo, title: "stash@{\(stash.index)}  \(stash.message)", files: show.files, selected: file.path) { f in
            VCSDiffTarget(path: f.path, oldPath: f.oldPath, kind: .commit(show.hash, parent: 1), status: f.status)
        }
    }
}

// MARK: - 文件历史

@MainActor
enum VCSFileHistoryDialog {
    static func present(repo: VCSRepo, path: String) {
        let id = "vcs-file-history"
        AppModel.shared.dialogs.present(id: id) {
            VCSLargeDialog(id: id, title: tr("vcs.fileHistory.title", path)) {
                Body(repo: repo, path: path, id: id)
            }
        }
    }

    private struct Body: View {
        let repo: VCSRepo
        let path: String
        let id: String
        @State private var commits: [VCSCommit] = []
        @State private var hasMore = false
        @State private var error: String?
        @State private var loading = true
        @State private var selected: String?
        @State private var listWidth: CGFloat = 420
        @StateObject private var diff = VCSDiffModel()
        @Environment(\.theme) private var theme

        var body: some View {
            PPSplitView(leftWidth: $listWidth, minLeft: 280, maxLeft: 640) {
                Group {
                    if let error {
                        PPAlert(tone: "danger", title: error).padding(M.md).frame(maxHeight: .infinity, alignment: .top)
                    } else if commits.isEmpty && !loading {
                        PPEmptyState(icon: "history", title: tr("vcs.fileHistory.empty"))
                    } else {
                        ScrollView {
                            LazyVStack(alignment: .leading, spacing: 0) {
                                ForEach(commits.indices, id: \.self) { i in
                                    row(commits[i])
                                        .onAppear { if i == commits.count - 1 { loadMore() } }
                                }
                                if loading { PPSpinner(size: M.iconSmall).frame(maxWidth: .infinity).padding(M.md) }
                            }
                            .padding(M.xs)
                        }
                    }
                }
                .background(theme.bgContent)
            } right: {
                VCSDiffView(model: diff, changes: repo.changes)
            }
            .task { await load(skip: 0) }
        }

        private func load(skip: Int) async {
            loading = true
            do {
                let log = try await repo.api().vcsFileHistory(projectId: repo.projectId, root: repo.root,
                                                              VCSFileHistoryArgs(path: path, limit: 100, skip: skip > 0 ? skip : nil))
                commits = skip == 0 ? log.commits : commits + log.commits
                hasMore = log.hasMore
                if selected == nil, let f = commits.first { select(f) }
            } catch {
                self.error = error.localizedDescription
            }
            loading = false
        }

        private func loadMore() {
            guard hasMore, !loading else { return }
            Task { await load(skip: commits.count) }
        }

        private func select(_ c: VCSCommit) {
            selected = c.hash
            diff.set(VCSDiffTarget(path: c.path ?? path, kind: .commit(c.hash, parent: 1), status: c.pathStatus), repo: repo)
        }

        private func row(_ c: VCSCommit) -> some View {
            VCSTableRow(selected: c.hash == selected, onTap: { select(c) }, onDoubleTap: nil) {
                HStack(spacing: M.sm) {
                    if let s = c.pathStatus { VCSStatusLetter(status: s) }
                    VStack(alignment: .leading, spacing: 0) {
                        Text(c.subject).lineLimit(1)
                        HStack(spacing: M.sm) {
                            Text(c.short).font(.pp(.monoSmall))
                            Text(c.author).lineLimit(1)
                            Text(VCSDate.short(c.parsedDate))
                            if let p = c.path, p != path { Text(p).lineLimit(1).truncationMode(.middle) }
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
                    .item(tr("vcs.copyHash"), icon: "copy") { Actions.copy(c.hash) },
                    .item(tr("vcs.fileHistory.compareWithCurrent"), icon: "git-compare") {
                        diff.set(VCSDiffTarget(path: path, oldPath: c.path != path ? c.path : nil, kind: .range(base: c.hash, head: nil)), repo: repo)
                    },
                    .item(tr("vcs.showInLog"), icon: "git-graph") {
                        AppModel.shared.dialogs.dismiss(id)
                        repo.session.page = .log
                        repo.log.focus(hash: c.hash)
                    },
                    .divider,
                    .item(tr("vcs.checkoutFileVersion"), icon: "rotate-ccw") {
                        VCSHistoryActions.restoreFile(repo: repo, path: c.path ?? path, ref: c.hash, short: c.short)
                    },
                ]
            }
        }
    }
}

// MARK: - 比较

@MainActor
enum VCSCompareDialog {
    /// base 与 head（nil 为当前分支）之间：双方独有的提交 + merge-base 到 head 的文件变化
    static func present(repo: VCSRepo, base: String, head: String?, title: String) {
        let id = "vcs-compare"
        AppModel.shared.dialogs.present(id: id) {
            VCSLargeDialog(id: id, title: title) {
                Body(repo: repo, base: base, head: head)
            }
        }
    }

    private struct Body: View {
        let repo: VCSRepo
        let base: String
        let head: String?
        @State private var result: VCSCompare?
        @State private var error: String?
        @State private var tab = "commits"
        @State private var selected: String?
        @Environment(\.theme) private var theme

        private var headName: String { head ?? repo.headTitle }

        var body: some View {
            VStack(spacing: 0) {
                HStack {
                    PPSegmented(options: [("commits", tr("vcs.compare.commits", (result?.ahead.count ?? 0) + (result?.behind.count ?? 0))),
                                          ("files", tr("vcs.compare.files", result?.files.count ?? 0))], selection: $tab, size: .small)
                    Spacer()
                    if result?.truncated == true { PPBadge(text: tr("vcs.compare.truncated"), tone: "warning") }
                }
                .padding(.horizontal, M.lg)
                .frame(height: M.toolbarHeight)
                PPDivider(subtle: true)
                if let error {
                    PPAlert(tone: "danger", title: error).padding(M.md).frame(maxHeight: .infinity, alignment: .top)
                } else if let r = result {
                    if tab == "files" {
                        VCSDiffBrowser(repo: repo, files: r.files, target: { f in
                            VCSDiffTarget(path: f.path, oldPath: f.oldPath, kind: .range(base: r.mergeBase ?? base, head: head ?? "HEAD"), status: f.status)
                        }, selected: nil)
                    } else {
                        commits(r)
                    }
                } else {
                    PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .task {
                do {
                    result = try await repo.api().vcsCompare(projectId: repo.projectId, root: repo.root, VCSCompareArgs(base: base, head: head))
                } catch {
                    self.error = error.localizedDescription
                }
            }
        }

        private func commits(_ r: VCSCompare) -> some View {
            HStack(spacing: 0) {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        VCSGroupHeader(title: tr("vcs.compare.onlyIn", headName), count: r.ahead.count)
                        ForEach(r.ahead) { c in commitRow(c) }
                        VCSGroupHeader(title: tr("vcs.compare.onlyIn", base.count == 40 ? String(base.prefix(8)) : base), count: r.behind.count)
                            .padding(.top, M.md)
                        ForEach(r.behind) { c in commitRow(c) }
                    }
                    .padding(M.xs)
                }
                .frame(maxWidth: .infinity)
                PPDivider(vertical: true)
                Group {
                    if let h = selected {
                        VCSCommitDetailView(repo: repo, hash: h).id(h)
                    } else {
                        PPEmptyState(icon: "git-commit-horizontal", title: tr("vcs.log.selectCommit"))
                    }
                }
                .frame(width: 400)
            }
        }

        private func commitRow(_ c: VCSCommit) -> some View {
            VCSTableRow(selected: c.hash == selected, onTap: { selected = c.hash }, onDoubleTap: nil) {
                HStack(spacing: M.md) {
                    Text(c.short).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary).frame(width: 64, alignment: .leading)
                    Text(c.subject).lineLimit(1)
                    Spacer(minLength: M.sm)
                    Text(c.author).foregroundColor(theme.textSecondary).lineLimit(1)
                    Text(VCSDate.short(c.parsedDate)).foregroundColor(theme.textTertiary).frame(width: 110, alignment: .trailing)
                }
                .padding(.horizontal, M.sm)
            }
            .ppContextMenu { VCSCommitMenu.entries(repo: repo, commits: [c]) }
        }
    }
}

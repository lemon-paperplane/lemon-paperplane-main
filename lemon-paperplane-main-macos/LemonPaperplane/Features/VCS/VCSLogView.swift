import AppKit
import PaperplaneKit
import SwiftUI

// 日志：提交图、引用徽标、按分支 / 作者 / 关键词 / 路径过滤、分页、右侧提交详情。

@MainActor
final class VCSLogModel: ObservableObject {
    unowned let repo: VCSRepo

    @Published private(set) var commits: [VCSCommit] = []
    @Published private(set) var hasMore = false
    @Published private(set) var hasGraph = true
    @Published private(set) var loading = false
    @Published private(set) var loadingMore = false
    @Published private(set) var error: String?

    /// "--all" 全部分支、"" 当前分支、其它为分支名
    @Published var branch: String = VCSPrefs.logAllBranches ? "--all" : ""
    @Published var text = ""
    @Published var author = ""
    @Published var path = ""

    @Published var selected: Set<String> = []
    @Published var primary: String?
    @Published var detailWidth: CGFloat = 400

    private var loadedRevision = -1
    private var anchor: String?
    private var task: Task<Void, Never>?
    let pageSize = 200

    init(repo: VCSRepo) { self.repo = repo }

    private func args(skip: Int) -> VCSLogArgs {
        var a = VCSLogArgs(ref: branch.isEmpty ? nil : branch, skip: skip > 0 ? skip : nil, limit: pageSize)
        let t = text.trimmingCharacters(in: .whitespaces)
        if !t.isEmpty { a.grep = t }
        let au = author.trimmingCharacters(in: .whitespaces)
        if !au.isEmpty { a.author = au }
        let p = path.trimmingCharacters(in: .whitespaces)
        if !p.isEmpty { a.path = p }
        return a
    }

    /// 仓库变化后（或首次）重新加载第一页
    func ensureLoaded() {
        if loadedRevision != repo.revision { reload() }
    }

    func reload() {
        loadedRevision = repo.revision
        task?.cancel()
        loading = commits.isEmpty
        task = Task {
            do {
                let log = try await repo.api().vcsLog(projectId: repo.projectId, root: repo.root, args(skip: 0))
                guard !Task.isCancelled else { return }
                commits = log.commits
                hasMore = log.hasMore
                hasGraph = (log.graph ?? true) && log.commits.contains { $0.graph != nil }
                error = nil
                // 关键词像提交哈希且没匹配到信息：直接显示那个提交
                let t = text.trimmingCharacters(in: .whitespaces)
                if log.commits.isEmpty, t.count >= 7, t.count <= 40, t.allSatisfy(\.isHexDigit) { primary = t }
                if let p = primary, !log.commits.contains(where: { $0.hash == p }) {
                    // 选中的提交不在第一页（或已不存在）时保持详情，但列表里不再高亮
                    selected = selected.filter { h in log.commits.contains { $0.hash == h } }
                }
            } catch is CancellationError {
            } catch {
                self.error = error.localizedDescription
            }
            loading = false
        }
    }

    func loadMore() {
        guard hasMore, !loadingMore, !loading else { return }
        loadingMore = true
        let skip = commits.count
        Task {
            if let log = try? await repo.api().vcsLog(projectId: repo.projectId, root: repo.root, args(skip: skip)),
               commits.count == skip {
                commits += log.commits
                hasMore = log.hasMore
            }
            loadingMore = false
        }
    }

    /// 过滤条件变化：稍等再查
    func filtersChanged() {
        task?.cancel()
        task = Task {
            try? await Task.sleep(nanoseconds: 350_000_000)
            guard !Task.isCancelled else { return }
            reload()
        }
    }

    func click(_ c: VCSCommit) {
        if VCSModifiers.command {
            if selected.contains(c.hash) { selected.remove(c.hash) } else { selected.insert(c.hash) }
            primary = c.hash
        } else if VCSModifiers.shift, let a = anchor, let i = commits.firstIndex(where: { $0.hash == a }),
                  let j = commits.firstIndex(where: { $0.hash == c.hash }) {
            selected = Set(commits[min(i, j)...max(i, j)].map(\.hash))
            primary = c.hash
        } else {
            selected = [c.hash]
            primary = c.hash
            anchor = c.hash
        }
    }

    /// 选中的提交（列表顺序：新的在前）
    var selectedCommits: [VCSCommit] { commits.filter { selected.contains($0.hash) } }

    func focus(hash: String) {
        if commits.contains(where: { $0.hash == hash }) {
            selected = [hash]
            anchor = hash
        }
        primary = hash
    }
}

struct VCSLogPage: View {
    @ObservedObject var repo: VCSRepo
    @ObservedObject var log: VCSLogModel
    @Environment(\.theme) private var theme

    init(repo: VCSRepo) {
        self.repo = repo
        self._log = ObservedObject(wrappedValue: repo.log)
    }

    var body: some View {
        VStack(spacing: 0) {
            filterBar
            PPDivider(subtle: true)
            HStack(spacing: 0) {
                list.frame(maxWidth: .infinity)
                VCSResizeHandle(width: $log.detailWidth, min: 280, max: 720)
                Group {
                    if let h = log.primary {
                        VCSCommitDetailView(repo: repo, hash: h) { log.focus(hash: $0) }
                            .id(h)
                    } else {
                        PPEmptyState(icon: "git-commit-horizontal", title: tr("vcs.log.selectCommit"))
                    }
                }
                .frame(width: log.detailWidth)
            }
        }
        .onAppear {
            log.ensureLoaded()
            if repo.branches == nil { Task { await repo.loadBranches() } }
        }
        .onChange(of: repo.revision) { _ in log.ensureLoaded() }
    }

    // MARK: 过滤

    private var branchOptions: [PPDropdown<String>.Option] {
        var o: [PPDropdown<String>.Option] = [
            .init(value: "--all", title: tr("vcs.log.allBranches"), icon: .builtin("git-fork")),
            .init(value: "", title: tr("vcs.log.currentBranch"), icon: .builtin("circle-dot")),
        ]
        if let b = repo.branches {
            o += b.local.map { .init(value: $0.name, title: $0.name, icon: .builtin("git-branch")) }
            o += b.remote.map { .init(value: $0.name, title: $0.name, icon: .builtin("cloud")) }
        }
        if !o.contains(where: { $0.value == log.branch }) { o.append(.init(value: log.branch, title: log.branch)) }
        return o
    }

    private var filterBar: some View {
        HStack(spacing: M.sm) {
            PPDropdown(options: branchOptions, selection: Binding(get: { log.branch }, set: { v in
                log.branch = v
                if v == "--all" || v.isEmpty { VCSPrefs.logAllBranches = v == "--all" }
                log.reload()
            }), size: .small, minWidth: 150, maxWidth: 220)
            PPTextField(tr("vcs.log.searchText"), text: Binding(get: { log.text }, set: { log.text = $0; log.filtersChanged() }),
                        icon: "search", clearable: true, height: M.controlHeightSmall)
                .frame(minWidth: 160, maxWidth: 300)
            PPTextField(tr("vcs.log.author"), text: Binding(get: { log.author }, set: { log.author = $0; log.filtersChanged() }),
                        icon: "user", clearable: true, height: M.controlHeightSmall)
                .frame(width: 150)
            PPTextField(tr("vcs.log.path"), text: Binding(get: { log.path }, set: { log.path = $0; log.filtersChanged() }),
                        icon: "folder", clearable: true, height: M.controlHeightSmall)
                .frame(width: 170)
            Spacer(minLength: 0)
            if log.loading || log.loadingMore { PPSpinner(size: M.iconSmall) }
            Text(tr("vcs.log.count", log.commits.count) + (log.hasMore ? "+" : ""))
                .font(.pp(.small)).foregroundColor(theme.textTertiary)
            PPIconButton("refresh-cw", help: tr("vcs.refresh"), size: M.iconButtonSmall, iconSize: M.iconSmall) { log.reload() }
        }
        .padding(.horizontal, M.md)
        .frame(height: M.toolbarHeight)
    }

    // MARK: 列表

    private static let rowH: CGFloat = 24
    private let geo = CommitGraphGeometry(laneWidth: 12, rowHeight: VCSLogPage.rowH, leftPadding: 6)

    @ViewBuilder private var list: some View {
        if let e = log.error, log.commits.isEmpty {
            PPAlert(tone: "danger", title: tr("vcs.log.failed"), message: e) {
                PPButton(tr("connection.retry"), size: .small) { log.reload() }
            }
            .padding(M.md)
            .frame(maxHeight: .infinity, alignment: .top)
        } else if log.commits.isEmpty && !log.loading {
            PPEmptyState(icon: "git-commit-horizontal", title: tr("vcs.log.empty"))
        } else {
            let graphWidth = log.hasGraph ? min(geo.width(for: log.commits.compactMap(\.graph)), 220) : 0
            VStack(spacing: 0) {
                header(graphWidth: graphWidth)
                PPDivider(subtle: true)
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(log.commits.indices, id: \.self) { i in
                            let c = log.commits[i]
                            VCSLogRow(repo: repo, log: log, commit: c, previous: i > 0 ? log.commits[i - 1].graph : nil,
                                      geo: geo, graphWidth: graphWidth, rowHeight: Self.rowH)
                                .onAppear { if i >= log.commits.count - 20 { log.loadMore() } }
                        }
                        if log.loadingMore {
                            PPSpinner(size: M.iconSmall).frame(maxWidth: .infinity).padding(M.md)
                        }
                    }
                }
            }
        }
    }

    private func header(graphWidth: CGFloat) -> some View {
        HStack(spacing: 0) {
            Color.clear.frame(width: graphWidth)
            Text(tr("vcs.log.subject")).frame(maxWidth: .infinity, alignment: .leading).padding(.leading, M.sm)
            Text(tr("vcs.log.authorColumn")).frame(width: 130, alignment: .leading)
            Text(tr("vcs.log.date")).frame(width: 118, alignment: .leading)
            Text(tr("vcs.log.hash")).frame(width: 70, alignment: .leading)
        }
        .font(.pp(.label))
        .foregroundColor(theme.textSecondary)
        .frame(height: M.rowHeightCompact)
        .background(theme.bgSubtle)
    }
}

/// 日志里的一行
private struct VCSLogRow: View {
    let repo: VCSRepo
    @ObservedObject var log: VCSLogModel
    let commit: VCSCommit
    let previous: VCSGraphRow?
    let geo: CommitGraphGeometry
    let graphWidth: CGFloat
    let rowHeight: CGFloat
    @State private var hover = false
    @Environment(\.theme) private var theme

    var body: some View {
        let selected = log.selected.contains(commit.hash)
        let isHead = commit.refs.contains { $0.kind == "head" }
        HStack(spacing: 0) {
            if graphWidth > 0 {
                VCSGraphCell(row: commit.graph, previous: previous, geo: geo, isHead: isHead, merge: commit.parents.count > 1)
                    .frame(width: graphWidth, height: rowHeight)
                    .clipped()
            }
            HStack(spacing: M.xs) {
                let refs = commit.refs.filter { $0.kind != "head" }
                ForEach(Array(refs.prefix(3).enumerated()), id: \.offset) { _, r in
                    VCSRefBadge(ref: r, current: isHead && r.kind == "local")
                }
                if refs.count > 3 {
                    Text("+\(refs.count - 3)").font(.pp(.caption)).foregroundColor(theme.textTertiary)
                        .help(refs.dropFirst(3).map(\.name).joined(separator: "\n"))
                }
                Text(commit.subject)
                    .font(isHead ? .pp(.bodyStrong) : .pp(.body))
                    .lineLimit(1)
            }
            .padding(.leading, M.sm)
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(commit.author).lineLimit(1).foregroundColor(theme.textSecondary).frame(width: 130, alignment: .leading)
            Text(VCSDate.short(commit.parsedDate)).lineLimit(1).foregroundColor(theme.textSecondary).frame(width: 118, alignment: .leading)
                .help(VCSDate.full(commit.parsedDate))
            Text(commit.short).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary).frame(width: 70, alignment: .leading)
        }
        .font(.pp(.body))
        .frame(height: rowHeight)
        .background(selected ? theme.bgSelected : (hover ? theme.bgHover : .clear))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture { log.click(commit) }
        .ppContextMenu {
            if !log.selected.contains(commit.hash) { log.click(commit) }
            return VCSCommitMenu.entries(repo: repo, commits: log.selectedCommits.isEmpty ? [commit] : log.selectedCommits)
        }
    }
}

/// 提交图的一格（内核算好的车道与连线，CommitGraphGeometry 换算坐标）
struct VCSGraphCell: View {
    let row: VCSGraphRow?
    let previous: VCSGraphRow?
    let geo: CommitGraphGeometry
    var isHead = false
    var merge = false
    @Environment(\.theme) private var theme

    var body: some View {
        Canvas { ctx, _ in
            guard let row else { return }
            let r = geo.row(row, previous: previous)
            for s in r.segments {
                var p = Path()
                p.move(to: s.from)
                p.addLine(to: s.to)
                ctx.stroke(p, with: .color(theme.graphLane(s.color)), style: StrokeStyle(lineWidth: 1.5, lineCap: .round))
            }
            let radius: CGFloat = merge ? 3.5 : 4
            let rect = CGRect(x: r.node.x - radius, y: r.node.y - radius, width: radius * 2, height: radius * 2)
            let color = theme.graphLane(r.nodeColor)
            if isHead {
                ctx.fill(Path(ellipseIn: rect), with: .color(theme.bgContent))
                ctx.stroke(Path(ellipseIn: rect), with: .color(color), lineWidth: 1.8)
            } else {
                ctx.fill(Path(ellipseIn: rect), with: .color(color))
            }
        }
    }
}

/// 右侧面板的拖动宽度把手
struct VCSResizeHandle: View {
    @Binding var width: CGFloat
    var min: CGFloat
    var max: CGFloat
    @State private var start: CGFloat?
    @Environment(\.theme) private var theme

    var body: some View {
        Rectangle().fill(theme.border).frame(width: 1)
            .overlay(
                Color.clear.frame(width: 7).contentShape(Rectangle())
                    .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
                    .gesture(DragGesture(minimumDistance: 1).onChanged { v in
                        let s = start ?? width
                        if start == nil { start = width }
                        width = Swift.min(Swift.max(s - v.translation.width, min), max)
                    }.onEnded { _ in start = nil })
            )
    }
}

// MARK: - 提交详情

struct VCSCommitDetailView: View {
    let repo: VCSRepo
    let hash: String
    var onSelectParent: ((String) -> Void)?
    @State private var detail: VCSCommitDetail?
    @State private var error: String?
    @State private var parent = 1
    @State private var containing: VCSContainingBranches?
    @State private var loadingContaining = false
    @Environment(\.theme) private var theme

    var body: some View {
        Group {
            if let d = detail {
                content(d)
            } else if let error {
                PPAlert(tone: "danger", title: tr("vcs.log.detailFailed"), message: error).padding(M.md)
                    .frame(maxHeight: .infinity, alignment: .top)
            } else {
                PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .task(id: "\(hash)#\(parent)") { await load() }
    }

    private func load() async {
        do {
            detail = try await repo.api().vcsShow(projectId: repo.projectId, root: repo.root, hash: hash, parent: parent > 1 ? parent : nil)
            error = nil
        } catch is CancellationError {
        } catch {
            self.error = error.localizedDescription
        }
        if containing == nil && !loadingContaining {
            loadingContaining = true
            containing = try? await repo.api().vcsContainingBranches(projectId: repo.projectId, root: repo.root, hash: hash)
            loadingContaining = false
        }
    }

    private func content(_ d: VCSCommitDetail) -> some View {
        let c = d.commit
        let message = d.message ?? ([c.subject] + (d.body.map { ["", $0] } ?? [])).joined(separator: "\n")
        let lines = message.split(separator: "\n", maxSplits: 1, omittingEmptySubsequences: false)
        let body = lines.count > 1 ? String(lines[1]).trimmingCharacters(in: .whitespacesAndNewlines) : ""
        return ScrollView {
            VStack(alignment: .leading, spacing: M.md) {
                Text(String(lines.first ?? "")).font(.pp(.title)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                if !body.isEmpty {
                    Text(body).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        .foregroundColor(theme.textSecondary)
                }
                if !c.refs.isEmpty {
                    FlowRow(spacing: M.xs) { ForEach(Array(c.refs.enumerated()), id: \.offset) { _, r in VCSRefBadge(ref: r) } }
                }
                PPDivider(subtle: true)
                meta(d)
                PPDivider(subtle: true)
                files(d)
            }
            .padding(M.lg)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func meta(_ d: VCSCommitDetail) -> some View {
        let c = d.commit
        return VStack(alignment: .leading, spacing: M.sm) {
            row(tr("vcs.log.authorColumn")) {
                Text(c.author + (c.email.map { " <\($0)>" } ?? "")).textSelection(.enabled).lineLimit(2)
            }
            row(tr("vcs.log.date")) { Text(VCSDate.full(c.parsedDate)) }
            if let cm = d.committer, cm != c.author || d.committerDate != c.date {
                row(tr("vcs.log.committer")) {
                    Text(cm + "  " + VCSDate.full(d.committerDate)).lineLimit(2).foregroundColor(theme.textSecondary)
                }
            }
            row(tr("vcs.log.hash")) {
                HStack(spacing: M.xs) {
                    Text(c.hash).font(.pp(.monoSmall)).textSelection(.enabled).lineLimit(1).truncationMode(.middle)
                    PPIconButton("copy", help: tr("vcs.copyHash"), size: 20, iconSize: 12) { Actions.copy(c.hash) }
                }
            }
            if !c.parents.isEmpty {
                row(tr("vcs.log.parents")) {
                    HStack(spacing: M.sm) {
                        ForEach(c.parents, id: \.self) { p in
                            PPLink(title: String(p.prefix(8)), mono: true) { onSelectParent?(p) }
                        }
                    }
                }
            }
            row(tr("vcs.log.containedIn")) {
                if let cb = containing {
                    if cb.refs.isEmpty {
                        Text(tr("vcs.log.noBranches")).foregroundColor(theme.textTertiary)
                    } else {
                        FlowRow(spacing: M.xs) {
                            ForEach(Array(cb.refs.prefix(30).enumerated()), id: \.offset) { _, r in VCSRefBadge(ref: r) }
                            if cb.refs.count > 30 || cb.truncated {
                                Text("…").foregroundColor(theme.textTertiary)
                            }
                        }
                    }
                } else {
                    PPSpinner(size: 12)
                }
            }
        }
        .font(.pp(.small))
    }

    private func row<V: View>(_ key: String, @ViewBuilder _ value: () -> V) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: M.md) {
            Text(key).foregroundColor(theme.textTertiary).frame(width: 64, alignment: .leading)
            value()
            Spacer(minLength: 0)
        }
    }

    private func files(_ d: VCSCommitDetail) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: M.sm) {
                Text(tr("vcs.log.files", d.files.count)).font(.pp(.label)).foregroundColor(theme.textSecondary)
                Spacer()
                if d.commit.parents.count > 1 {
                    PPSegmented(options: d.commit.parents.indices.map { ($0 + 1, tr("vcs.log.parentN", $0 + 1)) },
                                selection: $parent, size: .small)
                }
            }
            ForEach(d.files) { f in
                VCSFileLine(file: f) {
                    VCSDiffDialog.presentCommit(repo: repo, detail: d, file: f, parent: parent)
                }
                .ppContextMenu {
                    [
                        .item(tr("vcs.showDiff"), icon: "file-diff") {
                            VCSDiffDialog.presentCommit(repo: repo, detail: d, file: f, parent: parent)
                        },
                        .item(tr("vcs.fileHistory"), icon: "history") { VCSFileHistoryDialog.present(repo: repo, path: f.path) },
                        .item(tr("vcs.checkoutFileVersion"), icon: "rotate-ccw") {
                            VCSHistoryActions.restoreFile(repo: repo, path: f.path, ref: d.commit.hash, short: d.commit.short)
                        },
                        .divider,
                        .item(tr("vcs.copyPath"), icon: "copy") { Actions.copy(f.path) },
                    ]
                }
            }
        }
    }
}

/// 文件行（提交详情、储藏、比较里用）：状态字母、文件名、目录、增删行数
struct VCSFileLine: View {
    let file: VCSFileChange
    var selected = false
    let action: () -> Void
    @State private var hover = false
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.sm) {
            VCSStatusLetter(status: file.status)
            Text(VCSPath.name(file.path)).foregroundColor(VCSStyle.color(file.status, theme: theme)).lineLimit(1)
            Text(file.oldPath.map { "← " + $0 } ?? VCSPath.dir(file.path))
                .font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.middle)
            Spacer(minLength: M.xs)
            if file.binary {
                PPIcon("binary", size: 12, color: theme.textTertiary)
            } else {
                VCSLineStats(additions: file.additions, deletions: file.deletions)
            }
        }
        .font(.pp(.body))
        .padding(.horizontal, M.sm)
        .frame(height: M.rowHeightCompact)
        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(selected ? theme.bgSelected : (hover ? theme.bgHover : .clear)))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture(perform: action)
        .help(file.path)
    }
}

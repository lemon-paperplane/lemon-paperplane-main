import AppKit
import PaperplaneKit
import SwiftUI

// 只读差异视图：并排 / 统一两种模式、行号、增删着色、块间折叠的上下文（可展开）、二进制与过大文件占位、
// 图片左右对比、上一处 / 下一处变更、按块暂存 / 取消暂存 / 丢弃 / 勾选提交。绝不编辑文件内容。

/// 要看的差异
struct VCSDiffTarget: Hashable {
    enum Kind: Hashable {
        /// 工作区：mode 为 head | staged | unstaged
        case working(String)
        /// 提交对第 parent 个父提交（储藏也是提交）
        case commit(String, parent: Int)
        /// base → head（head 为空表示工作区）
        case range(base: String, head: String?)
    }

    var path: String
    var oldPath: String?
    var kind: Kind
    /// 列表里的状态字母（A/D/M…），只作提示
    var status: String?

    func args(context: Int, ignoreWhitespace: Bool, noLimit: Bool) -> VCSDiffArgs {
        var a = VCSDiffArgs(path: path, oldPath: oldPath, context: context)
        switch kind {
        case .working(let mode): a.mode = mode
        case .commit(let h, let p):
            a.mode = VCSDiffMode.commit
            a.commit = h
            if p > 1 { a.parent = p }
        case .range(let b, let h):
            a.mode = VCSDiffMode.range
            a.base = b
            a.head = h
        }
        if ignoreWhitespace { a.ignoreWhitespace = true }
        if noLimit { a.noLimit = true }
        return a
    }

    /// 旧侧版本（fileContent 的 ref；nil 表示不存在）
    var oldRef: String? {
        switch kind {
        case .working(let m): return m == VCSDiffMode.unstaged ? ":" : "HEAD"
        case .commit(let h, let p): return "\(h)^\(p)"
        case .range(let b, _): return b
        }
    }

    /// 新侧版本（"" 为工作区）
    var newRef: String {
        switch kind {
        case .working(let m): return m == VCSDiffMode.staged ? ":" : ""
        case .commit(let h, _): return h
        case .range(_, let h): return h ?? ""
        }
    }

    var isWorking: Bool { if case .working = kind { return true } else { return false } }
}

/// 块上的操作（由宿主决定）
enum VCSHunkMode: Equatable {
    case none
    /// 非暂存区模式：勾选块是否提交 + 丢弃块
    case include
    /// 暂存区模式未暂存一侧：暂存块 + 丢弃块
    case stage
    /// 暂存区模式已暂存一侧：取消暂存块
    case unstage
}

@MainActor
final class VCSDiffModel: ObservableObject {
    @Published private(set) var target: VCSDiffTarget?
    @Published private(set) var diff: VCSDiff? { didSet { version += 1 } }
    @Published private(set) var error: String?
    @Published private(set) var loading = false
    @Published var noLimit = false
    /// 新侧全文（展开折叠的上下文时才取）
    @Published private(set) var newText: [String]? { didSet { version += 1 } }
    @Published var expandedGaps: Set<Int> = []
    @Published private(set) var oldImage: NSImage?
    @Published private(set) var newImage: NSImage?
    @Published private(set) var oldSize = 0
    @Published private(set) var newSize = 0
    @Published private(set) var busyHunk: Int?

    weak var repo: VCSRepo?
    private var loadTask: Task<Void, Never>?
    static let context = 3

    // 布局缓存：父视图因别的状态重绘时不重新生成几千行
    private var version = 0
    private struct LayoutKey: Equatable {
        var version: Int
        var sideBySide: Bool
        var expanded: Set<Int>
    }
    private var layoutCache: (key: LayoutKey, value: VCSDiffLayout.Result)?

    func layout(sideBySide: Bool) -> VCSDiffLayout.Result? {
        guard let d = diff else { return nil }
        let key = LayoutKey(version: version, sideBySide: sideBySide, expanded: expandedGaps)
        if let c = layoutCache, c.key == key { return c.value }
        let v = VCSDiffLayout.build(d, sideBySide: sideBySide, newText: newText, expanded: expandedGaps)
        layoutCache = (key, v)
        return v
    }

    func set(_ t: VCSDiffTarget?, repo: VCSRepo) {
        self.repo = repo
        guard t != target else { return }
        target = t
        diff = nil
        error = nil
        noLimit = false
        reload(resetExpansion: true)
    }

    func reload(resetExpansion: Bool = false) {
        loadTask?.cancel()
        if resetExpansion {
            newText = nil
            expandedGaps = []
            oldImage = nil
            newImage = nil
        }
        guard let t = target, let repo else { diff = nil; error = nil; return }
        loading = diff == nil || resetExpansion
        loadTask = Task { [weak self] in
            do {
                let api = try AppModel.shared.client()
                let d = try await api.vcsDiff(projectId: repo.projectId, root: repo.root,
                                              t.args(context: Self.context, ignoreWhitespace: VCSPrefs.diffIgnoreWhitespace,
                                                     noLimit: self?.noLimit ?? false))
                guard !Task.isCancelled, let self, self.target == t else { return }
                self.diff = d
                self.error = nil
                self.loading = false
                if t.isWorking, t.kind == .working(VCSDiffMode.head) {
                    repo.changes.hunkCounts[t.path] = d.hunks.count
                }
                if d.binary && VCSPath.isImage(t.path) { await self.loadImages(t) }
                if !self.expandedGaps.isEmpty || self.newText != nil { await self.loadNewText() }
            } catch is CancellationError {
            } catch {
                guard let self, self.target == t else { return }
                self.error = error.localizedDescription
                self.diff = nil
                self.loading = false
            }
        }
    }

    private func loadImages(_ t: VCSDiffTarget) async {
        guard let repo, let api = AppModel.shared.api else { return }
        let max = 32 << 20
        async let o: VCSFileContent? = t.oldRef == nil ? nil
            : try? api.vcsFileContent(projectId: repo.projectId, root: repo.root,
                                      VCSFileContentArgs(path: t.oldPath ?? t.path, ref: t.oldRef, maxBytes: max))
        async let n: VCSFileContent? = try? api.vcsFileContent(projectId: repo.projectId, root: repo.root,
                                                                VCSFileContentArgs(path: t.path, ref: t.newRef.isEmpty ? nil : t.newRef, maxBytes: max))
        let (oc, nc) = await (o, n)
        guard target == t else { return }
        oldImage = oc.flatMap { $0.exists ? $0.data.flatMap(NSImage.init(data:)) : nil }
        newImage = nc.flatMap { $0.exists ? $0.data.flatMap(NSImage.init(data:)) : nil }
        oldSize = oc?.size ?? 0
        newSize = nc?.size ?? 0
    }

    /// 取新侧全文（展开上下文用）
    func loadNewText() async {
        guard let t = target, let repo, newText == nil, let api = AppModel.shared.api else { return }
        guard let c = try? await api.vcsFileContent(projectId: repo.projectId, root: repo.root,
                                                    VCSFileContentArgs(path: t.path, ref: t.newRef.isEmpty ? nil : t.newRef, maxBytes: 8 << 20)),
              target == t, c.exists, let text = c.text else { return }
        var lines = text.components(separatedBy: "\n")
        if text.hasSuffix("\n") { lines.removeLast() }
        newText = lines.map { $0.hasSuffix("\r") ? String($0.dropLast()) : $0 }
    }

    func expand(_ gap: Int) {
        Task {
            await loadNewText()
            expandedGaps.insert(gap)
        }
    }

    func expandAll() {
        Task {
            await loadNewText()
            if let d = diff { expandedGaps = Set(0...d.hunks.count) }
        }
    }

    // MARK: 按块操作

    func hunkOp(_ op: String, index: Int, title: String) {
        guard let d = diff, let t = target, let repo, d.hunks.indices.contains(index) else { return }
        busyHunk = index
        Task {
            let args = VCSHunksArgs(path: t.path, hunks: [index], context: d.context ?? Self.context, mode: d.mode,
                                    headers: [d.hunks[index].header], oldPath: t.oldPath)
            await repo.run(op, args, title: title, quietSuccess: true)
            busyHunk = nil
            // 状态字母可能不变（同一文件部分暂存），差异要自己刷新
            reload()
        }
    }
}

/// 一行（统一模式）或一格（并排模式的一侧）
struct VCSDiffCell: Hashable {
    var kind: String // " " | "+" | "-" | "~"（展开出来的上下文）
    var old: Int
    var new: Int
    var text: String
    var noEol = false
}

enum VCSDiffRow: Hashable {
    case hunk(Int, String)
    case gap(Int, Int) // 序号、行数（-1 表示未知，文件末尾）
    case line(VCSDiffCell)
    case pair(VCSDiffCell?, VCSDiffCell?)
}

/// 由 diff 生成要显示的行
enum VCSDiffLayout {
    struct Result {
        var rows: [VCSDiffRow]
        /// 每处变更（连续的增删）第一行的行号
        var changeStarts: [Int]
        var maxLine: Int
    }

    static func build(_ d: VCSDiff, sideBySide: Bool, newText: [String]?, expanded: Set<Int>) -> Result {
        var rows: [VCSDiffRow] = []
        var starts: [Int] = []
        var maxLine = 0
        let hunks = d.hunks

        func range(start: Int, count: Int) -> Int { count == 0 ? start + 1 : start }

        func gapLines(from a: Int, to b: Int, offset: Int) -> [VCSDiffCell] {
            guard let text = newText, a <= b else { return [] }
            var out: [VCSDiffCell] = []
            for n in a...b where n >= 1 && n <= text.count {
                out.append(VCSDiffCell(kind: "~", old: n + offset, new: n, text: text[n - 1]))
            }
            return out
        }

        func appendContext(_ cells: [VCSDiffCell]) {
            for c in cells {
                rows.append(sideBySide ? .pair(c, c) : .line(c))
                maxLine = max(maxLine, c.old, c.new)
            }
        }

        var prevNewEnd = 1
        for (i, h) in hunks.enumerated() {
            let newA = range(start: h.newStart, count: h.newLines)
            let oldA = range(start: h.oldStart, count: h.oldLines)
            let gap = newA - prevNewEnd
            let isNewFile = h.oldStart == 0 && h.oldLines == 0
            if gap > 0 && !isNewFile {
                if expanded.contains(i), newText != nil {
                    appendContext(gapLines(from: prevNewEnd, to: newA - 1, offset: oldA - newA))
                } else {
                    rows.append(.gap(i, gap))
                }
            }
            rows.append(.hunk(i, h.header))
            var dels: [VCSDiffCell] = []
            var adds: [VCSDiffCell] = []
            var inChange = false
            func flush() {
                if sideBySide {
                    for k in 0..<max(dels.count, adds.count) {
                        rows.append(.pair(k < dels.count ? dels[k] : nil, k < adds.count ? adds[k] : nil))
                    }
                } else {
                    for c in dels { rows.append(.line(c)) }
                    for c in adds { rows.append(.line(c)) }
                }
                dels = []
                adds = []
            }
            for l in h.lines {
                let c = VCSDiffCell(kind: l.kind, old: l.old, new: l.new, text: l.text, noEol: l.noEol)
                maxLine = max(maxLine, l.old, l.new)
                if l.kind == "-" || l.kind == "+" {
                    if !inChange {
                        starts.append(rows.count)
                        inChange = true
                    }
                    if l.kind == "-" { dels.append(c) } else { adds.append(c) }
                } else {
                    flush()
                    inChange = false
                    rows.append(sideBySide ? .pair(c, c) : .line(c))
                }
            }
            flush()
            prevNewEnd = newA + h.newLines
            // 文件末尾的折叠：只有取到全文才知道还剩多少行
            if i == hunks.count - 1, !isNewFile, d.status != "D" {
                let oldEnd = oldA + h.oldLines
                if let text = newText {
                    let remain = text.count - prevNewEnd + 1
                    if remain > 0 {
                        if expanded.contains(hunks.count) {
                            appendContext(gapLines(from: prevNewEnd, to: text.count, offset: oldEnd - prevNewEnd))
                        } else {
                            rows.append(.gap(hunks.count, remain))
                        }
                    }
                } else {
                    rows.append(.gap(hunks.count, -1))
                }
            }
        }
        return Result(rows: rows, changeStarts: starts, maxLine: maxLine)
    }
}

/// 差异视图（含工具栏）
struct VCSDiffView: View {
    @ObservedObject var model: VCSDiffModel
    var hunkMode: VCSHunkMode = .none
    /// 工具栏左侧附加内容（如文件路径前的返回按钮）
    var showPath = true
    @ObservedObject private var changes: VCSChangesModel
    @State private var sideBySide = VCSPrefs.diffSideBySide
    @State private var wrap = VCSPrefs.diffWrap
    @State private var ignoreWS = VCSPrefs.diffIgnoreWhitespace
    @State private var currentChange = -1
    @Environment(\.theme) private var theme

    init(model: VCSDiffModel, changes: VCSChangesModel, hunkMode: VCSHunkMode = .none, showPath: Bool = true) {
        self.model = model
        self._changes = ObservedObject(wrappedValue: changes)
        self.hunkMode = hunkMode
        self.showPath = showPath
    }

    var body: some View {
        let layout = model.layout(sideBySide: sideBySide)
        VStack(spacing: 0) {
            toolbar(layout)
            PPDivider(subtle: true)
            content(layout)
        }
        .background(theme.bgContent)
        .onChange(of: model.target) { _ in currentChange = -1 }
    }

    // MARK: 工具栏

    private func toolbar(_ layout: VCSDiffLayout.Result?) -> some View {
        let d = model.diff
        return HStack(spacing: M.sm) {
            if showPath, let t = model.target {
                if let s = d?.status ?? t.status { VCSStatusLetter(status: s) }
                Text(VCSPath.name(t.path)).font(.pp(.bodyStrong)).lineLimit(1)
                if let old = t.oldPath ?? d?.oldPath, old != t.path {
                    Text(old + " → " + t.path).font(.pp(.small)).foregroundColor(theme.textTertiary)
                        .lineLimit(1).truncationMode(.middle)
                } else {
                    Text(VCSPath.dir(t.path)).font(.pp(.small)).foregroundColor(theme.textTertiary)
                        .lineLimit(1).truncationMode(.middle)
                }
                if let d { VCSLineStats(additions: d.additions, deletions: d.deletions) }
            }
            Spacer(minLength: M.md)
            if model.loading { PPSpinner(size: M.iconSmall) }
            if let layout, !layout.changeStarts.isEmpty {
                Text("\(max(currentChange + 1, 0))/\(layout.changeStarts.count)")
                    .font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                PPIconButton("arrow-up", help: tr("vcs.diff.prev"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    jump(-1, layout.changeStarts)
                }
                .disabled(currentChange <= 0)
                PPIconButton("arrow-down", help: tr("vcs.diff.next"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    jump(1, layout.changeStarts)
                }
                .disabled(currentChange >= layout.changeStarts.count - 1)
            }
            PPIconButton("unfold-vertical", help: tr("vcs.diff.expandAll"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                model.expandAll()
            }
            .disabled(d == nil || (d?.hunks.isEmpty ?? true))
            PPIconButton("wrap-text", help: tr("vcs.diff.wrap"), size: M.iconButtonSmall, iconSize: M.iconSmall, active: wrap) {
                wrap.toggle()
                VCSPrefs.diffWrap = wrap
            }
            PPIconButton("case-sensitive", help: tr("vcs.diff.ignoreWhitespace"), size: M.iconButtonSmall, iconSize: M.iconSmall, active: ignoreWS) {
                ignoreWS.toggle()
                VCSPrefs.diffIgnoreWhitespace = ignoreWS
                model.reload()
            }
            PPSegmented(options: [(true, tr("vcs.diff.sideBySide")), (false, tr("vcs.diff.unified"))],
                        selection: Binding(get: { sideBySide }, set: { sideBySide = $0; VCSPrefs.diffSideBySide = $0; currentChange = -1 }),
                        size: .small)
        }
        .padding(.horizontal, M.md)
        .frame(height: M.toolbarHeight)
    }

    private func jump(_ delta: Int, _ starts: [Int]) {
        currentChange = min(max(currentChange + delta, 0), starts.count - 1)
        scrollRequest = starts[currentChange]
    }

    @State private var scrollRequest: Int?

    // MARK: 内容

    @ViewBuilder private func content(_ layout: VCSDiffLayout.Result?) -> some View {
        if model.target == nil {
            PPEmptyState(icon: "file-diff", title: tr("vcs.diff.none"))
        } else if let e = model.error {
            PPAlert(tone: "danger", title: tr("vcs.diff.failed"), message: e) {
                PPButton(tr("connection.retry"), size: .small) { model.reload() }
            }
            .padding(M.lg)
            .frame(maxHeight: .infinity, alignment: .top)
        } else if let d = model.diff {
            if d.binary {
                binary(d)
            } else if d.tooLarge {
                PPEmptyState(icon: "file-text", title: tr("vcs.diff.tooLarge"), actionTitle: tr("vcs.diff.showAnyway")) {
                    model.noLimit = true
                    model.reload()
                }
            } else if d.hunks.isEmpty {
                identical(d)
            } else if let layout {
                lines(d, layout: layout)
            }
        } else {
            PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func identical(_ d: VCSDiff) -> some View {
        VStack(spacing: M.md) {
            PPIcon("file-check", size: M.iconEmpty, color: theme.iconSecondary)
            Text(tr("vcs.diff.identical")).foregroundColor(theme.textSecondary)
            if let o = d.oldMode, let n = d.newMode, o != n {
                Text(tr("vcs.diff.modeChanged", o, n)).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
            }
            if let s = d.similarity, s > 0, d.oldPath != nil {
                Text(tr("vcs.diff.similarity", s)).font(.pp(.small)).foregroundColor(theme.textTertiary)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ViewBuilder private func binary(_ d: VCSDiff) -> some View {
        if let t = model.target, VCSPath.isImage(t.path) {
            HStack(spacing: 0) {
                imagePane(model.oldImage, size: model.oldSize, title: tr("vcs.diff.before"))
                PPDivider(vertical: true)
                imagePane(model.newImage, size: model.newSize, title: tr("vcs.diff.after"))
            }
        } else {
            PPEmptyState(icon: "binary", title: tr("vcs.diff.binary"))
        }
    }

    private func imagePane(_ img: NSImage?, size: Int, title: String) -> some View {
        VStack(spacing: M.sm) {
            HStack(spacing: M.sm) {
                Text(title).font(.pp(.label)).foregroundColor(theme.textSecondary)
                if let img {
                    Text("\(Int(img.size.width))×\(Int(img.size.height))  ·  \(ByteCountFormatter.string(fromByteCount: Int64(size), countStyle: .file))")
                        .font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                }
                Spacer()
            }
            .padding(.horizontal, M.md)
            .frame(height: M.rowHeight)
            ZStack {
                VCSCheckerboard().opacity(img == nil ? 0 : 1)
                if let img {
                    Image(nsImage: img).resizable().interpolation(.none).aspectRatio(contentMode: .fit)
                        .frame(maxWidth: max(img.size.width, 64) * 4, maxHeight: max(img.size.height, 64) * 4)
                        .padding(M.lg)
                } else {
                    Text(tr("vcs.diff.noFile")).foregroundColor(theme.textTertiary)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func lines(_ d: VCSDiff, layout: VCSDiffLayout.Result) -> some View {
        let digits = max(String(layout.maxLine).count, 2)
        let gutter = CGFloat(digits) * 7.3 + 10
        return GeometryReader { geo in
            ScrollViewReader { proxy in
                ScrollView(sideBySide || wrap ? [.vertical] : [.vertical, .horizontal]) {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(layout.rows.indices, id: \.self) { i in
                            row(layout.rows[i], diff: d, gutter: gutter, width: geo.size.width)
                                .id(i)
                        }
                    }
                    .frame(minWidth: geo.size.width, alignment: .leading)
                    .padding(.bottom, M.xl)
                }
                .onChange(of: scrollRequest) { r in
                    guard let r else { return }
                    withAnimation(.easeOut(duration: 0.12)) { proxy.scrollTo(r, anchor: .center) }
                    scrollRequest = nil
                }
            }
        }
        .font(.pp(.mono))
        .background(theme.bgContent)
    }

    @ViewBuilder
    private func row(_ r: VCSDiffRow, diff d: VCSDiff, gutter: CGFloat, width: CGFloat) -> some View {
        switch r {
        case .hunk(let i, let header):
            hunkHeader(i, header: header, diff: d)
        case .gap(let i, let n):
            gapRow(i, count: n)
        case .line(let c):
            unifiedLine(c, gutter: gutter, width: width)
        case .pair(let l, let rr):
            HStack(spacing: 0) {
                sideCell(l, left: true, gutter: gutter)
                Rectangle().fill(theme.borderSubtle).frame(width: 1)
                sideCell(rr, left: false, gutter: gutter)
            }
            .frame(width: width)
        }
    }

    /// 忽略空白时块的划分与插件按块操作用的 diff 不同，不给块操作
    private var effectiveHunkMode: VCSHunkMode { ignoreWS ? .none : hunkMode }

    private func hunkHeader(_ i: Int, header: String, diff d: VCSDiff) -> some View {
        let hunkMode = effectiveHunkMode
        return HStack(spacing: M.sm) {
            if hunkMode == .include, let path = model.target?.path {
                let excluded = changes.excludedHunks[path]?.contains(i) ?? false
                PPCheckbox(isOn: Binding(get: { !excluded }, set: { on in changes.setHunk(path: path, index: i, included: on, total: d.hunks.count) }))
                    .help(tr("vcs.diff.includeHunk"))
            }
            Text(header).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary).lineLimit(1)
            Spacer(minLength: M.md)
            if model.busyHunk == i { PPSpinner(size: M.iconSmall) }
            switch hunkMode {
            case .stage:
                PPButton(tr("vcs.diff.stageHunk"), icon: "plus", kind: .ghost, size: .small) {
                    model.hunkOp(VCSOp.stageHunks, index: i, title: tr("vcs.diff.stageHunk"))
                }
                discardHunkButton(i)
            case .unstage:
                PPButton(tr("vcs.diff.unstageHunk"), icon: "minus", kind: .ghost, size: .small) {
                    model.hunkOp(VCSOp.unstageHunks, index: i, title: tr("vcs.diff.unstageHunk"))
                }
            case .include:
                discardHunkButton(i)
            case .none:
                EmptyView()
            }
        }
        .padding(.horizontal, M.md)
        .frame(height: M.rowHeightCompact)
        .background(theme.diffHunkBg)
    }

    private func discardHunkButton(_ i: Int) -> some View {
        PPButton(tr("vcs.diff.discardHunk"), icon: "undo-2", kind: .ghost, size: .small) {
            Dialogs.confirm(title: tr("vcs.diff.discardHunk.title"), message: tr("vcs.diff.discardHunk.message"),
                            ok: tr("vcs.discard"), danger: true) {
                model.hunkOp(VCSOp.discardHunks, index: i, title: tr("vcs.diff.discardHunk"))
            }
        }
    }

    private func gapRow(_ i: Int, count: Int) -> some View {
        HStack(spacing: M.sm) {
            PPIcon("unfold-vertical", size: 12, color: theme.iconSecondary)
            Text(count < 0 ? tr("vcs.diff.expandRest") : tr("vcs.diff.unchangedLines", count))
                .font(.pp(.small)).foregroundColor(theme.textTertiary)
            Spacer()
        }
        .padding(.horizontal, M.md)
        .frame(height: 22)
        .background(theme.bgSubtle)
        .contentShape(Rectangle())
        .onTapGesture { model.expand(i) }
        .help(tr("vcs.diff.expand"))
    }

    private func bg(_ kind: String) -> Color {
        switch kind {
        case "+": return theme.diffAddedBg
        case "-": return theme.diffRemovedBg
        default: return .clear
        }
    }

    private func number(_ n: Int, gutter: CGFloat) -> some View {
        Text(n > 0 ? String(n) : "")
            .font(.pp(.monoSmall))
            .foregroundColor(theme.textTertiary)
            .frame(width: gutter, alignment: .trailing)
            .padding(.trailing, 6)
    }

    private func display(_ s: String) -> String { s.replacingOccurrences(of: "\t", with: "    ") }

    private func lineText(_ c: VCSDiffCell) -> some View {
        HStack(spacing: 4) {
            Text(display(c.text))
                .foregroundColor(c.kind == "~" ? theme.textSecondary : theme.textPrimary)
                .lineLimit(wrap ? nil : 1)
                .fixedSize(horizontal: !wrap && !sideBySide, vertical: wrap)
            if c.noEol {
                PPIcon("circle-minus", size: 11, color: theme.textTertiary).help(tr("vcs.diff.noEol"))
            }
        }
    }

    private func unifiedLine(_ c: VCSDiffCell, gutter: CGFloat, width: CGFloat) -> some View {
        HStack(alignment: .top, spacing: 0) {
            number(c.kind == "+" ? 0 : c.old, gutter: gutter)
            number(c.kind == "-" ? 0 : c.new, gutter: gutter)
            Text(c.kind == "+" || c.kind == "-" ? c.kind : " ")
                .foregroundColor(c.kind == "+" ? theme.diffAddedText : theme.diffRemovedText)
                .frame(width: 14, alignment: .leading)
            lineText(c)
            Spacer(minLength: 0)
        }
        .frame(minWidth: width, minHeight: 18, alignment: .leading)
        .background(bg(c.kind))
    }

    private func sideCell(_ c: VCSDiffCell?, left: Bool, gutter: CGFloat) -> some View {
        HStack(alignment: .top, spacing: 0) {
            if let c {
                number(left ? c.old : c.new, gutter: gutter)
                lineText(c)
                Spacer(minLength: 0)
            } else {
                Spacer(minLength: 0)
            }
        }
        .frame(maxWidth: .infinity, minHeight: 18, alignment: .leading)
        .clipped()
        .background(c.map { bg($0.kind) } ?? theme.bgSubtle)
        .help(c.map { display($0.text) } ?? "")
    }
}

/// 透明图片底下的棋盘格
struct VCSCheckerboard: View {
    @Environment(\.theme) private var theme

    var body: some View {
        Canvas { ctx, size in
            let s: CGFloat = 8
            var y: CGFloat = 0
            var row = 0
            while y < size.height {
                var x: CGFloat = row % 2 == 0 ? 0 : s
                while x < size.width {
                    ctx.fill(Path(CGRect(x: x, y: y, width: s, height: s)), with: .color(theme.neutralSubtle))
                    x += s * 2
                }
                y += s
                row += 1
            }
        }
    }
}

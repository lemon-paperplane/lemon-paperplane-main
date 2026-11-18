import Combine
import PaperplaneKit
import SwiftUI

/// 文件树数据：按需逐级展开（fs.list），过滤走 fs.search，Git 状态取各仓库的 vcs.status。
/// 只在「文件」标签页可见时存在，离开即释放；展开状态按工程记在内存里。
@MainActor
final class FileTreeModel: ObservableObject {
    struct Row: Identifiable {
        let entry: FSEntry
        let depth: Int
        var id: String { entry.path }
    }

    let projectId: String
    let projectPath: String
    private let vcsRoots: [VCSRoot]

    /// 目录（"" 为根）→ 子项
    @Published private(set) var children: [String: [FSEntry]] = [:]
    @Published private(set) var expanded: Set<String>
    @Published private(set) var loading: Set<String> = []
    @Published private(set) var errors: [String: String] = [:]
    @Published private(set) var truncated: Set<String> = []
    @Published var selection: String?
    /// 相对工程根的路径 → Git 状态（M A D R C U ? T）
    @Published private(set) var git: [String: String] = [:]
    /// 含有改动的目录
    @Published private(set) var changedDirs: Set<String> = []

    @Published var query = "" { didSet { scheduleSearch() } }
    @Published private(set) var results: [FSSearchHit]?
    @Published private(set) var searching = false

    private var searchTask: Task<Void, Never>?
    private var bag: Set<AnyCancellable> = []
    /// 各工程的展开状态（只存路径集合，很小）
    private static var expandedCache: [String: Set<String>] = [:]

    init(project: Project) {
        projectId = project.id
        projectPath = project.path
        vcsRoots = project.vcsRoots
        expanded = Self.expandedCache[project.id] ?? [""]
        // 仓库变化（提交、切分支、外部修改被 git 发现）时刷新状态与已展开的目录
        AppModel.shared.bus.vcsChanged
            .filter { [projectId] in $0.projectId == projectId }
            .debounce(for: .milliseconds(400), scheduler: RunLoop.main)
            .sink { [weak self] _ in Task { await self?.refresh() } }
            .store(in: &bag)
    }

    deinit { searchTask?.cancel() }

    // MARK: 目录

    func start() async {
        await withTaskGroup(of: Void.self) { g in
            for dir in expanded { g.addTask { await self.reload(dir) } }
            g.addTask { await self.loadGit() }
        }
    }

    func reload(_ dir: String, expand: Bool = false) async {
        if expand { setExpanded(dir, true) }
        loading.insert(dir)
        defer { loading.remove(dir) }
        do {
            let r = try await AppModel.shared.client().fsList(projectId: projectId, path: dir)
            children[dir] = r.entries
            errors.removeValue(forKey: dir)
            if r.truncated { truncated.insert(dir) } else { truncated.remove(dir) }
            // 已展开但被删掉的子目录不再记
            let alive = Set(r.entries.filter(\.dir).map(\.path))
            for e in expanded where FileActions.parent(of: e) == dir && !e.isEmpty && !alive.contains(e) { setExpanded(e, false) }
        } catch {
            errors[dir] = error.localizedDescription
        }
    }

    func toggle(_ entry: FSEntry) {
        guard entry.dir else { return }
        if expanded.contains(entry.path) {
            setExpanded(entry.path, false)
        } else {
            setExpanded(entry.path, true)
            if children[entry.path] == nil { Task { await reload(entry.path) } }
        }
    }

    func collapseAll() {
        expanded = [""]
        Self.expandedCache[projectId] = expanded
    }

    func refresh() async {
        await start()
    }

    private func setExpanded(_ dir: String, _ on: Bool) {
        if on { expanded.insert(dir) } else { expanded.remove(dir) }
        Self.expandedCache[projectId] = expanded
    }

    /// 展开的目录压平成行（只算已加载的部分）
    var rows: [Row] {
        var out: [Row] = []
        func walk(_ dir: String, _ depth: Int) {
            for e in children[dir] ?? [] {
                out.append(Row(entry: e, depth: depth))
                if e.dir && expanded.contains(e.path) { walk(e.path, depth + 1) }
            }
        }
        walk("", 0)
        return out
    }

    /// 选中项所在目录（新建文件用）
    var selectedDir: String {
        guard let s = selection else { return "" }
        if expanded.contains(s) || children[s] != nil { return s }
        if let e = rows.first(where: { $0.entry.path == s })?.entry, e.dir { return e.path }
        return FileActions.parent(of: s)
    }

    // MARK: Git

    func loadGit() async {
        guard !vcsRoots.isEmpty, let api = AppModel.shared.api else { return }
        var map: [String: String] = [:]
        for root in vcsRoots {
            guard let st = try? await api.vcsStatus(projectId: projectId, root: root.path) else { continue }
            for f in st.files {
                let rel = root.relPath.isEmpty ? f.path : root.relPath + "/" + f.path
                map[rel] = f.conflicted ? "U" : f.status
            }
        }
        var dirs = Set<String>()
        for (p, s) in map where s != "D" {
            var d = FileActions.parent(of: p)
            while !d.isEmpty, !dirs.contains(d) {
                dirs.insert(d)
                d = FileActions.parent(of: d)
            }
        }
        git = map
        changedDirs = dirs
    }

    // MARK: 过滤（按文件名模糊搜索整个工程）

    private func scheduleSearch() {
        searchTask?.cancel()
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else {
            results = nil
            searching = false
            return
        }
        searching = true
        searchTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 200_000_000)
            guard let self, !Task.isCancelled else { return }
            let hits = try? await AppModel.shared.client().fsSearch(projectId: self.projectId, query: q, limit: 200)
            guard !Task.isCancelled else { return }
            self.results = hits ?? []
            self.searching = false
        }
    }
}

/// 工程详情「文件」标签页
struct FilesTab: View {
    let project: Project
    @StateObject private var tree: FileTreeModel
    @Environment(\.theme) private var theme

    init(project: Project) {
        self.project = project
        _tree = StateObject(wrappedValue: FileTreeModel(project: project))
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            PPDivider(subtle: true)
            content
        }
        .task { await tree.start() }
    }

    private var toolbar: some View {
        HStack(spacing: M.xs) {
            PPTextField(tr("files.filter"), text: $tree.query, icon: "search", clearable: true, height: M.controlHeightSmall + 2)
                .frame(maxWidth: 320)
            if tree.searching { PPSpinner(size: M.iconSmall).padding(.leading, M.xs) }
            Spacer(minLength: M.md)
            PPIconButton("file-plus", help: tr("files.newFile")) {
                FileActions.newItem(projectId: project.id, in: tree.selectedDir, folder: false, tree: tree)
            }
            PPIconButton("folder-plus", help: tr("files.newFolder")) {
                FileActions.newItem(projectId: project.id, in: tree.selectedDir, folder: true, tree: tree)
            }
            PPIconButton("chevrons-down-up", help: tr("files.collapseAll")) { tree.collapseAll() }
            PPIconButton("refresh-cw", help: tr("files.refresh")) { Task { await tree.refresh() } }
            PPButton(tr("editorWindow.open"), icon: "file-pen", size: .small) {
                EditorWindows.shared.open(projectId: project.id, file: selectedFile)
            }
            .padding(.leading, M.xs)
        }
        .padding(.horizontal, M.lg)
        .frame(height: M.toolbarHeight)
    }

    /// 选中的是文件时随编辑窗口一起打开
    private var selectedFile: String? {
        guard let s = tree.selection else { return nil }
        if let e = tree.rows.first(where: { $0.entry.path == s })?.entry { return e.dir ? nil : e.path }
        return tree.results?.contains { $0.path == s } == true ? s : nil
    }

    @ViewBuilder private var content: some View {
        if let results = tree.results {
            if results.isEmpty && !tree.searching {
                PPEmptyState(icon: "file-search", title: tr("files.noMatch"))
            } else {
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(results) { hit in SearchHitRow(hit: hit, tree: tree, project: project) }
                    }
                    .padding(M.sm)
                }
            }
        } else if let err = tree.errors[""], tree.children[""] == nil {
            PPEmptyState(icon: "circle-alert", title: tr("files.loadFailed") + "\n" + err, actionTitle: tr("connection.retry")) {
                Task { await tree.reload("") }
            }
        } else if tree.children[""] == nil {
            PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if tree.children[""]?.isEmpty == true {
            PPEmptyState(icon: "folder-open", title: tr("files.empty"), actionTitle: tr("files.newFile"), actionIcon: "file-plus") {
                FileActions.newItem(projectId: project.id, in: "", folder: false, tree: tree)
            }
        } else {
            let rows = tree.rows
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(rows) { row in FileTreeRow(row: row, tree: tree, project: project) }
                    if tree.truncated.contains("") {
                        Text(tr("files.truncated")).font(.pp(.small)).foregroundColor(theme.textTertiary).padding(M.md)
                    }
                }
                .padding(.vertical, M.sm)
                .padding(.horizontal, M.sm)
            }
        }
    }
}

/// 树的一行
private struct FileTreeRow: View {
    let row: FileTreeModel.Row
    @ObservedObject var tree: FileTreeModel
    let project: Project
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme
    @State private var hover = false

    private var e: FSEntry { row.entry }

    var body: some View {
        let open = tree.expanded.contains(e.path)
        let selected = tree.selection == e.path
        HStack(spacing: M.xs) {
            Spacer().frame(width: CGFloat(row.depth) * 16)
            Group {
                if e.dir {
                    Button { tree.toggle(e) } label: {
                        PPIcon(open ? "chevron-down" : "chevron-right", size: M.iconSmall, color: theme.iconSecondary)
                            .frame(width: M.icon, height: M.icon)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                } else {
                    Spacer().frame(width: M.icon)
                }
            }
            FileIconView(entry: e, expanded: open)
            Text(e.name)
                .foregroundColor(nameColor)
                .lineLimit(1)
                .truncationMode(.middle)
            if e.symlink { PPIcon("folder-symlink", size: 12, color: theme.iconSecondary).help(tr("files.symlink")) }
            if tree.loading.contains(e.path) { PPSpinner(size: 12) }
            Spacer(minLength: M.sm)
            if let s = tree.git[e.path] {
                Text(s).font(.pp(.monoSmall)).foregroundColor(gitColor(s)).help(gitTitle(s))
            }
        }
        .opacity(e.ignored ? 0.5 : (e.hidden ? 0.75 : 1))
        .padding(.horizontal, M.sm)
        .frame(height: M.rowHeightCompact)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: M.radiusSM)
            .fill(selected ? theme.bgSelected : (hover ? theme.bgHover : .clear)))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .gesture(TapGesture(count: 2).onEnded {
            if e.dir { tree.toggle(e) } else { FileActions.edit(projectId: project.id, path: e.path) }
        })
        .simultaneousGesture(TapGesture().onEnded { tree.selection = e.path })
        .ppContextMenu {
            tree.selection = e.path
            return FileActions.menu(projectId: project.id, projectPath: project.path, entry: e, tree: tree)
        }
        .help(e.ignored ? tr("files.ignored") : "")
    }

    private var nameColor: Color {
        if let s = tree.git[e.path] { return gitColor(s) }
        if e.dir && tree.changedDirs.contains(e.path) { return theme.info }
        return theme.textPrimary
    }

    private func gitColor(_ s: String) -> Color { FileGitStyle.color(s, theme: theme) }
    private func gitTitle(_ s: String) -> String { FileGitStyle.title(s) }
}

/// 过滤结果的一行
private struct SearchHitRow: View {
    let hit: FSSearchHit
    @ObservedObject var tree: FileTreeModel
    let project: Project
    @Environment(\.theme) private var theme
    @State private var hover = false

    var body: some View {
        let entry = FSEntry(name: hit.name, path: hit.path, dir: false)
        HStack(spacing: M.sm) {
            FileIconView(entry: entry, expanded: false)
            Text(hit.name).foregroundColor(tree.git[hit.path].map { FileGitStyle.color($0, theme: theme) } ?? theme.textPrimary)
                .lineLimit(1)
            Text(hit.directory).font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.head)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, M.sm)
        .frame(height: M.rowHeightCompact)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: M.radiusSM)
            .fill(tree.selection == hit.path ? theme.bgSelected : (hover ? theme.bgHover : .clear)))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .gesture(TapGesture(count: 2).onEnded { FileActions.edit(projectId: project.id, path: hit.path) })
        .simultaneousGesture(TapGesture().onEnded { tree.selection = hit.path })
        .ppContextMenu {
            tree.selection = hit.path
            return FileActions.menu(projectId: project.id, projectPath: project.path, entry: entry, tree: tree)
        }
    }
}

/// 文件图标：有语言贡献且该语言对应某个技术徽标时用它的图标，否则按扩展名用通用 SVG
struct FileIconView: View {
    let entry: FSEntry
    let expanded: Bool
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        if !entry.dir, let lang = LanguageResolver.language(for: entry.name, in: data.contributions.languages),
           let facet = data.contributions.facet(lang.id), let icon = facet.icon {
            PPIcon(raw: icon, plugin: facet.plugin, fallback: FileIcon.builtinName(for: entry), size: M.icon)
        } else {
            PPIcon(FileIcon.builtinName(for: entry, expanded: expanded), size: M.icon,
                   color: entry.dir ? theme.iconSecondary : theme.iconPrimary)
        }
    }
}

/// Git 状态的颜色与说明（与 IDEA 一致：修改蓝、新增绿、未跟踪橙、冲突红、删除灰）
enum FileGitStyle {
    static func color(_ s: String, theme: Theme) -> Color {
        switch s {
        case "M", "R", "C", "T": return theme.info
        case "A": return theme.success
        case "?": return theme.warning
        case "U": return theme.danger
        case "D": return theme.textTertiary
        default: return theme.textPrimary
        }
    }

    static func title(_ s: String) -> String {
        switch s {
        case "M", "T": return tr("files.git.modified")
        case "A": return tr("files.git.added")
        case "?": return tr("files.git.untracked")
        case "U": return tr("files.git.conflict")
        case "D": return tr("files.git.deleted")
        case "R", "C": return tr("files.git.renamed")
        default: return s
        }
    }
}

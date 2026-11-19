import AppKit
import PaperplaneKit
import SwiftUI

/// 「提交」子页：左侧变更列表 + 提交信息，右侧差异
struct VCSChangesPage: View {
    @ObservedObject var repo: VCSRepo
    @ObservedObject var changes: VCSChangesModel
    @State private var leftWidth: CGFloat = 380

    init(repo: VCSRepo) {
        self.repo = repo
        self._changes = ObservedObject(wrappedValue: repo.changes)
    }

    var body: some View {
        PPSplitView(leftWidth: $leftWidth, minLeft: 260, maxLeft: 680) {
            VStack(spacing: 0) {
                VCSChangesToolbar(repo: repo, changes: changes)
                PPDivider(subtle: true)
                VCSChangeList(repo: repo, changes: changes)
                PPDivider()
                VCSCommitPanel(repo: repo, changes: changes)
            }
        } right: {
            VCSDiffView(model: changes.diff, changes: changes, hunkMode: changes.hunkMode)
        }
    }
}

// MARK: - 列表工具栏

private struct VCSChangesToolbar: View {
    @ObservedObject var repo: VCSRepo
    @ObservedObject var changes: VCSChangesModel

    var body: some View {
        HStack(spacing: M.xxs) {
            PPIconButton("refresh-cw", help: tr("vcs.refresh"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                Task {
                    await repo.refresh()
                    changes.diff.reload()
                }
            }
            PPIconButton("undo-2", help: tr("vcs.discard.selected"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                discardSelected()
            }
            .disabled(changes.selection == nil && changes.multi.isEmpty)
            PPIconButton("archive", help: tr("vcs.stash.new"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                VCSStashDialogs.presentNew(repo: repo, paths: nil)
            }
            .disabled((repo.status?.files.isEmpty ?? true))
            PPDivider(vertical: true).frame(height: 14).padding(.horizontal, M.xs)
            PPIconButton("folder-tree", help: tr("vcs.changes.groupByDir"), size: M.iconButtonSmall, iconSize: M.iconSmall,
                         active: changes.groupByDir) {
                changes.groupByDir.toggle()
            }
            PPIconButton("chevrons-down-up", help: tr("vcs.changes.collapseAll"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                collapseAll()
            }
            .disabled(!changes.groupByDir)
            PPIconButton("chevrons-up-down", help: tr("vcs.changes.expandAll"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                changes.collapsed = []
            }
            .disabled(!changes.groupByDir)
            PPIconButton("layers", help: tr("vcs.changes.stagingArea"), size: M.iconButtonSmall, iconSize: M.iconSmall,
                         active: changes.stagingArea) {
                changes.stagingArea.toggle()
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, M.sm)
        .frame(height: M.rowHeight + 4)
    }

    private func collapseAll() {
        guard let s = repo.status else { return }
        var keys: Set<String> = []
        for f in s.files {
            var d = VCSPath.dir(f.path)
            while !d.isEmpty {
                for g in ["conflict", "changes", "untracked", "staged", "unstaged"] { keys.insert("d:\(g):\(d)") }
                d = VCSPath.dir(d)
            }
        }
        changes.collapsed = keys
    }

    /// 丢弃列表里选中的文件（多选或当前选中，与 IDEA 的 Rollback 一致；不按勾选，避免误丢全部）
    private func discardSelected() {
        guard let s = repo.status else { return }
        let sels = changes.multi.isEmpty ? (changes.selection.map { [$0] } ?? []) : Array(changes.multi)
        let set = Set(sels.map(\.path))
        let files = s.files.filter { set.contains($0.path) && !$0.conflicted }
        guard !files.isEmpty else { return }
        let onlyUnstaged = changes.stagingArea && sels.allSatisfy { $0.side == .unstaged }
        VCSDiscardDialog.present(repo: repo, files: files, mode: onlyUnstaged ? VCSDiffMode.unstaged : nil)
    }
}

// MARK: - 变更列表

/// 列表的一行
private struct VCSChangeRowItem: Identifiable {
    enum Kind {
        case group(key: String, title: String, files: [VCSFileChange], side: VCSChangeSide)
        case dir(key: String, name: String, files: [VCSFileChange], side: VCSChangeSide)
        case file(VCSFileChange, VCSChangeSide)
        case truncated(Int)
    }

    let id: String
    let kind: Kind
    var depth = 0
}

@MainActor
private enum VCSChangeRows {
    static func build(_ s: VCSStatus, changes: VCSChangesModel) -> [VCSChangeRowItem] {
        let g = changes.groups(s.files)
        var rows: [VCSChangeRowItem] = []
        func add(_ key: String, _ title: String, _ files: [VCSFileChange], _ side: VCSChangeSide) {
            guard !files.isEmpty else { return }
            rows.append(VCSChangeRowItem(id: "g:" + key, kind: .group(key: key, title: title, files: files, side: side)))
            guard !changes.collapsed.contains("g:" + key) else { return }
            if changes.groupByDir {
                rows += tree(files, group: key, side: side, collapsed: changes.collapsed)
            } else {
                rows += files.sorted { $0.path.localizedStandardCompare($1.path) == .orderedAscending }
                    .map { VCSChangeRowItem(id: "f:\(key):\($0.path)", kind: .file($0, side), depth: 1) }
            }
        }
        add("conflict", tr("vcs.changes.conflicts"), g.conflicts, .conflict)
        if changes.stagingArea {
            add("staged", tr("vcs.changes.staged"), g.staged, .staged)
            add("unstaged", tr("vcs.changes.unstaged"), g.unstaged, .unstaged)
        } else {
            add("changes", tr("vcs.changes.changes"), g.tracked, .head)
        }
        add("untracked", tr("vcs.changes.untracked"), g.untracked, .untracked)
        if s.untrackedTruncated, !changes.collapsed.contains("g:untracked") {
            rows.append(VCSChangeRowItem(id: "trunc", kind: .truncated(s.untrackedCount), depth: 1))
        }
        return rows
    }

    private final class Node {
        var dirs: [String: Node] = [:]
        var files: [VCSFileChange] = []
        var all: [VCSFileChange] = []
    }

    static func tree(_ files: [VCSFileChange], group: String, side: VCSChangeSide, collapsed: Set<String>) -> [VCSChangeRowItem] {
        let root = Node()
        for f in files {
            var node = root
            node.all.append(f)
            let dir = VCSPath.dir(f.path)
            if !dir.isEmpty {
                for comp in dir.split(separator: "/") {
                    let k = String(comp)
                    let next = node.dirs[k] ?? Node()
                    node.dirs[k] = next
                    node = next
                    node.all.append(f)
                }
            }
            node.files.append(f)
        }
        var out: [VCSChangeRowItem] = []
        func walk(_ n: Node, prefix: String, depth: Int) {
            for name in n.dirs.keys.sorted(by: { $0.localizedStandardCompare($1) == .orderedAscending }) {
                var child = n.dirs[name]!
                var label = name
                var path = prefix.isEmpty ? name : prefix + "/" + name
                // 只有一个子目录且没有文件的目录链压缩成一行
                while child.files.isEmpty, child.dirs.count == 1, let (k, v) = child.dirs.first {
                    label += "/" + k
                    path += "/" + k
                    child = v
                }
                let key = "d:\(group):\(path)"
                out.append(VCSChangeRowItem(id: key, kind: .dir(key: key, name: label, files: child.all, side: side), depth: depth))
                if !collapsed.contains(key) { walk(child, prefix: path, depth: depth + 1) }
            }
            for f in n.files.sorted(by: { VCSPath.name($0.path).localizedStandardCompare(VCSPath.name($1.path)) == .orderedAscending }) {
                out.append(VCSChangeRowItem(id: "f:\(group):\(f.path)", kind: .file(f, side), depth: depth))
            }
        }
        walk(root, prefix: "", depth: 1)
        return out
    }
}

private struct VCSChangeList: View {
    @ObservedObject var repo: VCSRepo
    @ObservedObject var changes: VCSChangesModel
    @Environment(\.theme) private var theme

    var body: some View {
        if let s = repo.status {
            if s.files.isEmpty {
                PPEmptyState(icon: "circle-check", title: tr("vcs.changes.clean"))
            } else {
                let rows = VCSChangeRows.build(s, changes: changes)
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(rows) { r in rowView(r) }
                    }
                    .padding(.vertical, M.xs)
                    .padding(.horizontal, M.xs)
                }
            }
        } else if let e = repo.statusError {
            PPAlert(tone: "danger", title: tr("vcs.status.failed"), message: e) {
                PPButton(tr("connection.retry"), size: .small) { Task { await repo.loadStatus() } }
            }
            .padding(M.md)
            .frame(maxHeight: .infinity, alignment: .top)
        } else {
            PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    @ViewBuilder private func rowView(_ r: VCSChangeRowItem) -> some View {
        switch r.kind {
        case .group(let key, let title, let files, let side):
            groupRow(key: key, title: title, files: files, side: side)
        case .dir(let key, let name, let files, let side):
            dirRow(key: key, name: name, files: files, side: side, depth: r.depth)
        case .file(let f, let side):
            VCSChangeFileRow(repo: repo, changes: changes, file: f, side: side, depth: r.depth)
        case .truncated(let n):
            HStack(spacing: M.sm) {
                PPIcon("triangle-alert", size: M.iconSmall, color: theme.warning)
                Text(tr("vcs.changes.untrackedTruncated", n)).font(.pp(.small)).foregroundColor(theme.textSecondary)
                PPInfoButton(text: tr("vcs.changes.untrackedTruncated.info"))
                Spacer()
            }
            .padding(.leading, CGFloat(r.depth) * 14 + M.sm)
            .frame(height: M.rowHeightCompact)
        }
    }

    private func groupRow(key: String, title: String, files: [VCSFileChange], side: VCSChangeSide) -> some View {
        let collapsedKey = "g:" + key
        let isCollapsed = changes.collapsed.contains(collapsedKey)
        return HStack(spacing: M.xs) {
            VCSGroupHeader(title: title, count: files.count, collapsed: isCollapsed, onToggle: { toggle(collapsedKey) }) {
                if !changes.stagingArea && (side == .head || side == .untracked) {
                    VCSTriCheckbox(state: changes.groupState(files)) { changes.toggleGroup(files) }
                }
            }
            groupActions(side: side, files: files)
        }
        .padding(.trailing, M.xs)
        .padding(.top, M.xs)
    }

    @ViewBuilder private func groupActions(side: VCSChangeSide, files: [VCSFileChange]) -> some View {
        switch side {
        case .conflict:
            PPButton(tr("vcs.conflicts.resolve"), kind: .link) { repo.session.page = .conflicts }
                .font(.pp(.small))
        case .staged:
            PPIconButton("minus", help: tr("vcs.unstageAll"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                repo.perform(VCSOp.unstage, VCSPathsArgs(paths: files.map(\.path)), title: tr("vcs.unstage"), quietSuccess: true)
            }
        case .unstaged, .untracked:
            if changes.stagingArea {
                PPIconButton("plus", help: tr("vcs.stageAll"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    repo.perform(VCSOp.stage, VCSPathsArgs(paths: files.map(\.path)), title: tr("vcs.stage"), quietSuccess: true)
                }
            }
        default:
            EmptyView()
        }
    }

    private func dirRow(key: String, name: String, files: [VCSFileChange], side: VCSChangeSide, depth: Int) -> some View {
        let isCollapsed = changes.collapsed.contains(key)
        return HStack(spacing: M.xs) {
            PPIcon(isCollapsed ? "chevron-right" : "chevron-down", size: 12, color: theme.iconSecondary)
            if !changes.stagingArea && (side == .head || side == .untracked) {
                VCSTriCheckbox(state: changes.groupState(files)) { changes.toggleGroup(files) }
            }
            PPIcon("folder", size: M.iconSmall, color: theme.iconSecondary)
            Text(name).lineLimit(1).truncationMode(.middle)
            Text("\(files.count)").font(.pp(.caption)).foregroundColor(theme.textTertiary)
            Spacer(minLength: 0)
        }
        .padding(.leading, CGFloat(depth) * 14 + M.xxs)
        .padding(.trailing, M.sm)
        .frame(height: M.rowHeightCompact)
        .contentShape(Rectangle())
        .onTapGesture { toggle(key) }
    }

    private func toggle(_ key: String) {
        if changes.collapsed.contains(key) { changes.collapsed.remove(key) } else { changes.collapsed.insert(key) }
    }
}

/// 一个文件行：勾选、状态着色的文件名、目录、增删行数、悬停时暂存/取消暂存
private struct VCSChangeFileRow: View {
    @ObservedObject var repo: VCSRepo
    @ObservedObject var changes: VCSChangesModel
    let file: VCSFileChange
    let side: VCSChangeSide
    let depth: Int
    @State private var hover = false
    @Environment(\.theme) private var theme

    private var sel: VCSChangeSelection { VCSChangeSelection(path: file.path, side: side) }

    private var displayStatus: String {
        switch side {
        case .staged: return file.indexStatus ?? file.status
        case .unstaged: return file.worktreeStatus ?? file.status
        default: return file.status
        }
    }

    var body: some View {
        let selected = changes.selection == sel || changes.multi.contains(sel)
        let st = displayStatus
        HStack(spacing: M.sm) {
            if !changes.stagingArea && (side == .head || side == .untracked) {
                VCSTriCheckbox(state: changes.isIncluded(file)) {
                    changes.setIncluded(file, changes.isIncluded(file) != true)
                }
            }
            VCSStatusLetter(status: st)
            Text(VCSPath.name(file.path))
                .foregroundColor(VCSStyle.color(st, theme: theme))
                .strikethrough(st == "D", color: theme.textTertiary)
                .lineLimit(1)
            if let old = file.oldPath, old != file.path {
                Text("← " + (VCSPath.dir(old) == VCSPath.dir(file.path) ? VCSPath.name(old) : old))
                    .font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.middle)
            }
            if !changes.groupByDir {
                Text(VCSPath.dir(file.path)).font(.pp(.small)).foregroundColor(theme.textTertiary)
                    .lineLimit(1).truncationMode(.middle)
            }
            if side == .conflict {
                Text(VCSStyle.conflictTitle(file.conflictType)).font(.pp(.caption)).foregroundColor(theme.danger).lineLimit(1)
            }
            Spacer(minLength: M.xs)
            if hover && changes.stagingArea {
                hoverActions
            } else if file.binary {
                PPIcon("binary", size: 12, color: theme.textTertiary)
            } else if side == .head || side == .untracked || side == .staged {
                VCSLineStats(additions: file.additions, deletions: file.deletions)
            }
        }
        .padding(.leading, CGFloat(depth) * 14 + M.xxs)
        .padding(.trailing, M.sm)
        .frame(height: M.rowHeightCompact)
        .background(RoundedRectangle(cornerRadius: M.radiusSM)
            .fill(selected ? theme.bgSelected : (hover ? theme.bgHover : .clear)))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture { changes.select(sel, file: file, repo: repo) }
        .help(file.path)
        .ppContextMenu { VCSFileMenu.entries(repo: repo, scope: changes.scope(for: sel)) }
    }

    @ViewBuilder private var hoverActions: some View {
        switch side {
        case .staged:
            PPIconButton("minus", help: tr("vcs.unstage"), size: 20, iconSize: 12) {
                repo.perform(VCSOp.unstage, VCSPathsArgs(paths: [file.path]), title: tr("vcs.unstage"), quietSuccess: true)
            }
        case .unstaged, .untracked:
            PPIconButton("plus", help: tr("vcs.stage"), size: 20, iconSize: 12) {
                repo.perform(VCSOp.stage, VCSPathsArgs(paths: [file.path]), title: tr("vcs.stage"), quietSuccess: true)
            }
        default:
            EmptyView()
        }
    }
}

// MARK: - 文件右键菜单

@MainActor
enum VCSFileMenu {
    static func entries(repo: VCSRepo, scope: [VCSChangeSelection]) -> [MenuEntry] {
        guard let s = repo.status else { return [] }
        let byPath = Dictionary(s.files.map { ($0.path, $0) }, uniquingKeysWith: { a, _ in a })
        let files = scope.compactMap { byPath[$0.path] }
        guard let first = files.first, let firstSel = scope.first else { return [] }
        let paths = files.map(\.path)
        let single = files.count == 1
        let conflicts = files.filter(\.conflicted)
        let untracked = files.filter(\.isUntracked)
        let staging = repo.changes.stagingArea
        var e: [MenuEntry] = []

        e.append(.item(tr("vcs.showDiff"), icon: "file-diff", disabled: !single) {
            VCSDiffDialog.presentWorking(repo: repo, file: first, side: firstSel.side)
        })
        e.append(.item(tr("vcs.fileHistory"), icon: "history", disabled: !single || first.isUntracked || first.status == "A") {
            VCSFileHistoryDialog.present(repo: repo, path: first.path)
        })
        e.append(.divider)
        if staging {
            let stageable = files.filter { !$0.conflicted && ($0.isUntracked || !($0.worktreeStatus ?? "").isEmpty) }
            let unstageable = files.filter { !$0.conflicted && !($0.indexStatus ?? "").isEmpty }
            e.append(.item(tr("vcs.stage"), icon: "plus", disabled: stageable.isEmpty) {
                repo.perform(VCSOp.stage, VCSPathsArgs(paths: stageable.map(\.path)), title: tr("vcs.stage"), quietSuccess: true)
            })
            e.append(.item(tr("vcs.unstage"), icon: "minus", disabled: unstageable.isEmpty) {
                repo.perform(VCSOp.unstage, VCSPathsArgs(paths: unstageable.map(\.path)), title: tr("vcs.unstage"), quietSuccess: true)
            })
        }
        if !conflicts.isEmpty {
            let cp = conflicts.map(\.path)
            e.append(.item(tr("vcs.conflicts.useOurs"), icon: "arrow-left") { VCSConflictActions.resolve(repo: repo, paths: cp, side: "ours") })
            e.append(.item(tr("vcs.conflicts.useTheirs"), icon: "arrow-right") { VCSConflictActions.resolve(repo: repo, paths: cp, side: "theirs") })
            e.append(.item(tr("vcs.conflicts.mergeTool"), icon: "git-merge") { VCSConflictActions.mergeTool(repo: repo, paths: cp) })
            e.append(.item(tr("vcs.conflicts.markResolved"), icon: "check") { VCSConflictActions.resolve(repo: repo, paths: cp, side: "resolved") })
        }
        let discardable = files.filter { !$0.conflicted }
        if !discardable.isEmpty {
            let mode = staging && firstSel.side == .unstaged ? VCSDiffMode.unstaged : nil
            e.append(.item(tr("vcs.discard.ellipsis"), icon: "undo-2", destructive: true) {
                VCSDiscardDialog.present(repo: repo, files: discardable, mode: mode)
            })
        }
        if !untracked.isEmpty {
            e.append(.submenu(tr("vcs.ignore"), icon: "eye-off", ignoreEntries(repo: repo, files: untracked)))
            e.append(.item(tr("vcs.clean.ellipsis"), icon: "trash-2", destructive: true) {
                let up = untracked.map(\.path)
                Dialogs.confirm(title: tr("vcs.clean.title", up.count), message: up.prefix(10).joined(separator: "\n"),
                                ok: tr("common.delete"), danger: true) {
                    try await repo.runThrowing(VCSOp.clean, VCSCleanArgs(paths: up), title: tr("vcs.clean"))
                }
            })
        }
        e.append(.item(tr("vcs.stash.selected"), icon: "archive", disabled: files.allSatisfy(\.conflicted)) {
            VCSStashDialogs.presentNew(repo: repo, paths: paths)
        })
        e.append(.divider)
        if !Actions.isRemote {
            e.append(.item(tr("vcs.revealInFinder"), icon: "folder-open") {
                let abs = VCSPath.join(repo.root, first.path)
                Actions.revealInFinder(FileManager.default.fileExists(atPath: abs) ? abs : VCSPath.join(repo.root, VCSPath.dir(first.path)))
            })
        }
        e.append(.item(tr("vcs.copyPath"), icon: "copy") { Actions.copy(paths.joined(separator: "\n")) })
        e.append(.item(tr("vcs.copyAbsolutePath"), icon: "copy") {
            Actions.copy(paths.map { VCSPath.join(repo.root, $0) }.joined(separator: "\n"))
        })
        let plugin = VCSPluginActions.entries(ActionLocation.vcsFileMenu, repo: repo,
                                              args: ["path": .string(first.path), "paths": JSONValue(paths)])
        if !plugin.isEmpty {
            e.append(.divider)
            e += plugin
        }
        return e
    }

    static func ignoreEntries(repo: VCSRepo, files: [VCSFileChange]) -> [MenuEntry] {
        var e: [MenuEntry] = []
        let exact = files.map { "/" + ($0.path.hasSuffix("/") ? String($0.path.dropLast()) + "/" : $0.path) }
        e.append(.item(exact.count == 1 ? exact[0] : tr("vcs.ignore.files", exact.count)) {
            ignore(repo: repo, exact, file: nil)
        })
        let exts = Set(files.map { VCSPath.ext($0.path) }.filter { !$0.isEmpty })
        if exts.count == 1, let x = exts.first {
            e.append(.item("*." + x) { ignore(repo: repo, ["*." + x], file: nil) })
        }
        let dirs = Set(files.map { VCSPath.dir($0.path) }.filter { !$0.isEmpty })
        if dirs.count == 1, let d = dirs.first {
            e.append(.item("/" + d + "/") { ignore(repo: repo, ["/" + d + "/"], file: nil) })
        }
        e.append(.divider)
        e.append(.item(tr("vcs.ignore.exclude")) { ignore(repo: repo, exact, file: "exclude") })
        return e
    }

    private static func ignore(repo: VCSRepo, _ patterns: [String], file: String?) {
        repo.perform(VCSOp.ignore, VCSIgnoreArgs(patterns: patterns, file: file), title: tr("vcs.ignore"))
    }
}

// MARK: - 丢弃确认

@MainActor
enum VCSDiscardDialog {
    /// mode：nil 恢复到 HEAD；unstaged 只丢弃未暂存部分
    static func present(repo: VCSRepo, files: [VCSFileChange], mode: String?) {
        let id = "vcs-discard"
        AppModel.shared.dialogs.present(id: id) {
            Body(id: id, repo: repo, files: files, mode: mode)
        }
    }

    private struct Body: View {
        let id: String
        let repo: VCSRepo
        let files: [VCSFileChange]
        let mode: String?
        @State private var keepNew = false
        @State private var busy = false
        @State private var error: String?
        @Environment(\.theme) private var theme

        private var newFiles: [VCSFileChange] { files.filter { $0.isUntracked || $0.status == "A" } }

        var body: some View {
            PPDialog(title: tr("vcs.discard.title", files.count), width: 480, onClose: close) {
                VStack(alignment: .leading, spacing: M.md) {
                    VStack(alignment: .leading, spacing: 2) {
                        ForEach(files.prefix(12)) { f in
                            HStack(spacing: M.sm) {
                                VCSStatusLetter(status: f.status)
                                Text(f.path).font(.pp(.small)).lineLimit(1).truncationMode(.middle)
                            }
                        }
                        if files.count > 12 { Text("…").foregroundColor(theme.textTertiary) }
                    }
                    if !newFiles.isEmpty && mode != VCSDiffMode.unstaged {
                        PPAlert(tone: keepNew ? "info" : "warning",
                                title: keepNew ? tr("vcs.discard.keepNewNote", newFiles.count) : tr("vcs.discard.deleteNew", newFiles.count))
                        PPCheckbox(isOn: $keepNew, label: tr("vcs.discard.keepNew"))
                    }
                    if let error { PPAlert(tone: "danger", title: error) }
                }
            } footer: {
                PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
                PPButton(tr("vcs.discard"), kind: .danger, busy: busy) { submit() }.keyboardShortcut(.defaultAction)
            }
        }

        private func close() { AppModel.shared.dialogs.dismiss(id) }

        private func submit() {
            busy = true
            Task {
                do {
                    let args = VCSDiscardArgs(paths: files.map(\.path), mode: mode, keepNew: keepNew ? true : nil)
                    try await repo.runThrowing(VCSOp.discard, args, title: tr("vcs.discard"), quietSuccess: true)
                    close()
                } catch {
                    self.error = error.localizedDescription
                }
                busy = false
            }
        }
    }
}

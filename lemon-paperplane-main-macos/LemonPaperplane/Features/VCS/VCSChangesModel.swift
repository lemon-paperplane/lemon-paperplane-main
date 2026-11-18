import Combine
import PaperplaneKit
import SwiftUI

/// 提交草稿
@MainActor
final class VCSCommitDraft: ObservableObject {
    @Published var message = ""
    @Published var amend = false
    @Published var signOff = false
    @Published var noVerify = false
    @Published var author = ""
    @Published var committing = false
}

/// 变更列表里的一侧（暂存区模式下同一文件可能同时出现在已暂存与未暂存两组）
enum VCSChangeSide: String, Hashable {
    case head, staged, unstaged, untracked, conflict
}

struct VCSChangeSelection: Hashable {
    var path: String
    var side: VCSChangeSide
}

/// 变更与提交的界面状态（每个仓库一份）
@MainActor
final class VCSChangesModel: ObservableObject {
    let root: String

    /// 提交信息与选项（单独发布：打字时不重绘变更列表和差异）
    let draft = VCSCommitDraft()

    /// 非暂存区模式：不提交的已跟踪文件
    @Published var excluded: Set<String> = []
    /// 非暂存区模式：要提交的未跟踪文件
    @Published var includedUntracked: Set<String> = []
    /// 非暂存区模式：文件 → 不提交的块下标（基于 mode=head、上下文 3 的 diff）
    @Published var excludedHunks: [String: Set<Int>] = [:]
    /// 文件的块数（diff 加载后记录）
    var hunkCounts: [String: Int] = [:]
    /// 新出现的未跟踪文件默认勾选（工作区总览里「默认提交全部改动」）
    var autoIncludeUntracked = false
    private var seenUntracked: Set<String> = []

    /// 显示差异的文件
    @Published var selection: VCSChangeSelection?
    /// 多选（右键菜单作用范围）
    @Published var multi: Set<VCSChangeSelection> = []
    @Published var collapsed: Set<String> = []
    @Published var groupByDir = VCSPrefs.groupByDirectory {
        didSet { VCSPrefs.groupByDirectory = groupByDir }
    }
    @Published var stagingArea = VCSPrefs.stagingArea {
        didSet { VCSPrefs.stagingArea = stagingArea; selection = nil; multi = [] }
    }

    let diff = VCSDiffModel()
    /// ⌘K：聚焦提交信息
    let focusMessage = PassthroughSubject<Void, Never>()

    init(root: String) { self.root = root }

    /// 状态刷新后：去掉已消失文件的勾选与选中
    func sync(with s: VCSStatus) {
        let paths = Set(s.files.map(\.path))
        if autoIncludeUntracked {
            let fresh = s.files.filter { $0.isUntracked && !seenUntracked.contains($0.path) }.map(\.path)
            if !fresh.isEmpty { includedUntracked.formUnion(fresh) }
        }
        seenUntracked = Set(s.files.filter(\.isUntracked).map(\.path))
        if !excluded.isSubset(of: paths) { excluded.formIntersection(paths) }
        if !includedUntracked.isSubset(of: paths) { includedUntracked.formIntersection(paths) }
        if excludedHunks.keys.contains(where: { !paths.contains($0) }) {
            excludedHunks = excludedHunks.filter { paths.contains($0.key) }
        }
        if let sel = selection {
            let f = s.files.first { $0.path == sel.path }
            let still: Bool
            switch sel.side {
            case .staged: still = f.map { ($0.indexStatus ?? "").isEmpty == false && !$0.conflicted } ?? false
            case .unstaged: still = f.map { let w = $0.worktreeStatus ?? ""; return !w.isEmpty && w != "?" && !$0.conflicted } ?? false
            case .conflict: still = f?.conflicted ?? false
            default: still = f != nil
            }
            if !still { selection = nil } else { diff.reload() }
        }
        multi = multi.filter { paths.contains($0.path) }
    }

    // MARK: 分组

    struct Groups {
        var conflicts: [VCSFileChange] = []
        var tracked: [VCSFileChange] = []
        var untracked: [VCSFileChange] = []
        var staged: [VCSFileChange] = []
        var unstaged: [VCSFileChange] = []
    }

    func groups(_ files: [VCSFileChange]) -> Groups {
        var g = Groups()
        for f in files {
            if f.conflicted { g.conflicts.append(f); continue }
            if f.isUntracked { g.untracked.append(f); continue }
            g.tracked.append(f)
            if !(f.indexStatus ?? "").isEmpty { g.staged.append(f) }
            if let w = f.worktreeStatus, !w.isEmpty, w != "?" { g.unstaged.append(f) }
        }
        return g
    }

    // MARK: 勾选（非暂存区模式）

    func isIncluded(_ f: VCSFileChange) -> Bool? {
        if f.isUntracked { return includedUntracked.contains(f.path) }
        if excluded.contains(f.path) { return false }
        if let ex = excludedHunks[f.path], !ex.isEmpty { return nil }
        return true
    }

    func setIncluded(_ f: VCSFileChange, _ on: Bool) {
        if f.isUntracked {
            if on { includedUntracked.insert(f.path) } else { includedUntracked.remove(f.path) }
        } else {
            if on { excluded.remove(f.path) } else { excluded.insert(f.path) }
            excludedHunks[f.path] = nil
        }
    }

    func groupState(_ files: [VCSFileChange]) -> Bool? {
        guard !files.isEmpty else { return false }
        let states = files.map { isIncluded($0) }
        if states.allSatisfy({ $0 == true }) { return true }
        if states.allSatisfy({ $0 == false }) { return false }
        return nil
    }

    func toggleGroup(_ files: [VCSFileChange]) {
        let on = groupState(files) != true
        for f in files { setIncluded(f, on) }
    }

    func setHunk(path: String, index: Int, included: Bool, total: Int) {
        var ex = excludedHunks[path] ?? []
        if included { ex.remove(index) } else { ex.insert(index) }
        hunkCounts[path] = total
        if ex.count >= total {
            excludedHunks[path] = nil
            excluded.insert(path)
        } else {
            excludedHunks[path] = ex.isEmpty ? nil : ex
            excluded.remove(path)
        }
    }

    /// 非暂存区模式的提交内容：整文件路径 + 部分块
    func commitPlan(_ files: [VCSFileChange]) -> (paths: [String], hunks: [String: [Int]]) {
        var paths: [String] = []
        var hunks: [String: [Int]] = [:]
        for f in files where !f.conflicted {
            guard isIncluded(f) != false else { continue }
            if let ex = excludedHunks[f.path], !ex.isEmpty, let n = hunkCounts[f.path], n > 0 {
                let inc = (0..<n).filter { !ex.contains($0) }
                if !inc.isEmpty { hunks[f.path] = inc }
            } else {
                paths.append(f.path)
            }
        }
        return (paths, hunks)
    }

    // MARK: 选中与差异

    func select(_ sel: VCSChangeSelection, file: VCSFileChange, repo: VCSRepo) {
        if VCSModifiers.command {
            if multi.isEmpty, let cur = selection { multi.insert(cur) }
            if multi.contains(sel) { multi.remove(sel) } else { multi.insert(sel) }
        } else {
            multi = []
        }
        selection = sel
        diff.set(target(sel, file: file), repo: repo)
    }

    func target(_ sel: VCSChangeSelection, file: VCSFileChange) -> VCSDiffTarget {
        let mode: String
        switch sel.side {
        case .staged: mode = VCSDiffMode.staged
        case .unstaged: mode = VCSDiffMode.unstaged
        default: mode = VCSDiffMode.head
        }
        return VCSDiffTarget(path: file.path, oldPath: file.oldPath, kind: .working(mode), status: file.status)
    }

    var hunkMode: VCSHunkMode {
        guard let sel = selection else { return .none }
        switch sel.side {
        case .head: return stagingArea ? .none : .include
        case .unstaged: return .stage
        case .staged: return .unstage
        default: return .none
        }
    }

    /// 右键作用的文件：在多选里就用多选，否则只用这一个
    func scope(for sel: VCSChangeSelection) -> [VCSChangeSelection] {
        multi.contains(sel) && multi.count > 1 ? Array(multi) : [sel]
    }
}

import PaperplaneKit
import SwiftUI

// 冲突解决：按文件「用我的 / 用他们的 / 在外部工具中合并 / 标记已解决」，全部解决后「继续」。不做内置三方合并编辑器。

@MainActor
enum VCSConflictActions {
    static func resolve(repo: VCSRepo, paths: [String], side: String) {
        guard let first = paths.first else { return }
        let title: String
        switch side {
        case "ours": title = tr("vcs.conflicts.useOurs")
        case "theirs": title = tr("vcs.conflicts.useTheirs")
        default: title = tr("vcs.conflicts.markResolved")
        }
        let args = VCSResolveArgs(path: first, side: side, paths: paths.count > 1 ? paths : nil)
        repo.perform(VCSOp.resolve, args, title: title, quietSuccess: true, extra: { r in
            guard r.reason == VCSReason.markersRemain else { return [] }
            return [.init(title: tr("vcs.conflicts.markAnyway"), danger: true) {
                repo.notice = nil
                var a = args
                a.force = true
                repo.perform(VCSOp.resolve, a, title: title, quietSuccess: true)
            }]
        })
    }

    static func mergeTool(repo: VCSRepo, paths: [String]) {
        repo.perform(VCSOp.mergeTool, VCSPathsArgs(paths: paths), title: tr("vcs.conflicts.mergeTool"), long: true, quietSuccess: true)
    }

    /// 继续进行中的操作（未解决冲突 → 去冲突页；空提交 → 跳过）
    static func continueOp(repo: VCSRepo) {
        repo.perform(VCSOp.continue, VCSNoArgs(), title: tr("vcs.op.continue"), long: true)
    }

    static func abort(repo: VCSRepo) {
        let op = VCSStyle.operationTitle(repo.status?.operation)
        Dialogs.confirm(title: tr("vcs.op.abortTitle", op), message: tr("vcs.op.abortMessage"), ok: tr("vcs.op.abort"), danger: true) {
            try await repo.runThrowing(VCSOp.abort, VCSNoArgs(), title: tr("vcs.op.abort"))
        }
    }

    static func skip(repo: VCSRepo) {
        repo.perform(VCSOp.skip, VCSNoArgs(), title: tr("vcs.op.skip"), long: true)
    }

    /// 我方 / 对方指的是谁（变基时 ours 是变基目标、theirs 是正在重放的提交）
    static func sides(_ s: VCSStatus) -> (ours: String, theirs: String) {
        let d = s.operationDetail
        switch s.operation {
        case "rebase":
            return (VCSStyle.shortHash(d?.onto).isEmpty ? tr("vcs.conflicts.upstream") : VCSStyle.shortHash(d?.onto),
                    VCSStyle.shortHash(d?.current).isEmpty ? (d?.headName ?? "") : VCSStyle.shortHash(d?.current))
        default:
            let ours = s.branch.isEmpty ? VCSStyle.shortHash(s.headHash) : s.branch
            return (ours, VCSStyle.shortHash(d?.current).isEmpty ? tr("vcs.conflicts.incoming") : VCSStyle.shortHash(d?.current))
        }
    }
}

struct VCSConflictsPage: View {
    @ObservedObject var repo: VCSRepo
    @StateObject private var diff = VCSDiffModel()
    @State private var selected: String?
    @State private var tools: VCSMergeTools?
    @State private var listWidth: CGFloat = 460
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        let st = repo.status ?? VCSStatus()
        let files = st.files.filter(\.conflicted)
        PPSplitView(leftWidth: $listWidth, minLeft: 320, maxLeft: 760) {
            VStack(spacing: 0) {
                header(st, files: files)
                PPDivider(subtle: true)
                if files.isEmpty {
                    resolvedState(st)
                } else {
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 1) {
                            ForEach(files) { f in row(f) }
                        }
                        .padding(M.xs)
                    }
                }
            }
        } right: {
            VCSDiffView(model: diff, changes: repo.changes)
        }
        .task { tools = try? await repo.api().vcsMergeTools(projectId: repo.projectId, root: repo.root) }
        .onChange(of: repo.status?.files) { _ in
            if let s = selected, !(repo.status?.files.contains { $0.path == s && $0.conflicted } ?? false) {
                selected = nil
                diff.set(nil, repo: repo)
            }
        }
    }

    // MARK: 头部

    private func header(_ st: VCSStatus, files: [VCSFileChange]) -> some View {
        let sides = VCSConflictActions.sides(st)
        return VStack(alignment: .leading, spacing: M.sm) {
            HStack(spacing: M.sm) {
                Text(tr("vcs.conflicts.title", files.count)).font(.pp(.title))
                PPInfoButton(text: tr("vcs.conflicts.info"))
                Spacer()
                toolPicker
            }
            HStack(spacing: M.lg) {
                HStack(spacing: M.xs) {
                    Text(tr("vcs.conflicts.ours")).foregroundColor(theme.textTertiary)
                    Text(sides.ours).font(.pp(.monoSmall))
                }
                HStack(spacing: M.xs) {
                    Text(tr("vcs.conflicts.theirs")).foregroundColor(theme.textTertiary)
                    Text(sides.theirs).font(.pp(.monoSmall))
                }
                Spacer()
            }
            .font(.pp(.small))
            if !files.isEmpty {
                HStack(spacing: M.sm) {
                    PPButton(tr("vcs.conflicts.allOurs"), size: .small) {
                        VCSConflictActions.resolve(repo: repo, paths: files.map(\.path), side: "ours")
                    }
                    PPButton(tr("vcs.conflicts.allTheirs"), size: .small) {
                        VCSConflictActions.resolve(repo: repo, paths: files.map(\.path), side: "theirs")
                    }
                    PPButton(tr("vcs.conflicts.mergeAll"), icon: "git-merge", size: .small) {
                        VCSConflictActions.mergeTool(repo: repo, paths: [])
                    }
                    .disabled(tools?.tools.isEmpty ?? false)
                }
            }
        }
        .padding(M.md)
    }

    /// 外部合并工具：写入 git 插件设置 mergeTool
    private var toolPicker: some View {
        let plugin = repo.session.roots.first { $0.path == repo.root }?.plugin ?? "git"
        let settings = data.contributions.settings.first { $0.plugin == plugin }
        let current = settings?.values["mergeTool"]?.stringValue ?? "auto"
        var options: [PPDropdown<String>.Option] = [.init(value: "auto", title: tr("vcs.conflicts.toolAuto"))]
        options += (tools?.tools ?? []).map { .init(value: $0.id, title: $0.name) }
        if !options.contains(where: { $0.value == current }) { options.append(.init(value: current, title: current)) }
        return HStack(spacing: M.xs) {
            PPIcon("wrench", size: M.iconSmall, color: theme.iconSecondary)
            PPDropdown(options: options, selection: Binding(get: { current }, set: { v in
                var values = settings?.values ?? [:]
                values["mergeTool"] = .string(v)
                AppModel.shared.perform(tr("vcs.conflicts.toolFailed")) { api in
                    try await api.updatePluginSettings(plugin, values: values)
                    await AppModel.shared.reloadPlugins()
                }
            }), size: .small, minWidth: 140, borderless: true)
        }
        .help(tr("vcs.conflicts.tool"))
    }

    // MARK: 行

    private func row(_ f: VCSFileChange) -> some View {
        VCSTableRow(selected: selected == f.path, onTap: {
            selected = f.path
            diff.set(VCSDiffTarget(path: f.path, kind: .working(VCSDiffMode.head), status: "U"), repo: repo)
        }, onDoubleTap: { VCSConflictActions.mergeTool(repo: repo, paths: [f.path]) }) {
            HStack(spacing: M.sm) {
                PPIcon("triangle-alert", size: M.iconSmall, color: theme.danger)
                VStack(alignment: .leading, spacing: 0) {
                    Text(VCSPath.name(f.path)).lineLimit(1)
                    Text(VCSStyle.conflictTitle(f.conflictType) + (VCSPath.dir(f.path).isEmpty ? "" : "  ·  " + VCSPath.dir(f.path)))
                        .font(.pp(.caption)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.middle)
                }
                Spacer(minLength: M.sm)
                PPButton(tr("vcs.conflicts.ours.short"), kind: .ghost, size: .small) {
                    VCSConflictActions.resolve(repo: repo, paths: [f.path], side: "ours")
                }
                .help(tr("vcs.conflicts.useOurs"))
                PPButton(tr("vcs.conflicts.theirs.short"), kind: .ghost, size: .small) {
                    VCSConflictActions.resolve(repo: repo, paths: [f.path], side: "theirs")
                }
                .help(tr("vcs.conflicts.useTheirs"))
                PPIconButton("git-merge", help: tr("vcs.conflicts.mergeTool"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    VCSConflictActions.mergeTool(repo: repo, paths: [f.path])
                }
                PPIconButton("check", help: tr("vcs.conflicts.markResolved"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                    VCSConflictActions.resolve(repo: repo, paths: [f.path], side: "resolved")
                }
            }
            .padding(.horizontal, M.sm)
        }
        .frame(height: 38)
        .ppContextMenu { VCSFileMenu.entries(repo: repo, scope: [VCSChangeSelection(path: f.path, side: .conflict)]) }
    }

    @ViewBuilder private func resolvedState(_ st: VCSStatus) -> some View {
        if st.operation != nil {
            PPEmptyState(icon: "circle-check", title: tr("vcs.conflicts.allResolved"), actionTitle: tr("vcs.op.continue"),
                         actionIcon: "play") {
                VCSConflictActions.continueOp(repo: repo)
            }
        } else {
            PPEmptyState(icon: "circle-check", title: tr("vcs.conflicts.none"))
        }
    }
}

import PaperplaneKit
import SwiftUI

/// 工程卡片
struct ProjectCard: View {
    let project: ProjectSummary
    var showWorkspace = false
    @ObservedObject private var nav = AppModel.shared.nav
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme
    @State private var hover = false

    private var p: ProjectSummary { project }

    private var selectedConfigId: String? {
        if let c = nav.selectedConfig[p.id], p.configs.contains(where: { $0.id == c }) { return c }
        return p.defaultConfigId ?? p.configs.first?.id
    }

    private var currentRun: RunBrief? {
        if let c = selectedConfigId, let r = p.runs.first(where: { $0.configId == c }) { return r }
        return p.runs.first
    }

    var body: some View {
        VStack(alignment: .leading, spacing: M.md) {
            header
            if !p.facets.isEmpty {
                FlowRow(spacing: M.xs) {
                    ForEach(p.facets.prefix(6)) { FacetBadgeView(facet: $0) }
                }
            }
            if !p.info.isEmpty {
                HStack(spacing: M.md) {
                    ForEach(Array(p.info.prefix(3).enumerated()), id: \.offset) { _, i in
                        HStack(spacing: M.xs) {
                            Text(i.label).foregroundColor(theme.textTertiary)
                            Text(i.value).foregroundColor(theme.textSecondary)
                        }
                        .font(.pp(.small))
                        .lineLimit(1)
                    }
                }
            }
            PPDivider(subtle: true)
            runArea
            if let vcs = p.vcs, !vcs.branch.isEmpty { gitLine(vcs) }
            if let busy = p.busy, !busy.isEmpty {
                HStack(spacing: M.sm) {
                    PPSpinner(size: M.iconSmall, color: theme.accent)
                    Text(busy).font(.pp(.small)).foregroundColor(theme.textSecondary).lineLimit(1)
                }
            }
        }
        .padding(M.lg)
        // 撑满网格单元：同一排的卡片一样高（每排按最高的那张），内容靠上
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous).fill(theme.bgSurface)
            .shadow(color: .black.opacity(hover ? (theme.isDark ? 0.3 : 0.07) : 0), radius: 6, y: 2))
        .overlay(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous)
            .strokeBorder(hover ? theme.borderStrong : theme.border, lineWidth: 1))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture { nav.openProject(p.id) }
        .ppContextMenu { CardMenu.entries(for: p) }
    }

    private var header: some View {
        HStack(alignment: .top, spacing: M.sm) {
            StatusDot(state: p.status).padding(.top, 5)
            VStack(alignment: .leading, spacing: 2) {
                Text(p.name).font(.pp(.title)).lineLimit(1)
                HStack(spacing: M.xs) {
                    if showWorkspace, let ws = data.workspace(p.workspaceId) {
                        Text(ws.name).foregroundColor(theme.textSecondary)
                        Text("·").foregroundColor(theme.textTertiary)
                    }
                    Text(Format.abbreviatePath(p.path)).foregroundColor(theme.textTertiary)
                        .lineLimit(1).truncationMode(.middle)
                }
                .font(.pp(.small))
            }
            Spacer(minLength: M.xs)
            ProblemsIndicator(project: p)
            PPMenuButton(alignment: .trailing, entries: { CardMenu.entries(for: p) }) {
                PPIcon("ellipsis", size: M.icon, color: theme.iconSecondary)
                    .frame(width: M.iconButtonSmall, height: M.iconButtonSmall)
                    .contentShape(Rectangle())
            }
            .help(tr("common.more"))
        }
    }

    @ViewBuilder private var runArea: some View {
        if p.configs.isEmpty {
            Text(tr("card.noConfig")).font(.pp(.small)).foregroundColor(theme.textTertiary).frame(height: M.controlHeightSmall)
        } else {
            let cid = selectedConfigId
            let run = cid.flatMap { c in p.runs.first { $0.configId == c && RunState.isActive($0.state) } }
            VStack(alignment: .leading, spacing: M.sm) {
                HStack(spacing: M.xs) {
                    PPDropdown(options: p.configs.map { .init(value: $0.id, title: $0.name, icon: IconSource.parse($0.icon)) },
                               selection: Binding(get: { cid ?? "" }, set: { nav.selectedConfig[p.id] = $0 }),
                               size: .small, minWidth: 100, maxWidth: 220)
                    Spacer(minLength: M.xs)
                    if let run {
                        PPIconButton("rotate-cw", help: tr("action.restart"), size: M.iconButtonSmall) { Actions.restart(runId: run.id) }
                        PPIconButton("square", help: tr("action.stop"), size: M.iconButtonSmall, tint: theme.danger) { Actions.stop(runId: run.id) }
                    } else {
                        PPIconButton("play", help: tr("action.run"), size: M.iconButtonSmall, tint: theme.success) {
                            Actions.run(projectId: p.id, configId: cid)
                        }
                    }
                }
                // 运行中：地址 + 资源；没在运行：「未运行 · 上次运行 …」占住同一行，卡片高度不随启停跳动
                if let r = currentRun, RunState.isActive(r.state) {
                    HStack(spacing: M.md) {
                        if let url = r.urls.first {
                            PPLink(title: Format.shortURL(url), mono: true, icon: "external-link") { Actions.openURL(url) }
                        } else {
                            Text(RunStatusStyle.title(r.state)).font(.pp(.small)).foregroundColor(theme.textSecondary)
                        }
                        Spacer(minLength: M.xs)
                        // 只有这一小块随 stats 重绘（每个运行一个可观察对象），卡片本身不动
                        RunResourceInline(runId: r.id, startedAt: AppModel.shared.runs.runs[r.id]?.startedAt)
                    }
                    .frame(height: ProjectCard.statusLineHeight)
                } else {
                    IdleRunLine(lastRun: p.lastRun)
                        .frame(height: ProjectCard.statusLineHeight)
                }
            }
        }
    }

    /// 运行状态行（地址 + 资源 / 未运行）的固定高度
    static let statusLineHeight: CGFloat = 18

    private func gitLine(_ v: VCSBrief) -> some View {
        HStack(spacing: M.sm) {
            PPIcon("git-branch", size: 12, color: theme.iconSecondary)
            Text(v.branch).lineLimit(1).truncationMode(.middle)
            if v.ahead > 0 { HStack(spacing: 1) { PPIcon("arrow-up", size: 11); Text("\(v.ahead)") } }
            if v.behind > 0 { HStack(spacing: 1) { PPIcon("arrow-down", size: 11); Text("\(v.behind)") } }
            if v.changes > 0 {
                Text(tr("card.changes", v.changes)).foregroundColor(theme.textTertiary)
            }
            if v.conflicts > 0 {
                PPBadge(text: tr("card.conflicts", v.conflicts), tone: "danger")
            }
            Spacer(minLength: 0)
        }
        .font(.pp(.small))
        .foregroundColor(theme.textSecondary)
    }
}

/// 没在运行时的状态行：未运行 · 上次运行 3 小时前；上次异常退出用红色写退出码。每分钟刷新相对时间。
private struct IdleRunLine: View {
    let lastRun: LastRun?
    @Environment(\.theme) private var theme

    var body: some View {
        TimelineView(.periodic(from: .now, by: 60)) { _ in
            HStack(spacing: M.xs) {
                PPIcon("power", size: 12, color: theme.iconSecondary)
                Text(tr("card.idle")).foregroundColor(theme.textTertiary)
                if let lr = lastRun, let at = lr.endedAt {
                    Text("·").foregroundColor(theme.textTertiary)
                    if lr.failed {
                        Text(lr.exitCode != 0 ? tr("card.lastFailed", VCSDate.short(at), lr.exitCode) : tr("card.lastStartFailed", VCSDate.short(at)))
                            .foregroundColor(theme.danger)
                    } else {
                        Text(tr("card.lastRun", VCSDate.short(at))).foregroundColor(theme.textTertiary)
                    }
                }
                Spacer(minLength: 0)
            }
            .font(.pp(.small))
            .lineLimit(1)
            .help(lastRun?.name ?? "")
        }
    }
}

/// 卡片右键 / 更多菜单
@MainActor
enum CardMenu {
    static func entries(for p: ProjectSummary) -> [MenuEntry] {
        let model = AppModel.shared
        let facetIds = Set(p.facets.map(\.id))
        let plugins = Set(p.facets.map(\.plugin))
        let actions = Actions.applicable(model.data.contributions.actions(at: ActionLocation.cardMenu), facets: facetIds, plugins: plugins)
        let others = model.data.workspaces.filter { $0.id != p.workspaceId }
        var e: [MenuEntry] = [
            .item(tr("card.open"), icon: "arrow-right") { model.nav.openProject(p.id) },
            .item(tr("open.finder"), icon: "folder-open") { Actions.open(p.id, with: "finder") },
            .item(tr("open.terminal"), icon: "square-terminal") { Actions.open(p.id, with: "terminal") },
            .item(tr("open.idea"), icon: "code-xml") { Actions.open(p.id, with: "idea") },
            .item(tr("open.vscode"), icon: "code-xml") { Actions.open(p.id, with: "vscode") },
            .item(tr("editorWindow.open"), icon: "file-pen") { EditorWindows.shared.open(projectId: p.id) },
            .divider,
        ]
        if p.hasActiveRun {
            e.append(.item(tr("card.stopAll"), icon: "square") { Actions.stopProject(p.id) })
        }
        e.append(.item(tr("project.reimport"), icon: "refresh-cw") { Actions.reimport(p.id) })
        if !actions.isEmpty {
            e.append(.divider)
            e += Actions.menuEntries(actions, projectId: p.id)
        }
        e.append(.divider)
        e.append(.submenu(tr("card.moveTo"), icon: "folder-symlink", others.map { ws in
            .item(ws.name, icon: "layers") { Actions.move(p.id, to: ws.id) }
        }))
        e.append(.item(tr("project.rename"), icon: "pencil") { Actions.rename(p) })
        e.append(.item(tr("project.remove"), icon: "trash-2", destructive: true) { Actions.remove(p) })
        return e
    }
}

/// 问题提示：缺工具链 / 依赖未装 / 诊断，点开列出并可一键修复
struct ProblemsIndicator: View {
    let project: ProjectSummary
    @State private var open = false
    @Environment(\.theme) private var theme

    private var count: Int {
        project.problems + project.missingToolchains.count + ((project.dependencies == "missing" || project.dependencies == "outdated") ? 1 : 0)
    }

    var body: some View {
        if count > 0 {
            let sev = project.problemSeverity ?? (project.missingToolchains.isEmpty ? Severity.warning : Severity.error)
            let c = theme.severity(sev)
            Button { open.toggle() } label: {
                HStack(spacing: 3) {
                    PPIcon(sev == Severity.error ? "circle-alert" : "triangle-alert", size: M.iconSmall, color: c.fg)
                    Text("\(max(count, project.problems))").font(.pp(.small).monospacedDigit()).foregroundColor(c.fg)
                }
                .padding(.horizontal, M.sm)
                .frame(height: M.iconButtonSmall)
                .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(c.bg))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(tr("problems.title"))
            .ppPopover(isPresented: $open, alignment: .trailing) {
                ProblemsPopover(projectId: project.id) { open = false }
            }
        }
    }
}

struct ProblemsPopover: View {
    let projectId: String
    var close: () -> Void
    @State private var problems: [Diagnosis]?
    @State private var error: String?
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(tr("problems.title")).font(.pp(.title)).padding(.horizontal, M.md).frame(height: 36)
            PPDivider(subtle: true)
            Group {
                if let problems {
                    if problems.isEmpty {
                        Text(tr("problems.none")).foregroundColor(theme.textSecondary).padding(M.lg)
                    } else {
                        ScrollView {
                            VStack(alignment: .leading, spacing: M.xs) {
                                ForEach(problems, id: \.stableKey) { d in
                                    DiagnosisRow(diagnosis: d, runId: nil, projectId: projectId, compact: true) {
                                        close()
                                    }
                                }
                            }
                            .padding(M.sm)
                        }
                        .frame(maxHeight: 420)
                    }
                } else if let error {
                    Text(error).foregroundColor(theme.danger).padding(M.lg)
                } else {
                    PPSpinner().padding(M.lg)
                }
            }
        }
        .frame(width: 420)
        .task {
            do { problems = try await AppModel.shared.client().problems(projectId) } catch { self.error = error.localizedDescription }
        }
    }
}

/// 一条诊断：严重度、标题、说明、可展开的详情、修复按钮
struct DiagnosisRow: View {
    let diagnosis: Diagnosis
    var runId: String?
    var projectId: String?
    var compact = false
    var onFixed: (() -> Void)?
    @State private var expanded = false
    @Environment(\.theme) private var theme

    var body: some View {
        let c = theme.severity(diagnosis.severity)
        VStack(alignment: .leading, spacing: M.sm) {
            HStack(alignment: .top, spacing: M.sm) {
                PPIcon(diagnosis.severity == Severity.error ? "circle-alert" : (diagnosis.severity == Severity.warning ? "triangle-alert" : "info"),
                       size: M.icon, color: c.fg)
                VStack(alignment: .leading, spacing: 2) {
                    Text(diagnosis.title).font(.pp(.bodyStrong)).fixedSize(horizontal: false, vertical: true)
                    if let m = diagnosis.message, !m.isEmpty {
                        Text(m).font(.pp(.small)).foregroundColor(theme.textSecondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                Spacer(minLength: M.xs)
                if hasDetail {
                    PPIconButton(expanded ? "chevron-up" : "chevron-down", help: tr("diagnosis.detail"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                        expanded.toggle()
                    }
                }
            }
            if expanded {
                Text([diagnosis.detail, diagnosis.line].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: "\n\n"))
                    .font(.pp(.monoSmall))
                    .foregroundColor(theme.textSecondary)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(M.sm)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(theme.bgCode))
            }
            if !diagnosis.fixes.isEmpty {
                FlowRow(spacing: M.sm) {
                    ForEach(sortedFixes) { f in
                        PPButton(f.title, kind: f.recommended ? .primary : .secondary, size: .small) {
                            Actions.applyFix(f, diagnosis: diagnosis, runId: runId, projectId: projectId, onDone: onFixed)
                        }
                    }
                }
                .padding(.leading, M.icon + M.sm)
            }
        }
        .padding(compact ? M.sm : M.md)
        .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(compact ? Color.clear : c.bg))
        .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(compact ? Color.clear : c.border, lineWidth: 1))
    }

    private var hasDetail: Bool {
        (diagnosis.detail?.isEmpty == false) || (diagnosis.line?.isEmpty == false)
    }

    private var sortedFixes: [Fix] {
        diagnosis.fixes.sorted { $0.recommended && !$1.recommended }
    }
}

/// 自动换行的横向排列（徽标、按钮）
struct FlowRow: Layout {
    var spacing: CGFloat = 4

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let maxW = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, rowH: CGFloat = 0, width: CGFloat = 0
        for v in subviews {
            let s = v.sizeThatFits(.unspecified)
            if x > 0 && x + s.width > maxW {
                y += rowH + spacing
                x = 0
                rowH = 0
            }
            x += s.width + spacing
            rowH = max(rowH, s.height)
            width = max(width, x - spacing)
        }
        return CGSize(width: proposal.width ?? width, height: y + rowH)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, rowH: CGFloat = 0
        for v in subviews {
            let s = v.sizeThatFits(.unspecified)
            if x > bounds.minX && x + s.width > bounds.maxX {
                y += rowH + spacing
                x = bounds.minX
                rowH = 0
            }
            v.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(s))
            x += s.width + spacing
            rowH = max(rowH, s.height)
        }
    }
}

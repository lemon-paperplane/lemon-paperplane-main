import PaperplaneKit
import SwiftUI

// 交互式变基编辑器（IDEA 的 Interactively Rebase from Here）：列出 base..HEAD 的提交（旧的在上），
// 每行 pick / reword / squash / fixup / drop，拖动或按钮调整顺序，reword / squash 可改信息，校验后执行。

@MainActor
enum VCSRebaseEditor {
    /// from：选中的提交（包含它，base 为它的第一个父提交；根提交时 --root）
    static func present(repo: VCSRepo, from: VCSCommit) {
        let id = "vcs-rebase"
        AppModel.shared.dialogs.present(id: id) {
            Body(id: id, repo: repo, from: from)
        }
    }

    fileprivate struct Step: Identifiable, Equatable {
        let commit: VCSCommit
        var action = "pick"
        var message: String?
        /// 原提交的完整信息（reword 开始编辑时取）
        var original: String?
        var editing = false
        var id: String { commit.hash }
    }

    static let actions = ["pick", "reword", "squash", "fixup", "drop"]

    private struct Body: View {
        let id: String
        let repo: VCSRepo
        let from: VCSCommit
        @State private var steps: [Step] = []
        @State private var loading = true
        @State private var error: String?
        @State private var busy = false
        @State private var dragging: String?
        @State private var dragOffset: CGFloat = 0
        @Environment(\.theme) private var theme

        private var base: String { from.parents.first ?? "" }
        private static let rowH: CGFloat = 34

        var body: some View {
            VCSLargeDialog(id: id, title: tr("vcs.rebase.title", from.short), maxWidth: 820, maxHeight: 680) {
                VStack(spacing: 0) {
                    HStack(spacing: M.sm) {
                        Text(tr("vcs.rebase.onto", base.isEmpty ? tr("vcs.rebase.root") : String(base.prefix(8))))
                            .foregroundColor(theme.textSecondary)
                        PPInfoButton(text: tr("vcs.rebase.info"))
                        Spacer()
                        Text(tr("vcs.rebase.count", steps.count)).font(.pp(.small)).foregroundColor(theme.textTertiary)
                    }
                    .padding(.horizontal, M.xl)
                    .frame(height: M.toolbarHeight)
                    PPDivider(subtle: true)
                    if loading {
                        PPSpinner().frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        ScrollView {
                            VStack(spacing: 0) {
                                ForEach(steps.indices, id: \.self) { i in
                                    row(i)
                                }
                            }
                            .padding(.vertical, M.xs)
                        }
                    }
                    if let msg = validation ?? error {
                        PPAlert(tone: "danger", title: msg).padding(.horizontal, M.lg).padding(.bottom, M.md)
                    }
                }
            } footer: {
                PPButton(tr("vcs.rebase.reset"), kind: .ghost) { Task { await load() } }
                    .disabled(loading || busy)
                PPButton(tr("common.cancel")) { AppModel.shared.dialogs.dismiss(id) }.keyboardShortcut(.cancelAction)
                PPButton(tr("vcs.rebase.start"), kind: .primary, busy: busy, action: submit)
                    .disabled(loading || validation != nil || steps.isEmpty)
            }
            .task { await load() }
        }

        private func load() async {
            loading = true
            error = nil
            do {
                let ref = base.isEmpty ? "HEAD" : "\(base)..HEAD"
                let log = try await repo.api().vcsLog(projectId: repo.projectId, root: repo.root,
                                                      VCSLogArgs(ref: ref, limit: 500, noGraph: true))
                if log.hasMore {
                    error = tr("vcs.rebase.tooMany")
                } else if log.commits.contains(where: { $0.parents.count > 1 }) {
                    error = tr("vcs.rebase.merges")
                } else if !log.commits.contains(where: { $0.hash == from.hash }) {
                    error = tr("vcs.rebase.notAncestor")
                }
                steps = log.commits.reversed().map { Step(commit: $0) }
            } catch {
                self.error = error.localizedDescription
            }
            loading = false
        }

        /// 校验：第一个保留的提交不能是 squash/fixup；reword/squash 的新信息不能为空
        private var validation: String? {
            let kept = steps.filter { $0.action != "drop" }
            if let f = kept.first, f.action == "squash" || f.action == "fixup" { return tr("vcs.rebase.firstSquash") }
            if steps.contains(where: { ($0.action == "reword") && ($0.message ?? $0.commit.subject).trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) {
                return tr("vcs.rebase.emptyMessage")
            }
            return nil
        }

        private func row(_ i: Int) -> some View {
            let s = steps[i]
            let isDragging = dragging == s.id
            return VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: M.sm) {
                    PPIcon("grip-vertical", size: M.iconSmall, color: theme.iconSecondary)
                        .frame(width: 18, height: Self.rowH)
                        .contentShape(Rectangle())
                        .gesture(dragGesture(i))
                        .onHover { inside in if inside { NSCursor.openHand.push() } else { NSCursor.pop() } }
                    PPDropdown(options: VCSRebaseEditor.actions.map { .init(value: $0, title: tr("vcs.rebase.action.\($0)")) },
                               selection: Binding(get: { steps[i].action }, set: { v in
                                   steps[i].action = v
                                   if v == "reword" { ensureMessage(steps[i].id) }
                                   if v == "reword" || v == "squash" { steps[i].editing = true }
                               }), size: .small, minWidth: 96, maxWidth: 96)
                    Text(s.commit.short).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                    Text(firstLine(s.message ?? s.commit.subject))
                        .lineLimit(1)
                        .strikethrough(s.action == "drop", color: theme.textTertiary)
                        .foregroundColor(s.action == "drop" ? theme.textTertiary : (s.message != nil && s.message != s.original ? theme.accent : theme.textPrimary))
                    Spacer(minLength: M.sm)
                    if s.action == "reword" || s.action == "squash" {
                        PPIconButton("pencil", help: tr("vcs.rebase.editMessage"), size: M.iconButtonSmall, iconSize: M.iconSmall,
                                     active: s.editing) {
                            if s.action == "reword" { ensureMessage(s.id) }
                            steps[i].editing.toggle()
                        }
                    }
                    PPIconButton("arrow-up", help: tr("vcs.rebase.moveUp"), size: M.iconButtonSmall, iconSize: M.iconSmall) { move(i, to: i - 1) }
                        .disabled(i == 0)
                    PPIconButton("arrow-down", help: tr("vcs.rebase.moveDown"), size: M.iconButtonSmall, iconSize: M.iconSmall) { move(i, to: i + 1) }
                        .disabled(i == steps.count - 1)
                }
                .frame(height: Self.rowH)
                if s.editing && (s.action == "reword" || s.action == "squash") {
                    PPTextArea(text: Binding(get: { steps[i].message ?? "" }, set: { steps[i].message = $0 }),
                               placeholder: s.action == "squash" ? tr("vcs.rebase.squashPlaceholder") : "", minHeight: 72)
                        .padding(.leading, 26)
                        .padding(.bottom, M.sm)
                }
            }
            .padding(.horizontal, M.lg)
            .background(isDragging ? theme.bgSelected : Color.clear)
            .offset(y: isDragging ? dragOffset : 0)
            .zIndex(isDragging ? 1 : 0)
        }

        /// reword：把原信息取来作为编辑起点
        private func ensureMessage(_ hash: String) {
            guard let i = steps.firstIndex(where: { $0.id == hash }), steps[i].original == nil else { return }
            Task {
                let d = try? await repo.api().vcsShow(projectId: repo.projectId, root: repo.root, hash: hash)
                guard let j = steps.firstIndex(where: { $0.id == hash }) else { return }
                let full = d?.message ?? steps[j].commit.subject
                steps[j].original = full
                if steps[j].message == nil { steps[j].message = full }
            }
        }

        private func firstLine(_ s: String) -> String {
            s.split(separator: "\n", maxSplits: 1).first.map(String.init) ?? s
        }

        private func move(_ from: Int, to: Int) {
            guard steps.indices.contains(from), steps.indices.contains(to), from != to else { return }
            let s = steps.remove(at: from)
            steps.insert(s, at: to)
        }

        private func dragGesture(_ i: Int) -> some Gesture {
            DragGesture(minimumDistance: 2)
                .onChanged { v in
                    if dragging == nil { dragging = steps[i].id }
                    dragOffset = v.translation.height
                }
                .onEnded { v in
                    if let id = dragging, let from = steps.firstIndex(where: { $0.id == id }) {
                        let to = min(max(from + Int((v.translation.height / Self.rowH).rounded()), 0), steps.count - 1)
                        withAnimation(.easeOut(duration: 0.12)) { move(from, to: to) }
                    }
                    dragging = nil
                    dragOffset = 0
                }
        }

        private func submit() {
            guard validation == nil, !busy else { return }
            busy = true
            let plan = VCSRebasePlan(base: base, steps: steps.map { s in
                let msg = (s.action == "reword" || s.action == "squash") ? s.message?.trimmingCharacters(in: .whitespacesAndNewlines) : nil
                return VCSRebaseStep(hash: s.commit.hash, action: s.action, message: (msg?.isEmpty ?? true) ? nil : msg)
            })
            Task {
                do {
                    let r = try await repo.runThrowing(VCSOp.rebaseInteractive, plan, title: tr("vcs.rebaseInteractive"), long: true)
                    if r.ok || !r.conflicts.isEmpty || r.reason == VCSReason.conflicts { AppModel.shared.dialogs.dismiss(id) }
                } catch {
                    self.error = error.localizedDescription
                }
                busy = false
            }
        }
    }
}

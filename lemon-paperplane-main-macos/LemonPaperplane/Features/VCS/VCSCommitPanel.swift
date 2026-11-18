import PaperplaneKit
import SwiftUI

/// 提交区：选项、提交信息（最近信息历史）、提交 / 提交并推送
struct VCSCommitPanel: View {
    @ObservedObject var repo: VCSRepo
    @ObservedObject var changes: VCSChangesModel
    @ObservedObject private var draft: VCSCommitDraft
    @FocusState private var focused: Bool

    init(repo: VCSRepo, changes: VCSChangesModel) {
        self.repo = repo
        self.changes = changes
        self._draft = ObservedObject(wrappedValue: changes.draft)
    }
    @Environment(\.theme) private var theme

    var body: some View {
        let st = repo.status
        let summary = selectionSummary(st)
        VStack(alignment: .leading, spacing: M.sm) {
            HStack(spacing: M.md) {
                PPCheckbox(isOn: Binding(get: { draft.amend }, set: { setAmend($0) }), label: tr("vcs.commit.amend"))
                    .disabled(st?.unborn ?? false)
                PPCheckbox(isOn: $draft.signOff, label: tr("vcs.commit.signOff"))
                Spacer(minLength: 0)
                if !draft.author.isEmpty {
                    PPBadge(text: draft.author, tone: "info", icon: .builtin("user"))
                }
                if draft.noVerify {
                    PPBadge(text: tr("vcs.commit.noVerify"), tone: "warning")
                }
                PPMenuButton(alignment: .trailing, entries: historyEntries) {
                    PPIcon("history", size: M.iconSmall, color: theme.iconPrimary)
                        .frame(width: M.iconButtonSmall, height: M.iconButtonSmall)
                        .help(tr("vcs.commit.history"))
                }
                PPMenuButton(alignment: .trailing, entries: optionEntries) {
                    PPIcon("ellipsis", size: M.iconSmall, color: theme.iconPrimary)
                        .frame(width: M.iconButtonSmall, height: M.iconButtonSmall)
                        .help(tr("vcs.commit.options"))
                }
            }
            .font(.pp(.small))
            VCSMessageEditor(text: $draft.message, placeholder: tr("vcs.commit.placeholder"), focused: $focused)
                .frame(height: 96)
            HStack(spacing: M.md) {
                PPButton(draft.amend ? tr("vcs.commit.amendButton") : tr("vcs.commit"), kind: .primary, busy: draft.committing) {
                    repo.commit(andPush: false)
                }
                .disabled(!canCommit(st, summary: summary))
                .help(tr("vcs.commit") + "  ⌘↩")
                PPButton(tr("vcs.commitAndPush"), busy: false) {
                    repo.commit(andPush: true)
                }
                .disabled(!canCommit(st, summary: summary) || draft.committing)
                Spacer(minLength: 0)
                Text(summary.text).font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1)
            }
        }
        .padding(M.md)
        .background(
            // ⌘↩ 提交
            Button("") { if canCommit(st, summary: summary) { repo.commit(andPush: false) } }
                .keyboardShortcut(.return, modifiers: .command)
                .frame(width: 0, height: 0).opacity(0).accessibilityHidden(true)
        )
        .onReceive(changes.focusMessage) { _ in focused = true }
    }

    private struct Summary {
        var count: Int
        var text: String
    }

    private func selectionSummary(_ st: VCSStatus?) -> Summary {
        guard let st else { return Summary(count: 0, text: "") }
        if changes.stagingArea {
            let n = st.files.filter { !$0.conflicted && !($0.indexStatus ?? "").isEmpty }.count
            return Summary(count: n, text: tr("vcs.commit.stagedCount", n))
        }
        let plan = changes.commitPlan(st.files)
        let n = plan.paths.count + plan.hunks.count
        return Summary(count: n, text: tr("vcs.commit.selectedCount", n))
    }

    private func canCommit(_ st: VCSStatus?, summary: Summary) -> Bool {
        guard let st, !draft.committing, st.conflicts == 0 else { return false }
        let hasMessage = !draft.message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        let merging = st.operation == "merge"
        guard hasMessage || draft.amend || merging else { return false }
        return summary.count > 0 || draft.amend || merging
    }

    private func setAmend(_ on: Bool) {
        draft.amend = on
        guard on, draft.message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        Task {
            if let d = try? await repo.api().vcsShow(projectId: repo.projectId, root: repo.root, hash: "HEAD"),
               draft.amend, draft.message.isEmpty {
                draft.message = d.message ?? ([d.commit.subject] + (d.body.map { ["", $0] } ?? [])).joined(separator: "\n")
            }
        }
    }

    private func historyEntries() -> [MenuEntry] {
        let list = VCSPrefs.messageHistory(repo.root)
        if list.isEmpty { return [.header(tr("vcs.commit.historyEmpty"))] }
        return [.header(tr("vcs.commit.history"))] + list.map { m in
            let first = m.split(separator: "\n", maxSplits: 1).first.map(String.init) ?? m
            return .item(first.count > 60 ? String(first.prefix(60)) + "…" : first) { draft.message = m }
        }
    }

    private func optionEntries() -> [MenuEntry] {
        [
            .item(tr("vcs.commit.author"), icon: "user", checked: !draft.author.isEmpty) {
                Dialogs.input(title: tr("vcs.commit.author"), label: tr("vcs.commit.authorLabel"), initial: draft.author,
                              ok: tr("common.ok")) { v in draft.author = v.contains("<") ? v : "" }
            },
            .item(tr("vcs.commit.clearAuthor"), disabled: draft.author.isEmpty) { draft.author = "" },
            .divider,
            .item(tr("vcs.commit.noVerify"), checked: draft.noVerify) { draft.noVerify.toggle() },
            .divider,
            .item(tr("vcs.commit.undoLast"), icon: "undo-2", disabled: repo.status?.unborn ?? true) { VCSCommitActions.undoLast(repo: repo) },
        ]
    }
}

/// 提交信息输入框（外部可控制聚焦，⌘K 用）
struct VCSMessageEditor: View {
    @Binding var text: String
    var placeholder = ""
    var focused: FocusState<Bool>.Binding
    @Environment(\.theme) private var theme

    var body: some View {
        ZStack(alignment: .topLeading) {
            if text.isEmpty {
                Text(placeholder).foregroundColor(theme.textTertiary)
                    .padding(.top, 6).padding(.leading, 5)
                    .allowsHitTesting(false)
            }
            TextEditor(text: $text)
                .font(.pp(.body))
                .scrollContentBackground(.hidden)
                .focused(focused)
        }
        .padding(.vertical, M.xs)
        .padding(.horizontal, M.xs)
        .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgInput))
        .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(focused.wrappedValue ? theme.borderFocus : theme.borderStrong, lineWidth: 1))
        .overlay(RoundedRectangle(cornerRadius: M.radiusMD + 2).stroke(focused.wrappedValue ? theme.focusRing : .clear, lineWidth: 2).padding(-2))
    }
}

// MARK: - 提交

extension VCSRepo {
    /// 提交（⌘K 面板、⌘↩）。先确认身份，再按模式组织参数。
    /// paths：true 时按勾选的文件提交（总览用，不看暂存区模式）；push：提交后的推送（nil 弹推送对话框）
    func commit(andPush: Bool, byPaths: Bool = false, push: (() -> Void)? = nil) {
        guard let st = status, !changes.draft.committing else { return }
        ensureIdentity { [weak self] in self?.doCommit(st, andPush: andPush, byPaths: byPaths, push: push) }
    }

    private func doCommit(_ st: VCSStatus, andPush: Bool, byPaths: Bool, push: (() -> Void)?) {
        let c = changes
        let d = changes.draft
        let msg = d.message.trimmingCharacters(in: .whitespacesAndNewlines)
        var args = VCSCommitArgs(message: msg)
        if d.amend { args.amend = true }
        if d.signOff { args.signOff = true }
        if d.noVerify { args.noVerify = true }
        if !d.author.isEmpty { args.author = d.author }
        if !c.stagingArea || byPaths {
            let plan = c.commitPlan(st.files)
            if plan.paths.isEmpty && plan.hunks.isEmpty {
                if !d.amend && st.operation != "merge" {
                    AppModel.shared.toasts.show("info", tr("vcs.commit.nothingSelected"))
                    return
                }
            } else {
                args.paths = plan.paths
                if !plan.hunks.isEmpty { args.hunks = plan.hunks }
            }
        }
        d.committing = true
        Task {
            defer { d.committing = false }
            do {
                let r = try await api().vcsCommit(projectId: projectId, root: root, args)
                VCSPrefs.remember(message: msg, root: root)
                d.message = ""
                d.amend = false
                c.excludedHunks = [:]
                d.author = ""
                notice = nil
                AppModel.shared.toasts.show("success", tr("vcs.commit.done", r.short ?? VCSStyle.shortHash(r.hash)))
                scheduleRefresh()
                if andPush {
                    if let push { push() } else { VCSPushDialog.present(repo: self) }
                }
            } catch {
                if error is CancellationError { return }
                if (error as? APIError)?.reason == VCSReason.identityMissing {
                    // 预检之后身份又没了（或预检没读到配置）：设置后重试
                    VCSIdentityDialog.present(repo: self, missing: []) {
                        self.commit(andPush: andPush, byPaths: byPaths, push: push)
                    }
                    return
                }
                var n = VCSNotice(tone: "danger", title: tr("vcs.commit.failed"), message: error.localizedDescription)
                if case .object(let o)? = (error as? APIError)?.data, case .string(let out)? = o["output"] { n.output = out }
                notice = n
            }
        }
    }
}

@MainActor
enum VCSCommitActions {
    /// 撤销最近一次提交（改动留在工作区）。HEAD 已在上游里时明确提示并强制（插件默认拒绝 alreadyPushed）
    static func undoLast(repo: VCSRepo) {
        let pushed = repo.status?.upstream != nil && (repo.status?.ahead ?? 0) == 0
        Dialogs.confirm(title: tr("vcs.undoCommit.title"),
                        message: pushed ? tr("vcs.undoCommit.pushedMessage") : tr("vcs.undoCommit.message"),
                        ok: tr("vcs.undoCommit"), danger: pushed) {
            try await repo.runThrowing(VCSOp.undoCommit, VCSUndoCommitArgs(force: pushed ? true : nil), title: tr("vcs.undoCommit"))
        }
    }
}

/// 设置 user.name / user.email（本仓库或全局）
@MainActor
enum VCSIdentityDialog {
    static func present(repo: VCSRepo, missing: [String], then: @escaping () -> Void) {
        let id = "vcs-identity"
        AppModel.shared.dialogs.present(id: id) {
            Body(id: id, repo: repo, then: then)
        }
    }

    private struct Body: View {
        let id: String
        let repo: VCSRepo
        let then: () -> Void
        @State private var name = ""
        @State private var email = ""
        @State private var global = true
        @State private var busy = false
        @State private var error: String?
        @Environment(\.theme) private var theme

        var body: some View {
            PPDialog(title: tr("vcs.identity.title"), width: 440, onClose: close) {
                VStack(alignment: .leading, spacing: M.md) {
                    HStack(spacing: M.sm) {
                        Text(tr("vcs.identity.missing")).foregroundColor(theme.textSecondary)
                        PPInfoButton(text: tr("vcs.identity.info"))
                    }
                    field(tr("vcs.identity.name"), $name)
                    field(tr("vcs.identity.email"), $email)
                    HStack(spacing: M.lg) {
                        PPRadio(selected: global, label: tr("vcs.identity.global")) { global = true }
                        PPRadio(selected: !global, label: tr("vcs.identity.local")) { global = false }
                    }
                    if let error { PPAlert(tone: "danger", title: error) }
                }
            } footer: {
                PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
                PPButton(tr("vcs.identity.setAndContinue"), kind: .primary, busy: busy, action: submit)
                    .keyboardShortcut(.defaultAction)
                    .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty || !email.contains("@"))
            }
            .task {
                if let cfg = try? await repo.api().vcsConfigGet(projectId: repo.projectId, root: repo.root, keys: ["user.name", "user.email"]) {
                    if name.isEmpty { name = cfg.values["user.name"] ?? "" }
                    if email.isEmpty { email = cfg.values["user.email"] ?? "" }
                }
            }
        }

        private func field(_ label: String, _ text: Binding<String>) -> some View {
            HStack(spacing: M.md) {
                Text(label).frame(width: 64, alignment: .leading).foregroundColor(theme.textSecondary)
                PPTextField("", text: text, onSubmit: submit)
            }
        }

        private func close() { AppModel.shared.dialogs.dismiss(id) }

        private func submit() {
            let n = name.trimmingCharacters(in: .whitespaces)
            let e = email.trimmingCharacters(in: .whitespaces)
            guard !n.isEmpty, e.contains("@"), !busy else { return }
            busy = true
            Task {
                do {
                    let api = try repo.api()
                    for (k, v) in [("user.name", n), ("user.email", e)] {
                        let r: VCSOpResult = try await api.vcsRun(VCSOp.configSet, projectId: repo.projectId, root: repo.root,
                                                                  args: VCSConfigSetArgs(key: k, value: v, global: global ? true : nil))
                        if !r.ok { throw APIError(code: APIError.codeUserError, message: r.message ?? tr("common.failed")) }
                    }
                    close()
                    then()
                } catch {
                    self.error = error.localizedDescription
                }
                busy = false
            }
        }
    }
}

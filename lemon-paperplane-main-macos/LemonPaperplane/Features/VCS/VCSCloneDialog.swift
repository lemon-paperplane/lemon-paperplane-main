import AppKit
import PaperplaneKit
import SwiftUI

/// 从 Git 克隆：地址、目标目录（父目录 + 文件夹名）、分支、工作区 → project.clone（克隆后自动导入）
@MainActor
enum VCSCloneDialog {
    static func present() {
        let id = "vcs-clone"
        AppModel.shared.dialogs.present(id: id) { Body(id: id) }
    }

    private struct Body: View {
        let id: String
        @State private var url = ""
        @State private var parent = Self.defaultParent
        @State private var folder = ""
        @State private var folderEdited = false
        @State private var branch = ""
        @State private var workspaceId = Actions.targetWorkspaceId ?? ""
        @State private var busy = false
        @State private var started: Date?
        @State private var error: String?
        @ObservedObject private var data = AppModel.shared.data
        @ObservedObject private var runs = AppModel.shared.runs
        @Environment(\.theme) private var theme

        /// 目标目录在当前服务器上：按服务器的路径规则拼（连着 Windows 服务器时是 C:\…）
        private var dest: String { AppModel.shared.serverPath.join(parent, folder) }

        /// 上次用的父目录按服务器分开记
        private static var parentKey: String {
            AppModel.shared.servers.currentId.map { "PaperplaneVCSCloneDir." + $0 } ?? "PaperplaneVCSCloneDir"
        }

        private static var defaultParent: String {
            if let saved = UserDefaults.standard.string(forKey: parentKey) { return saved }
            let model = AppModel.shared
            let home = model.isRemote ? (model.systemInfo?.home ?? "") : NSHomeDirectory()
            return home.isEmpty ? "" : model.serverPath.join(home, "Code")
        }

        var body: some View {
            PPDialog(title: tr("vcs.clone.title"), width: 560, onClose: busy ? nil : { close() }) {
                VStack(alignment: .leading, spacing: M.md) {
                    field(tr("vcs.clone.url")) {
                        PPTextField("https://github.com/… / git@…", text: Binding(get: { url }, set: { v in
                            url = v
                            if !folderEdited { folder = Self.folderName(v) }
                        }), mono: true, onSubmit: submit)
                    }
                    field(tr("vcs.clone.directory")) {
                        HStack(spacing: M.sm) {
                            PPTextField("", text: $parent, mono: true)
                            PPButton(tr("vcs.clone.choose"), size: .regular) { choose() }
                        }
                    }
                    field(tr("vcs.clone.folder")) {
                        PPTextField("", text: Binding(get: { folder }, set: { folder = $0; folderEdited = true }), mono: true)
                    }
                    field(tr("vcs.clone.branch")) {
                        PPTextField(tr("vcs.clone.branchPlaceholder"), text: $branch, mono: true)
                    }
                    if data.workspaces.count > 1 {
                        field(tr("import.workspace")) {
                            PPDropdown(options: data.workspaces.map { .init(value: $0.id, title: $0.name) }, selection: $workspaceId, fullWidth: true)
                        }
                    }
                    if !folder.isEmpty {
                        Text(Format.abbreviatePath(dest)).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                            .lineLimit(1).truncationMode(.middle)
                    }
                    if busy {
                        HStack(spacing: M.sm) {
                            PPSpinner(size: M.iconSmall)
                            Text(tr("vcs.clone.running")).foregroundColor(theme.textSecondary)
                            if let t = cloneTask {
                                PPLink(title: tr("vcs.output")) { Actions.showTaskLog(t.id) }
                            }
                            Spacer()
                        }
                    }
                    if let error { PPAlert(tone: "danger", title: error) }
                }
            } footer: {
                PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction).disabled(busy)
                PPButton(tr("vcs.clone"), icon: "download", kind: .primary, busy: busy, action: submit)
                    .keyboardShortcut(.defaultAction)
                    .disabled(url.trimmingCharacters(in: .whitespaces).isEmpty || folder.isEmpty || parent.isEmpty)
            }
        }

        /// 正在跑的克隆任务（内核为克隆建的任务没有工程 id）
        private var cloneTask: TaskInfo? {
            guard let started else { return nil }
            return runs.activeTasks.first { $0.kind == "vcs" && $0.projectId == nil && ($0.startedAt ?? .distantPast) >= started.addingTimeInterval(-2) }
        }

        private func field<C: View>(_ label: String, @ViewBuilder _ c: () -> C) -> some View {
            HStack(spacing: M.md) {
                Text(label).foregroundColor(theme.textSecondary).frame(width: 80, alignment: .leading)
                c()
            }
        }

        /// 由地址推出文件夹名：…/name.git → name
        static func folderName(_ url: String) -> String {
            var s = url.trimmingCharacters(in: .whitespaces)
            while s.hasSuffix("/") { s.removeLast() }
            if s.hasSuffix(".git") { s.removeLast(4) }
            let parts = s.split(whereSeparator: { $0 == "/" || $0 == ":" })
            return parts.last.map(String.init) ?? ""
        }

        private func choose() {
            let binding = $parent
            PathPicker.choose(title: tr("vcs.clone.directory"), kind: .folder, initial: parent) { binding.wrappedValue = $0 }
        }

        private func close() { AppModel.shared.dialogs.dismiss(id) }

        private func submit() {
            let u = url.trimmingCharacters(in: .whitespaces)
            guard !u.isEmpty, !folder.isEmpty, !busy else { return }
            busy = true
            started = Date()
            error = nil
            UserDefaults.standard.set(parent, forKey: Self.parentKey)
            let b = branch.trimmingCharacters(in: .whitespaces)
            Task {
                do {
                    let p = try await AppModel.shared.client().cloneProject(url: u, dest: dest, branch: b.isEmpty ? nil : b,
                                                                            workspaceId: workspaceId.isEmpty ? nil : workspaceId)
                    close()
                    AppModel.shared.toasts.show("success", tr("vcs.clone.done", p.name))
                    if !workspaceId.isEmpty { AppModel.shared.nav.page = .workspace(workspaceId) }
                    AppModel.shared.nav.openProject(p.id, tab: .vcs)
                } catch {
                    self.error = error.localizedDescription
                }
                busy = false
            }
        }
    }
}

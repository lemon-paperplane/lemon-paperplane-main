import AppKit
import PaperplaneKit
import SwiftUI

/// 导入：选择文件夹 → 预览（识别结果、名称、工作区）→ 导入；或扫描文件夹 → 勾选 → 批量导入。
@MainActor
enum ImportFlow {
    static func chooseFolder(title: String) -> String? {
        let p = NSOpenPanel()
        p.canChooseDirectories = true
        p.canChooseFiles = false
        p.allowsMultipleSelection = false
        p.prompt = title
        p.message = title
        return p.runModal() == .OK ? p.url?.path : nil
    }

    static func importFolder() {
        guard let path = chooseFolder(title: tr("import.folder")) else { return }
        let id = "import-" + path
        AppModel.shared.dialogs.present(id: id) { ImportPreviewDialog(id: id, path: path) }
    }

    static func scanFolder() {
        guard let path = chooseFolder(title: tr("import.scan")) else { return }
        let id = "scan-" + path
        AppModel.shared.dialogs.present(id: id) { ScanDialog(id: id, path: path) }
    }
}

struct ImportPreviewDialog: View {
    let id: String
    let path: String
    @ObservedObject private var data = AppModel.shared.data
    @State private var entry: ScanEntry?
    /// 所选文件夹本身不是工程、但里面有工程时
    @State private var children = 0
    @State private var loading = true
    @State private var name = ""
    @State private var workspaceId = Actions.targetWorkspaceId ?? ""
    @State private var busy = false
    @State private var error: String?
    @Environment(\.theme) private var theme

    var body: some View {
        PPDialog(title: tr("import.folder"), width: M.dialogWidth, onClose: close) {
            VStack(alignment: .leading, spacing: M.lg) {
                HStack(spacing: M.md) {
                    PPIcon("folder", size: M.iconLarge, color: theme.iconSecondary)
                    Text(Format.abbreviatePath(path)).font(.pp(.mono)).lineLimit(2).truncationMode(.middle).textSelection(.enabled)
                }
                if loading {
                    HStack(spacing: M.sm) { PPSpinner(size: M.iconSmall); Text(tr("import.detecting")).foregroundColor(theme.textSecondary) }
                } else if let e = entry, !e.facets.isEmpty {
                    FlowRow(spacing: M.xs) { ForEach(e.facets) { FacetBadgeView(facet: $0) } }
                } else if children > 0 {
                    PPAlert(tone: "info", title: tr("import.containsProjects", children)) {
                        PPButton(tr("import.scanInstead"), icon: "scan-search", size: .small) {
                            close()
                            let sid = "scan-" + path
                            AppModel.shared.dialogs.present(id: sid) { ScanDialog(id: sid, path: path) }
                        }
                    }
                } else if (AppModel.shared.systemInfo?.plugins ?? 1) == 0 {
                    // 后台服务没有加载任何插件：不是工程识别不了，是安装不完整
                    PPAlert(tone: "danger", title: tr("import.noPlugins"))
                } else {
                    PPAlert(tone: "warning", title: tr("import.nothingDetected"))
                }
                if entry?.imported == true {
                    PPAlert(tone: "info", title: tr("import.alreadyImported"))
                }
                labeled(tr("common.name")) { PPTextField("", text: $name) }
                labeled(tr("import.workspace")) {
                    PPDropdown(options: data.workspaces.map { .init(value: $0.id, title: $0.name, icon: .builtin("layers")) },
                               selection: $workspaceId, fullWidth: true)
                }
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
            if entry?.imported == true, let pid = entry?.projectId {
                PPButton(tr("card.open"), kind: .primary) {
                    close()
                    AppModel.shared.nav.openProject(pid)
                }
            } else {
                PPButton(tr("import.import"), kind: .primary, busy: busy, action: submit)
                    .keyboardShortcut(.defaultAction)
                    .disabled(workspaceId.isEmpty || loading)
            }
        }
        .task { await load() }
    }

    private func labeled<C: View>(_ title: String, @ViewBuilder _ c: () -> C) -> some View {
        HStack(spacing: M.md) {
            Text(title).frame(width: 96, alignment: .leading)
            c()
        }
    }

    private func load() async {
        name = URL(fileURLWithPath: path).lastPathComponent
        defer { loading = false }
        guard let api = AppModel.shared.api else { return }
        // maxDepth 0 时，所选文件夹不是工程会返回它下面的工程
        if let list = try? await api.scan(path: path, maxDepth: 0) {
            let normalized = (path as NSString).standardizingPath
            entry = list.first { ($0.path as NSString).standardizingPath == normalized }
            children = list.filter { ($0.path as NSString).standardizingPath != normalized }.count
            if let n = entry?.name, !n.isEmpty { name = n }
        }
    }

    private func close() { AppModel.shared.dialogs.dismiss(id) }

    private func finish(_ projectId: String) {
        close()
        if AppModel.shared.nav.currentWorkspaceId != workspaceId { AppModel.shared.nav.page = .workspace(workspaceId) }
        AppModel.shared.nav.openProject(projectId)
    }

    /// 这个文件夹现在是否已经是工程（导入请求出错后核实用）
    private func importedProjectId() async -> String? {
        guard let list = try? await AppModel.shared.client().scan(path: path, maxDepth: 0) else { return nil }
        let normalized = (path as NSString).standardizingPath
        guard let e = list.first(where: { ($0.path as NSString).standardizingPath == normalized }), e.imported else { return nil }
        return e.projectId
    }

    private func submit() {
        busy = true
        error = nil
        let n = name.trimmingCharacters(in: .whitespaces)
        Task {
            do {
                let p = try await AppModel.shared.client().importProject(path: path, workspaceId: workspaceId,
                                                                         name: n.isEmpty ? nil : n)
                finish(p.id)
            } catch {
                // 请求出错不等于没导入：内核可能已经导入成功，只是返回结果没解析出来或连接中途断了。
                // 以内核的实际状态为准，否则对话框报错、用户点取消后工程却已经在工作区里了
                if let pid = await importedProjectId() {
                    finish(pid)
                } else {
                    self.error = error.localizedDescription
                }
            }
            busy = false
        }
    }
}

struct ScanDialog: View {
    let id: String
    let path: String
    @ObservedObject private var data = AppModel.shared.data
    @State private var entries: [ScanEntry] = []
    @State private var selected: Set<String> = []
    @State private var depth = 2
    @State private var loading = true
    @State private var workspaceId = Actions.targetWorkspaceId ?? ""
    @State private var busy = false
    @State private var error: String?
    @Environment(\.theme) private var theme

    var body: some View {
        PPDialog(title: tr("import.scan"), width: M.dialogWidthLarge, onClose: close) {
            VStack(alignment: .leading, spacing: M.md) {
                HStack(spacing: M.md) {
                    Text(Format.abbreviatePath(path)).font(.pp(.mono)).lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Text(tr("import.depth")).foregroundColor(theme.textSecondary)
                    PPSegmented(options: [(1, "1"), (2, "2"), (3, "3"), (4, "4")], selection: $depth, size: .small)
                }
                VStack(spacing: 0) {
                    HStack(spacing: M.md) {
                        PPCheckbox(isOn: Binding(get: { !selectable.isEmpty && selected.count == selectable.count }, set: { on in
                            selected = on ? Set(selectable.map(\.path)) : []
                        }))
                        Text(tr("import.selectedCount", selected.count, entries.count)).foregroundColor(theme.textSecondary)
                        Spacer()
                    }
                    .padding(.horizontal, M.md)
                    .frame(height: M.rowHeight)
                    .background(theme.bgSubtle)
                    PPDivider(subtle: true)
                    if loading {
                        HStack(spacing: M.sm) { PPSpinner(size: M.iconSmall); Text(tr("import.detecting")) }
                            .foregroundColor(theme.textSecondary)
                            .frame(maxWidth: .infinity, minHeight: 120)
                    } else if entries.isEmpty {
                        Text(tr("import.scanEmpty")).foregroundColor(theme.textSecondary).frame(maxWidth: .infinity, minHeight: 120)
                    } else {
                        ScrollView {
                            LazyVStack(spacing: 0) {
                                ForEach(entries) { e in row(e) }
                            }
                        }
                        .frame(height: 320)
                    }
                }
                .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgSurface))
                .clipShape(RoundedRectangle(cornerRadius: M.radiusMD))
                .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(theme.border, lineWidth: 1))
                HStack(spacing: M.md) {
                    Text(tr("import.workspace"))
                    PPDropdown(options: data.workspaces.map { .init(value: $0.id, title: $0.name, icon: .builtin("layers")) },
                               selection: $workspaceId, minWidth: 200)
                    Spacer()
                }
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
            PPButton(tr("import.importN", selected.count), kind: .primary, busy: busy, action: submit)
                .keyboardShortcut(.defaultAction)
                .disabled(selected.isEmpty || workspaceId.isEmpty)
        }
        .task(id: depth) { await load() }
    }

    private var selectable: [ScanEntry] { entries.filter { !$0.imported } }

    private func row(_ e: ScanEntry) -> some View {
        HStack(spacing: M.md) {
            PPCheckbox(isOn: Binding(get: { selected.contains(e.path) }, set: { on in
                if on { selected.insert(e.path) } else { selected.remove(e.path) }
            }))
            .disabled(e.imported)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: M.sm) {
                    Text(e.name).font(.pp(.bodyStrong)).lineLimit(1)
                    if e.imported { PPBadge(text: tr("import.imported")) }
                }
                Text(relative(e.path)).font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.middle)
            }
            Spacer(minLength: M.md)
            HStack(spacing: M.xs) { ForEach(e.facets.prefix(4)) { FacetBadgeView(facet: $0) } }
        }
        .padding(.horizontal, M.md)
        .padding(.vertical, M.sm)
        .opacity(e.imported ? 0.6 : 1)
    }

    private func relative(_ p: String) -> String {
        p.hasPrefix(path) ? "." + p.dropFirst(path.count) : Format.abbreviatePath(p)
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            entries = try await AppModel.shared.client().scan(path: path, maxDepth: depth)
            selected = Set(entries.filter { !$0.imported }.map(\.path))
            error = nil
        } catch {
            entries = []
            self.error = error.localizedDescription
        }
    }

    private func close() { AppModel.shared.dialogs.dismiss(id) }

    private func submit() {
        busy = true
        error = nil
        let paths = entries.map(\.path).filter { selected.contains($0) }
        Task {
            do {
                let res = try await AppModel.shared.client().importProjects(paths: paths, workspaceId: workspaceId)
                close()
                AppModel.shared.nav.page = .workspace(workspaceId)
                AppModel.shared.toasts.show("success", tr("import.done", res.count))
            } catch {
                // 同单个导入：先核实哪些其实已经导入了。全部已导入按成功处理；否则刷新列表（已导入的会标出来）再报错
                let message = error.localizedDescription
                let fresh = (try? await AppModel.shared.client().scan(path: path, maxDepth: depth)) ?? []
                let done = Set(fresh.filter(\.imported).map { ($0.path as NSString).standardizingPath })
                let all = paths.allSatisfy { done.contains(($0 as NSString).standardizingPath) }
                if all {
                    close()
                    AppModel.shared.nav.page = .workspace(workspaceId)
                    AppModel.shared.toasts.show("success", tr("import.done", paths.count))
                } else {
                    if !fresh.isEmpty {
                        entries = fresh
                        selected = Set(fresh.filter { !$0.imported && selected.contains($0.path) }.map(\.path))
                    }
                    self.error = message
                }
            }
            busy = false
        }
    }
}

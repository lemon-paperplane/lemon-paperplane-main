import AppKit
import PaperplaneKit
import SwiftUI

/// 选文件夹 / 文件：本机用系统对话框；连着远程服务器时弹「选择服务器上的文件夹」（fs.browse），
/// 本机路径在服务器上不存在。
@MainActor
enum PathPicker {
    enum Kind {
        case folder
        case file
        /// 文件或文件夹都行（表单里的路径字段、工具链目录、插件包）
        case any
    }

    static func choose(title: String, kind: Kind = .folder, initial: String? = nil, allowedExtensions: [String] = [],
                       completion: @escaping (String) -> Void) {
        if AppModel.shared.isRemote {
            ServerFolderPicker.present(title: title, kind: kind, initial: initial, allowedExtensions: allowedExtensions, completion: completion)
            return
        }
        let p = NSOpenPanel()
        p.canChooseDirectories = kind != .file
        p.canChooseFiles = kind != .folder
        p.canCreateDirectories = kind != .file
        p.allowsMultipleSelection = false
        p.prompt = title
        p.message = title
        if let initial, !initial.isEmpty { p.directoryURL = URL(fileURLWithPath: initial) }
        if p.runModal() == .OK, let url = p.url { completion(url.path) }
    }
}

/// 选择服务器上的文件夹：路径栏（可输入）、上一级、主目录、盘符（Windows 服务器）、文件夹列表
struct ServerFolderPicker: View {
    let id: String
    let title: String
    let kind: PathPicker.Kind
    let initial: String?
    let allowedExtensions: [String]
    let completion: (String) -> Void

    @State private var listing: FSBrowseResult?
    @State private var pathText = ""
    @State private var selected: String?
    @State private var loading = false
    @State private var error: String?
    @State private var showHidden = false
    @Environment(\.theme) private var theme

    static func present(title: String, kind: PathPicker.Kind, initial: String?, allowedExtensions: [String],
                        completion: @escaping (String) -> Void) {
        let id = "server-picker-" + UUID().uuidString
        AppModel.shared.dialogs.present(id: id) {
            ServerFolderPicker(id: id, title: title, kind: kind, initial: initial, allowedExtensions: allowedExtensions, completion: completion)
        }
    }

    private var path: ServerPath { ServerPath(sep: listing?.sep ?? AppModel.shared.serverPath.sep) }

    /// 上次浏览到的位置（按服务器记）
    private static var lastKey: String { "PaperplaneRemotePickerPath." + (AppModel.shared.servers.currentId ?? "") }

    var body: some View {
        PPDialog(title: kind == .file ? tr("remotePicker.titleFile") : tr("remotePicker.title"), width: M.dialogWidthLarge, onClose: close) {
            VStack(alignment: .leading, spacing: M.md) {
                if !title.isEmpty { Text(title).foregroundColor(theme.textSecondary) }
                toolbar
                list
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
            PPButton(tr("remotePicker.choose"), kind: .primary, action: choose)
                .keyboardShortcut(.defaultAction)
                .disabled(chosenPath == nil)
        }
        .task { await load(initial ?? UserDefaults.standard.string(forKey: Self.lastKey)) }
    }

    // MARK: 工具条

    private var toolbar: some View {
        HStack(spacing: M.xs) {
            PPIconButton("arrow-up", help: tr("remotePicker.up")) {
                if let p = listing?.parent, !p.isEmpty { Task { await load(p) } }
            }
            .disabled((listing?.parent ?? "").isEmpty)
            PPIconButton("house", help: tr("remotePicker.home")) { Task { await load(listing?.home ?? "") } }
            if let roots = listing?.roots, roots.count > 1 {
                PPDropdown(options: roots.map { .init(value: $0, title: $0, icon: .builtin("hard-drive")) },
                           selection: Binding(get: { currentRoot(roots) }, set: { r in Task { await load(r) } }),
                           minWidth: 72)
            }
            PPTextField(tr("remotePicker.path"), text: $pathText, mono: true) { Task { await load(pathText) } }
            PPIconButton("folder-plus", help: tr("remotePicker.newFolder")) { newFolder() }
                .disabled(listing == nil)
            PPIconButton(showHidden ? "eye" : "eye-off", help: tr("remotePicker.showHidden"), active: showHidden) { showHidden.toggle() }
            PPIconButton("refresh-cw", help: tr("files.refresh")) { Task { await load(listing?.path ?? pathText) } }
        }
    }

    private func currentRoot(_ roots: [String]) -> String {
        let p = (listing?.path ?? "").lowercased()
        return roots.first { p.hasPrefix($0.lowercased()) } ?? roots.first ?? ""
    }

    // MARK: 列表

    private var visible: [FSBrowseEntry] {
        (listing?.entries ?? []).filter { e in
            if e.hidden && !showHidden { return false }
            if e.dir { return true }
            guard kind != .folder else { return false }
            if allowedExtensions.isEmpty { return true }
            return allowedExtensions.contains { e.name.lowercased().hasSuffix("." + $0.lowercased()) }
        }
    }

    private var list: some View {
        VStack(spacing: 0) {
            if loading && listing == nil {
                PPSpinner(size: M.iconSmall).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if visible.isEmpty {
                Text(tr("remotePicker.empty")).foregroundColor(theme.textSecondary).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(visible) { e in row(e) }
                    }
                    .padding(M.xs)
                }
            }
        }
        .frame(height: 320)
        .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgSurface))
        .clipShape(RoundedRectangle(cornerRadius: M.radiusMD))
        .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(theme.border, lineWidth: 1))
    }

    private func row(_ e: FSBrowseEntry) -> some View {
        let sel = selected == e.path
        return HStack(spacing: M.sm) {
            PPIcon(e.dir ? "folder" : FileIcon.builtinName(forFileName: e.name), size: M.icon, color: theme.iconSecondary)
            Text(e.name).lineLimit(1).truncationMode(.middle)
            Spacer(minLength: M.md)
            if !e.dir, let size = e.size { Text(Format.bytes(size)).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary) }
            if e.dir { PPIcon("chevron-right", size: M.iconSmall, color: theme.iconSecondary) }
        }
        .opacity(e.hidden ? 0.6 : 1)
        .padding(.horizontal, M.md)
        .frame(height: M.rowHeightCompact)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(sel ? theme.bgSelected : .clear))
        .contentShape(Rectangle())
        .onTapGesture(count: 2) {
            if e.dir { Task { await load(e.path) } } else { selected = e.path; choose() }
        }
        .onTapGesture { selected = e.path }
    }

    // MARK: 动作

    /// 点「选择」时的结果：选中的条目，或（选文件夹时）当前文件夹
    private var chosenPath: String? {
        if let s = selected, let e = listing?.entries.first(where: { $0.path == s }) {
            switch kind {
            case .folder: return e.dir ? e.path : nil
            case .file: return e.dir ? nil : e.path
            case .any: return e.path
            }
        }
        return kind == .file ? nil : listing?.path
    }

    private func load(_ p: String?) async {
        loading = true
        defer { loading = false }
        do {
            let r = try await AppModel.shared.client().fsBrowse(path: p)
            listing = r
            pathText = r.path
            selected = nil
            error = nil
            UserDefaults.standard.set(r.path, forKey: Self.lastKey)
        } catch {
            self.error = error.localizedDescription
            if listing == nil, p != nil, !(p ?? "").isEmpty {
                // 记住的位置已经不在了：回到主目录
                await load(nil)
            }
        }
    }

    /// 新建文件夹（fs.mkdir），建好后选中它
    private func newFolder() {
        guard let base = listing?.path else { return }
        let path = self.path
        Dialogs.input(title: tr("remotePicker.newFolder"), label: tr("common.name")) { name in
            let n = name.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !n.isEmpty else { return }
            let created = try await AppModel.shared.client().fsMkdir(path: path.join(base, n))
            await load(base)
            selected = created
        }
    }

    private func choose() {
        guard let p = chosenPath else { return }
        close()
        completion(p)
    }

    private func close() { AppModel.shared.dialogs.dismiss(id) }
}

import AppKit
import PaperplaneKit
import SwiftUI

/// 文件树的操作：新建、重命名、删除（移到废纸篓）、在访达/终端中打开、复制路径、在编辑窗口打开。
@MainActor
enum FileActions {
    static var model: AppModel { AppModel.shared }

    /// 相对工程根的路径 → 绝对路径
    static func absolute(_ rel: String, projectPath: String) -> String {
        rel.isEmpty ? projectPath : (projectPath as NSString).appendingPathComponent(rel)
    }

    /// 所在目录（相对路径）；根下的条目为 ""
    static func parent(of rel: String) -> String {
        guard let slash = rel.lastIndex(of: "/") else { return "" }
        return String(rel[..<slash])
    }

    static func join(_ dir: String, _ name: String) -> String {
        dir.isEmpty ? name : dir + "/" + name
    }

    static func edit(projectId: String, path: String, line: Int? = nil) {
        EditorWindows.shared.open(projectId: projectId, file: path, line: line)
    }

    static func newItem(projectId: String, in dir: String, folder: Bool, tree: FileTreeModel?) {
        Dialogs.input(title: folder ? tr("files.newFolder") : tr("files.newFile"), label: tr("common.name"),
                      ok: tr("common.create")) { name in
            let rel = join(dir, name.trimmingCharacters(in: CharacterSet(charactersIn: "/ ")))
            let r = try await model.client().fsCreate(projectId: projectId, path: rel, dir: folder)
            await tree?.reload(dir, expand: true)
            tree?.selection = r.path.isEmpty ? rel : r.path
            if !folder { edit(projectId: projectId, path: r.path.isEmpty ? rel : r.path) }
        }
    }

    static func rename(projectId: String, entry: FSEntry, tree: FileTreeModel?) {
        Dialogs.input(title: tr("files.rename"), label: tr("common.name"), initial: entry.name, ok: tr("common.save")) { name in
            let dir = parent(of: entry.path)
            let r = try await model.client().fsRename(projectId: projectId, from: entry.path, to: join(dir, name))
            await tree?.reload(dir)
            tree?.selection = r.path
        }
    }

    static func delete(projectId: String, entries: [FSEntry], tree: FileTreeModel?) {
        guard let first = entries.first else { return }
        let title = entries.count == 1 ? tr("files.delete.title", first.name) : tr("files.delete.titleN", entries.count)
        Dialogs.confirm(title: title, message: tr("files.delete.message"), ok: tr("files.delete"), danger: true) {
            let r = try await model.client().fsDelete(projectId: projectId, paths: entries.map(\.path))
            for dir in Set(entries.map { parent(of: $0.path) }) { await tree?.reload(dir) }
            if tree?.selection.map({ s in entries.contains { $0.path == s } }) == true { tree?.selection = nil }
            model.toasts.show("success", tr("files.deleted", r.trashed.count))
        }
    }

    static func revealInFinder(_ rel: String, projectPath: String) {
        Actions.revealInFinder(absolute(rel, projectPath: projectPath))
    }

    /// 在设置里选的终端中打开目录（默认 Terminal）
    static func openTerminal(at rel: String, projectPath: String) {
        let url = URL(fileURLWithPath: absolute(rel, projectPath: projectPath), isDirectory: true)
        let ids: [String: String] = [
            "Terminal": "com.apple.Terminal", "iTerm": "com.googlecode.iterm2",
            "Warp": "dev.warp.Warp-Stable", "Ghostty": "com.mitchellh.ghostty",
        ]
        let chosen = model.data.settings?.terminal ?? "Terminal"
        let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: ids[chosen] ?? "com.apple.Terminal")
            ?? NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Terminal")
        guard let app else { return }
        NSWorkspace.shared.open([url], withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration()) { _, error in
            if let error { Task { @MainActor in model.toasts.error(error) } }
        }
    }

    /// 右键菜单
    static func menu(projectId: String, projectPath: String, entry: FSEntry, tree: FileTreeModel?) -> [MenuEntry] {
        let dirForNew = entry.dir ? entry.path : parent(of: entry.path)
        var e: [MenuEntry] = []
        if !entry.dir {
            e.append(.item(tr("files.edit"), icon: "file-pen") { edit(projectId: projectId, path: entry.path) })
            if entry.isLaunchable {
                e.append(.item(tr("launch.addFile"), icon: "rocket") { Actions.launchAddFile(projectId: projectId, path: entry.path) })
            }
            e.append(.divider)
        }
        e += [
            .item(tr("files.newFile"), icon: "file-plus") { newItem(projectId: projectId, in: dirForNew, folder: false, tree: tree) },
            .item(tr("files.newFolder"), icon: "folder-plus") { newItem(projectId: projectId, in: dirForNew, folder: true, tree: tree) },
            .divider,
            .item(tr("files.rename"), icon: "pencil") { rename(projectId: projectId, entry: entry, tree: tree) },
            .item(tr("files.delete"), icon: "trash-2", destructive: true) { delete(projectId: projectId, entries: [entry], tree: tree) },
            .divider,
            .item(tr("open.finder"), icon: "folder-open") { revealInFinder(entry.path, projectPath: projectPath) },
            .item(tr("files.openTerminal"), icon: "square-terminal") {
                openTerminal(at: entry.dir ? entry.path : parent(of: entry.path), projectPath: projectPath)
            },
            .item(tr("files.copyPath"), icon: "copy") { Actions.copy(absolute(entry.path, projectPath: projectPath)) },
            .item(tr("files.copyRelativePath"), icon: "clipboard") { Actions.copy(entry.path) },
        ]
        if entry.dir, let tree {
            e.append(.divider)
            e.append(.item(tr("files.refresh"), icon: "refresh-cw") { Task { await tree.reload(entry.path) } })
        }
        return e
    }
}

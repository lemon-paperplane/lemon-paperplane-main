import AppKit
import PaperplaneKit
import SwiftUI

/// 用户操作的统一入口（按钮、菜单、快捷键、菜单栏都走这里）。
@MainActor
enum Actions {
    static var model: AppModel { AppModel.shared }

    // MARK: 运行

    static func run(projectId: String, configId: String? = nil) {
        model.perform(tr("action.run.failed")) { api in
            let r = try await api.startRun(projectId: projectId, configId: configId)
            model.runs.runs[r.id] = r
        }
    }

    static func stop(runId: String) {
        model.perform(tr("action.stop.failed")) { api in
            let r = try await api.stopRun(runId)
            model.runs.runs[r.id] = r
        }
    }

    static func restart(runId: String) {
        model.perform(tr("action.restart.failed")) { api in
            let r = try await api.restartRun(runId)
            model.runs.runs[r.id] = r
        }
    }

    static func restart(projectId: String, configId: String) {
        model.perform(tr("action.restart.failed")) { api in
            let r = try await api.restartRun(projectId: projectId, configId: configId)
            model.runs.runs[r.id] = r
        }
    }

    static func stopProject(_ projectId: String) {
        model.perform(tr("action.stop.failed")) { api in _ = try await api.stopAll(projectId: projectId) }
    }

    static func stopAll() {
        model.perform(tr("action.stop.failed")) { api in _ = try await api.stopAll() }
    }

    /// 当前工程（详情页打开的，或运行中聚合页里唯一的）
    static var currentProjectId: String? { model.nav.openProjectId }

    /// ⌘R：运行当前工程（运行页选中的配置，否则默认配置）
    static func runCurrent() {
        guard let pid = currentProjectId else { return }
        run(projectId: pid, configId: model.nav.selectedConfig[pid])
    }

    /// ⌘.：停止当前工程（运行页选中配置的运行，否则整个工程）
    static func stopCurrent() {
        guard let pid = currentProjectId else { return }
        if let cid = model.nav.selectedConfig[pid], let r = model.runs.activeRun(projectId: pid, configId: cid) {
            stop(runId: r.id)
        } else {
            stopProject(pid)
        }
    }

    // MARK: 工程

    static func open(_ projectId: String, with app: String, moduleId: String? = nil) {
        model.perform(tr("action.open.failed")) { api in try await api.openProject(projectId, moduleId: moduleId, with: app) }
    }

    static func reimport(_ projectId: String) {
        model.perform(tr("action.reimport.failed")) { api in model.track(try await api.reimport(projectId)) }
    }

    static func sync(_ projectId: String, moduleId: String? = nil) {
        model.perform(tr("action.sync.failed")) { api in model.track(try await api.sync(projectId, moduleId: moduleId)) }
    }

    static func installDeps(projectId: String, moduleId: String?, clean: Bool = false) {
        model.perform(tr("action.deps.failed")) { api in
            model.track(try await api.installDependencies(projectId: projectId, moduleId: moduleId, clean: clean))
        }
    }

    static func rename(_ p: ProjectSummary) {
        Dialogs.input(title: tr("project.rename"), label: tr("common.name"), initial: p.name, ok: tr("common.save")) { name in
            let s = try await model.client().renameProject(p.id, name: name)
            model.data.projects[s.id] = s
        }
    }

    static func remove(_ p: ProjectSummary) {
        Dialogs.confirm(title: tr("project.remove.title", p.name), message: tr("project.remove.message"),
                        ok: tr("common.remove"), danger: true) {
            try await model.client().removeProject(p.id)
            model.data.projects.removeValue(forKey: p.id)
            if model.nav.openProjectId == p.id { model.nav.openProjectId = nil }
        }
    }

    static func move(_ projectId: String, to workspaceId: String) {
        model.perform { api in try await api.moveProject(projectId, to: workspaceId) }
    }

    static func revealInFinder(_ path: String) {
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }

    static func openURL(_ s: String) {
        guard let url = URL(string: s) else { return }
        NSWorkspace.shared.open(url)
    }

    static func copy(_ text: String, toast: String? = nil) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        model.toasts.show("success", toast ?? tr("common.copied"))
    }

    static func openFile(_ path: String) {
        NSWorkspace.shared.open(URL(fileURLWithPath: path))
    }

    // MARK: 工作区

    static func createWorkspace() {
        Dialogs.input(title: tr("workspace.new"), label: tr("common.name"), ok: tr("common.create")) { name in
            let ws = try await model.client().createWorkspace(name: name)
            if !model.data.workspaces.contains(where: { $0.id == ws.id }) { model.data.workspaces.append(ws) }
            model.nav.page = .workspace(ws.id)
        }
    }

    static func renameWorkspace(_ ws: Workspace) {
        Dialogs.input(title: tr("workspace.rename"), label: tr("common.name"), initial: ws.name, ok: tr("common.save")) { name in
            let n = try await model.client().renameWorkspace(ws.id, name: name)
            if let i = model.data.workspaces.firstIndex(where: { $0.id == n.id }) { model.data.workspaces[i] = n }
        }
    }

    static func deleteWorkspace(_ ws: Workspace) {
        let others = model.data.workspaces.filter { $0.id != ws.id }
        if ws.projectIds.isEmpty || others.isEmpty {
            Dialogs.confirm(title: tr("workspace.delete.title", ws.name), message: ws.projectIds.isEmpty ? nil : tr("workspace.delete.message"),
                            ok: tr("common.delete"), danger: true) {
                try await model.client().deleteWorkspace(ws.id)
                afterWorkspaceDeleted(ws.id)
            }
            return
        }
        // 有工程：选择移到哪个工作区，或一并移除
        let field = FormField(key: "moveTo", title: tr("workspace.delete.moveTo"), type: "select",
                              options: [Option(value: "", label: tr("workspace.delete.removeProjects"))]
                                  + others.map { Option(value: $0.id, label: $0.name) })
        Dialogs.form(title: tr("workspace.delete.title", ws.name), fields: [field],
                     values: ["moveTo": .string(others[0].id)], ok: tr("common.delete"), danger: true) { v in
            let target = v["moveTo"]?.stringValue
            try await model.client().deleteWorkspace(ws.id, moveTo: (target?.isEmpty ?? true) ? nil : target)
            afterWorkspaceDeleted(ws.id)
        }
    }

    private static func afterWorkspaceDeleted(_ id: String) {
        model.data.workspaces.removeAll { $0.id == id }
        if model.nav.currentWorkspaceId == id {
            model.nav.page = model.data.workspaces.first.map { .workspace($0.id) } ?? .running
        }
    }

    /// 导入时的目标工作区：当前页的，否则第一个
    static var targetWorkspaceId: String? {
        model.nav.currentWorkspaceId ?? model.data.workspaces.first?.id
    }

    // MARK: 任务

    static func cancelTask(_ id: String) {
        model.perform { api in try await api.cancelTask(id) }
    }

    static func showTaskLog(_ taskId: String) {
        let title = model.runs.tasks[taskId]?.title ?? tr("task.log")
        let id = "tasklog-" + taskId
        model.dialogs.present(id: id) {
            PPDialog(title: title, width: 760, onClose: { model.dialogs.dismiss(id) }) {
                ConsolePanel(consoleId: taskId, runId: nil, logFile: nil, compact: true)
                    .frame(height: 420)
            } footer: {
                PPButton(tr("common.close")) { model.dialogs.dismiss(id) }.keyboardShortcut(.cancelAction)
            }
        }
    }

    // MARK: 修复

    /// 执行修复：带 confirm（或 killPort）的先确认
    static func applyFix(_ fix: Fix, diagnosis: Diagnosis, runId: String?, projectId: String?, onDone: (() -> Void)? = nil) {
        if fix.kind == FixKind.openURL, let url = fix.params["url"] {
            openURL(url)
            return
        }
        let exec: () async throws -> Void = {
            let api = try model.client()
            let res = try await api.applyFix(runId: runId, projectId: runId == nil ? projectId : nil,
                                             diagnosisId: diagnosis.id, fixId: fix.id)
            if let t = res.task { model.track(t) }
            if let r = res.rerun { model.runs.runs[r.id] = r }
            if let url = res.openUrl, !url.isEmpty { openURL(url) }
            if let m = res.message, !m.isEmpty { model.toasts.show("success", fix.title, message: m) }
            onDone?()
        }
        let confirm = fix.confirm?.isEmpty == false ? fix.confirm : (fix.kind == FixKind.killPort ? tr("fix.killPort.confirm", fix.params["port"] ?? "") : nil)
        if let confirm {
            Dialogs.confirm(title: fix.title, message: confirm, ok: tr("fix.apply"), danger: fix.kind == FixKind.killPort, onConfirm: exec)
        } else {
            Task {
                do { try await exec() } catch { model.toasts.error(error, title: fix.title) }
            }
        }
    }

    // MARK: 插件动作

    /// 插件动作：确定模块 → 确认 → 表单 → project.action
    static func invoke(_ action: ActionInfo, projectId: String, moduleId: String? = nil, runId: String? = nil) {
        Task {
            do {
                let api = try model.client()
                var mid = moduleId
                if mid == nil {
                    let p = try await api.project(projectId)
                    let candidates = p.modules.filter { m in
                        m.hasAnyFacet(action.facets) && (action.facets.isEmpty ? m.facets.contains { $0.plugin == action.plugin } : true)
                    }
                    if candidates.count > 1 {
                        pickModule(candidates, title: action.title) { picked in
                            continueInvoke(action, projectId: projectId, moduleId: picked, runId: runId)
                        }
                        return
                    }
                    mid = candidates.first?.id ?? p.modules.first?.id
                }
                guard let mid else { return }
                continueInvoke(action, projectId: projectId, moduleId: mid, runId: runId)
            } catch {
                model.toasts.error(error, title: action.title)
            }
        }
    }

    private static func continueInvoke(_ action: ActionInfo, projectId: String, moduleId: String, runId: String?) {
        let send: ([String: JSONValue]?) async throws -> Void = { args in
            let t = try await model.client().invokeAction(projectId: projectId, moduleId: moduleId, plugin: action.plugin,
                                                          action: action.id, args: args, runId: runId)
            model.track(t)
        }
        if !action.form.isEmpty {
            // 带 optionsFrom 的下拉框取模块分析结果里的动态选项（如 iOS 插件的 scheme 列表）
            guard action.form.contains(where: { $0.optionsFrom?.isEmpty == false }) else {
                Dialogs.form(title: action.title, message: action.confirm, fields: action.form, ok: action.title) { v in try await send(v) }
                return
            }
            Task {
                var opts: [String: [Option]] = [:]
                if let p = try? await model.client().project(projectId) {
                    opts = (p.module(moduleId) ?? p.modules.first)?.optionValues ?? [:]
                }
                Dialogs.form(title: action.title, message: action.confirm, fields: action.form, optionValues: opts, ok: action.title) { v in
                    try await send(v)
                }
            }
        } else if let c = action.confirm, !c.isEmpty {
            Dialogs.confirm(title: action.title, message: c, ok: action.title) { try await send(nil) }
        } else {
            Task {
                do { try await send(nil) } catch { model.toasts.error(error, title: action.title) }
            }
        }
    }

    static func pickModule(_ modules: [Module], title: String, then: @escaping (String) -> Void) {
        let field = FormField(key: "module", title: tr("project.module"), type: "select", options: modules.map {
            Option(value: $0.id, label: $0.relDir.isEmpty ? $0.name : "\($0.name)  (\($0.relDir))")
        }, required: true)
        Dialogs.form(title: title, fields: [field], values: ["module": .string(modules[0].id)], ok: tr("common.continue")) { v in
            if let id = v["module"]?.stringValue { DispatchQueue.main.async { then(id) } }
        }
    }

    /// 插件动作是否适用于这个工程（按 facet）
    static func applicable(_ actions: [ActionInfo], facets: Set<String>, plugins: Set<String>) -> [ActionInfo] {
        actions.filter { a in
            a.facets.isEmpty ? plugins.contains(a.plugin) : !facets.isDisjoint(with: a.facets)
        }
    }

    /// 插件动作的菜单项
    static func menuEntries(_ actions: [ActionInfo], projectId: String, moduleId: String? = nil) -> [MenuEntry] {
        actions.map { a in
            .item(MenuItem(a.title, iconRef: IconSource.parse(a.icon, pluginId: a.plugin)) { invoke(a, projectId: projectId, moduleId: moduleId) })
        }
    }

    // MARK: 打开方式

    static func openWithEntries(_ projectId: String, moduleId: String? = nil) -> [MenuEntry] {
        [
            .item(tr("open.finder"), icon: "folder-open") { open(projectId, with: "finder", moduleId: moduleId) },
            .item(tr("open.terminal"), icon: "square-terminal") { open(projectId, with: "terminal", moduleId: moduleId) },
            .item(tr("open.idea"), icon: "code-xml") { open(projectId, with: "idea", moduleId: moduleId) },
            .item(tr("open.vscode"), icon: "code-xml") { open(projectId, with: "vscode", moduleId: moduleId) },
        ]
    }
}

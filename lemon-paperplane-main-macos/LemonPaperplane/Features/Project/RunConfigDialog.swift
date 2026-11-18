import PaperplaneKit
import SwiftUI

/// 新建 / 编辑 / 复制运行配置：选模块 → 选类型（runconfig.types）→ 按类型的 Options 渲染表单。
struct RunConfigDialog: View {
    enum Mode { case create, edit(RunConfig), duplicate(RunConfig) }

    let id: String
    let project: Project
    let mode: Mode

    @State private var moduleId: String
    @State private var choices: [RunConfigTypeChoice] = []
    @State private var typeId = ""
    @State private var name: String
    @State private var values: [String: JSONValue]
    @State private var env: [(String, String)]
    @State private var loading = false
    @State private var busy = false
    @State private var error: String?
    @State private var showErrors = false
    /// 实时下拉选项（如已连接设备），覆盖分析时取的快照
    @State private var liveOptions: [String: [Option]] = [:]
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    init(id: String, project: Project, mode: Mode) {
        self.id = id
        self.project = project
        self.mode = mode
        switch mode {
        case .create:
            _moduleId = State(initialValue: project.modules.first?.id ?? "")
            _name = State(initialValue: "")
            _values = State(initialValue: [:])
            _env = State(initialValue: [])
        case .edit(let c), .duplicate(let c):
            _moduleId = State(initialValue: c.moduleId ?? project.modules.first?.id ?? "")
            _name = State(initialValue: { if case .duplicate = mode { return c.name + " " + tr("runconfig.copySuffix") } else { return c.name } }())
            _values = State(initialValue: c.options)
            _env = State(initialValue: c.env.map { ($0.key, $0.value) }.sorted { $0.0 < $1.0 })
            _typeId = State(initialValue: c.type)
        }
    }

    static func presentNew(project: Project) {
        let id = "runconfig-new"
        AppModel.shared.dialogs.present(id: id) { RunConfigDialog(id: id, project: project, mode: .create) }
    }

    static func presentEdit(project: Project, config: RunConfig) {
        let id = "runconfig-edit-" + config.id
        AppModel.shared.dialogs.present(id: id) { RunConfigDialog(id: id, project: project, mode: .edit(config)) }
    }

    static func presentDuplicate(project: Project, config: RunConfig) {
        let id = "runconfig-dup-" + config.id
        AppModel.shared.dialogs.present(id: id) { RunConfigDialog(id: id, project: project, mode: .duplicate(config)) }
    }

    private var isEdit: Bool { if case .edit = mode { return true }; return false }
    private var isCreate: Bool { if case .create = mode { return true }; return false }

    private var module: Module? { project.module(moduleId) }

    /// 当前类型的表单字段
    private var fields: [FormField] {
        if let c = choices.first(where: { $0.type.id == typeId }) { return c.type.options }
        let plugin: String? = { if case .edit(let c) = mode { return c.plugin }; if case .duplicate(let c) = mode { return c.plugin }; return nil }()
        return data.contributions.runConfigType(plugin: plugin, type: typeId)?.options ?? []
    }

    private var optionValues: [String: [Option]] {
        var o = module?.optionValues ?? [:]
        if let c = choices.first(where: { $0.type.id == typeId }) {
            for (k, v) in c.optionValues { o[k] = v }
        }
        for (k, v) in liveOptions { o[k] = v }
        return o
    }

    /// 本类配置里标了 live 的 optionsFrom 键
    private var liveKeys: [String] { Array(Set(fields.filter(\.live).compactMap(\.optionsFrom))).sorted() }

    /// 对话框打开期间每 4 秒刷新一次（插拔设备后下拉框跟着变）；对话框关闭时任务自动取消
    private func refreshLiveOptions() async {
        let keys = liveKeys
        guard !keys.isEmpty else { return }
        while !Task.isCancelled {
            if let api = AppModel.shared.api,
               let v = try? await api.liveOptions(projectId: project.id, moduleId: moduleId, keys: keys), !Task.isCancelled {
                for (k, o) in v where liveOptions[k] != o { liveOptions[k] = o }
            }
            try? await Task.sleep(nanoseconds: 4_000_000_000)
        }
    }

    var body: some View {
        PPDialog(title: isEdit ? tr("runconfig.edit") : tr("runconfig.new"), width: M.dialogWidthLarge, onClose: close) {
            VStack(alignment: .leading, spacing: M.md) {
                if project.modules.count > 1 {
                    row(tr("project.module")) {
                        PPDropdown(options: project.modules.map { .init(value: $0.id, title: $0.name, detail: $0.relDir.isEmpty ? nil : $0.relDir) },
                                   selection: $moduleId, fullWidth: true)
                            .disabled(isEdit)
                    }
                }
                row(tr("runconfig.type")) {
                    if isCreate {
                        if loading {
                            PPSpinner(size: M.iconSmall)
                        } else {
                            PPDropdown(options: choices.map { .init(value: $0.type.id, title: $0.type.name,
                                                                    icon: IconSource.parse($0.type.icon, pluginId: $0.type.plugin)) },
                                       selection: $typeId, placeholder: tr("form.choose"), fullWidth: true)
                        }
                    } else {
                        Text(data.contributions.runConfigType(plugin: nil, type: typeId)?.name ?? typeId)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                row(tr("common.name")) { PPTextField("", text: $name, invalid: showErrors && name.trimmingCharacters(in: .whitespaces).isEmpty) }
                if !fields.isEmpty {
                    PPDivider(subtle: true)
                    FormView(fields: fields, values: $values, optionValues: optionValues, showErrors: showErrors)
                }
                PPDivider(subtle: true)
                HStack(alignment: .top, spacing: M.md) {
                    Text(tr("config.env")).frame(width: M.formLabelWidth, alignment: .leading).padding(.top, 6)
                    KeyValueEditor(pairs: $env)
                }
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
            PPButton(isEdit ? tr("common.save") : tr("common.create"), kind: .primary, busy: busy, action: submit)
                .keyboardShortcut(.defaultAction)
                .disabled(typeId.isEmpty)
        }
        .task(id: moduleId) { await loadTypes() }
        .task(id: moduleId + "|" + typeId + "|" + liveKeys.joined(separator: ",")) { await refreshLiveOptions() }
        .onChange(of: typeId) { t in
            if isCreate {
                values = FormView.initialValues(fields, [:])
                if name.isEmpty || choices.contains(where: { $0.type.name == name }) {
                    name = choices.first { $0.type.id == t }?.type.name ?? name
                }
            }
        }
    }

    private func row<C: View>(_ title: String, @ViewBuilder _ c: () -> C) -> some View {
        HStack(spacing: M.md) {
            Text(title).frame(width: M.formLabelWidth, alignment: .leading)
            c()
        }
    }

    private func loadTypes() async {
        guard !moduleId.isEmpty else { return }
        loading = true
        defer { loading = false }
        do {
            choices = try await AppModel.shared.client().runConfigTypes(projectId: project.id, moduleId: moduleId)
            if isCreate, !choices.contains(where: { $0.type.id == typeId }) { typeId = choices.first?.type.id ?? "" }
        } catch {
            if isCreate { self.error = error.localizedDescription }
        }
    }

    private func close() { AppModel.shared.dialogs.dismiss(id) }

    private func submit() {
        let n = name.trimmingCharacters(in: .whitespaces)
        if n.isEmpty || !FormView.missingRequired(fields, values).isEmpty {
            showErrors = true
            return
        }
        busy = true
        error = nil
        var envDict: [String: String] = [:]
        for (k, v) in env where !k.isEmpty { envDict[k] = v }
        let opts = FormView.normalized(fields, values)
        Task {
            do {
                let api = try AppModel.shared.client()
                switch mode {
                case .edit(let c):
                    _ = try await api.updateRunConfig(projectId: project.id, configId: c.id, name: n, options: opts, env: envDict)
                case .create, .duplicate:
                    let c = try await api.createRunConfig(projectId: project.id, moduleId: moduleId, type: typeId, name: n,
                                                          options: opts, env: envDict)
                    AppModel.shared.nav.selectedConfig[project.id] = c.id
                }
                AppModel.shared.bus.projectChanged.send(project.id)
                close()
            } catch {
                self.error = error.localizedDescription
            }
            busy = false
        }
    }
}

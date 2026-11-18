import PaperplaneKit
import SwiftUI

/// 配置：工程环境变量、工具链覆盖、运行配置编辑、相关插件设置。
struct ConfigTab: View {
    let project: Project
    @ObservedObject var model: ProjectDetailModel
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: M.xxl) {
                EnvSection(project: project, model: model)
                toolchains
                RunConfigsSection(project: project)
                pluginSettings
            }
            .padding(M.xl)
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    /// 模块需要的工具链种类
    private var kinds: [String] {
        var s = Set<String>()
        for m in project.modules {
            for k in m.toolchains.keys { s.insert(k) }
            for r in m.requirements { s.insert(r.kind) }
        }
        for k in project.settings?.toolchains.keys ?? [:].keys { s.insert(k) }
        return s.sorted()
    }

    @ViewBuilder private var toolchains: some View {
        if !kinds.isEmpty {
            PPSection(tr("config.toolchains"), info: tr("config.toolchains.info")) {
                PPCard {
                    VStack(alignment: .leading, spacing: M.sm) {
                        ForEach(kinds, id: \.self) { k in
                            HStack(spacing: M.md) {
                                PPIcon(raw: data.toolchainKinds.first { $0.kind == k }?.icon, fallback: "boxes", size: M.icon)
                                Text(data.kindName(k)).frame(width: 140, alignment: .leading)
                                Text(selectedText(k)).font(.pp(.mono)).foregroundColor(theme.textSecondary).lineLimit(1)
                                Spacer()
                                ToolchainOverridePicker(project: project, kind: k, model: model)
                            }
                        }
                    }
                }
            }
        }
    }

    private func selectedText(_ kind: String) -> String {
        let sels = project.modules.compactMap { $0.toolchains[kind] }
        if sels.contains(where: { $0.missing }) { return tr("toolchain.missing") }
        return Array(Set(sels.compactMap { $0.version })).sorted().joined(separator: ", ")
    }

    @ViewBuilder private var pluginSettings: some View {
        let relevant = data.contributions.settings.filter { project.pluginIds.contains($0.plugin) && !$0.fields.isEmpty }
        if !relevant.isEmpty {
            PPSection(tr("config.pluginSettings"), info: tr("config.pluginSettings.info")) {
                VStack(alignment: .leading, spacing: M.lg) {
                    ForEach(relevant, id: \.plugin) { s in PluginSettingsCard(info: s) }
                }
            }
        }
    }
}

/// 工程环境变量
private struct EnvSection: View {
    let project: Project
    @ObservedObject var model: ProjectDetailModel
    @State private var pairs: [(String, String)] = []
    @State private var dirty = false
    @State private var busy = false
    @State private var editorId = UUID()

    var body: some View {
        PPSection(tr("config.env"), info: tr("config.env.info"), accessory: {
            if dirty {
                PPButton(tr("common.revert"), kind: .ghost, size: .small) { load() }
                PPButton(tr("common.save"), kind: .primary, size: .small, busy: busy) { save() }
            }
        }) {
            PPCard {
                KeyValueEditor(pairs: Binding(get: { pairs }, set: { pairs = $0; dirty = true }))
                    .id(editorId)
            }
        }
        .onAppear { load() }
    }

    private func load() {
        pairs = (project.settings?.env ?? [:]).map { ($0.key, $0.value) }.sorted { $0.0 < $1.0 }
        dirty = false
        editorId = UUID()
    }

    private func save() {
        busy = true
        var env: [String: String] = [:]
        for (k, v) in pairs where !k.isEmpty { env[k] = v }
        Task {
            do {
                model.project = try await AppModel.shared.client().updateProjectSettings(project.id, env: env)
                dirty = false
            } catch {
                AppModel.shared.toasts.error(error)
            }
            busy = false
        }
    }
}

/// 运行配置编辑器（内嵌）
private struct RunConfigsSection: View {
    let project: Project
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        PPSection(tr("runconfig.title"), accessory: {
            PPButton(tr("runconfig.new"), icon: "plus", kind: .ghost, size: .small) { RunConfigDialog.presentNew(project: project) }
        }) {
            if project.allRunConfigs.isEmpty {
                Text(tr("run.noConfig")).foregroundColor(theme.textSecondary)
            } else {
                PPCard(padding: M.xs) {
                    VStack(spacing: 0) {
                        ForEach(project.allRunConfigs) { c in
                            let type = data.contributions.runConfigType(plugin: c.plugin, type: c.type)
                            PPRow(action: { RunConfigDialog.presentEdit(project: project, config: c) }) {
                                HStack(spacing: M.md) {
                                    PPIcon(raw: type?.icon, plugin: c.plugin, fallback: "play", size: M.icon)
                                    Text(c.name).lineLimit(1)
                                    Text(type?.name ?? c.type).foregroundColor(theme.textTertiary).lineLimit(1)
                                    if c.id == project.defaultConfigId { PPBadge(text: tr("runconfig.default"), tone: "info") }
                                    if c.edited { PPBadge(text: tr("runconfig.edited")) }
                                    Spacer()
                                    PPIcon("pencil", size: M.iconSmall, color: theme.iconSecondary)
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

/// 一个插件的设置表单（plugin.settings）
struct PluginSettingsCard: View {
    let info: PluginSettingsInfo
    @State private var values: [String: JSONValue] = [:]
    @State private var dirty = false
    @State private var busy = false
    @ObservedObject private var data = AppModel.shared.data

    var body: some View {
        PPCard {
            VStack(alignment: .leading, spacing: M.md) {
                HStack(spacing: M.sm) {
                    PPIcon(raw: data.plugin(info.plugin)?.icon, plugin: info.plugin, fallback: "puzzle", size: M.icon)
                    Text(info.title).font(.pp(.title))
                    Spacer()
                    if dirty {
                        PPButton(tr("common.save"), kind: .primary, size: .small, busy: busy, action: save)
                    }
                }
                FormView(fields: info.fields, values: Binding(get: { values }, set: { values = $0; dirty = true }))
            }
        }
        .onAppear { values = FormView.initialValues(info.fields, info.values) }
        .onChange(of: info.values) { v in if !dirty { values = FormView.initialValues(info.fields, v) } }
    }

    private func save() {
        busy = true
        Task {
            do {
                try await AppModel.shared.client().updatePluginSettings(info.plugin, values: FormView.normalized(info.fields, values))
                dirty = false
                AppModel.shared.toasts.show("success", tr("common.saved"))
            } catch {
                AppModel.shared.toasts.error(error)
            }
            busy = false
        }
    }
}

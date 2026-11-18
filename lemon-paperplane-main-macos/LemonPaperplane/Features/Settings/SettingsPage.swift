import AppKit
import PaperplaneKit
import SwiftUI

/// 设置：语言、主题、终端、镜像（测速）、自动下载/安装、插件闲置、日志、Git、API 与 MCP、关于。
struct SettingsPage: View {
    @ObservedObject private var data = AppModel.shared.data
    @ObservedObject private var model = AppModel.shared
    @ObservedObject private var l10n = L10n.shared
    @State private var mirrors: MirrorStatus?
    @State private var probing = false
    @State private var mcp: MCPConfig?
    @Environment(\.theme) private var theme

    private var s: AppSettings { data.settings ?? AppSettings() }

    var body: some View {
        VStack(spacing: 0) {
            PPPageHeader(title: tr("sidebar.settings")) { EmptyView() }
            PPDivider(subtle: true)
            ScrollView {
                VStack(alignment: .leading, spacing: M.xxl) {
                    general
                    mirrorSection
                    automation
                    EditorSettingsSection()
                    apiSection
                    about
                    ForEach(data.contributions.views(at: ViewLocation.settings)) { v in
                        PPSection(v.title) {
                            PPCard { PluginViewHost(view: v, context: ViewContext(plugin: v.plugin, view: v.id), scrolls: false) }
                        }
                    }
                }
                .padding(M.xl)
                .frame(maxWidth: 820, alignment: .leading)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .task {
            if let api = model.api {
                mirrors = try? await api.mirrorsStatus()
                mcp = try? await api.mcpConfig()
            }
        }
    }

    // MARK: 通用

    private var general: some View {
        PPSection(tr("settings.general")) {
            PPCard {
                VStack(alignment: .leading, spacing: M.md) {
                    SettingRow(tr("settings.language")) {
                        PPDropdown(options: [.init(value: "system", title: tr("settings.followSystem")),
                                             .init(value: "zh-Hans", title: "中文"), .init(value: "en", title: "English")],
                                   selection: Binding(get: { s.locale.isEmpty ? "system" : s.locale }, set: { v in
                                       L10n.shared.apply(setting: v)
                                       update(["locale": .string(v)])
                                   }), minWidth: 180)
                    }
                    SettingRow(tr("settings.theme")) {
                        PPSegmented(options: [("system", tr("settings.followSystem")), ("light", tr("settings.light")), ("dark", tr("settings.dark"))],
                                    selection: Binding(get: { s.theme.isEmpty ? "system" : s.theme }, set: { v in
                                        Appearance.apply(v)
                                        UserDefaults.standard.set(v, forKey: "PaperplaneTheme")
                                        update(["theme": .string(v)])
                                    }))
                    }
                    SettingRow(tr("settings.terminal")) {
                        PPDropdown(options: ["Terminal", "iTerm", "Warp", "Ghostty"].map { .init(value: $0, title: $0) },
                                   selection: Binding(get: { s.terminal ?? "Terminal" }, set: { update(["terminal": .string($0)]) }),
                                   minWidth: 180)
                    }
                }
            }
        }
    }

    // MARK: 镜像

    private var mirrorSection: some View {
        PPSection(tr("settings.mirrors"), info: tr("settings.mirrors.info")) {
            PPCard {
                VStack(alignment: .leading, spacing: M.md) {
                    SettingRow(tr("settings.mirrorRegion")) {
                        HStack(spacing: M.md) {
                            PPSegmented(options: [("auto", tr("settings.mirror.auto")), ("china", tr("settings.mirror.china")),
                                                  ("global", tr("settings.mirror.global"))],
                                        selection: Binding(get: { s.mirrorRegion.isEmpty ? "auto" : s.mirrorRegion },
                                                           set: { update(["mirrorRegion": .string($0)]) }))
                            if let m = mirrors, !m.effective.isEmpty {
                                Text(tr("settings.mirror.effective", regionTitle(m.effective))).font(.pp(.small)).foregroundColor(theme.textSecondary)
                            }
                        }
                    }
                    if let probes = mirrors?.probes, !probes.isEmpty {
                        VStack(spacing: 0) {
                            ForEach(probes) { p in
                                HStack(spacing: M.md) {
                                    Circle().fill(p.ok ? theme.statusRunning : theme.statusFailed).frame(width: 6, height: 6)
                                    Text(p.name).frame(width: 160, alignment: .leading)
                                    Text(p.url).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary).lineLimit(1).truncationMode(.middle)
                                    Spacer()
                                    Text(p.ok ? "\(Int(p.ms ?? 0)) ms" : tr("settings.mirror.unreachable"))
                                        .font(.pp(.monoSmall)).foregroundColor(p.ok ? theme.textSecondary : theme.danger)
                                }
                                .frame(height: M.rowHeightCompact)
                            }
                        }
                        .padding(.leading, M.formLabelWidth + M.md)
                    }
                    HStack {
                        Spacer().frame(width: M.formLabelWidth + M.md)
                        PPButton(tr("settings.mirror.probe"), icon: "gauge", size: .small, busy: probing) {
                            probing = true
                            Task {
                                do { mirrors = try await AppModel.shared.client().mirrorsProbe() } catch { AppModel.shared.toasts.error(error) }
                                probing = false
                            }
                        }
                    }
                }
            }
        }
    }

    private func regionTitle(_ r: String) -> String {
        r == "china" ? tr("settings.mirror.china") : (r == "global" ? tr("settings.mirror.global") : r)
    }

    // MARK: 自动化、插件、日志、Git

    private var automation: some View {
        PPSection(tr("settings.behavior")) {
            PPCard {
                VStack(alignment: .leading, spacing: M.md) {
                    SettingRow(tr("settings.autoToolchains"), info: tr("settings.autoToolchains.info")) {
                        PPSwitch(isOn: Binding(get: { s.autoInstallToolchains }, set: { update(["autoInstallToolchains": .bool($0)]) }))
                    }
                    SettingRow(tr("settings.autoDeps"), info: tr("settings.autoDeps.info")) {
                        PPSwitch(isOn: Binding(get: { s.autoInstallDependencies }, set: { update(["autoInstallDependencies": .bool($0)]) }))
                    }
                    SettingRow(tr("settings.autoSync"), info: tr("settings.autoSync.info")) {
                        PPSwitch(isOn: Binding(get: { s.autoSyncOnImport }, set: { update(["autoSyncOnImport": .bool($0)]) }))
                    }
                    PPDivider(subtle: true)
                    SettingRow(tr("settings.pluginIdle"), info: tr("settings.pluginIdle.info")) {
                        NumberSetting(value: s.pluginIdleSeconds, unit: tr("unit.seconds")) { update(["pluginIdleSeconds": .number(Double($0))]) }
                    }
                    SettingRow(tr("settings.logLines"), info: tr("settings.logLines.info")) {
                        NumberSetting(value: s.logLinesPerRun, unit: tr("unit.lines")) { update(["logLinesPerRun": .number(Double($0))]) }
                    }
                    SettingRow(tr("settings.logRetention")) {
                        NumberSetting(value: s.logRetentionRuns, unit: tr("unit.runs")) { update(["logRetentionRuns": .number(Double($0))]) }
                    }
                    SettingRow(tr("settings.gitFetch"), info: tr("settings.gitFetch.info")) {
                        NumberSetting(value: s.gitAutoFetchMinutes, unit: tr("unit.minutes")) { update(["gitAutoFetchMinutes": .number(Double($0))]) }
                    }
                }
            }
        }
    }

    // MARK: API 与 MCP

    private var apiSection: some View {
        PPSection(tr("settings.api"), info: tr("settings.api.info")) {
            PPCard {
                VStack(alignment: .leading, spacing: M.md) {
                    SettingRow(tr("settings.apiBase")) { copyable(model.systemInfo?.apiBase ?? "") }
                    SettingRow(tr("settings.mcpUrl")) { copyable(mcp?.url ?? model.systemInfo?.mcpUrl ?? "") }
                    SettingRow(tr("settings.token")) {
                        HStack(spacing: M.sm) {
                            Text(String(repeating: "•", count: 16)).font(.pp(.mono)).foregroundColor(theme.textSecondary)
                            PPButton(tr("settings.copyToken"), icon: "key-round", size: .small) {
                                if let t = mcp?.token ?? AppModel.shared.api?.token { Actions.copy(t) }
                            }
                        }
                    }
                    SettingRow(tr("settings.claudeCode")) {
                        HStack(spacing: M.sm) {
                            Text(mcp?.claudeCode ?? "").font(.pp(.monoSmall)).foregroundColor(theme.textSecondary)
                                .lineLimit(1).truncationMode(.middle)
                            PPButton(tr("common.copy"), icon: "copy", size: .small) {
                                if let c = mcp?.claudeCode { Actions.copy(c) }
                            }
                            .disabled(mcp == nil)
                        }
                    }
                    SettingRow(tr("settings.claudeDesktop")) {
                        PPButton(tr("settings.copyConfig"), icon: "copy", size: .small) {
                            if let cfg = mcp?.claudeDesktop { Actions.copy(prettyJSON(cfg)) }
                        }
                        .disabled(mcp?.claudeDesktop == nil)
                    }
                    SettingRow(tr("settings.cli"), info: tr("settings.cli.info")) {
                        PPButton(tr("settings.installCli"), icon: "terminal", size: .small) {
                            AppModel.shared.perform(tr("settings.installCli")) { api in
                                let r = try await api.installCLI()
                                AppModel.shared.toasts.show("success", tr("settings.cliInstalled"), message: r.path)
                            }
                        }
                    }
                }
            }
        }
    }

    private func copyable(_ text: String) -> some View {
        HStack(spacing: M.sm) {
            Text(text).font(.pp(.mono)).textSelection(.enabled).lineLimit(1)
            PPIconButton("copy", help: tr("common.copy"), size: M.iconButtonSmall) { Actions.copy(text) }
        }
    }

    private func prettyJSON(_ v: JSONValue) -> String {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return (try? String(decoding: enc.encode(v), as: UTF8.self)) ?? v.jsonString
    }

    // MARK: 关于

    private var about: some View {
        PPSection(tr("settings.about")) {
            PPCard {
                let info = model.systemInfo
                let uiVersion = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
                PPKeyValueList(rows: [
                    .init(key: tr("settings.about.ui"), value: uiVersion, mono: true),
                    .init(key: tr("settings.about.daemon"), value: info?.version ?? "-", mono: true),
                    .init(key: tr("settings.about.daemonMemory"), value: Format.memory(mb: info?.memoryMB ?? 0), mono: true),
                    .init(key: tr("settings.about.pluginProcs"), value: "\(info?.pluginProcs ?? 0)", mono: true),
                    .init(key: tr("settings.about.pluginMemory"), value: Format.memory(mb: info?.pluginMemoryMB ?? 0), mono: true),
                    .init(key: tr("settings.about.dataDir"), value: Format.abbreviatePath(info?.dataDir ?? ""), mono: true),
                ], keyWidth: M.formLabelWidth)
            }
        }
        .task {
            // 每次打开时刷新一次内存数据
            if let i = try? await AppModel.shared.client().systemInfo() { AppModel.shared.updateSystemInfo(i) }
        }
    }

    private func update(_ patch: [String: JSONValue]) {
        if var cur = data.settings {
            // 先本地生效，避免控件回跳
            if let v = patch["locale"]?.stringValue { cur.locale = v }
            if let v = patch["theme"]?.stringValue { cur.theme = v }
            if let v = patch["mirrorRegion"]?.stringValue { cur.mirrorRegion = v }
            if let v = patch["terminal"]?.stringValue { cur.terminal = v }
            if let v = patch["autoInstallToolchains"]?.boolValue { cur.autoInstallToolchains = v }
            if let v = patch["autoInstallDependencies"]?.boolValue { cur.autoInstallDependencies = v }
            if let v = patch["autoSyncOnImport"]?.boolValue { cur.autoSyncOnImport = v }
            if let v = patch["pluginIdleSeconds"]?.intValue { cur.pluginIdleSeconds = v }
            if let v = patch["logLinesPerRun"]?.intValue { cur.logLinesPerRun = v }
            if let v = patch["logRetentionRuns"]?.intValue { cur.logRetentionRuns = v }
            if let v = patch["gitAutoFetchMinutes"]?.intValue { cur.gitAutoFetchMinutes = v }
            data.settings = cur
        }
        AppModel.shared.perform(tr("settings.saveFailed")) { api in
            let s = try await api.updateSettings(.object(patch))
            AppModel.shared.applySettings(s)
            if patch["mirrorRegion"] != nil { mirrors = try? await api.mirrorsStatus() }
        }
    }
}

/// 设置行：标题（可带 i）+ 控件
struct SettingRow<Content: View>: View {
    let title: String
    var info: String?
    @ViewBuilder let content: Content

    init(_ title: String, info: String? = nil, @ViewBuilder content: () -> Content) {
        self.title = title
        self.info = info
        self.content = content()
    }

    var body: some View {
        HStack(spacing: M.md) {
            HStack(spacing: M.xs) {
                Text(title).lineLimit(1)
                if let info { PPInfoButton(text: info) }
            }
            .frame(width: M.formLabelWidth, alignment: .leading)
            content
            Spacer(minLength: 0)
        }
        .frame(minHeight: M.rowHeight)
    }
}

/// 数字设置：回车或失焦时提交
struct NumberSetting: View {
    let value: Int
    var unit: String = ""
    let commit: (Int) -> Void
    @State private var text = ""
    @FocusState private var focused: Bool
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.sm) {
            InputChrome(focused: focused) {
                TextField("", text: $text)
                    .textFieldStyle(.plain)
                    .font(.pp(.mono))
                    .focused($focused)
                    .onSubmit(apply)
            }
            .frame(width: 110)
            Text(unit).foregroundColor(theme.textSecondary)
        }
        .onAppear { text = "\(value)" }
        .onChange(of: value) { v in if !focused { text = "\(v)" } }
        .onChange(of: focused) { f in if !f { apply() } }
    }

    private func apply() {
        guard let v = Int(text.trimmingCharacters(in: .whitespaces)), v >= 0 else { text = "\(value)"; return }
        if v != value { commit(v) }
    }
}

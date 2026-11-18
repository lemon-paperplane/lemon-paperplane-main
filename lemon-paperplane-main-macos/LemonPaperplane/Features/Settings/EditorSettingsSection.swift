import PaperplaneKit
import SwiftUI

/// 设置页「编辑器」分组：写入 settings.editor（docs/editor.md 第 3 节）。
struct EditorSettingsSection: View {
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    private var e: EditorSettings { data.settings?.editor ?? EditorSettings() }

    /// 本平台默认的方案 id
    private var platformDefault: String { data.contributions.defaultKeymap ?? EditorSettings.platformDefaultKeymap }

    var body: some View {
        PPSection(tr("settings.editor"), info: tr("settings.editor.info")) {
            PPCard {
                VStack(alignment: .leading, spacing: M.md) {
                    SettingRow(tr("settings.editor.keymap")) {
                        PPDropdown(options: keymapOptions, selection: Binding(get: { e.keymap.isEmpty ? platformDefault : e.keymap }, set: { v in
                            update { $0.keymap = v == platformDefault ? "" : v }
                        }), minWidth: 240)
                    }
                    SettingRow(tr("settings.editor.theme")) {
                        PPDropdown(options: themeOptions, selection: Binding(get: { e.theme }, set: { v in update { $0.theme = v } }),
                                   minWidth: 240)
                    }
                    SettingRow(tr("settings.editor.fontSize")) {
                        NumberSetting(value: e.fontSize, unit: "pt") { v in update { $0.fontSize = min(max(v, 8), 40) } }
                    }
                    SettingRow(tr("settings.editor.fontFamily")) {
                        TextSetting(value: e.fontFamily, placeholder: tr("settings.editor.fontFamily.default")) { v in
                            update { $0.fontFamily = v }
                        }
                    }
                    SettingRow(tr("settings.editor.tabSize")) {
                        NumberSetting(value: e.tabSize, unit: "") { v in update { $0.tabSize = min(max(v, 1), 16) } }
                    }
                    SettingRow(tr("settings.editor.indent")) {
                        PPSegmented(options: [(true, tr("settings.editor.spaces")), (false, tr("settings.editor.tabs"))],
                                    selection: Binding(get: { e.insertSpaces }, set: { v in update { $0.insertSpaces = v } }))
                    }
                    SettingRow(tr("settings.editor.wordWrap")) {
                        PPSwitch(isOn: Binding(get: { e.wordWrap }, set: { v in update { $0.wordWrap = v } }))
                    }
                    SettingRow(tr("settings.editor.autoSave")) {
                        PPDropdown(options: [
                            .init(value: "off", title: tr("settings.editor.autoSave.off")),
                            .init(value: "onFocusChange", title: tr("settings.editor.autoSave.onFocusChange")),
                            .init(value: "afterDelay", title: tr("settings.editor.autoSave.afterDelay")),
                        ], selection: Binding(get: { e.autoSave.isEmpty ? "off" : e.autoSave }, set: { v in update { $0.autoSave = v } }),
                        minWidth: 240)
                    }
                    SettingRow(tr("settings.editor.trim")) {
                        PPSwitch(isOn: Binding(get: { e.trimTrailingWhitespace }, set: { v in update { $0.trimTrailingWhitespace = v } }))
                    }
                    SettingRow(tr("settings.editor.finalNewline")) {
                        PPSwitch(isOn: Binding(get: { e.insertFinalNewline }, set: { v in update { $0.insertFinalNewline = v } }))
                    }
                    SettingRow(tr("settings.editor.showWhitespace")) {
                        PPSwitch(isOn: Binding(get: { e.showWhitespace }, set: { v in update { $0.showWhitespace = v } }))
                    }
                }
            }
        }
    }

    /// 快捷键方案：本平台与通用的在前；没有插件时也显示默认方案名
    private var keymapOptions: [PPDropdown<String>.Option] {
        var list = data.contributions.keymaps
        if !list.contains(where: { $0.id == platformDefault }) {
            list.insert(KeymapInfo.placeholder(id: platformDefault), at: 0)
        }
        let order = ["macos": 0, "any": 1]
        list.sort { (order[$0.platform] ?? 2, $0.name) < (order[$1.platform] ?? 2, $1.name) }
        return list.map { k in
            var detail = platformTitle(k.platform)
            if k.id == platformDefault { detail += " · " + tr("settings.editor.keymap.default") }
            return .init(value: k.id, title: k.name.isEmpty ? k.id : k.name, detail: detail)
        }
    }

    private var themeOptions: [PPDropdown<String>.Option] {
        [.init(value: "", title: tr("settings.editor.theme.followApp"))] + data.contributions.editorThemes.map { t in
            .init(value: t.id, title: t.name.isEmpty ? t.id : t.name, detail: t.kind == "dark" ? tr("settings.dark") : tr("settings.light"))
        }
    }

    private func platformTitle(_ p: String) -> String {
        switch p {
        case "macos": return "macOS"
        case "windows": return "Windows"
        case "linux": return "Linux"
        default: return tr("settings.editor.platform.any")
        }
    }

    /// 改一项：本地先生效，再把整个 editor 对象写回（settings.update 做 JSON merge）
    private func update(_ change: (inout EditorSettings) -> Void) {
        var next = e
        change(&next)
        guard next != e else { return }
        if var s = data.settings {
            s.editor = next
            data.settings = s
        }
        let editor = next
        AppModel.shared.perform(tr("settings.saveFailed")) { api in
            let s = try await api.updateSettings(["editor": try JSONValue(encoding: editor)])
            AppModel.shared.applySettings(s)
        }
    }
}

extension KeymapInfo {
    /// contributions 里还没有快捷键插件时显示的默认方案
    static func placeholder(id: String) -> KeymapInfo {
        let json = #"{"id":"\#(id)","name":"Eclipse (macOS)","platform":"macos","plugin":""}"#
        return (try? JSONCoding.decoder.decode(KeymapInfo.self, from: Data(json.utf8)))
            ?? (try! JSONCoding.decoder.decode(KeymapInfo.self, from: Data(#"{"id":"eclipse-macos"}"#.utf8)))
    }
}

/// 文本设置：回车或失焦时提交
struct TextSetting: View {
    let value: String
    var placeholder = ""
    let commit: (String) -> Void
    @State private var text = ""
    @FocusState private var focused: Bool
    @Environment(\.theme) private var theme

    var body: some View {
        InputChrome(focused: focused) {
            ZStack(alignment: .leading) {
                if text.isEmpty { Text(placeholder).foregroundColor(theme.textTertiary).allowsHitTesting(false) }
                TextField("", text: $text)
                    .textFieldStyle(.plain)
                    .focused($focused)
                    .onSubmit(apply)
            }
        }
        .frame(width: 240)
        .onAppear { text = value }
        .onChange(of: value) { v in if !focused { text = v } }
        .onChange(of: focused) { f in if !f { apply() } }
    }

    private func apply() {
        let v = text.trimmingCharacters(in: .whitespaces)
        if v != value { commit(v) }
    }
}

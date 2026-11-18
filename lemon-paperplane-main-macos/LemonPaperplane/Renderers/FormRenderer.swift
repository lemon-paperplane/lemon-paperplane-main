import AppKit
import PaperplaneKit
import SwiftUI

/// 声明式表单渲染（sdk.FormField）：string / text / boolean / number / select / multiselect / keyvalue / path / list，
/// 外加 password（密码框）。info 显示为 i 图标；advanced 字段折叠进「更多」。
struct FormView: View {
    let fields: [FormField]
    @Binding var values: [String: JSONValue]
    var optionValues: [String: [Option]] = [:]
    var showErrors = false
    var labelWidth: CGFloat = M.formLabelWidth
    @State private var showAdvanced = false
    @Environment(\.theme) private var theme

    var body: some View {
        let basic = fields.filter { !$0.advanced }
        let advanced = fields.filter { $0.advanced }
        let missing = showErrors ? Set(Self.missingRequired(fields, values)) : []
        VStack(alignment: .leading, spacing: M.md) {
            ForEach(basic) { f in
                FieldRow(field: f, values: $values, options: options(for: f), invalid: missing.contains(f.key), labelWidth: labelWidth)
            }
            if !advanced.isEmpty {
                Button {
                    withAnimation(.easeOut(duration: 0.12)) { showAdvanced.toggle() }
                } label: {
                    HStack(spacing: M.xs) {
                        PPIcon(showAdvanced ? "chevron-down" : "chevron-right", size: M.iconSmall)
                        Text(tr("form.more"))
                    }
                    .foregroundColor(theme.textSecondary)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                if showAdvanced {
                    ForEach(advanced) { f in
                        FieldRow(field: f, values: $values, options: options(for: f), invalid: missing.contains(f.key), labelWidth: labelWidth)
                    }
                }
            }
        }
    }

    private func options(for f: FormField) -> [Option] {
        var out = f.options
        if let from = f.optionsFrom, let dyn = optionValues[from] {
            for o in dyn where !out.contains(where: { $0.value == o.value }) { out.append(o) }
        }
        return out
    }

    // MARK: 值处理

    /// 没给值的字段填默认值
    static func initialValues(_ fields: [FormField], _ values: [String: JSONValue]) -> [String: JSONValue] {
        var v = values
        for f in fields where v[f.key] == nil {
            if let d = f.default { v[f.key] = d }
        }
        return v
    }

    static func missingRequired(_ fields: [FormField], _ values: [String: JSONValue]) -> [String] {
        fields.filter { f in
            guard f.required, f.type != "boolean" else { return false }
            return values[f.key]?.isEmptyValue ?? true
        }.map(\.key)
    }

    /// 提交前整理：去掉 null；数字字段的字符串转数字
    static func normalized(_ fields: [FormField], _ values: [String: JSONValue]) -> [String: JSONValue] {
        var out: [String: JSONValue] = [:]
        for (k, v) in values where !v.isNull {
            if let f = fields.first(where: { $0.key == k }), f.type == "number", case .string(let s) = v {
                if let d = Double(s) { out[k] = .number(d) }
                continue
            }
            out[k] = v
        }
        return out
    }
}

private struct FieldRow: View {
    let field: FormField
    @Binding var values: [String: JSONValue]
    let options: [Option]
    let invalid: Bool
    let labelWidth: CGFloat
    @Environment(\.theme) private var theme

    private var tall: Bool { ["text", "keyvalue", "list", "multiselect"].contains(field.type) }

    var body: some View {
        if field.type == "boolean" {
            HStack(spacing: M.md) {
                Spacer().frame(width: labelWidth)
                PPCheckbox(isOn: boolBinding, label: field.title)
                if let info = field.info, !info.isEmpty { PPInfoButton(text: info) }
                Spacer(minLength: 0)
            }
        } else {
            HStack(alignment: tall ? .top : .center, spacing: M.md) {
                HStack(spacing: M.xs) {
                    Text(field.title).lineLimit(2).foregroundColor(invalid ? theme.danger : theme.textPrimary)
                    if field.required { Text("*").foregroundColor(theme.danger) }
                    if let info = field.info, !info.isEmpty { PPInfoButton(text: info) }
                    Spacer(minLength: 0)
                }
                .frame(width: labelWidth, alignment: .leading)
                .padding(.top, tall ? 6 : 0)
                control
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }

    @ViewBuilder private var control: some View {
        switch field.type {
        case "text":
            PPTextArea(text: stringBinding, placeholder: field.placeholder ?? "")
        case "number":
            PPNumberField(placeholder: field.placeholder ?? "", value: numberBinding)
        case "select":
            // 非必填时首项为「无」；必填时没有空选项，未选显示占位
            PPDropdown(options: (field.required ? [] : [PPDropdown<String>.Option(value: "", title: tr("form.none"))])
                + options.map { PPDropdown<String>.Option(value: $0.value, title: $0.displayLabel) },
                selection: stringBinding, placeholder: field.placeholder ?? tr("form.choose"), fullWidth: true)
        case "multiselect":
            MultiSelectEditor(options: options, selection: arrayBinding)
        case "keyvalue":
            KeyValueEditor(pairs: mapBinding)
        case "list":
            StringListEditor(items: arrayBinding, placeholder: field.placeholder ?? "")
        case "path":
            HStack(spacing: M.sm) {
                PPTextField(field.placeholder ?? "", text: stringBinding, mono: true, invalid: invalid)
                PPButton(tr("form.browse")) { choosePath() }
            }
        default:
            PPTextField(field.placeholder ?? "", text: stringBinding, secure: field.isSecret, invalid: invalid)
        }
    }

    private var stringBinding: Binding<String> {
        Binding(get: { values[field.key]?.stringValue ?? "" }, set: { values[field.key] = .string($0) })
    }

    private var boolBinding: Binding<Bool> {
        Binding(get: { values[field.key]?.boolValue ?? false }, set: { values[field.key] = .bool($0) })
    }

    private var numberBinding: Binding<Double?> {
        Binding(get: { values[field.key]?.doubleValue }, set: { values[field.key] = $0.map { .number($0) } ?? .null })
    }

    private var arrayBinding: Binding<[String]> {
        Binding(get: {
            if let a = values[field.key]?.stringArray { return a }
            // 兼容字符串形式（逗号或换行分隔）
            if let s = values[field.key]?.stringValue, !s.isEmpty {
                return s.split(whereSeparator: { $0 == "," || $0 == "\n" }).map { $0.trimmingCharacters(in: .whitespaces) }
            }
            return []
        }, set: { values[field.key] = JSONValue($0) })
    }

    private var mapBinding: Binding<[(String, String)]> {
        Binding(get: {
            (values[field.key]?.objectValue ?? [:]).map { ($0.key, $0.value.stringValue ?? "") }.sorted { $0.0 < $1.0 }
        }, set: { pairs in
            var o: [String: JSONValue] = [:]
            for (k, v) in pairs where !k.isEmpty { o[k] = .string(v) }
            values[field.key] = .object(o)
        })
    }

    private func choosePath() {
        let p = NSOpenPanel()
        p.canChooseFiles = true
        p.canChooseDirectories = true
        p.allowsMultipleSelection = false
        if p.runModal() == .OK, let url = p.url { values[field.key] = .string(url.path) }
    }
}

/// 多选：复选框列表
struct MultiSelectEditor: View {
    let options: [Option]
    @Binding var selection: [String]
    @Environment(\.theme) private var theme

    var body: some View {
        if options.isEmpty {
            StringListEditor(items: $selection, placeholder: "")
        } else {
            ScrollView {
                VStack(alignment: .leading, spacing: M.xs) {
                    ForEach(options, id: \.value) { o in
                        PPCheckbox(isOn: Binding(get: { selection.contains(o.value) }, set: { on in
                            if on { if !selection.contains(o.value) { selection.append(o.value) } }
                            else { selection.removeAll { $0 == o.value } }
                        }), label: o.displayLabel)
                    }
                }
                .padding(M.sm)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: options.count > 6 ? 160 : nil)
            .fixedSize(horizontal: false, vertical: options.count <= 6)
            .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgInput))
            .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(theme.borderStrong, lineWidth: 1))
        }
    }
}

/// 键值表编辑（环境变量等）
struct KeyValueEditor: View {
    @Binding var pairs: [(String, String)]
    var keyPlaceholder = "KEY"
    var valuePlaceholder = "value"
    @State private var rows: [Row] = []
    @State private var loaded = false
    @Environment(\.theme) private var theme

    struct Row: Identifiable, Equatable {
        let id = UUID()
        var key: String
        var value: String
    }

    var body: some View {
        VStack(alignment: .leading, spacing: M.xs) {
            ForEach($rows) { $row in
                HStack(spacing: M.sm) {
                    PPTextField(keyPlaceholder, text: $row.key, mono: true).frame(maxWidth: 220)
                    PPTextField(valuePlaceholder, text: $row.value, mono: true)
                    PPIconButton("x", help: tr("common.remove"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                        rows.removeAll { $0.id == row.id }
                    }
                }
            }
            PPButton(tr("form.addRow"), icon: "plus", kind: .ghost, size: .small) {
                rows.append(Row(key: "", value: ""))
            }
        }
        .onAppear {
            if !loaded { rows = pairs.map { Row(key: $0.0, value: $0.1) }; loaded = true }
        }
        .onChange(of: rows) { r in
            pairs = r.map { ($0.key.trimmingCharacters(in: .whitespaces), $0.value) }
        }
    }
}

/// 字符串列表编辑（list 字段：JVM 参数、程序参数等）
struct StringListEditor: View {
    @Binding var items: [String]
    var placeholder = ""
    @State private var rows: [Row] = []
    @State private var loaded = false

    struct Row: Identifiable, Equatable {
        let id = UUID()
        var text: String
    }

    var body: some View {
        VStack(alignment: .leading, spacing: M.xs) {
            ForEach($rows) { $row in
                HStack(spacing: M.sm) {
                    PPTextField(placeholder, text: $row.text, mono: true)
                    PPIconButton("x", help: tr("common.remove"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                        rows.removeAll { $0.id == row.id }
                    }
                }
            }
            PPButton(tr("form.addItem"), icon: "plus", kind: .ghost, size: .small) {
                rows.append(Row(text: ""))
            }
        }
        .onAppear {
            if !loaded { rows = items.map { Row(text: $0) }; loaded = true }
        }
        .onChange(of: rows) { r in
            items = r.map(\.text).filter { !$0.isEmpty }
        }
    }
}

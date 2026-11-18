import AppKit
import PaperplaneKit
import SwiftUI

/// 输入框外框：描边、圆角、聚焦时强调色描边加光圈。
struct InputChrome<Content: View>: View {
    var focused: Bool
    var invalid = false
    var disabled = false
    var height: CGFloat? = M.controlHeight
    @ViewBuilder let content: Content
    @Environment(\.theme) private var theme

    var body: some View {
        content
            .padding(.horizontal, M.md)
            .frame(height: height)
            .frame(minHeight: height == nil ? M.controlHeight : nil)
            .background(RoundedRectangle(cornerRadius: M.radiusMD, style: .continuous)
                .fill(disabled ? theme.bgInputDisabled : theme.bgInput))
            .overlay(RoundedRectangle(cornerRadius: M.radiusMD, style: .continuous)
                .strokeBorder(invalid ? theme.danger : (focused ? theme.borderFocus : theme.borderStrong), lineWidth: 1))
            .overlay(RoundedRectangle(cornerRadius: M.radiusMD + 2, style: .continuous)
                .stroke(focused ? theme.focusRing : .clear, lineWidth: 2).padding(-2))
    }
}

/// 单行输入
struct PPTextField: View {
    let placeholder: String
    @Binding var text: String
    var icon: String?
    var mono = false
    var secure = false
    var invalid = false
    var clearable = false
    var height: CGFloat = M.controlHeight
    var onSubmit: (() -> Void)?
    @FocusState private var focused: Bool
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var enabled

    init(_ placeholder: String, text: Binding<String>, icon: String? = nil, mono: Bool = false, secure: Bool = false,
         invalid: Bool = false, clearable: Bool = false, height: CGFloat = M.controlHeight, onSubmit: (() -> Void)? = nil) {
        self.placeholder = placeholder
        self._text = text
        self.icon = icon
        self.mono = mono
        self.secure = secure
        self.invalid = invalid
        self.clearable = clearable
        self.height = height
        self.onSubmit = onSubmit
    }

    var body: some View {
        InputChrome(focused: focused, invalid: invalid, disabled: !enabled, height: height) {
            HStack(spacing: M.sm) {
                if let icon { PPIcon(icon, size: M.iconSmall, color: theme.iconSecondary) }
                ZStack(alignment: .leading) {
                    if text.isEmpty {
                        Text(placeholder).foregroundColor(theme.textTertiary).lineLimit(1)
                            .font(mono ? .pp(.mono) : .pp(.body))
                            .allowsHitTesting(false)
                    }
                    Group {
                        if secure {
                            SecureField("", text: $text)
                        } else {
                            TextField("", text: $text)
                        }
                    }
                    .textFieldStyle(.plain)
                    .font(mono ? .pp(.mono) : .pp(.body))
                    .focused($focused)
                    .onSubmit { onSubmit?() }
                }
                if clearable && !text.isEmpty {
                    Button { text = "" } label: { PPIcon("x", size: M.iconSmall, color: theme.iconSecondary) }
                        .buttonStyle(.plain)
                }
            }
        }
    }
}

/// 多行文本
struct PPTextArea: View {
    @Binding var text: String
    var placeholder = ""
    var mono = false
    var minHeight: CGFloat = 72
    @FocusState private var focused: Bool
    @Environment(\.theme) private var theme

    var body: some View {
        ZStack(alignment: .topLeading) {
            if text.isEmpty {
                Text(placeholder).foregroundColor(theme.textTertiary)
                    .padding(.top, 6).padding(.leading, 5)
                    .allowsHitTesting(false)
            }
            TextEditor(text: $text)
                .font(mono ? .pp(.mono) : .pp(.body))
                .scrollContentBackground(.hidden)
                .focused($focused)
        }
        .padding(.vertical, M.xs)
        .padding(.horizontal, M.xs)
        .frame(minHeight: minHeight)
        .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgInput))
        .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(focused ? theme.borderFocus : theme.borderStrong, lineWidth: 1))
        .overlay(RoundedRectangle(cornerRadius: M.radiusMD + 2).stroke(focused ? theme.focusRing : .clear, lineWidth: 2).padding(-2))
    }
}

/// 开关（自绘，三端一致）
struct PPSwitch: View {
    @Binding var isOn: Bool
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        Button {
            withAnimation(.easeOut(duration: 0.12)) { isOn.toggle() }
        } label: {
            ZStack(alignment: isOn ? .trailing : .leading) {
                Capsule().fill(isOn ? theme.accent : theme.switchOff)
                Circle().fill(Color.white).padding(2).shadow(color: .black.opacity(0.15), radius: 1, y: 0.5)
            }
            .frame(width: 30, height: 18)
            .opacity(enabled ? 1 : 0.5)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(.isButton)
        .accessibilityValue(isOn ? "on" : "off")
    }
}

/// 复选框
struct PPCheckbox: View {
    @Binding var isOn: Bool
    var label: String?
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        Button { isOn.toggle() } label: {
            HStack(spacing: M.sm) {
                ZStack {
                    RoundedRectangle(cornerRadius: M.radiusXS).fill(isOn ? theme.accent : theme.bgInput)
                    RoundedRectangle(cornerRadius: M.radiusXS).strokeBorder(isOn ? theme.accent : theme.borderStrong, lineWidth: 1)
                    if isOn { PPIcon("check", size: 12, color: theme.textOnAccent) }
                }
                .frame(width: 16, height: 16)
                if let label { Text(label).lineLimit(1) }
            }
            .contentShape(Rectangle())
            .opacity(enabled ? 1 : 0.5)
        }
        .buttonStyle(.plain)
    }
}

/// 单选框（分组选项）
struct PPRadio: View {
    let selected: Bool
    let label: String
    let action: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        Button(action: action) {
            HStack(spacing: M.sm) {
                ZStack {
                    Circle().fill(theme.bgInput)
                    Circle().strokeBorder(selected ? theme.accent : theme.borderStrong, lineWidth: selected ? 5 : 1)
                }
                .frame(width: 16, height: 16)
                Text(label)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

/// 分段控件
struct PPSegmented<Value: Hashable>: View {
    let options: [(Value, String)]
    @Binding var selection: Value
    var size: PPButtonSize = .regular
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: 2) {
            ForEach(options.indices, id: \.self) { i in
                let (v, title) = options[i]
                let on = v == selection
                Button { selection = v } label: {
                    Text(title)
                        .font(.pp(.label))
                        .foregroundColor(on ? theme.textPrimary : theme.textSecondary)
                        .padding(.horizontal, M.lg)
                        .frame(height: size.height - 4)
                        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(on ? theme.bgSurface : .clear)
                            .shadow(color: on ? .black.opacity(theme.isDark ? 0.3 : 0.08) : .clear, radius: 1, y: 0.5))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.neutralSubtle))
    }
}

/// 下划线标签栏（工程详情、插件页）
struct PPTabBar<Value: Hashable>: View {
    struct Tab: Identifiable {
        let value: Value
        let title: String
        var icon: IconSource?
        var badge: Int?
        var id: Value { value }
    }

    let tabs: [Tab]
    @Binding var selection: Value
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.xs) {
            ForEach(tabs) { tab in
                TabButton(tab: tab, selected: tab.value == selection) { selection = tab.value }
            }
        }
        .frame(height: M.tabBarHeight)
    }

    private struct TabButton: View {
        let tab: Tab
        let selected: Bool
        let action: () -> Void
        @Environment(\.theme) private var theme
        @State private var hover = false

        var body: some View {
            Button(action: action) {
                VStack(spacing: 0) {
                    Spacer(minLength: 0)
                    HStack(spacing: M.sm) {
                        if let icon = tab.icon { PPIcon(ref: icon, size: M.iconSmall) }
                        Text(tab.title).font(.pp(selected ? .bodyStrong : .body))
                        if let b = tab.badge, b > 0 { PPCountBadge(count: b) }
                    }
                    .foregroundColor(selected ? theme.textPrimary : theme.textSecondary)
                    .padding(.horizontal, M.md)
                    .frame(height: M.tabBarHeight - 6)
                    .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(hover && !selected ? theme.bgHover : .clear))
                    Rectangle().fill(selected ? theme.accent : .clear).frame(height: 2)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .onHover { hover = $0 }
        }
    }
}

/// 数值输入（存为 Double）
struct PPNumberField: View {
    let placeholder: String
    @Binding var value: Double?
    @State private var text = ""

    var body: some View {
        PPTextField(placeholder, text: Binding(get: {
            text.isEmpty ? (value.map { $0.rounded() == $0 ? String(Int64($0)) : String($0) } ?? "") : text
        }, set: { s in
            text = s
            let t = s.trimmingCharacters(in: .whitespaces)
            value = t.isEmpty ? nil : Double(t)
        }), mono: true, invalid: !text.isEmpty && Double(text.trimmingCharacters(in: .whitespaces)) == nil)
    }
}

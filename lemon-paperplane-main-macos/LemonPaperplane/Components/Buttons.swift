import PaperplaneKit
import SwiftUI

/// 按钮种类（design-system.md「按钮」）
enum PPButtonKind {
    case primary // 强调色实心
    case secondary // 描边
    case ghost // 无底，悬停出底色（工具栏）
    case danger // 危险操作实心红
    case link // 文本链接
}

enum PPButtonSize {
    case regular // 28
    case small // 24

    var height: CGFloat { self == .regular ? M.controlHeight : M.controlHeightSmall }
    var hPadding: CGFloat { self == .regular ? M.lg : M.md }
}

struct PPButtonStyle: ButtonStyle {
    var kind: PPButtonKind = .secondary
    var size: PPButtonSize = .regular
    var fullWidth = false

    func makeBody(configuration: Configuration) -> some View {
        StyledButtonBody(configuration: configuration, kind: kind, size: size, fullWidth: fullWidth)
    }
}

private struct StyledButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let kind: PPButtonKind
    let size: PPButtonSize
    let fullWidth: Bool
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var enabled
    @State private var hover = false

    var body: some View {
        let pressed = configuration.isPressed
        configuration.label
            .font(.pp(kind == .link ? .body : .label))
            .lineLimit(1)
            .foregroundColor(fg)
            .padding(.horizontal, kind == .link ? 0 : size.hPadding)
            .frame(height: kind == .link ? nil : size.height)
            .frame(maxWidth: fullWidth ? .infinity : nil)
            .background(
                RoundedRectangle(cornerRadius: M.radiusMD, style: .continuous).fill(bg(pressed: pressed))
            )
            .overlay(
                RoundedRectangle(cornerRadius: M.radiusMD, style: .continuous)
                    .strokeBorder(kind == .secondary ? theme.buttonSecondaryBorder : .clear, lineWidth: 1)
            )
            .opacity(enabled ? 1 : 0.5)
            .contentShape(Rectangle())
            .onHover { hover = $0 && enabled }
    }

    private var fg: Color {
        switch kind {
        case .primary, .danger: return theme.textOnAccent
        case .secondary, .ghost: return theme.textPrimary
        case .link: return hover ? theme.accentHover : theme.textLink
        }
    }

    private func bg(pressed: Bool) -> Color {
        switch kind {
        case .primary: return pressed ? theme.accentPressed : (hover ? theme.accentHover : theme.accent)
        case .danger: return pressed ? theme.dangerHover : (hover ? theme.dangerHover : theme.danger)
        case .secondary: return pressed ? theme.bgPressed : (hover ? theme.buttonSecondaryHover : theme.buttonSecondaryBg)
        case .ghost: return pressed ? theme.bgPressed : (hover ? theme.bgHover : .clear)
        case .link: return .clear
        }
    }
}

/// 文本按钮（可带图标）
struct PPButton: View {
    let title: String
    var iconRef: IconSource?
    var kind: PPButtonKind = .secondary
    var size: PPButtonSize = .regular
    var fullWidth = false
    var busy = false
    let action: () -> Void

    init(_ title: String, icon: String? = nil, kind: PPButtonKind = .secondary, size: PPButtonSize = .regular,
         fullWidth: Bool = false, busy: Bool = false, action: @escaping () -> Void) {
        self.title = title
        self.iconRef = icon.map { .builtin($0) }
        self.kind = kind
        self.size = size
        self.fullWidth = fullWidth
        self.busy = busy
        self.action = action
    }

    init(_ title: String, iconRef: IconSource?, kind: PPButtonKind = .secondary, size: PPButtonSize = .regular,
         busy: Bool = false, action: @escaping () -> Void) {
        self.title = title
        self.iconRef = iconRef
        self.kind = kind
        self.size = size
        self.busy = busy
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: M.sm) {
                if busy {
                    PPSpinner(size: M.iconSmall)
                } else if let iconRef {
                    PPIcon(ref: iconRef, size: size == .small ? M.iconSmall : M.icon)
                }
                Text(title)
            }
        }
        .buttonStyle(PPButtonStyle(kind: kind, size: size, fullWidth: fullWidth))
        .disabled(busy)
    }
}

/// 只有图标的方形按钮（工具栏、卡片运行区）。tooltip 用系统 help。
struct PPIconButton: View {
    let icon: IconSource?
    var help: String?
    var size: CGFloat = M.iconButton
    var iconSize: CGFloat = M.icon
    var tint: Color?
    var active = false
    var busy = false
    let action: () -> Void

    init(_ icon: String, help: String? = nil, size: CGFloat = M.iconButton, iconSize: CGFloat = M.icon,
         tint: Color? = nil, active: Bool = false, busy: Bool = false, action: @escaping () -> Void) {
        self.icon = .builtin(icon)
        self.help = help
        self.size = size
        self.iconSize = iconSize
        self.tint = tint
        self.active = active
        self.busy = busy
        self.action = action
    }

    init(ref: IconSource?, help: String? = nil, size: CGFloat = M.iconButton, tint: Color? = nil, action: @escaping () -> Void) {
        self.icon = ref
        self.help = help
        self.size = size
        self.tint = tint
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            if busy {
                PPSpinner(size: M.iconSmall)
            } else {
                PPIcon(ref: icon, size: iconSize)
            }
        }
        .buttonStyle(IconButtonStyle(size: size, tint: tint, active: active))
        .help(help ?? "")
        .accessibilityLabel(help ?? "")
    }
}

struct IconButtonStyle: ButtonStyle {
    var size: CGFloat = M.iconButton
    var tint: Color?
    var active = false

    func makeBody(configuration: Configuration) -> some View {
        IconButtonBody(configuration: configuration, size: size, tint: tint, active: active)
    }
}

private struct IconButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let size: CGFloat
    let tint: Color?
    let active: Bool
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var enabled
    @State private var hover = false

    var body: some View {
        configuration.label
            .foregroundColor(enabled ? (tint ?? (active ? theme.accent : theme.iconPrimary)) : theme.textDisabled)
            .frame(width: size, height: size)
            .background(
                RoundedRectangle(cornerRadius: M.radiusMD, style: .continuous)
                    .fill(configuration.isPressed ? theme.bgPressed : (active ? theme.accentSubtle : (hover ? theme.bgHover : .clear)))
            )
            .contentShape(Rectangle())
            .onHover { hover = $0 && enabled }
    }
}

/// 一组紧挨的工具栏控件
struct PPToolbarGroup<Content: View>: View {
    @ViewBuilder let content: Content
    var body: some View { HStack(spacing: M.xxs) { content } }
}

/// 可点击的文本链接（URL、路径）
struct PPLink: View {
    let title: String
    var mono = false
    var icon: String?
    let action: () -> Void
    @Environment(\.theme) private var theme
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: M.xs) {
                if let icon { PPIcon(icon, size: M.iconSmall) }
                Text(title)
                    .font(mono ? .pp(.monoSmall) : .pp(.small))
                    .underline(hover)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            .foregroundColor(theme.textLink)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

import AppKit
import PaperplaneKit
import SwiftUI

/// 徽标（语气色）
struct PPBadge: View {
    let text: String
    var tone: String? = nil
    var icon: IconSource?
    var mono = false
    @Environment(\.theme) private var theme

    var body: some View {
        let c = theme.tone(tone)
        HStack(spacing: M.xs) {
            if let icon { PPIcon(ref: icon, size: 12) }
            Text(text).font(mono ? .pp(.monoSmall) : .pp(.caption)).lineLimit(1)
        }
        .foregroundColor(tone == nil || tone == "neutral" ? theme.textSecondary : c.fg)
        .padding(.horizontal, M.sm)
        .frame(height: 18)
        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(c.bg))
        .overlay(RoundedRectangle(cornerRadius: M.radiusSM).strokeBorder(c.border.opacity(0.6), lineWidth: 1))
    }
}

/// 数量小圆角块（标签页、指示器）
struct PPCountBadge: View {
    let count: Int
    var tone: String? = nil
    @Environment(\.theme) private var theme

    var body: some View {
        Text("\(count)")
            .font(.pp(.caption).monospacedDigit())
            .foregroundColor(tone == nil ? theme.textSecondary : theme.tone(tone).fg)
            .padding(.horizontal, 5)
            .frame(minWidth: 16, minHeight: 16)
            .background(Capsule().fill(tone == nil ? theme.neutralSubtle : theme.tone(tone).bg))
    }
}

/// 技术徽标（facet）：图标 + 名称
struct FacetBadgeView: View {
    let facet: FacetBadge
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.xs) {
            PPIcon(raw: facet.icon, plugin: facet.plugin, fallback: "box", size: 12)
            Text(facet.name).font(.pp(.caption)).lineLimit(1)
        }
        .foregroundColor(theme.textSecondary)
        .padding(.horizontal, M.sm)
        .frame(height: 20)
        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(theme.neutralSubtle))
    }
}

/// 状态：idle | preparing | starting | running | stopping | failed | exited | partial
enum RunStatusStyle {
    static func color(_ state: String, theme: Theme) -> Color {
        switch state {
        case RunState.running, "partial": return theme.statusRunning
        case RunState.starting, RunState.preparing: return theme.statusStarting
        case RunState.failed: return theme.statusFailed
        case RunState.stopping: return theme.statusStopping
        default: return theme.statusIdle
        }
    }

    static func title(_ state: String) -> String {
        switch state {
        case RunState.running: return tr("status.running")
        case "partial": return tr("status.partial")
        case RunState.starting: return tr("status.starting")
        case RunState.preparing: return tr("status.preparing")
        case RunState.failed: return tr("status.failed")
        case RunState.stopping: return tr("status.stopping")
        case RunState.exited: return tr("status.exited")
        default: return tr("status.idle")
        }
    }

    static func isBusy(_ state: String) -> Bool {
        state == RunState.starting || state == RunState.preparing || state == RunState.stopping
    }
}

/// 状态圆点（启动中会呼吸闪烁）
struct StatusDot: View {
    let state: String
    var size: CGFloat = M.statusDot
    @Environment(\.theme) private var theme
    @State private var pulse = false

    var body: some View {
        Circle()
            .fill(RunStatusStyle.color(state, theme: theme))
            .frame(width: size, height: size)
            .opacity(RunStatusStyle.isBusy(state) ? (pulse ? 0.35 : 1) : 1)
            .onAppear { startPulse() }
            .onChange(of: state) { _ in startPulse() }
            .help(RunStatusStyle.title(state))
    }

    private func startPulse() {
        guard RunStatusStyle.isBusy(state) else { pulse = false; return }
        withAnimation(.easeInOut(duration: 0.7).repeatForever(autoreverses: true)) { pulse = true }
    }
}

/// 转圈（loader-circle 旋转）
struct PPSpinner: View {
    var size: CGFloat = M.icon
    var color: Color?
    @State private var rotating = false

    var body: some View {
        PPIcon("loader-circle", size: size, color: color)
            .rotationEffect(.degrees(rotating ? 360 : 0))
            .onAppear {
                withAnimation(.linear(duration: 0.9).repeatForever(autoreverses: false)) { rotating = true }
            }
    }
}

/// 进度条；value < 0 为不确定
struct PPProgressBar: View {
    let value: Double
    var height: CGFloat = 4
    var tone: String? = nil
    @Environment(\.theme) private var theme
    @State private var phase: CGFloat = 0

    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(theme.neutralSubtle)
                if value < 0 {
                    Capsule().fill(tone == nil ? theme.accent : theme.tone(tone).fg)
                        .frame(width: g.size.width * 0.3)
                        .offset(x: phase * g.size.width * 0.7)
                        .onAppear {
                            withAnimation(.easeInOut(duration: 1).repeatForever(autoreverses: true)) { phase = 1 }
                        }
                } else {
                    Capsule().fill(tone == nil ? theme.accent : theme.tone(tone).fg)
                        .frame(width: g.size.width * CGFloat(min(max(value, 0), 100) / 100))
                }
            }
        }
        .frame(height: height)
        .clipShape(Capsule())
    }
}

/// 分隔线
struct PPDivider: View {
    var vertical = false
    var subtle = false
    @Environment(\.theme) private var theme

    var body: some View {
        Rectangle()
            .fill(subtle ? theme.borderSubtle : theme.border)
            .frame(width: vertical ? 1 : nil, height: vertical ? nil : 1)
    }
}

/// 标题 + 内容的区块
struct PPSection<Content: View, Accessory: View>: View {
    let title: String
    var info: String?
    var collapsible = false
    @State var collapsed = false
    @ViewBuilder let accessory: Accessory
    @ViewBuilder let content: Content
    @Environment(\.theme) private var theme

    init(_ title: String, info: String? = nil, collapsible: Bool = false, collapsed: Bool = false,
         @ViewBuilder accessory: () -> Accessory, @ViewBuilder content: () -> Content) {
        self.title = title
        self.info = info
        self.collapsible = collapsible
        self._collapsed = State(initialValue: collapsed)
        self.accessory = accessory()
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: M.md) {
            HStack(spacing: M.sm) {
                if collapsible {
                    PPIcon(collapsed ? "chevron-right" : "chevron-down", size: M.iconSmall, color: theme.iconSecondary)
                }
                Text(title).font(.pp(.title))
                if let info { PPInfoButton(text: info) }
                Spacer(minLength: M.md)
                accessory
            }
            .contentShape(Rectangle())
            .onTapGesture { if collapsible { withAnimation(.easeOut(duration: 0.12)) { collapsed.toggle() } } }
            if !collapsed { content }
        }
    }
}

extension PPSection where Accessory == EmptyView {
    init(_ title: String, info: String? = nil, collapsible: Bool = false, collapsed: Bool = false,
         @ViewBuilder content: () -> Content) {
        self.init(title, info: info, collapsible: collapsible, collapsed: collapsed, accessory: { EmptyView() }, content: content)
    }
}

/// 卡片容器
struct PPCard<Content: View>: View {
    var padding: CGFloat = M.lg
    var hoverable = false
    var selected = false
    @ViewBuilder let content: Content
    @Environment(\.theme) private var theme
    @State private var hover = false

    var body: some View {
        content
            .padding(padding)
            .background(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous).fill(theme.bgSurface)
                .shadow(color: .black.opacity(hover && hoverable ? (theme.isDark ? 0.35 : 0.08) : 0), radius: 6, y: 2))
            .overlay(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous)
                .strokeBorder(selected ? theme.accent : (hover ? theme.borderStrong : theme.border), lineWidth: 1))
            .onHover { if hoverable { hover = $0 } }
    }
}

/// 空状态：一个图标 + 一句话 + 一个按钮
struct PPEmptyState: View {
    let icon: String
    let title: String
    var actionTitle: String?
    var actionIcon: String?
    var action: (() -> Void)?
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(spacing: M.lg) {
            PPIcon(icon, size: M.iconEmpty, color: theme.iconSecondary)
            Text(title).font(.pp(.body)).foregroundColor(theme.textSecondary).multilineTextAlignment(.center)
            if let actionTitle, let action {
                PPButton(actionTitle, icon: actionIcon, kind: .primary, action: action)
            }
        }
        .padding(M.huge)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// i 图标：点开显示说明文字（界面上不铺解释性小字）
struct PPInfoButton: View {
    let text: String
    @State private var open = false
    @Environment(\.theme) private var theme

    var body: some View {
        Button { open.toggle() } label: {
            PPIcon("info", size: M.iconSmall, color: open ? theme.accent : theme.iconSecondary)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .ppPopover(isPresented: $open) {
            Text(text)
                .font(.pp(.small))
                .foregroundColor(theme.textPrimary)
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: 280, alignment: .leading)
                .padding(M.md)
        }
    }
}

/// 键值列表
struct PPKeyValueList: View {
    struct Row: Identifiable {
        let key: String
        let value: String
        var mono = false
        var tone: String?
        var href: String?
        var id: String { key + "|" + value }
    }

    let rows: [Row]
    var keyWidth: CGFloat = 120
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: M.xs) {
            ForEach(rows) { r in
                HStack(alignment: .firstTextBaseline, spacing: M.md) {
                    Text(r.key).foregroundColor(theme.textSecondary).frame(width: keyWidth, alignment: .leading).lineLimit(1)
                    if let href = r.href {
                        PPLink(title: r.value, mono: r.mono) { Actions.openURL(href) }
                    } else {
                        Text(r.value)
                            .font(r.mono ? .pp(.mono) : .pp(.body))
                            .foregroundColor(r.tone == nil ? theme.textPrimary : theme.tone(r.tone).fg)
                            .textSelection(.enabled)
                            .lineLimit(3)
                    }
                    Spacer(minLength: 0)
                }
            }
        }
    }
}

/// 列表行：悬停底色、选中底色
struct PPRow<Content: View>: View {
    var selected = false
    var height: CGFloat? = M.rowHeight
    var action: (() -> Void)?
    @ViewBuilder let content: Content
    @Environment(\.theme) private var theme
    @State private var hover = false

    var body: some View {
        content
            .padding(.horizontal, M.md)
            .frame(height: height)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: M.radiusMD)
                .fill(selected ? theme.bgSelected : (hover ? theme.bgHover : .clear)))
            .contentShape(Rectangle())
            .onHover { hover = $0 }
            .onTapGesture { action?() }
    }
}

/// 页面标题栏（页内，不是窗口工具栏）
struct PPPageHeader<Accessory: View>: View {
    let title: String
    var subtitle: String?
    @ViewBuilder let accessory: Accessory
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.md) {
            Text(title).font(.pp(.heading))
            if let subtitle { Text(subtitle).foregroundColor(theme.textSecondary) }
            Spacer()
            accessory
        }
        .padding(.horizontal, M.xxl)
        .frame(height: 52)
    }
}

/// 提示条（alert）
struct PPAlert<Actions: View>: View {
    let tone: String
    let title: String
    var message: String?
    @ViewBuilder let actions: Actions
    @Environment(\.theme) private var theme

    var body: some View {
        let c = theme.tone(tone)
        HStack(alignment: .top, spacing: M.md) {
            PPIcon(tone == "danger" ? "circle-alert" : (tone == "warning" ? "triangle-alert" : (tone == "success" ? "circle-check" : "info")),
                   size: M.icon, color: c.fg)
            VStack(alignment: .leading, spacing: M.xxs) {
                Text(title).font(.pp(.bodyStrong))
                if let message, !message.isEmpty {
                    Text(message).font(.pp(.small)).foregroundColor(theme.textSecondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: M.md)
            actions
        }
        .padding(M.md)
        .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(c.bg))
        .overlay(RoundedRectangle(cornerRadius: M.radiusMD).strokeBorder(c.border, lineWidth: 1))
    }
}

extension PPAlert where Actions == EmptyView {
    init(tone: String, title: String, message: String? = nil) {
        self.init(tone: tone, title: title, message: message, actions: { EmptyView() })
    }
}

/// 可调宽度的左右分栏（拖动分隔线）
struct PPSplitView<Left: View, Right: View>: View {
    @Binding var leftWidth: CGFloat
    var minLeft: CGFloat = 180
    var maxLeft: CGFloat = 480
    @ViewBuilder let left: Left
    @ViewBuilder let right: Right
    @Environment(\.theme) private var theme
    @State private var dragStart: CGFloat?

    var body: some View {
        HStack(spacing: 0) {
            left.frame(width: leftWidth)
            Rectangle().fill(theme.border).frame(width: 1)
                .overlay(
                    Color.clear.frame(width: 7).contentShape(Rectangle())
                        .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
                        .gesture(DragGesture(minimumDistance: 1).onChanged { v in
                            let start = dragStart ?? leftWidth
                            if dragStart == nil { dragStart = leftWidth }
                            leftWidth = min(max(start + v.translation.width, minLeft), maxLeft)
                        }.onEnded { _ in dragStart = nil })
                )
            right.frame(maxWidth: .infinity)
        }
    }
}

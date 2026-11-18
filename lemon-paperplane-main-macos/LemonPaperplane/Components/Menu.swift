import AppKit
import PaperplaneKit
import SwiftUI

// 自绘菜单：下拉菜单、「更多」菜单、右键菜单共用，支持子菜单与键盘（上下、回车、左右、Esc）。

struct MenuItem: Identifiable {
    let id = UUID()
    var title: String
    var icon: IconSource?
    var shortcut: String?
    var destructive = false
    var disabled = false
    var checked = false
    var children: [MenuEntry]?
    var action: (() -> Void)?

    init(_ title: String, icon: String? = nil, shortcut: String? = nil, destructive: Bool = false,
         disabled: Bool = false, checked: Bool = false, action: (() -> Void)? = nil) {
        self.title = title
        self.icon = icon.map { .builtin($0) }
        self.shortcut = shortcut
        self.destructive = destructive
        self.disabled = disabled
        self.checked = checked
        self.action = action
    }

    init(_ title: String, iconRef: IconSource?, disabled: Bool = false, action: (() -> Void)? = nil) {
        self.title = title
        self.icon = iconRef
        self.disabled = disabled
        self.action = action
    }

    /// 子菜单
    init(_ title: String, icon: String? = nil, children: [MenuEntry]) {
        self.title = title
        self.icon = icon.map { .builtin($0) }
        self.children = children
        self.disabled = children.isEmpty
    }
}

enum MenuEntry: Identifiable {
    case item(MenuItem)
    case separator(UUID = UUID())
    case header(String, UUID = UUID())

    var id: UUID {
        switch self {
        case .item(let i): return i.id
        case .separator(let u): return u
        case .header(_, let u): return u
        }
    }

    static func item(_ title: String, icon: String? = nil, shortcut: String? = nil, destructive: Bool = false,
                     disabled: Bool = false, checked: Bool = false, action: @escaping () -> Void) -> MenuEntry {
        .item(MenuItem(title, icon: icon, shortcut: shortcut, destructive: destructive, disabled: disabled,
                       checked: checked, action: action))
    }

    static func submenu(_ title: String, icon: String? = nil, _ children: [MenuEntry]) -> MenuEntry {
        .item(MenuItem(title, icon: icon, children: children))
    }

    static var divider: MenuEntry { .separator() }
}

/// 去掉首尾与连续的分隔线
func normalizeMenu(_ entries: [MenuEntry]) -> [MenuEntry] {
    var out: [MenuEntry] = []
    for e in entries {
        if case .separator = e {
            if out.isEmpty { continue }
            if case .separator = out.last! { continue }
        }
        out.append(e)
    }
    while let last = out.last, case .separator = last { out.removeLast() }
    return out
}

@MainActor
final class MenuState: ObservableObject {
    @Published var highlighted: UUID?
    let entries: [MenuEntry]
    weak var panel: PopupPanel?
    var submenuPanel: PopupPanel?
    var submenuOwner: UUID?
    var rowFrames: [UUID: NSRect] = [:] // 屏幕坐标，用于子菜单定位

    init(entries: [MenuEntry]) { self.entries = entries }

    var selectable: [MenuItem] {
        entries.compactMap { if case .item(let i) = $0, !i.disabled { return i } else { return nil } }
    }

    func move(_ delta: Int) {
        let items = selectable
        guard !items.isEmpty else { return }
        let idx = items.firstIndex { $0.id == highlighted } ?? (delta > 0 ? -1 : items.count)
        let next = (idx + delta + items.count) % items.count
        highlighted = items[next].id
    }

    func activate(_ item: MenuItem) {
        guard !item.disabled else { return }
        if item.children != nil {
            openSubmenu(item)
            return
        }
        PopupController.shared.dismissAll()
        if let a = item.action { DispatchQueue.main.async { a() } }
    }

    func openSubmenu(_ item: MenuItem) {
        guard let children = item.children, !children.isEmpty, let panel else { return }
        if submenuOwner == item.id, submenuPanel != nil { return }
        closeSubmenu()
        let rect = rowFrames[item.id] ?? NSRect(x: panel.frame.maxX, y: panel.frame.maxY, width: 0, height: 0)
        submenuOwner = item.id
        submenuPanel = MenuPresenter.show(children, anchorRect: rect, preferRight: true, parent: panel)
    }

    func closeSubmenu() {
        if let p = submenuPanel { PopupController.shared.dismiss(p) }
        submenuPanel = nil
        submenuOwner = nil
    }

    func handleKey(_ e: NSEvent) -> Bool {
        switch e.keyCode {
        case 125: move(1); return true // 下
        case 126: move(-1); return true // 上
        case 36, 76: // 回车
            if let item = selectable.first(where: { $0.id == highlighted }) { activate(item) }
            return true
        case 124: // 右
            if let item = selectable.first(where: { $0.id == highlighted }), item.children != nil { openSubmenu(item) }
            return true
        case 123: // 左：关闭子菜单自身
            if let panel, PopupController.shared.panels.count > 1 { PopupController.shared.dismiss(panel); return true }
            return false
        default: return false
        }
    }
}

enum MenuPresenter {
    @MainActor @discardableResult
    static func show(_ entries: [MenuEntry], anchorRect: NSRect, alignment: PopupAlignment = .leading,
                     preferRight: Bool = false, parent: NSWindow? = nil, onClose: (() -> Void)? = nil) -> PopupPanel? {
        let entries = normalizeMenu(entries)
        guard !entries.isEmpty else { return nil }
        let state = MenuState(entries: entries)
        let panel = PopupController.shared.present(anchorRect: anchorRect, alignment: alignment, preferRight: preferRight,
                                                   parent: parent, onClose: onClose) {
            MenuView(state: state)
        }
        state.panel = panel
        panel.keyHandler = { [weak state] e in state?.handleKey(e) ?? false }
        return panel
    }

    /// 在鼠标位置弹出（右键菜单）
    @MainActor
    static func showAtMouse(_ entries: [MenuEntry], parent: NSWindow?) {
        let p = NSEvent.mouseLocation
        show(entries, anchorRect: NSRect(x: p.x, y: p.y, width: 0, height: 0), parent: parent)
    }
}

private struct MenuView: View {
    @ObservedObject var state: MenuState
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(state.entries) { e in
                switch e {
                case .separator:
                    Rectangle().fill(theme.borderSubtle).frame(height: 1).padding(.vertical, M.xs).padding(.horizontal, M.xs)
                case .header(let t, _):
                    Text(t).font(.pp(.caption)).foregroundColor(theme.textTertiary)
                        .padding(.horizontal, M.md).frame(height: M.rowHeightCompact, alignment: .leading)
                case .item(let item):
                    MenuRow(item: item, state: state)
                }
            }
        }
        .padding(M.xs)
        .frame(minWidth: 180, maxWidth: 360, alignment: .leading)
        .fixedSize(horizontal: true, vertical: false) // 各行等宽（取最宽一行），高亮底色铺满
    }
}

private struct MenuRow: View {
    let item: MenuItem
    @ObservedObject var state: MenuState
    @Environment(\.theme) private var theme
    @State private var anchor = PopupAnchor()

    var body: some View {
        let hl = state.highlighted == item.id && !item.disabled
        let fg: Color = item.disabled ? theme.textDisabled : (item.destructive ? theme.danger : theme.textPrimary)
        HStack(spacing: M.md) {
            ZStack {
                if item.checked {
                    PPIcon("check", size: M.iconSmall, color: hl ? theme.textOnAccent : theme.accent)
                } else if let icon = item.icon {
                    PPIcon(ref: icon, size: M.icon, color: hl ? theme.textOnAccent : (item.destructive ? theme.danger : theme.iconPrimary))
                        .opacity(item.disabled ? 0.45 : 1)
                }
            }
            .frame(width: M.icon, height: M.icon)
            Text(item.title).lineLimit(1).foregroundColor(hl ? theme.textOnAccent : fg)
            Spacer(minLength: M.xl)
            if let s = item.shortcut {
                Text(s).font(.pp(.small)).foregroundColor(hl ? theme.textOnAccent.opacity(0.85) : theme.textTertiary)
            }
            if item.children != nil {
                PPIcon("chevron-right", size: M.iconSmall, color: hl ? theme.textOnAccent : theme.iconSecondary)
            }
        }
        .padding(.horizontal, M.md)
        .frame(height: M.rowHeight)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(hl ? theme.accent : Color.clear))
        .contentShape(Rectangle())
        .background(AnchorReader(anchor: anchor))
        .onHover { inside in
            guard inside else { return }
            state.highlighted = item.id
            if let r = anchor.screenRect { state.rowFrames[item.id] = r }
            if item.children != nil && !item.disabled {
                state.openSubmenu(item)
            } else if state.submenuOwner != item.id {
                state.closeSubmenu()
            }
        }
        .onTapGesture {
            if let r = anchor.screenRect { state.rowFrames[item.id] = r }
            state.activate(item)
        }
    }
}

// MARK: - 菜单按钮与右键菜单

/// 点击弹出菜单的按钮（「更多」、导入菜单等）
struct PPMenuButton<Label: View>: View {
    var alignment: PopupAlignment = .leading
    let entries: () -> [MenuEntry]
    @ViewBuilder let label: () -> Label
    @State private var anchor = PopupAnchor()

    var body: some View {
        Button {
            guard let rect = anchor.screenRect else { return }
            if let p = anchor.panel { PopupController.shared.dismiss(p); return }
            anchor.panel = MenuPresenter.show(entries(), anchorRect: rect, alignment: alignment,
                                              parent: anchor.view?.window, onClose: { [anchor] in anchor.panel = nil })
        } label: {
            label()
        }
        .buttonStyle(.plain)
        .background(AnchorReader(anchor: anchor))
    }
}

/// 外观和 PPButton 一样、点击弹出菜单的按钮（「新建终端」选工程等），标题后带下拉箭头
struct PPButtonMenu: View {
    let title: String
    var icon: String?
    var kind: PPButtonKind = .secondary
    var size: PPButtonSize = .regular
    var alignment: PopupAlignment = .leading
    let entries: () -> [MenuEntry]
    @State private var anchor = PopupAnchor()

    var body: some View {
        Button {
            guard let rect = anchor.screenRect else { return }
            if let p = anchor.panel { PopupController.shared.dismiss(p); return }
            anchor.panel = MenuPresenter.show(entries(), anchorRect: rect, alignment: alignment,
                                              parent: anchor.view?.window, onClose: { [anchor] in anchor.panel = nil })
        } label: {
            HStack(spacing: M.sm) {
                if let icon { PPIcon(icon, size: size == .small ? M.iconSmall : M.icon) }
                Text(title)
                PPIcon("chevron-down", size: M.iconSmall).opacity(0.8)
            }
        }
        .buttonStyle(PPButtonStyle(kind: kind, size: size))
        .background(AnchorReader(anchor: anchor))
    }
}

/// 右键菜单登记处：一个本地事件监视器，按点击位置找最内层（可见面积最小）的登记区域。
@MainActor
final class ContextMenuRegistry {
    static let shared = ContextMenuRegistry()

    private struct Entry {
        weak var anchor: PopupAnchor?
        var entries: () -> [MenuEntry]
    }

    private var items: [ObjectIdentifier: Entry] = [:]
    private var monitor: Any?
    /// 鼠标当前所在的菜单区域（SwiftUI 的 onHover 判定，和界面上的悬停高亮一致）
    private var hovered: Set<ObjectIdentifier> = []

    /// 鼠标进入/离开某个右键菜单区域
    func hover(_ anchor: PopupAnchor, _ inside: Bool) {
        if inside { hovered.insert(ObjectIdentifier(anchor)) } else { hovered.remove(ObjectIdentifier(anchor)) }
    }

    func register(_ anchor: PopupAnchor, entries: @escaping () -> [MenuEntry]) {
        items[ObjectIdentifier(anchor)] = Entry(anchor: anchor, entries: entries)
        installIfNeeded()
    }

    func unregister(_ anchor: PopupAnchor) {
        items.removeValue(forKey: ObjectIdentifier(anchor))
        hovered.remove(ObjectIdentifier(anchor))
    }

    private func installIfNeeded() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.rightMouseDown, .leftMouseDown]) { [weak self] e in
            guard let self else { return e }
            if e.type == .leftMouseDown && !e.modifierFlags.contains(.control) { return e }
            if e.window is PopupPanel { return e }
            return self.handle(e) ? nil : e
        }
    }

    private func handle(_ e: NSEvent) -> Bool {
        items = items.filter { $0.value.anchor?.view != nil }
        hovered = hovered.filter { items[$0] != nil }
        let hits = items.filter { $0.value.anchor?.visibleContains(e.locationInWindow, window: e.window) == true }
        // 只按几何位置找会有并列：懒加载列表里滚出去又复用的行可能残留旧位置，和鼠标下的行一样大，
        // 曾经右键 deploy.sh 却弹出了 tsconfig.app.json 的菜单。优先取鼠标当前所在（SwiftUI 悬停判定）的区域。
        let hoveredHits = hits.filter { hovered.contains($0.key) }
        let pool = hoveredHits.isEmpty ? Array(hits.values) : Array(hoveredHits.values)
        guard let best = pool.min(by: { ($0.anchor?.visibleArea ?? 0) < ($1.anchor?.visibleArea ?? 0) }) else { return false }
        PopupController.shared.dismissAll()
        MenuPresenter.showAtMouse(best.entries(), parent: e.window)
        return true
    }
}

/// 登记右键菜单的占位视图：每次 SwiftUI 更新时刷新闭包，移除时注销
private struct ContextMenuAnchor: NSViewRepresentable {
    let anchor: PopupAnchor
    let entries: () -> [MenuEntry]

    func makeNSView(context: Context) -> NSView {
        let v = PassThroughView()
        anchor.view = v
        ContextMenuRegistry.shared.register(anchor, entries: entries)
        return v
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        anchor.view = nsView
        ContextMenuRegistry.shared.register(anchor, entries: entries)
    }

    func makeCoordinator() -> PopupAnchor { anchor }

    static func dismantleNSView(_ nsView: NSView, coordinator: PopupAnchor) {
        ContextMenuRegistry.shared.unregister(coordinator)
    }
}

/// 右键菜单：占位视图登记区域 + onHover 记录鼠标是否在区域内（右键时以此为准）
private struct ContextMenuModifier: ViewModifier {
    let entries: () -> [MenuEntry]
    @State private var anchor = PopupAnchor()

    func body(content: Content) -> some View {
        content
            .background(ContextMenuAnchor(anchor: anchor, entries: entries))
            .onHover { ContextMenuRegistry.shared.hover(anchor, $0) }
            .onDisappear { ContextMenuRegistry.shared.hover(anchor, false) }
    }
}

extension View {
    /// 右键（或 Control+点击）弹出自绘菜单
    func ppContextMenu(_ entries: @escaping () -> [MenuEntry]) -> some View {
        modifier(ContextMenuModifier(entries: entries))
    }
}

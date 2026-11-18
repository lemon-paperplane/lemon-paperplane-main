import AppKit
import SwiftUI

// 弹出层：自绘的无边框面板（下拉、菜单、弹出框、右键菜单共用），外观三端一致，不用系统 NSPopover/NSMenu。

/// 记录某个 SwiftUI 视图对应的 NSView，用来算它在屏幕上的位置。
final class PopupAnchor {
    weak var view: NSView?
    var panel: PopupPanel?

    var screenRect: NSRect? {
        guard let v = view, let w = v.window else { return nil }
        return w.convertToScreen(v.convert(v.bounds, to: nil))
    }

    /// 在可见区域内（没被滚动裁掉）且包含窗口坐标点
    func visibleContains(_ windowPoint: NSPoint, window: NSWindow?) -> Bool {
        guard let v = view, v.window === window, !v.isHiddenOrHasHiddenAncestor else { return false }
        let r = v.convert(v.visibleRect, to: nil)
        return !r.isEmpty && r.contains(windowPoint)
    }

    var visibleArea: CGFloat {
        guard let v = view else { return .greatestFiniteMagnitude }
        return v.visibleRect.width * v.visibleRect.height
    }
}

struct AnchorReader: NSViewRepresentable {
    let anchor: PopupAnchor

    func makeNSView(context: Context) -> NSView {
        let v = PassThroughView()
        anchor.view = v
        return v
    }

    func updateNSView(_ nsView: NSView, context: Context) { anchor.view = nsView }
}

/// 不接收任何鼠标事件的占位视图
final class PassThroughView: NSView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

enum PopupAlignment { case leading, trailing, center }

final class PopupPanel: NSPanel {
    var wantsKey = false
    var onClose: (() -> Void)?
    /// 菜单键盘导航：返回 true 表示已处理
    var keyHandler: ((NSEvent) -> Bool)?
    var anchorRect: NSRect = .zero
    var alignment: PopupAlignment = .leading
    var preferRight = false // 子菜单：贴在锚点右侧
    fileprivate var sized = false

    override var canBecomeKey: Bool { wantsKey }
    override var canBecomeMain: Bool { false }
}

/// 管理全部弹出面板：点外面、Esc、App 失去焦点时关闭。
@MainActor
final class PopupController {
    static let shared = PopupController()

    private(set) var panels: [PopupPanel] = []
    private var monitors: [Any] = []
    private var observers: [NSObjectProtocol] = []

    var isShowingAny: Bool { !panels.isEmpty }

    @discardableResult
    func present<V: View>(anchorRect: NSRect, alignment: PopupAlignment = .leading, preferRight: Bool = false,
                          wantsKey: Bool = false, parent: NSWindow? = nil,
                          onClose: (() -> Void)? = nil, @ViewBuilder content: () -> V) -> PopupPanel {
        let parent = parent ?? NSApp.keyWindow
        let panel = PopupPanel(contentRect: NSRect(x: anchorRect.minX, y: anchorRect.minY - 10, width: 10, height: 10),
                               styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.isFloatingPanel = true
        panel.level = .popUpMenu
        panel.hasShadow = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .utilityWindow
        panel.collectionBehavior = [.transient, .ignoresCycle, .fullScreenAuxiliary]
        panel.wantsKey = wantsKey
        panel.onClose = onClose
        panel.anchorRect = anchorRect
        panel.alignment = alignment
        panel.preferRight = preferRight
        panel.alphaValue = 0

        let root = PopupRoot(content: content(), onSize: { [weak panel] size in
            guard let panel else { return }
            PopupController.shared.layout(panel, size: size)
        })
        let host = NSHostingView(rootView: Themed { root })
        host.frame = NSRect(x: 0, y: 0, width: 800, height: 800)
        panel.contentView = host

        panels.append(panel)
        installMonitors()
        if let parent, parent !== panel { parent.addChildWindow(panel, ordered: .above) }
        if wantsKey { panel.makeKeyAndOrderFront(nil) } else { panel.orderFront(nil) }
        return panel
    }

    /// 内容尺寸确定后定位：优先在锚点下方，空间不够翻到上方，并限制在屏幕内
    fileprivate func layout(_ panel: PopupPanel, size: CGSize) {
        guard size.width > 0, size.height > 0 else { return }
        let a = panel.anchorRect
        let screen = (NSScreen.screens.first { $0.frame.intersects(a) } ?? NSScreen.main)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let gap: CGFloat = 4
        var x: CGFloat
        var y: CGFloat
        if panel.preferRight {
            x = a.maxX - 2
            if x + size.width > screen.maxX { x = a.minX - size.width + 2 }
            y = a.maxY - size.height + 4 // 子菜单首项与父项对齐
        } else {
            switch panel.alignment {
            case .leading: x = a.minX
            case .trailing: x = a.maxX - size.width
            case .center: x = a.midX - size.width / 2
            }
            y = a.minY - gap - size.height
            if y < screen.minY { y = a.maxY + gap }
        }
        x = min(max(x, screen.minX + 4), screen.maxX - size.width - 4)
        y = min(max(y, screen.minY + 4), screen.maxY - size.height - 4)
        panel.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
        panel.invalidateShadow()
        if !panel.sized {
            panel.sized = true
            panel.alphaValue = 1
        }
    }

    func dismiss(_ panel: PopupPanel) {
        guard let idx = panels.firstIndex(where: { $0 === panel }) else { return }
        // 关闭它和它之上的（子菜单）
        let closing = Array(panels[idx...])
        panels.removeSubrange(idx...)
        for p in closing.reversed() { close(p) }
        if panels.isEmpty { removeMonitors() }
    }

    func dismissAll() {
        let all = panels
        panels.removeAll()
        for p in all.reversed() { close(p) }
        removeMonitors()
    }

    /// 关闭某个面板之上的全部面板（父菜单里移动时关子菜单）
    func dismissAbove(_ panel: PopupPanel) {
        guard let idx = panels.firstIndex(where: { $0 === panel }), idx + 1 < panels.count else { return }
        dismiss(panels[idx + 1])
    }

    private func close(_ p: PopupPanel) {
        p.parent?.removeChildWindow(p)
        p.orderOut(nil)
        let cb = p.onClose
        p.onClose = nil
        p.contentView = nil
        cb?()
    }

    private func installMonitors() {
        guard monitors.isEmpty else { return }
        if let m = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown], handler: { [weak self] e in
            guard let self else { return e }
            if let w = e.window as? PopupPanel, let idx = self.panels.firstIndex(where: { $0 === w }) {
                if idx + 1 < self.panels.count { self.dismiss(self.panels[idx + 1]) }
                return e
            }
            self.dismissAll()
            return e
        }) { monitors.append(m) }
        if let m = NSEvent.addLocalMonitorForEvents(matching: [.keyDown], handler: { [weak self] e in
            guard let self, let top = self.panels.last else { return e }
            if let h = top.keyHandler, h(e) { return nil }
            if e.keyCode == 53 { // Esc
                self.dismiss(top)
                return nil
            }
            return e
        }) { monitors.append(m) }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown], handler: { [weak self] _ in
            Task { @MainActor in self?.dismissAll() }
        }) { monitors.append(m) }
        let n = NotificationCenter.default.addObserver(forName: NSApplication.didResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.dismissAll() }
        }
        observers.append(n)
    }

    private func removeMonitors() {
        for m in monitors { NSEvent.removeMonitor(m) }
        monitors.removeAll()
        for o in observers { NotificationCenter.default.removeObserver(o) }
        observers.removeAll()
    }
}

/// 弹出层内容外壳：背景、描边、圆角，并把尺寸报告给面板。
private struct PopupRoot<Content: View>: View {
    let content: Content
    let onSize: (CGSize) -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        content
            .fixedSize()
            .background(theme.bgElevated)
            .clipShape(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous).strokeBorder(theme.border, lineWidth: 1))
            .background(GeometryReader { g in
                Color.clear.preference(key: PopupSizeKey.self, value: g.size)
            })
            .onPreferenceChange(PopupSizeKey.self) { onSize($0) }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

private struct PopupSizeKey: PreferenceKey {
    static var defaultValue: CGSize = .zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) {
        let n = nextValue()
        if n != .zero { value = n }
    }
}

// MARK: - SwiftUI 接口

private struct PopoverModifier<P: View>: ViewModifier {
    @Binding var isPresented: Bool
    let alignment: PopupAlignment
    let wantsKey: Bool
    let popup: () -> P
    @State private var anchor = PopupAnchor()

    func body(content: Content) -> some View {
        content
            .background(AnchorReader(anchor: anchor))
            .onChange(of: isPresented) { show in
                if show { present() } else { close() }
            }
            .onDisappear { close() }
    }

    private func present() {
        guard anchor.panel == nil, let rect = anchor.screenRect else { return }
        let binding = $isPresented
        anchor.panel = PopupController.shared.present(anchorRect: rect, alignment: alignment, wantsKey: wantsKey,
                                                      parent: anchor.view?.window, onClose: { [anchor] in
            anchor.panel = nil
            if binding.wrappedValue { binding.wrappedValue = false }
        }, content: popup)
    }

    private func close() {
        if let p = anchor.panel {
            anchor.panel = nil
            PopupController.shared.dismiss(p)
        }
    }
}

extension View {
    /// 锚定在本视图下方的弹出框（点外面 / Esc 关闭）
    func ppPopover<P: View>(isPresented: Binding<Bool>, alignment: PopupAlignment = .leading, wantsKey: Bool = false,
                            @ViewBuilder content: @escaping () -> P) -> some View {
        modifier(PopoverModifier(isPresented: isPresented, alignment: alignment, wantsKey: wantsKey, popup: content))
    }
}

/// 弹出框里的标准内容外边距与最大宽度
struct PopoverContent<Content: View>: View {
    var width: CGFloat?
    let content: Content

    init(width: CGFloat? = nil, @ViewBuilder content: () -> Content) {
        self.width = width
        self.content = content()
    }

    var body: some View {
        content
            .padding(M.md)
            .frame(width: width)
            .frame(maxWidth: M.popoverMaxWidth)
    }
}

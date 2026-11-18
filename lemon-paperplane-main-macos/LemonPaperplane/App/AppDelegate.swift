import AppKit
import PaperplaneKit
import SwiftUI

/// 主窗口用 AppKit 管理：自绘标题栏区域（40px 工具栏，红绿灯垂直居中）；
/// 关闭窗口只是隐藏界面，App 变成仅菜单栏（Dock 图标消失），服务继续在 daemon 里跑。
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    static weak var shared: AppDelegate?

    private var window: NSWindow?

    override init() {
        super.init()
        AppDelegate.shared = self
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        Task { @MainActor in
            AppModel.shared.start()
            self.showMainWindow()
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// 有未保存的编辑窗口时不退出，让这些窗口各自提示（网页确认后会关闭窗口）
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        EditorWindows.shared.prepareForQuit() ? .terminateNow : .terminateCancel
    }

    /// 有主窗口或编辑窗口时显示 Dock 图标与菜单栏；全部关掉后变成仅菜单栏图标
    @MainActor
    func updateActivationPolicy() {
        let needRegular = window != nil || !EditorWindows.shared.isEmpty
        let target: NSApplication.ActivationPolicy = needRegular ? .regular : .accessory
        if NSApp.activationPolicy() != target { NSApp.setActivationPolicy(target) }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        Task { @MainActor in self.showMainWindow() }
        return true
    }

    @MainActor
    func showMainWindow() {
        NSApp.setActivationPolicy(.regular)
        if window == nil { window = makeWindow() }
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        if let w = window { TrafficLights.layout(w) }
    }

    var isMainWindowVisible: Bool { window?.isVisible ?? false }

    @MainActor
    private func makeWindow() -> NSWindow {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1180, height: 760),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                         backing: .buffered, defer: false)
        w.title = tr("app.name")
        w.titlebarAppearsTransparent = true
        w.titleVisibility = .hidden
        w.isReleasedWhenClosed = false
        w.minSize = NSSize(width: 760, height: 480)
        w.delegate = self
        w.tabbingMode = .disallowed
        w.contentView = NSHostingView(rootView: Themed { MainView() })
        if !w.setFrameUsingName("PaperplaneMainWindow") { w.center() }
        w.setFrameAutosaveName("PaperplaneMainWindow")
        TrafficLights.layout(w)
        return w
    }

    // MARK: NSWindowDelegate

    func windowWillClose(_ notification: Notification) {
        guard (notification.object as? NSWindow) === window else { return }
        Task { @MainActor in
            PopupController.shared.dismissAll()
            // 释放窗口与视图树（省内存），状态都在 store 里，下次打开重建
            self.window?.contentView = nil
            self.window = nil
            self.updateActivationPolicy()
        }
    }

    func windowDidResize(_ notification: Notification) { relayout(notification) }
    func windowDidEndLiveResize(_ notification: Notification) { relayout(notification) }
    func windowDidBecomeKey(_ notification: Notification) { relayout(notification) }
    func windowDidResignKey(_ notification: Notification) { relayout(notification) }

    func windowDidEnterFullScreen(_ notification: Notification) {
        Task { @MainActor in AppModel.shared.nav.isFullScreen = true }
    }

    func windowDidExitFullScreen(_ notification: Notification) {
        Task { @MainActor in AppModel.shared.nav.isFullScreen = false }
        relayout(notification)
    }

    private func relayout(_ n: Notification) {
        guard let w = n.object as? NSWindow, w === window else { return }
        TrafficLights.layout(w)
        DispatchQueue.main.async { TrafficLights.layout(w) }
    }
}

/// 把红绿灯放到 40px 工具栏的垂直中线上
enum TrafficLights {
    static let leftInset: CGFloat = 14
    static let spacing: CGFloat = 20

    static func layout(_ window: NSWindow) {
        guard !window.styleMask.contains(.fullScreen),
              let close = window.standardWindowButton(.closeButton),
              let titlebar = close.superview,
              let container = titlebar.superview else { return }
        let h = M.toolbarHeight
        var cf = container.frame
        if cf.height != h {
            cf.size.height = h
            cf.origin.y = window.frame.height - h
            container.frame = cf
        }
        var tf = titlebar.frame
        if tf.height != h || tf.origin.y != 0 {
            tf.size.height = h
            tf.origin.y = 0
            titlebar.frame = tf
        }
        for (i, kind) in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton].enumerated() {
            guard let b = window.standardWindowButton(kind) else { continue }
            let origin = NSPoint(x: leftInset + CGFloat(i) * spacing, y: (h - b.frame.height) / 2)
            if b.frame.origin != origin { b.setFrameOrigin(origin) }
        }
    }

    /// 工具栏左侧要让出的宽度
    static var reservedWidth: CGFloat { leftInset + spacing * 2 + 14 + 16 }
}

/// 工具栏空白处拖动窗口、双击缩放
struct WindowDragArea: NSViewRepresentable {
    final class DragView: NSView {
        override var mouseDownCanMoveWindow: Bool { true }
        override func mouseDown(with event: NSEvent) {
            if event.clickCount == 2 {
                let action = UserDefaults.standard.string(forKey: "AppleActionOnDoubleClick") ?? "Maximize"
                if action == "Minimize" { window?.miniaturize(nil) } else if action != "None" { window?.zoom(nil) }
                return
            }
            window?.performDrag(with: event)
        }
    }

    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}
}

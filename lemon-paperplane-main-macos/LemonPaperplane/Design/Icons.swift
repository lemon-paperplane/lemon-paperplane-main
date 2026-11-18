import AppKit
import PaperplaneKit
import SwiftUI

/// SVG 图标缓存：内置图标同步从包里读，插件图标经 API 异步取（按插件 id + 版本缓存）。
@MainActor
final class IconStore: ObservableObject {
    static let shared = IconStore()

    /// 插件图标加载完成时递增，让显示中的图标刷新
    @Published private(set) var revision = 0

    private var docs: [String: SVGDocument] = [:]
    private var failed: Set<String> = []
    private var loading: Set<String> = []
    private var images: [String: NSImage] = [:]
    /// 插件 id → 版本（缓存键的一部分）
    var pluginVersions: [String: String] = [:]

    func document(_ ref: IconSource) -> SVGDocument? {
        let key = cacheKey(ref)
        if let d = docs[key] { return d }
        if failed.contains(key) { return nil }
        switch ref {
        case .builtin(let name):
            let url = SharedResources.icons.appendingPathComponent("\(name).svg")
            if let data = try? Data(contentsOf: url), let d = try? SVGParser.parse(data) {
                docs[key] = d
                return d
            }
            failed.insert(key)
            return nil
        case .plugin(let id, let path):
            if !loading.contains(key) {
                loading.insert(key)
                Task { await self.loadPlugin(id: id, path: path, key: key) }
            }
            return nil
        }
    }

    private func loadPlugin(id: String, path: String, key: String) async {
        defer { loading.remove(key) }
        guard let api = AppModel.shared.api else { return }
        do {
            let data = try await api.pluginFile(pluginId: id, path: path)
            docs[key] = try SVGParser.parse(data)
            revision += 1
        } catch {
            failed.insert(key)
        }
    }

    /// 插件变化（安装、更新、禁用）后清掉插件图标缓存
    func clearPluginIcons() {
        docs = docs.filter { $0.key.hasPrefix("builtin:") }
        images = images.filter { $0.key.hasPrefix("builtin:") }
        failed = failed.filter { $0.hasPrefix("builtin:") }
        revision += 1
    }

    private func cacheKey(_ ref: IconSource) -> String {
        switch ref {
        case .builtin: return ref.key
        case .plugin(let id, _): return ref.key + "@" + (pluginVersions[id] ?? "")
        }
    }

    /// 绘制好的 NSImage。单色图标做成模板图（随前景色着色），彩色图标保留原色。
    func image(_ ref: IconSource, size: CGFloat) -> (NSImage, template: Bool)? {
        guard let doc = document(ref) else { return nil }
        let template = doc.isMonochrome
        let key = "\(cacheKey(ref))#\(size)"
        if let img = images[key] { return (img, template) }
        let scale = DesignSystem.shared.strokeScale(forIconSize: size)
        let img = NSImage(size: NSSize(width: size, height: size), flipped: true) { rect in
            guard let ctx = NSGraphicsContext.current?.cgContext else { return false }
            SVGRenderer.draw(doc, in: ctx, rect: rect, currentColor: NSColor.black.cgColor,
                             strokeScale: scale, flipped: true)
            return true
        }
        img.isTemplate = template
        images[key] = img
        return (img, template)
    }

    /// 菜单栏模板图标（只生成一次）
    lazy var menuBarIcon: NSImage = menuBarImage()

    private func menuBarImage() -> NSImage {
        let url = SharedResources.branding.appendingPathComponent("menubar.svg")
        guard let data = try? Data(contentsOf: url), let doc = try? SVGParser.parse(data) else {
            return NSImage(size: NSSize(width: 18, height: 18))
        }
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { rect in
            guard let ctx = NSGraphicsContext.current?.cgContext else { return false }
            SVGRenderer.draw(doc, in: ctx, rect: rect, currentColor: NSColor.black.cgColor, strokeScale: 1, flipped: true)
            return true
        }
        img.isTemplate = true
        return img
    }
}

/// SVG 图标视图。所有界面图标都走它（不用 SF Symbols、不用 emoji）。
struct PPIcon: View {
    let ref: IconSource?
    var size: CGFloat = M.icon
    var color: Color?

    @ObservedObject private var store = IconStore.shared

    init(_ name: String, size: CGFloat = M.icon, color: Color? = nil) {
        ref = .builtin(name)
        self.size = size
        self.color = color
    }

    init(ref: IconSource?, size: CGFloat = M.icon, color: Color? = nil) {
        self.ref = ref
        self.size = size
        self.color = color
    }

    /// 图标引用字符串（builtin:/plugin:/插件相对路径）
    init(raw: String?, plugin: String? = nil, fallback: String? = nil, size: CGFloat = M.icon, color: Color? = nil) {
        ref = IconSource.parse(raw, pluginId: plugin) ?? fallback.map { .builtin($0) }
        self.size = size
        self.color = color
    }

    var body: some View {
        Group {
            if let ref, let r = store.image(ref, size: size) {
                let img = r.0
                if r.template {
                    if let color {
                        Image(nsImage: img).renderingMode(.template).foregroundColor(color)
                    } else {
                        Image(nsImage: img).renderingMode(.template)
                    }
                } else {
                    Image(nsImage: img)
                }
            } else {
                Color.clear
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

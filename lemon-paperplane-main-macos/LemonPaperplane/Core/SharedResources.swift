import Foundation

/// 三端共用资源（构建时由 Run Script 拷到 Contents/Resources/shared）。
enum SharedResources {
    static let root: URL = {
        if let r = Bundle.main.resourceURL?.appendingPathComponent("shared"),
           FileManager.default.fileExists(atPath: r.appendingPathComponent("design-tokens.json").path) {
            return r
        }
        // 兜底：直接用源码仓库里的 shared（只在开发时可能用到）
        return URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("shared")
    }()

    static var icons: URL { root.appendingPathComponent("icons") }
    static var i18n: URL { root.appendingPathComponent("i18n") }
    static var branding: URL { root.appendingPathComponent("branding") }
    static var tokens: URL { root.appendingPathComponent("design-tokens.json") }
}

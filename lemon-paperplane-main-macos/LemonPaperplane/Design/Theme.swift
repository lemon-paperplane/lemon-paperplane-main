import AppKit
import PaperplaneKit
import SwiftUI

/// 一套主题（亮或暗）的颜色。颜色名见 ColorTokens.swift，取值来自 shared/design-tokens.json。
struct Theme {
    let isDark: Bool
    private let colors: [ColorToken: Color]
    private let nsColors: [ColorToken: NSColor]
    /// 16 个 ANSI 基础色
    let ansi: [NSColor]

    init(isDark: Bool, hex: [String: String], ansiHex: [String]) {
        self.isDark = isDark
        var c: [ColorToken: Color] = [:]
        var n: [ColorToken: NSColor] = [:]
        for t in ColorToken.allCases {
            let ns = NSColor(hex: hex[t.rawValue] ?? "#FF00FF")
            n[t] = ns
            c[t] = Color(nsColor: ns)
        }
        colors = c
        nsColors = n
        ansi = ansiHex.map { NSColor(hex: $0) }
    }

    subscript(_ t: ColorToken) -> Color { colors[t] ?? .pink }
    func ns(_ t: ColorToken) -> NSColor { nsColors[t] ?? .systemPink }

    /// 语气色（插件界面与提示条的 tone）：前景、浅底、描边
    func tone(_ tone: String?) -> (fg: Color, bg: Color, border: Color) {
        switch tone {
        case "success": return (success, successSubtle, successBorder)
        case "warning": return (warning, warningSubtle, warningBorder)
        case "danger", "error": return (danger, dangerSubtle, dangerBorder)
        case "info": return (info, infoSubtle, infoBorder)
        default: return (textSecondary, neutralSubtle, neutralBorder)
        }
    }

    func severity(_ s: String?) -> (fg: Color, bg: Color, border: Color) {
        switch s {
        case Severity.error: return tone("danger")
        case Severity.warning: return tone("warning")
        default: return tone("info")
        }
    }

    /// 提交图车道颜色
    func graphLane(_ index: Int) -> Color {
        let all: [Color] = [graphLane0, graphLane1, graphLane2, graphLane3, graphLane4, graphLane5, graphLane6, graphLane7]
        return all[CommitGraphGeometry.paletteIndex(index, paletteSize: all.count)]
    }
}

/// 全局设计系统：加载一次 design-tokens.json。
final class DesignSystem {
    static let shared = DesignSystem()

    let tokens: DesignTokens?
    let light: Theme
    let dark: Theme

    private init() {
        let t = try? DesignTokens.load(SharedResources.tokens)
        tokens = t
        light = Theme(isDark: false, hex: t?.color["light"] ?? [:], ansiHex: t?.ansi["light"] ?? [])
        dark = Theme(isDark: true, hex: t?.color["dark"] ?? [:], ansiHex: t?.ansi["dark"] ?? [])
    }

    func theme(dark isDark: Bool) -> Theme { isDark ? dark : light }

    func size(_ key: String, _ fallback: CGFloat) -> CGFloat { CGFloat(tokens?.size[key] ?? Double(fallback)) }
    func spacing(_ key: String, _ fallback: CGFloat) -> CGFloat { CGFloat(tokens?.spacing[key] ?? Double(fallback)) }
    func radius(_ key: String, _ fallback: CGFloat) -> CGFloat { CGFloat(tokens?.radius[key] ?? Double(fallback)) }

    func strokeScale(forIconSize size: CGFloat) -> CGFloat {
        CGFloat(tokens?.icon.strokeScale(forSize: Double(size)) ?? (size <= 16 ? 0.75 : 1))
    }
}

/// 尺寸与间距（来自设计变量，名字与 design-tokens.json 一致）。
enum M {
    private static let ds = DesignSystem.shared
    static let toolbarHeight = ds.size("toolbarHeight", 40)
    static let tabBarHeight = ds.size("tabBarHeight", 36)
    static let rowHeight = ds.size("rowHeight", 28)
    static let rowHeightCompact = ds.size("rowHeightCompact", 24)
    static let controlHeight = ds.size("controlHeight", 28)
    static let controlHeightSmall = ds.size("controlHeightSmall", 24)
    static let iconButton = ds.size("iconButton", 28)
    static let iconButtonSmall = ds.size("iconButtonSmall", 22)
    static let sidebarWidth = ds.size("sidebarWidth", 232)
    static let sidebarMinWidth = ds.size("sidebarMinWidth", 180)
    static let sidebarMaxWidth = ds.size("sidebarMaxWidth", 360)
    static let cardMinWidth = ds.size("cardMinWidth", 300)
    static let cardMaxWidth = ds.size("cardMaxWidth", 440)
    static let formLabelWidth = ds.size("formLabelWidth", 160)
    static let dialogWidth = ds.size("dialogWidth", 480)
    static let dialogWidthLarge = ds.size("dialogWidthLarge", 640)
    static let popoverMaxWidth = ds.size("popoverMaxWidth", 420)
    static let statusDot = ds.size("statusDot", 8)
    static let consoleMaxLines = Int(ds.size("consoleMaxLines", 5000))

    static let xxs = ds.spacing("xxs", 2)
    static let xs = ds.spacing("xs", 4)
    static let sm = ds.spacing("sm", 6)
    static let md = ds.spacing("md", 8)
    static let lg = ds.spacing("lg", 12)
    static let xl = ds.spacing("xl", 16)
    static let xxl = ds.spacing("xxl", 20)
    static let xxxl = ds.spacing("xxxl", 24)
    static let huge = ds.spacing("huge", 32)

    static let radiusXS = ds.radius("xs", 3)
    static let radiusSM = ds.radius("sm", 4)
    static let radiusMD = ds.radius("md", 6)
    static let radiusLG = ds.radius("lg", 8)
    static let radiusXL = ds.radius("xl", 12)

    static let icon: CGFloat = CGFloat(ds.tokens?.icon.size ?? 16)
    static let iconSmall: CGFloat = CGFloat(ds.tokens?.icon.sizeSmall ?? 14)
    static let iconLarge: CGFloat = CGFloat(ds.tokens?.icon.sizeLarge ?? 20)
    static let iconEmpty: CGFloat = CGFloat(ds.tokens?.icon.sizeEmpty ?? 32)
}

/// 字体层级（design-tokens.json 的 typography）。
enum PPText: String {
    case caption, small, body, bodyStrong, label, title, heading, display, stat, mono, monoSmall

    var spec: DesignTokens.TextStyle {
        DesignSystem.shared.tokens?.typography[rawValue]
            ?? DesignTokens.TextStyle(size: 13, weight: 400, lineHeight: 18)
    }

    var isMono: Bool { self == .mono || self == .monoSmall }

    var font: Font {
        let s = spec
        if isMono { return MonoFont.swiftUI(size: s.size) }
        return .system(size: s.size, weight: Self.weight(s.weight))
    }

    var nsFont: NSFont {
        let s = spec
        if isMono { return MonoFont.ns(size: s.size) }
        return .systemFont(ofSize: s.size, weight: Self.nsWeight(s.weight))
    }

    static func weight(_ w: Int) -> Font.Weight {
        switch w {
        case ..<350: return .light
        case ..<450: return .regular
        case ..<550: return .medium
        case ..<650: return .semibold
        default: return .bold
        }
    }

    static func nsWeight(_ w: Int) -> NSFont.Weight {
        switch w {
        case ..<350: return .light
        case ..<450: return .regular
        case ..<550: return .medium
        case ..<650: return .semibold
        default: return .bold
        }
    }
}

/// 等宽字体：优先 JetBrains Mono（装了的话），否则系统等宽字体。
enum MonoFont {
    static let preferredName: String? = {
        for n in ["JetBrainsMono-Regular", "JetBrains Mono"] where NSFont(name: n, size: 12) != nil { return n }
        return nil
    }()

    static func ns(size: CGFloat, bold: Bool = false) -> NSFont {
        if let n = preferredName, let f = NSFont(name: n, size: size) {
            return bold ? NSFontManager.shared.convert(f, toHaveTrait: .boldFontMask) : f
        }
        return .monospacedSystemFont(ofSize: size, weight: bold ? .bold : .regular)
    }

    static func swiftUI(size: CGFloat) -> Font {
        if let n = preferredName { return .custom(n, size: size) }
        return .system(size: size, design: .monospaced)
    }
}

extension Font {
    static func pp(_ s: PPText) -> Font { s.font }
}

// MARK: - 环境

private struct ThemeKey: EnvironmentKey {
    static let defaultValue: Theme = DesignSystem.shared.light
}

extension EnvironmentValues {
    var theme: Theme {
        get { self[ThemeKey.self] }
        set { self[ThemeKey.self] = newValue }
    }
}

/// 所有顶层视图（主窗口、菜单栏窗口、弹出框）的根：按系统/设置的明暗注入主题、默认字体与前景色。
struct Themed<Content: View>: View {
    @Environment(\.colorScheme) private var scheme
    @ObservedObject private var l10n = L10n.shared
    let content: Content

    init(@ViewBuilder content: () -> Content) { self.content = content() }

    var body: some View {
        let t = DesignSystem.shared.theme(dark: scheme == .dark)
        content
            .environment(\.theme, t)
            .font(.pp(.body))
            .foregroundColor(t.textPrimary)
            .tint(t.accent)
            .id(l10n.locale) // 切换语言即时生效：整棵树按新文案重建（状态都在 store 里）
    }
}

// MARK: - 颜色工具

extension NSColor {
    convenience init(hex: String) {
        let c = SVGParser.parseColor(hex) ?? (1, 0, 1, 1)
        self.init(srgbRed: c.0, green: c.1, blue: c.2, alpha: c.3)
    }
}

/// 主题外观：system / light / dark（设置里切换，NSApp.appearance 驱动全部窗口的 colorScheme）。
enum Appearance {
    static func apply(_ setting: String?) {
        switch setting {
        case "light": NSApp.appearance = NSAppearance(named: .aqua)
        case "dark": NSApp.appearance = NSAppearance(named: .darkAqua)
        default: NSApp.appearance = nil
        }
    }
}

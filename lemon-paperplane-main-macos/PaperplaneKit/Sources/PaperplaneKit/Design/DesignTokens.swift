import Foundation

/// shared/design-tokens.json 的结构（三端共用的设计变量）。
public struct DesignTokens: Codable, Hashable {
    public struct TextStyle: Codable, Hashable {
        public var size: Double
        public var weight: Int
        public var lineHeight: Double
        public init(size: Double, weight: Int, lineHeight: Double) { self.size = size; self.weight = weight; self.lineHeight = lineHeight }
    }

    public struct StrokeStep: Codable, Hashable {
        public var maxSize: Double
        public var scale: Double
    }

    public struct IconTokens: Codable, Hashable {
        public var size: Double
        public var sizeSmall: Double
        public var sizeLarge: Double
        public var sizeEmpty: Double
        public var strokeScale: [StrokeStep]

        /// 按显示尺寸取线宽系数
        public func strokeScale(forSize size: Double) -> Double {
            for s in strokeScale.sorted(by: { $0.maxSize < $1.maxSize }) where size <= s.maxSize { return s.scale }
            return 1
        }
    }

    public struct Shadow: Codable, Hashable {
        public var x: Double
        public var y: Double
        public var blur: Double
        public var opacity: Double
    }

    public var version: Int
    public var typography: [String: TextStyle]
    public var spacing: [String: Double]
    public var radius: [String: Double]
    public var size: [String: Double]
    public var icon: IconTokens
    public var shadow: [String: Shadow]
    public var motion: [String: Double]
    /// theme（light/dark）→ 颜色名 → #RRGGBB(AA)
    public var color: [String: [String: String]]
    /// theme → 16 个 ANSI 颜色
    public var ansi: [String: [String]]

    public static func load(_ url: URL) throws -> DesignTokens {
        try JSONDecoder().decode(DesignTokens.self, from: Data(contentsOf: url))
    }

    /// 界面代码要求每个主题都提供的颜色名
    public static let requiredColors: [String] = [
        "bgChrome", "bgContent", "bgSurface", "bgSubtle", "bgElevated", "bgInput", "bgInputDisabled", "bgHover",
        "bgPressed", "bgSelected", "bgSelectedInactive", "bgCode", "border", "borderSubtle", "borderStrong",
        "borderFocus", "focusRing", "textPrimary", "textSecondary", "textTertiary", "textDisabled", "textOnAccent",
        "textLink", "iconPrimary", "iconSecondary", "accent", "accentHover", "accentPressed", "accentSubtle",
        "accentSubtleBorder", "success", "successSubtle", "successBorder", "warning", "warningSubtle",
        "warningBorder", "danger", "dangerHover", "dangerSubtle", "dangerBorder", "info", "infoSubtle", "infoBorder",
        "neutralSubtle", "neutralBorder", "statusIdle", "statusStarting", "statusRunning", "statusFailed",
        "statusStopping", "buttonSecondaryBg", "buttonSecondaryHover", "buttonSecondaryBorder", "switchOff", "scrim",
        "shadow", "consoleBg", "consoleText", "consoleSystem", "consoleStderr", "consoleSelection", "searchMatch",
        "searchMatchCurrent", "scrollbar", "graphLane0", "graphLane1", "graphLane2", "graphLane3", "graphLane4",
        "graphLane5", "graphLane6", "graphLane7", "diffAddedBg", "diffAddedText", "diffRemovedBg",
        "diffRemovedText", "diffHunkBg", "brandLemon",
    ]

    /// 校验：返回缺失项（主题.颜色名）
    public func missingColors() -> [String] {
        var out: [String] = []
        for theme in ["light", "dark"] {
            let m = color[theme] ?? [:]
            for k in Self.requiredColors where m[k] == nil { out.append("\(theme).\(k)") }
            if (ansi[theme] ?? []).count != 16 { out.append("ansi.\(theme)") }
        }
        return out
    }
}

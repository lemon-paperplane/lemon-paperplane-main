import Foundation

/// 终端颜色：默认 / 调色板序号（0–255）/ 真彩。
public enum ANSIColor: Hashable, Sendable {
    case `default`
    case palette(UInt8)
    case rgb(UInt8, UInt8, UInt8)
}

public struct ANSIStyle: Hashable, Sendable {
    public var fg: ANSIColor = .default
    public var bg: ANSIColor = .default
    public var bold = false
    public var dim = false
    public var italic = false
    public var underline = false
    public var inverse = false
    public var strikethrough = false

    public init() {}
    public static let plain = ANSIStyle()
    public var isPlain: Bool { self == .plain }
}

public struct ANSISpan: Hashable, Sendable {
    public var text: String
    public var style: ANSIStyle
    public init(text: String, style: ANSIStyle) { self.text = text; self.style = style }
}

/// ANSI 转义解析：支持 SGR（16/256/真彩、粗体、暗淡、斜体、下划线、反显、删除线），
/// 丢弃光标控制等其他 CSI、OSC（含超链接）以及单字符 ESC 序列。
/// 样式状态跨行保留（与终端一致），每个控制台各用一个解析器实例。
public struct ANSIParser: Sendable {
    public var style = ANSIStyle()

    public init() {}

    public mutating func reset() { style = ANSIStyle() }

    /// 解析一行（不含换行）。行内的 \r 视为回到行首：只保留最后一段非空内容（进度条）。
    public mutating func parse(_ line: String) -> [ANSISpan] {
        var spans: [ANSISpan] = []
        var current = ""
        var curStyle = style
        var segmentStart = 0 // \r 之后的 spans 起点
        var keptBeforeCR: [ANSISpan] = []

        func flush() {
            if !current.isEmpty {
                if let last = spans.last, last.style == curStyle, spans.count > segmentStart {
                    spans[spans.count - 1].text += current
                } else {
                    spans.append(ANSISpan(text: current, style: curStyle))
                }
                current = ""
            }
        }

        let scalars = Array(line.unicodeScalars)
        var i = 0
        let n = scalars.count
        while i < n {
            let c = scalars[i]
            switch c.value {
            case 0x1B: // ESC
                guard i + 1 < n else { i += 1; continue }
                let next = scalars[i + 1].value
                if next == 0x5B { // CSI: ESC [
                    var j = i + 2
                    var params = ""
                    var final: UInt32 = 0
                    while j < n {
                        let v = scalars[j].value
                        if v >= 0x40 && v <= 0x7E { final = v; break }
                        params.unicodeScalars.append(scalars[j])
                        j += 1
                    }
                    if final == 0x6D { // 'm' = SGR
                        flush()
                        applySGR(params)
                        curStyle = style
                    }
                    i = j + 1
                } else if next == 0x5D { // OSC: ESC ] ... (BEL | ESC \)
                    var j = i + 2
                    while j < n {
                        if scalars[j].value == 0x07 { j += 1; break }
                        if scalars[j].value == 0x1B, j + 1 < n, scalars[j + 1].value == 0x5C { j += 2; break }
                        j += 1
                    }
                    i = j
                } else if next == 0x50 || next == 0x58 || next == 0x5E || next == 0x5F { // DCS/SOS/PM/APC
                    var j = i + 2
                    while j < n {
                        if scalars[j].value == 0x07 { j += 1; break }
                        if scalars[j].value == 0x1B, j + 1 < n, scalars[j + 1].value == 0x5C { j += 2; break }
                        j += 1
                    }
                    i = j
                } else {
                    // ESC + 中间字节(0x20–0x2F)* + 终结字节
                    var j = i + 1
                    while j < n, scalars[j].value >= 0x20, scalars[j].value <= 0x2F { j += 1 }
                    i = min(j + 1, n)
                }
            case 0x0D: // \r
                flush()
                if spans.count > segmentStart {
                    keptBeforeCR = Array(spans[segmentStart...])
                }
                spans.removeSubrange(segmentStart...)
                segmentStart = spans.count
                i += 1
            case 0x08: // 退格
                if !current.isEmpty { current.removeLast() }
                else if spans.count > segmentStart, !spans[spans.count - 1].text.isEmpty {
                    spans[spans.count - 1].text.removeLast()
                }
                i += 1
            case 0x09: // tab 保留
                current.unicodeScalars.append(c)
                i += 1
            case 0x00...0x1F, 0x7F:
                i += 1 // 其他控制字符丢弃
            default:
                current.unicodeScalars.append(c)
                i += 1
            }
        }
        flush()
        // 行尾的 \r（CRLF 残留）不应清空内容
        if spans.count == segmentStart, !keptBeforeCR.isEmpty {
            spans.append(contentsOf: keptBeforeCR)
        }
        return spans.filter { !$0.text.isEmpty }
    }

    /// 去掉全部转义序列，只留文本。
    public static func strip(_ line: String) -> String {
        var p = ANSIParser()
        return p.parse(line).map(\.text).joined()
    }

    private mutating func applySGR(_ raw: String) {
        // 参数用 ; 分隔，子参数可用 :（如 38:2::r:g:b）
        let groups = raw.isEmpty ? [""] : raw.split(separator: ";", omittingEmptySubsequences: false).map(String.init)
        var idx = 0
        func intAt(_ k: Int) -> Int? { k < groups.count ? Int(groups[k]) : nil }
        while idx < groups.count {
            let g = groups[idx]
            if g.contains(":") {
                let sub = g.split(separator: ":", omittingEmptySubsequences: false).map { Int($0) }
                if let first = sub.first ?? nil {
                    if first == 38 || first == 48, let color = Self.extendedColor(Array(sub.dropFirst())) {
                        if first == 38 { style.fg = color } else { style.bg = color }
                    } else if first == 4 {
                        style.underline = (sub.count > 1 ? (sub[1] ?? 1) : 1) != 0
                    }
                }
                idx += 1
                continue
            }
            let code = Int(g) ?? 0
            switch code {
            case 0: style = ANSIStyle()
            case 1: style.bold = true
            case 2: style.dim = true
            case 3: style.italic = true
            case 4, 21: style.underline = true
            case 7: style.inverse = true
            case 9: style.strikethrough = true
            case 22: style.bold = false; style.dim = false
            case 23: style.italic = false
            case 24: style.underline = false
            case 27: style.inverse = false
            case 29: style.strikethrough = false
            case 30...37: style.fg = .palette(UInt8(code - 30))
            case 39: style.fg = .default
            case 40...47: style.bg = .palette(UInt8(code - 40))
            case 49: style.bg = .default
            case 90...97: style.fg = .palette(UInt8(code - 90 + 8))
            case 100...107: style.bg = .palette(UInt8(code - 100 + 8))
            case 38, 48:
                if intAt(idx + 1) == 5, let n = intAt(idx + 2) {
                    let c = ANSIColor.palette(UInt8(clamping: n))
                    if code == 38 { style.fg = c } else { style.bg = c }
                    idx += 2
                } else if intAt(idx + 1) == 2, let r = intAt(idx + 2), let gg = intAt(idx + 3), let b = intAt(idx + 4) {
                    let c = ANSIColor.rgb(UInt8(clamping: r), UInt8(clamping: gg), UInt8(clamping: b))
                    if code == 38 { style.fg = c } else { style.bg = c }
                    idx += 4
                }
            default: break
            }
            idx += 1
        }
    }

    private static func extendedColor(_ rest: [Int?]) -> ANSIColor? {
        guard let mode = rest.first ?? nil else { return nil }
        if mode == 5, rest.count >= 2, let n = rest[1] { return .palette(UInt8(clamping: n)) }
        if mode == 2 {
            // 38:2:r:g:b 或 38:2:<色彩空间>:r:g:b
            let vals = Array(rest.dropFirst())
            let rgb = vals.count >= 4 ? Array(vals.suffix(3)) : vals
            if rgb.count == 3, let r = rgb[0], let g = rgb[1], let b = rgb[2] {
                return .rgb(UInt8(clamping: r), UInt8(clamping: g), UInt8(clamping: b))
            }
        }
        return nil
    }
}

public enum ANSIPalette {
    /// 256 色调色板中 16–255 的 RGB（0–15 由设计变量的 ansi 数组提供）。
    public static func rgb(forIndex index: UInt8) -> (UInt8, UInt8, UInt8)? {
        let i = Int(index)
        if i < 16 { return nil }
        if i < 232 {
            let v = i - 16
            let levels: [UInt8] = [0, 95, 135, 175, 215, 255]
            return (levels[v / 36], levels[(v / 6) % 6], levels[v % 6])
        }
        let gray = UInt8(8 + (i - 232) * 10)
        return (gray, gray, gray)
    }
}

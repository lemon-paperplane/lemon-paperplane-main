import CoreGraphics
import Foundation

// 只支持 docs/plugin-protocol.md 第 9 节规定的 SVG 子集：
// 元素 svg g path circle ellipse rect line polyline polygon；
// 属性 viewBox fill stroke stroke-width stroke-linecap stroke-linejoin fill-rule opacity transform
// （另外容忍 fill-opacity / stroke-opacity / style 里的同名声明）。

public enum SVGPaint: Hashable, Sendable {
    case none
    case currentColor
    case color(r: Double, g: Double, b: Double, a: Double)
}

public struct SVGShapeStyle: Hashable, Sendable {
    public var fill: SVGPaint = .color(r: 0, g: 0, b: 0, a: 1) // SVG 默认填充黑色
    public var stroke: SVGPaint = .none
    public var strokeWidth: Double = 1
    public var lineCap: CGLineCap = .butt
    public var lineJoin: CGLineJoin = .miter
    public var evenOdd = false
    public var opacity: Double = 1
    public var fillOpacity: Double = 1
    public var strokeOpacity: Double = 1
}

public struct SVGShape: @unchecked Sendable {
    public var path: CGPath
    public var style: SVGShapeStyle
}

public struct SVGDocument: @unchecked Sendable {
    public var viewBox: CGRect
    public var shapes: [SVGShape]

    /// 只用 currentColor（或无色）着色：可以作为模板图随主题着色
    public var isMonochrome: Bool {
        shapes.allSatisfy { s in
            [s.style.fill, s.style.stroke].allSatisfy { p in
                switch p {
                case .none, .currentColor: return true
                case .color: return false
                }
            }
        }
    }
}

public enum SVGError: Error {
    case invalidXML(String)
    case notSVG
}

public enum SVGParser {
    public static func parse(_ data: Data) throws -> SVGDocument {
        let delegate = Delegate()
        let p = XMLParser(data: data)
        p.shouldProcessNamespaces = false
        p.delegate = delegate
        if !p.parse() {
            throw SVGError.invalidXML(p.parserError?.localizedDescription ?? "unknown")
        }
        guard let vb = delegate.viewBox else { throw SVGError.notSVG }
        return SVGDocument(viewBox: vb, shapes: delegate.shapes)
    }

    public static func parse(_ string: String) throws -> SVGDocument {
        try parse(Data(string.utf8))
    }

    private struct Context {
        var style = SVGShapeStyle()
        var transform = CGAffineTransform.identity
    }

    private final class Delegate: NSObject, XMLParserDelegate {
        var viewBox: CGRect?
        var shapes: [SVGShape] = []
        var stack: [Context] = []
        var skipDepth = 0 // 不认识的容器（defs、title 等）内部全部跳过

        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?,
                    qualifiedName: String?, attributes attrs: [String: String]) {
            let local = name.split(separator: ":").last.map(String.init) ?? name
            if skipDepth > 0 { skipDepth += 1; return }
            var ctx = stack.last ?? Context()
            if local == "svg" && stack.isEmpty {
                viewBox = SVGParser.viewBox(attrs)
                ctx = Context()
            }
            SVGParser.applyPresentation(attrs, to: &ctx.style)
            if let t = attrs["transform"] {
                ctx.transform = SVGParser.parseTransform(t).concatenating(ctx.transform)
            }
            switch local {
            case "svg", "g":
                stack.append(ctx)
            case "path", "circle", "ellipse", "rect", "line", "polyline", "polygon":
                stack.append(ctx)
                if let path = SVGParser.geometry(local, attrs) {
                    var t = ctx.transform
                    let transformed = path.copy(using: &t) ?? path
                    var style = ctx.style
                    let det = abs(t.a * t.d - t.b * t.c)
                    if det > 0 { style.strokeWidth *= sqrt(det) }
                    shapes.append(SVGShape(path: transformed, style: style))
                }
            default:
                skipDepth = 1
            }
        }

        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
            if skipDepth > 0 { skipDepth -= 1; return }
            if !stack.isEmpty { stack.removeLast() }
        }
    }

    // MARK: 属性

    static func viewBox(_ attrs: [String: String]) -> CGRect {
        if let vb = attrs["viewBox"] {
            let n = numbers(vb)
            if n.count == 4, n[2] > 0, n[3] > 0 { return CGRect(x: n[0], y: n[1], width: n[2], height: n[3]) }
        }
        let w = attrs["width"].flatMap { numbers($0).first } ?? 24
        let h = attrs["height"].flatMap { numbers($0).first } ?? 24
        return CGRect(x: 0, y: 0, width: w, height: h)
    }

    static func applyPresentation(_ attrs: [String: String], to style: inout SVGShapeStyle) {
        var all = attrs
        if let s = attrs["style"] {
            for decl in s.split(separator: ";") {
                let kv = decl.split(separator: ":", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
                if kv.count == 2 { all[kv[0]] = kv[1] }
            }
        }
        if let v = all["fill"], let p = paint(v) { style.fill = p }
        if let v = all["stroke"], let p = paint(v) { style.stroke = p }
        if let v = all["stroke-width"], let n = numbers(v).first { style.strokeWidth = n }
        if let v = all["stroke-linecap"] {
            switch v.trimmingCharacters(in: .whitespaces) {
            case "round": style.lineCap = .round
            case "square": style.lineCap = .square
            default: style.lineCap = .butt
            }
        }
        if let v = all["stroke-linejoin"] {
            switch v.trimmingCharacters(in: .whitespaces) {
            case "round": style.lineJoin = .round
            case "bevel": style.lineJoin = .bevel
            default: style.lineJoin = .miter
            }
        }
        if let v = all["fill-rule"] { style.evenOdd = v.trimmingCharacters(in: .whitespaces) == "evenodd" }
        if let v = all["opacity"], let n = numbers(v).first { style.opacity *= n }
        if let v = all["fill-opacity"], let n = numbers(v).first { style.fillOpacity = n }
        if let v = all["stroke-opacity"], let n = numbers(v).first { style.strokeOpacity = n }
    }

    static let namedColors: [String: (Double, Double, Double)] = [
        "black": (0, 0, 0), "white": (1, 1, 1), "red": (1, 0, 0), "green": (0, 0.5, 0), "lime": (0, 1, 0),
        "blue": (0, 0, 1), "yellow": (1, 1, 0), "orange": (1, 0.647, 0), "gray": (0.5, 0.5, 0.5),
        "grey": (0.5, 0.5, 0.5), "silver": (0.75, 0.75, 0.75), "purple": (0.5, 0, 0.5), "navy": (0, 0, 0.5),
        "teal": (0, 0.5, 0.5), "maroon": (0.5, 0, 0), "olive": (0.5, 0.5, 0),
    ]

    public static func paint(_ raw: String) -> SVGPaint? {
        let v = raw.trimmingCharacters(in: .whitespaces)
        let lower = v.lowercased()
        if lower == "none" || lower == "transparent" { return SVGPaint.none }
        if lower == "currentcolor" { return .currentColor }
        if let c = parseColor(lower) { return .color(r: c.0, g: c.1, b: c.2, a: c.3) }
        return nil
    }

    /// 解析颜色，返回 0–1 的 RGBA。
    public static func parseColor(_ raw: String) -> (Double, Double, Double, Double)? {
        let v = raw.trimmingCharacters(in: .whitespaces).lowercased()
        if v.hasPrefix("#") {
            let hex = Array(v.dropFirst())
            func h(_ s: ArraySlice<Character>) -> Double? { UInt8(String(s), radix: 16).map { Double($0) / 255 } }
            switch hex.count {
            case 3, 4:
                let e = hex.map { "\($0)\($0)" }.joined()
                return parseColor("#" + e)
            case 6, 8:
                guard let r = h(hex[0..<2]), let g = h(hex[2..<4]), let b = h(hex[4..<6]) else { return nil }
                let a = hex.count == 8 ? (h(hex[6..<8]) ?? 1) : 1
                return (r, g, b, a)
            default: return nil
            }
        }
        if v.hasPrefix("rgb") {
            let n = numbers(v.replacingOccurrences(of: "%", with: ""))
            guard n.count >= 3 else { return nil }
            return (n[0] / 255, n[1] / 255, n[2] / 255, n.count >= 4 ? n[3] : 1)
        }
        if let c = namedColors[v] { return (c.0, c.1, c.2, 1) }
        return nil
    }

    // MARK: 变换

    public static func parseTransform(_ raw: String) -> CGAffineTransform {
        var result = CGAffineTransform.identity
        let scanner = raw
        var idx = scanner.startIndex
        while idx < scanner.endIndex {
            guard let open = scanner[idx...].firstIndex(of: "("),
                  let close = scanner[open...].firstIndex(of: ")") else { break }
            let name = scanner[idx..<open].trimmingCharacters(in: CharacterSet(charactersIn: " ,\t\n"))
            let args = numbers(String(scanner[scanner.index(after: open)..<close]))
            var t = CGAffineTransform.identity
            switch name {
            case "translate":
                t = CGAffineTransform(translationX: args.first ?? 0, y: args.count > 1 ? args[1] : 0)
            case "scale":
                let sx = args.first ?? 1
                t = CGAffineTransform(scaleX: sx, y: args.count > 1 ? args[1] : sx)
            case "rotate":
                let a = (args.first ?? 0) * .pi / 180
                if args.count >= 3 {
                    t = CGAffineTransform(translationX: args[1], y: args[2])
                        .rotated(by: a)
                        .translatedBy(x: -args[1], y: -args[2])
                } else {
                    t = CGAffineTransform(rotationAngle: a)
                }
            case "matrix":
                if args.count == 6 { t = CGAffineTransform(a: args[0], b: args[1], c: args[2], d: args[3], tx: args[4], ty: args[5]) }
            case "skewX":
                t = CGAffineTransform(a: 1, b: 0, c: tan((args.first ?? 0) * .pi / 180), d: 1, tx: 0, ty: 0)
            case "skewY":
                t = CGAffineTransform(a: 1, b: tan((args.first ?? 0) * .pi / 180), c: 0, d: 1, tx: 0, ty: 0)
            default: break
            }
            // transform 列表从左到右：先应用最右边的
            result = t.concatenating(result)
            idx = scanner.index(after: close)
        }
        return result
    }

    // MARK: 几何

    static func geometry(_ name: String, _ a: [String: String]) -> CGPath? {
        func num(_ k: String) -> Double? { a[k].flatMap { numbers($0).first } }
        switch name {
        case "path":
            guard let d = a["d"] else { return nil }
            return SVGPathParser.parse(d)
        case "circle":
            guard let r = num("r"), r > 0 else { return nil }
            let cx = num("cx") ?? 0, cy = num("cy") ?? 0
            return CGPath(ellipseIn: CGRect(x: cx - r, y: cy - r, width: r * 2, height: r * 2), transform: nil)
        case "ellipse":
            guard let rx = num("rx"), let ry = num("ry"), rx > 0, ry > 0 else { return nil }
            let cx = num("cx") ?? 0, cy = num("cy") ?? 0
            return CGPath(ellipseIn: CGRect(x: cx - rx, y: cy - ry, width: rx * 2, height: ry * 2), transform: nil)
        case "rect":
            guard let w = num("width"), let h = num("height"), w > 0, h > 0 else { return nil }
            let rect = CGRect(x: num("x") ?? 0, y: num("y") ?? 0, width: w, height: h)
            var rx = num("rx"), ry = num("ry")
            if rx == nil { rx = ry }
            if ry == nil { ry = rx }
            let crx = min(max(rx ?? 0, 0), w / 2), cry = min(max(ry ?? 0, 0), h / 2)
            if crx > 0 && cry > 0 {
                return CGPath(roundedRect: rect, cornerWidth: crx, cornerHeight: cry, transform: nil)
            }
            return CGPath(rect: rect, transform: nil)
        case "line":
            let p = CGMutablePath()
            p.move(to: CGPoint(x: num("x1") ?? 0, y: num("y1") ?? 0))
            p.addLine(to: CGPoint(x: num("x2") ?? 0, y: num("y2") ?? 0))
            return p
        case "polyline", "polygon":
            let n = numbers(a["points"] ?? "")
            guard n.count >= 4 else { return nil }
            let p = CGMutablePath()
            p.move(to: CGPoint(x: n[0], y: n[1]))
            var i = 2
            while i + 1 < n.count {
                p.addLine(to: CGPoint(x: n[i], y: n[i + 1]))
                i += 2
            }
            if name == "polygon" { p.closeSubpath() }
            return p
        default:
            return nil
        }
    }

    /// 从字符串里取出全部数字（容忍逗号、空白、紧挨的负号与小数点，如 "1.5.5-2"）。
    public static func numbers(_ s: String) -> [Double] {
        var scanner = NumberScanner(s)
        var out: [Double] = []
        while let n = scanner.next() { out.append(n) }
        return out
    }
}

/// SVG 数字扫描器。
struct NumberScanner {
    let chars: [UInt8]
    var pos = 0

    init(_ s: String) { chars = Array(s.utf8) }

    var atEnd: Bool { pos >= chars.count }

    mutating func skipSeparators() {
        while pos < chars.count {
            let c = chars[pos]
            if c == 0x20 || c == 0x2C || c == 0x09 || c == 0x0A || c == 0x0D { pos += 1 } else { break }
        }
    }

    /// 读一个旗标（arc 的 large-arc / sweep 可以写成紧挨的 0/1）
    mutating func nextFlag() -> Bool? {
        skipSeparators()
        guard pos < chars.count else { return nil }
        let c = chars[pos]
        if c == 0x30 { pos += 1; return false }
        if c == 0x31 { pos += 1; return true }
        return nil
    }

    mutating func next() -> Double? {
        // 跳过非数字字符（单位、括号等）
        while pos < chars.count {
            let c = chars[pos]
            if (c >= 0x30 && c <= 0x39) || c == 0x2D || c == 0x2B || c == 0x2E { break }
            pos += 1
        }
        return readNumber()
    }

    /// 只在当前位置（跳过分隔符后）读数字，读不到返回 nil 且不移动
    mutating func peekNumberAvailable() -> Bool {
        skipSeparators()
        guard pos < chars.count else { return false }
        let c = chars[pos]
        return (c >= 0x30 && c <= 0x39) || c == 0x2D || c == 0x2B || c == 0x2E
    }

    mutating func readNumber() -> Double? {
        skipSeparators()
        guard pos < chars.count else { return nil }
        let start = pos
        if chars[pos] == 0x2D || chars[pos] == 0x2B { pos += 1 }
        var sawDigit = false
        var sawDot = false
        while pos < chars.count {
            let c = chars[pos]
            if c >= 0x30 && c <= 0x39 { sawDigit = true; pos += 1 }
            else if c == 0x2E && !sawDot { sawDot = true; pos += 1 }
            else { break }
        }
        if pos < chars.count, sawDigit, chars[pos] == 0x65 || chars[pos] == 0x45 { // 指数
            var p = pos + 1
            if p < chars.count, chars[p] == 0x2D || chars[p] == 0x2B { p += 1 }
            if p < chars.count, chars[p] >= 0x30 && chars[p] <= 0x39 {
                pos = p
                while pos < chars.count, chars[pos] >= 0x30 && chars[pos] <= 0x39 { pos += 1 }
            }
        }
        guard sawDigit else { pos = start; return nil }
        return Double(String(decoding: chars[start..<pos], as: UTF8.self))
    }
}

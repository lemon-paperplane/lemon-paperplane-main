import CoreGraphics
import Foundation

/// SVG path 的 d 属性解析：M L H V C S Q T A Z（含相对形式），弧线转换为三次贝塞尔。
public enum SVGPathParser {
    public static func parse(_ d: String) -> CGPath {
        let path = CGMutablePath()
        var s = NumberScanner(d)
        var cmd: UInt8 = 0
        var cur = CGPoint.zero
        var start = CGPoint.zero
        var lastCtrl: CGPoint? // 上一个 C/S 的第二控制点（用于 S）
        var lastQuad: CGPoint? // 上一个 Q/T 的控制点（用于 T）
        var hasCurrent = false

        func isCommand(_ c: UInt8) -> Bool {
            switch c {
            case 0x4D, 0x6D, 0x4C, 0x6C, 0x48, 0x68, 0x56, 0x76, 0x43, 0x63, 0x53, 0x73,
                 0x51, 0x71, 0x54, 0x74, 0x41, 0x61, 0x5A, 0x7A: return true
            default: return false
            }
        }

        while true {
            s.skipSeparators()
            if s.atEnd { break }
            let c = s.chars[s.pos]
            if isCommand(c) {
                cmd = c
                s.pos += 1
                if c == 0x5A || c == 0x7A { // Z
                    if hasCurrent { path.closeSubpath() }
                    cur = start
                    lastCtrl = nil; lastQuad = nil
                    continue
                }
            } else if cmd == 0 {
                s.pos += 1 // 开头的垃圾字符
                continue
            } else if !s.peekNumberAvailable() {
                s.pos += 1
                continue
            }

            let rel = cmd >= 0x61
            func pt(_ x: Double, _ y: Double) -> CGPoint { rel ? CGPoint(x: cur.x + x, y: cur.y + y) : CGPoint(x: x, y: y) }

            switch cmd | 0x20 { // 统一小写比较
            case 0x6D: // m
                guard let x = s.readNumber(), let y = s.readNumber() else { return path }
                cur = pt(x, y)
                start = cur
                path.move(to: cur)
                hasCurrent = true
                // 后续的坐标对按 L/l 处理
                cmd = rel ? 0x6C : 0x4C
                lastCtrl = nil; lastQuad = nil
            case 0x6C: // l
                guard let x = s.readNumber(), let y = s.readNumber() else { return path }
                cur = pt(x, y)
                ensureStart(path, &hasCurrent, cur)
                path.addLine(to: cur)
                lastCtrl = nil; lastQuad = nil
            case 0x68: // h
                guard let x = s.readNumber() else { return path }
                cur = CGPoint(x: rel ? cur.x + x : x, y: cur.y)
                ensureStart(path, &hasCurrent, cur)
                path.addLine(to: cur)
                lastCtrl = nil; lastQuad = nil
            case 0x76: // v
                guard let y = s.readNumber() else { return path }
                cur = CGPoint(x: cur.x, y: rel ? cur.y + y : y)
                ensureStart(path, &hasCurrent, cur)
                path.addLine(to: cur)
                lastCtrl = nil; lastQuad = nil
            case 0x63: // c
                guard let x1 = s.readNumber(), let y1 = s.readNumber(), let x2 = s.readNumber(),
                      let y2 = s.readNumber(), let x = s.readNumber(), let y = s.readNumber() else { return path }
                let c1 = pt(x1, y1), c2 = pt(x2, y2), e = pt(x, y)
                ensureStart(path, &hasCurrent, cur)
                path.addCurve(to: e, control1: c1, control2: c2)
                lastCtrl = c2; lastQuad = nil
                cur = e
            case 0x73: // s
                guard let x2 = s.readNumber(), let y2 = s.readNumber(), let x = s.readNumber(),
                      let y = s.readNumber() else { return path }
                let c1 = lastCtrl.map { CGPoint(x: 2 * cur.x - $0.x, y: 2 * cur.y - $0.y) } ?? cur
                let c2 = pt(x2, y2), e = pt(x, y)
                ensureStart(path, &hasCurrent, cur)
                path.addCurve(to: e, control1: c1, control2: c2)
                lastCtrl = c2; lastQuad = nil
                cur = e
            case 0x71: // q
                guard let x1 = s.readNumber(), let y1 = s.readNumber(), let x = s.readNumber(),
                      let y = s.readNumber() else { return path }
                let c1 = pt(x1, y1), e = pt(x, y)
                ensureStart(path, &hasCurrent, cur)
                path.addQuadCurve(to: e, control: c1)
                lastQuad = c1; lastCtrl = nil
                cur = e
            case 0x74: // t
                guard let x = s.readNumber(), let y = s.readNumber() else { return path }
                let c1 = lastQuad.map { CGPoint(x: 2 * cur.x - $0.x, y: 2 * cur.y - $0.y) } ?? cur
                let e = pt(x, y)
                ensureStart(path, &hasCurrent, cur)
                path.addQuadCurve(to: e, control: c1)
                lastQuad = c1; lastCtrl = nil
                cur = e
            case 0x61: // a
                guard let rx = s.readNumber(), let ry = s.readNumber(), let rot = s.readNumber(),
                      let large = s.nextFlag(), let sweep = s.nextFlag(),
                      let x = s.readNumber(), let y = s.readNumber() else { return path }
                let e = pt(x, y)
                ensureStart(path, &hasCurrent, cur)
                addArc(path, from: cur, to: e, rx: rx, ry: ry, xRotation: rot, largeArc: large, sweep: sweep)
                cur = e
                lastCtrl = nil; lastQuad = nil
            default:
                s.pos += 1
            }
        }
        return path
    }

    private static func ensureStart(_ path: CGMutablePath, _ has: inout Bool, _ p: CGPoint) {
        if !has { path.move(to: p); has = true }
    }

    /// 椭圆弧（端点参数化）→ 若干段三次贝塞尔，算法见 SVG 1.1 附录 F.6。
    public static func addArc(_ path: CGMutablePath, from p0: CGPoint, to p1: CGPoint, rx rxIn: Double, ry ryIn: Double,
                              xRotation: Double, largeArc: Bool, sweep: Bool) {
        if p0 == p1 { return }
        var rx = abs(rxIn), ry = abs(ryIn)
        if rx == 0 || ry == 0 { path.addLine(to: p1); return }
        let phi = xRotation * .pi / 180
        let cosPhi = cos(phi), sinPhi = sin(phi)
        let dx = (p0.x - p1.x) / 2, dy = (p0.y - p1.y) / 2
        let x1p = cosPhi * dx + sinPhi * dy
        let y1p = -sinPhi * dx + cosPhi * dy
        // 半径不够时放大
        let lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
        if lambda > 1 { let k = sqrt(lambda); rx *= k; ry *= k }
        let num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
        let den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
        var coef = den == 0 ? 0 : sqrt(max(0, num / den))
        if largeArc == sweep { coef = -coef }
        let cxp = coef * (rx * y1p / ry)
        let cyp = coef * -(ry * x1p / rx)
        let cx = cosPhi * cxp - sinPhi * cyp + (p0.x + p1.x) / 2
        let cy = sinPhi * cxp + cosPhi * cyp + (p0.y + p1.y) / 2

        func angle(_ ux: Double, _ uy: Double, _ vx: Double, _ vy: Double) -> Double {
            let dot = ux * vx + uy * vy
            let len = sqrt(ux * ux + uy * uy) * sqrt(vx * vx + vy * vy)
            var a = acos(max(-1, min(1, dot / len)))
            if ux * vy - uy * vx < 0 { a = -a }
            return a
        }
        let theta1 = angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
        var delta = angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
        if !sweep && delta > 0 { delta -= 2 * .pi }
        if sweep && delta < 0 { delta += 2 * .pi }

        let segments = max(1, Int(ceil(abs(delta) / (.pi / 2) - 1e-7)))
        let step = delta / Double(segments)
        let t = 4.0 / 3.0 * tan(step / 4)
        var a1 = theta1
        func point(_ a: Double) -> CGPoint {
            CGPoint(x: cx + rx * cos(a) * cosPhi - ry * sin(a) * sinPhi,
                    y: cy + rx * cos(a) * sinPhi + ry * sin(a) * cosPhi)
        }
        func deriv(_ a: Double) -> CGPoint {
            CGPoint(x: -rx * sin(a) * cosPhi - ry * cos(a) * sinPhi,
                    y: -rx * sin(a) * sinPhi + ry * cos(a) * cosPhi)
        }
        for i in 0..<segments {
            let a2 = a1 + step
            let s0 = point(a1), s1 = i == segments - 1 ? p1 : point(a2)
            let d0 = deriv(a1), d1 = deriv(a2)
            let c1 = CGPoint(x: s0.x + t * d0.x, y: s0.y + t * d0.y)
            let c2 = CGPoint(x: s1.x - t * d1.x, y: s1.y - t * d1.y)
            path.addCurve(to: s1, control1: c1, control2: c2)
            a1 = a2
        }
    }
}

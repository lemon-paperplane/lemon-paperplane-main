import CoreGraphics
import Foundation

/// 用 CoreGraphics 绘制 SVGDocument。界面（NSImage 绘制回调）和图标生成工具共用。
public enum SVGRenderer {
    /// - Parameters:
    ///   - rect: 目标矩形（保持比例居中，相当于 xMidYMid meet）
    ///   - currentColor: currentColor 的颜色
    ///   - strokeScale: 线宽系数（16px 显示时 0.75，让 Lucide 的 2 单位线宽变成 1.5，见设计规范「图标」）
    ///   - flipped: 上下文是否已是左上原点（NSImage flipped:true 的绘制回调为 true；位图上下文为 false）
    public static func draw(_ doc: SVGDocument, in ctx: CGContext, rect: CGRect, currentColor: CGColor,
                            strokeScale: CGFloat = 1, flipped: Bool) {
        let vb = doc.viewBox
        guard vb.width > 0, vb.height > 0 else { return }
        let scale = min(rect.width / vb.width, rect.height / vb.height)
        let w = vb.width * scale, h = vb.height * scale
        let ox = rect.minX + (rect.width - w) / 2
        let oy = rect.minY + (rect.height - h) / 2

        ctx.saveGState()
        if flipped {
            ctx.translateBy(x: ox, y: oy)
        } else {
            ctx.translateBy(x: ox, y: oy + h)
            ctx.scaleBy(x: 1, y: -1)
        }
        ctx.scaleBy(x: scale, y: scale)
        ctx.translateBy(x: -vb.minX, y: -vb.minY)

        for shape in doc.shapes {
            let st = shape.style
            if let fill = color(st.fill, currentColor: currentColor, opacity: st.opacity * st.fillOpacity) {
                ctx.addPath(shape.path)
                ctx.setFillColor(fill)
                ctx.fillPath(using: st.evenOdd ? .evenOdd : .winding)
            }
            if let stroke = color(st.stroke, currentColor: currentColor, opacity: st.opacity * st.strokeOpacity),
               st.strokeWidth > 0 {
                ctx.addPath(shape.path)
                ctx.setStrokeColor(stroke)
                ctx.setLineWidth(st.strokeWidth * strokeScale)
                ctx.setLineCap(st.lineCap)
                ctx.setLineJoin(st.lineJoin)
                ctx.setMiterLimit(4)
                ctx.strokePath()
            }
        }
        ctx.restoreGState()
    }

    static func color(_ p: SVGPaint, currentColor: CGColor, opacity: Double) -> CGColor? {
        switch p {
        case .none: return nil
        case .currentColor:
            return opacity >= 1 ? currentColor : currentColor.copy(alpha: currentColor.alpha * opacity)
        case .color(let r, let g, let b, let a):
            return CGColor(srgbRed: r, green: g, blue: b, alpha: a * opacity)
        }
    }

    /// 渲染成 RGBA 位图（图标生成工具用）。
    public static func bitmap(_ doc: SVGDocument, pixels: Int, currentColor: CGColor) -> CGImage? {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let ctx = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.setShouldAntialias(true)
        ctx.interpolationQuality = .high
        draw(doc, in: ctx, rect: CGRect(x: 0, y: 0, width: pixels, height: pixels),
             currentColor: currentColor, strokeScale: 1, flipped: false)
        return ctx.makeImage()
    }
}

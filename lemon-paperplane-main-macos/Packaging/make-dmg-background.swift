// 生成 DMG 窗口背景（dmg-background.tiff，含 1x 与 2x）：浅色底 + 从应用图标指向「应用程序」的箭头。
// 尺寸与图标位置要和 build.sh 里 dmgbuild 的设置一致（窗口 600×380，图标中心 (160,175) 与 (440,175)）。
//
// 重新生成：cd lemon-paperplane-main-macos/Packaging && swift make-dmg-background.swift
import AppKit

let size = CGSize(width: 600, height: 380)
let iconY: CGFloat = 175          // 图标中心（从窗口顶部算）
let arrowFrom: CGFloat = 246, arrowTo: CGFloat = 354

func render(scale: CGFloat, to path: String) {
    let w = Int(size.width * scale), h = Int(size.height * scale)
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = size
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext

    // 背景：极浅的竖向渐变
    let top = NSColor(srgbRed: 0.984, green: 0.984, blue: 0.988, alpha: 1)
    let bottom = NSColor(srgbRed: 0.937, green: 0.941, blue: 0.949, alpha: 1)
    NSGradient(starting: bottom, ending: top)!.draw(in: NSRect(origin: .zero, size: size), angle: 90)

    // 箭头
    let y = size.height - iconY
    ctx.setStrokeColor(NSColor(srgbRed: 0.60, green: 0.62, blue: 0.66, alpha: 1).cgColor)
    ctx.setLineWidth(2.5)
    ctx.setLineCap(.round)
    ctx.setLineJoin(.round)
    ctx.move(to: CGPoint(x: arrowFrom, y: y))
    ctx.addLine(to: CGPoint(x: arrowTo, y: y))
    ctx.strokePath()
    ctx.move(to: CGPoint(x: arrowTo - 11, y: y + 10))
    ctx.addLine(to: CGPoint(x: arrowTo, y: y))
    ctx.addLine(to: CGPoint(x: arrowTo - 11, y: y - 10))
    ctx.strokePath()

    NSGraphicsContext.restoreGraphicsState()
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path))
}

let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("pp-dmg-bg-\(getpid())")
try! FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
let p1 = tmp.appendingPathComponent("bg.png").path, p2 = tmp.appendingPathComponent("bg@2x.png").path
render(scale: 1, to: p1)
render(scale: 2, to: p2)
let tiffutil = Process()
tiffutil.executableURL = URL(fileURLWithPath: "/usr/bin/tiffutil")
tiffutil.arguments = ["-cathidpicheck", p1, p2, "-out", "dmg-background.tiff"]
try! tiffutil.run()
tiffutil.waitUntilExit()
try? FileManager.default.removeItem(at: tmp)
print(tiffutil.terminationStatus == 0 ? "dmg-background.tiff" : "tiffutil 失败")

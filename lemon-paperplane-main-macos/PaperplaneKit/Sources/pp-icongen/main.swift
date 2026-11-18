import CoreGraphics
import Foundation
import ImageIO
import PaperplaneKit
import UniformTypeIdentifiers

// 用 PaperplaneKit 的 SVG 渲染器生成 macOS AppIcon.appiconset。
// 用法：swift run pp-icongen <源.svg> <AppIcon.appiconset 目录>
//      swift run pp-icongen --png <源.svg> <像素> <输出.png>

func writePNG(_ image: CGImage, to url: URL) throws {
    guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
        throw NSError(domain: "icongen", code: 1, userInfo: [NSLocalizedDescriptionKey: "cannot create \(url.path)"])
    }
    CGImageDestinationAddImage(dest, image, nil)
    if !CGImageDestinationFinalize(dest) {
        throw NSError(domain: "icongen", code: 2, userInfo: [NSLocalizedDescriptionKey: "cannot write \(url.path)"])
    }
}

func render(_ svg: URL, _ px: Int) throws -> CGImage {
    let doc = try SVGParser.parse(Data(contentsOf: svg))
    guard let img = SVGRenderer.bitmap(doc, pixels: px, currentColor: CGColor(gray: 0, alpha: 1)) else {
        throw NSError(domain: "icongen", code: 3, userInfo: [NSLocalizedDescriptionKey: "render failed"])
    }
    return img
}

let args = CommandLine.arguments
do {
    if args.count == 5, args[1] == "--png" {
        try writePNG(try render(URL(fileURLWithPath: args[2]), Int(args[3]) ?? 512), to: URL(fileURLWithPath: args[4]))
        exit(0)
    }
    guard args.count == 3 else {
        FileHandle.standardError.write(Data("usage: pp-icongen <icon.svg> <AppIcon.appiconset>\n".utf8))
        exit(2)
    }
    let svg = URL(fileURLWithPath: args[1])
    let out = URL(fileURLWithPath: args[2], isDirectory: true)
    try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
    var images: [[String: String]] = []
    for base in [16, 32, 128, 256, 512] {
        for scale in [1, 2] {
            let px = base * scale
            let name = "icon_\(base)x\(base)\(scale == 2 ? "@2x" : "").png"
            try writePNG(try render(svg, px), to: out.appendingPathComponent(name))
            images.append(["idiom": "mac", "size": "\(base)x\(base)", "scale": "\(scale)x", "filename": name])
        }
    }
    let contents: [String: Any] = ["images": images, "info": ["author": "xcode", "version": 1]]
    let data = try JSONSerialization.data(withJSONObject: contents, options: [.prettyPrinted, .sortedKeys])
    try data.write(to: out.appendingPathComponent("Contents.json"))
    print("wrote \(images.count) images to \(out.path)")
} catch {
    FileHandle.standardError.write(Data("error: \(error)\n".utf8))
    exit(1)
}

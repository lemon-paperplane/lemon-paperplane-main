import XCTest
@testable import PaperplaneKit

/// 用本机已有的插件仓库里的图标检验 SVG 子集渲染器（没有这些目录时跳过）。
final class PluginIconTests: XCTestCase {
    func testSiblingPluginIconsParse() throws {
        let parent = repoRoot.deletingLastPathComponent()
        let dirs = (try? FileManager.default.contentsOfDirectory(atPath: parent.path))?.filter { $0.hasPrefix("lemon-paperplane-plugin-") } ?? []
        var count = 0
        for d in dirs {
            let icons = parent.appendingPathComponent(d).appendingPathComponent("icons")
            for f in (try? FileManager.default.contentsOfDirectory(atPath: icons.path)) ?? [] where f.hasSuffix(".svg") {
                let doc = try SVGParser.parse(Data(contentsOf: icons.appendingPathComponent(f)))
                XCTAssertFalse(doc.shapes.isEmpty, "\(d)/\(f)")
                XCTAssertGreaterThan(doc.viewBox.width, 0, "\(d)/\(f)")
                XCTAssertNotNil(SVGRenderer.bitmap(doc, pixels: 32, currentColor: CGColor(gray: 0, alpha: 1)))
                count += 1
            }
        }
        if count == 0 { throw XCTSkip("没有找到插件仓库") }
    }
}

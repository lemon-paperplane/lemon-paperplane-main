import CoreGraphics
import XCTest
@testable import PaperplaneKit

/// 仓库根（lemon-paperplane-main）
let repoRoot: URL = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    .deletingLastPathComponent().deletingLastPathComponent()

final class ANSITests: XCTestCase {
    func testPlainText() {
        var p = ANSIParser()
        XCTAssertEqual(p.parse("hello"), [ANSISpan(text: "hello", style: .plain)])
    }

    func testBasicColorsAndReset() {
        var p = ANSIParser()
        let spans = p.parse("\u{1B}[31mred\u{1B}[0m plain \u{1B}[1;92mbold\u{1B}[22m")
        XCTAssertEqual(spans.count, 3)
        XCTAssertEqual(spans[0].style.fg, .palette(1))
        XCTAssertEqual(spans[1].style, .plain)
        XCTAssertEqual(spans[2].style.fg, .palette(10))
        XCTAssertTrue(spans[2].style.bold)
        XCTAssertFalse(p.style.bold) // 22 关掉粗体
        XCTAssertEqual(p.style.fg, .palette(10)) // 颜色跨行保留
    }

    func test256AndTrueColor() {
        var p = ANSIParser()
        let s = p.parse("\u{1B}[38;5;208ma\u{1B}[48;2;10;20;30mb\u{1B}[38:2::1:2:3mc\u{1B}[38:5:33md")
        XCTAssertEqual(s[0].style.fg, .palette(208))
        XCTAssertEqual(s[1].style.bg, .rgb(10, 20, 30))
        XCTAssertEqual(s[2].style.fg, .rgb(1, 2, 3))
        XCTAssertEqual(s[3].style.fg, .palette(33))
    }

    func testUnderlineItalicInverse() {
        var p = ANSIParser()
        let s = p.parse("\u{1B}[3;4;7mx\u{1B}[23;24;27my")
        XCTAssertTrue(s[0].style.italic && s[0].style.underline && s[0].style.inverse)
        XCTAssertEqual(s[1].style, .plain)
    }

    func testStripsCursorControlAndOSC() {
        var p = ANSIParser()
        let s = p.parse("\u{1B}[2K\u{1B}[1Gdone \u{1B}]8;;http://x\u{07}link\u{1B}]8;;\u{1B}\\ end\u{1B}[?25h")
        XCTAssertEqual(s.map(\.text).joined(), "done link end")
    }

    func testCarriageReturnProgress() {
        var p = ANSIParser()
        XCTAssertEqual(p.parse("10%\r50%\r100%").map(\.text).joined(), "100%")
        XCTAssertEqual(p.parse("line\r").map(\.text).joined(), "line")
    }

    func testStrip() {
        XCTAssertEqual(ANSIParser.strip("\u{1B}[32mok\u{1B}[0m\t1"), "ok\t1")
    }

    func testPalette256() {
        XCTAssertNil(ANSIPalette.rgb(forIndex: 7))
        XCTAssertEqual(ANSIPalette.rgb(forIndex: 16).map { [$0.0, $0.1, $0.2] }, [0, 0, 0])
        XCTAssertEqual(ANSIPalette.rgb(forIndex: 231).map { [$0.0, $0.1, $0.2] }, [255, 255, 255])
        XCTAssertEqual(ANSIPalette.rgb(forIndex: 196).map { [$0.0, $0.1, $0.2] }, [255, 0, 0])
        XCTAssertEqual(ANSIPalette.rgb(forIndex: 232).map { [$0.0, $0.1, $0.2] }, [8, 8, 8])
    }
}

final class SSETests: XCTestCase {
    func testParserFraming() {
        var p = SSEParser()
        XCTAssertNil(p.feed("event: end"))
        XCTAssertNil(p.feed("data: {}"))
        XCTAssertEqual(p.feed(""), SSEMessage(event: "end", data: "{}"))
        XCTAssertNil(p.feed(": ping"))
        XCTAssertNil(p.feed(""))
        XCTAssertNil(p.feed("data:a"))
        XCTAssertNil(p.feed("data: b"))
        XCTAssertEqual(p.feed(""), SSEMessage(data: "a\nb"))
    }

    func testLineSplitterAcrossChunks() {
        var s = LineSplitter()
        XCTAssertEqual(s.append(Data("data: 中".utf8)), [])
        XCTAssertEqual(s.append(Data("文\r\n\r\ndata: x\n".utf8)), ["data: 中文", "", "data: x"])
    }
}

final class SVGTests: XCTestCase {
    func testAllSharedIconsParse() throws {
        let dir = repoRoot.appendingPathComponent("shared/icons")
        let files = try FileManager.default.contentsOfDirectory(atPath: dir.path).filter { $0.hasSuffix(".svg") }
        XCTAssertGreaterThan(files.count, 100)
        for f in files {
            let doc = try SVGParser.parse(Data(contentsOf: dir.appendingPathComponent(f)))
            XCTAssertEqual(doc.viewBox, CGRect(x: 0, y: 0, width: 24, height: 24), f)
            XCTAssertFalse(doc.shapes.isEmpty, f)
            XCTAssertTrue(doc.isMonochrome, f)
            for s in doc.shapes {
                XCTAssertEqual(s.style.stroke, .currentColor, f)
                XCTAssertEqual(s.style.strokeWidth, 2, f)
                let box = s.path.boundingBoxOfPath
                XCTAssertTrue(CGRect(x: -1, y: -1, width: 26, height: 26).contains(box), "\(f) \(box)")
            }
        }
    }

    func testBrandingParses() throws {
        let dir = repoRoot.appendingPathComponent("shared/branding")
        for f in ["app-icon.svg", "menubar.svg"] {
            let doc = try SVGParser.parse(Data(contentsOf: dir.appendingPathComponent(f)))
            XCTAssertFalse(doc.shapes.isEmpty, f)
        }
        let menubar = try SVGParser.parse(Data(contentsOf: dir.appendingPathComponent("menubar.svg")))
        XCTAssertTrue(menubar.isMonochrome)
    }

    func testArcCircleBoundingBox() {
        // 用两段弧画一个圆心 (12,12) 半径 10 的圆
        let p = SVGPathParser.parse("M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0z")
        let b = p.boundingBoxOfPath
        XCTAssertEqual(b.minX, 2, accuracy: 0.05)
        XCTAssertEqual(b.maxX, 22, accuracy: 0.05)
        XCTAssertEqual(b.minY, 2, accuracy: 0.05)
        XCTAssertEqual(b.maxY, 22, accuracy: 0.05)
    }

    func testCompactNumbersAndFlags() {
        // 紧凑写法：.5.5、紧挨的旗标 011
        XCTAssertEqual(SVGParser.numbers("1.5.5-2e1,3"), [1.5, 0.5, -20, 3])
        let p = SVGPathParser.parse("M0 0a1 1 0 011 1")
        XCTAssertEqual(p.currentPoint.x, 1, accuracy: 0.001)
        XCTAssertEqual(p.currentPoint.y, 1, accuracy: 0.001)
        let q = SVGPathParser.parse("M1 1h4v4H1z m10 0 2 2")
        XCTAssertEqual(q.currentPoint, CGPoint(x: 13, y: 3))
    }

    func testSmoothCurvesAndRelative() {
        let p = SVGPathParser.parse("M0 0c1 1 2 1 3 0s2-1 3 0q1 1 2 0t2 0")
        XCTAssertEqual(p.currentPoint, CGPoint(x: 10, y: 0))
    }

    func testShapesAndTransforms() throws {
        let svg = """
        <svg viewBox="0 0 100 50" fill="#ff0000" stroke-width="3">
          <g transform="translate(10 5) scale(2)" opacity="0.5">
            <rect x="0" y="0" width="10" height="5" rx="1"/>
            <circle cx="5" cy="5" r="2" fill="currentColor"/>
          </g>
          <polygon points="0,0 10,0 10,10" fill-rule="evenodd" stroke="#00f" fill="none"/>
          <line x1="0" y1="0" x2="5" y2="5" stroke="black" style="stroke-linecap:round"/>
          <ellipse cx="50" cy="25" rx="10" ry="5" transform="rotate(90 50 25)"/>
          <defs><path d="M0 0L100 100"/></defs>
        </svg>
        """
        let doc = try SVGParser.parse(svg)
        XCTAssertEqual(doc.viewBox, CGRect(x: 0, y: 0, width: 100, height: 50))
        XCTAssertEqual(doc.shapes.count, 5) // defs 内的被跳过
        let rect = doc.shapes[0]
        XCTAssertEqual(rect.path.boundingBoxOfPath, CGRect(x: 10, y: 5, width: 20, height: 10))
        XCTAssertEqual(rect.style.opacity, 0.5)
        XCTAssertEqual(rect.style.strokeWidth, 6, accuracy: 0.001) // 随缩放
        XCTAssertEqual(doc.shapes[1].style.fill, .currentColor)
        XCTAssertTrue(doc.shapes[2].style.evenOdd)
        XCTAssertEqual(doc.shapes[2].style.fill, SVGPaint.none)
        XCTAssertEqual(doc.shapes[3].style.lineCap, .round)
        let e = doc.shapes[4].path.boundingBoxOfPath
        XCTAssertEqual(e.width, 10, accuracy: 0.01) // 旋转 90° 后宽高互换
        XCTAssertEqual(e.height, 20, accuracy: 0.01)
        XCTAssertFalse(doc.isMonochrome)
    }

    func testColors() {
        XCTAssertEqual(SVGParser.paint("#fff"), .color(r: 1, g: 1, b: 1, a: 1))
        XCTAssertEqual(SVGParser.paint("rgb(255,0,0)"), .color(r: 1, g: 0, b: 0, a: 1))
        XCTAssertEqual(SVGParser.paint("currentColor"), .currentColor)
        XCTAssertEqual(SVGParser.paint("none"), SVGPaint.none)
        let c = SVGParser.parseColor("#3574F080")
        XCTAssertEqual(c?.3 ?? 0, 128.0 / 255, accuracy: 0.001)
    }

    func testRenderBitmap() throws {
        let doc = try SVGParser.parse(Data(contentsOf: repoRoot.appendingPathComponent("shared/icons/play.svg")))
        let img = SVGRenderer.bitmap(doc, pixels: 32, currentColor: CGColor(gray: 0, alpha: 1))
        XCTAssertEqual(img?.width, 32)
    }
}

final class DesignAndI18nTests: XCTestCase {
    func testDesignTokensComplete() throws {
        let t = try DesignTokens.load(repoRoot.appendingPathComponent("shared/design-tokens.json"))
        XCTAssertEqual(t.missingColors(), [])
        XCTAssertEqual(t.icon.strokeScale(forSize: 16), 0.75)
        XCTAssertEqual(t.icon.strokeScale(forSize: 20), 0.875)
        XCTAssertEqual(t.icon.strokeScale(forSize: 32), 1)
        XCTAssertEqual(t.size["toolbarHeight"], 40)
        for theme in ["light", "dark"] {
            for (k, v) in t.color[theme]! { XCTAssertNotNil(SVGParser.parseColor(v), "\(theme).\(k)=\(v)") }
            for v in t.ansi[theme]! { XCTAssertNotNil(SVGParser.parseColor(v)) }
        }
    }

    func testI18nFilesHaveSameKeys() throws {
        let dir = repoRoot.appendingPathComponent("shared/i18n")
        let zh = Localizer.load(dir.appendingPathComponent("zh-Hans.json"))
        let en = Localizer.load(dir.appendingPathComponent("en.json"))
        XCTAssertGreaterThan(zh.count, 100)
        XCTAssertEqual(Set(zh.keys).subtracting(en.keys).sorted(), [], "en.json 缺少")
        XCTAssertEqual(Set(en.keys).subtracting(zh.keys).sorted(), [], "zh-Hans.json 缺少")
        // 占位符数量一致
        for (k, v) in zh {
            let a = placeholders(v), b = placeholders(en[k] ?? "")
            XCTAssertEqual(a, b, k)
        }
        // 文案里不得出现 emoji
        for (k, v) in zh.merging(en, uniquingKeysWith: { a, _ in a }) {
            XCTAssertFalse(v.unicodeScalars.contains { $0.properties.isEmojiPresentation }, k)
        }
    }

    private func placeholders(_ s: String) -> Set<String> {
        var out = Set<String>()
        for i in 0..<10 where s.contains("{\(i)}") { out.insert("{\(i)}") }
        return out
    }

    func testLocalizer() {
        let l = Localizer(locale: "zh-Hans", table: ["hi": "你好 {0}"], fallback: ["hi": "Hi {0}", "only.en": "E"])
        XCTAssertEqual(l.t("hi", ["A"]), "你好 A")
        XCTAssertEqual(l.t("only.en"), "E")
        XCTAssertEqual(l.t("missing.key"), "missing.key")
        XCTAssertEqual(Localizer.resolve(setting: "system", preferredLanguages: ["zh-Hant-TW"]), "zh-Hans")
        XCTAssertEqual(Localizer.resolve(setting: "system", preferredLanguages: ["ja-JP", "en-US"]), "en")
        XCTAssertEqual(Localizer.resolve(setting: "en", preferredLanguages: ["zh-Hans"]), "en")
        XCTAssertEqual(Localizer.resolve(setting: nil, preferredLanguages: []), "en")
    }
}

final class MiscTests: XCTestCase {
    func testCommitGraphGeometry() {
        let g = CommitGraphGeometry(laneWidth: 10, rowHeight: 20, leftPadding: 0)
        let prev = VCSGraphRow(lane: 0, lanes: 2, edges: [VCSGraphEdge(from: 0, to: 0, color: 0), VCSGraphEdge(from: 1, to: 0, color: 1)], color: 0)
        let cur = VCSGraphRow(lane: 0, lanes: 1, edges: [VCSGraphEdge(from: 0, to: 0, color: 0)], color: 0)
        let row = g.row(cur, previous: prev)
        XCTAssertEqual(row.node, CGPoint(x: 5, y: 10))
        XCTAssertEqual(row.segments.count, 3)
        // 上一行 1→0 的连线：在本行上半段从两列中点到本列中线
        XCTAssertEqual(row.segments[1].from, CGPoint(x: 10, y: 0))
        XCTAssertEqual(row.segments[1].to, CGPoint(x: 5, y: 10))
        XCTAssertEqual(row.segments[2].to, CGPoint(x: 5, y: 20))
        XCTAssertEqual(row.width, 20)
        XCTAssertEqual(CommitGraphGeometry.paletteIndex(9), 1)
        XCTAssertEqual(CommitGraphGeometry.paletteIndex(-1), 7)
    }

    func testIconRef() {
        XCTAssertEqual(IconSource.parse("builtin:play"), .builtin("play"))
        XCTAssertEqual(IconSource.parse("plugin:spring-boot/icons/a.svg"), .plugin(id: "spring-boot", path: "icons/a.svg"))
        XCTAssertEqual(IconSource.parse("icons/x.svg", pluginId: "gradle"), .plugin(id: "gradle", path: "icons/x.svg"))
        XCTAssertEqual(IconSource.parse("play"), .builtin("play"))
        XCTAssertNil(IconSource.parse(""))
        XCTAssertNil(IconSource.parse(nil))
    }

    func testFormat() {
        XCTAssertEqual(Format.abbreviatePath("/Users/me/Code/x", home: "/Users/me"), "~/Code/x")
        XCTAssertEqual(Format.abbreviatePath("/Users/meme/x", home: "/Users/me"), "/Users/meme/x")
        XCTAssertEqual(Format.memory(mb: 2048), "2.0 GB")
        XCTAssertEqual(Format.memory(mb: 512.4), "512 MB")
        XCTAssertEqual(Format.shortURL("http://localhost:8080/"), "localhost:8080")
        XCTAssertEqual(Format.duration(125), "2m 5s")
    }

    func testGoTime() {
        XCTAssertNotNil(GoTime.parse("2026-11-18T09:39:12Z"))
        XCTAssertNotNil(GoTime.parse("2026-11-18T09:39:12.5+08:00"))
        XCTAssertNotNil(GoTime.parse("2026-11-18T09:39:12.123456789-07:00"))
        XCTAssertTrue(GoTime.isZero(GoTime.parse("0001-01-01T00:00:00Z")))
    }
}

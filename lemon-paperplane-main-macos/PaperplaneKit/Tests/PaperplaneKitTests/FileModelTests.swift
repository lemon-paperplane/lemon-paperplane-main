import XCTest
@testable import PaperplaneKit

final class FileModelTests: XCTestCase {
    func fixture<T: Decodable>(_ name: String, _ t: T.Type) throws -> T {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures/\(name)", withExtension: nil))
        return try APIClient.decodeEnvelope(try Data(contentsOf: url), status: 200)
    }

    func testFSDecoding() throws {
        let list = try fixture("fs.list.json", FSListResult.self)
        XCTAssertEqual(list.entries.map(\.name), [".github", "node_modules", "src", "package.json", "link"])
        XCTAssertTrue(list.entries[0].hidden)
        XCTAssertTrue(list.entries[1].ignored && list.entries[1].dir)
        XCTAssertEqual(list.entries[3].size, 1234)
        XCTAssertEqual(list.entries[3].ext, "json")
        XCTAssertTrue(list.entries[4].symlink)
        XCTAssertEqual(list.entries[0].mtime, "2026-11-18T09:00:00.123456789+08:00") // 原样保留，回传给 expectedMtime
        let read = try fixture("fs.read.json", FSReadResult.self)
        XCTAssertEqual(read.lineEnding, "crlf")
        XCTAssertEqual(read.encoding, "gb18030")
        XCTAssertFalse(read.tooLarge)
        let hits = try fixture("fs.search.json", [FSSearchHit].self)
        XCTAssertEqual(hits.first?.directory, "src/main")
        XCTAssertEqual(hits.last?.directory, "")
        let grep = try fixture("fs.grep.json", FSGrepResult.self)
        XCTAssertEqual(grep.matches.first?.column, 7)
        XCTAssertTrue(grep.truncated)
    }

    func testWriteConflictErrorCarriesData() throws {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures/fs.write.conflict.json", withExtension: nil))
        XCTAssertThrowsError(try APIClient.decodeEnvelope(try Data(contentsOf: url), status: 400, as: FSWriteResult.self)) { e in
            let err = e as? APIError
            XCTAssertEqual(err?.code, "user_error")
            XCTAssertEqual(err?.data?["reason"], .string("modifiedExternally"))
        }
    }

    func testEditorContributionsAndSettings() throws {
        let c = try fixture("editor.contributions.json", Contributions.self)
        XCTAssertEqual(c.languages.count, 5)
        XCTAssertEqual(c.languages[0].blockComment, ["/*", "*/"])
        XCTAssertEqual(c.keymaps.map(\.platform), ["macos", "windows", "any"])
        XCTAssertEqual(c.keymaps[2].base, "eclipse-macos")
        XCTAssertEqual(c.editorThemes.first?.kind, "dark")
        // 老内核没有这些字段时为空数组
        let old = try JSONCoding.decoder.decode(Contributions.self, from: Data("{}".utf8))
        XCTAssertEqual(old.languages, [])

        let s = try fixture("settings.editor.json", AppSettings.self)
        XCTAssertTrue(s.autoSyncOnImport)
        XCTAssertEqual(s.editor?.fontSize, 14)
        XCTAssertEqual(s.editor?.autoSave, "onFocusChange")
        XCTAssertEqual(s.editor?.effectiveKeymap, "eclipse-macos")
        XCTAssertEqual(EditorSettings().tabSize, 4)
    }

    func testLanguageResolver() throws {
        let langs = try fixture("editor.contributions.json", Contributions.self).languages
        XCTAssertEqual(LanguageResolver.language(for: "App.java", in: langs)?.id, "java")
        XCTAssertEqual(LanguageResolver.language(for: "index.ts", in: langs)?.id, "typescript") // priority 高的
        XCTAssertEqual(LanguageResolver.language(for: "types.d.ts", in: langs)?.id, "tsdecl") // 最长扩展名
        XCTAssertEqual(LanguageResolver.language(for: "Dockerfile", in: langs)?.id, "dockerfile")
        XCTAssertEqual(LanguageResolver.language(for: "Dockerfile.dev", in: langs)?.id, "dockerfile")
        XCTAssertEqual(LanguageResolver.language(for: "app.dockerfile", in: langs)?.id, "dockerfile")
        XCTAssertNil(LanguageResolver.language(for: "notes.txt", in: langs))
        XCTAssertNil(LanguageResolver.language(for: ".ts", in: langs)) // 只有扩展名不算
        XCTAssertTrue(LanguageResolver.glob("*.config.*", matches: "vite.config.ts"))
        XCTAssertFalse(LanguageResolver.glob("a?c", matches: "abbc"))
    }

    func testFileIconsExistInSharedIcons() throws {
        let names = ["App.java", "package.json", "README.md", "logo.png", "run.sh", "docker-compose.yml", "data.csv",
                     "server.pem", "font.woff2", "x.sqlite", "a.zip", "Dockerfile", ".gitignore", ".env.local", "LICENSE",
                     "clip.mp4", "song.mp3", "noext", "a.patch"]
        let dir = repoRoot.appendingPathComponent("shared/icons")
        for n in names {
            let icon = FileIcon.builtinName(forFileName: n)
            XCTAssertTrue(FileManager.default.fileExists(atPath: dir.appendingPathComponent("\(icon).svg").path), "\(n) → \(icon)")
        }
        for e in [FSEntry(name: "src", path: "src", dir: true), FSEntry(name: ".git", path: ".git", dir: true)] {
            for expanded in [false, true] {
                let icon = FileIcon.builtinName(for: e, expanded: expanded)
                XCTAssertTrue(FileManager.default.fileExists(atPath: dir.appendingPathComponent("\(icon).svg").path), icon)
            }
        }
    }

    func testEditorURL() {
        let u = EditorURL.make(base: URL(string: "http://127.0.0.1:47213")!, token: "ab12", projectId: "p1",
                               file: "src/my file#1.ts", line: 12, locale: "zh-Hans", theme: "dark")
        XCTAssertEqual(u.absoluteString,
                       "http://127.0.0.1:47213/editor/index.html#token=ab12&projectId=p1&file=src/my%20file%231.ts&line=12&locale=zh-Hans&theme=dark")
        let v = EditorURL.make(base: URL(string: "http://127.0.0.1:1")!, token: "t", projectId: "p", file: nil, line: nil,
                               locale: "en", theme: "light")
        XCTAssertEqual(v.fragment, "token=t&projectId=p&locale=en&theme=light")
    }
}

/// 真实 daemon 的 fs.* / 编辑器相关响应（Fixtures/real）
final class RealFileFixtureTests: XCTestCase {
    func decode<T: Decodable>(_ name: String, _ t: T.Type) throws -> T {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures/real/\(name)", withExtension: nil), name)
        return try APIClient.decodeEnvelope(try Data(contentsOf: url), status: 200)
    }

    func testRealFS() throws {
        let list = try decode("fs.list.json", FSListResult.self)
        let venv = try XCTUnwrap(list.entries.first { $0.name == ".venv" })
        XCTAssertTrue(venv.dir && venv.hidden && venv.ignored)
        XCTAssertEqual(list.entries.first { $0.name == "main.py" }?.ext, "py")
        let read = try decode("fs.read.json", FSReadResult.self)
        XCTAssertTrue(read.content.hasPrefix("import time"))
        let stat = try decode("fs.stat.json", [FSStat].self)
        XCTAssertEqual(stat.map(\.exists), [true, false])
        let hits = try decode("fs.search.json", [FSSearchHit].self)
        XCTAssertEqual(hits.first?.path, "main.py")
        let grep = try decode("fs.grep.json", FSGrepResult.self)
        XCTAssertEqual(grep.matches.first?.line, 10)
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures/real/fs.write.conflict.json", withExtension: nil))
        XCTAssertThrowsError(try APIClient.decodeEnvelope(try Data(contentsOf: url), status: 400, as: FSWriteResult.self)) { e in
            XCTAssertEqual((e as? APIError)?.reason, "modifiedExternally")
        }
    }

    func testRealEditorContributionsAndSettings() throws {
        let c = try decode("ui.contributions.json", Contributions.self)
        XCTAssertEqual(c.defaultKeymap, "eclipse-macos")
        XCTAssertEqual(c.keymaps, []) // 快捷键插件尚未提供时为空
        let s = try decode("settings.get.json", AppSettings.self)
        XCTAssertEqual(s.editor?.tabSize, 4)
        XCTAssertEqual(s.editor?.fontFamily, "")
        XCTAssertEqual(s.editor?.effectiveKeymap, "eclipse-macos")
    }
}

import XCTest
@testable import PaperplaneKit

/// 用真实 daemon（0.1.0）返回的响应核对模型解码。夹具由 curl 抓取，见 Fixtures/real。
final class RealFixtureTests: XCTestCase {
    func data(_ name: String) throws -> Data {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures/real/\(name)", withExtension: nil), name)
        return try Data(contentsOf: url)
    }

    func decode<T: Decodable>(_ name: String, _ t: T.Type) throws -> T {
        try APIClient.decodeEnvelope(try data(name), status: 200)
    }

    func testAllRealResponsesDecode() throws {
        XCTAssertEqual(try decode("system.info.json", SystemInfo.self).apiBase, "http://127.0.0.1:47292")
        let ws = try decode("workspace.list.json", [Workspace].self)
        XCTAssertEqual(ws.first?.name, "默认工作区")
        let list = try decode("project.list.json", [ProjectSummary].self)
        XCTAssertFalse(list.isEmpty)
        XCTAssertTrue(list[0].facets.contains { $0.icon?.hasPrefix("plugin:") == true })
        let running = try decode("project.list.running.json", [ProjectSummary].self)
        XCTAssertTrue(running.contains { $0.hasActiveRun })
        let p = try decode("project.get.json", Project.self)
        XCTAssertEqual(p.modules.first?.runConfigs.first?.options["script"], .string("main.py"))
        XCTAssertEqual(p.modules.first?.toolchains["python"]?.version, "3.12.12")
        XCTAssertEqual(p.modules.first?.info.count, 3)
        let c = try decode("ui.contributions.json", Contributions.self)
        XCTAssertFalse(c.runConfigTypes.isEmpty)
        XCTAssertFalse(c.actions(at: ActionLocation.projectMenu).isEmpty)
        XCTAssertEqual(c.actions.first { $0.id == "installPackage" }?.form.first?.required, true)
        XCTAssertFalse(try decode("plugin.list.json", [PluginInfo].self).isEmpty)
        XCTAssertEqual(try decode("settings.get.json", AppSettings.self).apiPort, 47292)
        let tcs = try decode("toolchain.list.json", [Toolchain].self)
        XCTAssertFalse(tcs.isEmpty)
        XCTAssertFalse(try decode("toolchain.kinds.json", [ToolchainKind].self).isEmpty)
        let ms = try decode("mirrors.status.json", MirrorStatus.self)
        XCTAssertEqual(ms.effective, "china")
        XCTAssertFalse(ms.probes.isEmpty)
        let mcp = try decode("mcp.config.json", MCPConfig.self)
        XCTAssertTrue(mcp.claudeCode.hasPrefix("claude mcp add"))
        XCTAssertNotNil(mcp.claudeDesktop?["mcpServers"])
        XCTAssertEqual(try decode("project.problems.json", [Diagnosis].self), [])
        let types = try decode("runconfig.types.json", [RunConfigTypeChoice].self)
        XCTAssertFalse(types.first?.type.options.isEmpty ?? true)
        let scan = try decode("project.scan.json", [ScanEntry].self)
        XCTAssertTrue(scan.contains { $0.imported })
        let avail = try decode("toolchain.available.json", [AvailableVersion].self)
        XCTAssertTrue(avail.contains { $0.recommended })
        XCTAssertEqual(try decode("store.list.json", [StoreEntry].self), [])
        XCTAssertEqual(try decode("task.list.json", [TaskInfo].self), [])
        let view = try decode("view.render.json", ViewRenderResult.self)
        XCTAssertEqual(view.root.type, "column")
        XCTAssertEqual(view.root.children.first?.children.first?.style, "primary")
        let run = try decode("run.get.json", Run.self)
        XCTAssertEqual(run.ports, [47311])
        let start = try decode("run.start.json", Run.self)
        XCTAssertEqual(start.state, "preparing")
        let logs = try decode("run.logs.json", LogsResult.self)
        XCTAssertEqual(logs.lines.count, 5)
        XCTAssertTrue(logs.truncated)
    }

    func testRealEventStream() throws {
        var splitter = LineSplitter()
        var parser = SSEParser()
        var types: [String] = []
        for line in splitter.append(try data("events.sse.txt")) {
            guard let m = parser.feed(line) else { continue }
            let e = try JSONCoding.decoder.decode(DaemonEvent.self, from: Data(m.data.utf8))
            types.append(e.type)
            switch e.type {
            case EventType.hello: _ = try e.decode(SystemInfo.self)
            case EventType.run: _ = try e.decode(Run.self)
            case EventType.project: _ = try e.decode(ProjectSummary.self)
            case EventType.stats: XCTAssertFalse(try e.decode([RunBrief].self).isEmpty)
            default: break
            }
        }
        XCTAssertEqual(types.first, EventType.hello)
        XCTAssertTrue(types.contains(EventType.stats))
    }
}

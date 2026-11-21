import XCTest
@testable import PaperplaneKit

/// 对真实 daemon 的联调测试。默认跳过；设置 PAPERPLANE_LIVE_DATA_DIR=<独立实例的数据目录> 后运行。
final class LiveDaemonTests: XCTestCase {
    private func manager() throws -> DaemonManager {
        guard let dir = ProcessInfo.processInfo.environment["PAPERPLANE_LIVE_DATA_DIR"], !dir.isEmpty else {
            throw XCTSkip("未设置 PAPERPLANE_LIVE_DATA_DIR")
        }
        return DaemonManager(dataDir: URL(fileURLWithPath: dir))
    }

    func testCallsAndDecoding() async throws {
        let m = try manager()
        guard let (api, info) = await m.probe() else { return XCTFail("daemon 不可达") }
        XCTAssertFalse(info.version.isEmpty)
        _ = try await api.workspaces()
        _ = try await api.projects()
        _ = try await api.runs()
        _ = try await api.tasks()
        _ = try await api.settings()
        _ = try await api.contributions()
        _ = try await api.plugins()
        _ = try await api.toolchainKinds()
        _ = try await api.toolchains()
        _ = try await api.mirrorsStatus()
        _ = try await api.mcpConfig()
        _ = try await api.pendingPrompts()
        let ops = try await api.ops()
        XCTAssertGreaterThan(ops.arrayValue?.count ?? 0, 50)
        // 错误信封
        do {
            _ = try await api.project("no-such-project")
            XCTFail("应当报错")
        } catch let e as APIError {
            XCTAssertEqual(e.code, "not_found")
        }
        // 插件图标
        let svg = try await api.pluginFile(pluginId: "python", path: "icons/python.svg")
        XCTAssertFalse(try SVGParser.parse(svg).shapes.isEmpty)
    }

    func testEventStreamHello() async throws {
        let m = try manager()
        guard let (api, _) = await m.probe() else { return XCTFail("daemon 不可达") }
        let exp = expectation(description: "hello")
        let stream = EventStream()
        stream.start(client: api) { u in
            if case .event(let e) = u, e.type == EventType.hello { exp.fulfill() }
        }
        await fulfillment(of: [exp], timeout: 5)
        stream.stop()
    }

    func testConsoleStreamReplaysAndEnds() async throws {
        let m = try manager()
        guard let (api, _) = await m.probe() else { return XCTFail("daemon 不可达") }
        guard let run = try await api.runs().first(where: { !$0.isActive && $0.lines > 0 }) else {
            throw XCTSkip("没有已结束且有日志的运行")
        }
        let gotLines = expectation(description: "lines")
        gotLines.assertForOverFulfill = false
        let ended = expectation(description: "end")
        let box = LinesBox()
        let s = ConsoleStream(consoleId: run.id, since: 0)
        s.start(client: api) { u in
            switch u {
            case .lines(let l, _):
                // 只有实时尾部的消息不算回放的行
                if !l.isEmpty { box.add(l.count); gotLines.fulfill() }
            case .ended: ended.fulfill()
            case .disconnected: break
            }
        }
        await fulfillment(of: [gotLines, ended], timeout: 10)
        s.stop()
        XCTAssertGreaterThan(box.count, 0)
        // run.logs 尾部
        let logs = try await api.logs(consoleId: run.id, tail: 3)
        XCTAssertLessThanOrEqual(logs.lines.count, 3)
    }
}

private final class LinesBox: @unchecked Sendable {
    private let lock = NSLock()
    private var n = 0
    func add(_ k: Int) { lock.lock(); n += k; lock.unlock() }
    var count: Int { lock.lock(); defer { lock.unlock() }; return n }
}

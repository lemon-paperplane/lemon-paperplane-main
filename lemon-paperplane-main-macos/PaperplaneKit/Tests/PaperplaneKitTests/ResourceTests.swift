import CoreGraphics
import XCTest
@testable import PaperplaneKit

final class ResourceTests: XCTestCase {
    func real<T: Decodable>(_ name: String, _ t: T.Type) throws -> T {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures/real/\(name)", withExtension: nil), name)
        return try APIClient.decodeEnvelope(try Data(contentsOf: url), status: 200)
    }

    func testRealRunStats() throws {
        let s = try real("run.stats.json", RunStats.self)
        XCTAssertEqual(s.state, "running")
        XCTAssertEqual(s.samples.count, 4)
        XCTAssertEqual(s.processes.first?.name, "Python")
        XCTAssertEqual(s.ports, [47311])
        XCTAssertEqual(s.intervalMs, 2000)
        XCTAssertEqual(s.peakRSSMB, 24.1, accuracy: 0.001)
        let ended = try real("run.stats.ended.json", RunStats.self)
        XCTAssertEqual(ended.state, "exited")
        XCTAssertEqual(ended.processes, [])
        XCTAssertEqual(ended.samples.count, 4) // 结束后保留曲线
        let run = try real("run.get.json", Run.self)
        XCTAssertEqual(run.procs, 1)
        XCTAssertGreaterThan(run.peakRSSMB, 0)
    }

    func testRealStatsEvent() throws {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures/real/stats.event.txt", withExtension: nil))
        var parser = SSEParser()
        var splitter = LineSplitter()
        var events: [DaemonEvent] = []
        for line in splitter.append(try Data(contentsOf: url) + Data("\n\n".utf8)) {
            if let m = parser.feed(line) { events.append(try JSONCoding.decoder.decode(DaemonEvent.self, from: Data(m.data.utf8))) }
        }
        let briefs = try XCTUnwrap(events.first).decode([RunBrief].self)
        XCTAssertEqual(briefs.first?.projectId, "6a16998ec83a0ff4")
        XCTAssertEqual(briefs.first?.procs, 1)
        XCTAssertGreaterThan(briefs.first?.t ?? 0, 0)
    }

    func testSeriesAppendMergeCapacity() {
        var s = ResourceSeries(capacity: 3)
        XCTAssertTrue(s.append(ResourceSample(t: 10, cpu: 1, rssMB: 10)))
        XCTAssertFalse(s.append(ResourceSample(t: 10, cpu: 9, rssMB: 99))) // 同一时间点忽略
        XCTAssertFalse(s.append(ResourceSample(t: 5, cpu: 9, rssMB: 99))) // 更早的忽略
        s.append(ResourceSample(t: 20, cpu: 2, rssMB: 20))
        s.append(ResourceSample(t: 30, cpu: 3, rssMB: 30))
        s.append(ResourceSample(t: 40, cpu: 4, rssMB: 40))
        XCTAssertEqual(s.samples.map(\.t), [20, 30, 40])
        XCTAssertTrue(s.merge([ResourceSample(t: 15, cpu: 0, rssMB: 0), ResourceSample(t: 35, cpu: 5, rssMB: 50)]))
        XCTAssertEqual(s.samples.map(\.t), [30, 35, 40])
        XCTAssertEqual(s.maxRSS, 50)
        XCTAssertFalse(s.merge([]))
    }

    func testSparklineGeometry() {
        let samples = [ResourceSample(t: 0, cpu: 0, rssMB: 0), ResourceSample(t: 1000, cpu: 0, rssMB: 50),
                       ResourceSample(t: 2000, cpu: 0, rssMB: 100)]
        let rect = CGRect(x: 0, y: 0, width: 100, height: 20)
        let pts = SparklineGeometry.points(samples, value: { $0.rssMB }, in: rect, maxValue: 100)
        // 最大值留 10% 余量：100 → 110
        XCTAssertEqual(pts.first, CGPoint(x: 0, y: 20))
        XCTAssertEqual(pts.last!.x, 100, accuracy: 0.001)
        XCTAssertEqual(pts.last!.y, 20 - 20 * (100 / 110), accuracy: 0.01)
        // 固定时间轴：5 分钟跨度时，2 秒的数据贴在右端
        let fixed = SparklineGeometry.points(samples, value: { $0.rssMB }, in: rect, span: 300_000, maxValue: 100)
        XCTAssertGreaterThan(fixed.first!.x, 99)
        XCTAssertEqual(SparklineGeometry.nearestIndex(pts, x: 52), 1)
        XCTAssertEqual(SparklineGeometry.niceCeiling(230), 250)
        XCTAssertEqual(SparklineGeometry.niceCeiling(0.7), 1)
        XCTAssertEqual(SparklineGeometry.niceCeiling(1300), 2000)
        // 单点
        let one = SparklineGeometry.points([samples[2]], value: { $0.rssMB }, in: rect)
        XCTAssertEqual(one.count, 1)
    }

    func testProcessTree() {
        // yarn(10) → node vite(20) → esbuild(30)；另一个孤立进程 99 的父进程不在树里
        let list = [
            RunProcess(pid: 10, ppid: 1, name: "node", rssMB: 115.9),
            RunProcess(pid: 20, ppid: 10, name: "node", rssMB: 93),
            RunProcess(pid: 30, ppid: 20, name: "esbuild", rssMB: 16.8),
            RunProcess(pid: 99, ppid: 7, name: "sh", rssMB: 1),
        ]
        let nodes = ProcessTree.flatten(list)
        XCTAssertEqual(nodes.map(\.process.pid), [10, 20, 30, 99])
        XCTAssertEqual(nodes.map(\.depth), [0, 1, 2, 0])
        XCTAssertEqual(nodes[0].subtreeRSSMB, 225.7, accuracy: 0.001)
        XCTAssertTrue(nodes[1].hasChildren)
        XCTAssertFalse(nodes[2].hasChildren)
        XCTAssertEqual(ProcessTree.flatten(list, collapsed: [20]).map(\.process.pid), [10, 20, 99])
        // 自己是自己的父进程、环：不死循环
        let loop = [RunProcess(pid: 1, ppid: 2, name: "a"), RunProcess(pid: 2, ppid: 1, name: "b")]
        XCTAssertEqual(Set(ProcessTree.flatten(loop).map(\.process.pid)), [1, 2])
    }
}

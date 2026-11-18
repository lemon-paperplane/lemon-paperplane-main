import XCTest
@testable import PaperplaneKit

final class ModelDecodingTests: XCTestCase {
    func fixture(_ name: String) throws -> Data {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures/\(name)", withExtension: nil))
        return try Data(contentsOf: url)
    }

    func decode<T: Decodable>(_ name: String, as: T.Type = T.self) throws -> T {
        try APIClient.decodeEnvelope(try fixture(name), status: 200)
    }

    func testSystemInfo() throws {
        let info: SystemInfo = try decode("system.info.json")
        XCTAssertEqual(info.version, "0.1.0")
        XCTAssertEqual(info.pluginProcs, 2)
        XCTAssertEqual(info.memoryMB, 21.4, accuracy: 0.001)
        // 纳秒精度、带时区
        let started = try XCTUnwrap(info.startedAt)
        XCTAssertEqual(started.timeIntervalSince1970, 1_794_965_952.123, accuracy: 0.01)
    }

    func testWorkspacesWithNullSliceAndZeroTime() throws {
        let ws: [Workspace] = try decode("workspace.list.json")
        XCTAssertEqual(ws.count, 2)
        XCTAssertEqual(ws[0].projectIds, ["p1", "p2"])
        XCTAssertEqual(ws[1].projectIds, [])
        XCTAssertTrue(GoTime.isZero(ws[1].createdAt))
        XCTAssertFalse(GoTime.isZero(ws[0].createdAt))
    }

    func testProjectSummaries() throws {
        let list: [ProjectSummary] = try decode("project.list.json")
        XCTAssertEqual(list.count, 2)
        let p = list[0]
        XCTAssertEqual(p.facets.map(\.id), ["gradle", "spring-boot"])
        XCTAssertEqual(p.runs.first?.urls, ["http://localhost:8080"])
        XCTAssertEqual(p.vcs?.ahead, 1)
        XCTAssertEqual(p.vcs?.conflicts, 0)
        XCTAssertEqual(p.missingToolchains, ["jdk 17"])
        XCTAssertTrue(p.hasActiveRun)
        let q = list[1]
        XCTAssertEqual(q.facets, [])
        XCTAssertEqual(q.configs, [])
        XCTAssertNil(q.vcs)
        XCTAssertFalse(q.hasActiveRun)
    }

    func testProjectDetail() throws {
        let p: Project = try decode("project.get.json")
        XCTAssertEqual(p.modules.count, 1)
        let m = p.modules[0]
        XCTAssertEqual(m.facets.count, 3)
        XCTAssertEqual(m.facets[1].data?["version"]?.stringValue, "8.12")
        XCTAssertEqual(m.analysis["gradle"]?.info.first?.value, "8.12")
        XCTAssertEqual(m.analysis["spring-boot"]?.info, [])
        XCTAssertEqual(m.optionValues["profiles"]?.map(\.value), ["dev", "prod"])
        XCTAssertEqual(m.problems.count, 1)
        XCTAssertTrue(m.toolchains["gradle"]?.missing == true)
        XCTAssertEqual(m.runConfigs.count, 2)
        let c1 = m.runConfigs[0]
        XCTAssertTrue(c1.default)
        XCTAssertEqual(c1.options["port"]?.intValue, 8080)
        XCTAssertEqual(c1.options["profiles"]?.stringArray, ["dev"])
        XCTAssertEqual(m.runConfigs[1].options, [:])
        XCTAssertTrue(m.runConfigs[1].edited)
        XCTAssertEqual(p.settings?.toolchains["jdk"]?.constraint, "17")
        XCTAssertEqual(p.allRunConfigs.count, 2)
        XCTAssertEqual(p.pluginIds, ["java", "gradle", "spring-boot"])
        XCTAssertTrue(GoTime.isZero(p.analyzedAt))
    }

    func testRuns() throws {
        let runs: [Run] = try decode("run.list.json")
        XCTAssertEqual(runs.count, 2)
        XCTAssertEqual(runs[0].diagnoses.first?.fixes.first?.params["port"], "8080")
        XCTAssertEqual(runs[0].diagnoses.first?.fixes.first?.recommended, true)
        XCTAssertNotNil(runs[0].readyAt)
        XCTAssertNil(runs[0].exitCode)
        XCTAssertEqual(runs[1].exitCode, 1)
        XCTAssertTrue(runs[0].isActive)
        XCTAssertFalse(runs[1].isActive)
    }

    func testTasks() throws {
        let tasks: [TaskInfo] = try decode("task.list.json")
        XCTAssertEqual(tasks[0].percent, 22.1, accuracy: 0.001)
        XCTAssertTrue(tasks[0].isRunning)
        XCTAssertEqual(tasks[1].result?["message"]?.stringValue, "已清理")
    }

    func testContributions() throws {
        let c: Contributions = try decode("ui.contributions.json")
        XCTAssertEqual(c.runConfigTypes.first?.options.count, 4)
        XCTAssertEqual(c.runConfigTypes.first?.options[3].default?.intValue, 8080)
        XCTAssertEqual(c.views(at: ViewLocation.runPanel).first?.runState, "running")
        XCTAssertTrue(c.views(at: ViewLocation.sidebar).first?.isWebview == true)
        // locations 为空的动作默认挂 project.menu
        XCTAssertEqual(c.actions(at: ActionLocation.projectMenu).map(\.id), ["clean", "deps"])
        XCTAssertEqual(c.actions(at: ActionLocation.cardMenu).map(\.id), ["clean"])
        XCTAssertEqual(c.settings.first?.values["daemon"]?.boolValue, true)
    }

    func testPlugins() throws {
        let p: [PluginInfo] = try decode("plugin.list.json")
        XCTAssertEqual(p[0].contributes?.diagnostics, 12)
        XCTAssertEqual(p[1].error, "engine mismatch")
        XCTAssertEqual(p[1].contributes?.facets, [])
    }

    func testSettings() throws {
        let s: AppSettings = try decode("settings.get.json")
        XCTAssertEqual(s.theme, "dark")
        XCTAssertEqual(s.pluginSettings["gradle"]?["daemon"], .bool(true))
        XCTAssertEqual(s.disabledPlugins, [])
        XCTAssertNil(s.terminal)
    }

    func testViewRender() throws {
        let r: ViewRenderResult = try decode("view.render.json")
        XCTAssertEqual(r.refreshMs, 5000)
        XCTAssertEqual(r.root.children.count, 8)
        let table = r.root.children[2]
        XCTAssertTrue(table.selectable)
        XCTAssertEqual(table.columns[1].mono, true)
        XCTAssertEqual(table.rows[0].actions.first?.style, "link")
        XCTAssertEqual(r.root.children[4].value, -1)
        XCTAssertEqual(r.root.children[5].values?["name"], .string("x"))
        XCTAssertEqual(r.root.children[6].rows[0].children.count, 1)
        XCTAssertEqual(r.state?["page"]?.intValue, 1)
    }

    func testErrorAndNullEnvelope() throws {
        XCTAssertThrowsError(try APIClient.decodeEnvelope(try fixture("error.json"), status: 400, as: Project.self)) { e in
            let err = e as? APIError
            XCTAssertEqual(err?.code, "user_error")
            XCTAssertEqual(err?.message, "工程不存在")
        }
        let list: [ProjectSummary] = try APIClient.decodeEnvelope(try fixture("null.json"), status: 200)
        XCTAssertEqual(list, [])
        let none: EmptyResult = try APIClient.decodeEnvelope(try fixture("null.json"), status: 200)
        XCTAssertEqual(none, EmptyResult())
        let v: JSONValue = try APIClient.decodeEnvelope(try fixture("null.json"), status: 200)
        XCTAssertEqual(v, .null)
        // 非 JSON 的错误响应
        XCTAssertThrowsError(try APIClient.decodeEnvelope(Data("unauthorized".utf8), status: 401, as: JSONValue.self)) { e in
            XCTAssertEqual((e as? APIError)?.code, "unauthorized")
        }
    }

    func testSSEEventsFixture() throws {
        let text = String(decoding: try fixture("events.sse.txt"), as: UTF8.self)
        var splitter = LineSplitter()
        var parser = SSEParser()
        var events: [DaemonEvent] = []
        for line in splitter.append(Data(text.utf8)) {
            if let m = parser.feed(line) {
                events.append(try JSONCoding.decoder.decode(DaemonEvent.self, from: Data(m.data.utf8)))
            }
        }
        XCTAssertEqual(events.map(\.type), ["hello", "stats", "prompt", "project.removed", "toolchains.changed"])
        let hello = try events[0].decode(SystemInfo.self)
        XCTAssertEqual(hello.locale, "en")
        let stats = try events[1].decode([RunBrief].self)
        XCTAssertEqual(stats.first?.cpu, 2.5)
        let prompt = try events[2].decode(Prompt.self)
        XCTAssertEqual(prompt.fields.count, 2)
        XCTAssertTrue(prompt.fields[1].isSecret)
        XCTAssertFalse(prompt.fields[0].isSecret)
        XCTAssertEqual(try events[3].decode([String: String].self)["id"], "p9")
        XCTAssertTrue(events[4].data == nil || events[4].data == .null)
    }

    func testRunConfigRoundTrip() throws {
        let p: Project = try decode("project.get.json")
        let c = p.modules[0].runConfigs[0]
        let data = try JSONCoding.encoder.encode(c)
        let back = try JSONCoding.decoder.decode(RunConfig.self, from: data)
        XCTAssertEqual(back, c)
        // 平铺：没有嵌套的 RunConfiguration 对象
        let obj = try JSONCoding.decoder.decode(JSONValue.self, from: data)
        XCTAssertEqual(obj["mainClass"], nil)
        XCTAssertEqual(obj["options"]?["mainClass"], .string("com.example.App"))
        XCTAssertEqual(obj["default"], .bool(true))
    }

    func testJSONValueHelpers() throws {
        let v: JSONValue = ["a": 1, "b": [1, 2], "c": "x", "d": nil, "e": true]
        XCTAssertEqual(v["a"]?.intValue, 1)
        XCTAssertEqual(v["b"]?.arrayValue?.count, 2)
        XCTAssertEqual(v["d"]?.isNull, true)
        XCTAssertEqual(v["e"]?.boolValue, true)
        XCTAssertEqual(JSONValue.number(3).jsonString, "3")
        XCTAssertEqual(JSONValue.number(2.5).jsonString, "2.5")
        let o = jsonObject(["x": .string("1"), "y": nil])
        XCTAssertEqual(o.objectValue?.keys.sorted(), ["x"])
        struct A: Encodable { var path: String; var context: Int? }
        let flat = try JSONValue(encoding: A(path: "a.txt", context: nil))
        XCTAssertEqual(flat, ["path": "a.txt"])
    }

    func testVCSModels() throws {
        let json = """
        {"commits":[{"hash":"abc","short":"abc","parents":["def"],"author":"me","date":"2026-11-18T09:00:00+08:00","subject":"init","refs":[{"name":"main","kind":"head"}],"graph":{"lane":0,"lanes":2,"edges":[{"from":0,"to":0,"color":0},{"from":1,"to":0,"color":1}],"color":0}}],"hasMore":true}
        """
        let log = try JSONCoding.decoder.decode(VCSLog.self, from: Data(json.utf8))
        XCTAssertEqual(log.commits.first?.graph?.edges.count, 2)
        XCTAssertNotNil(log.commits.first?.parsedDate)
        let args = try JSONValue(encoding: VCSSubmoduleUpdateArgs(initialize: true))
        XCTAssertEqual(args["init"], .bool(true))
    }

    /// 插件给的 nil 切片会变成 null（如 pom 里没写 profiles 时的 maven.profiles）：不能让整个导入结果解析失败
    func testOptionValuesTolerateNullEntries() throws {
        let json = #"""
        {"info": null, "problems": [], "optionValues": {"maven.profiles": null, "jvm.modules": [{"value": ":", "label": "root"}]},
         "at": "2026-11-18T15:00:00.123456789+08:00"}
        """#
        let a = try JSONCoding.decoder.decode(ModuleAnalysis.self, from: Data(json.utf8))
        XCTAssertEqual(a.optionValues["maven.profiles"], [])
        XCTAssertEqual(a.optionValues["jvm.modules"]?.count, 1)
        XCTAssertEqual(a.info, [])

        // 值不是数组/字典时，null 那一项直接丢掉
        struct Box: Codable { @DefDict var m: [String: String] }
        let b = try JSONCoding.decoder.decode(Box.self, from: Data(#"{"m": {"a": "x", "b": null}}"#.utf8))
        XCTAssertEqual(b.m, ["a": "x"])
    }
}

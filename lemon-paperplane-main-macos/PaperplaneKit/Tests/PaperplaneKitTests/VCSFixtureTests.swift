import XCTest
@testable import PaperplaneKit

/// 用真实 daemon + git 插件返回的版本控制响应核对模型解码（夹具在临时仓库上抓取，路径已脱敏，见 Fixtures/real/vcs.*）。
final class VCSFixtureTests: XCTestCase {
    func decode<T: Decodable>(_ name: String, _ t: T.Type) throws -> T {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures/real/\(name)", withExtension: nil), name)
        return try APIClient.decodeEnvelope(try Data(contentsOf: url), status: 200)
    }

    func testStatus() throws {
        let s = try decode("vcs.status.json", VCSStatus.self)
        XCTAssertEqual(s.branch, "main")
        XCTAssertEqual(s.upstream, "origin/main")
        XCTAssertEqual(s.ahead, 2)
        XCTAssertEqual(s.untrackedCount, 2)
        XCTAssertEqual(s.headHash?.count, 40)
        XCTAssertFalse(s.unborn)
        XCTAssertNil(s.operationDetail)
        let staged = try XCTUnwrap(s.files.first { $0.path == "src/staged.txt" })
        XCTAssertEqual(staged.indexStatus, "A")
        XCTAssertTrue(staged.staged)
        XCTAssertNil(staged.worktreeStatus)
        XCTAssertTrue(s.files.contains { $0.path == "docs/中文 名.md" && $0.isUntracked })
        XCTAssertTrue(s.files.first { $0.path == "logo.png" }?.binary == true)
    }

    func testConflictStatus() throws {
        let s = try decode("vcs.status.conflict.json", VCSStatus.self)
        XCTAssertEqual(s.operation, "merge")
        XCTAssertEqual(s.conflicts, 3)
        XCTAssertEqual(s.operationDetail?.op, "merge")
        XCTAssertEqual(s.operationDetail?.message, "Merge branch 'other'")
        XCTAssertEqual(s.files.filter(\.conflicted).compactMap(\.conflictType).sorted(), ["AA", "DU", "UU"])
        // SDK 里 operationDetail 是字符串：两种形态都要能解
        let str = try JSONCoding.decoder.decode(VCSStatus.self, from: Data(#"{"branch":"x","operationDetail":"rebase 2/5"}"#.utf8))
        XCTAssertEqual(str.operationDetail?.message, "rebase 2/5")
    }

    func testDiff() throws {
        let d = try decode("vcs.diff.json", VCSDiff.self)
        XCTAssertEqual(d.mode, "head")
        XCTAssertEqual(d.context, 3)
        XCTAssertEqual(d.status, "M")
        XCTAssertEqual(d.additions, 2)
        XCTAssertEqual(d.hunks.first?.lines.count, 8)
        XCTAssertEqual(d.hunks.first?.lines[1].kind, "-")
        XCTAssertEqual(d.hunks.first?.lines[1].new, 0)
        let b = try decode("vcs.diff.binary.json", VCSDiff.self)
        XCTAssertTrue(b.binary)
        XCTAssertTrue(b.hunks.isEmpty)
    }

    func testLogShowBranches() throws {
        let log = try decode("vcs.log.json", VCSLog.self)
        XCTAssertEqual(log.commits.count, 5)
        XCTAssertTrue(log.hasMore)
        XCTAssertEqual(log.graph, true)
        let merge = try XCTUnwrap(log.commits.first { $0.parents.count == 2 })
        XCTAssertEqual(merge.graph?.edges.count, 2)
        XCTAssertTrue(merge.refs.contains { $0.kind == "tag" })
        XCTAssertEqual(log.commits[0].refs.first?.kind, "head")

        let show = try decode("vcs.show.json", VCSCommitDetail.self)
        XCTAssertEqual(show.commit.parents.count, 2)
        XCTAssertEqual(show.message, "Merge branch 'feature/login'")
        XCTAssertEqual(show.parent, 1)
        XCTAssertEqual(show.files.first?.status, "A")

        let br = try decode("vcs.branches.json", VCSBranches.self)
        XCTAssertEqual(br.current, "main")
        XCTAssertEqual(br.recent, ["main", "feature/login"])
        XCTAssertEqual(br.local.first { $0.current }?.ahead, 2)
        XCTAssertEqual(br.remote.first?.remote, "origin")

        let hist = try decode("vcs.fileHistory.json", VCSLog.self)
        XCTAssertEqual(hist.commits.first?.path, "src/a.txt")
        XCTAssertNotNil(hist.commits.first?.pathStatus)

        let cmp = try decode("vcs.compare.json", VCSCompare.self)
        XCTAssertFalse(cmp.ahead.isEmpty)
        XCTAssertNotNil(cmp.mergeBase)
    }

    func testRemoteAndResults() throws {
        let push = try decode("vcs.push.json", VCSOpResult.self)
        XCTAssertTrue(push.ok)
        XCTAssertNotNil(push.taskId)
        let fetch = try decode("vcs.fetch.json", VCSOpResult.self)
        XCTAssertEqual(fetch.reason, VCSReason.upToDate)
        let pull = try decode("vcs.pull.json", VCSOpResult.self)
        XCTAssertEqual(pull.reason, VCSReason.upToDate)
        let pv = try decode("vcs.pushPreview.json", VCSPushPreview.self)
        XCTAssertEqual(pv.remote, "origin")
        XCTAssertEqual(pv.commits.count, 2)
        XCTAssertEqual(pv.behind, 0)
        let markers = try decode("vcs.resolve.markers.json", VCSOpResult.self)
        XCTAssertFalse(markers.ok)
        XCTAssertEqual(markers.reason, VCSReason.markersRemain)
        XCTAssertEqual(markers.files, ["both.txt"])
        let cont = try decode("vcs.continue.unresolved.json", VCSOpResult.self)
        XCTAssertEqual(cont.reason, VCSReason.unresolvedConflicts)
        XCTAssertEqual(cont.conflicts.count, 3)
        XCTAssertTrue(try decode("vcs.stashSave.json", VCSOpResult.self).ok)
    }

    func testExtensions() throws {
        let stashes = try decode("vcs.stashList.json", VCSStashList.self)
        XCTAssertEqual(stashes.stashes.first?.message, "wip test")
        XCTAssertNotNil(stashes.stashes.first?.hash)
        XCTAssertNotNil(stashes.stashes.first?.parsedDate)
        let show = try decode("vcs.stashShow.json", VCSStashShow.self)
        XCTAssertEqual(show.files.count, 4)
        XCTAssertEqual(show.untracked.count, 2)
        let fc = try decode("vcs.fileContent.json", VCSFileContent.self)
        XCTAssertTrue(fc.binary)
        XCTAssertTrue(fc.exists)
        XCTAssertEqual(fc.data?.prefix(4), Data([0x89, 0x50, 0x4E, 0x47]))
        let cb = try decode("vcs.containingBranches.json", VCSContainingBranches.self)
        XCTAssertTrue(cb.refs.contains { $0.kind == "tag" })
        let tools = try decode("vcs.mergeTools.json", VCSMergeTools.self)
        XCTAssertFalse(tools.tools.isEmpty)
        let cfg = try decode("vcs.configGet.json", VCSConfig.self)
        XCTAssertEqual(cfg.scopes["user.name"], "local")
        let roots = try decode("vcs.roots.json", [VCSRoot].self)
        XCTAssertEqual(roots.first?.relPath, ".")
    }

    func testOverview() throws {
        let list = try decode("vcs.overview.json", [VCSRepoOverview].self)
        XCTAssertEqual(list.count, 4)
        XCTAssertEqual(Set(list.map(\.root)).count, list.count)
        let demo = try XCTUnwrap(list.first { $0.name == "demo" })
        XCTAssertEqual(demo.projectName, "demo")
        XCTAssertEqual(demo.status?.branch, "main")
        XCTAssertFalse(demo.status?.files.isEmpty ?? true)
        XCTAssertNil(demo.error)
        let conflict = try XCTUnwrap(list.first { $0.name == "conflict" })
        XCTAssertEqual(conflict.status?.operation, "merge")
        XCTAssertEqual(conflict.status?.operationDetail?.op, "merge")
        XCTAssertGreaterThan(conflict.status?.conflicts ?? 0, 0)
        let nested = try XCTUnwrap(list.first { $0.name == "nested" })
        XCTAssertEqual(nested.relPath, "nested")
        XCTAssertEqual(nested.projectId, conflict.projectId)
        // 取状态失败的仓库：没有 status，有 error
        let failed = try JSONCoding.decoder.decode(VCSRepoOverview.self, from: Data(#"{"projectId":"p","projectName":"P","root":"/r","relPath":"","name":"r","vcs":"git","error":"boom"}"#.utf8))
        XCTAssertNil(failed.status)
        XCTAssertEqual(failed.error, "boom")
        XCTAssertEqual(try decode("vcs.overview.json", [VCSRepoOverview].self).first?.id, list.first?.root)
    }

    func testArgsEncoding() throws {
        let h = try JSONValue(encoding: VCSHunksArgs(path: "a.txt", hunks: [0, 2], context: 3, mode: VCSDiffMode.head, headers: ["@@ -1 +1 @@"]))
        XCTAssertEqual(h["mode"], .string("head"))
        XCTAssertEqual(h["headers"], .array([.string("@@ -1 +1 @@")]))
        let c = try JSONValue(encoding: VCSCommitArgs(message: "m", paths: ["a"], hunks: ["b": [1]], signOff: true))
        XCTAssertEqual(c["hunks"], .object(["b": .array([.number(1)])]))
        XCTAssertNil(c["amend"])
        let d = try JSONValue(encoding: VCSDiscardArgs(paths: ["x"], keepNew: true))
        XCTAssertEqual(d["keepNew"], .bool(true))
        XCTAssertEqual(APIClient.vcsTimeout(VCSOp.push), 3600)
        XCTAssertNil(APIClient.vcsTimeout(VCSOp.status))
    }
}

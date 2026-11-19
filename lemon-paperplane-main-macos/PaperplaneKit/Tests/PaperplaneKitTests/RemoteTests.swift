import XCTest
@testable import PaperplaneKit

final class ServerPathTests: XCTestCase {
    func testPosix() {
        let p = ServerPath.posix
        XCTAssertEqual(p.join("/Users/me", "Code"), "/Users/me/Code")
        XCTAssertEqual(p.join("/Users/me/", "Code/x"), "/Users/me/Code/x")
        XCTAssertEqual(p.join("/", "tmp"), "/tmp")
        XCTAssertEqual(p.lastComponent("/Users/me/Code/"), "Code")
        XCTAssertEqual(p.lastComponent("/"), "/")
        XCTAssertEqual(p.parent("/Users/me"), "/Users")
        XCTAssertEqual(p.parent("/Users"), "/")
        XCTAssertNil(p.parent("/"))
        XCTAssertEqual(p.normalize("/a/b//"), "/a/b")
        XCTAssertTrue(p.isAbsolute("/a"))
        XCTAssertFalse(p.isAbsolute("a/b"))
        XCTAssertEqual(p.relative("/p/src/x", to: "/p"), "src/x")
        XCTAssertEqual(p.relative("/p", to: "/p/"), "")
        XCTAssertNil(p.relative("/px/a", to: "/p"))
        XCTAssertEqual(p.abbreviate("/Users/me/Code", home: "/Users/me"), "~/Code")
        XCTAssertEqual(p.abbreviate("/Users/meme", home: "/Users/me"), "/Users/meme")
    }

    func testWindows() {
        let p = ServerPath.windows
        XCTAssertEqual(p.join("C:\\Users\\me", "Code"), "C:\\Users\\me\\Code")
        XCTAssertEqual(p.join("C:\\", "Code"), "C:\\Code")
        XCTAssertEqual(p.join("C:\\Users\\me", "a/b"), "C:\\Users\\me\\a\\b")
        XCTAssertEqual(p.lastComponent("C:\\Users\\me\\"), "me")
        XCTAssertEqual(p.lastComponent("C:\\"), "C:\\")
        XCTAssertEqual(p.parent("C:\\Users"), "C:\\")
        XCTAssertNil(p.parent("C:\\"))
        XCTAssertEqual(p.normalize("D:"), "D:\\")
        XCTAssertTrue(p.isAbsolute("C:\\x"))
        XCTAssertTrue(p.isAbsolute("\\\\nas\\share"))
        XCTAssertFalse(p.isAbsolute("x\\y"))
        XCTAssertTrue(p.same("c:\\Code\\A", "C:\\code\\a\\"))
        XCTAssertEqual(p.relative("C:\\p\\src\\x", to: "c:\\P"), "src/x")
        XCTAssertEqual(p.absolute("src/main.go", root: "C:\\p"), "C:\\p\\src\\main.go")
        XCTAssertEqual(p.abbreviate("C:\\Users\\me\\Code", home: "C:\\Users\\me"), "~\\Code")
    }

    func testFormatUsesServerHome() {
        let saved = (Format.serverHome, Format.serverPath)
        defer { (Format.serverHome, Format.serverPath) = saved }
        Format.serverHome = "C:\\Users\\me"
        Format.serverPath = .windows
        XCTAssertEqual(Format.abbreviatePath("C:\\Users\\me\\x"), "~\\x")
        Format.serverHome = "/home/u"
        Format.serverPath = .posix
        XCTAssertEqual(Format.abbreviatePath("/home/u/x"), "~/x")
        XCTAssertEqual(Format.abbreviatePath("/Users/me/x", home: "/Users/me"), "~/x")
    }
}

final class ServerURLTests: XCTestCase {
    func testParse() {
        let p = ServerURL.parse("http://localhost:8080/app?x=1")
        XCTAssertEqual(p?.host, "localhost")
        XCTAssertEqual(p?.port, 8080)
        XCTAssertEqual(p?.rest, "/app?x=1")
        XCTAssertEqual(ServerURL.parse("https://[fe80::1]:47290")?.host, "[fe80::1]")
        XCTAssertEqual(ServerURL.parse("http://127.0.0.1")?.effectivePort, 80)
        XCTAssertNil(ServerURL.parse("localhost:8080"))
    }

    func testDirectAndForward() {
        XCTAssertEqual(ServerURL.direct("http://localhost:8080/app", serverHost: "192.168.1.5"), "http://192.168.1.5:8080/app")
        XCTAssertEqual(ServerURL.direct("http://0.0.0.0:3000", serverHost: "nas.local"), "http://nas.local:3000")
        XCTAssertEqual(ServerURL.direct("http://127.0.0.1:3000/", serverHost: "fe80::1"), "http://[fe80::1]:3000/")
        XCTAssertEqual(ServerURL.direct("http://example.com:3000/", serverHost: "nas"), "http://example.com:3000/")
        XCTAssertEqual(ServerURL.loopbackPort("http://localhost:5173/"), 5173)
        XCTAssertNil(ServerURL.loopbackPort("http://example.com:5173/"))
        XCTAssertEqual(ServerURL.loopbackPort("http://app.localhost:3000"), 3000)
        XCTAssertEqual(ServerURL.forwarded("http://127.0.0.1:8080/a", localPort: 18080), "http://localhost:18080/a")
        XCTAssertEqual(ServerURL.host(of: "https://nas.local:47290"), "nas.local")
        XCTAssertEqual(ServerURL.host(of: "nas.local:47290"), "nas.local")
        XCTAssertEqual(ServerURL.normalize("HTTPS://Nas.Local:47290/"), "https://nas.local:47290")
        XCTAssertEqual(ServerURL.normalizeAddress("192.168.1.10"), "https://192.168.1.10:47290")
        XCTAssertEqual(ServerURL.normalizeAddress(" nas.local:5000 "), "https://nas.local:5000")
        XCTAssertEqual(ServerURL.normalizeAddress("https://example.com/"), "https://example.com:443")
        XCTAssertEqual(ServerURL.normalizeAddress("https://example.com:47290/x"), "https://example.com:47290")
        XCTAssertEqual(ServerURL.normalizeAddress("fe80::1"), "https://[fe80::1]:47290")
        XCTAssertEqual(ServerURL.normalizeAddress("[fe80::1]:9"), "https://[fe80::1]:9")
        XCTAssertNil(ServerURL.normalizeAddress("http://x"))
        XCTAssertNil(ServerURL.normalizeAddress("host:99999"))
        XCTAssertNil(ServerURL.normalizeAddress(""))
    }

    func testRebasedRelativeWebviewURL() {
        // 远程服务器只给相对路径（不带它自己的令牌）
        let c = APIClient(baseURL: URL(string: "http://127.0.0.1:55001")!, token: "gw")
        let u = c.rebasedWebviewURL(URL(string: "/plugins/p/index.html?projectId=a&moduleId=&runId=")!)
        let comps = URLComponents(url: u, resolvingAgainstBaseURL: false)!
        XCTAssertEqual(comps.scheme, "http")
        XCTAssertEqual(comps.host, "127.0.0.1")
        XCTAssertEqual(comps.port, 55001)
        XCTAssertEqual(comps.path, "/plugins/p/index.html")
        XCTAssertEqual(comps.queryItems?.first { $0.name == "token" }?.value, "gw")
        XCTAssertEqual(comps.queryItems?.first { $0.name == "projectId" }?.value, "a")
    }

    func testRebasedWebviewURL() {
        let c = APIClient(baseURL: URL(string: "http://127.0.0.1:55001")!, token: "gw")
        let raw = URL(string: "http://127.0.0.1:47123/plugins/p/index.html?token=remote&projectId=a&moduleId=&runId=")!
        let u = c.rebasedWebviewURL(raw)
        let comps = URLComponents(url: u, resolvingAgainstBaseURL: false)!
        XCTAssertEqual(comps.port, 55001)
        XCTAssertEqual(comps.path, "/plugins/p/index.html")
        XCTAssertEqual(comps.queryItems?.first { $0.name == "token" }?.value, "gw")
        XCTAssertEqual(comps.queryItems?.filter { $0.name == "token" }.count, 1)
        XCTAssertEqual(comps.queryItems?.first { $0.name == "projectId" }?.value, "a")
    }
}

final class RemoteModelTests: XCTestCase {
    private func decode<T: Decodable>(_ json: String, as: T.Type = T.self) throws -> T {
        try JSONCoding.decoder.decode(T.self, from: Data(json.utf8))
    }

    func testStatusAndKeys() throws {
        let st: RemoteStatus = try decode("""
        {"enabled":true,"running":true,"listen":"0.0.0.0:47290","port":47290,"addresses":["https://192.168.1.5:47290"],
         "fingerprint":"sha256:ab12","fingerprintDisplay":"AB12","customCert":false,"passwordLogin":true,"autostart":false,
         "autostartSupported":true,"allowCIDRs":null,"keys":2,"users":1,"hostname":"mbp"}
        """)
        XCTAssertTrue(st.running)
        XCTAssertEqual(st.port, 47290)
        XCTAssertEqual(st.allowCIDRs, [])
        let keys: [RemoteKey] = try decode("""
        [{"id":"1a2b3c4d","name":"MacBook","kind":"device","user":"me","createdAt":"2026-11-19T10:00:00.123456789+08:00",
          "lastUsedAt":"2026-11-19T11:00:00Z","lastIP":"10.0.0.2","current":true},
         {"id":"9","name":"k","kind":"key","createdAt":"2026-11-19T10:00:00Z","current":false}]
        """)
        XCTAssertTrue(keys[0].isDevice)
        XCTAssertTrue(keys[0].current)
        XCTAssertNotNil(keys[0].lastUsedAt)
        XCTAssertNil(keys[1].lastUsedAt)
    }

    func testGatewayAndBrowse() throws {
        let o: GatewayOpen = try decode("""
        {"id":"abc","port":53001,"token":"t","info":{"version":"0.1","os":"windows","arch":"amd64","hostname":"WIN",
         "home":"C:\\\\Users\\\\me","pathSeparator":"\\\\","apiVersion":1}}
        """)
        XCTAssertEqual(o.baseURL.absoluteString, "http://127.0.0.1:53001")
        XCTAssertEqual(o.info?.pathSeparator, "\\")
        XCTAssertEqual(o.info?.home, "C:\\Users\\me")
        let b: FSBrowseResult = try decode("""
        {"path":"/Users/me","parent":"/Users","home":"/Users/me","sep":"/","roots":["/"],
         "entries":[{"name":"Code","path":"/Users/me/Code","dir":true,"hidden":false,"size":96,"modTime":"2026-11-19T10:00:00Z"}]}
        """)
        XCTAssertEqual(b.entries.first?.dir, true)
        let probe: GatewayProbe = try decode("""
        {"url":"https://h:47290","fingerprint":"sha256:aa","fingerprintDisplay":"AA","trusted":false,
         "hello":{"name":"h","hostname":"h","version":"1","apiVersion":1,"os":"linux","arch":"arm64","passwordLogin":true}}
        """)
        XCTAssertEqual(probe.hello?.os, "linux")
    }

    func testRemoteLinksDefaults() throws {
        let p: ProjectSummary = try decode("""
        {"id":"p","name":"x","remoteLinks":{"c1":{"forward":true,"direct":false}}}
        """)
        XCTAssertEqual(p.remoteLinks(for: "c1"), RemoteLinks(forward: true, direct: false))
        XCTAssertEqual(p.remoteLinks(for: "c2"), .both)
        let q: ProjectSummary = try decode(#"{"id":"p","name":"x"}"#)
        XCTAssertTrue(q.remoteLinks.isEmpty)
    }

    func testSavedServers() {
        let s = SavedServer(id: "1", name: "NAS", url: "https://nas.local:47290", fingerprint: "sha256:abcdef0123456789", trusted: false, os: "linux")
        let data = SavedServerList.encode([s])
        XCTAssertEqual(SavedServerList.decode(data), [s])
        XCTAssertEqual(SavedServerList.decode(nil), [])
        XCTAssertEqual(s.shortFingerprint, "ABCDEF01")
        XCTAssertEqual(s.host, "nas.local")
        XCTAssertEqual(s.displayAddress, "nas.local:47290")
        XCTAssertTrue(s.isSameServer(url: "https://NAS.local:47290/", fingerprint: "SHA256:ABCDEF0123456789"))
        XCTAssertFalse(s.isSameServer(url: "https://nas.local:47290", fingerprint: "sha256:ffff"))
    }

    func testRemoteErrorCodes() throws {
        do {
            let _: JSONValue = try APIClient.decodeEnvelope(Data(#"{"error":{"code":"remote_unauthorized","message":"x"}}"#.utf8), status: 403)
            XCTFail("应当报错")
        } catch let e as APIError {
            XCTAssertEqual(e.code, APIError.codeRemoteUnauthorized)
            XCTAssertTrue(e.isRemoteFailure)
            XCTAssertEqual(e.status, 403)
        }
        do {
            let _: JSONValue = try APIClient.decodeEnvelope(Data(#"{"error":{"code":"fingerprint_mismatch","message":"x","data":{"fingerprint":"sha256:zz"}}}"#.utf8), status: 409)
            XCTFail("应当报错")
        } catch let e as APIError {
            XCTAssertEqual(e.dataString("fingerprint"), "sha256:zz")
        }
    }
}

/// 对真实内核的远程联调测试。默认跳过；需要：
/// PAPERPLANE_LIVE_DATA_DIR=<客户端内核数据目录>、PAPERPLANE_LIVE_REMOTE_CODE=<服务器生成的连接码>
final class LiveRemoteTests: XCTestCase {
    func testGatewayRoundTrip() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let dir = env["PAPERPLANE_LIVE_DATA_DIR"], !dir.isEmpty, let code = env["PAPERPLANE_LIVE_REMOTE_CODE"], !code.isEmpty else {
            throw XCTSkip("未设置 PAPERPLANE_LIVE_DATA_DIR / PAPERPLANE_LIVE_REMOTE_CODE")
        }
        guard let (local, _) = await DaemonManager(dataDir: URL(fileURLWithPath: dir)).probe() else { return XCTFail("客户端内核不可达") }
        let decoded = try await local.gatewayDecode(code: code)
        XCTAssertFalse(decoded.urls.isEmpty)
        var opened: GatewayOpen?
        var openedURL = ""
        for u in decoded.urls {
            if let o = try? await local.gatewayOpen(url: u, fingerprint: decoded.fingerprint, trusted: false, token: decoded.token) {
                opened = o
                openedURL = u
                break
            }
        }
        guard let gw = opened else { return XCTFail("所有地址都连不上") }
        XCTAssertEqual(gw.info?.apiVersion, 1)
        XCTAssertFalse(gw.info?.hostname.isEmpty ?? true)
        let remote = APIClient(baseURL: gw.baseURL, token: gw.token)
        let info = try await remote.systemInfo()
        XCTAssertFalse(info.pathSeparator.isEmpty)
        _ = try await remote.workspaces()
        _ = try await remote.projects()
        let st = try await remote.remoteStatus()
        XCTAssertTrue(st.running)
        let keys = try await remote.remoteKeys()
        XCTAssertTrue(keys.contains { $0.current })
        _ = try await remote.remoteAudit(limit: 50)
        let home = try await remote.fsBrowse(path: nil)
        XCTAssertEqual(home.path, info.home)
        // 远程不能调 gateway.* / system.shutdown
        do {
            _ = try await remote.gatewayDecode(code: code)
            XCTFail("远程调用 gateway.* 应被拒绝")
        } catch {}
        // 同一 url+token 复用同一个网关
        let again = try await local.gatewayOpen(url: openedURL, fingerprint: decoded.fingerprint, trusted: false, token: decoded.token)
        XCTAssertEqual(again.id, gw.id)
        XCTAssertEqual(again.port, gw.port)
        // 错误的指纹
        do {
            _ = try await local.gatewayOpen(url: openedURL, fingerprint: "sha256:" + String(repeating: "0", count: 64), trusted: false, token: decoded.token)
            XCTFail("指纹不符应报错")
        } catch let e as APIError {
            XCTAssertTrue(e.code == APIError.codeFingerprintMismatch || e.code == APIError.codeUnreachable, e.code)
        }
        // 错误的密钥
        do {
            _ = try await local.gatewayOpen(url: openedURL, fingerprint: decoded.fingerprint, trusted: false, token: "ppk_00000000_x")
            XCTFail("密钥不对应报错")
        } catch let e as APIError {
            XCTAssertEqual(e.code, APIError.codeUnauthorized)
        }
        try await local.gatewayClose(gw.id)
    }

    /// 吊销正在用的密钥后，经网关调用收到 403 remote_unauthorized（不是 401，界面不能当成本机令牌失效）；
    /// 端口转发把服务器 localhost 的端口转到本机。需要额外设置 PAPERPLANE_LIVE_FORWARD_PORT=<服务器上在监听的端口>
    func testRevokedKeyAndForward() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let dir = env["PAPERPLANE_LIVE_DATA_DIR"], !dir.isEmpty, let code = env["PAPERPLANE_LIVE_REMOTE_CODE"], !code.isEmpty else {
            throw XCTSkip("未设置 PAPERPLANE_LIVE_DATA_DIR / PAPERPLANE_LIVE_REMOTE_CODE")
        }
        guard let (local, _) = await DaemonManager(dataDir: URL(fileURLWithPath: dir)).probe() else { return XCTFail("客户端内核不可达") }
        let d = try await local.gatewayDecode(code: code)
        let admin = try await local.gatewayOpen(url: d.urls[0], fingerprint: d.fingerprint, trusted: false, token: d.token)
        let adminAPI = APIClient(baseURL: admin.baseURL, token: admin.token)
        // 新建一条密钥，用它开第二个网关
        let created = try await adminAPI.remoteKeyCreate(name: "live-test")
        XCTAssertTrue(created.code.hasPrefix("ppc1_"))
        let second = try await local.gatewayOpen(url: d.urls[0], fingerprint: d.fingerprint, trusted: false, token: created.token)
        XCTAssertNotEqual(second.id, admin.id)
        let secondAPI = APIClient(baseURL: second.baseURL, token: second.token)
        _ = try await secondAPI.systemInfo()

        if let p = env["PAPERPLANE_LIVE_FORWARD_PORT"], let port = Int(p) {
            let f = try await local.gatewayForward(id: second.id, port: port)
            XCTAssertGreaterThan(f.localPort, 0)
            let url = URL(string: ServerURL.forwarded("http://localhost:\(port)/", localPort: f.localPort))!
            let (_, resp) = try await URLSession.shared.data(from: url)
            XCTAssertEqual((resp as? HTTPURLResponse)?.statusCode, 200)
            // 再调一次直接返回同一个转发
            let again = try await local.gatewayForward(id: second.id, port: port)
            XCTAssertEqual(again.localPort, f.localPort)
            try await local.gatewayUnforward(id: second.id, port: port)
        }

        try await adminAPI.remoteKeyRevoke(created.key.id)
        do {
            _ = try await secondAPI.systemInfo()
            XCTFail("吊销后应被拒绝")
        } catch let e as APIError {
            XCTAssertEqual(e.code, APIError.codeRemoteUnauthorized)
            XCTAssertEqual(e.status, 403)
            XCTAssertTrue(e.isRemoteFailure)
        }
        // 重新 open 直接报 unauthorized
        do {
            _ = try await local.gatewayOpen(url: d.urls[0], fingerprint: d.fingerprint, trusted: false, token: created.token)
            XCTFail("吊销的密钥不能再 open")
        } catch let e as APIError {
            XCTAssertEqual(e.code, APIError.codeUnauthorized)
        }
        try await local.gatewayClose(second.id)
        try await local.gatewayClose(admin.id)
    }
}

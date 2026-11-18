import Foundation

/// daemon 返回的错误，或网络错误（code = "network"）。
public struct APIError: Error, LocalizedError, Hashable {
    public var code: String
    public var message: String
    public var status: Int?
    /// 插件给出的结构化信息，如 {"reason": "identityMissing", "missing": ["user.email"]}
    public var data: JSONValue?

    public init(code: String, message: String, status: Int? = nil, data: JSONValue? = nil) {
        self.code = code; self.message = message; self.status = status; self.data = data
    }

    /// data.reason 的快捷访问
    public var reason: String? {
        if case .object(let o)? = data, case .string(let s)? = o["reason"] { return s }
        return nil
    }

    public var errorDescription: String? { message }

    public static let codeNetwork = "network"
    public static let codeBadResponse = "bad_response"
    public static let codeNotFound = "not_found"
    public static let codeCancelled = "cancelled"
    public static let codeUserError = "user_error"
    public static let codeNotSupported = "not_supported"
}

/// 内核 HTTP API 客户端：POST /api/v1/call/<op>，Bearer token。
public final class APIClient: @unchecked Sendable {
    public let baseURL: URL
    public let token: String
    private let session: URLSession

    public init(baseURL: URL, token: String, timeout: TimeInterval = 300) {
        self.baseURL = baseURL
        self.token = token
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = timeout
        cfg.timeoutIntervalForResource = timeout * 4
        cfg.requestCachePolicy = .reloadIgnoringLocalCacheData
        cfg.urlCache = nil
        cfg.httpMaximumConnectionsPerHost = 8
        // 本机连接不走系统代理（用户机器上常开着代理软件）
        cfg.connectionProxyDictionary = [:]
        self.session = URLSession(configuration: cfg)
    }

    public convenience init(runtime: RuntimeInfo) {
        self.init(baseURL: URL(string: "http://127.0.0.1:\(runtime.port)")!, token: runtime.token)
    }

    // MARK: 请求构造

    public func url(_ path: String) -> URL {
        URL(string: path, relativeTo: baseURL)!.absoluteURL
    }

    /// 带鉴权头的请求（SSE、插件文件也用它）
    public func authorizedRequest(_ path: String, method: String = "GET") -> URLRequest {
        var r = URLRequest(url: url(path))
        r.httpMethod = method
        r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        return r
    }

    // MARK: 调用

    /// 调用一个操作并解码结果。
    public func call<T: Decodable>(_ op: String, _ input: JSONValue = [:], as type: T.Type = T.self,
                                   timeout: TimeInterval? = nil) async throws -> T {
        let body = try JSONCoding.encoder.encode(input)
        return try await post(op, body: body, timeout: timeout)
    }

    /// 用 Encodable 输入调用。
    public func call<I: Encodable, T: Decodable>(_ op: String, encodable input: I, as type: T.Type = T.self,
                                                 timeout: TimeInterval? = nil) async throws -> T {
        let body = try JSONCoding.encoder.encode(input)
        return try await post(op, body: body, timeout: timeout)
    }

    /// 调用但不关心输出。
    public func send(_ op: String, _ input: JSONValue = [:], timeout: TimeInterval? = nil) async throws {
        let _: JSONValue = try await call(op, input, timeout: timeout)
    }

    private func post<T: Decodable>(_ op: String, body: Data, timeout: TimeInterval?) async throws -> T {
        var req = authorizedRequest("/api/v1/call/\(op)", method: "POST")
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = body
        if let timeout { req.timeoutInterval = timeout }
        let (data, resp) = try await perform(req)
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        return try Self.decodeEnvelope(data, status: status)
    }

    /// 解析 {"result": ...} / {"error": {...}} 信封（单元测试也用它）。
    public static func decodeEnvelope<T: Decodable>(_ data: Data, status: Int, as type: T.Type = T.self) throws -> T {
        let env: Envelope<T>
        do {
            env = try JSONCoding.decoder.decode(Envelope<T>.self, from: data)
        } catch {
            if status >= 400 {
                let text = String(decoding: data.prefix(500), as: UTF8.self)
                throw APIError(code: status == 401 ? "unauthorized" : "http_\(status)", message: text, status: status)
            }
            throw APIError(code: APIError.codeBadResponse, message: "\(error)", status: status)
        }
        if let e = env.error {
            throw APIError(code: e.code ?? "internal", message: e.message ?? "", status: status, data: e.data)
        }
        if status >= 400 {
            throw APIError(code: "http_\(status)", message: "HTTP \(status)", status: status)
        }
        if let r = env.result { return r }
        if let n = T.self as? NullDecodable.Type, let v = n.nullValue as? T { return v }
        throw APIError(code: APIError.codeBadResponse, message: "empty result", status: status)
    }

    private struct Envelope<T: Decodable>: Decodable {
        let result: T?
        let error: ErrorBody?
    }

    private struct ErrorBody: Decodable {
        let code: String?
        let message: String?
        let data: JSONValue?
    }

    private func perform(_ req: URLRequest) async throws -> (Data, URLResponse) {
        do {
            return try await session.data(for: req)
        } catch let e as URLError where e.code == .cancelled {
            throw CancellationError()
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw APIError(code: APIError.codeNetwork, message: error.localizedDescription)
        }
    }

    // MARK: 其他端点

    /// GET /api/v1/ops
    public func ops() async throws -> JSONValue {
        let (data, resp) = try await perform(authorizedRequest("/api/v1/ops"))
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if let env: JSONValue = try? Self.decodeEnvelope(data, status: status) { return env }
        return try JSONCoding.decoder.decode(JSONValue.self, from: data)
    }

    /// GET /api/v1/plugins/<pluginId>/files/<path>
    public func pluginFile(pluginId: String, path: String) async throws -> Data {
        let escaped = path.split(separator: "/").map {
            String($0).addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? String($0)
        }.joined(separator: "/")
        let (data, resp) = try await perform(authorizedRequest("/api/v1/plugins/\(pluginId)/files/\(escaped)"))
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            throw APIError(code: "http_\(status)", message: "plugin file \(pluginId)/\(path): HTTP \(status)", status: status)
        }
        return data
    }
}

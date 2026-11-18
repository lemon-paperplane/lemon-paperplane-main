import Foundation

/// 全局事件流 GET /api/v1/events。断线自动重连；每次（重）连上后先收到 hello。
public final class EventStream: @unchecked Sendable {
    public enum Update: Sendable {
        case connected
        case event(DaemonEvent)
        case disconnected(String?)
    }

    private var connection: SSEConnection?

    public init() {}

    /// handler 在后台串行队列调用，界面层自行切回主线程。
    public func start(client: APIClient, handler: @escaping @Sendable (Update) -> Void) {
        stop()
        let conn = SSEConnection(reconnect: true, makeRequest: {
            client.authorizedRequest("/api/v1/events")
        }, onEvent: { ev in
            switch ev {
            case .open: handler(.connected)
            case .closed(let reason): handler(.disconnected(reason))
            case .message(let m):
                guard let data = m.data.data(using: .utf8),
                      let e = try? JSONCoding.decoder.decode(DaemonEvent.self, from: data) else { return }
                handler(.event(e))
            }
        })
        connection = conn
        conn.start()
    }

    public func stop() {
        connection?.stop()
        connection = nil
    }
}

/// 控制台日志流 GET /api/v1/consoles/<id>/stream?since=<seq>。
/// 断线后从最后收到的 seq 续传；收到 event: end 后结束。
public final class ConsoleStream: @unchecked Sendable {
    public enum Update: Sendable {
        case lines([LogLine])
        case ended
        case disconnected(String?)
    }

    private struct Batch: Decodable { @DefArray var lines: [LogLine] }

    private var connection: SSEConnection?
    private let lock = NSLock()
    private var lastSeq: Int64

    public let consoleId: String

    public init(consoleId: String, since: Int64 = 0) {
        self.consoleId = consoleId
        self.lastSeq = since
    }

    public func start(client: APIClient, handler: @escaping @Sendable (Update) -> Void) {
        stop()
        let id = consoleId.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? consoleId
        let conn = SSEConnection(reconnect: true, endEvent: "end", makeRequest: { [weak self] in
            let since = self?.currentSeq ?? 0
            return client.authorizedRequest("/api/v1/consoles/\(id)/stream?since=\(since)")
        }, onEvent: { [weak self] ev in
            switch ev {
            case .open: break
            case .closed(let reason):
                if reason == nil { handler(.ended) } else { handler(.disconnected(reason)) }
            case .message(let m):
                if m.event == "end" { return }
                guard let data = m.data.data(using: .utf8),
                      let b = try? JSONCoding.decoder.decode(Batch.self, from: data), !b.lines.isEmpty else { return }
                self?.advance(to: b.lines.last!.seq)
                handler(.lines(b.lines))
            }
        })
        connection = conn
        conn.start()
    }

    public func stop() {
        connection?.stop()
        connection = nil
    }

    private var currentSeq: Int64 { lock.lock(); defer { lock.unlock() }; return lastSeq }

    private func advance(to seq: Int64) {
        lock.lock(); if seq > lastSeq { lastSeq = seq }; lock.unlock()
    }
}

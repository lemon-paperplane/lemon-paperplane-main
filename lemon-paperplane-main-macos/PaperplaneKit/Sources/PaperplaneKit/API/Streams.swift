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
/// 断线后从最后收到的已定型行的 seq 续传；收到 event: end 后结束。
///
/// 每条 data: 消息里：
/// - `lines`：已定型的行（有 seq、已落盘），按顺序追加；
/// - `live`（可选）：实时尾部，即终端屏幕末尾还在被程序原地改写的若干行（进度条等，seq 为 0）。
///   键存在时整体替换客户端当前的实时尾部（空数组 = 清空），键缺省时实时尾部不变。
///   同一条消息先追加 lines 再替换 live；实时尾部永远显示在所有已定型行后面，不推进 seq。
/// 老内核从不发 live，此时行为与只有 lines 时完全相同。
public final class ConsoleStream: @unchecked Sendable {
    public enum Update: Sendable {
        /// 一条消息的内容，作为一次更新交给界面（保证「先追加再替换」的顺序是原子的）。
        /// live 为 nil 表示消息里没有 live 键（实时尾部保持不变）；为 [] 表示清空实时尾部
        case lines([LogLine], live: [LogLine]?)
        /// 流结束（event: end）。界面此时应清空实时尾部（内核结束前会把实时行全部定型，正常情况下已经是空的）
        case ended
        case disconnected(String?)
    }

    /// 一条 data: 消息。live 必须能区分「键缺省」和「空数组」，所以手写解码
    struct Batch: Decodable {
        var lines: [LogLine]
        var live: [LogLine]?

        private enum CodingKeys: String, CodingKey { case lines, live }

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            // lines 缺省或为 null（Go 的 nil 切片）都当作没有新行
            lines = try c.decodeIfPresent([LogLine].self, forKey: .lines) ?? []
            if c.contains(.live) {
                // 键存在即整体替换：null 当作清空；实时行解析失败也只清空尾部，不连累同一条消息里的已定型行
                if (try? c.decodeNil(forKey: .live)) == true {
                    live = []
                } else {
                    live = (try? c.decode([LogLine].self, forKey: .live)) ?? []
                }
            } else {
                live = nil
            }
        }
    }

    /// 解析一条 data: 消息；解码失败或既没有新行也没有 live 键时返回 nil（无需通知界面）
    static func decode(_ data: String) -> Batch? {
        guard let raw = data.data(using: .utf8),
              let b = try? JSONCoding.decoder.decode(Batch.self, from: raw),
              !b.lines.isEmpty || b.live != nil else { return nil }
        return b
    }

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
                guard let b = ConsoleStream.decode(m.data) else { return }
                // 续传只看已定型的行；实时行不推进 seq
                if let last = b.lines.last { self?.advance(to: last.seq) }
                handler(.lines(b.lines, live: b.live))
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

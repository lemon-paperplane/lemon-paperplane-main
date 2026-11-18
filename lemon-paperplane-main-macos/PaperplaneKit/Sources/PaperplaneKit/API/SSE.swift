import Foundation

/// 一条 Server-Sent Events 消息。
public struct SSEMessage: Hashable, Sendable {
    public var event: String?
    public var data: String
    public var id: String?
    public init(event: String? = nil, data: String, id: String? = nil) {
        self.event = event; self.data = data; self.id = id
    }
}

/// 按行喂入的 SSE 解析器（不含换行符）。空行结束一条消息；以 ":" 开头的是注释（如 ": ping"）。
public struct SSEParser {
    private var event: String?
    private var data: [String] = []
    private var id: String?
    private var hasField = false

    public init() {}

    /// 返回完整消息（遇到空行时），或 nil。
    public mutating func feed(_ line: String) -> SSEMessage? {
        if line.isEmpty {
            defer { event = nil; data = []; hasField = false }
            guard hasField, !(data.isEmpty && event == nil) else { return nil }
            return SSEMessage(event: event, data: data.joined(separator: "\n"), id: id)
        }
        if line.hasPrefix(":") { return nil }
        let field: Substring
        var value: Substring
        if let colon = line.firstIndex(of: ":") {
            field = line[..<colon]
            value = line[line.index(after: colon)...]
            if value.hasPrefix(" ") { value = value.dropFirst() }
        } else {
            field = Substring(line)
            value = ""
        }
        switch field {
        case "data": data.append(String(value)); hasField = true
        case "event": event = String(value); hasField = true
        case "id": id = String(value)
        default: break
        }
        return nil
    }
}

/// 按字节块切行（处理 \r\n 与跨块的半行）。
public struct LineSplitter {
    private var buffer = Data()
    public init() {}

    public mutating func append(_ chunk: Data) -> [String] {
        buffer.append(chunk)
        var lines: [String] = []
        var start = buffer.startIndex
        while let nl = buffer[start...].firstIndex(of: 0x0A) {
            var end = nl
            if end > start, buffer[end - 1] == 0x0D { end -= 1 }
            lines.append(String(decoding: buffer[start..<end], as: UTF8.self))
            start = buffer.index(after: nl)
        }
        if start > buffer.startIndex { buffer = Data(buffer[start...]) }
        return lines
    }
}

/// 自动重连的 SSE 连接（URLSession 数据回调，避免逐字节异步迭代）。
/// 回调在内部串行队列上执行；断线后指数退避重连（0.5s 起，最长 15s），45 秒没有任何字节视为断线。
public final class SSEConnection: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    public enum Event: Sendable {
        case open
        case message(SSEMessage)
        case closed(String?)
    }

    private let makeRequest: @Sendable () -> URLRequest
    private let onEvent: @Sendable (Event) -> Void
    private let reconnect: Bool
    private let endEvent: String?
    private let queue: OperationQueue
    private let lock = NSLock()
    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var stopped = false
    private var attempt = 0
    private var splitter = LineSplitter()
    private var parser = SSEParser()
    private var lastByte = Date()
    private var watchdog: DispatchSourceTimer?
    private let idleTimeout: TimeInterval

    /// - Parameters:
    ///   - makeRequest: 每次（重）连时生成请求，可带上最新的 since
    ///   - endEvent: 收到这个 event 名后停止且不再重连（控制台流是 "end"）
    public init(reconnect: Bool = true, endEvent: String? = nil, idleTimeout: TimeInterval = 45,
                makeRequest: @escaping @Sendable () -> URLRequest,
                onEvent: @escaping @Sendable (Event) -> Void) {
        self.makeRequest = makeRequest
        self.onEvent = onEvent
        self.reconnect = reconnect
        self.endEvent = endEvent
        self.idleTimeout = idleTimeout
        self.queue = OperationQueue()
        self.queue.maxConcurrentOperationCount = 1
        self.queue.name = "paperplane.sse"
        super.init()
    }

    public func start() {
        lock.lock(); stopped = false; lock.unlock()
        queue.addOperation { [weak self] in self?.connect() }
    }

    public func stop() {
        lock.lock()
        stopped = true
        let t = task
        task = nil
        let s = session
        session = nil
        watchdog?.cancel()
        watchdog = nil
        lock.unlock()
        t?.cancel()
        s?.invalidateAndCancel()
    }

    private var isStopped: Bool { lock.lock(); defer { lock.unlock() }; return stopped }

    private func connect() {
        guard !isStopped else { return }
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = idleTimeout + 15
        cfg.timeoutIntervalForResource = 60 * 60 * 24 * 365
        cfg.requestCachePolicy = .reloadIgnoringLocalCacheData
        cfg.urlCache = nil
        cfg.connectionProxyDictionary = [:]
        let s = URLSession(configuration: cfg, delegate: self, delegateQueue: queue)
        var req = makeRequest()
        req.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        req.timeoutInterval = idleTimeout + 15
        let t = s.dataTask(with: req)
        lock.lock()
        session?.invalidateAndCancel()
        session = s
        task = t
        lock.unlock()
        splitter = LineSplitter()
        parser = SSEParser()
        lastByte = Date()
        startWatchdog()
        t.resume()
    }

    private func startWatchdog() {
        watchdog?.cancel()
        let timer = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
        timer.schedule(deadline: .now() + 5, repeating: 5)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            self.queue.addOperation {
                if Date().timeIntervalSince(self.lastByte) > self.idleTimeout {
                    self.lock.lock(); let t = self.task; self.lock.unlock()
                    t?.cancel()
                }
            }
        }
        timer.resume()
        watchdog = timer
    }

    private func scheduleReconnect() {
        guard reconnect, !isStopped else { return }
        let delay = min(15, 0.5 * pow(2, Double(min(attempt, 6))))
        attempt += 1
        DispatchQueue.global().asyncAfter(deadline: .now() + delay) { [weak self] in
            self?.queue.addOperation { self?.connect() }
        }
    }

    // MARK: URLSessionDataDelegate

    public func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                           completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard session === self.session else { completionHandler(.cancel); return }
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 200 {
            attempt = 0
            onEvent(.open)
            completionHandler(.allow)
        } else {
            completionHandler(.cancel)
        }
    }

    public func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard session === self.session, !isStopped else { return }
        lastByte = Date()
        for line in splitter.append(data) {
            if let msg = parser.feed(line) {
                onEvent(.message(msg))
                if let endEvent, msg.event == endEvent {
                    stop()
                    onEvent(.closed(nil))
                    return
                }
            }
        }
    }

    public func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard session === self.session || self.session == nil else { return }
        let wasStopped = isStopped
        session.finishTasksAndInvalidate()
        lock.lock()
        if self.session === session { self.session = nil; self.task = nil }
        lock.unlock()
        if wasStopped { return }
        // 非 end 事件导致的断开一律带原因，nil 只用于 end
        onEvent(.closed(error?.localizedDescription ?? "connection closed"))
        scheduleReconnect()
    }
}

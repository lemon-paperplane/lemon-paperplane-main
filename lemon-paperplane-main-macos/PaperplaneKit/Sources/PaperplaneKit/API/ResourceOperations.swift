import Foundation

public extension APIClient {
    /// 运行的资源趋势与进程明细（最近 5 分钟）
    func runStats(_ runId: String) async throws -> RunStats {
        try await call("run.stats", ["runId": .string(runId)])
    }
}

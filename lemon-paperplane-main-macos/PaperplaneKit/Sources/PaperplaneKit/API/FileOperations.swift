import Foundation

// fs.* 操作的类型化封装（docs/editor.md 第 3 节）。路径相对工程根、"/" 分隔，内核拒绝越出工程根。

public extension APIClient {
    /// 列出目录（目录在前、按名称排序）；path 为空表示工程根
    func fsList(projectId: String, path: String = "") async throws -> FSListResult {
        try await call("fs.list", ["projectId": .string(projectId), "path": .string(path)])
    }

    func fsRead(projectId: String, path: String, maxBytes: Int? = nil) async throws -> FSReadResult {
        try await call("fs.read", jsonObject([
            "projectId": .string(projectId), "path": .string(path), "maxBytes": maxBytes.map { .number(Double($0)) },
        ]))
    }

    /// expectedMtime 与磁盘不一致时返回 user_error（data.reason = modifiedExternally）
    func fsWrite(projectId: String, path: String, content: String, encoding: String? = nil, bom: Bool? = nil,
                 lineEnding: String? = nil, expectedMtime: String? = nil, create: Bool? = nil) async throws -> FSWriteResult {
        try await call("fs.write", jsonObject([
            "projectId": .string(projectId), "path": .string(path), "content": .string(content),
            "encoding": encoding.map(JSONValue.string), "bom": bom.map(JSONValue.bool),
            "lineEnding": lineEnding.map(JSONValue.string), "expectedMtime": expectedMtime.map(JSONValue.string),
            "create": create.map(JSONValue.bool),
        ]))
    }

    func fsStat(projectId: String, paths: [String]) async throws -> [FSStat] {
        try await call("fs.stat", ["projectId": .string(projectId), "paths": JSONValue(paths)])
    }

    func fsCreate(projectId: String, path: String, dir: Bool) async throws -> PathResult {
        try await call("fs.create", ["projectId": .string(projectId), "path": .string(path), "dir": .bool(dir)])
    }

    func fsRename(projectId: String, from: String, to: String) async throws -> PathResult {
        try await call("fs.rename", ["projectId": .string(projectId), "from": .string(from), "to": .string(to)])
    }

    /// 移到废纸篓，不直接删除
    func fsDelete(projectId: String, paths: [String]) async throws -> FSDeleteResult {
        try await call("fs.delete", ["projectId": .string(projectId), "paths": JSONValue(paths)])
    }

    /// 按文件名模糊匹配（快速打开）
    func fsSearch(projectId: String, query: String, limit: Int? = nil) async throws -> [FSSearchHit] {
        try await call("fs.search", jsonObject([
            "projectId": .string(projectId), "query": .string(query), "limit": limit.map { .number(Double($0)) },
        ]))
    }

    func fsGrep(projectId: String, query: String, regex: Bool = false, caseSensitive: Bool = false,
                include: String? = nil, limit: Int? = nil) async throws -> FSGrepResult {
        try await call("fs.grep", jsonObject([
            "projectId": .string(projectId), "query": .string(query), "regex": .bool(regex),
            "caseSensitive": .bool(caseSensitive), "include": include.map(JSONValue.string),
            "limit": limit.map { .number(Double($0)) },
        ]))
    }
}

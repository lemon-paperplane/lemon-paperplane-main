using System.Collections.Generic;
using System.Threading.Tasks;

namespace PaperplaneKit;

// fs.* 操作的类型化封装（docs/editor.md 第 3 节）。路径相对工程根、"/" 分隔，内核拒绝越出工程根。

public sealed partial class ApiClient
{
    /// <summary>列出目录（目录在前、按名称排序）；path 为空表示工程根</summary>
    public Task<FSListResult> FsListAsync(string projectId, string path = "") =>
        CallAsync<FSListResult>("fs.list", Json.Object(("projectId", projectId), ("path", path)));

    public Task<FSReadResult> FsReadAsync(string projectId, string path, int? maxBytes = null) =>
        CallAsync<FSReadResult>("fs.read", Json.Object(("projectId", projectId), ("path", path), ("maxBytes", maxBytes)));

    /// <summary>expectedMtime 与磁盘不一致时报 user_error（ApiException.Reason = modifiedExternally）</summary>
    public Task<FSWriteResult> FsWriteAsync(string projectId, string path, string content, string? encoding = null, bool? bom = null,
        string? lineEnding = null, string? expectedMtime = null, bool? create = null) =>
        CallAsync<FSWriteResult>("fs.write", Json.Object(("projectId", projectId), ("path", path), ("content", content),
            ("encoding", encoding), ("bom", bom), ("lineEnding", lineEnding), ("expectedMtime", expectedMtime), ("create", create)));

    public Task<List<FSStat>> FsStatAsync(string projectId, IEnumerable<string> paths) =>
        CallAsync<List<FSStat>>("fs.stat", Json.Object(("projectId", projectId), ("paths", paths)));

    public Task<PathResult> FsCreateAsync(string projectId, string path, bool dir) =>
        CallAsync<PathResult>("fs.create", Json.Object(("projectId", projectId), ("path", path), ("dir", dir)));

    public Task<PathResult> FsRenameAsync(string projectId, string from, string to) =>
        CallAsync<PathResult>("fs.rename", Json.Object(("projectId", projectId), ("from", from), ("to", to)));

    /// <summary>移到回收站，不直接删除</summary>
    public Task<FSDeleteResult> FsDeleteAsync(string projectId, IEnumerable<string> paths) =>
        CallAsync<FSDeleteResult>("fs.delete", Json.Object(("projectId", projectId), ("paths", paths)));

    /// <summary>按文件名模糊匹配（快速打开）</summary>
    public Task<List<FSSearchHit>> FsSearchAsync(string projectId, string query, int? limit = null) =>
        CallAsync<List<FSSearchHit>>("fs.search", Json.Object(("projectId", projectId), ("query", query), ("limit", limit)));

    public Task<FSGrepResult> FsGrepAsync(string projectId, string query, bool regex = false, bool caseSensitive = false,
        string? include = null, int? limit = null) =>
        CallAsync<FSGrepResult>("fs.grep", Json.Object(("projectId", projectId), ("query", query), ("regex", regex),
            ("caseSensitive", caseSensitive), ("include", include), ("limit", limit)));
}

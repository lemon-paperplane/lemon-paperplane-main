using System.Threading.Tasks;

namespace PaperplaneKit;

public sealed partial class ApiClient
{
    /// <summary>运行的资源趋势与进程明细（最近 5 分钟）</summary>
    public Task<RunStats> RunStatsAsync(string runId) => CallAsync<RunStats>("run.stats", Json.Object(("runId", runId)));
}

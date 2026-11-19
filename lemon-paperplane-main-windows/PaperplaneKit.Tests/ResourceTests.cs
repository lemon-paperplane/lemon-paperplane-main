using System.Collections.Generic;
using System.Linq;
using System.Text;
using static PaperplaneKit.Tests.FixtureFiles;

namespace PaperplaneKit.Tests;

/// <summary>对应 Mac 的 ResourceTests.swift。</summary>
public class ResourceTests
{
    [Test]
    public void RealRunStats()
    {
        var s = Real<RunStats>("run.stats.json");
        Assert.Equal("running", s.State);
        Assert.Equal(4, s.Samples.Count);
        Assert.Equal("Python", s.Processes.First().Name);
        KitAssert.Seq(new[] { 47311 }, s.Ports);
        Assert.Equal(2000, s.IntervalMs);
        Assert.Near(24.1, s.PeakRSSMB, 0.001);
        var ended = Real<RunStats>("run.stats.ended.json");
        Assert.Equal("exited", ended.State);
        Assert.Equal(0, ended.Processes.Count);
        Assert.Equal(4, ended.Samples.Count); // 结束后保留曲线
        var run = Real<Run>("run.get.json");
        Assert.Equal(1, run.Procs);
        Assert.True(run.PeakRSSMB > 0);
    }

    [Test]
    public void RealStatsEvent()
    {
        var data = Bytes("real/stats.event.txt").Concat(Encoding.UTF8.GetBytes("\n\n")).ToArray();
        var events = Events(data);
        var briefs = events.First().Decode<List<RunBrief>>();
        Assert.Equal("6a16998ec83a0ff4", briefs.First().ProjectId);
        Assert.Equal(1, briefs.First().Procs);
        Assert.True(briefs.First().T > 0);
    }

    [Test]
    public void SeriesAppendMergeCapacity()
    {
        var s = new ResourceSeries(capacity: 3);
        Assert.True(s.Append(new ResourceSample(10, 1, 10)));
        Assert.False(s.Append(new ResourceSample(10, 9, 99))); // 同一时间点忽略
        Assert.False(s.Append(new ResourceSample(5, 9, 99))); // 更早的忽略
        s.Append(new ResourceSample(20, 2, 20));
        s.Append(new ResourceSample(30, 3, 30));
        s.Append(new ResourceSample(40, 4, 40));
        KitAssert.Seq(new long[] { 20, 30, 40 }, s.Samples.Select(x => x.T));
        Assert.True(s.Merge(new[] { new ResourceSample(15, 0, 0), new ResourceSample(35, 5, 50) }));
        KitAssert.Seq(new long[] { 30, 35, 40 }, s.Samples.Select(x => x.T));
        Assert.Equal(50.0, s.MaxRSS);
        Assert.False(s.Merge(new ResourceSample[0]));
        // 同样的历史再合并一次：没有变化
        Assert.False(s.Merge(s.Samples.ToList()));
    }

    [Test]
    public void SparklineGeometryPoints()
    {
        var samples = new[] { new ResourceSample(0, 0, 0), new ResourceSample(1000, 0, 50), new ResourceSample(2000, 0, 100) };
        var rect = new KitRect(0, 0, 100, 20);
        var pts = SparklineGeometry.Points(samples, x => x.RssMB, rect, maxValue: 100);
        // 最大值留 10% 余量：100 → 110
        Assert.Equal(new KitPoint(0, 20), pts.First());
        Assert.Near(100, pts.Last().X, 0.001);
        Assert.Near(20 - 20 * (100.0 / 110), pts.Last().Y, 0.01);
        // 固定时间轴：5 分钟跨度时，2 秒的数据贴在右端
        var fixedPts = SparklineGeometry.Points(samples, x => x.RssMB, rect, span: 300_000, maxValue: 100);
        Assert.True(fixedPts.First().X > 99);
        Assert.Equal(1, SparklineGeometry.NearestIndex(pts, 52));
        Assert.Equal(250.0, SparklineGeometry.NiceCeiling(230));
        Assert.Equal(1.0, SparklineGeometry.NiceCeiling(0.7));
        Assert.Equal(2000.0, SparklineGeometry.NiceCeiling(1300));
        // 单点
        var one = SparklineGeometry.Points(new[] { samples[2] }, x => x.RssMB, rect);
        Assert.Equal(1, one.Count);
    }

    [Test]
    public void ProcessTreeFlatten()
    {
        // yarn(10) → node vite(20) → esbuild(30)；另一个孤立进程 99 的父进程不在树里
        var list = new[]
        {
            new RunProcess(10, 1, "node", rssMB: 115.9),
            new RunProcess(20, 10, "node", rssMB: 93),
            new RunProcess(30, 20, "esbuild", rssMB: 16.8),
            new RunProcess(99, 7, "sh", rssMB: 1),
        };
        var nodes = ProcessTree.Flatten(list);
        KitAssert.Seq(new[] { 10, 20, 30, 99 }, nodes.Select(n => n.Process.Pid));
        KitAssert.Seq(new[] { 0, 1, 2, 0 }, nodes.Select(n => n.Depth));
        Assert.Near(225.7, nodes[0].SubtreeRSSMB, 0.001);
        Assert.True(nodes[1].HasChildren);
        Assert.False(nodes[2].HasChildren);
        KitAssert.Seq(new[] { 10, 20, 99 }, ProcessTree.Flatten(list, new HashSet<int> { 20 }).Select(n => n.Process.Pid));
        // 自己是自己的父进程、环：不死循环
        var loop = new[] { new RunProcess(1, 2, "a"), new RunProcess(2, 1, "b") };
        KitAssert.SetEq(new[] { 1, 2 }, ProcessTree.Flatten(loop).Select(n => n.Process.Pid));
    }
}

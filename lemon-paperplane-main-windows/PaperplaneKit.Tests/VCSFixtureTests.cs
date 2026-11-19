using System;
using System.Collections.Generic;
using System.Linq;
using static PaperplaneKit.Tests.FixtureFiles;

namespace PaperplaneKit.Tests;

/// <summary>用真实 daemon + git 插件返回的版本控制响应核对模型解码（对应 Mac 的 VCSFixtureTests.swift）。</summary>
public class VCSFixtureTests
{
    [Test]
    public void Status()
    {
        var s = Real<VCSStatus>("vcs.status.json");
        Assert.Equal("main", s.Branch);
        Assert.Equal("origin/main", s.Upstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(2, s.UntrackedCount);
        Assert.Equal(40, s.HeadHash?.Length);
        Assert.False(s.Unborn);
        Assert.Null(s.OperationDetail);
        var staged = s.Files.FirstOrDefault(f => f.Path == "src/staged.txt");
        Assert.NotNull(staged);
        Assert.Equal("A", staged!.IndexStatus);
        Assert.True(staged.Staged);
        Assert.Null(staged.WorktreeStatus);
        Assert.True(s.Files.Any(f => f.Path == "docs/中文 名.md" && f.IsUntracked));
        Assert.True(s.Files.FirstOrDefault(f => f.Path == "logo.png")?.Binary == true);
    }

    [Test]
    public void ConflictStatus()
    {
        var s = Real<VCSStatus>("vcs.status.conflict.json");
        Assert.Equal("merge", s.Operation);
        Assert.Equal(3, s.Conflicts);
        Assert.Equal("merge", s.OperationDetail?.Op);
        Assert.Equal("Merge branch 'other'", s.OperationDetail?.Message);
        KitAssert.Seq(new[] { "AA", "DU", "UU" },
            s.Files.Where(f => f.Conflicted).Select(f => f.ConflictType).Where(t => t != null).OrderBy(t => t, StringComparer.Ordinal));
        // SDK 里 operationDetail 是字符串：两种形态都要能解
        var str = Json.Deserialize<VCSStatus>("{\"branch\":\"x\",\"operationDetail\":\"rebase 2/5\"}");
        Assert.Equal("rebase 2/5", str.OperationDetail?.Message);
    }

    [Test]
    public void Diff()
    {
        var d = Real<VCSDiff>("vcs.diff.json");
        Assert.Equal("head", d.Mode);
        Assert.Equal(3, d.Context);
        Assert.Equal("M", d.Status);
        Assert.Equal(2, d.Additions);
        Assert.Equal(8, d.Hunks.First().Lines.Count);
        Assert.Equal("-", d.Hunks.First().Lines[1].Kind);
        Assert.Equal(0, d.Hunks.First().Lines[1].New);
        var b = Real<VCSDiff>("vcs.diff.binary.json");
        Assert.True(b.Binary);
        Assert.Equal(0, b.Hunks.Count);
    }

    [Test]
    public void LogShowBranches()
    {
        var log = Real<VCSLog>("vcs.log.json");
        Assert.Equal(5, log.Commits.Count);
        Assert.True(log.HasMore);
        Assert.Equal(true, log.Graph);
        var merge = log.Commits.FirstOrDefault(c => c.Parents.Count == 2);
        Assert.NotNull(merge);
        Assert.Equal(2, merge!.Graph?.Edges.Count);
        Assert.True(merge.Refs.Any(r => r.Kind == "tag"));
        Assert.Equal("head", log.Commits[0].Refs.First().Kind);

        var show = Real<VCSCommitDetail>("vcs.show.json");
        Assert.Equal(2, show.Commit.Parents.Count);
        Assert.Equal("Merge branch 'feature/login'", show.Message);
        Assert.Equal(1, show.Parent);
        Assert.Equal("A", show.Files.First().Status);

        var br = Real<VCSBranches>("vcs.branches.json");
        Assert.Equal("main", br.Current);
        KitAssert.Seq(new[] { "main", "feature/login" }, br.Recent);
        Assert.Equal(2, br.Local.FirstOrDefault(x => x.Current)?.Ahead);
        Assert.Equal("origin", br.Remote.First().Remote);

        var hist = Real<VCSLog>("vcs.fileHistory.json");
        Assert.Equal("src/a.txt", hist.Commits.First().Path);
        Assert.NotNull(hist.Commits.First().PathStatus);

        var cmp = Real<VCSCompare>("vcs.compare.json");
        Assert.True(cmp.Ahead.Count > 0);
        Assert.NotNull(cmp.MergeBase);
    }

    [Test]
    public void RemoteAndResults()
    {
        var push = Real<VCSOpResult>("vcs.push.json");
        Assert.True(push.Ok);
        Assert.NotNull(push.TaskId);
        var fetch = Real<VCSOpResult>("vcs.fetch.json");
        Assert.Equal(VCSReason.UpToDate, fetch.Reason);
        var pull = Real<VCSOpResult>("vcs.pull.json");
        Assert.Equal(VCSReason.UpToDate, pull.Reason);
        var pv = Real<VCSPushPreview>("vcs.pushPreview.json");
        Assert.Equal("origin", pv.Remote);
        Assert.Equal(2, pv.Commits.Count);
        Assert.Equal(0, pv.Behind);
        var markers = Real<VCSOpResult>("vcs.resolve.markers.json");
        Assert.False(markers.Ok);
        Assert.Equal(VCSReason.MarkersRemain, markers.Reason);
        KitAssert.Seq(new[] { "both.txt" }, markers.Files);
        var cont = Real<VCSOpResult>("vcs.continue.unresolved.json");
        Assert.Equal(VCSReason.UnresolvedConflicts, cont.Reason);
        Assert.Equal(3, cont.Conflicts.Count);
        Assert.True(Real<VCSOpResult>("vcs.stashSave.json").Ok);
    }

    [Test]
    public void Extensions()
    {
        var stashes = Real<VCSStashList>("vcs.stashList.json");
        Assert.Equal("wip test", stashes.Stashes.First().Message);
        Assert.NotNull(stashes.Stashes.First().Hash);
        Assert.NotNull(stashes.Stashes.First().ParsedDate);
        var show = Real<VCSStashShow>("vcs.stashShow.json");
        Assert.Equal(4, show.Files.Count);
        Assert.Equal(2, show.Untracked.Count);
        var fc = Real<VCSFileContent>("vcs.fileContent.json");
        Assert.True(fc.Binary);
        Assert.True(fc.Exists);
        KitAssert.Seq(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, fc.Data?.Take(4));
        var cb = Real<VCSContainingBranches>("vcs.containingBranches.json");
        Assert.True(cb.Refs.Any(r => r.Kind == "tag"));
        var tools = Real<VCSMergeTools>("vcs.mergeTools.json");
        Assert.True(tools.Tools.Count > 0);
        var cfg = Real<VCSConfig>("vcs.configGet.json");
        Assert.Equal("local", cfg.Scopes.GetValueOrDefault("user.name"));
        var roots = Real<List<VCSRoot>>("vcs.roots.json");
        Assert.Equal(".", roots.First().RelPath);
    }

    [Test]
    public void Overview()
    {
        var list = Real<List<VCSRepoOverview>>("vcs.overview.json");
        Assert.Equal(4, list.Count);
        Assert.Equal(list.Count, list.Select(r => r.Root).Distinct().Count());
        var demo = list.FirstOrDefault(r => r.Name == "demo");
        Assert.NotNull(demo);
        Assert.Equal("demo", demo!.ProjectName);
        Assert.Equal("main", demo.Status?.Branch);
        Assert.True(demo.Status?.Files.Count > 0);
        Assert.Null(demo.Error);
        var conflict = list.FirstOrDefault(r => r.Name == "conflict");
        Assert.NotNull(conflict);
        Assert.Equal("merge", conflict!.Status?.Operation);
        Assert.Equal("merge", conflict.Status?.OperationDetail?.Op);
        Assert.True((conflict.Status?.Conflicts ?? 0) > 0);
        var nested = list.FirstOrDefault(r => r.Name == "nested");
        Assert.NotNull(nested);
        Assert.Equal("nested", nested!.RelPath);
        Assert.Equal(conflict.ProjectId, nested.ProjectId);
        // 取状态失败的仓库：没有 status，有 error
        var failed = Json.Deserialize<VCSRepoOverview>("{\"projectId\":\"p\",\"projectName\":\"P\",\"root\":\"/r\",\"relPath\":\"\",\"name\":\"r\",\"vcs\":\"git\",\"error\":\"boom\"}");
        Assert.Null(failed.Status);
        Assert.Equal("boom", failed.Error);
        Assert.Equal(list.First().Root, Real<List<VCSRepoOverview>>("vcs.overview.json").First().Id);
    }

    [Test]
    public void ArgsEncoding()
    {
        var h = JSONValue.Encode(new VCSHunksArgs { Path = "a.txt", Hunks = new List<int> { 0, 2 }, Context = 3, Mode = VCSDiffMode.Head, Headers = new List<string> { "@@ -1 +1 @@" } });
        KitAssert.JsonEq("\"head\"", h.Get("mode"));
        KitAssert.JsonEq("[\"@@ -1 +1 @@\"]", h.Get("headers"));
        Assert.Null(h.Get("oldPath"));
        var c = JSONValue.Encode(new VCSCommitArgs
        {
            Message = "m",
            Paths = new List<string> { "a" },
            Hunks = new Dictionary<string, List<int>> { ["b"] = new List<int> { 1 } },
            SignOff = true,
        });
        KitAssert.JsonEq("{\"b\":[1]}", c.Get("hunks"));
        Assert.Null(c.Get("amend"));
        var d = JSONValue.Encode(new VCSDiscardArgs { Paths = new List<string> { "x" }, KeepNew = true });
        KitAssert.JsonEq("true", d.Get("keepNew"));
        // 大小写不同的 JSON 键
        var r = JSONValue.Encode(new VCSRefArgs { Ref = "main", NoFF = true, FfOnly = true });
        KitAssert.JsonEq("true", r.Get("noFF"));
        KitAssert.JsonEq("true", r.Get("ffOnly"));
        KitAssert.JsonEq("{\"old\":\"a\",\"new\":\"b\"}", JSONValue.Encode(new VCSBranchRenameArgs { Old = "a", New = "b" }));
        Assert.Equal(TimeSpan.FromSeconds(3600), ApiClient.VcsTimeout(VCSOp.Push));
        Assert.Equal(TimeSpan.FromSeconds(900), ApiClient.VcsTimeout(VCSOp.Commit));
        Assert.Null(ApiClient.VcsTimeout(VCSOp.Status));
    }
}

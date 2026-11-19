using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 冲突解决：按文件「用我的 / 用他们的 / 在外部工具中合并 / 标记已解决」，全部解决后「继续」。不做内置三方合并编辑器（对应 Mac VCSConflictsView.swift）。

public static class VCSConflictActions
{
    public static void Resolve(VCSRepo repo, List<string> paths, string side)
    {
        if (paths.Count == 0) return;
        var title = side switch
        {
            "ours" => L.T("vcs.conflicts.useOurs"),
            "theirs" => L.T("vcs.conflicts.useTheirs"),
            _ => L.T("vcs.conflicts.markResolved"),
        };
        var args = new VCSResolveArgs { Path = paths[0], Side = side, Paths = paths.Count > 1 ? paths : null };
        repo.Perform(VCSOp.Resolve, args, title, quietSuccess: true, extra: r =>
        {
            if (r.Reason != VCSReason.MarkersRemain) return new List<VCSNotice.Action>();
            return new List<VCSNotice.Action>
            {
                new(L.T("vcs.conflicts.markAnyway"), () =>
                {
                    repo.Notice = null;
                    repo.Perform(VCSOp.Resolve, args with { Force = true }, title, quietSuccess: true);
                }, danger: true),
            };
        });
    }

    public static void MergeTool(VCSRepo repo, List<string> paths) =>
        repo.Perform(VCSOp.MergeTool, new VCSPathsArgs { Paths = paths }, L.T("vcs.conflicts.mergeTool"), @long: true, quietSuccess: true);

    /// <summary>继续进行中的操作（未解决冲突 → 去冲突页；空提交 → 跳过）</summary>
    public static void ContinueOp(VCSRepo repo) =>
        repo.Perform(VCSOp.Continue, new VCSNoArgs(), L.T("vcs.op.continue"), @long: true);

    public static void Abort(VCSRepo repo)
    {
        var op = VCSStyle.OperationTitle(repo.Status?.Operation);
        Dialogs.Confirm(L.T("vcs.op.abortTitle", op), L.T("vcs.op.abortMessage"), async () =>
            await repo.RunThrowing(VCSOp.Abort, new VCSNoArgs(), L.T("vcs.op.abort")), ok: L.T("vcs.op.abort"), danger: true);
    }

    public static void Skip(VCSRepo repo) =>
        repo.Perform(VCSOp.Skip, new VCSNoArgs(), L.T("vcs.op.skip"), @long: true);

    /// <summary>我方 / 对方指的是谁（变基时 ours 是变基目标、theirs 是正在重放的提交）</summary>
    public static (string Ours, string Theirs) Sides(VCSStatus s)
    {
        var d = s.OperationDetail;
        if (s.Operation == "rebase")
        {
            var onto = VCSStyle.ShortHash(d?.Onto);
            var cur = VCSStyle.ShortHash(d?.Current);
            return (onto.Length == 0 ? L.T("vcs.conflicts.upstream") : onto, cur.Length == 0 ? d?.HeadName ?? "" : cur);
        }
        var ours = s.Branch.Length == 0 ? VCSStyle.ShortHash(s.HeadHash) : s.Branch;
        var c = VCSStyle.ShortHash(d?.Current);
        return (ours, c.Length == 0 ? L.T("vcs.conflicts.incoming") : c);
    }
}

/// <summary>冲突子页。对应 Mac VCSConflictsPage。</summary>
public sealed class VCSConflictsPage : PPView
{
    private readonly VCSRepo _repo;
    private readonly VCSDiffModel _diff = new();

    public VCSConflictsPage(VCSRepo repo)
    {
        _repo = repo;
    }

    protected override UIElement Build()
    {
        var list = new VCSConflictList(_repo, _diff);
        var right = new VCSDiffView(_diff, _repo.Changes);
        return new PPSplitView(list, right, UIState.Bind("vcs.conflicts.left", 460.0), 320, 760);
    }
}

internal sealed class VCSConflictList : PPView
{
    private readonly VCSRepo _repo;
    private readonly VCSDiffModel _diff;
    private readonly State<string?> _selected;
    private readonly State<VCSMergeTools?> _tools;

    public VCSConflictList(VCSRepo repo, VCSDiffModel diff)
    {
        _repo = repo;
        _diff = diff;
        _selected = UseState<string?>(null);
        _tools = UseState<VCSMergeTools?>(null);
        Observe(repo, nameof(VCSRepo.Status));
        Observe(AppModel.Shared.Data, nameof(DataStore.Contributions));
    }

    protected override async void OnAppear()
    {
        _repo.PropertyChanged += OnRepo;
        _tools.Value = await VCSAsync.Try(() => _repo.Api().VcsMergeToolsAsync(_repo.ProjectId, _repo.Root));
    }

    protected override void OnDisappear() => _repo.PropertyChanged -= OnRepo;

    private void OnRepo(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VCSRepo.Status)) return;
        if (_selected.Value is { } s && !(_repo.Status?.Files.Any(f => f.Path == s && f.Conflicted) ?? false))
        {
            _selected.Value = null;
            _diff.Set(null, _repo);
        }
    }

    protected override UIElement Build()
    {
        var st = _repo.Status ?? new VCSStatus();
        var files = st.Files.Where(f => f.Conflicted).ToList();
        UIElement body;
        if (files.Count == 0) body = ResolvedState(st);
        else
        {
            var col = VStack(1, files.Select(Row));
            col.Align = CrossAlign.Stretch;
            col.Padding = new Thickness(M.Xs);
            body = Scroll(col).Key("vcs.conflicts.list");
        }
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(Header(st, files).At(0, 0));
        g.Children.Add(new PPDivider(subtle: true).At(1, 0));
        g.Children.Add(body.At(2, 0));
        return g;
    }

    private UIElement Header(VCSStatus st, List<VCSFileChange> files)
    {
        var (ours, theirs) = VCSConflictActions.Sides(st);
        var col = VStack(M.Sm,
            HStack(M.Sm, Text(L.T("vcs.conflicts.title", files.Count), Typo.Title), new PPInfoButton(L.T("vcs.conflicts.info")), Spacer(), ToolPicker()),
            HStack(M.Lg,
                HStack(M.Xs, Text(L.T("vcs.conflicts.ours"), Typo.Small, ColorToken.TextTertiary), Text(ours, Typo.MonoSmall)),
                HStack(M.Xs, Text(L.T("vcs.conflicts.theirs"), Typo.Small, ColorToken.TextTertiary), Text(theirs, Typo.MonoSmall)),
                Spacer()),
            files.Count > 0
                ? HStack(M.Sm,
                    new PPButton(L.T("vcs.conflicts.allOurs"), () => VCSConflictActions.Resolve(_repo, files.Select(f => f.Path).ToList(), "ours"), size: PPButtonSize.Small),
                    new PPButton(L.T("vcs.conflicts.allTheirs"), () => VCSConflictActions.Resolve(_repo, files.Select(f => f.Path).ToList(), "theirs"), size: PPButtonSize.Small),
                    new PPButton(L.T("vcs.conflicts.mergeAll"), () => VCSConflictActions.MergeTool(_repo, new List<string>()), icon: "git-merge", size: PPButtonSize.Small)
                        .Disabled(_tools.Value?.Tools.Count == 0))
                : null);
        col.Align = CrossAlign.Stretch;
        col.Padding = new Thickness(M.Md);
        return col;
    }

    /// <summary>外部合并工具：写入 git 插件设置 mergeTool</summary>
    private UIElement ToolPicker()
    {
        var plugin = _repo.Session.Roots.FirstOrDefault(r => r.Path == _repo.Root)?.Plugin ?? "git";
        if (plugin.Length == 0) plugin = "git";
        var settings = AppModel.Shared.Data.Contributions.Settings.FirstOrDefault(s => s.Plugin == plugin);
        var current = settings != null && settings.Values.TryGetValue("mergeTool", out var v) ? v.StringValue() ?? "auto" : "auto";
        var options = new List<DropdownOption<string>> { new("auto", L.T("vcs.conflicts.toolAuto")) };
        options.AddRange((_tools.Value?.Tools ?? new List<VCSMergeToolInfo>()).Select(t => new DropdownOption<string>(t.Id, t.Name)));
        if (options.All(o => o.Value != current)) options.Add(new DropdownOption<string>(current, current));
        var dd = new PPDropdown<string>(options, Bind.Of(() => current, nv =>
        {
            var values = settings != null ? new Dictionary<string, JToken>(settings.Values) : new Dictionary<string, JToken>();
            values["mergeTool"] = nv;
            AppModel.Shared.Perform(L.T("vcs.conflicts.toolFailed"), async api =>
            {
                await api.UpdatePluginSettingsAsync(plugin, values);
                await AppModel.Shared.ReloadPlugins();
            });
        }), size: PPButtonSize.Small, minWidth: 140, borderless: true);
        return HStack(M.Xs, new PPIcon("wrench", M.IconSmall, ColorToken.IconSecondary), dd).Tip(L.T("vcs.conflicts.tool"));
    }

    private UIElement Row(VCSFileChange f)
    {
        var dir = VCSPath.Dir(f.Path);
        var content = HStack(M.Sm, new PPIcon("triangle-alert", M.IconSmall, ColorToken.Danger),
            VStack(0, Text(VCSPath.Name(f.Path)),
                TextMiddle(VCSStyle.ConflictTitle(f.ConflictType) + (dir.Length == 0 ? "" : "  ·  " + dir), Typo.Caption, ColorToken.TextTertiary)).Flex(),
            new PPButton(L.T("vcs.conflicts.ours.short"), () => VCSConflictActions.Resolve(_repo, new List<string> { f.Path }, "ours"), kind: PPButtonKind.Ghost, size: PPButtonSize.Small)
                .Tip(L.T("vcs.conflicts.useOurs")),
            new PPButton(L.T("vcs.conflicts.theirs.short"), () => VCSConflictActions.Resolve(_repo, new List<string> { f.Path }, "theirs"), kind: PPButtonKind.Ghost, size: PPButtonSize.Small)
                .Tip(L.T("vcs.conflicts.useTheirs")),
            new PPIconButton("git-merge", L.T("vcs.conflicts.mergeTool"), () => VCSConflictActions.MergeTool(_repo, new List<string> { f.Path }), size: M.IconButtonSmall, iconSize: M.IconSmall),
            new PPIconButton("check", L.T("vcs.conflicts.markResolved"), () => VCSConflictActions.Resolve(_repo, new List<string> { f.Path }, "resolved"), size: M.IconButtonSmall, iconSize: M.IconSmall)
                .Id("vcs.conflicts.resolved." + f.Path));
        content.Margin = new Thickness(M.Sm, 0, M.Sm, 0);
        var row = VCSViews.TableRow(content, _selected.Value == f.Path, () =>
        {
            _selected.Value = f.Path;
            _diff.Set(new VCSDiffTarget(f.Path, null, VCSDiffKind.Working(VCSDiffMode.Head), "U"), _repo);
        }, () => VCSConflictActions.MergeTool(_repo, new List<string> { f.Path }), height: 38);
        row.PPContextMenu(() => VCSFileMenu.Entries(_repo, new List<VCSChangeSelection> { new(f.Path, VCSChangeSide.Conflict) }));
        return row.Id("vcs.conflicts.row." + f.Path);
    }

    private UIElement ResolvedState(VCSStatus st) =>
        st.Operation != null
            ? new PPEmptyState("circle-check", L.T("vcs.conflicts.allResolved"), L.T("vcs.op.continue"), () => VCSConflictActions.ContinueOp(_repo), "play")
            : new PPEmptyState("circle-check", L.T("vcs.conflicts.none"));
}

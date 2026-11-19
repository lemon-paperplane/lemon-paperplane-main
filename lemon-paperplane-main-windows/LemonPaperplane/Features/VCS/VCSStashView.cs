using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 储藏：列表（信息、分支、时间）、查看文件、应用 / 弹出 / 恢复为分支 / 删除、新建储藏（对应 Mac VCSStashView.swift）。

public static class VCSStashDialogs
{
    /// <summary>新建储藏；paths 非空时只储藏这些文件</summary>
    public static void PresentNew(VCSRepo repo, List<string>? paths)
    {
        var fields = new List<FormField> { new() { Key = "message", Title = L.T("vcs.stash.message"), Type = "string" } };
        if (paths == null) fields.Add(new FormField { Key = "includeUntracked", Title = L.T("vcs.stash.includeUntracked"), Type = "boolean" });
        fields.Add(new FormField { Key = "keepIndex", Title = L.T("vcs.stash.keepIndex"), Type = "boolean", Info = L.T("vcs.stash.keepIndexInfo") });
        var title = paths != null ? L.T("vcs.stash.newFiles", paths.Count) : L.T("vcs.stash.new");
        Dialogs.Form(title, fields, async v =>
        {
            var msg = VCSForm.Str(v, "message").Trim();
            var args = new VCSStashSaveArgs
            {
                Message = msg.Length == 0 ? null : msg,
                IncludeUntracked = VCSForm.Bool(v, "includeUntracked") ? true : null,
                Paths = paths,
                KeepIndex = VCSForm.Bool(v, "keepIndex") ? true : null,
            };
            await repo.RunThrowing(VCSOp.StashSave, args, L.T("vcs.stash.new"));
            await repo.LoadStashes();
        }, ok: L.T("vcs.stash.save"));
    }

    public static void Apply(VCSRepo repo, VCSStash s, bool pop, bool restoreIndex) =>
        repo.Perform(VCSOp.StashApply, new VCSStashArgs { Index = s.Index, Pop = pop ? true : null, RestoreIndex = restoreIndex ? true : null },
            pop ? L.T("vcs.stash.pop") : L.T("vcs.stash.apply"), then: async _ => await repo.LoadStashes());

    public static void AsBranch(VCSRepo repo, VCSStash s) =>
        Dialogs.Input(L.T("vcs.stash.asBranch"), L.T("vcs.branch.name"), async name =>
        {
            await repo.RunThrowing(VCSOp.StashApply, new VCSStashArgs { Index = s.Index, Branch = name }, L.T("vcs.stash.asBranch"));
            await repo.LoadStashes();
        }, ok: L.T("common.create"));

    public static void Drop(VCSRepo repo, VCSStash s) =>
        Dialogs.Confirm(L.T("vcs.stash.dropTitle", $"stash@{{{s.Index}}}"), s.Message, async () =>
        {
            await repo.RunThrowing(VCSOp.StashDrop, new VCSStashArgs { Index = s.Index }, L.T("vcs.stash.drop"), quietSuccess: true);
            await repo.LoadStashes();
        }, ok: L.T("common.delete"), danger: true);

    public static void Clear(VCSRepo repo) =>
        Dialogs.Confirm(L.T("vcs.stash.clearTitle"), L.T("vcs.stash.clearMessage", repo.Stashes.Count), async () =>
        {
            await repo.RunThrowing(VCSOp.StashDrop, new VCSStashArgs { Index = 0, All = true }, L.T("vcs.stash.clear"), quietSuccess: true);
            await repo.LoadStashes();
        }, ok: L.T("vcs.stash.clear"), danger: true);
}

/// <summary>储藏子页。对应 Mac VCSStashPage。</summary>
public sealed class VCSStashPage : PPView
{
    private readonly VCSRepo _repo;
    private readonly State<int?> _selected;
    private readonly State<VCSStashShow?> _show;
    private readonly State<string?> _showError;
    private readonly State<bool> _restoreIndex;

    public VCSStashPage(VCSRepo repo)
    {
        _repo = repo;
        _selected = UseState<int?>(null);
        _show = UseState<VCSStashShow?>(null);
        _showError = UseState<string?>(null);
        _restoreIndex = UseState(false);
        Observe(repo, nameof(VCSRepo.Stashes), nameof(VCSRepo.Status));
    }

    private VCSStash? Current => _repo.Stashes.FirstOrDefault(s => s.Index == _selected.Value);

    protected override async void OnAppear()
    {
        _repo.PropertyChanged += OnRepo;
        await _repo.LoadStashes();
        if (_selected.Value == null && _repo.Stashes.FirstOrDefault() is { } f) Select(f);
    }

    protected override void OnDisappear() => _repo.PropertyChanged -= OnRepo;

    private async void OnRepo(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VCSRepo.Revision)) await _repo.LoadStashes();
        else if (e.PropertyName == nameof(VCSRepo.Stashes))
        {
            var list = _repo.Stashes;
            if (_selected.Value is int sel && list.All(s => s.Index != sel))
            {
                _selected.Value = null;
                _show.Value = null;
            }
            if (_selected.Value == null && list.FirstOrDefault() is { } f) Select(f);
        }
    }

    private async void Select(VCSStash s)
    {
        _selected.Value = s.Index;
        _show.Value = null;
        _showError.Value = null;
        try
        {
            var r = await _repo.Api().VcsStashShowAsync(_repo.ProjectId, s.Index, _repo.Root);
            if (_selected.Value == s.Index) _show.Value = r;
        }
        catch (Exception e)
        {
            _showError.Value = e.Message;
        }
    }

    protected override UIElement Build()
    {
        var tb = HStack(M.Xs,
            new PPButton(L.T("vcs.stash.new"), () => VCSStashDialogs.PresentNew(_repo, null), icon: "plus", size: PPButtonSize.Small)
                .Disabled(_repo.Status?.Files.Count is null or 0).Id("vcs.stash.new"),
            Spacer(),
            new PPIconButton("trash-2", L.T("vcs.stash.clear"), () => VCSStashDialogs.Clear(_repo), size: M.IconButtonSmall, iconSize: M.IconSmall).Disabled(_repo.Stashes.Count == 0),
            new PPIconButton("refresh-cw", L.T("vcs.refresh"), async () => await _repo.LoadStashes(), size: M.IconButtonSmall, iconSize: M.IconSmall));
        tb.Padding = new Thickness(M.Md, 0, M.Md, 0);
        tb.Height = M.ToolbarHeight;
        UIElement list;
        if (_repo.Stashes.Count == 0) list = new PPEmptyState("archive", L.T("vcs.stash.empty"));
        else
        {
            var col = VStack(1, _repo.Stashes.Select(Row));
            col.Align = CrossAlign.Stretch;
            col.Padding = new Thickness(M.Xs);
            list = Scroll(col).Key("vcs.stash.list");
        }
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.Children.Add(tb.At(0, 0));
        left.Children.Add(new PPDivider(subtle: true).At(1, 0));
        left.Children.Add(list.At(2, 0));
        UIElement right = Current is { } s ? Detail(s) : new PPEmptyState("archive", L.T("vcs.stash.select"));
        return new PPSplitView(left, right, UIState.Bind("vcs.stash.left", 380.0), 260, 600);
    }

    private UIElement Row(VCSStash s)
    {
        var content = HStack(M.Sm, new PPIcon("archive", M.IconSmall, ColorToken.IconSecondary),
            VStack(0,
                Text(s.Message.Length == 0 ? $"stash@{{{s.Index}}}" : s.Message),
                HStack(M.Xs, s.Branch != null ? Text(s.Branch, Typo.Caption, ColorToken.TextTertiary) : null, Text(VCSDate.Short(s.ParsedDate), Typo.Caption, ColorToken.TextTertiary).Shrink(false)))
                .Flex());
        content.Margin = new Thickness(M.Sm, 0, M.Sm, 0);
        var row = VCSViews.TableRow(content, s.Index == _selected.Value, () => Select(s), height: 38);
        row.PPContextMenu(() => new[]
        {
            MenuEntry.Item(L.T("vcs.stash.apply"), () => VCSStashDialogs.Apply(_repo, s, false, _restoreIndex.Value), icon: "archive-restore"),
            MenuEntry.Item(L.T("vcs.stash.pop"), () => VCSStashDialogs.Apply(_repo, s, true, _restoreIndex.Value), icon: "arrow-up-from-line"),
            MenuEntry.Item(L.T("vcs.stash.asBranch"), () => VCSStashDialogs.AsBranch(_repo, s), icon: "git-branch"),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("vcs.stash.drop"), () => VCSStashDialogs.Drop(_repo, s), icon: "trash-2", destructive: true),
        });
        return row.Id("vcs.stash.row." + s.Index);
    }

    private UIElement Detail(VCSStash s)
    {
        var meta = HStack(M.Md, Text($"stash@{{{s.Index}}}", Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false),
            s.Branch != null ? HStack(M.Xs, new PPIcon("git-branch", 12), Text(s.Branch, Typo.Small)).Fg(ColorToken.TextSecondary) : null,
            Text(VCSDate.Full(s.ParsedDate), Typo.Small, ColorToken.TextSecondary).Shrink(false));
        var actions = HStack(M.Sm,
            new PPButton(L.T("vcs.stash.apply"), () => VCSStashDialogs.Apply(_repo, s, false, _restoreIndex.Value), icon: "archive-restore", kind: PPButtonKind.Primary, size: PPButtonSize.Small).Id("vcs.stash.apply"),
            new PPButton(L.T("vcs.stash.pop"), () => VCSStashDialogs.Apply(_repo, s, true, _restoreIndex.Value), icon: "arrow-up-from-line", size: PPButtonSize.Small),
            new PPButton(L.T("vcs.stash.asBranch"), () => VCSStashDialogs.AsBranch(_repo, s), icon: "git-branch", size: PPButtonSize.Small),
            new PPButton(L.T("vcs.stash.drop"), () => VCSStashDialogs.Drop(_repo, s), icon: "trash-2", kind: PPButtonKind.Ghost, size: PPButtonSize.Small),
            VCSViews.Small(new PPCheckbox(_restoreIndex, L.T("vcs.stash.restoreIndex"))));
        var head = VStack(M.Sm, Selectable(s.Message.Length == 0 ? $"stash@{{{s.Index}}}" : s.Message, Typo.Title), meta, actions);
        head.Align = CrossAlign.Stretch;
        head.Padding = new Thickness(M.Lg);
        UIElement body;
        if (_showError.Value is { } e) body = new PPAlert("danger", e).Margins(M.Md).Top();
        else if (_show.Value is { } sh)
        {
            var col = VStack(2, Text(L.T("vcs.log.files", sh.Files.Count), Typo.Label, ColorToken.TextSecondary).Margins(M.Sm, 0, M.Sm, M.Xs));
            foreach (var f in sh.Files)
                col.Children.Add(new VCSFileLine(f, false, () => VCSDiffDialog.PresentStash(_repo, s, sh, f)));
            if (sh.Untracked.Count > 0)
            {
                col.Children.Add(Text(L.T("vcs.stash.untracked", sh.Untracked.Count), Typo.Label, ColorToken.TextSecondary).Margins(M.Sm, M.Md, M.Sm, M.Xs));
                foreach (var p in sh.Untracked)
                {
                    var r = HStack(M.Sm, VCSViews.StatusLetter("?"), TextMiddle(p, Typo.Body, ColorToken.TextSecondary), Spacer());
                    r.Padding = new Thickness(M.Sm, 0, M.Sm, 0);
                    r.Height = M.RowHeightCompact;
                    col.Children.Add(r);
                }
            }
            col.Align = CrossAlign.Stretch;
            col.Padding = new Thickness(M.Md);
            body = Scroll(col).Key("vcs.stash.files");
        }
        else body = new PPSpinner().Center();
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(head.At(0, 0));
        g.Children.Add(new PPDivider(subtle: true).At(1, 0));
        g.Children.Add(body.At(2, 0));
        return g;
    }
}

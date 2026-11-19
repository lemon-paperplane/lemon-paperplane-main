using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 分支：工具栏分支弹出框、分支页（本地 / 远程 / 标签）、分支操作（对应 Mac VCSBranches.swift）。

public static class VCSBranchActions
{
    /// <summary>签出（本地修改会被覆盖时给「智能签出」「强制签出」）</summary>
    public static void Checkout(VCSRepo repo, string @ref, string? title = null, Action? then = null)
    {
        var t = title ?? L.T("vcs.checkout.title", @ref);
        repo.Perform(VCSOp.Checkout, new VCSCheckoutArgs { Ref = @ref }, t, extra: r =>
        {
            if (r.Reason != VCSReason.LocalChanges) return new List<VCSNotice.Action>();
            return new List<VCSNotice.Action>
            {
                new(L.T("vcs.checkout.smart"), () =>
                {
                    repo.Notice = null;
                    repo.Perform(VCSOp.Checkout, new VCSCheckoutArgs { Ref = @ref, Smart = true }, t, then: x => { if (x.Ok) then?.Invoke(); });
                }, primary: true),
                new(L.T("vcs.checkout.force"), () =>
                    Dialogs.Confirm(L.T("vcs.checkout.forceTitle"), L.T("vcs.checkout.forceMessage"), async () =>
                    {
                        repo.Notice = null;
                        await repo.RunThrowing(VCSOp.Checkout, new VCSCheckoutArgs { Ref = @ref, Force = true }, t);
                    }, ok: L.T("vcs.checkout.force"), danger: true), danger: true),
            };
        }, then: r => { if (r.Ok) then?.Invoke(); });
    }

    public static void NewBranch(VCSRepo repo, string? start, string? startTitle)
    {
        var fields = new List<FormField>
        {
            new() { Key = "name", Title = L.T("vcs.branch.name"), Type = "string", Required = true },
            new() { Key = "checkout", Title = L.T("vcs.branch.checkoutAfter"), Type = "boolean", Default = true },
        };
        var title = startTitle != null ? L.T("vcs.branch.newFromTitle", startTitle) : L.T("vcs.branch.new");
        Dialogs.Form(title, fields, async v =>
        {
            var name = VCSForm.Str(v, "name").Trim();
            if (VCSForm.Bool(v, "checkout", true))
            {
                await repo.RunThrowing(VCSOp.Checkout, new VCSCheckoutArgs { Ref = name, Create = true, Start = start }, L.T("vcs.branch.new"), extra: r =>
                {
                    if (r.Reason != VCSReason.LocalChanges) return new List<VCSNotice.Action>();
                    return new List<VCSNotice.Action>
                    {
                        new(L.T("vcs.checkout.smart"), () =>
                        {
                            repo.Notice = null;
                            repo.Perform(VCSOp.Checkout, new VCSCheckoutArgs { Ref = name, Create = true, Start = start, Smart = true }, L.T("vcs.branch.new"));
                        }, primary: true),
                    };
                });
            }
            else
            {
                await repo.RunThrowing(VCSOp.BranchCreate, new VCSBranchCreateArgs { Name = name, Start = start }, L.T("vcs.branch.new"));
            }
            await repo.LoadBranches();
        }, ok: L.T("common.create"));
    }

    public static void CheckoutAndRebase(VCSRepo repo, string branch)
    {
        if (repo.CurrentBranch is not { } cur) return;
        Checkout(repo, branch, then: () =>
            repo.Perform(VCSOp.Rebase, new VCSRefArgs { Ref = cur }, L.T("vcs.branch.rebaseOnto", branch, cur), @long: true));
    }

    public static void Merge(VCSRepo repo, string @ref)
    {
        var cur = repo.HeadTitle;
        var title = L.T("vcs.branch.mergeInto", @ref, cur);
        repo.Perform(VCSOp.Merge, new VCSRefArgs { Ref = @ref }, title, @long: true, extra: r =>
        {
            if (r.Reason != VCSReason.UnrelatedHistories) return new List<VCSNotice.Action>();
            return new List<VCSNotice.Action>
            {
                new(L.T("vcs.merge.allowUnrelated"), () =>
                {
                    repo.Notice = null;
                    repo.Perform(VCSOp.Merge, new VCSRefArgs { Ref = @ref, AllowUnrelated = true }, title, @long: true);
                }, danger: true),
            };
        });
    }

    public static void Rebase(VCSRepo repo, string onto) =>
        repo.Perform(VCSOp.Rebase, new VCSRefArgs { Ref = onto }, L.T("vcs.branch.rebaseOnto", repo.HeadTitle, onto), @long: true);

    public static void Rename(VCSRepo repo, string branch) =>
        Dialogs.Input(L.T("vcs.branch.rename"), L.T("vcs.branch.name"), async name =>
        {
            await repo.RunThrowing(VCSOp.BranchRename, new VCSBranchRenameArgs { Old = branch, New = name }, L.T("vcs.branch.rename"));
            await repo.LoadBranches();
        }, initial: branch, ok: L.T("common.save"));

    public static void Delete(VCSRepo repo, string branch) =>
        Dialogs.Confirm(L.T("vcs.branch.deleteTitle", branch), null, async () =>
        {
            var r = await repo.RunThrowing(VCSOp.BranchDelete, new VCSBranchDeleteArgs { Name = branch }, L.T("vcs.branch.delete"), quietSuccess: true, extra: x =>
            {
                if (x.Reason != VCSReason.NotFullyMerged) return new List<VCSNotice.Action>();
                return new List<VCSNotice.Action>
                {
                    new(L.T("vcs.branch.forceDelete"), () => { repo.Notice = null; DeleteForced(repo, branch); }, danger: true),
                };
            });
            if (r.Ok) Deleted(repo, branch, r.Hash);
            await repo.LoadBranches();
        }, ok: L.T("common.delete"), danger: true);

    private static void DeleteForced(VCSRepo repo, string branch) =>
        repo.Perform(VCSOp.BranchDelete, new VCSBranchDeleteArgs { Name = branch, Force = true }, L.T("vcs.branch.delete"), quietSuccess: true,
            then: r => { if (r.Ok) Deleted(repo, branch, r.Hash); });

    /// <summary>删除成功：提示里给「恢复」</summary>
    private static void Deleted(VCSRepo repo, string branch, string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            AppModel.Shared.Toasts.Show("success", L.T("vcs.branch.deleted", branch));
            return;
        }
        AppModel.Shared.Toasts.Show("success", L.T("vcs.branch.deleted", branch), null, L.T("vcs.branch.restore"), () =>
            repo.Perform(VCSOp.BranchCreate, new VCSBranchCreateArgs { Name = branch, Start = hash }, L.T("vcs.branch.restore")));
    }

    public static void DeleteRemote(VCSRepo repo, string branch) =>
        Dialogs.Confirm(L.T("vcs.branch.deleteRemoteTitle", branch), L.T("vcs.branch.deleteRemoteMessage"), async () =>
        {
            await repo.RunThrowing(VCSOp.BranchDelete, new VCSBranchDeleteArgs { Name = branch, Remote = true }, L.T("vcs.branch.deleteRemote"), @long: true);
            await repo.LoadBranches();
        }, ok: L.T("common.delete"), danger: true);

    public static async void SetUpstream(VCSRepo repo, string branch)
    {
        if (repo.Branches == null) await repo.LoadBranches();
        var remotes = repo.Branches?.Remote.Select(b => b.Name).ToList() ?? new List<string>();
        var current = repo.Branches?.Local.FirstOrDefault(b => b.Name == branch)?.Upstream ?? "";
        var options = new List<Option> { new() { Value = "", Label = L.T("vcs.branch.noUpstream") } };
        options.AddRange(remotes.Select(r => new Option { Value = r }));
        var field = new FormField { Key = "upstream", Title = L.T("vcs.branch.upstream"), Type = "select", Options = options };
        var guess = current.Length == 0 ? remotes.FirstOrDefault(r => r.EndsWith("/" + branch, StringComparison.Ordinal)) ?? "" : current;
        Dialogs.Form(L.T("vcs.branch.setUpstreamTitle", branch), new List<FormField> { field }, async v =>
        {
            await repo.RunThrowing(VCSOp.SetUpstream, new VCSSetUpstreamArgs { Branch = branch, Upstream = VCSForm.Str(v, "upstream") }, L.T("vcs.branch.setUpstream"));
            await repo.LoadBranches();
        }, values: new Dictionary<string, JToken> { ["upstream"] = guess }, ok: L.T("common.save"));
    }

    /// <summary>签出标签或修订（输入引用）</summary>
    public static void CheckoutRef(VCSRepo repo) =>
        Dialogs.Input(L.T("vcs.checkoutRef"), L.T("vcs.checkoutRef.label"), r =>
        {
            Checkout(repo, r);
            return Task.CompletedTask;
        }, ok: L.T("vcs.checkout"));

    /// <summary>分支右键 / 弹出框里的操作</summary>
    public static List<MenuEntry> Entries(VCSRepo repo, VCSBranch b, bool remote)
    {
        var cur = repo.CurrentBranch;
        bool isCurrent = !remote && b.Name == cur;
        var e = new List<MenuEntry>();
        if (!isCurrent)
            e.Add(MenuEntry.Item(L.T("vcs.checkout"), () => Checkout(repo, b.Name), icon: "check", disabled: b.Worktree != null));
        e.Add(MenuEntry.Item(L.T("vcs.branch.newFromNamed", b.Name), () => NewBranch(repo, b.Name, b.Name), icon: "git-branch"));
        if (!isCurrent && !remote && cur != null)
            e.Add(MenuEntry.Item(L.T("vcs.branch.checkoutAndRebase", cur), () => CheckoutAndRebase(repo, b.Name), icon: "git-pull-request-arrow", disabled: b.Worktree != null));
        if (!isCurrent)
        {
            e.Add(MenuEntry.Divider);
            e.Add(MenuEntry.Item(L.T("vcs.branch.compareWith", repo.HeadTitle), () =>
                VCSCompareDialog.Present(repo, b.Name, null, L.T("vcs.compare.title", b.Name, repo.HeadTitle)), icon: "git-compare"));
            e.Add(MenuEntry.Item(L.T("vcs.branch.rebaseCurrentOnto", repo.HeadTitle, b.Name), () => Rebase(repo, b.Name), icon: "git-pull-request-arrow"));
            e.Add(MenuEntry.Item(L.T("vcs.branch.mergeInto", b.Name, repo.HeadTitle), () => Merge(repo, b.Name), icon: "git-merge"));
        }
        e.Add(MenuEntry.Divider);
        if (!remote)
        {
            e.Add(MenuEntry.Item(L.T("vcs.push.ellipsis"), () => VCSPushDialog.Present(repo, b.Name), icon: "arrow-up"));
            e.Add(MenuEntry.Item(L.T("vcs.branch.setUpstream"), () => SetUpstream(repo, b.Name), icon: "link"));
            e.Add(MenuEntry.Item(L.T("vcs.branch.rename"), () => Rename(repo, b.Name), icon: "pencil"));
        }
        e.Add(MenuEntry.Item(L.T("vcs.copyName"), () => Actions.Copy(b.Name), icon: "copy"));
        if (remote)
        {
            e.Add(MenuEntry.Divider);
            e.Add(MenuEntry.Item(L.T("vcs.branch.deleteRemote"), () => DeleteRemote(repo, b.Name), icon: "trash-2", destructive: true));
        }
        else if (!isCurrent)
        {
            e.Add(MenuEntry.Divider);
            e.Add(MenuEntry.Item(L.T("vcs.branch.delete"), () => Delete(repo, b.Name), icon: "trash-2", destructive: true));
        }
        return e;
    }
}

// MARK: - 工具栏分支按钮 + 弹出框

/// <summary>分支按钮（点开分支弹出框）。对应 Mac VCSBranchButton。状态变化只换按钮里的内容（弹出框的锚点不变，弹出框不会被关掉）。</summary>
public sealed class VCSBranchButton : PPButtonBase
{
    private readonly VCSRepo _repo;
    private readonly bool _showCounts;
    private readonly Border _bg;
    private PPPopup? _open;

    public VCSBranchButton(VCSRepo repo, bool showCounts = true, bool compact = false)
    {
        _repo = repo;
        _showCounts = showCounts;
        _bg = new Border
        {
            Padding = new Thickness(compact ? M.Sm : M.Md, 0, compact ? M.Sm : M.Md, 0),
            Height = compact ? M.ControlHeightSmall : M.ControlHeight,
            CornerRadius = new CornerRadius(M.RadiusMD),
            SnapsToDevicePixels = true,
        };
        Compact = compact;
        _bg.Fg(ColorToken.TextPrimary);
        Content = _bg;
        Action = Toggle;
        this.Id("vcs.branchButton");
        Loaded += (_, _) => { _repo.PropertyChanged -= OnRepo; _repo.PropertyChanged += OnRepo; Render(); };
        Unloaded += (_, _) => _repo.PropertyChanged -= OnRepo;
        Render();
    }

    private bool Compact { get; }

    private void OnRepo(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VCSRepo.Status)) Render();
    }

    private void Render()
    {
        var s = _repo.Status;
        var row = HStack(M.Sm,
            new PPIcon(s?.Detached == true ? "circle-dot" : "git-branch", M.Icon, ColorToken.IconPrimary),
            TextMiddle(_repo.HeadTitle.Length == 0 ? "\u2026" : _repo.HeadTitle, Compact ? Typo.Body : Typo.BodyStrong));
        if (_showCounts && s != null)
        {
            if (s.Ahead > 0) row.Children.Add(Text("\u2191" + s.Ahead, Typo.MonoSmall, ColorToken.Success).Shrink(false));
            if (s.Behind > 0) row.Children.Add(Text("\u2193" + s.Behind, Typo.MonoSmall, ColorToken.Info).Shrink(false));
            if (s.UpstreamGone) row.Children.Add(new PPIcon("triangle-alert", 12, ColorToken.Warning).Tip(L.T("vcs.branch.upstreamGone")));
        }
        row.Children.Add(new PPIcon("chevron-down", 12, ColorToken.IconSecondary));
        row.VerticalAlignment = VerticalAlignment.Center;
        _bg.Child = row;
        this.Tip(s?.Upstream is { } up ? L.T("vcs.branch.tracking", up) : null);
        this.A11y(_repo.HeadTitle);
        UpdateVisual();
    }

    private void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        if (PopupController.WasJustDismissed(this)) return;
        _open = Popover.Show(this, new VCSBranchPopover(_repo, () => _open?.Close()), wantsKey: true, onClose: () =>
        {
            _open = null;
            UpdateVisual();
        });
        UpdateVisual();
    }

    public override void UpdateVisual()
    {
        if (_bg == null) return;
        _bg.Bg(_open != null || Pressed ? ColorToken.BgPressed : Hover ? ColorToken.BgHover : ColorToken.Clear);
    }
}

internal sealed class VCSBranchPopover : PPView
{
    private readonly VCSRepo _repo;
    private readonly Action _close;
    private readonly State<string> _query = new("");

    public VCSBranchPopover(VCSRepo repo, Action close)
    {
        _repo = repo;
        _close = close;
    }

    protected override async void OnAppear() => await _repo.LoadBranches();

    protected override UIElement Build()
    {
        var field = Keep("search", () => new PPTextField(L.T("vcs.branch.search"), _query, icon: "search", clearable: true, height: M.ControlHeightSmall).Id("vcs.branch.search"));
        UI.Detach(field);
        var col = VStack(0, Box(field).Pad(M.Sm), new BranchList(_repo, _query, _close));
        col.Align = CrossAlign.Stretch;
        col.Width = 360;
        return col;
    }

    /// <summary>筛选结果（单独的视图：输入时只重建列表）</summary>
    private sealed class BranchList : PPView
    {
        private readonly VCSRepo _repo;
        private readonly State<string> _query;
        private readonly Action _close;
        private readonly VCSVirtualList _list = new(new Thickness(M.Xs, 0, M.Xs, M.Xs)) { FitContent = true };
        private readonly ScrollViewer _scroll;

        public BranchList(VCSRepo repo, State<string> query, Action close)
        {
            _repo = repo;
            _query = query;
            _close = close;
            _scroll = _list.InScroll();
            _scroll.MaxHeight = 460;
            Observe(query);
            Observe(repo, nameof(VCSRepo.Branches), nameof(VCSRepo.Status));
        }

        private bool Match(string name) => _query.Value.Length == 0 || name.IndexOf(_query.Value, StringComparison.CurrentCultureIgnoreCase) >= 0;

        protected override UIElement Build()
        {
            var rows = new List<VRow>();
            if (_query.Value.Length == 0)
            {
                rows.Add(new VRow("a:new", M.RowHeight, () => ActionRow(L.T("vcs.branch.new"), "plus", () => { _close(); VCSBranchActions.NewBranch(_repo, null, null); })));
                rows.Add(new VRow("a:ref", M.RowHeight, () => ActionRow(L.T("vcs.checkoutRef"), "circle-dot", () => { _close(); VCSBranchActions.CheckoutRef(_repo); })));
                rows.Add(new VRow("a:div", 1 + M.Xs * 2, () => new PPDivider(subtle: true).Margins(0, M.Xs, 0, M.Xs)));
            }
            if (_repo.Branches is not { } b)
            {
                rows.Add(new VRow("spin", 40, () => new PPSpinner(M.IconSmall).Center()));
            }
            else
            {
                var recent = _query.Value.Length == 0
                    ? b.Recent.Where(n => n != b.Current).Select(n => b.Local.FirstOrDefault(x => x.Name == n)).Where(x => x != null).Select(x => x!).Take(5).ToList()
                    : new List<VCSBranch>();
                var local = b.Local.Where(x => Match(x.Name)).ToList();
                var remote = b.Remote.Where(x => Match(x.Name)).ToList();
                if (recent.Count > 0)
                {
                    rows.Add(new VRow("h:recent", M.RowHeightCompact + M.Sm, () => Header(L.T("vcs.branch.recent"))));
                    foreach (var br in recent) rows.Add(new VRow("r:" + br.Name, M.RowHeight, () => BranchRow(br, false)));
                }
                if (local.Count > 0)
                {
                    rows.Add(new VRow("h:local", M.RowHeightCompact + M.Sm, () => Header(L.T("vcs.branch.local"))));
                    foreach (var br in local) rows.Add(new VRow("l:" + br.Name, M.RowHeight, () => BranchRow(br, false)));
                }
                if (remote.Count > 0)
                {
                    rows.Add(new VRow("h:remote", M.RowHeightCompact + M.Sm, () => Header(L.T("vcs.branch.remote"))));
                    foreach (var br in remote) rows.Add(new VRow("o:" + br.Name, M.RowHeight, () => BranchRow(br, true)));
                }
                if (local.Count == 0 && remote.Count == 0)
                    rows.Add(new VRow("none", 40, () => Text(L.T("vcs.branch.noMatch"), Typo.Body, ColorToken.TextTertiary).Margins(M.Md)));
            }
            _list.SetRows(rows);
            UI.Detach(_scroll);
            return _scroll;
        }

        private static FrameworkElement Header(string t) =>
            Text(t, Typo.Caption, ColorToken.TextTertiary).Margins(M.Md, M.Sm, M.Md, 0).Bottom();

        private static FrameworkElement ActionRow(string title, string icon, Action action) =>
            new VCSPopoverRow(_ => HStack(M.Md, new PPIcon(icon, M.Icon), Text(title), Spacer()), action);

        private FrameworkElement BranchRow(VCSBranch br, bool remote)
        {
            VCSPopoverRow? row = null;
            row = new VCSPopoverRow(hl =>
            {
                var r = HStack(M.Sm,
                    new PPIcon(br.Current ? "check" : remote ? "cloud" : "git-branch", M.IconSmall,
                        hl ? ColorToken.TextOnAccent : br.Current ? ColorToken.Accent : ColorToken.IconSecondary),
                    TextMiddle(br.Name, br.Current ? Typo.BodyStrong : Typo.Body),
                    br.Ahead > 0 ? Text("↑" + br.Ahead, Typo.MonoSmall).Shrink(false) : null,
                    br.Behind > 0 ? Text("↓" + br.Behind, Typo.MonoSmall).Shrink(false) : null,
                    br.Gone ? new PPIcon("triangle-alert", 12, hl ? ColorToken.TextOnAccent : ColorToken.Warning).Tip(L.T("vcs.branch.upstreamGone")) : null,
                    br.Worktree != null ? new PPIcon("git-fork", 12).Tip(L.T("vcs.branch.inWorktree", br.Worktree)) : null,
                    Spacer(M.Md),
                    new PPIcon("chevron-right", 12, hl ? ColorToken.TextOnAccent : ColorToken.IconSecondary));
                return r;
            }, () =>
            {
                var pos = Mouse.GetPosition(row);
                if (pos.X < 0 || pos.X > row!.ActualWidth) pos = new Point(row!.ActualWidth - 20, row.ActualHeight / 2);
                MenuPresenter.ShowAt(VCSBranchActions.Entries(_repo, br, remote), row!, pos);
            });
            row.Tip(br.Subject).Id("vcs.branch.popover." + br.Name);
            return row;
        }
    }
}

/// <summary>弹出框里的可点击行（悬停强调色底）。对应 Mac VCSPopoverRow。</summary>
public sealed class VCSPopoverRow : Border
{
    private readonly Func<bool, UIElement> _content;
    private bool _hl;

    public VCSPopoverRow(Func<bool, UIElement> content, Action action)
    {
        _content = content;
        Padding = new Thickness(M.Md, 0, M.Md, 0);
        Height = M.RowHeight;
        CornerRadius = new CornerRadius(M.RadiusSM);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        SnapsToDevicePixels = true;
        MouseEnter += (_, _) => SetHighlight(true);
        MouseLeave += (_, _) => SetHighlight(false);
        this.OnClick(action);
        Render();
    }

    private void SetHighlight(bool on)
    {
        if (_hl == on) return;
        _hl = on;
        Render();
    }

    private void Render()
    {
        var c = _content(_hl);
        if (c is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Child = c;
        this.Fg(_hl ? ColorToken.TextOnAccent : ColorToken.TextPrimary);
        this.Bg(_hl ? ColorToken.Accent : ColorToken.Clear);
    }
}

// MARK: - 分支页

public sealed class VCSBranchesPage : PPView
{
    private readonly VCSRepo _repo;
    private readonly State<string> _query = new("");
    private readonly State<HashSet<string>> _collapsed = new(new HashSet<string>());
    private readonly State<string?> _selected = new(null);

    public VCSBranchesPage(VCSRepo repo)
    {
        _repo = repo;
        Observe(AppModel.Shared.Data, nameof(DataStore.Contributions));
    }

    protected override void OnAppear()
    {
        _repo.PropertyChanged += OnRepoChanged;
        Reload();
    }

    protected override void OnDisappear() => _repo.PropertyChanged -= OnRepoChanged;

    private void OnRepoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VCSRepo.Revision)) Reload();
    }

    private async void Reload()
    {
        await _repo.LoadBranches();
        await _repo.LoadTags();
    }

    protected override UIElement Build()
    {
        var search = Keep("search", () => new PPTextField(L.T("vcs.branch.search"), _query, icon: "search", clearable: true, height: M.ControlHeightSmall)
            .W(280).Shrink().Id("vcs.branches.search"));
        UI.Detach(search);
        var tb = HStack(M.Sm, search,
            new PPButton(L.T("vcs.branch.new"), () => VCSBranchActions.NewBranch(_repo, null, null), icon: "plus", size: PPButtonSize.Small),
            new PPButton(L.T("vcs.fetch"), () => VCSRemoteActions.Fetch(_repo), icon: "cloud-download", size: PPButtonSize.Small),
            Spacer());
        foreach (var a in VCSPluginActions.ActionsAt(ActionLocation.VcsToolbar).Where(a => a.Id == "cleanupBranches"))
            tb.Children.Add(new PPButton(a.Title, () => VCSPluginActions.Invoke(a, _repo, new Dictionary<string, JToken>()), size: PPButtonSize.Small,
                iconRef: IconKey.Parse(a.Icon, a.Plugin)));
        tb.Children.Add(new PPIconButton("refresh-cw", L.T("vcs.refresh"), Reload, size: M.IconButtonSmall, iconSize: M.IconSmall));
        tb.Padding = new Thickness(M.Md, 0, M.Md, 0);
        tb.Height = M.ToolbarHeight;

        var header = HStack(0,
            Text(L.T("vcs.branch.name"), Typo.Label, ColorToken.TextSecondary).Margins(30, 0, 0, 0).Flex(),
            Text(L.T("vcs.branch.upstream"), Typo.Label, ColorToken.TextSecondary).W(200).Shrink(false),
            Text(L.T("vcs.branch.lastCommit"), Typo.Label, ColorToken.TextSecondary).W(280).Shrink(false),
            Text(L.T("vcs.log.date"), Typo.Label, ColorToken.TextSecondary).W(118).Shrink(false));
        header.Height = M.RowHeightCompact;
        header.Padding = new Thickness(M.Xs, 0, M.Xs, 0);
        header.Bg(ColorToken.BgSubtle);

        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(tb.At(0, 0));
        g.Children.Add(new PPDivider(subtle: true).At(1, 0));
        g.Children.Add(header.At(2, 0));
        g.Children.Add(new PPDivider(subtle: true).At(3, 0));
        g.Children.Add(Keep("list", () => new BranchTable(_repo, _query, _collapsed, _selected)).At(4, 0));
        return g;
    }

    private sealed class BranchTable : PPView
    {
        private readonly VCSRepo _repo;
        private readonly State<string> _query;
        private readonly State<HashSet<string>> _collapsed;
        private readonly State<string?> _selected;
        private readonly VCSVirtualList _list = new(new Thickness(M.Xs));
        private readonly ScrollViewer _scroll;

        public BranchTable(VCSRepo repo, State<string> query, State<HashSet<string>> collapsed, State<string?> selected)
        {
            _repo = repo;
            _query = query;
            _collapsed = collapsed;
            _selected = selected;
            _scroll = _list.InScroll();
            _scroll.Key("vcs.branches.list");
            Observe(query);
            Observe(collapsed);
            Observe(selected);
            Observe(repo, nameof(VCSRepo.Branches), nameof(VCSRepo.Tags), nameof(VCSRepo.Status));
        }

        private bool Match(string n) => _query.Value.Length == 0 || n.IndexOf(_query.Value, StringComparison.CurrentCultureIgnoreCase) >= 0;

        private void Toggle(string key)
        {
            var c = new HashSet<string>(_collapsed.Value);
            if (!c.Remove(key)) c.Add(key);
            _collapsed.Value = c;
        }

        protected override UIElement Build()
        {
            if (_repo.Branches is not { } b) return new PPSpinner().Center();
            var rows = new List<VRow>();
            void Section(string key, string title, int count, IEnumerable<VRow> content)
            {
                bool isCollapsed = _collapsed.Value.Contains(key);
                rows.Add(new VRow("s:" + key, M.RowHeightCompact, () => VCSViews.GroupHeader(title, count, isCollapsed, () => Toggle(key)).Id("vcs.branches.section." + key)));
                if (!isCollapsed) rows.AddRange(content);
            }
            Section("local", L.T("vcs.branch.local"), b.Local.Count,
                b.Local.Where(x => Match(x.Name)).Select(br => new VRow("l:" + br.Name, M.RowHeight, () => BranchRow(br, false))));
            var byRemote = b.Remote.GroupBy(x => x.Remote ?? x.Name.Split('/')[0]).OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in byRemote)
                Section("remote:" + g.Key, L.T("vcs.branch.remoteNamed", g.Key), g.Count(),
                    g.Where(x => Match(x.Name)).Select(br => new VRow("r:" + br.Name, M.RowHeight, () => BranchRow(br, true))).ToList());
            Section("tags", L.T("vcs.branch.tags"), _repo.Tags.Count,
                _repo.Tags.Where(t => Match(t.Name)).Select(t => new VRow("t:" + t.Name, M.RowHeight, () => TagRow(t))));
            _list.SetRows(rows);
            UI.Detach(_scroll);
            return _scroll;
        }

        private FrameworkElement BranchRow(VCSBranch br, bool remote)
        {
            var id = (remote ? "r:" : "l:") + br.Name;
            var shortName = remote && br.Remote != null && br.Name.StartsWith(br.Remote + "/", StringComparison.Ordinal) ? br.Name.Substring(br.Remote.Length + 1) : br.Name;
            var name = HStack(M.Sm,
                new PPIcon(br.Current ? "check" : remote ? "cloud" : "git-branch", M.IconSmall, br.Current ? ColorToken.Accent : ColorToken.IconSecondary),
                TextMiddle(shortName, br.Current ? Typo.BodyStrong : Typo.Body),
                br.Gone ? new PPBadge(L.T("vcs.branch.gone"), tone: "warning") : null,
                br.Worktree is { } wt ? new PPBadge(L.T("vcs.branch.worktree"), icon: "git-fork").Tip(wt) : null);
            name.Margin = new Thickness(18, 0, 0, 0);
            var upstream = HStack(M.Xs,
                Text(br.Upstream ?? "", Typo.Body, ColorToken.TextSecondary),
                br.Ahead > 0 ? Text("↑" + br.Ahead, Typo.MonoSmall, ColorToken.Success).Shrink(false) : null,
                br.Behind > 0 ? Text("↓" + br.Behind, Typo.MonoSmall, ColorToken.Info).Shrink(false) : null).W(200);
            var last = HStack(M.Xs, Text(br.Head ?? "", Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false), Text(br.Subject ?? "", Typo.Body, ColorToken.TextSecondary)).W(280);
            var row = HStack(0, name.Flex(), upstream, last, Text(VCSDate.Short(br.ParsedDate), Typo.Body, ColorToken.TextSecondary).W(118).Shrink(false));
            var tr = VCSViews.TableRow(row, _selected.Value == id, () => _selected.Value = id, () => { if (!br.Current) VCSBranchActions.Checkout(_repo, br.Name); });
            tr.PPContextMenu(() => VCSBranchActions.Entries(_repo, br, remote));
            return tr.Id("vcs.branches.row." + id);
        }

        private FrameworkElement TagRow(VCSTag t)
        {
            var id = "t:" + t.Name;
            var name = HStack(M.Sm, new PPIcon("tag", M.IconSmall, ColorToken.Warning), Text(t.Name),
                t.Annotated && !string.IsNullOrEmpty(t.Message) ? Text(t.Message, Typo.Small, ColorToken.TextTertiary) : null);
            name.Margin = new Thickness(18, 0, 0, 0);
            var row = HStack(0, name.Flex(),
                Text(t.Tagger ?? "", Typo.Body, ColorToken.TextSecondary).W(200).Shrink(false),
                HStack(M.Xs, Text(t.Hash.Length > 7 ? t.Hash.Substring(0, 7) : t.Hash, Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false),
                    Text(t.Subject ?? "", Typo.Body, ColorToken.TextSecondary)).W(280),
                Text(VCSDate.Short(GoTime.Parse(t.Date)), Typo.Body, ColorToken.TextSecondary).W(118).Shrink(false));
            var tr = VCSViews.TableRow(row, _selected.Value == id, () => _selected.Value = id);
            tr.PPContextMenu(() => VCSTagActions.Entries(_repo, t));
            return tr;
        }
    }
}

public static class VCSTagActions
{
    public static List<MenuEntry> Entries(VCSRepo repo, VCSTag t) => new()
    {
        MenuEntry.Item(L.T("vcs.checkout"), () => VCSBranchActions.Checkout(repo, t.Name), icon: "check"),
        MenuEntry.Item(L.T("vcs.branch.newFromNamed", t.Name), () => VCSBranchActions.NewBranch(repo, t.Name, t.Name), icon: "git-branch"),
        MenuEntry.Divider,
        MenuEntry.Item(L.T("vcs.tag.pushOne"), () => repo.Perform(VCSOp.TagPush, new VCSTagPushArgs { Ref = t.Name }, L.T("vcs.tag.pushOne"), @long: true), icon: "arrow-up"),
        MenuEntry.Item(L.T("vcs.copyName"), () => Actions.Copy(t.Name), icon: "copy"),
        MenuEntry.Divider,
        MenuEntry.Item(L.T("vcs.tag.delete"), () =>
            Dialogs.Confirm(L.T("vcs.tag.deleteTitle", t.Name), null, async () =>
            {
                await repo.RunThrowing(VCSOp.TagDelete, new VCSRefArgs { Ref = t.Name }, L.T("vcs.tag.delete"));
                await repo.LoadTags();
            }, ok: L.T("common.delete"), danger: true), icon: "trash-2", destructive: true),
        MenuEntry.Item(L.T("vcs.tag.deleteRemote"), () =>
            Dialogs.Confirm(L.T("vcs.tag.deleteRemoteTitle", t.Name), null, async () =>
                await repo.RunThrowing(VCSOp.TagPush, new VCSTagPushArgs { Ref = t.Name, Delete = true }, L.T("vcs.tag.deleteRemote"), @long: true),
                ok: L.T("common.delete"), danger: true), icon: "trash-2", destructive: true),
    };
}

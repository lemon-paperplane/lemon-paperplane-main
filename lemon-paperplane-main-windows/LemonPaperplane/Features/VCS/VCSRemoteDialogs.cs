using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 远程操作：获取、拉取对话框（merge/rebase/ff-only、自动储藏）、推送对话框（推送预览、远端领先提示、设置上游、
// --force-with-lease 二次确认、推送标签）、添加远程（对应 Mac VCSRemoteDialogs.swift）。

public static class VCSRemoteActions
{
    public static void Fetch(VCSRepo repo) => repo.Perform(VCSOp.Fetch, new VCSFetchArgs(), L.T("vcs.fetch"), @long: true);

    public static void AddRemote(VCSRepo repo, Action? then = null)
    {
        var fields = new List<FormField>
        {
            new() { Key = "name", Title = L.T("vcs.remote.name"), Type = "string", Default = "origin", Required = true },
            new() { Key = "url", Title = L.T("vcs.remote.url"), Type = "string", Placeholder = "https://… / git@…", Required = true },
        };
        Dialogs.Form(L.T("vcs.remote.add"), fields, async v =>
        {
            var name = VCSForm.Str(v, "name");
            var r = new VCSRemote { Name = name.Length == 0 ? "origin" : name, FetchUrl = VCSForm.Str(v, "url") };
            await repo.RunThrowing(VCSOp.RemoteSet, r, L.T("vcs.remote.add"), quietSuccess: true);
            await repo.LoadRemotes();
            then?.Invoke();
        }, ok: L.T("common.create"));
    }

    /// <summary>拉取（带对话框里选的参数）</summary>
    public static void Pull(VCSRepo repo, VCSPullArgs args)
    {
        var title = L.T("vcs.pull");
        repo.Perform(VCSOp.Pull, args, title, @long: true, extra: r =>
        {
            switch (r.Reason)
            {
                case VCSReason.Divergent:
                    return new List<VCSNotice.Action>
                    {
                        new(L.T("vcs.pull.rebase"), () => { repo.Notice = null; Pull(repo, args with { Mode = "rebase" }); }, primary: true),
                        new(L.T("vcs.pull.merge"), () => { repo.Notice = null; Pull(repo, args with { Mode = "merge" }); }),
                    };
                case VCSReason.LocalChanges:
                    return new List<VCSNotice.Action>
                    {
                        new(L.T("vcs.pull.stashAndRetry"), () => { repo.Notice = null; Pull(repo, args with { Autostash = true }); }, primary: true),
                    };
                case VCSReason.NoUpstream:
                    return new List<VCSNotice.Action>
                    {
                        new(L.T("vcs.pull.chooseBranch"), () => { repo.Notice = null; VCSPullDialog.Present(repo); }, primary: true),
                    };
                default:
                    return new List<VCSNotice.Action>();
            }
        });
    }

    public static void Push(VCSRepo repo, VCSPushArgs args) =>
        repo.Perform(VCSOp.Push, args, L.T("vcs.push"), @long: true, extra: r =>
        {
            if (r.Reason != VCSReason.Rejected && !r.Rejected) return new List<VCSNotice.Action>();
            return new List<VCSNotice.Action>
            {
                new(L.T("vcs.push.forceEllipsis"), () => { repo.Notice = null; ConfirmForce(repo, args with { Force = true }); }, danger: true),
            };
        });

    public static void ConfirmForce(VCSRepo repo, VCSPushArgs args) =>
        Dialogs.Confirm(L.T("vcs.push.forceTitle"), L.T("vcs.push.forceMessage"), () => Push(repo, args), ok: L.T("vcs.push.force"), danger: true);
}

// MARK: - 拉取

public static class VCSPullDialog
{
    public static void Present(VCSRepo repo)
    {
        const string id = "vcs-pull";
        Dialogs.Present(id, new Body(id, repo));
    }

    private sealed class Body : PPView
    {
        private readonly string _id;
        private readonly VCSRepo _repo;
        private readonly State<string> _target;
        private readonly State<string> _mode;
        private readonly State<bool> _autostash;

        public Body(string id, VCSRepo repo)
        {
            _id = id;
            _repo = repo;
            _target = UseState("");
            _mode = UseState(VCSPrefs.PullMode);
            _autostash = UseState(VCSPrefs.PullAutostash);
            Observe(repo, nameof(VCSRepo.Branches));
        }

        protected override async void OnAppear()
        {
            if (_repo.Branches == null) await _repo.LoadBranches();
            if (_target.Value.Length == 0) _target.Value = DefaultTarget();
        }

        private string DefaultTarget()
        {
            var remotes = _repo.Branches?.Remote ?? new List<VCSBranch>();
            if (_repo.Status?.Upstream is { } up && !(_repo.Status?.UpstreamGone ?? false) && remotes.Any(r => r.Name == up)) return up;
            if (_repo.CurrentBranch is { } cur && remotes.FirstOrDefault(r => r.Name.EndsWith("/" + cur, StringComparison.Ordinal)) is { } m) return m.Name;
            return remotes.FirstOrDefault()?.Name ?? "";
        }

        private static Stack Labeled(string label, UIElement c) =>
            HStack(M.Md, Text(label, Typo.Body, ColorToken.TextSecondary).W(80).Shrink(false), c is FrameworkElement fe ? fe.Flex() : c);

        protected override UIElement Build()
        {
            var remotes = _repo.Branches?.Remote ?? new List<VCSBranch>();
            var col = VStack(M.Lg);
            col.Align = CrossAlign.Stretch;
            if (remotes.Count == 0 && _repo.Branches != null)
                col.Children.Add(new PPAlert("warning", L.T("vcs.pull.noRemoteBranches"), null,
                    new PPButton(L.T("vcs.remote.add"), () => { Close(); VCSRemoteActions.AddRemote(_repo); }, size: PPButtonSize.Small)));
            else
                col.Children.Add(Labeled(L.T("vcs.pull.from"), new PPDropdown<string>(remotes.Select(r => new DropdownOption<string>(r.Name, r.Name, icon: "cloud")),
                    _target, L.T("vcs.pull.chooseBranch"), fullWidth: true).Id("vcs.pull.target")));
            col.Children.Add(Labeled(L.T("vcs.pull.mode"), HStack(M.Sm,
                new PPSegmented<string>(new[] { ("merge", L.T("vcs.pull.merge")), ("rebase", L.T("vcs.pull.rebase")), ("ff-only", L.T("vcs.pull.ffOnly")) }, _mode),
                new PPInfoButton(L.T("vcs.pull.modeInfo")), Spacer())));
            col.Children.Add(Labeled("", HStack(M.Sm, new PPCheckbox(_autostash, L.T("vcs.pull.autostash")), new PPInfoButton(L.T("vcs.pull.autostashInfo")), Spacer())));
            var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
            var ok = new PPButton(L.T("vcs.pull"), Submit, icon: "arrow-down-to-line", kind: PPButtonKind.Primary).Disabled(_target.Value.Length == 0).Id("dialog.ok");
            return new PPDialog(L.T("vcs.pull.title", _repo.HeadTitle), col, new UIElement[] { cancel, ok }, 480, Close) { DefaultButton = ok, CancelButton = cancel };
        }

        private void Close() => Dialogs.Dismiss(_id);

        private void Submit()
        {
            var target = _target.Value;
            if (target.Length == 0) return;
            VCSPrefs.PullMode = _mode.Value;
            VCSPrefs.PullAutostash = _autostash.Value;
            var args = new VCSPullArgs { Mode = _mode.Value, Autostash = _autostash.Value ? true : null };
            if (target != _repo.Status?.Upstream && _repo.Branches?.Remote.FirstOrDefault(b => b.Name == target) is { } b)
            {
                var remote = b.Remote ?? target.Split('/')[0];
                args.Remote = remote;
                args.Branch = target.Length > remote.Length + 1 ? target.Substring(remote.Length + 1) : target;
            }
            Close();
            VCSRemoteActions.Pull(_repo, args);
        }
    }
}

// MARK: - 推送

public static class VCSPushDialog
{
    public static void Present(VCSRepo repo, string? branch = null)
    {
        const string id = "vcs-push";
        Dialogs.Present(id, new Body(id, repo, branch));
    }

    private sealed class Body : PPView
    {
        private readonly string _id;
        private readonly VCSRepo _repo;
        private readonly string? _branch;
        private readonly State<string> _remote;
        private readonly State<VCSPushPreview?> _preview;
        private readonly State<bool> _loading;
        private readonly State<string?> _error;
        private readonly State<bool> _force;
        private readonly State<bool> _tags;
        private readonly State<string> _tagsMode;
        private readonly State<bool> _setUpstream;
        private readonly State<bool> _noVerify;
        private bool _started;
        private VCSVirtualList? _commitList;
        private ScrollViewer? _commitScroll;
        private VCSPushPreview? _shownPreview;

        public Body(string id, VCSRepo repo, string? branch)
        {
            _id = id;
            _repo = repo;
            _branch = branch;
            _remote = UseState("");
            _preview = UseState<VCSPushPreview?>(null);
            _loading = UseState(true);
            _error = UseState<string?>(null);
            _force = UseState(false);
            _tags = UseState(false);
            _tagsMode = UseState("follow");
            _setUpstream = UseState(true);
            _noVerify = UseState(false);
            Observe(repo, nameof(VCSRepo.Remotes));
        }

        protected override async void OnAppear()
        {
            if (_started) return;
            _started = true;
            await Load();
        }

        private async System.Threading.Tasks.Task Load()
        {
            await _repo.LoadRemotes();
            if (_remote.Value.Length == 0)
            {
                var up = _repo.Status?.Upstream is { } u ? _repo.Remotes.FirstOrDefault(r => u.StartsWith(r.Name + "/", StringComparison.Ordinal))?.Name : null;
                _remote.Value = up ?? _repo.Remotes.FirstOrDefault(r => r.Name == "origin")?.Name ?? _repo.Remotes.FirstOrDefault()?.Name ?? "";
            }
            await LoadPreview();
        }

        private async System.Threading.Tasks.Task LoadPreview()
        {
            _loading.Value = true;
            try
            {
                var p = await _repo.Api().VcsPushPreviewAsync(_repo.ProjectId, new VCSPushArgs { Remote = _remote.Value.Length == 0 ? null : _remote.Value, Branch = _branch }, _repo.Root);
                _preview.Value = p;
                if (_remote.Value.Length == 0) _remote.Value = p.Remote;
                _setUpstream.Value = p.SetUpstream || p.NewBranch;
                _error.Value = null;
            }
            catch (Exception e)
            {
                _error.Value = e.Message;
                _preview.Value = null;
            }
            _loading.Value = false;
        }

        /// <summary>新分支或还没有上游时才给「设置上游」</summary>
        private bool OfferUpstream => _preview.Value?.NewBranch == true || _preview.Value?.SetUpstream == true;

        protected override UIElement Build()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.Children.Add(Target().At(0, 0));
            g.Children.Add(new PPDivider(subtle: true).At(1, 0));
            g.Children.Add(Commits().At(2, 0));
            g.Children.Add(new PPDivider(subtle: true).At(3, 0));
            g.Children.Add(Options().At(4, 0));
            var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
            var ok = new PPButton(_force.Value ? L.T("vcs.push.force") : L.T("vcs.push"), Submit, icon: "arrow-up",
                kind: _force.Value ? PPButtonKind.Danger : PPButtonKind.Primary).Disabled(_preview.Value == null || _remote.Value.Length == 0).Id("dialog.ok");
            return new VCSLargeDialog(_id, L.T("vcs.push.title", _branch ?? _repo.HeadTitle), g, new UIElement[] { cancel, ok }, 680, 620) { DefaultButton = ok };
        }

        private UIElement Target()
        {
            var p = _preview.Value;
            var row = HStack(M.Sm,
                new PPIcon("git-branch", M.Icon, ColorToken.IconSecondary),
                Text(p?.Branch ?? _branch ?? _repo.HeadTitle, Typo.BodyStrong).Shrink(false),
                new PPIcon("arrow-right", M.IconSmall, ColorToken.IconSecondary));
            if (_repo.Remotes.Count > 1)
                row.Children.Add(new PPDropdown<string>(_repo.Remotes.Select(r => new DropdownOption<string>(r.Name, r.Name, icon: "cloud")),
                    Bind.Of(() => _remote.Value, async v => { _remote.Value = v; await LoadPreview(); }), size: PPButtonSize.Small, minWidth: 100).Id("vcs.push.remote"));
            else
                row.Children.Add(Text(_remote.Value, Typo.BodyStrong).Shrink(false));
            row.Children.Add(Text(":", Typo.Body, ColorToken.TextTertiary).Shrink(false));
            var remoteRef = p?.RemoteRef ?? "";
            if (remoteRef.StartsWith("refs/heads/", StringComparison.Ordinal)) remoteRef = remoteRef.Substring("refs/heads/".Length);
            row.Children.Add(Text(remoteRef, Typo.BodyStrong));
            if (p?.NewBranch == true) row.Children.Add(new PPBadge(L.T("vcs.push.newBranch"), tone: "success"));
            row.Children.Add(Spacer());
            if (_loading.Value) row.Children.Add(new PPSpinner(M.IconSmall));
            row.Padding = new Thickness(M.Xl, 0, M.Xl, 0);
            row.Height = 44;
            return row;
        }

        private UIElement Commits()
        {
            if (_error.Value is { } e)
            {
                var col = VStack(M.Md, new PPAlert("danger", e),
                    _repo.Remotes.Count == 0
                        ? new PPButton(L.T("vcs.remote.add"), () => VCSRemoteActions.AddRemote(_repo, async () => await Load()), icon: "plus", size: PPButtonSize.Small)
                        : null);
                col.Align = CrossAlign.Stretch;
                col.Padding = new Thickness(M.Xl);
                return col;
            }
            if (_preview.Value is not { } p) return new FrameworkElement();
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            if (p.Behind > 0)
                g.Children.Add(new PPAlert("warning", L.T("vcs.push.behind", p.Behind), null,
                    new PPButton(L.T("vcs.pull.ellipsis"), () => { Close(); VCSPullDialog.Present(_repo); }, size: PPButtonSize.Small)).Margins(M.Md).At(0, 0));
            if (p.Commits.Count == 0)
            {
                g.Children.Add(new PPEmptyState("circle-check", p.NewBranch ? L.T("vcs.push.newBranchNoCommits") : L.T("vcs.push.nothing")).At(2, 0));
                return g;
            }
            var head = HStack(0, Text(L.T("vcs.push.commits", p.Commits.Count) + (p.Truncated ? "+" : ""), Typo.Label, ColorToken.TextSecondary), Spacer());
            head.Padding = new Thickness(M.Xl, 0, M.Xl, 0);
            head.Height = M.RowHeightCompact;
            g.Children.Add(head.At(1, 0));
            var list = _commitList ??= new VCSVirtualList();
            _commitScroll ??= list.InScroll();
            if (!ReferenceEquals(_shownPreview, p))
            {
                _shownPreview = p;
                list.SetRows(p.Commits.Select(c => new VRow(c.Hash, M.RowHeightCompact, () =>
            {
                var r = HStack(M.Md,
                    Text(c.Short, Typo.MonoSmall, ColorToken.TextTertiary).W(64).Shrink(false),
                    Text(c.Subject),
                    Spacer(M.Md),
                    Text(c.Author, Typo.Body, ColorToken.TextSecondary),
                    Text(VCSDate.Short(c.ParsedDate), Typo.Body, ColorToken.TextTertiary).W(110).Shrink(false).Align(TextAlignment.Right));
                r.Padding = new Thickness(M.Xl, 0, M.Xl, 0);
                return r;
            })).ToList());
            }
            UI.Detach(_commitScroll);
            g.Children.Add(_commitScroll.At(2, 0));
            return g;
        }

        private UIElement Options()
        {
            var col = VStack(M.Sm,
                HStack(M.Lg, new PPCheckbox(_tags, L.T("vcs.push.tags")).Id("vcs.push.tags"),
                    _tags.Value ? new PPSegmented<string>(new[] { ("follow", L.T("vcs.push.tagsFollow")), ("all", L.T("vcs.push.tagsAll")) }, _tagsMode, PPButtonSize.Small) : null,
                    Spacer()),
                OfferUpstream ? new PPCheckbox(_setUpstream, L.T("vcs.push.setUpstream")).Id("vcs.push.setUpstream") : null,
                HStack(M.Sm, new PPCheckbox(_force, L.T("vcs.push.forceLease")).Id("vcs.push.force"), new PPInfoButton(L.T("vcs.push.forceInfo")),
                    Gap(M.Lg), new PPCheckbox(_noVerify, L.T("vcs.commit.noVerify"))));
            col.Padding = new Thickness(M.Xl, M.Md, M.Xl, M.Md);
            return col;
        }

        private void Close() => Dialogs.Dismiss(_id);

        private void Submit()
        {
            if (_preview.Value == null) return;
            var args = new VCSPushArgs { Remote = _remote.Value.Length == 0 ? null : _remote.Value, Branch = _branch };
            if (OfferUpstream && _setUpstream.Value) args.SetUpstream = true;
            if (_tags.Value)
            {
                args.Tags = true;
                args.TagsMode = _tagsMode.Value;
            }
            if (_noVerify.Value) args.NoVerify = true;
            Close();
            if (_force.Value)
            {
                args.Force = true;
                VCSRemoteActions.ConfirmForce(_repo, args);
            }
            else
            {
                VCSRemoteActions.Push(_repo, args);
            }
        }
    }
}

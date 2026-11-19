using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 提交右键菜单与历史改写：签出修订、新建分支 / 标签、拣选、还原、重置、修改信息、交互式变基、比较、恢复文件（对应 Mac VCSHistoryActions.swift）。

public static class VCSCommitMenu
{
    /// <summary>commits：选中的提交（日志顺序，新的在前）</summary>
    public static List<MenuEntry> Entries(VCSRepo repo, List<VCSCommit> commits)
    {
        if (commits.Count == 0) return new List<MenuEntry>();
        var c = commits[0];
        bool single = commits.Count == 1;
        var hashes = commits.Select(x => x.Hash).ToList();
        bool isHead = c.Hash == repo.Status?.HeadHash;
        bool merge = c.Parents.Count > 1;
        var e = new List<MenuEntry>
        {
            MenuEntry.Item(L.T("vcs.copyHash"), () => Actions.Copy(string.Join("\n", hashes)), icon: "copy"),
            MenuEntry.Item(L.T("vcs.copySubject"), () => Actions.Copy(c.Subject), icon: "copy", disabled: !single),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("vcs.checkoutRevision"), () => VCSBranchActions.Checkout(repo, c.Hash, L.T("vcs.checkoutRevision")), icon: "circle-dot", disabled: !single),
            MenuEntry.Item(L.T("vcs.branch.newFrom"), () => VCSBranchActions.NewBranch(repo, c.Hash, c.Short), icon: "git-branch", disabled: !single),
            MenuEntry.Item(L.T("vcs.tag.new"), () => VCSHistoryActions.NewTag(repo, c.Hash), icon: "tag", disabled: !single),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("vcs.cherryPick"), () => VCSHistoryActions.CherryPick(repo, hashes), icon: "cherry"),
            MenuEntry.Item(L.T("vcs.revertCommit"), () => VCSHistoryActions.Revert(repo, hashes), icon: "undo-2"),
            MenuEntry.Item(L.T("vcs.reset.ellipsis"), () => VCSHistoryActions.Reset(repo, c), icon: "rotate-ccw",
                disabled: !single || (repo.CurrentBranch == null && !(repo.Status?.Detached ?? false))),
            MenuEntry.Item(L.T("vcs.reword.ellipsis"), () => VCSHistoryActions.Reword(repo, c), icon: "pencil", disabled: !single),
            MenuEntry.Item(L.T("vcs.rebaseInteractive.ellipsis"), () => VCSRebaseEditor.Present(repo, c), icon: "list", disabled: !single || merge),
        };
        if (isHead && single) e.Add(MenuEntry.Item(L.T("vcs.undoCommit"), () => VCSCommitActions.UndoLast(repo), icon: "undo-2"));
        e.Add(MenuEntry.Divider);
        e.Add(MenuEntry.Item(L.T("vcs.compareWithCurrent"), () =>
            VCSCompareDialog.Present(repo, c.Hash, null, L.T("vcs.compare.title", c.Short, repo.HeadTitle)), icon: "git-compare", disabled: !single || isHead));
        var plugin = VCSPluginActions.Entries(ActionLocation.VcsCommitMenu, repo,
            new Dictionary<string, JToken> { ["hash"] = c.Hash, ["hashes"] = new JArray(hashes) });
        if (plugin.Count > 0)
        {
            e.Add(MenuEntry.Divider);
            e.AddRange(plugin);
        }
        return e;
    }
}

public static class VCSHistoryActions
{
    public static void CherryPick(VCSRepo repo, List<string> hashes)
    {
        if (hashes.Count == 0) return;
        repo.Perform(VCSOp.CherryPick, new VCSRefArgs { Ref = hashes[0], Refs = hashes.Count > 1 ? hashes.Skip(1).ToList() : null }, L.T("vcs.cherryPick"));
    }

    public static void Revert(VCSRepo repo, List<string> hashes)
    {
        if (hashes.Count == 0) return;
        Dialogs.Confirm(L.T("vcs.revert.title", hashes.Count), L.T("vcs.revert.message"), async () =>
            await repo.RunThrowing(VCSOp.Revert, new VCSRefArgs { Ref = hashes[0], Refs = hashes.Count > 1 ? hashes.Skip(1).ToList() : null }, L.T("vcs.revertCommit")),
            ok: L.T("vcs.revertCommit"));
    }

    public static void NewTag(VCSRepo repo, string @ref)
    {
        var fields = new List<FormField>
        {
            new() { Key = "name", Title = L.T("vcs.tag.name"), Type = "string", Required = true },
            new() { Key = "message", Title = L.T("vcs.tag.message"), Type = "text", Info = L.T("vcs.tag.messageInfo") },
            new() { Key = "push", Title = L.T("vcs.tag.push"), Type = "boolean" },
        };
        Dialogs.Form(L.T("vcs.tag.new"), fields, async v =>
        {
            var name = VCSForm.Str(v, "name");
            var msg = VCSForm.Str(v, "message");
            var push = VCSForm.Bool(v, "push");
            await repo.RunThrowing(VCSOp.TagCreate, new VCSTagCreateArgs { Name = name, Ref = @ref, Message = msg.Length == 0 ? null : msg, Push = push ? true : null },
                L.T("vcs.tag.new"), @long: push);
            await repo.LoadTags();
        }, ok: L.T("common.create"));
    }

    public static void Reset(VCSRepo repo, VCSCommit commit)
    {
        const string id = "vcs-reset";
        Dialogs.Present(id, new ResetBody(id, repo, commit));
    }

    private sealed class ResetBody : PPView
    {
        private readonly string _id;
        private readonly VCSRepo _repo;
        private readonly VCSCommit _commit;
        private readonly State<string> _mode;
        private readonly State<bool> _busy;
        private readonly State<string?> _error;

        public ResetBody(string id, VCSRepo repo, VCSCommit commit)
        {
            _id = id;
            _repo = repo;
            _commit = commit;
            _mode = UseState("mixed");
            _busy = UseState(false);
            _error = UseState<string?>(null);
        }

        protected override UIElement Build()
        {
            var col = VStack(M.Md, Text(_commit.Subject, Typo.Body, ColorToken.TextSecondary).Lines(2));
            foreach (var m in new[] { "soft", "mixed", "keep", "hard" })
                col.Children.Add(HStack(M.Sm, new PPRadio(_mode.Value == m, L.T("vcs.reset." + m), () => _mode.Value = m).Id("vcs.reset." + m),
                    new PPInfoButton(L.T("vcs.reset." + m + ".info"))));
            if (_mode.Value == "hard") col.Children.Add(new PPAlert("danger", L.T("vcs.reset.hardWarning")));
            if (_error.Value is { } e) col.Children.Add(new PPAlert("danger", e));
            col.Align = CrossAlign.Stretch;
            var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
            var ok = new PPButton(L.T("vcs.reset"), Submit, kind: _mode.Value == "hard" ? PPButtonKind.Danger : PPButtonKind.Primary, busy: _busy.Value).Id("dialog.ok");
            return new PPDialog(L.T("vcs.reset.title", _repo.HeadTitle, _commit.Short), col, new UIElement[] { cancel, ok }, 460, Close) { DefaultButton = ok, CancelButton = cancel };
        }

        private void Close() => Dialogs.Dismiss(_id);

        private async void Submit()
        {
            if (_mode.Value == "hard")
            {
                // 二次确认：硬重置会丢掉未提交的修改
                Close();
                Dialogs.Confirm(L.T("vcs.reset.hardConfirm"), L.T("vcs.reset.hardConfirmMessage", _commit.Short), async () =>
                    await _repo.RunThrowing(VCSOp.Reset, new VCSResetArgs { Ref = _commit.Hash, Mode = "hard" }, L.T("vcs.reset")),
                    ok: L.T("vcs.reset"), danger: true);
                return;
            }
            _busy.Value = true;
            try
            {
                await _repo.RunThrowing(VCSOp.Reset, new VCSResetArgs { Ref = _commit.Hash, Mode = _mode.Value }, L.T("vcs.reset"));
                Close();
            }
            catch (Exception e)
            {
                _error.Value = e.Message;
            }
            _busy.Value = false;
        }
    }

    public static void Reword(VCSRepo repo, VCSCommit commit)
    {
        const string id = "vcs-reword";
        Dialogs.Present(id, new RewordBody(id, repo, commit));
    }

    private sealed class RewordBody : PPView
    {
        private readonly string _id;
        private readonly VCSRepo _repo;
        private readonly VCSCommit _commit;
        private readonly State<string> _text = new("");
        private readonly State<bool> _loaded;
        private readonly State<bool> _busy;
        private readonly State<string?> _error;
        private PPButton? _save;

        public RewordBody(string id, VCSRepo repo, VCSCommit commit)
        {
            _id = id;
            _repo = repo;
            _commit = commit;
            _loaded = UseState(false);
            _busy = UseState(false);
            _error = UseState<string?>(null);
        }

        protected override async void OnAppear()
        {
            if (_loaded.Value) return;
            try
            {
                var d = await _repo.Api().VcsShowAsync(_repo.ProjectId, _commit.Hash, root: _repo.Root);
                _text.Value = VCSViews.FullMessage(d);
            }
            catch
            {
                _text.Value = _commit.Subject;
            }
            Area().Sync();
            _loaded.Value = true;
        }

        private PPTextArea? _area;

        private PPTextArea Area()
        {
            if (_area != null) return _area;
            _area = new PPTextArea(_text, minHeight: 160).Id("vcs.reword.text");
            _area.TextBox.TextChanged += (_, _) => UpdateSave();
            return _area;
        }

        private void UpdateSave()
        {
            if (_save != null) _save.IsEnabled = _loaded.Value && _text.Value.Trim().Length > 0 && !_busy.Value;
        }

        protected override UIElement Build()
        {
            var area = Area();
            UI.Detach(area);
            var col = VStack(M.Md, area, _error.Value is { } e ? new PPAlert("danger", e) : null);
            col.Align = CrossAlign.Stretch;
            var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
            _save = new PPButton(L.T("common.save"), Submit, kind: PPButtonKind.Primary, busy: _busy.Value).Id("dialog.ok");
            UpdateSave();
            return new PPDialog(L.T("vcs.reword.title", _commit.Short), col, new UIElement[] { cancel, _save }, 560, Close) { CancelButton = cancel };
        }

        private void Close() => Dialogs.Dismiss(_id);

        private async void Submit()
        {
            if (_busy.Value) return;
            _busy.Value = true;
            try
            {
                await _repo.RunThrowing(VCSOp.Reword, new VCSRewordArgs { Hash = _commit.Hash, Message = _text.Value }, L.T("vcs.reword"));
                Close();
            }
            catch (Exception e)
            {
                _error.Value = e.Message;
            }
            _busy.Value = false;
        }
    }

    /// <summary>把文件恢复到某个版本（工作区和暂存区）</summary>
    public static void RestoreFile(VCSRepo repo, string path, string @ref, string @short) =>
        Dialogs.Confirm(L.T("vcs.checkoutFileVersion.title", VCSPath.Name(path), @short), L.T("vcs.checkoutFileVersion.message"), async () =>
            await repo.RunThrowing(VCSOp.CheckoutFile, new VCSCheckoutFileArgs { Path = path, Ref = @ref }, L.T("vcs.checkoutFileVersion")),
            ok: L.T("vcs.checkoutFileVersion"), danger: true);
}

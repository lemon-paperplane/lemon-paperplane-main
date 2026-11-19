using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>提交区：选项、提交信息（最近信息历史）、提交 / 提交并推送。Ctrl+Enter 提交。对应 Mac VCSCommitPanel。</summary>
public sealed class VCSCommitPanel : PPView
{
    private readonly VCSRepo _repo;
    private PPButton? _commit;
    private PPButton? _commitPush;

    private VCSChangesModel Changes => _repo.Changes;
    private VCSCommitDraft Draft => _repo.Changes.Draft;

    public VCSCommitPanel(VCSRepo repo)
    {
        _repo = repo;
        Observe(repo, nameof(VCSRepo.Status));
        Observe(repo.Changes, nameof(VCSChangesModel.StagingArea), VCSChangesModel.ChecksProperty);
        Observe(repo.Changes.Draft, nameof(VCSCommitDraft.Amend), nameof(VCSCommitDraft.SignOff), nameof(VCSCommitDraft.NoVerify),
            nameof(VCSCommitDraft.Author), nameof(VCSCommitDraft.Committing));
    }

    protected override void OnAppear()
    {
        Changes.FocusMessage += FocusEditor;
        Draft.MessageReplaced += SyncEditor;
        Shortcuts.Register("Ctrl+Enter", () => { if (CanCommit(_repo.Status, Summary(_repo.Status).Count)) _repo.Commit(false); });
        SyncEditor();
    }

    protected override void OnDisappear()
    {
        Changes.FocusMessage -= FocusEditor;
        Draft.MessageReplaced -= SyncEditor;
        Shortcuts.Unregister("Ctrl+Enter");
    }

    // 输入框跨重建复用（不能在 Build 之外调用 Keep：Keep 会把元素从当前位置摘下）
    private PPTextArea? _editor;
    private PPTextArea Editor() => _editor ??= VCSMessageEditor.Make(Draft, L.T("vcs.commit.placeholder"), UpdateButtons).H(96).Id("vcs.commit.message");

    private void FocusEditor() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => Editor().TextBox.Focus()));

    private void SyncEditor() => Editor().Sync();

    protected override UIElement Build()
    {
        var st = _repo.Status;
        var summary = Summary(st);
        var amend = VCSViews.Small(new PPCheckbox(Bind.Of(() => Draft.Amend, SetAmend), L.T("vcs.commit.amend"))).Disabled(st?.Unborn ?? false);
        var signOff = VCSViews.Small(new PPCheckbox(Bind.Of(() => Draft.SignOff, v => Draft.SignOff = v), L.T("vcs.commit.signOff")));
        var options = HStack(M.Md, amend, signOff, Spacer(),
            Draft.Author.Length > 0 ? new PPBadge(Draft.Author, tone: "info", icon: "user") : null,
            Draft.NoVerify ? new PPBadge(L.T("vcs.commit.noVerify"), tone: "warning") : null,
            new PPMenuButton(HistoryEntries, VCSViews.MenuGlyph("history"), PopupAlignment.Trailing, L.T("vcs.commit.history")).Id("vcs.commit.history"),
            new PPMenuButton(OptionEntries, VCSViews.MenuGlyph("ellipsis"), PopupAlignment.Trailing, L.T("vcs.commit.options")).Id("vcs.commit.options"));
        _commit = new PPButton(Draft.Amend ? L.T("vcs.commit.amendButton") : L.T("vcs.commit"), () => _repo.Commit(false),
            kind: PPButtonKind.Primary, busy: Draft.Committing).Tip(L.T("vcs.commit") + "  Ctrl+Enter").Id("vcs.commit.commit");
        _commitPush = new PPButton(L.T("vcs.commitAndPush"), () => _repo.Commit(true)).Id("vcs.commit.commitAndPush");
        var buttons = HStack(M.Md, _commit, _commitPush, Spacer(), Text(summary.Text, Typo.Small, ColorToken.TextTertiary));
        UpdateButtons();
        var editor = Editor();
        UI.Detach(editor);
        var col = VStack(M.Sm, options, editor, buttons);
        col.Align = CrossAlign.Stretch;
        col.Padding = new Thickness(M.Md);
        return col;
    }

    private void UpdateButtons()
    {
        var st = _repo.Status;
        bool can = CanCommit(st, Summary(st).Count);
        if (_commit != null && !Draft.Committing) _commit.IsEnabled = can;
        if (_commitPush != null) _commitPush.IsEnabled = can && !Draft.Committing;
    }

    private (int Count, string Text) Summary(VCSStatus? st)
    {
        if (st == null) return (0, "");
        if (Changes.StagingArea)
        {
            var n = st.Files.Count(f => !f.Conflicted && !string.IsNullOrEmpty(f.IndexStatus));
            return (n, L.T("vcs.commit.stagedCount", n));
        }
        var plan = Changes.CommitPlan(st.Files);
        var count = plan.Paths.Count + plan.Hunks.Count;
        return (count, L.T("vcs.commit.selectedCount", count));
    }

    private bool CanCommit(VCSStatus? st, int count)
    {
        if (st == null || Draft.Committing || st.Conflicts != 0) return false;
        bool hasMessage = Draft.Message.Trim().Length > 0;
        bool merging = st.Operation == "merge";
        if (!(hasMessage || Draft.Amend || merging)) return false;
        return count > 0 || Draft.Amend || merging;
    }

    private async void SetAmend(bool on)
    {
        Draft.Amend = on;
        if (!on || Draft.Message.Trim().Length > 0) return;
        try
        {
            var d = await _repo.Api().VcsShowAsync(_repo.ProjectId, "HEAD", root: _repo.Root);
            if (Draft.Amend && Draft.Message.Length == 0) Draft.ReplaceMessage(VCSViews.FullMessage(d));
        }
        catch { }
    }

    private IEnumerable<MenuEntry> HistoryEntries()
    {
        var list = VCSPrefs.MessageHistory(_repo.Root);
        if (list.Count == 0) return new[] { MenuEntry.Header(L.T("vcs.commit.historyEmpty")) };
        var e = new List<MenuEntry> { MenuEntry.Header(L.T("vcs.commit.history")) };
        foreach (var m in list)
        {
            var first = m.Split('\n')[0];
            e.Add(MenuEntry.Item(first.Length > 60 ? first.Substring(0, 60) + "…" : first, () => Draft.ReplaceMessage(m)));
        }
        return e;
    }

    private IEnumerable<MenuEntry> OptionEntries() => new[]
    {
        MenuEntry.Item(L.T("vcs.commit.author"), () =>
            Dialogs.Input(L.T("vcs.commit.author"), L.T("vcs.commit.authorLabel"), v =>
            {
                Draft.Author = v.Contains("<") ? v : "";
                return System.Threading.Tasks.Task.CompletedTask;
            }, initial: Draft.Author, ok: L.T("common.ok")), icon: "user", @checked: Draft.Author.Length > 0),
        MenuEntry.Item(L.T("vcs.commit.clearAuthor"), () => Draft.Author = "", disabled: Draft.Author.Length == 0),
        MenuEntry.Divider,
        MenuEntry.Item(L.T("vcs.commit.noVerify"), () => Draft.NoVerify = !Draft.NoVerify, @checked: Draft.NoVerify),
        MenuEntry.Divider,
        MenuEntry.Item(L.T("vcs.commit.undoLast"), () => VCSCommitActions.UndoLast(_repo), icon: "undo-2", disabled: _repo.Status?.Unborn ?? true),
    };
}

/// <summary>提交信息输入框（多行；外部可聚焦，Ctrl+K 用）。对应 Mac VCSMessageEditor。</summary>
public static class VCSMessageEditor
{
    public static PPTextArea Make(VCSCommitDraft draft, string placeholder, Action? changed = null)
    {
        var area = new PPTextArea(Bind.Of(() => draft.Message, v => { draft.Message = v; changed?.Invoke(); }), placeholder, minHeight: 0);
        area.TextBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        return area;
    }
}

// MARK: - 提交

public sealed partial class VCSRepo
{
    /// <summary>提交（Ctrl+K 面板、Ctrl+Enter）。先确认身份，再按模式组织参数。
    /// byPaths：true 时按勾选的文件提交（总览用，不看暂存区模式）；push：提交后的推送（null 弹推送对话框）</summary>
    public void Commit(bool andPush, bool byPaths = false, Action? push = null)
    {
        if (Status is not { } st || Changes.Draft.Committing) return;
        EnsureIdentity(() => DoCommit(st, andPush, byPaths, push));
    }

    private async void DoCommit(VCSStatus st, bool andPush, bool byPaths, Action? push)
    {
        var c = Changes;
        var d = c.Draft;
        var msg = d.Message.Trim();
        var args = new VCSCommitArgs { Message = msg };
        if (d.Amend) args.Amend = true;
        if (d.SignOff) args.SignOff = true;
        if (d.NoVerify) args.NoVerify = true;
        if (d.Author.Length > 0) args.Author = d.Author;
        if (!c.StagingArea || byPaths)
        {
            var plan = c.CommitPlan(st.Files);
            if (plan.Paths.Count == 0 && plan.Hunks.Count == 0)
            {
                if (!d.Amend && st.Operation != "merge")
                {
                    AppModel.Shared.Toasts.Show("info", L.T("vcs.commit.nothingSelected"));
                    return;
                }
            }
            else
            {
                args.Paths = plan.Paths;
                if (plan.Hunks.Count > 0) args.Hunks = plan.Hunks;
            }
        }
        d.Committing = true;
        try
        {
            var r = await Api().VcsCommitAsync(ProjectId, args, Root);
            VCSPrefs.Remember(msg, Root);
            d.ReplaceMessage("");
            d.Amend = false;
            c.ClearExcludedHunks();
            d.Author = "";
            Notice = null;
            AppModel.Shared.Toasts.Show("success", L.T("vcs.commit.done", r.Short ?? VCSStyle.ShortHash(r.Hash)));
            ScheduleRefresh();
            if (andPush)
            {
                if (push != null) push(); else VCSPushDialog.Present(this);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (e is ApiException ae && ae.Reason == VCSReason.IdentityMissing)
            {
                // 预检之后身份又没了（或预检没读到配置）：设置后重试
                d.Committing = false;
                VCSIdentityDialog.Present(this, new List<string>(), () => Commit(andPush, byPaths, push));
                return;
            }
            var n = new VCSNotice { Tone = "danger", Title = L.T("vcs.commit.failed"), Message = e.Message };
            if (e is ApiException api && api.Data.Get("output").StringValue() is { } output) n.Output = output;
            Notice = n;
        }
        finally
        {
            d.Committing = false;
        }
    }
}

public static class VCSCommitActions
{
    /// <summary>撤销最近一次提交（改动留在工作区）。HEAD 已在上游里时明确提示并强制（插件默认拒绝 alreadyPushed）</summary>
    public static void UndoLast(VCSRepo repo)
    {
        bool pushed = repo.Status?.Upstream != null && (repo.Status?.Ahead ?? 0) == 0;
        Dialogs.Confirm(L.T("vcs.undoCommit.title"), pushed ? L.T("vcs.undoCommit.pushedMessage") : L.T("vcs.undoCommit.message"),
            async () => await repo.RunThrowing(VCSOp.UndoCommit, new VCSUndoCommitArgs { Force = pushed ? true : null }, L.T("vcs.undoCommit")),
            ok: L.T("vcs.undoCommit"), danger: pushed);
    }
}

/// <summary>设置 user.name / user.email（本仓库或全局）。对应 Mac VCSIdentityDialog。</summary>
public static class VCSIdentityDialog
{
    public static void Present(VCSRepo repo, List<string> missing, Action then)
    {
        const string id = "vcs-identity";
        Dialogs.Present(id, new Body(id, repo, then));
    }

    private sealed class Body : PPView
    {
        private readonly string _id;
        private readonly VCSRepo _repo;
        private readonly Action _then;
        private readonly State<string> _name = new("");
        private readonly State<string> _email = new("");
        private readonly State<bool> _global;
        private readonly State<bool> _busy;
        private readonly State<string?> _error;
        private PPButton? _ok;
        private bool _loaded;

        public Body(string id, VCSRepo repo, Action then)
        {
            _id = id;
            _repo = repo;
            _then = then;
            _global = UseState(true);
            _busy = UseState(false);
            _error = UseState<string?>(null);
        }

        protected override async void OnAppear()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var cfg = await _repo.Api().VcsConfigGetAsync(_repo.ProjectId, new[] { "user.name", "user.email" }, root: _repo.Root);
                if (_name.Value.Length == 0 && cfg.Values.TryGetValue("user.name", out var n)) { _name.Value = n; Field("name", _name).Sync(); }
                if (_email.Value.Length == 0 && cfg.Values.TryGetValue("user.email", out var e)) { _email.Value = e; Field("email", _email).Sync(); }
                UpdateOk();
            }
            catch { }
        }

        private readonly Dictionary<string, PPTextField> _fields = new();

        private PPTextField Field(string key, State<string> value)
        {
            if (_fields.TryGetValue(key, out var f)) return f;
            var tf = new PPTextField("", value, onSubmit: Submit).Id("vcs.identity." + key);
            tf.TextChanged += _ => UpdateOk();
            _fields[key] = tf;
            return tf;
        }

        private bool Valid => _name.Value.Trim().Length > 0 && _email.Value.Contains("@");

        private void UpdateOk()
        {
            if (_ok != null) _ok.IsEnabled = Valid && !_busy.Value;
        }

        private Stack Row(string label, UIElement field)
        {
            UI.Detach(field);
            return HStack(M.Md, Text(label, Typo.Body, ColorToken.TextSecondary).W(64).Shrink(false), ((FrameworkElement)field).Flex());
        }

        protected override UIElement Build()
        {
            var col = VStack(M.Md,
                HStack(M.Sm, Text(L.T("vcs.identity.missing"), Typo.Body, ColorToken.TextSecondary), new PPInfoButton(L.T("vcs.identity.info"))),
                Row(L.T("vcs.identity.name"), Field("name", _name)),
                Row(L.T("vcs.identity.email"), Field("email", _email)),
                HStack(M.Lg,
                    new PPRadio(_global.Value, L.T("vcs.identity.global"), () => _global.Value = true),
                    new PPRadio(!_global.Value, L.T("vcs.identity.local"), () => _global.Value = false)),
                _error.Value is { } err ? new PPAlert("danger", err) : null);
            col.Align = CrossAlign.Stretch;
            var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
            _ok = new PPButton(L.T("vcs.identity.setAndContinue"), Submit, kind: PPButtonKind.Primary, busy: _busy.Value).Id("dialog.ok");
            UpdateOk();
            return new PPDialog(L.T("vcs.identity.title"), col, new UIElement[] { cancel, _ok }, 440, Close) { DefaultButton = _ok, CancelButton = cancel };
        }

        private void Close() => Dialogs.Dismiss(_id);

        private async void Submit()
        {
            var n = _name.Value.Trim();
            var e = _email.Value.Trim();
            if (n.Length == 0 || !e.Contains("@") || _busy.Value) return;
            _busy.Value = true;
            try
            {
                var api = _repo.Api();
                foreach (var (k, v) in new[] { ("user.name", n), ("user.email", e) })
                {
                    var r = await api.VcsRunAsync(VCSOp.ConfigSet, _repo.ProjectId, new VCSConfigSetArgs { Key = k, Value = v, Global = _global.Value ? true : null }, _repo.Root);
                    if (!r.Ok) throw new ApiException(ApiException.CodeUserError, r.Message ?? L.T("common.failed"));
                }
                Close();
                _then();
            }
            catch (Exception ex)
            {
                _error.Value = ex.Message;
            }
            _busy.Value = false;
        }
    }
}

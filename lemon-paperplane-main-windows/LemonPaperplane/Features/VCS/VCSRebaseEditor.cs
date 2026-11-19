using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 交互式变基编辑器（IDEA 的 Interactively Rebase from Here）：列出 base..HEAD 的提交（旧的在上），
// 每行 pick / reword / squash / fixup / drop，拖动或按钮调整顺序，reword / squash 可改信息，校验后执行（对应 Mac VCSRebaseEditor.swift）。

public static class VCSRebaseEditor
{
    /// <summary>from：选中的提交（包含它，base 为它的第一个父提交；根提交时 --root）</summary>
    public static void Present(VCSRepo repo, VCSCommit from)
    {
        const string id = "vcs-rebase";
        Dialogs.Present(id, new Body(id, repo, from));
    }

    private sealed class Step
    {
        public VCSCommit Commit = null!;
        public string Action = "pick";
        public string? Message;
        /// <summary>原提交的完整信息（reword 开始编辑时取）</summary>
        public string? Original;
        public bool Editing;
        public string Id => Commit.Hash;
        public TextBlock? TitleView;
    }

    public static readonly string[] ActionNames = { "pick", "reword", "squash", "fixup", "drop" };

    private sealed class Body : PPView
    {
        private const double RowH = 34;
        private readonly string _id;
        private readonly VCSRepo _repo;
        private readonly VCSCommit _from;
        private List<Step> _steps = new();
        private readonly State<int> _version;
        private readonly State<bool> _loading;
        private readonly State<string?> _error;
        private readonly State<bool> _busy;
        private PPButton? _start;
        private PPAlert? _validationAlert;
        private Border? _validationHost;
        private bool _started;

        public Body(string id, VCSRepo repo, VCSCommit from)
        {
            _id = id;
            _repo = repo;
            _from = from;
            _version = UseState(0);
            _loading = UseState(true);
            _error = UseState<string?>(null);
            _busy = UseState(false);
        }

        private string Base => _from.Parents.FirstOrDefault() ?? "";

        private void Changed() => _version.Value++;

        protected override async void OnAppear()
        {
            if (_started) return;
            _started = true;
            await Load();
        }

        private async System.Threading.Tasks.Task Load()
        {
            _loading.Value = true;
            _error.Value = null;
            try
            {
                var @ref = Base.Length == 0 ? "HEAD" : Base + "..HEAD";
                var log = await _repo.Api().VcsLogAsync(_repo.ProjectId, new VCSLogArgs { Ref = @ref, Limit = 500, NoGraph = true }, _repo.Root);
                if (log.HasMore) _error.Value = L.T("vcs.rebase.tooMany");
                else if (log.Commits.Any(c => c.Parents.Count > 1)) _error.Value = L.T("vcs.rebase.merges");
                else if (log.Commits.All(c => c.Hash != _from.Hash)) _error.Value = L.T("vcs.rebase.notAncestor");
                _steps = log.Commits.AsEnumerable().Reverse().Select(c => new Step { Commit = c }).ToList();
                Changed();
            }
            catch (Exception e)
            {
                _error.Value = e.Message;
            }
            _loading.Value = false;
        }

        /// <summary>校验：第一个保留的提交不能是 squash/fixup；reword 的新信息不能为空</summary>
        private string? Validation
        {
            get
            {
                var kept = _steps.Where(s => s.Action != "drop").ToList();
                if (kept.FirstOrDefault() is { } f && (f.Action == "squash" || f.Action == "fixup")) return L.T("vcs.rebase.firstSquash");
                if (_steps.Any(s => s.Action == "reword" && (s.Message ?? s.Commit.Subject).Trim().Length == 0)) return L.T("vcs.rebase.emptyMessage");
                return null;
            }
        }

        private void UpdateValidation()
        {
            var msg = Validation ?? _error.Value;
            if (_validationHost != null)
            {
                _validationHost.Child = msg != null ? new PPAlert("danger", msg) : null;
                _validationHost.Visibility = msg != null ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_start != null) _start.IsEnabled = !_loading.Value && Validation == null && _steps.Count > 0 && !_busy.Value;
        }

        protected override UIElement Build()
        {
            var top = HStack(M.Sm,
                Text(L.T("vcs.rebase.onto", Base.Length == 0 ? L.T("vcs.rebase.root") : (Base.Length > 8 ? Base.Substring(0, 8) : Base)), Typo.Body, ColorToken.TextSecondary),
                new PPInfoButton(L.T("vcs.rebase.info")),
                Spacer(),
                Text(L.T("vcs.rebase.count", _steps.Count), Typo.Small, ColorToken.TextTertiary).Shrink(false));
            top.Padding = new Thickness(M.Xl, 0, M.Xl, 0);
            top.Height = M.ToolbarHeight;
            UIElement list;
            if (_loading.Value) list = new PPSpinner().Center();
            else
            {
                var col = VStack(0);
                col.Align = CrossAlign.Stretch;
                for (int i = 0; i < _steps.Count; i++) col.Children.Add(Row(i));
                col.Padding = new Thickness(0, M.Xs, 0, M.Xs);
                list = Scroll(col).Key("vcs.rebase.list");
            }
            _validationHost = new Border { Margin = new Thickness(M.Lg, 0, M.Lg, M.Md) };
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.Children.Add(top.At(0, 0));
            g.Children.Add(new PPDivider(subtle: true).At(1, 0));
            g.Children.Add(list.At(2, 0));
            g.Children.Add(_validationHost.At(3, 0));
            var reset = new PPButton(L.T("vcs.rebase.reset"), async () => await Load(), kind: PPButtonKind.Ghost).Disabled(_loading.Value || _busy.Value);
            var cancel = new PPButton(L.T("common.cancel"), () => Dialogs.Dismiss(_id)).Id("dialog.cancel");
            _start = new PPButton(L.T("vcs.rebase.start"), Submit, kind: PPButtonKind.Primary, busy: _busy.Value).Id("dialog.ok");
            UpdateValidation();
            return new VCSLargeDialog(_id, L.T("vcs.rebase.title", _from.Short), g, new UIElement[] { reset, cancel, _start }, 820, 680) { DefaultButton = _start };
        }

        private UIElement Row(int i)
        {
            var s = _steps[i];
            var grip = Box(new PPIcon("grip-vertical", M.IconSmall, ColorToken.IconSecondary).Center()).Size(18, RowH).Bg(ColorToken.Clear);
            grip.Cursor = Cursors.SizeAll;
            var dd = new PPDropdown<string>(ActionNames.Select(a => new DropdownOption<string>(a, L.T("vcs.rebase.action." + a))),
                Bind.Of(() => s.Action, v =>
                {
                    s.Action = v;
                    if (v == "reword") EnsureMessage(s);
                    if (v == "reword" || v == "squash") s.Editing = true;
                    Changed();
                }), size: PPButtonSize.Small, minWidth: 96, maxWidth: 96).Id("vcs.rebase.action." + i);
            var title = Text(FirstLine(s.Message ?? s.Commit.Subject), Typo.Body, TitleColor(s));
            if (s.Action == "drop") title.TextDecorations = TextDecorations.Strikethrough;
            s.TitleView = title;
            var head = HStack(M.Sm, grip, dd, Text(s.Commit.Short, Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false), title, Spacer(M.Sm));
            if (s.Action == "reword" || s.Action == "squash")
                head.Children.Add(new PPIconButton("pencil", L.T("vcs.rebase.editMessage"), () =>
                {
                    if (s.Action == "reword") EnsureMessage(s);
                    s.Editing = !s.Editing;
                    Changed();
                }, size: M.IconButtonSmall, iconSize: M.IconSmall, active: s.Editing));
            head.Children.Add(new PPIconButton("arrow-up", L.T("vcs.rebase.moveUp"), () => Move(i, i - 1), size: M.IconButtonSmall, iconSize: M.IconSmall).Disabled(i == 0));
            head.Children.Add(new PPIconButton("arrow-down", L.T("vcs.rebase.moveDown"), () => Move(i, i + 1), size: M.IconButtonSmall, iconSize: M.IconSmall).Disabled(i == _steps.Count - 1));
            head.Height = RowH;
            var col = VStack(0, head);
            col.Align = CrossAlign.Stretch;
            if (s.Editing && (s.Action == "reword" || s.Action == "squash"))
            {
                var area = Keep("msg:" + s.Id, () =>
                {
                    var a = new PPTextArea(Bind.Of(() => s.Message ?? "", v =>
                    {
                        s.Message = v;
                        if (s.TitleView != null)
                        {
                            s.TitleView.Text = FirstLine(v);
                            s.TitleView.Fg(TitleColor(s));
                        }
                        UpdateValidation();
                    }), s.Action == "squash" ? L.T("vcs.rebase.squashPlaceholder") : "", minHeight: 72);
                    return a;
                }, a => a.Sync());
                UI.Detach(area);
                area.Margin = new Thickness(26, 0, 0, M.Sm);
                col.Children.Add(area);
            }
            col.Padding = new Thickness(M.Lg, 0, M.Lg, 0);
            var row = Box(col).Bg(ColorToken.Clear);
            AttachDrag(grip, row, i);
            return row;
        }

        private static string TitleColor(Step s) =>
            s.Action == "drop" ? ColorToken.TextTertiary : s.Message != null && s.Message != s.Original ? ColorToken.Accent : ColorToken.TextPrimary;

        /// <summary>拖动把手：拖动时整行跟着移动，松开时按移动的行数换位置</summary>
        private void AttachDrag(FrameworkElement grip, Border row, int index)
        {
            double? startY = null;
            var move = new TranslateTransform();
            grip.MouseLeftButtonDown += (_, e) =>
            {
                startY = e.GetPosition(this).Y;
                grip.CaptureMouse();
                e.Handled = true;
            };
            grip.MouseMove += (_, e) =>
            {
                if (startY == null || !grip.IsMouseCaptured) return;
                var dy = e.GetPosition(this).Y - startY.Value;
                if (Math.Abs(dy) < 2 && row.RenderTransform != move) return;
                row.RenderTransform = move;
                move.Y = dy;
                Panel.SetZIndex(row, 1);
                row.Bg(ColorToken.BgSelected);
            };
            grip.MouseLeftButtonUp += (_, e) =>
            {
                if (startY == null) return;
                var dy = e.GetPosition(this).Y - startY.Value;
                startY = null;
                grip.ReleaseMouseCapture();
                row.RenderTransform = null;
                Panel.SetZIndex(row, 0);
                row.Bg(ColorToken.Clear);
                var to = Math.Min(Math.Max(index + (int)Math.Round(dy / RowH), 0), _steps.Count - 1);
                Move(index, to);
            };
        }

        /// <summary>reword：把原信息取来作为编辑起点</summary>
        private async void EnsureMessage(Step s)
        {
            if (s.Original != null) return;
            string full;
            try
            {
                var d = await _repo.Api().VcsShowAsync(_repo.ProjectId, s.Id, root: _repo.Root);
                full = d.Message ?? s.Commit.Subject;
            }
            catch { full = s.Commit.Subject; }
            s.Original = full;
            if (s.Message == null) s.Message = full;
            Changed();
        }

        private static string FirstLine(string s)
        {
            var i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i);
        }

        private void Move(int from, int to)
        {
            if (from < 0 || from >= _steps.Count || to < 0 || to >= _steps.Count || from == to) return;
            var s = _steps[from];
            _steps.RemoveAt(from);
            _steps.Insert(to, s);
            Changed();
        }

        private async void Submit()
        {
            if (Validation != null || _busy.Value) return;
            _busy.Value = true;
            var plan = new VCSRebasePlan
            {
                Base = Base,
                Steps = _steps.Select(s =>
                {
                    var msg = s.Action is "reword" or "squash" ? s.Message?.Trim() : null;
                    return new VCSRebaseStep { Hash = s.Commit.Hash, Action = s.Action, Message = string.IsNullOrEmpty(msg) ? null : msg };
                }).ToList(),
            };
            try
            {
                var r = await _repo.RunThrowing(VCSOp.RebaseInteractive, plan, L.T("vcs.rebaseInteractive"), @long: true);
                if (r.Ok || r.Conflicts.Count > 0 || r.Reason == VCSReason.Conflicts) Dialogs.Dismiss(_id);
            }
            catch (Exception e)
            {
                _error.Value = e.Message;
            }
            _busy.Value = false;
        }
    }
}

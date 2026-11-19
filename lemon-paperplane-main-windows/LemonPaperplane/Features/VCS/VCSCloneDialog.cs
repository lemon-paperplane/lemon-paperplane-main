using System;
using System.IO;
using System.Linq;
using System.Windows;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>从 Git 克隆：地址、目标目录（父目录 + 文件夹名）、分支、工作区 → project.clone（克隆后自动导入）。对应 Mac VCSCloneDialog。</summary>
public static class VCSCloneDialog
{
    public static void Present()
    {
        const string id = "vcs-clone";
        Dialogs.Present(id, new Body(id));
    }

    /// <summary>由地址推出文件夹名：…/name.git → name</summary>
    public static string FolderName(string url)
    {
        var s = url.Trim();
        while (s.EndsWith("/", StringComparison.Ordinal) || s.EndsWith("\\", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
        if (s.EndsWith(".git", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 4);
        var parts = s.Split(new[] { '/', ':', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.LastOrDefault() ?? "";
    }

    private sealed class Body : PPView
    {
        private readonly string _id;
        private readonly State<string> _url = new("");
        private readonly State<string> _parent;
        private readonly State<string> _folder = new("");
        private readonly State<string> _branch = new("");
        private readonly State<string> _workspaceId;
        private readonly State<bool> _busy;
        private readonly State<string?> _error;
        private bool _folderEdited;
        private DateTimeOffset? _started;
        private PPButton? _ok;
        private PPTextField? _folderField;
        private PPTextField? _parentField;
        private MiddleTrimText? _dest;

        public Body(string id)
        {
            _id = id;
            _parent = new State<string>(VCSPrefs.String("VCSCloneDir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Code"));
            _workspaceId = UseState(Actions.TargetWorkspaceId ?? "");
            _busy = UseState(false);
            _error = UseState<string?>(null);
            Observe(AppModel.Shared.Data, nameof(DataStore.Workspaces));
            Observe(AppModel.Shared.Runs, nameof(RunStore.Tasks));
        }

        private string Dest => _folder.Value.Length == 0 ? _parent.Value : Path.Combine(_parent.Value, _folder.Value);

        private void Changed()
        {
            if (_ok != null) _ok.IsEnabled = !_busy.Value && _url.Value.Trim().Length > 0 && _folder.Value.Length > 0 && _parent.Value.Length > 0;
            if (_dest != null)
            {
                _dest.FullText = Format.AbbreviatePath(Dest);
                _dest.Visibility = _folder.Value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static Stack Field(string label, UIElement c) =>
            HStack(M.Md, Text(label, Typo.Body, ColorToken.TextSecondary).W(80).Shrink(false), c is FrameworkElement fe ? fe.Flex() : c);

        /// <summary>正在跑的克隆任务（内核为克隆建的任务没有工程 id）</summary>
        private TaskInfo? CloneTask => _started is { } st
            ? AppModel.Shared.Runs.ActiveTasks.FirstOrDefault(t => t.Kind == "vcs" && t.ProjectId == null && (t.StartedAt ?? DateTimeOffset.MinValue) >= st.AddSeconds(-2))
            : null;

        protected override UIElement Build()
        {
            var url = Keep("url", () =>
            {
                var tf = new PPTextField("https://github.com/… / git@…", Bind.Of(() => _url.Value, v =>
                {
                    _url.Value = v;
                    if (!_folderEdited)
                    {
                        _folder.Value = FolderName(v);
                        _folderField?.Sync();
                    }
                    Changed();
                }), mono: true, onSubmit: Submit).Id("vcs.clone.url");
                return tf;
            });
            var parent = _parentField = Keep("parent", () =>
            {
                var tf = new PPTextField("", _parent, mono: true).Id("vcs.clone.parent");
                tf.TextChanged += _ => Changed();
                return tf;
            });
            _folderField = Keep("folder", () => new PPTextField("", Bind.Of(() => _folder.Value, v =>
            {
                _folder.Value = v;
                _folderEdited = true;
                Changed();
            }), mono: true).Id("vcs.clone.folder"));
            var branch = Keep("branch", () => new PPTextField(L.T("vcs.clone.branchPlaceholder"), _branch, mono: true).Id("vcs.clone.branch"));
            foreach (var f in new UIElement[] { url, parent, _folderField, branch }) UI.Detach(f);
            var col = VStack(M.Md,
                Field(L.T("vcs.clone.url"), url),
                Field(L.T("vcs.clone.directory"), HStack(M.Sm, parent.Flex(), new PPButton(L.T("vcs.clone.choose"), Choose).Id("vcs.clone.choose"))),
                Field(L.T("vcs.clone.folder"), _folderField),
                Field(L.T("vcs.clone.branch"), branch));
            col.Align = CrossAlign.Stretch;
            var ws = AppModel.Shared.Data.Workspaces;
            if (ws.Count > 1)
                col.Children.Add(Field(L.T("import.workspace"), new PPDropdown<string>(ws.Select(w => new DropdownOption<string>(w.Id, w.Name)), _workspaceId, fullWidth: true)));
            _dest = TextMiddle(Format.AbbreviatePath(Dest), Typo.MonoSmall, ColorToken.TextTertiary);
            _dest.Visibility = _folder.Value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            col.Children.Add(_dest);
            if (_busy.Value)
            {
                var t = CloneTask;
                col.Children.Add(HStack(M.Sm, new PPSpinner(M.IconSmall), Text(L.T("vcs.clone.running"), Typo.Body, ColorToken.TextSecondary),
                    t != null ? new PPLink(L.T("vcs.output"), () => Actions.ShowTaskLog(t.Id)) : null, Spacer()));
            }
            if (_error.Value is { } e) col.Children.Add(new PPAlert("danger", e));
            var cancel = new PPButton(L.T("common.cancel"), Close).Disabled(_busy.Value).Id("dialog.cancel");
            _ok = new PPButton(L.T("vcs.clone"), Submit, icon: "download", kind: PPButtonKind.Primary, busy: _busy.Value).Id("dialog.ok");
            Changed();
            var dlg = new PPDialog(L.T("vcs.clone.title"), col, new UIElement[] { cancel, _ok }, 560, _busy.Value ? null : Close)
            {
                DefaultButton = _ok,
                CancelButton = _busy.Value ? null : cancel,
            };
            // 克隆进行中：没有关闭按钮，Esc 也不关（对应 Mac 里 onClose 为空、取消按钮禁用）
            if (_busy.Value) dlg.OnCancel = () => { };
            return dlg;
        }

        private void Choose()
        {
            if (ImportFlow.ChooseFolder(L.T("vcs.clone.directory"), Directory.Exists(_parent.Value) ? _parent.Value : null) is { } p)
            {
                _parent.Value = p;
                _parentField?.Sync();
                Changed();
            }
        }

        private void Close() => Dialogs.Dismiss(_id);

        private async void Submit()
        {
            var u = _url.Value.Trim();
            if (u.Length == 0 || _folder.Value.Length == 0 || _busy.Value) return;
            _busy.Value = true;
            _started = DateTimeOffset.Now;
            _error.Value = null;
            VCSPrefs.SetString("VCSCloneDir", _parent.Value);
            var b = _branch.Value.Trim();
            var wsId = _workspaceId.Value;
            try
            {
                var p = await AppModel.Shared.Client().CloneProjectAsync(u, Dest, b.Length == 0 ? null : b, wsId.Length == 0 ? null : wsId);
                Close();
                AppModel.Shared.Toasts.Show("success", L.T("vcs.clone.done", p.Name));
                if (wsId.Length > 0) AppModel.Shared.Nav.Page = NavPage.WorkspacePage(wsId);
                AppModel.Shared.Nav.OpenProject(p.Id, ProjectTab.Vcs);
            }
            catch (Exception e)
            {
                _error.Value = e.Message;
            }
            _busy.Value = false;
        }
    }
}

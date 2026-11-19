using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>选什么：文件夹、文件，或两者都行（表单里的路径字段、工具链目录、插件包）。</summary>
public enum PathKind { Folder, File, Any }

/// <summary>
/// 选文件夹 / 文件的统一入口（docs/remote.md 5.4，对应 Mac PathPicker）：本机用系统对话框；连着远程服务器时弹
/// 「选择服务器上的文件夹」（fs.browse），本机路径在服务器上不存在。结果是服务器上的路径（按服务器的分隔符）。
/// </summary>
public static class ServerFiles
{
    /// <summary>选文件夹；取消时不回调。</summary>
    public static void ChooseFolder(string title, string? initial, Action<string> chosen)
    {
        if (!AppModel.Shared.IsRemote)
        {
            if (ImportFlow.ChooseFolder(title, LocalDir(initial)) is { } p) chosen(p);
            return;
        }
        RemoteFolderPicker.Present(title, PathKind.Folder, initial, null, chosen);
    }

    /// <summary>选文件；filters 用于本机的系统对话框，extensions（如 "zip"）用于服务器上的列表。</summary>
    public static void ChooseFile(string title, string? initial, Action<string> chosen,
        IEnumerable<(string Name, string Spec)>? filters = null, IEnumerable<string>? extensions = null)
    {
        if (!AppModel.Shared.IsRemote)
        {
            if (ImportFlow.ChooseFile(title, filters, LocalDir(initial)) is { } p) chosen(p);
            return;
        }
        RemoteFolderPicker.Present(title, PathKind.File, initial, extensions, chosen);
    }

    /// <summary>服务器上的文件或文件夹都行（只用于远程；本机的系统对话框只能二选一，调用方自己决定）。</summary>
    public static void ChooseRemoteAny(string title, string? initial, Action<string> chosen, IEnumerable<string>? extensions = null) =>
        RemoteFolderPicker.Present(title, PathKind.Any, initial, extensions, chosen);

    private static string? LocalDir(string? p) => p is { Length: > 0 } && System.IO.Directory.Exists(p) ? p : null;

    /// <summary>服务器路径按服务器的分隔符拼接（不用本机的 System.IO.Path）。</summary>
    public static string Join(string dir, string name) => ServerPath.Join(dir, name, AppModel.Shared.PathSeparator);

    public static string Name(string path) => ServerPath.Name(path, AppModel.Shared.PathSeparator);

    public static string? Parent(string path) => ServerPath.Parent(path, AppModel.Shared.PathSeparator);

    public static bool IsAbsolute(string path) => ServerPath.IsAbsolute(path, AppModel.Shared.PathSeparator);

    public static bool Same(string a, string b) => ServerPath.Same(a, b, AppModel.Shared.PathSeparator);

    /// <summary>full 在 root 下时的相对路径（服务器分隔符），否则 null。</summary>
    public static string? Relative(string root, string full) => ServerPath.Relative(root, full, AppModel.Shared.PathSeparator);

    /// <summary>当前服务器的主目录（本机时 %USERPROFILE%）。</summary>
    public static string Home =>
        AppModel.Shared.SystemInfo?.Home is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>
/// 选择服务器上的文件夹 / 文件：路径栏（可输入，回车跳转）、上一级、主目录、盘符（Windows 服务器）、新建文件夹、显示隐藏文件、刷新，
/// 列表单击选中、双击进入文件夹（或选择文件）。「选择」= 选中的条目，选文件夹时没选中就是当前文件夹。上次的位置按服务器记住。
/// </summary>
public sealed class RemoteFolderPicker : PPView
{
    private readonly string _id;
    private readonly string _title;
    private readonly PathKind _kind;
    private readonly List<string> _extensions;
    private readonly Action<string> _chosen;
    private readonly string? _initial;
    private readonly State<FSBrowseResult?> _cur;
    private readonly State<string?> _selected;
    private readonly State<bool> _loading;
    private readonly State<string?> _error;
    private readonly State<bool> _showHidden;
    // 路径栏不订阅：打字时不重建
    private readonly State<string> _path = new("");
    private PPTextField? _pathField;
    private bool _started;
    private int _gen;

    /// <summary>上次浏览到的位置（按服务器记；本次运行内）。</summary>
    private static readonly Dictionary<string, string> LastPath = new();

    private RemoteFolderPicker(string id, string title, PathKind kind, string? initial, IEnumerable<string>? extensions, Action<string> chosen)
    {
        _id = id;
        _title = title;
        _kind = kind;
        _initial = initial;
        _extensions = extensions?.Select(e => e.TrimStart('.').ToLowerInvariant()).ToList() ?? new List<string>();
        _chosen = chosen;
        _cur = UseState<FSBrowseResult?>(null);
        _selected = UseState<string?>(null);
        _loading = UseState(true);
        _error = UseState<string?>(null);
        _showHidden = UseState(false);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public static void Present(string title, PathKind kind, string? initial, IEnumerable<string>? extensions, Action<string> chosen)
    {
        var id = "remote-picker-" + Guid.NewGuid().ToString("N");
        Dialogs.Present(id, new RemoteFolderPicker(id, title, kind, initial, extensions, chosen));
    }

    private static string ServerKey => AppModel.Shared.Server?.Id ?? "";

    protected override void OnAppear()
    {
        if (_started) return;
        _started = true;
        var start = _initial is { Length: > 0 } i && ServerFiles.IsAbsolute(i) ? i
            : LastPath.TryGetValue(ServerKey, out var last) ? last : null;
        Load(start);
    }

    private void Load(string? path) => _ = LoadAsync(path);

    private async System.Threading.Tasks.Task LoadAsync(string? path)
    {
        var gen = ++_gen;
        _loading.Value = true;
        try
        {
            var r = await AppModel.Shared.Client().FsBrowseAsync(path);
            if (gen != _gen) return;
            _cur.Value = r;
            _selected.Value = null;
            _error.Value = null;
            _path.Value = r.Path;
            _pathField?.Sync();
            LastPath[ServerKey] = r.Path;
        }
        catch (Exception e)
        {
            if (gen != _gen) return;
            _error.Value = e.Message;
            // 记住的位置已经不在了：回到主目录
            if (_cur.Value == null && !string.IsNullOrEmpty(path))
            {
                await LoadAsync(null);
                return;
            }
            _path.Value = _cur.Value?.Path ?? "";
            _pathField?.Sync();
        }
        finally
        {
            if (gen == _gen) _loading.Value = false;
        }
    }

    private void Close() => Dialogs.Dismiss(_id);

    /// <summary>点「选择」时的结果：选中的条目，或（选文件夹时）当前文件夹。</summary>
    private string? ChosenPath
    {
        get
        {
            if (_selected.Value is { } s && _cur.Value?.Entries.FirstOrDefault(e => e.Path == s) is { } e)
                return _kind switch
                {
                    PathKind.Folder => e.Dir ? e.Path : null,
                    PathKind.File => e.Dir ? null : e.Path,
                    _ => e.Path,
                };
            return _kind == PathKind.File ? null : _cur.Value?.Path;
        }
    }

    private void Choose()
    {
        if (ChosenPath is not { } p) return;
        Close();
        _chosen(p);
    }

    /// <summary>在当前文件夹里新建文件夹（fs.mkdir），建好后选中它。</summary>
    private void NewFolder()
    {
        if (_cur.Value?.Path is not { } dir) return;
        var sep = _cur.Value.Sep.Length > 0 ? _cur.Value.Sep : AppModel.Shared.PathSeparator;
        Dialogs.Input(L.T("remotePicker.newFolder"), L.T("common.name"), async name =>
        {
            var n = name.Trim();
            if (n.Length == 0) return;
            var created = await AppModel.Shared.Client().FsMkdirAsync(ServerPath.Join(dir, n, sep));
            await LoadAsync(dir);
            _selected.Value = created;
        }, ok: L.T("common.create"));
    }

    private bool Visible(FSBrowseEntry e)
    {
        if (e.Hidden && !_showHidden.Value) return false;
        if (e.Dir) return true;
        if (_kind == PathKind.Folder) return false;
        return _extensions.Count == 0 || _extensions.Any(x => e.Name.EndsWith("." + x, StringComparison.OrdinalIgnoreCase));
    }

    protected override UIElement Build()
    {
        var cur = _cur.Value;
        // 路径栏回车跳转（不按「选择」）
        _pathField = Keep("path", () => new PPTextField(L.T("remotePicker.path"), _path, mono: true, onSubmit: () => Load(_path.Value.Trim()))
            { SubmitOverridesDialog = true }.Id("remotePicker.path"));
        Detach(_pathField);
        var up = new PPIconButton("arrow-up", L.T("remotePicker.up"), () => { if (cur?.Parent is { Length: > 0 } p) Load(p); })
            .Disabled(string.IsNullOrEmpty(cur?.Parent)).Id("remotePicker.up");
        var home = new PPIconButton("house", L.T("remotePicker.home"), () => Load(cur?.Home is { Length: > 0 } h ? h : null)).Id("remotePicker.home");
        UIElement? roots = null;
        if (cur != null && cur.Roots.Count > 1)
        {
            var currentRoot = cur.Roots.FirstOrDefault(r => cur.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) ?? cur.Roots[0];
            roots = new PPDropdown<string>(cur.Roots.Select(r => new DropdownOption<string>(r, r, icon: "hard-drive")),
                Bind.Of(() => currentRoot, v => Load(v)), minWidth: 72).Id("remotePicker.roots");
        }
        var mkdir = new PPIconButton("folder-plus", L.T("remotePicker.newFolder"), NewFolder).Disabled(cur == null).Id("remotePicker.newFolder");
        var hidden = new PPIconButton(_showHidden.Value ? "eye" : "eye-off", L.T("remotePicker.showHidden"),
            () => _showHidden.Value = !_showHidden.Value, active: _showHidden.Value).Id("remotePicker.showHidden");
        var refresh = new PPIconButton("refresh-cw", L.T("files.refresh"), () => Load(cur?.Path ?? _path.Value)).Id("remotePicker.refresh");
        var bar = HStack(M.Xs, up, home, roots, _pathField.Flex(), mkdir, hidden, refresh);

        UIElement body;
        var entries = cur?.Entries.Where(Visible).ToList() ?? new List<FSBrowseEntry>();
        if (_loading.Value && cur == null)
            body = new PPSpinner(M.IconSmall).Center();
        else if (entries.Count == 0)
            body = Text(L.T("remotePicker.empty"), Typo.Body, ColorToken.TextSecondary).Center();
        else
        {
            var list = VStack(0, entries.Select(EntryRow));
            list.Align = CrossAlign.Stretch;
            body = Scroll(list.Pad(M.Xs)).Key("remotePicker.list:" + cur?.Path);
        }
        var listBox = Box(body).H(320).Rounded(M.RadiusMD).Bg(ColorToken.BgSurface).Outline(ColorToken.Border);
        listBox.ClipToBounds = true;

        var col = VStack(M.Md,
            _title.Length > 0 ? Text(_title, Typo.Body, ColorToken.TextSecondary) : null,
            bar, listBox,
            _error.Value is { } err ? new PPAlert("danger", err) : null);
        col.Align = CrossAlign.Stretch;
        var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
        var ok = new PPButton(L.T("remotePicker.choose"), Choose, kind: PPButtonKind.Primary).Disabled(ChosenPath == null).Id("dialog.ok");
        var title = _kind == PathKind.File ? L.T("remotePicker.titleFile") : L.T("remotePicker.title");
        return new PPDialog(title, col, new UIElement[] { cancel, ok }, M.DialogWidthLarge, Close) { DefaultButton = ok, CancelButton = cancel };
    }

    private UIElement EntryRow(FSBrowseEntry e)
    {
        var sel = _selected.Value == e.Path;
        var content = HStack(M.Sm,
            new PPIcon(e.Dir ? "folder" : "file", M.Icon, ColorToken.IconSecondary),
            TextMiddle(e.Name, Typo.Body).Flex(),
            !e.Dir ? Text(Format.Bytes(e.Size), Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false) : null,
            e.Dir ? new PPIcon("chevron-right", M.IconSmall, ColorToken.IconSecondary) : null);
        var row = Box(content).Pad(M.Md, 0).H(M.RowHeightCompact).Rounded(M.RadiusSM).Bg(sel ? ColorToken.BgSelected : ColorToken.Clear);
        if (e.Hidden) row.Opacity = 0.6;
        // 单击选中；双击进入文件夹或选择文件
        row.OnClick(() => _selected.Value = e.Path);
        row.PreviewMouseLeftButtonDown += (_, ev) =>
        {
            if (ev.ClickCount != 2) return;
            ev.Handled = true;
            if (e.Dir) Load(e.Path);
            else
            {
                _selected.Value = e.Path;
                Choose();
            }
        };
        return row.A11y(e.Name).Id("remotePicker.entry." + e.Name);
    }
}

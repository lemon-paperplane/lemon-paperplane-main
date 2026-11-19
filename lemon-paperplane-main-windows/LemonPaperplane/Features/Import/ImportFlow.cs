using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 导入：选择文件夹 → 预览（识别结果、名称、工作区）→ 导入；或扫描文件夹 → 勾选 → 批量导入。对应 Mac 的 ImportFlow。
/// 选择文件夹 / 文件用系统的新式对话框（IFileOpenDialog，文件夹用 FOS_PICKFOLDERS），不用老式的树形 FolderBrowserDialog。
/// </summary>
public static class ImportFlow
{
    /// <summary>系统的选择文件夹对话框（新式 IFileOpenDialog），取消返回 null。克隆仓库等别处也用它。</summary>
    public static string? ChooseFolder(string title, string? initialDirectory = null) =>
        ShellDialogs.Pick(title, initialDirectory, pickFolders: true, filters: null);

    /// <summary>系统的打开文件对话框（新式 IFileOpenDialog），filters 为 (名称, "*.zip;*.tar") 列表，取消返回 null。</summary>
    public static string? ChooseFile(string title, IEnumerable<(string Name, string Spec)>? filters = null, string? initialDirectory = null) =>
        ShellDialogs.Pick(title, initialDirectory, pickFolders: false, filters: filters?.ToList());

    /// <summary>导入文件夹：本机用系统对话框，连着远程服务器时选服务器上的文件夹。</summary>
    public static void ImportFolder() => ServerFiles.ChooseFolder(L.T("import.folder"), null, ImportPath);

    public static void ScanFolder() => ServerFiles.ChooseFolder(L.T("import.scan"), null, ScanPath);

    /// <summary>直接打开某个文件夹的导入预览（拖放、命令行等已经有路径时）。</summary>
    public static void ImportPath(string path)
    {
        var id = "import-" + path;
        Dialogs.Present(id, new ImportPreviewDialog(id, path));
    }

    /// <summary>直接打开某个文件夹的扫描勾选。</summary>
    public static void ScanPath(string path)
    {
        var id = "scan-" + path;
        Dialogs.Present(id, new ScanDialog(id, path));
    }

    /// <summary>比较用的规范路径（按服务器的规则：Windows 不区分大小写，去掉末尾分隔符）。</summary>
    internal static string Normalize(string p) => ServerPath.Normalize(p, AppModel.Shared.PathSeparator);
}

/// <summary>导入预览：识别结果、名称、目标工作区（对应 Mac ImportPreviewDialog）。</summary>
public sealed class ImportPreviewDialog : PPView
{
    private readonly string _id;
    private readonly string _path;
    private readonly DataStore _data = AppModel.Shared.Data;
    private readonly State<ScanEntry?> _entry;
    /// <summary>所选文件夹本身不是工程、但里面有工程时的数量。</summary>
    private readonly State<int> _children;
    private readonly State<bool> _loading;
    private readonly State<bool> _busy;
    private readonly State<string?> _error;
    private readonly State<string> _workspaceId;
    // 名称不订阅：打字时不重建对话框
    private readonly State<string> _name;
    private PPTextField? _nameField;
    private bool _started;

    public ImportPreviewDialog(string id, string path)
    {
        _id = id;
        _path = path;
        _entry = UseState<ScanEntry?>(null);
        _children = UseState(0);
        _loading = UseState(true);
        _busy = UseState(false);
        _error = UseState<string?>(null);
        _workspaceId = UseState(Actions.TargetWorkspaceId ?? "");
        _name = new State<string>(ServerFiles.Name(path));
        Observe(_data, nameof(DataStore.Workspaces));
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnAppear()
    {
        if (_started) return;
        _started = true;
        Load();
    }

    private async void Load()
    {
        try
        {
            if (AppModel.Shared.Api is not { } api) return;
            // maxDepth 0 时，所选文件夹不是工程会返回它下面的工程
            var list = await api.ScanAsync(_path, 0);
            var normalized = ImportFlow.Normalize(_path);
            _entry.Value = list.FirstOrDefault(e => ImportFlow.Normalize(e.Path) == normalized);
            _children.Value = list.Count(e => ImportFlow.Normalize(e.Path) != normalized);
            if (_entry.Value is { } en && !string.IsNullOrEmpty(en.Name))
            {
                _name.Value = en.Name;
                _nameField?.Sync();
            }
        }
        catch
        {
            // 识别不了按「没有识别到」显示
        }
        finally
        {
            _loading.Value = false;
        }
    }

    private void Close() => Dialogs.Dismiss(_id);

    private void Finish(string projectId)
    {
        Close();
        var nav = AppModel.Shared.Nav;
        if (nav.CurrentWorkspaceId != _workspaceId.Value) nav.Page = NavPage.WorkspacePage(_workspaceId.Value);
        nav.OpenProject(projectId);
    }

    /// <summary>这个文件夹现在是否已经是工程（导入请求出错后核实用）。</summary>
    private async System.Threading.Tasks.Task<string?> ImportedProjectId()
    {
        try
        {
            var list = await AppModel.Shared.Client().ScanAsync(_path, 0);
            var normalized = ImportFlow.Normalize(_path);
            var e = list.FirstOrDefault(x => ImportFlow.Normalize(x.Path) == normalized);
            return e is { Imported: true } ? e.ProjectId : null;
        }
        catch
        {
            return null;
        }
    }

    private async void Submit()
    {
        if (_busy.Value || _loading.Value || _workspaceId.Value.Length == 0) return;
        _busy.Value = true;
        _error.Value = null;
        var n = _name.Value.Trim();
        try
        {
            var p = await AppModel.Shared.Client().ImportProjectAsync(_path, _workspaceId.Value, n.Length == 0 ? null : n);
            Finish(p.Id);
        }
        catch (Exception e)
        {
            // 请求出错不等于没导入：内核可能已经导入成功，只是返回结果没解析出来或连接中途断了。
            // 以内核的实际状态为准，否则对话框报错、用户点取消后工程却已经在工作区里了
            if (await ImportedProjectId() is { } pid) Finish(pid);
            else _error.Value = e.Message;
        }
        finally
        {
            _busy.Value = false;
        }
    }

    private static UIElement Labeled(string title, UIElement c) =>
        HStack(M.Md, Text(title).W(96).Shrink(false), c.Flex());

    protected override UIElement Build()
    {
        var entry = _entry.Value;
        var pathText = Selectable(Format.AbbreviatePath(_path), Typo.Mono);
        pathText.MaxHeight = Typo.Mono.LineHeight * 2 + 2;
        var col = VStack(M.Lg, HStack(M.Md, new PPIcon("folder", M.IconLarge, ColorToken.IconSecondary), pathText.Flex()));
        col.Align = CrossAlign.Stretch;
        if (_loading.Value)
            col.Children.Add(HStack(M.Sm, new PPSpinner(M.IconSmall), Text(L.T("import.detecting"), Typo.Body, ColorToken.TextSecondary)));
        else if (entry != null && entry.Facets.Count > 0)
            col.Children.Add(Flow(M.Xs, entry.Facets.Select(f => new FacetBadgeView(f.Name, f.Icon, f.Plugin))));
        else if (_children.Value > 0)
            col.Children.Add(new PPAlert("info", L.T("import.containsProjects", _children.Value), null,
                new PPButton(L.T("import.scanInstead"), () =>
                {
                    Close();
                    ImportFlow.ScanPath(_path);
                }, icon: "scan-search", size: PPButtonSize.Small).Id("import.scanInstead")));
        else if ((AppModel.Shared.SystemInfo?.Plugins ?? 1) == 0)
            // 后台服务没有加载任何插件：不是工程识别不了，是安装不完整
            col.Children.Add(new PPAlert("danger", L.T("import.noPlugins")));
        else
            col.Children.Add(new PPAlert("warning", L.T("import.nothingDetected")));
        if (entry?.Imported == true) col.Children.Add(new PPAlert("info", L.T("import.alreadyImported")));

        _nameField = Keep("name", () => new PPTextField("", _name, onSubmit: Submit).Id("import.name"));
        col.Children.Add(Labeled(L.T("common.name"), _nameField));
        col.Children.Add(Labeled(L.T("import.workspace"), new PPDropdown<string>(
            _data.Workspaces.Select(w => new DropdownOption<string>(w.Id, w.Name, icon: "layers")), _workspaceId, fullWidth: true).Id("import.workspace")));
        if (_error.Value is { } err) col.Children.Add(new PPAlert("danger", err));

        var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
        PPButton ok;
        PPButton? defaultButton = null;
        if (entry is { Imported: true, ProjectId: { } pid })
        {
            ok = new PPButton(L.T("card.open"), () =>
            {
                Close();
                AppModel.Shared.Nav.OpenProject(pid);
            }, kind: PPButtonKind.Primary).Id("dialog.ok");
        }
        else
        {
            ok = new PPButton(L.T("import.import"), Submit, kind: PPButtonKind.Primary, busy: _busy.Value).Id("dialog.ok");
            ok.IsEnabled = !_busy.Value && _workspaceId.Value.Length > 0 && !_loading.Value;
            defaultButton = ok;
        }
        return new PPDialog(L.T("import.folder"), col, new UIElement[] { cancel, ok }, M.DialogWidth, Close)
        {
            DefaultButton = defaultButton,
            CancelButton = cancel,
        }.Id("import.dialog");
    }
}

/// <summary>扫描文件夹：按深度列出识别到的工程，勾选后批量导入（对应 Mac ScanDialog）。</summary>
public sealed class ScanDialog : PPView
{
    private readonly string _id;
    private readonly string _path;
    private readonly DataStore _data = AppModel.Shared.Data;
    private readonly State<List<ScanEntry>> _entries;
    private readonly State<int> _depth;
    private readonly State<bool> _loading;
    private readonly State<bool> _busy;
    private readonly State<string?> _error;
    private readonly State<string> _workspaceId;
    private HashSet<string> _selected = new();
    private int _loadedDepth = -1;
    private int _loadSeq;

    public ScanDialog(string id, string path)
    {
        _id = id;
        _path = path;
        _entries = UseState(new List<ScanEntry>());
        _depth = UseState(2);
        _loading = UseState(true);
        _busy = UseState(false);
        _error = UseState<string?>(null);
        _workspaceId = UseState(Actions.TargetWorkspaceId ?? "");
        Observe(_data, nameof(DataStore.Workspaces));
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    private List<ScanEntry> Selectable => _entries.Value.Where(e => !e.Imported).ToList();

    protected override void OnAppear()
    {
        if (_loadedDepth != _depth.Value) Load();
    }

    private async void Load()
    {
        var depth = _depth.Value;
        _loadedDepth = depth;
        var seq = ++_loadSeq;
        _loading.Value = true;
        try
        {
            var list = await AppModel.Shared.Client().ScanAsync(_path, depth);
            if (seq != _loadSeq) return;
            _selected = new HashSet<string>(list.Where(e => !e.Imported).Select(e => e.Path));
            _entries.Value = list;
            _error.Value = null;
        }
        catch (Exception e)
        {
            if (seq != _loadSeq) return;
            _entries.Value = new List<ScanEntry>();
            _error.Value = e.Message;
        }
        finally
        {
            if (seq == _loadSeq) _loading.Value = false;
        }
    }

    private void Close() => Dialogs.Dismiss(_id);

    private void Done(int count)
    {
        Close();
        AppModel.Shared.Nav.Page = NavPage.WorkspacePage(_workspaceId.Value);
        AppModel.Shared.Toasts.Show("success", L.T("import.done", count));
    }

    private async void Submit()
    {
        if (_busy.Value || _selected.Count == 0 || _workspaceId.Value.Length == 0) return;
        _busy.Value = true;
        _error.Value = null;
        var paths = _entries.Value.Select(e => e.Path).Where(_selected.Contains).ToList();
        try
        {
            var res = await AppModel.Shared.Client().ImportProjectsAsync(paths, _workspaceId.Value);
            Done(res.Count);
        }
        catch (Exception e)
        {
            // 同单个导入：先核实哪些其实已经导入了。全部已导入按成功处理；否则刷新列表（已导入的会标出来）再报错
            var message = e.Message;
            List<ScanEntry> fresh;
            try { fresh = await AppModel.Shared.Client().ScanAsync(_path, _depth.Value); }
            catch { fresh = new List<ScanEntry>(); }
            var done = new HashSet<string>(fresh.Where(x => x.Imported).Select(x => ImportFlow.Normalize(x.Path)));
            if (paths.All(p => done.Contains(ImportFlow.Normalize(p))))
            {
                Done(paths.Count);
            }
            else
            {
                if (fresh.Count > 0)
                {
                    _selected = new HashSet<string>(fresh.Where(x => !x.Imported && _selected.Contains(x.Path)).Select(x => x.Path));
                    _entries.Value = fresh;
                }
                _error.Value = message;
            }
        }
        finally
        {
            _busy.Value = false;
        }
    }

    private string Relative(string p) =>
        p.StartsWith(_path, StringComparison.OrdinalIgnoreCase) ? "." + p.Substring(_path.Length) : Format.AbbreviatePath(p);

    protected override UIElement Build()
    {
        if (IsAppeared && _loadedDepth != _depth.Value) Dispatcher.BeginInvoke(new Action(Load));
        var entries = _entries.Value;
        var selectable = Selectable;

        var top = HStack(M.Md, TextMiddle(Format.AbbreviatePath(_path), Typo.Mono), Spacer(),
            Text(L.T("import.depth"), Typo.Body, ColorToken.TextSecondary).Shrink(false),
            new PPSegmented<int>(new[] { (1, "1"), (2, "2"), (3, "3"), (4, "4") }, _depth, PPButtonSize.Small).Id("scan.depth"));

        var all = Bind.Of(() => selectable.Count > 0 && _selected.Count == selectable.Count, on =>
        {
            _selected = on ? new HashSet<string>(selectable.Select(e => e.Path)) : new HashSet<string>();
            Invalidate();
        });
        var head = HStack(M.Md, new PPCheckbox(all).Id("scan.all"),
            Text(L.T("import.selectedCount", _selected.Count, entries.Count), Typo.Body, ColorToken.TextSecondary), Spacer());
        head.Padding = new Thickness(M.Md, 0, M.Md, 0);
        head.Height = M.RowHeight;
        head.Bg(ColorToken.BgSubtle);

        UIElement body;
        if (_loading.Value)
        {
            var c = HStack(M.Sm, new PPSpinner(M.IconSmall), Text(L.T("import.detecting"), Typo.Body, ColorToken.TextSecondary)).Center();
            body = Box(c).MinH(120);
        }
        else if (entries.Count == 0)
            body = Box(Text(L.T("import.scanEmpty"), Typo.Body, ColorToken.TextSecondary).Center()).MinH(120);
        else
        {
            var rows = VStack(0, entries.Select(Row));
            rows.Align = CrossAlign.Stretch;
            body = Scroll(rows).Key("scan.list").H(320);
        }
        var list = VStack(0, head, new PPDivider(subtle: true), body);
        list.Align = CrossAlign.Stretch;
        RunResourcePanel.RoundedClip(list, M.RadiusMD - 1);
        var box = Box(list).Rounded(M.RadiusMD).Bg(ColorToken.BgSurface).Outline(ColorToken.Border);

        var ws = HStack(M.Md, Text(L.T("import.workspace")).Shrink(false),
            new PPDropdown<string>(_data.Workspaces.Select(w => new DropdownOption<string>(w.Id, w.Name, icon: "layers")), _workspaceId, minWidth: 200)
                .Id("scan.workspace"),
            Spacer());

        var col = VStack(M.Md, top, box, ws, _error.Value is { } err ? new PPAlert("danger", err) : null);
        col.Align = CrossAlign.Stretch;

        var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
        var ok = new PPButton(L.T("import.importN", _selected.Count), Submit, kind: PPButtonKind.Primary, busy: _busy.Value).Id("dialog.ok");
        ok.IsEnabled = !_busy.Value && _selected.Count > 0 && _workspaceId.Value.Length > 0;
        return new PPDialog(L.T("import.scan"), col, new UIElement[] { cancel, ok }, M.DialogWidthLarge, Close)
        {
            DefaultButton = ok,
            CancelButton = cancel,
        }.Id("scan.dialog");
    }

    private UIElement Row(ScanEntry e)
    {
        var path = e.Path;
        var check = new PPCheckbox(Bind.Of(() => _selected.Contains(path), on =>
        {
            if (on) _selected.Add(path); else _selected.Remove(path);
            Invalidate();
        })).Disabled(e.Imported);
        var name = HStack(M.Sm, Text(e.Name, Typo.BodyStrong), e.Imported ? new PPBadge(L.T("import.imported")) : null);
        var info = VStack(2, name, TextMiddle(Relative(e.Path), Typo.Small, ColorToken.TextTertiary));
        info.Align = CrossAlign.Stretch;
        var row = HStack(M.Md, check, info.Shrink(), Spacer(M.Md),
            HStack(M.Xs, e.Facets.Take(4).Select(f => new FacetBadgeView(f.Name, f.Icon, f.Plugin))));
        row.Padding = new Thickness(M.Md, M.Sm, M.Md, M.Sm);
        row.Opacity = e.Imported ? 0.6 : 1;
        return row;
    }
}

/// <summary>系统文件对话框（IFileOpenDialog 的 COM 互操作）。</summary>
internal static class ShellDialogs
{
    public static string? Pick(string title, string? initialDirectory, bool pickFolders, List<(string Name, string Spec)>? filters)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
        try
        {
            dialog.GetOptions(out var opts);
            opts |= FOS.FORCEFILESYSTEM | FOS.PATHMUSTEXIST | FOS.NOCHANGEDIR;
            opts |= pickFolders ? FOS.PICKFOLDERS : FOS.FILEMUSTEXIST;
            dialog.SetOptions(opts);
            dialog.SetTitle(title);
            if (pickFolders) dialog.SetOkButtonLabel(title);
            if (!pickFolders && filters is { Count: > 0 })
            {
                var spec = filters.Select(f => new COMDLG_FILTERSPEC { pszName = f.Name, pszSpec = f.Spec }).ToArray();
                dialog.SetFileTypes((uint)spec.Length, spec);
            }
            if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            {
                var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(initialDirectory!, IntPtr.Zero, ref iid, out var folder) == 0) dialog.SetFolder(folder);
            }
#if DEBUG
            // 自动化测试（调试桥截图）：没有可见桌面时不弹系统对话框，直接用环境变量给的路径
            if (Environment.GetEnvironmentVariable("PAPERPLANE_TEST_PICK") is { Length: > 0 } fake) return fake;
#endif
            var owner = MainWindow.Current is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
            var hr = dialog.Show(owner);
            if (hr != 0) return null; // 取消（ERROR_CANCELLED）或失败
            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            return path;
        }
        catch (Exception e)
        {
            ToastStore.Shared.Error(e);
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

    [Flags]
    private enum FOS : uint
    {
        NOCHANGEDIR = 0x8,
        PICKFOLDERS = 0x20,
        FORCEFILESYSTEM = 0x40,
        PATHMUSTEXIST = 0x800,
        FILEMUSTEXIST = 0x1000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);
        // IFileDialog
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        // IFileOpenDialog
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}

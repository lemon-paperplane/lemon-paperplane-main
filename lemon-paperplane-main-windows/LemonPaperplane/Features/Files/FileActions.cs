using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>文件树的操作：新建、重命名、删除（移到回收站）、在资源管理器 / 终端中打开、复制路径、在编辑窗口打开。对应 Mac FileActions。</summary>
public static class FileActions
{
    private static AppModel Model => AppModel.Shared;

    /// <summary>相对工程根的路径（"/" 分隔）→ 绝对路径（Windows 分隔符）。</summary>
    public static string Absolute(string rel, string projectPath)
    {
        if (string.IsNullOrEmpty(rel)) return projectPath;
        return Path.Combine(projectPath, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>所在目录（相对路径）；根下的条目为 ""。</summary>
    public static string Parent(string rel)
    {
        int slash = rel.LastIndexOf('/');
        return slash < 0 ? "" : rel.Substring(0, slash);
    }

    public static string Join(string dir, string name) => string.IsNullOrEmpty(dir) ? name : dir + "/" + name;

    public static void Edit(string projectId, string path, int? line = null) => EditorWindows.Shared.Open(projectId, path, line);

    public static void NewItem(string projectId, string dir, bool folder, FileTreeModel? tree)
    {
        Dialogs.Input(folder ? L.T("files.newFolder") : L.T("files.newFile"), L.T("common.name"), async name =>
        {
            var rel = Join(dir, name.Trim('/', '\\', ' ').Replace('\\', '/'));
            var r = await Model.Client().FsCreateAsync(projectId, rel, folder);
            var created = string.IsNullOrEmpty(r.Path) ? rel : r.Path;
            if (tree != null)
            {
                await tree.Reload(dir, expand: true);
                tree.Selection = created;
            }
            if (!folder) Edit(projectId, created);
        }, ok: L.T("common.create"));
    }

    public static void Rename(string projectId, FSEntry entry, FileTreeModel? tree)
    {
        Dialogs.Input(L.T("files.rename"), L.T("common.name"), async name =>
        {
            var dir = Parent(entry.Path);
            var r = await Model.Client().FsRenameAsync(projectId, entry.Path, Join(dir, name));
            if (tree != null)
            {
                await tree.Reload(dir);
                tree.Selection = r.Path;
            }
        }, initial: entry.Name, ok: L.T("common.save"));
    }

    public static void Delete(string projectId, IReadOnlyList<FSEntry> entries, FileTreeModel? tree)
    {
        if (entries.Count == 0) return;
        var first = entries[0];
        var title = entries.Count == 1 ? L.T("files.delete.title", first.Name) : L.T("files.delete.titleN", entries.Count);
        Dialogs.Confirm(title, L.T("files.delete.message"), async () =>
        {
            var r = await Model.Client().FsDeleteAsync(projectId, entries.Select(e => e.Path));
            if (tree != null)
            {
                foreach (var dir in entries.Select(e => Parent(e.Path)).Distinct().ToList()) await tree.Reload(dir);
                if (tree.Selection != null && entries.Any(e => e.Path == tree.Selection)) tree.Selection = null;
            }
            Model.Toasts.Show("success", L.T("files.deleted", r.Trashed.Count));
        }, ok: L.T("files.delete"), danger: true);
    }

    /// <summary>在资源管理器中显示（文件则选中它）。</summary>
    public static void RevealInExplorer(string rel, string projectPath) => Actions.RevealInExplorer(Absolute(rel, projectPath));

    /// <summary>
    /// 在设置里选的终端中打开目录（对应 Mac 的 Terminal / iTerm…；Windows 的取值同内核 openwith_windows.go：
    /// ""（自动：Windows Terminal → PowerShell 7 → Windows PowerShell）、"wt"、"pwsh"、"powershell"、"cmd"）。
    /// </summary>
    public static void OpenTerminal(string rel, string projectPath)
    {
        var dir = Absolute(rel, projectPath);
        var chosen = (Model.Data.Settings?.Terminal ?? "").Trim().ToLowerInvariant();
        try
        {
            if (chosen is "" or "wt")
            {
                var wt = FindOnPath("wt.exe")
                         ?? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe"));
                if (wt != null)
                {
                    Process.Start(new ProcessStartInfo(wt, "-d \"" + dir.TrimEnd('\\') + "\"") { UseShellExecute = false });
                    return;
                }
                chosen = "";
            }
            string exe, args = "";
            switch (chosen)
            {
                case "cmd":
                    exe = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                    break;
                case "pwsh":
                    exe = "pwsh.exe";
                    args = "-NoLogo";
                    break;
                default:
                    exe = (chosen == "" ? FindOnPath("pwsh.exe") : null) ?? "powershell.exe";
                    args = "-NoLogo";
                    break;
            }
            // 新开一个可见的控制台窗口，起始目录是这个目录
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = dir });
        }
        catch (Exception e)
        {
            Model.Toasts.Error(e);
        }
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static string? FindOnPath(string exe)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (d.Trim().Length == 0) continue;
            try
            {
                var p = Path.Combine(d.Trim().Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>右键菜单。</summary>
    public static List<MenuEntry> Menu(string projectId, string projectPath, FSEntry entry, FileTreeModel? tree)
    {
        var dirForNew = entry.Dir ? entry.Path : Parent(entry.Path);
        var e = new List<MenuEntry>();
        if (!entry.Dir)
        {
            e.Add(MenuEntry.Item(L.T("files.edit"), () => Edit(projectId, entry.Path), icon: "file-pen"));
            if (entry.IsLaunchable)
                e.Add(MenuEntry.Item(L.T("launch.addFile"), () => Actions.LaunchAddFile(projectId, entry.Path), icon: "rocket"));
            e.Add(MenuEntry.Divider);
        }
        e.Add(MenuEntry.Item(L.T("files.newFile"), () => NewItem(projectId, dirForNew, false, tree), icon: "file-plus"));
        e.Add(MenuEntry.Item(L.T("files.newFolder"), () => NewItem(projectId, dirForNew, true, tree), icon: "folder-plus"));
        e.Add(MenuEntry.Divider);
        e.Add(MenuEntry.Item(L.T("files.rename"), () => Rename(projectId, entry, tree), icon: "pencil"));
        e.Add(MenuEntry.Item(L.T("files.delete"), () => Delete(projectId, new[] { entry }, tree), icon: "trash-2", destructive: true));
        e.Add(MenuEntry.Divider);
        e.Add(MenuEntry.Item(L.T("open.finder"), () => RevealInExplorer(entry.Path, projectPath), icon: "folder-open"));
        e.Add(MenuEntry.Item(L.T("files.openTerminal"), () => OpenTerminal(entry.Dir ? entry.Path : Parent(entry.Path), projectPath), icon: "square-terminal"));
        e.Add(MenuEntry.Item(L.T("files.copyPath"), () => Actions.Copy(Absolute(entry.Path, projectPath)), icon: "copy"));
        e.Add(MenuEntry.Item(L.T("files.copyRelativePath"), () => Actions.Copy(entry.Path), icon: "clipboard"));
        if (entry.Dir && tree != null)
        {
            e.Add(MenuEntry.Divider);
            e.Add(MenuEntry.Item(L.T("files.refresh"), () => _ = tree.Reload(entry.Path), icon: "refresh-cw"));
        }
        return e;
    }
}

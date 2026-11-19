// 工作区「启动」（对应 Mac Features/Launch/LaunchBoard.swift）：用户从各工程挑出要一起跑的运行配置（ProjectSummary.launch），按工程分组，
// 每项可单独运行 / 停止 / 看日志，也可一键全部运行、全部停止。数据存在内核（launch.* 接口，AI 也能用）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PaperplaneKit;
using static LemonPaperplane.UI;
using RunModel = PaperplaneKit.Run;

namespace LemonPaperplane;

// MARK: - 操作

public static partial class Actions
{
    /// <summary>加入或移出启动面板。</summary>
    public static void SetLaunch(string projectId, IEnumerable<string> configIds, bool on)
    {
        var ids = configIds.ToList();
        AppModel.Shared.Perform(async api =>
        {
            if (on) await api.LaunchAddAsync(projectId, ids);
            else await api.LaunchRemoveAsync(projectId, ids);
        });
    }

    /// <summary>把脚本 / 可执行文件加到启动（文件树右键「添加到启动」）。</summary>
    public static void LaunchAddFile(string projectId, string path) =>
        AppModel.Shared.Perform(async api =>
        {
            var r = await api.LaunchAddFileAsync(projectId, path);
            AppModel.Shared.Toasts.Show("success", r.Created ? L.T("launch.fileAdded", r.Name) : L.T("launch.fileAlready", r.Name));
        });

    /// <summary>运行启动面板里还没在运行的项（可限定到一个工程）。</summary>
    public static void LaunchRun(string workspaceId, string? projectId = null) =>
        AppModel.Shared.Perform(L.T("action.run.failed"), async api =>
        {
            var res = await api.LaunchRunAsync(workspaceId, projectId);
            foreach (var r in res.Started) AppModel.Shared.Runs.PutRun(r);
            ReportLaunchErrors(res.Errors);
        });

    /// <summary>停止启动面板里正在运行的项（可限定到一个工程）。</summary>
    public static void LaunchStop(string workspaceId, string? projectId = null) =>
        AppModel.Shared.Perform(L.T("action.stop.failed"), async api =>
        {
            var res = await api.LaunchStopAsync(workspaceId, projectId);
            ReportLaunchErrors(res.Errors);
        });

    private static void ReportLaunchErrors(List<LaunchRunError> errors)
    {
        if (errors.FirstOrDefault() is not { } first) return;
        var name = AppModel.Shared.Data.Project(first.ProjectId)?.Configs.FirstOrDefault(c => c.Id == first.ConfigId)?.Name ?? first.ConfigId;
        AppModel.Shared.Toasts.Show("danger", L.T("launch.someFailed", errors.Count), name + "：" + first.Error);
    }
}

// MARK: - 数据

/// <summary>工作区里的启动项：按工作区内工程顺序，每个工程按加入顺序。对应 Mac LaunchGroupData。</summary>
public sealed class LaunchGroupData
{
    public ProjectSummary Project { get; }
    public List<ConfigBrief> Items { get; }
    public string Id => Project.Id;

    public LaunchGroupData(ProjectSummary project, List<ConfigBrief> items)
    {
        Project = project;
        Items = items;
    }

    public static List<LaunchGroupData> Groups(string workspaceId, DataStore data) =>
        data.ProjectsIn(workspaceId).Select(p =>
        {
            var items = p.Launch.Select(id => p.Configs.FirstOrDefault(c => c.Id == id)).Where(c => c != null).Select(c => c!).ToList();
            return items.Count == 0 ? null : new LaunchGroupData(p, items);
        }).Where(g => g != null).Select(g => g!).ToList();

    /// <summary>工作区里正在运行的启动项数（分段控件上的数字）。</summary>
    public static int ActiveCount(string workspaceId, DataStore data, RunStore runs) =>
        Groups(workspaceId, data).Sum(g => g.Items.Count(c => runs.ActiveRun(g.Project.Id, c.Id) != null));

    /// <summary>运行状态文字（停止 / 状态 · 退出码 / 状态 · 时长）。</summary>
    public static string StatusText(RunModel r)
    {
        if (!r.IsActive && r.StoppedByUser) return L.T("status.stopped");
        var s = RunStatusStyle.Title(r.State);
        if (!r.IsActive && r.ExitCode is int code) s += " · " + L.T("run.exitCode", code);
        if (r.IsActive && r.StartedAt is { } start) s += " · " + Format.Duration(DateTimeOffset.Now - start);
        return s;
    }
}

// MARK: - 页面

/// <summary>启动页（工作区「启动」分段；对应 Mac LaunchBoardPage）。</summary>
public sealed class LaunchBoardPage : PPView
{
    private readonly string _workspaceId;
    private readonly AppModel _app = AppModel.Shared;

    public LaunchBoardPage(string workspaceId)
    {
        _workspaceId = workspaceId;
        Observe(_app.Data, nameof(DataStore.Projects), nameof(DataStore.Workspaces));
        Observe(_app.Runs, nameof(RunStore.Runs));
        Observe(_app.Nav, nameof(NavStore.Search));
    }

    /// <summary>工具栏搜索：匹配工程名、运行配置名、模块名或别名。</summary>
    private List<LaunchGroupData> Filtered(List<LaunchGroupData> all)
    {
        var q = _app.Nav.Search.Trim();
        if (q.Length == 0) return all;
        bool Has(string? s) => s != null && s.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        return all.Select(g =>
        {
            if (Has(g.Project.Name)) return g;
            var items = g.Items.Where(c => Has(c.Name) || Has(c.ModuleName) || Has(g.Project.LaunchAlias.TryGetValue(c.Id, out var a) ? a : null)).ToList();
            return items.Count == 0 ? null : new LaunchGroupData(g.Project, items);
        }).Where(g => g != null).Select(g => g!).ToList();
    }

    protected override UIElement Build()
    {
        var all = LaunchGroupData.Groups(_workspaceId, _app.Data);
        var ws = _workspaceId;
        if (all.Count == 0)
        {
            var col = VStack(M.Lg,
                new PPIcon("rocket", M.IconEmpty, ColorToken.IconSecondary).HCenter(),
                Text(L.T("launch.empty"), Typo.Body, ColorToken.TextSecondary).HCenter(),
                HStack(M.Sm,
                    new PPButton(L.T("launch.choose"), () => LaunchPickerDialog.Present(ws), icon: "list-checks", kind: PPButtonKind.Primary).Id("launch.choose"),
                    new PPButton(L.T("launch.addExecutable"), () => AddRunnableDialog.Present(ws), icon: "file-terminal").Id("launch.addExecutable"),
                    new PPButton(L.T("launch.addCommand"), () => AddCommandDialog.Present(ws), icon: "square-terminal").Id("launch.addCommand")).HCenter(),
                new PPInfoButton(L.T("launch.info")).HCenter());
            col.Align = CrossAlign.Center;
            col.HorizontalAlignment = HorizontalAlignment.Center;
            col.VerticalAlignment = VerticalAlignment.Center;
            return col;
        }
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Toolbar(all).At(0, 0));
        root.Children.Add(new PPDivider(subtle: true).At(1, 0));
        var list = Filtered(all);
        if (list.Count == 0)
        {
            root.Children.Add(new PPEmptyState("search", L.T("workspace.noMatch")).At(2, 0));
        }
        else
        {
            var col = VStack(M.Lg, list.Select(Group));
            col.Align = CrossAlign.Stretch;
            var content = Box(col).Pad(M.Xl);
            root.Children.Add(Keep("scroll", () => Scroll(Gap(0)).Key("launch.scroll"), sv => sv.Content = content).At(2, 0));
        }
        return root;
    }

    private UIElement Toolbar(List<LaunchGroupData> all)
    {
        var runs = _app.Runs;
        int total = all.Sum(g => g.Items.Count);
        int active = all.Sum(g => g.Items.Count(c => runs.ActiveRun(g.Project.Id, c.Id) != null));
        var ws = _workspaceId;
        var row = HStack(M.Sm,
            Text(L.T("launch.summary", total, active), Typo.Small, ColorToken.TextSecondary).Id("launch.summary"),
            new PPInfoButton(L.T("launch.info")),
            Spacer(),
            new PPButton(L.T("launch.addExecutable"), () => AddRunnableDialog.Present(ws), icon: "file-terminal", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("launch.addExecutable"),
            new PPButton(L.T("launch.addCommand"), () => AddCommandDialog.Present(ws), icon: "square-terminal", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("launch.addCommand"),
            new PPButton(L.T("launch.choose"), () => LaunchPickerDialog.Present(ws), icon: "list-checks", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("launch.choose"),
            new PPButton(L.T("launch.stopAll"), () => Actions.LaunchStop(ws), icon: "square", size: PPButtonSize.Small).Disabled(active == 0).Id("launch.stopAll"),
            new PPButton(L.T("launch.runAll"), () => Actions.LaunchRun(ws), icon: "play", kind: PPButtonKind.Primary, size: PPButtonSize.Small).Disabled(active == total).Id("launch.runAll"));
        return Box(row).Pad(M.Xl, 0).H(M.ToolbarHeight);
    }

    // MARK: 分组

    private UIElement Group(LaunchGroupData g)
    {
        var p = g.Project;
        var runs = _app.Runs;
        var nav = _app.Nav;
        var ws = _workspaceId;
        int active = g.Items.Count(c => runs.ActiveRun(p.Id, c.Id) != null);
        var header = HStack(M.Sm,
            new StatusDot(p.Status),
            Text(p.Name, Typo.Heading).Shrink(false),
            TextMiddle(Format.AbbreviatePath(p.Path), Typo.Small, ColorToken.TextTertiary),
            Spacer(M.Md),
            new PPIconButton("play", L.T("launch.runGroup"), () => Actions.LaunchRun(ws, p.Id), size: M.IconButtonSmall, iconSize: M.IconSmall, tint: ColorToken.Success)
                .Disabled(active == g.Items.Count).Id("launch.runGroup." + p.Id),
            new PPIconButton("square", L.T("launch.stopGroup"), () => Actions.LaunchStop(ws, p.Id), size: M.IconButtonSmall, iconSize: M.IconSmall, tint: ColorToken.Danger)
                .Disabled(active == 0).Id("launch.stopGroup." + p.Id),
            new PPIconButton("arrow-up-right", L.T("launch.openProject"), () => nav.OpenProject(p.Id, ProjectTab.Run), size: M.IconButtonSmall, iconSize: M.IconSmall)
                .Id("launch.openProject." + p.Id));
        var col = VStack(0, Box(header).Pad(M.Lg, 0).H(M.ToolbarHeight), new PPDivider(subtle: true));
        col.Align = CrossAlign.Stretch;
        var rows = VStack(0);
        rows.Align = CrossAlign.Stretch;
        bool showModule = g.Items.Select(c => c.ModuleId).Distinct().Count() > 1;
        for (int i = 0; i < g.Items.Count; i++)
        {
            if (i > 0) rows.Children.Add(new PPDivider(subtle: true).Margins(M.Lg, 0, 0, 0));
            rows.Children.Add(ItemRow(p, g.Items[i], showModule || g.Items[i].ModuleName.Length > 0));
        }
        rows.Margin = new Thickness(0, M.Xxs, 0, M.Xxs);
        col.Children.Add(rows);
        return Box(col).Rounded(M.RadiusLG).Bg(ColorToken.BgSurface).Outline(ColorToken.Border).Id("launch.group." + p.Id);
    }

    // MARK: 行

    private UIElement ItemRow(ProjectSummary project, ConfigBrief config, bool showModule)
    {
        var runs = _app.Runs;
        var nav = _app.Nav;
        var active = runs.ActiveRun(project.Id, config.Id);
        var latest = active ?? runs.LatestRun(project.Id, config.Id);
        var alias = project.LaunchAlias.TryGetValue(config.Id, out var a) && a.Length > 0 ? a : null;
        var module = showModule && config.ModuleName.Length > 0 ? config.ModuleName : null;
        // 第二行：有别名时是「原名 · 模块」，否则是模块名
        var subtitle = alias != null ? string.Join(" · ", new[] { config.Name, module }.Where(x => x != null)) : module;

        var icon = ZStack(new PPIcon(IconKey.Parse(config.Icon) ?? IconKey.Builtin("play"), M.Icon));
        if (latest != null)
        {
            var dot = new StatusDot(latest.State, 6) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            dot.Margin = new Thickness(0, 0, -2, -2);
            icon.Children.Add(dot);
        }
        var names = VStack(1, TextMiddle(alias ?? config.Name, Typo.Body), subtitle != null ? TextMiddle(subtitle, Typo.Caption, ColorToken.TextTertiary) : null).Shrink().MinW(120);
        if (alias != null) names.Tip(L.T("launch.alias.original", config.Name));

        UIElement status;
        if (latest == null) status = Text(L.T("launch.notRun"), Typo.Small, ColorToken.TextTertiary);
        else if (latest.IsActive) status = new LaunchRunStatus(latest);
        else status = Text(LaunchGroupData.StatusText(latest), Typo.Small, latest.State == RunState.Failed ? ColorToken.Danger : ColorToken.TextTertiary);
        var statusBox = Box(status).W(156).Shrink(false);

        // 地址列在窗口窄时可以收缩（其余列定宽，各行对齐）
        UIElement? url = active?.Urls.FirstOrDefault() is { } u ? RunAddress.Links(u, project.Id, config.Id, localIcon: "external-link", spacing: M.Sm).Left() : null;
        var urlBox = Box(url).W(150).Shrink();
        var resBox = Box(active != null ? new RunResourceText(active.Id, fallbackCpu: active.Cpu, fallbackRss: active.RssMB) : null).W(120).Shrink(false);

        var pid = project.Id;
        var cid = config.Id;
        var edit = new PPIconButton("pencil", L.T("launch.alias.set"), () => EditAlias(project, config, alias), size: M.IconButtonSmall, iconSize: M.IconSmall).Alpha(0).Id("launch.alias." + config.Id);
        var remove = new PPIconButton("x", L.T("launch.remove"), () => Actions.SetLaunch(pid, new[] { cid }, false), size: M.IconButtonSmall, iconSize: M.IconSmall).Alpha(0).Id("launch.remove." + cid);
        var acts = HStack(M.Xxs);
        if (active != null)
        {
            acts.Children.Add(new PPIconButton("rotate-cw", L.T("action.restart"), () => Actions.Restart(active.Id), size: M.IconButtonSmall, iconSize: M.IconSmall));
            acts.Children.Add(new PPIconButton("square", L.T("action.stop"), () => Actions.Stop(active.Id), size: M.IconButtonSmall, iconSize: M.IconSmall, tint: ColorToken.Danger)
                .Id("launch.stop." + cid));
        }
        else
        {
            acts.Children.Add(Gap(M.IconButtonSmall));
            acts.Children.Add(new PPIconButton("play", L.T("action.run"), () => Actions.Run(pid, cid), size: M.IconButtonSmall, iconSize: M.IconSmall, tint: ColorToken.Success)
                .Id("launch.run." + cid));
        }
        acts.Children.Add(new PPIconButton("terminal", L.T("launch.logs"), () => nav.OpenRunConsole(pid, cid), size: M.IconButtonSmall, iconSize: M.IconSmall).Id("launch.logs." + cid));
        acts.Children.Add(edit);
        acts.Children.Add(remove);

        var row = HStack(M.Md, icon, names, Spacer(M.Md), statusBox, urlBox, resBox, acts);
        var box = Box(row).Pad(M.Lg, 0).H(44).Bg(ColorToken.Clear).Id("launch.item." + cid);
        box.OnHover(h =>
        {
            box.Bg(h ? ColorToken.BgHover : ColorToken.Clear);
            edit.Opacity = h ? 1 : 0;
            remove.Opacity = h ? 1 : 0;
        });
        box.OnDoubleClick(() => nav.OpenRunConsole(pid, cid));
        box.PPContextMenu(() =>
        {
            var e = new List<MenuEntry>();
            if (active != null)
            {
                e.Add(MenuEntry.Item(L.T("action.restart"), () => Actions.Restart(active.Id), icon: "rotate-cw"));
                e.Add(MenuEntry.Item(L.T("action.stop"), () => Actions.Stop(active.Id), icon: "square"));
            }
            else e.Add(MenuEntry.Item(L.T("action.run"), () => Actions.Run(pid, cid), icon: "play"));
            e.Add(MenuEntry.Item(L.T("launch.logs"), () => nav.OpenRunConsole(pid, cid), icon: "terminal"));
            e.Add(MenuEntry.Item(L.T("launch.alias.set"), () => EditAlias(project, config, alias), icon: "pencil"));
            e.Add(MenuEntry.Divider);
            e.Add(MenuEntry.Item(L.T("launch.remove"), () => Actions.SetLaunch(pid, new[] { cid }, false), icon: "x"));
            return e;
        });
        return box;
    }

    private static void EditAlias(ProjectSummary project, ConfigBrief config, string? alias)
    {
        var field = new FormField { Key = "alias", Title = L.T("launch.alias"), Type = "string", Placeholder = config.Name, Info = L.T("launch.alias.info") };
        var pid = project.Id;
        var cid = config.Id;
        Dialogs.Form(L.T("launch.alias.title", config.Name), new List<FormField> { field }, async v =>
        {
            await AppModel.Shared.Client().LaunchSetAliasAsync(pid, cid, v.TryGetValue("alias", out var x) ? x.StringValue() ?? "" : "");
        }, values: new Dictionary<string, Newtonsoft.Json.Linq.JToken> { ["alias"] = alias ?? "" }, ok: L.T("common.save"));
    }
}

/// <summary>运行中的状态文字（时长每秒刷新，只在这一小块里）。</summary>
internal sealed class LaunchRunStatus : PPView
{
    private readonly RunModel _run;
    private readonly DispatcherTimer _timer;
    private TextBlock? _text;

    public LaunchRunStatus(RunModel run)
    {
        _run = run;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { if (_text != null) _text.Text = LaunchGroupData.StatusText(_run); };
    }

    protected override void OnAppear() => _timer.Start();
    protected override void OnDisappear() => _timer.Stop();

    protected override UIElement Build() => _text = Text(LaunchGroupData.StatusText(_run), Typo.Small, ColorToken.TextSecondary);
}

// MARK: - 选择启动项

/// <summary>勾选要加入启动的运行配置（按工程、模块分组）。对应 Mac LaunchPickerDialog。</summary>
public sealed class LaunchPickerDialog : PPView
{
    private readonly string _id;
    private readonly string _workspaceId;
    private readonly HashSet<string> _selected; // "工程id/配置id"
    private readonly State<int> _version;
    private readonly State<bool> _busy;
    private readonly AppModel _app = AppModel.Shared;

    private LaunchPickerDialog(string id, string workspaceId)
    {
        _id = id;
        _workspaceId = workspaceId;
        _selected = new HashSet<string>(_app.Data.ProjectsIn(workspaceId).SelectMany(p => p.Launch.Select(c => p.Id + "/" + c)));
        _version = UseState(0);
        _busy = UseState(false);
        Observe(_app.Data, nameof(DataStore.Projects));
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public static void Present(string workspaceId)
    {
        var id = "launch-picker-" + workspaceId;
        Dialogs.Present(id, new LaunchPickerDialog(id, workspaceId));
    }

    private List<ProjectSummary> Projects => _app.Data.ProjectsIn(_workspaceId).Where(p => p.Configs.Count > 0).ToList();

    private void Toggle(string key, bool on)
    {
        if (on) _selected.Add(key); else _selected.Remove(key);
        _version.Value++;
    }

    private void Close() => Dialogs.Dismiss(_id);

    private async void Save()
    {
        _busy.Value = true;
        var ps = _app.Data.ProjectsIn(_workspaceId);
        var chosen = new HashSet<string>(_selected);
        try
        {
            var api = _app.Client();
            foreach (var p in ps)
            {
                var cur = p.Launch;
                var want = p.Configs.Select(c => c.Id).Where(c => chosen.Contains(p.Id + "/" + c)).ToList();
                var add = want.Where(c => !cur.Contains(c)).ToList();
                var remove = cur.Where(c => !want.Contains(c)).ToList();
                if (add.Count > 0) await api.LaunchAddAsync(p.Id, add);
                if (remove.Count > 0) await api.LaunchRemoveAsync(p.Id, remove);
            }
            Close();
        }
        catch (Exception e)
        {
            _app.Toasts.Error(e);
        }
        _busy.Value = false;
    }

    protected override UIElement Build()
    {
        var projects = Projects;
        UIElement content;
        if (projects.Count == 0)
        {
            var t = Text(L.T("launch.choose.none"), Typo.Body, ColorToken.TextSecondary).Center();
            content = Box(t).MinH(120);
        }
        else
        {
            var col = VStack(M.Lg, projects.Select(ProjectSection));
            col.Align = CrossAlign.Stretch;
            var sv = Keep("scroll", () => Scroll(Gap(0)).Key("picker.scroll"), s => s.Content = col);
            sv.MaxHeight = 440;
            content = sv;
        }
        var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
        var ok = new PPButton(L.T("common.ok"), Save, kind: PPButtonKind.Primary, busy: _busy.Value).Id("dialog.ok");
        if (projects.Count == 0) ok.IsEnabled = false;
        return new PPDialog(L.T("launch.choose"), content, new UIElement[] { cancel, ok }, M.DialogWidthLarge, Close) { DefaultButton = ok, CancelButton = cancel };
    }

    private UIElement ProjectSection(ProjectSummary p)
    {
        var order = new List<string>();
        foreach (var c in p.Configs) if (!order.Contains(c.ModuleId)) order.Add(c.ModuleId);
        var all = p.Configs.Select(c => p.Id + "/" + c.Id).ToList();
        bool allOn = all.All(_selected.Contains);
        var col = VStack(M.Xs,
            HStack(M.Sm, Text(p.Name, Typo.Label), Spacer(),
                new PPButton(allOn ? L.T("launch.choose.clear") : L.T("launch.choose.all"), () =>
                {
                    if (allOn) foreach (var k in all) _selected.Remove(k);
                    else foreach (var k in all) _selected.Add(k);
                    _version.Value++;
                }, kind: PPButtonKind.Link, size: PPButtonSize.Small).Id("picker.all." + p.Id)));
        col.Align = CrossAlign.Stretch;
        foreach (var mid in order)
        {
            var list = p.Configs.Where(c => c.ModuleId == mid).ToList();
            if (order.Count > 1 && list.FirstOrDefault()?.ModuleName is { Length: > 0 } name)
                col.Children.Add(Text(name, Typo.Caption, ColorToken.TextTertiary).Margins(0, M.Xxs, 0, 0));
            foreach (var c in list)
            {
                var key = p.Id + "/" + c.Id;
                var cb = new PPCheckbox(Bind.Of(() => _selected.Contains(key), on => Toggle(key, on))).Fg(ColorToken.TextPrimary);
                var row = HStack(M.Sm, cb, new PPIcon(IconKey.Parse(c.Icon) ?? IconKey.Builtin("play"), M.IconSmall), Text(c.Name)).Bg(ColorToken.Clear);
                row.Margin = new Thickness(M.Sm, 0, 0, 0);
                row.OnClick(() => Toggle(key, !_selected.Contains(key))).A11y(c.Name).Id("picker.item." + c.Id);
                col.Children.Add(row);
            }
        }
        return col;
    }
}

// MARK: - 添加命令

/// <summary>手动输入一段命令（可多行）作为启动项：名称、所属工程、执行目录、命令（代码输入框）。对应 Mac AddCommandDialog。</summary>
public sealed class AddCommandDialog : AsyncDialog
{
    private readonly string _workspaceId;
    private readonly AppModel _app = AppModel.Shared;
    private readonly State<string> _projectId;
    private readonly State<string> _name = new("");
    private readonly State<string> _cwd = new("");
    private readonly State<string> _command = new("");
    private readonly State<bool> _showErrors;
    private PPTextField? _cwdField;

    private AddCommandDialog(string id, string workspaceId) : base(id)
    {
        _workspaceId = workspaceId;
        var projects = Projects;
        var open = _app.Nav.OpenProjectId;
        _projectId = UseState(projects.FirstOrDefault(p => p.Id == open)?.Id ?? projects.FirstOrDefault()?.Id ?? "");
        _showErrors = UseState(false);
    }

    public static void Present(string workspaceId)
    {
        var id = "launch-add-command-" + workspaceId;
        Dialogs.Present(id, new AddCommandDialog(id, workspaceId));
    }

    private List<ProjectSummary> Projects => _app.Data.ProjectsIn(_workspaceId);
    private ProjectSummary? Project => Projects.FirstOrDefault(p => p.Id == _projectId.Value);

    private void Close() => Dialogs.Dismiss(DialogId);

    /// <summary>选执行目录：从工程目录开始；工程内的目录显示成相对路径。</summary>
    private void ChooseDir()
    {
        if (Project is not { } p) return;
        var cwd = _cwd.Value;
        // 路径是服务器上的：按服务器的分隔符拼接、比较（连着远程时不用本机的 System.IO.Path）
        var start = cwd.Length == 0 ? p.Path : ServerFiles.IsAbsolute(cwd) ? cwd : ServerFiles.Join(p.Path, cwd);
        ServerFiles.ChooseFolder(L.T("launch.addCommand.cwd"), start, chosen =>
        {
            _cwd.Value = ServerFiles.Relative(p.Path, chosen) ?? chosen;
            _cwdField?.Sync();
        });
    }

    private void Submit()
    {
        if (Busy.Value) return;
        if (_command.Value.Trim().Length == 0 || _projectId.Value.Length == 0)
        {
            _showErrors.Value = true;
            return;
        }
        var pid = _projectId.Value;
        var n = _name.Value;
        var c = _command.Value;
        var dir = _cwd.Value;
        Run(async () =>
        {
            var r = await _app.Client().LaunchAddCommandAsync(pid, n, c, dir);
            _app.Toasts.Show("success", L.T("launch.commandAdded", r.Name));
        });
    }

    private static Stack Row(string title, UIElement control) =>
        HStack(M.Md, Text(title).W(M.FormLabelWidth).Shrink(false), control.Flex());

    protected override UIElement Build()
    {
        var projects = Projects;
        var col = VStack(M.Md);
        col.Align = CrossAlign.Stretch;
        if (projects.Count == 0)
        {
            col.Children.Add(Paragraph(L.T("launch.addCommand.noProject"), Typo.Body, ColorToken.TextSecondary));
        }
        else
        {
            col.Children.Add(Row(L.T("common.name"), Keep("name", () => new PPTextField(L.T("launch.addCommand.namePlaceholder"), _name).Id("addCommand.name"))));
            col.Children.Add(Row(L.T("launch.addCommand.project"), new PPDropdown<string>(
                projects.Select(p => new DropdownOption<string>(p.Id, p.Name, detail: Format.AbbreviatePath(p.Path))), _projectId, fullWidth: true).Id("addCommand.project")));
            _cwdField = Keep("cwd", () => new PPTextField(L.T("launch.addCommand.cwdPlaceholder"), _cwd, mono: true).Id("addCommand.cwd"));
            col.Children.Add(Row(L.T("launch.addCommand.cwd"), HStack(M.Sm, _cwdField.Flex(), new PPButton(L.T("form.browse"), ChooseDir).Id("addCommand.browse"))));
            var label = HStack(M.Xs, Text(L.T("launch.addCommand.command")).Shrink(false), Text("*", Typo.Body, ColorToken.Danger).Shrink(false),
                new PPInfoButton(L.T("launch.addCommand.commandInfo")));
            var labelBox = Box(label).W(M.FormLabelWidth).Top();
            labelBox.Padding = new Thickness(0, 6, 0, 0);
            var code = Keep("command", () => new PPCodeInput(_command, L.T("launch.addCommand.commandPlaceholder"), "shell", 220, Submit, Close).Id("addCommand.command"),
                c => c.Invalid = _showErrors.Value && _command.Value.Trim().Length == 0);
            var codeRow = HStack(M.Md, labelBox, code.Flex());
            codeRow.Align = CrossAlign.Start;
            col.Children.Add(codeRow);
            if (_showErrors.Value && _command.Value.Trim().Length == 0)
                label.Children.OfType<TextBlock>().First().Fg(ColorToken.Danger);
        }
        if (ErrorAlert is { } alert) col.Children.Add(alert);
        var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
        var ok = new PPButton(L.T("launch.addCommand.add"), Submit, kind: PPButtonKind.Primary, busy: Busy.Value).Id("dialog.ok");
        if (projects.Count == 0) ok.IsEnabled = false;
        return new PPDialog(L.T("launch.addCommand"), col, new UIElement[] { cancel, ok }, M.DialogWidthLarge, Close) { DefaultButton = ok, CancelButton = cancel };
    }
}

// MARK: - 添加脚本或程序

/// <summary>「添加脚本或程序」的状态：筛选词、文件列表、勾选。</summary>
internal sealed class RunnablePickState : PPObservable
{
    private string _filter = "";
    private RunnableList? _list;
    private bool _loading;
    private int _selectedCount;

    public string Filter { get => _filter; set => Set(ref _filter, value); }
    public RunnableList? List { get => _list; set { _list = value; Notify(); } }
    public bool Loading { get => _loading; set => Set(ref _loading, value); }
    public HashSet<string> Selected { get; } = new();
    public int SelectedCount { get => _selectedCount; private set => Set(ref _selectedCount, value); }

    public void Toggle(string path, bool on)
    {
        if (on) Selected.Add(path); else Selected.Remove(path);
        SelectedCount = Selected.Count;
    }

    public void ClearSelection()
    {
        Selected.Clear();
        SelectedCount = 0;
    }

    public void KeepOnly(HashSet<string> paths)
    {
        Selected.RemoveWhere(p => !paths.Contains(p));
        SelectedCount = Selected.Count;
    }
}

/// <summary>
/// 自己的文件选择框：只列内核判定能运行的文件（脚本、程序；可选加上其他源码），按目录分组，可筛选、多选。对应 Mac AddRunnableDialog。
/// 筛选框、文件列表跨重建复用；列表虚拟化（最多 2000 个文件）。
/// </summary>
public sealed class AddRunnableDialog : AsyncDialog
{
    private readonly string _workspaceId;
    private readonly AppModel _app = AppModel.Shared;
    private readonly State<string> _projectId;
    private readonly State<bool> _includeSources;
    private readonly RunnablePickState _state = new();
    private int _loadGen;

    private AddRunnableDialog(string id, string workspaceId) : base(id)
    {
        _workspaceId = workspaceId;
        var projects = Projects;
        var open = _app.Nav.OpenProjectId;
        _projectId = UseState(projects.FirstOrDefault(p => p.Id == open)?.Id ?? projects.FirstOrDefault()?.Id ?? "");
        _includeSources = UseState(false);
        Observe(_state, nameof(RunnablePickState.SelectedCount), nameof(RunnablePickState.List));
    }

    public static void Present(string workspaceId)
    {
        var id = "launch-add-runnable-" + workspaceId;
        Dialogs.Present(id, new AddRunnableDialog(id, workspaceId));
    }

    private List<ProjectSummary> Projects => _app.Data.ProjectsIn(_workspaceId);

    protected override void OnAppear() => _ = Load();

    private async Task Load()
    {
        var pid = _projectId.Value;
        if (pid.Length == 0 || _app.Api is not { } api) return;
        int gen = ++_loadGen;
        _state.Loading = true;
        try
        {
            var r = await api.LaunchCandidatesAsync(pid, _includeSources.Value);
            if (gen != _loadGen) return;
            _state.List = r;
            _state.KeepOnly(new HashSet<string>(r.Files.Select(f => f.Path)));
            Error.Value = null;
        }
        catch (Exception e)
        {
            if (gen == _loadGen) Error.Value = e.Message;
        }
        finally
        {
            if (gen == _loadGen) _state.Loading = false;
        }
    }

    private void Close() => Dialogs.Dismiss(DialogId);

    private void Submit()
    {
        if (_state.Selected.Count == 0 || Busy.Value) return;
        var pid = _projectId.Value;
        var paths = (_state.List?.Files ?? new List<RunnableFile>()).Select(f => f.Path).Where(_state.Selected.Contains).ToList();
        Run(async () =>
        {
            var api = _app.Client();
            foreach (var p in paths) await api.LaunchAddFileAsync(pid, p);
            _app.Toasts.Show("success", L.T("launch.pick.added", paths.Count));
        });
    }

    protected override UIElement Build()
    {
        var projects = Projects;
        var col = VStack(M.Md);
        col.Align = CrossAlign.Stretch;
        if (projects.Count == 0)
        {
            col.Children.Add(Paragraph(L.T("launch.addCommand.noProject"), Typo.Body, ColorToken.TextSecondary));
        }
        else
        {
            var filter = Keep("filter", () => new PPTextField(L.T("launch.pick.filter"), Bind.Of(() => _state.Filter, v => _state.Filter = v), icon: "search").Id("pick.filter"));
            var top = HStack(M.Sm,
                projects.Count > 1
                    ? new PPDropdown<string>(projects.Select(p => new DropdownOption<string>(p.Id, p.Name)), Bind.Of(() => _projectId.Value, v =>
                    {
                        if (v == _projectId.Value) return;
                        _projectId.Value = v;
                        _state.ClearSelection();
                        _state.List = null;
                        _ = Load();
                    }), minWidth: 180).Id("pick.project")
                    : null,
                filter.Flex());
            col.Children.Add(top);
            var list = _state.List;
            col.Children.Add(HStack(M.Sm,
                new PPSwitch(Bind.Of(() => _includeSources.Value, v => { _includeSources.Value = v; _ = Load(); })).Id("pick.sources"),
                Text(L.T("launch.pick.sources"), Typo.Small, ColorToken.TextSecondary),
                new PPInfoButton(L.T("launch.pick.sources.info")),
                Spacer(),
                list?.Truncated == true ? Text(L.T("launch.pick.truncated", list.Files.Count), Typo.Small, ColorToken.TextTertiary) : null));
            // 固定高度：加载、筛选时对话框不跳
            var files = Keep("files", () => new RunnableFileList(_state, _app.Data));
            col.Children.Add(Box(files).H(360).Rounded(M.RadiusMD).Bg(ColorToken.BgSubtle).Outline(ColorToken.Border));
        }
        if (ErrorAlert is { } alert) col.Children.Add(alert);
        var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
        var ok = new PPButton(L.T("launch.pick.add", _state.SelectedCount), Submit, kind: PPButtonKind.Primary, busy: Busy.Value).Id("dialog.ok");
        if (_state.SelectedCount == 0) ok.IsEnabled = false;
        return new PPDialog(L.T("launch.pick.title"), col, new UIElement[] { cancel, ok }, M.DialogWidthLarge, Close) { DefaultButton = ok, CancelButton = cancel };
    }
}

/// <summary>可运行文件列表：按目录分组（根目录在前），筛选，虚拟化。</summary>
internal sealed class RunnableFileList : PPView
{
    private readonly RunnablePickState _state;
    private readonly DataStore _data;
    private LaunchVirtualList? _list;

    private sealed record HeaderItem(string Dir);

    public RunnableFileList(RunnablePickState state, DataStore data)
    {
        _state = state;
        _data = data;
        Observe(state, nameof(RunnablePickState.Filter), nameof(RunnablePickState.List), nameof(RunnablePickState.Loading));
    }

    private List<object> Items()
    {
        var q = _state.Filter.Trim();
        var files = (_state.List?.Files ?? new List<RunnableFile>())
            .Where(f => q.Length == 0 || f.Path.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        var order = new List<string>();
        var map = new Dictionary<string, List<RunnableFile>>();
        foreach (var f in files)
        {
            if (!map.TryGetValue(f.Directory, out var l)) { order.Add(f.Directory); map[f.Directory] = l = new List<RunnableFile>(); }
            l.Add(f);
        }
        var items = new List<object>();
        foreach (var d in order)
        {
            items.Add(new HeaderItem(d));
            items.AddRange(map[d]);
        }
        return items;
    }

    protected override UIElement Build()
    {
        if (_state.Loading && _state.List == null) return new PPSpinner(M.Icon).Center();
        var items = Items();
        if (items.Count == 0)
            return Text(_state.Filter.Length == 0 ? L.T("launch.pick.empty") : L.T("workspace.noMatch"), Typo.Body, ColorToken.TextSecondary).Center();
        _list ??= new LaunchVirtualList(Make) { Padding = new Thickness(0, M.Xs, 0, M.Xs) };
        _list.ItemsSource = items;
        return _list;
    }

    private UIElement Make(object item)
    {
        if (item is HeaderItem h)
        {
            var t = TextMiddle(h.Dir.Length == 0 ? L.T("launch.pick.root") : h.Dir, Typo.Caption, ColorToken.TextTertiary);
            var row = HStack(M.Xs, new PPIcon("folder", M.IconSmall, ColorToken.IconSecondary), t);
            return Box(row).Pad(M.Md, M.Sm, M.Md, M.Xxs);
        }
        var f = (RunnableFile)item;
        bool on = f.Launch || _state.Selected.Contains(f.Path);
        var cb = new PPCheckbox(Bind.Of(() => f.Launch || _state.Selected.Contains(f.Path), v => { if (!f.Launch) _state.Toggle(f.Path, v); }));
        if (f.Launch) cb.IsEnabled = false;
        var r = HStack(M.Sm,
            cb,
            FileIconFor(f),
            TextMiddle(f.Name),
            new PPBadge(L.T("launch.pick.kind." + f.Kind)),
            Spacer(M.Sm),
            f.Launch ? Text(L.T("launch.pick.inLaunch"), Typo.Small, ColorToken.TextTertiary) : null);
        var box = Box(r).Pad(M.Md + M.Sm, 0, M.Md, 0).H(M.RowHeightCompact).Bg(ColorToken.Clear).A11y(f.Name).Id("pick.file." + f.Path);
        if (f.Launch) box.Opacity = 0.6;
        else box.OnClick(() =>
        {
            _state.Toggle(f.Path, !_state.Selected.Contains(f.Path));
            cb.Sync();
        });
        _ = on;
        return box;
    }

    /// <summary>文件图标：有语言贡献且该语言对应某个技术徽标时用它的图标，否则按扩展名用通用 SVG（同文件树）。</summary>
    private UIElement FileIconFor(RunnableFile f)
    {
        var lang = LanguageResolver.Language(f.Name, _data.Contributions.Languages);
        if (lang != null && _data.Contributions.Facet(lang.Id) is { Icon: { } icon } facet)
            return PPIcon.Raw(icon, facet.Plugin, FileIcon.BuiltinName(f.Name), M.Icon);
        return new PPIcon(FileIcon.BuiltinName(f.Name), M.Icon, ColorToken.IconPrimary);
    }
}

/// <summary>虚拟化列表（只为看得见的行建界面）：ItemsControl + VirtualizingStackPanel，按像素滚动，滚动条同全局样式。</summary>
internal sealed class LaunchVirtualList : ItemsControl
{
    private readonly Func<object, UIElement> _make;

    public LaunchVirtualList(Func<object, UIElement> make)
    {
        _make = make;
        Focusable = false;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(this, true);
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer));
        sv.SetValue(ScrollViewer.CanContentScrollProperty, true);
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        sv.SetValue(FocusableProperty, false);
        sv.SetValue(ScrollViewer.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        sv.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = sv };
    }

    protected override DependencyObject GetContainerForItemOverride() => new ContentControl { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };

    protected override bool IsItemItsOwnContainerOverride(object item) => false;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ContentControl cc) cc.Content = _make(item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ContentControl cc) cc.Content = null;
    }
}

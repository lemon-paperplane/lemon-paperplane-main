using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>插件：已安装（列表 + 详情 + 设置）与商店（对应 Mac PluginsPage）。</summary>
public sealed class PluginsPage : PPView
{
    private const string TabInstalled = "installed";
    private const string TabStore = "store";

    private readonly DataStore _data = AppModel.Shared.Data;
    private readonly State<string> _tab;
    private readonly State<string?> _selected;
    private readonly State<string> _filter;
    private double _listWidth = 300;

    public PluginsPage()
    {
        _tab = UseState(TabInstalled);
        _selected = UseState<string?>(null);
        _filter = UseState("");
        Observe(_data, nameof(DataStore.Plugins), nameof(DataStore.Contributions));
    }

    protected override UIElement Build()
    {
        var tabs = new PPTabBar<string>(new[]
        {
            new PPTab<string>(TabInstalled, L.T("plugin.installed"), "puzzle", _data.Plugins.Count),
            new PPTab<string>(TabStore, L.T("plugin.store"), "store"),
        }, _tab);
        var top = HStack(M.Lg, tabs, Spacer(),
            new PPButton(L.T("plugin.installFromFile"), InstallFromFile, icon: "upload", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("plugins.installFile"));
        top.Padding = new Thickness(M.Lg, 0, M.Lg, 0);
        var body = _tab.Value == TabStore ? Keep("store", () => new StoreView()) : Installed();
        var col = VStack(0, top, new PPDivider(subtle: true), body.Flex());
        col.Align = CrossAlign.Stretch;
        return col;
    }

    private UIElement Installed()
    {
        var f = _filter.Value;
        var list = _data.Plugins.Where(p => f.Length == 0 || p.Name.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0
            || p.Id.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        var current = _data.Plugins.FirstOrDefault(p => p.Id == _selected.Value) ?? list.FirstOrDefault();

        // 筛选框跨重建复用（输入中不被换掉）
        var field = Keep("filter." + L10n.Shared.Locale, () => new PPTextField(L.T("common.filter"), _filter, icon: "search", clearable: true, height: M.ControlHeightSmall + 2).Id("plugins.filter"));
        field.Margin = new Thickness(M.Sm);
        var rows = VStack(1, list.Select(p => Row(p, p.Id == current?.Id)));
        rows.Align = CrossAlign.Stretch;
        rows.Padding = new Thickness(M.Sm, 0, M.Sm, 0);
        var left = VStack(0, field, Scroll(rows).Key("plugins.list").Flex());
        left.Align = CrossAlign.Stretch;
        left.Bg(ColorToken.BgContent);

        UIElement right = current != null
            ? Keep("detail:" + current.Id, () => new PluginDetail(current), d => d.Update(current))
            : new PPEmptyState("puzzle", L.T("plugin.none"), L.T("plugin.installFromFile"), InstallFromFile, "upload");
        return new PPSplitView(left, right, Bind.Of(() => _listWidth, v => _listWidth = v), 240, 440);
    }

    private PPRow Row(PluginInfo p, bool selected)
    {
        var title = HStack(M.Xs, Text(p.Name, Typo.BodyStrong),
            p.Running ? Box().Size(6, 6).Rounded(3).Bg(ColorToken.StatusRunning).Tip(L.T("plugin.running")) : null);
        var info = VStack(2, title, Text(p.Version + (p.Vendor != null ? "  ·  " + p.Vendor : ""), Typo.Small, ColorToken.TextTertiary));
        var content = HStack(M.Md,
            PPIcon.Raw(p.Icon, p.Id, "puzzle", M.IconLarge).Alpha(p.Enabled ? 1 : 0.45),
            info.Shrink(),
            Spacer(M.Xs),
            p.Error != null ? new PPIcon("circle-alert", M.IconSmall, ColorToken.Danger) : null,
            !p.Enabled ? new PPBadge(L.T("plugin.disabled")) : null);
        var id = p.Id;
        return new PPRow(content, selected, () => _selected.Value = id, 48).Id("plugin.row." + p.Id);
    }

    /// <summary>从本地 zip 安装（系统的打开文件对话框）；连着远程服务器时插件装在服务器上：选服务器上的插件包或文件夹。</summary>
    private void InstallFromFile()
    {
        if (AppModel.Shared.IsRemote)
        {
            ServerFiles.ChooseRemoteAny(L.T("plugin.installFromFile"), null, Install, new[] { "zip" });
            return;
        }
        if (ImportFlow.ChooseFile(L.T("plugin.installFromFile"), new[] { ("ZIP", "*.zip") }) is not { } path) return;
        Install(path);
    }

    private void Install(string path)
    {
        AppModel.Shared.Perform(L.T("plugin.installFailed"), async api =>
        {
            var info = await api.InstallPluginAsync(path);
            await AppModel.Shared.ReloadPlugins();
            _selected.Value = info.Id;
            _tab.Value = TabInstalled;
            AppModel.Shared.Toasts.Show("success", L.T("plugin.installedToast", info.Name, info.Version));
        });
    }
}

/// <summary>插件详情：名称、版本、来源、启用开关、卸载 / 回退、主页、错误、贡献、依赖、设置、设置视图。</summary>
internal sealed class PluginDetail : PPView
{
    private readonly DataStore _data = AppModel.Shared.Data;
    private PluginInfo _p;

    public PluginDetail(PluginInfo plugin)
    {
        _p = plugin;
        Observe(_data, nameof(DataStore.Contributions), nameof(DataStore.Plugins));
    }

    public void Update(PluginInfo p)
    {
        if (ReferenceEquals(_p, p)) return;
        _p = p;
        Invalidate();
    }

    protected override UIElement Build()
    {
        var p = _p;
        var col = VStack(M.Xl, Header(p));
        col.Align = CrossAlign.Stretch;
        if (!string.IsNullOrEmpty(p.Error)) col.Children.Add(new PPAlert("danger", L.T("plugin.error"), p.Error));
        if (p.Contributes is { } c)
        {
            var rows = VStack(M.Sm,
                ContribRow(L.T("plugin.c.facets"), c.Facets),
                ContribRow(L.T("plugin.c.toolchains"), c.Toolchains),
                ContribRow(L.T("plugin.c.runConfigTypes"), c.RunConfigTypes),
                ContribRow(L.T("plugin.c.views"), c.Views),
                ContribRow(L.T("plugin.c.actions"), c.Actions),
                ContribRow(L.T("plugin.c.tools"), c.Tools),
                !string.IsNullOrEmpty(c.Vcs) ? ContribRow(L.T("plugin.c.vcs"), new List<string> { c.Vcs! }) : null,
                c.Diagnostics > 0
                    ? HStack(M.Md, Text(L.T("plugin.c.diagnostics"), Typo.Body, ColorToken.TextSecondary).W(140).Shrink(false), Text(c.Diagnostics.ToString(), Typo.Mono))
                    : null);
            rows.Align = CrossAlign.Stretch;
            col.Children.Add(new PPSection(L.T("plugin.contributes"), new PPCard(rows)));
        }
        if (p.Dependencies.Count > 0)
        {
            col.Children.Add(new PPSection(L.T("plugin.dependencies"), Flow(M.Xs, p.Dependencies.Select(d =>
                new PPBadge(d.Id + (d.Version != null ? " " + d.Version : "") + (d.Optional ? " · " + L.T("plugin.optional") : ""),
                    tone: _data.Plugin(d.Id) == null ? (d.Optional ? null : "danger") : null)))));
        }
        foreach (var s in _data.Contributions.Settings.Where(s => s.Plugin == p.Id && s.Fields.Count > 0))
        {
            var info = s;
            col.Children.Add(new PPSection(L.T("plugin.settings"), Keep("settings:" + s.Plugin, () => new PluginSettingsCard(info))));
        }
        foreach (var v in _data.Contributions.ViewsAt(ViewLocation.Settings).Where(v => v.Plugin == p.Id))
        {
            var view = v;
            col.Children.Add(new PPSection(v.Title, Keep("view:" + v.Key, () => new PluginViewHost(view, new ViewContext { Plugin = view.Plugin, View = view.Id }, scrolls: false))));
        }
        col.Padding = new Thickness(M.Xl);
        return Scroll(col).Key("plugin.detail").Id("plugin.detail");
    }

    private UIElement Header(PluginInfo p)
    {
        var titleRow = HStack(M.Sm, Text(p.Name, Typo.Display),
            new PPBadge(p.Version, mono: true),
            new PPBadge(SourceTitle(p.Source), tone: p.Source == "bundled" ? null : "info"),
            p.Running ? new PPBadge(L.T("plugin.running"), tone: "success") : null);
        var meta = HStack(M.Sm, Text(p.Id, Typo.Mono),
            p.Vendor != null ? Text("·", Typo.Small) : null, p.Vendor != null ? Text(p.Vendor, Typo.Small) : null,
            p.Category != null ? Text("·", Typo.Small) : null, p.Category != null ? Text(p.Category, Typo.Small) : null);
        meta.Fg(ColorToken.TextSecondary);
        var texts = VStack(M.Xs, titleRow, meta,
            !string.IsNullOrEmpty(p.Description) ? Paragraph(p.Description, Typo.Body, ColorToken.TextSecondary) : null);
        texts.Align = CrossAlign.Stretch;

        var id = p.Id;
        var right = VStack(M.Sm,
            HStack(M.Sm, Text(L.T("plugin.enabled"), Typo.Body, ColorToken.TextSecondary).Shrink(false),
                new PPSwitch(Bind.Of(() => p.Enabled, on => AppModel.Shared.Perform(async api =>
                {
                    await api.SetPluginEnabledAsync(id, on);
                    await AppModel.Shared.ReloadPlugins();
                }))).Id("plugin.enabled")).Right(),
            p.Source != "bundled"
                ? new PPButton(p.BundledVersion != null ? L.T("plugin.revert", p.BundledVersion) : L.T("plugin.uninstall"),
                    () => Dialogs.Confirm(L.T("plugin.uninstall.title", p.Name), null, async () =>
                    {
                        await AppModel.Shared.Client().UninstallPluginAsync(id);
                        await AppModel.Shared.ReloadPlugins();
                    }, ok: L.T("plugin.uninstall"), danger: true),
                    icon: "trash-2", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Right()
                : null,
            !string.IsNullOrEmpty(p.Homepage) ? new PPLink(L.T("plugin.homepage"), () => Actions.OpenURL(p.Homepage!), icon: "external-link").Right() : null);
        right.Align = CrossAlign.End;

        var row = HStack(M.Lg, PPIcon.Raw(p.Icon, p.Id, "puzzle", 40).Top(), texts.Shrink(), Spacer(), right.Top());
        row.Align = CrossAlign.Start;
        return row;
    }

    private static UIElement? ContribRow(string title, List<string> items)
    {
        if (items.Count == 0) return null;
        var row = HStack(M.Md, Text(title, Typo.Body, ColorToken.TextSecondary).W(140).Shrink(false).Top(),
            Flow(M.Xs, items.Select(i => new PPBadge(i))).Flex());
        row.Align = CrossAlign.Start;
        return row;
    }

    private static string SourceTitle(string s) => s switch
    {
        "bundled" => L.T("plugin.source.bundled"),
        "dev" => L.T("plugin.source.dev"),
        _ => L.T("plugin.source.user"),
    };
}

/// <summary>商店（store.list）：自适应网格，安装 / 更新 / 不兼容 / 已安装（对应 Mac StoreView）。</summary>
internal sealed class StoreView : PPView
{
    private readonly State<List<StoreEntry>?> _entries;
    private readonly State<string?> _error;
    private readonly State<bool> _loading;
    private bool _started;

    public StoreView()
    {
        _entries = UseState<List<StoreEntry>?>(null);
        _error = UseState<string?>(null);
        _loading = UseState(false);
    }

    protected override void OnAppear()
    {
        if (_started) return;
        _started = true;
        Load(false);
    }

    private async void Load(bool refresh)
    {
        _loading.Value = true;
        try
        {
            _entries.Value = await AppModel.Shared.Client().StoreAsync(refresh);
            _error.Value = null;
        }
        catch (Exception e)
        {
            _error.Value = e.Message;
        }
        finally
        {
            _loading.Value = false;
        }
    }

    protected override UIElement Build()
    {
        var bar = HStack(0, Spacer(),
            new PPButton(L.T("plugin.storeRefresh"), () => Load(true), icon: "refresh-cw", kind: PPButtonKind.Ghost, size: PPButtonSize.Small, busy: _loading.Value)
                .Id("store.refresh"));
        bar.Padding = new Thickness(M.Lg, M.Sm, M.Lg, M.Sm);
        UIElement body;
        if (_entries.Value is { } entries)
        {
            if (entries.Count == 0) body = new PPEmptyState("store", L.T("plugin.storeEmpty"));
            else
            {
                var grid = new AdaptiveGrid(300, 460, M.Lg, center: true);
                foreach (var e in entries) grid.Children.Add(StoreCard(e));
                body = Scroll(Box(grid).Pad(M.Xl)).Key("store.grid");
            }
        }
        else if (_error.Value is { } err)
            body = new PPEmptyState("circle-alert", err, L.T("connection.retry"), () => Load(true));
        else
            body = new PPSpinner();
        var col = VStack(0, bar, body.Flex());
        col.Align = CrossAlign.Stretch;
        return col;
    }

    private static UIElement StoreCard(StoreEntry e)
    {
        var canUpdate = e.Installed != null && e.Installed != e.Version;
        UIElement action;
        if (!e.Compatible) action = new PPBadge(L.T("plugin.incompatible"), tone: "warning");
        else if (e.Installed is { } inst && !canUpdate) action = new PPBadge(L.T("plugin.installedVersion", inst), tone: "success");
        else
        {
            var id = e.Id;
            action = new PPButton(canUpdate ? L.T("plugin.update") : L.T("plugin.install"),
                () => AppModel.Shared.Perform(L.T("plugin.installFailed"), async api => AppModel.Shared.Track(await api.InstallFromStoreAsync(id))),
                icon: "download", kind: PPButtonKind.Primary, size: PPButtonSize.Small).Id("store.install." + e.Id);
        }
        var head = HStack(M.Sm,
            new PPIcon("puzzle", M.IconLarge, ColorToken.IconSecondary),
            VStack(1, Text(e.Name, Typo.Title), Text(e.Version + (e.Vendor != null ? "  ·  " + e.Vendor : ""), Typo.Small, ColorToken.TextTertiary)).Shrink(),
            Spacer(),
            action);
        var col = VStack(M.Sm, head,
            !string.IsNullOrEmpty(e.Description) ? Paragraph(e.Description, Typo.Small, ColorToken.TextSecondary).Lines(3) : null,
            e.Engines?.Paperplane is { Length: > 0 } eng ? Text(L.T("plugin.engines", eng), Typo.Caption, ColorToken.TextTertiary) : null);
        col.Align = CrossAlign.Stretch;
        return new PPCard(col).Id("store.card." + e.Id);
    }
}

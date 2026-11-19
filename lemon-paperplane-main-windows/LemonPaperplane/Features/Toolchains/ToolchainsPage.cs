using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>工具链：按种类分组列出已安装的，下载新版本（任务进度），手动添加路径，移除（对应 Mac ToolchainsPage）。</summary>
public sealed class ToolchainsPage : PPView
{
    private readonly DataStore _data = AppModel.Shared.Data;
    private readonly State<bool> _refreshing;

    public ToolchainsPage()
    {
        _refreshing = UseState(false);
        Observe(_data, nameof(DataStore.ToolchainKinds));
    }

    protected override void OnAppear() => _ = AppModel.Shared.ReloadToolchains();

    private async void Rescan()
    {
        _refreshing.Value = true;
        try { await AppModel.Shared.Client().RefreshToolchainsAsync(); }
        catch (Exception e) { AppModel.Shared.Toasts.Error(e); }
        await AppModel.Shared.ReloadToolchains();
        _refreshing.Value = false;
    }

    protected override UIElement Build()
    {
        var header = new PPPageHeader(L.T("sidebar.toolchains"), null,
            new PPButton(L.T("toolchain.rescan"), Rescan, icon: "refresh-cw", kind: PPButtonKind.Ghost, size: PPButtonSize.Small, busy: _refreshing.Value)
                .Id("toolchains.rescan"));
        UIElement body;
        if (_data.ToolchainKinds.Count == 0) body = new PPEmptyState("boxes", L.T("toolchain.noKinds"));
        else
        {
            var col = VStack(M.Xxl, _data.ToolchainKinds.Select(k => Keep("kind:" + k.Kind, () => new ToolchainKindSection(k), s => s.Update(k))));
            col.Align = CrossAlign.Stretch;
            col.Padding = new Thickness(M.Xl);
            col.MaxWidth = 960;
            col.HorizontalAlignment = HorizontalAlignment.Left;
            body = Scroll(col).Key("toolchains.scroll");
        }
        var page = VStack(0, header, new PPDivider(subtle: true), body.Flex());
        page.Align = CrossAlign.Stretch;
        return page;
    }

    /// <summary>版本号降序比较（数字段逐段比较）。</summary>
    public static int CompareVersions(string a, string b)
    {
        static List<int> Parts(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"\d+").Cast<System.Text.RegularExpressions.Match>()
            .Select(m => int.TryParse(m.Value, out var n) ? n : int.MaxValue).ToList();
        var pa = Parts(a);
        var pb = Parts(b);
        for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            int x = i < pa.Count ? pa[i] : 0, y = i < pb.Count ? pb[i] : 0;
            if (x != y) return y.CompareTo(x);
        }
        return -string.CompareOrdinal(a, b);
    }
}

/// <summary>一个种类：标题行（图标、名称、数量、添加路径、下载）、正在下载的任务、已安装列表。</summary>
internal sealed class ToolchainKindSection : PPView
{
    private readonly DataStore _data = AppModel.Shared.Data;
    private readonly RunStore _runs = AppModel.Shared.Runs;
    private readonly State<bool> _downloadOpen = new(false);
    private ToolchainKind _kind;

    public ToolchainKindSection(ToolchainKind kind)
    {
        _kind = kind;
        Observe(_data, nameof(DataStore.Toolchains));
        Observe(_runs, nameof(RunStore.Tasks));
    }

    public void Update(ToolchainKind k)
    {
        if (ReferenceEquals(_kind, k)) return;
        _kind = k;
        Invalidate();
    }

    protected override UIElement Build()
    {
        var kind = _kind;
        var list = _data.ToolchainsOf(kind.Kind).ToList();
        list.Sort((a, b) => ToolchainsPage.CompareVersions(a.Version, b.Version));
        var installing = _runs.ActiveTasks.Where(t => _runs.ToolchainTaskKind.TryGetValue(t.Id, out var k)
            ? k == kind.Kind
            : t.Kind == "toolchain" && t.Title.IndexOf(kind.Name, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();

        // 下载按钮跨重建复用（任务进度变化时弹出框不被关掉）
        var download = kind.Installable ? Keep("download", () =>
        {
            var b = new PPButton(L.T("toolchain.download"), null, icon: "download", size: PPButtonSize.Small).Id("toolchain.download." + kind.Kind);
            b.Action = () =>
            {
                if (!_downloadOpen.Value && PopupController.WasJustDismissed(b)) return;
                _downloadOpen.Value = !_downloadOpen.Value;
            };
            return b.PPPopover(_downloadOpen, () => new AvailableVersionsPopover(_kind, () => _downloadOpen.Value = false), PopupAlignment.Trailing, wantsKey: true);
        }, b => b.Title = L.T("toolchain.download")) : null;

        var head = HStack(M.Sm,
            PPIcon.Raw(kind.Icon, kind.Plugin, "boxes", M.IconLarge),
            Text(kind.Name, Typo.Title),
            new PPCountBadge(list.Count),
            Spacer(),
            new PPButton(L.T("toolchain.addPath"), AddPath, icon: "folder-plus", kind: PPButtonKind.Ghost, size: PPButtonSize.Small),
            download);
        var col = VStack(M.Md, head);
        col.Align = CrossAlign.Stretch;
        foreach (var t in installing)
        {
            var id = t.Id;
            var top = HStack(M.Sm, new PPSpinner(M.IconSmall, ColorToken.Accent), Text(t.Title), Spacer(),
                !string.IsNullOrEmpty(t.Message) ? Text(t.Message, Typo.Small, ColorToken.TextSecondary) : null,
                new PPIconButton("scroll-text", L.T("task.viewLog"), () => Actions.ShowTaskLog(id), size: M.IconButtonSmall),
                new PPIconButton("x", L.T("common.cancel"), () => Actions.CancelTask(id), size: M.IconButtonSmall));
            var inner = VStack(M.Sm, top, new PPProgressBar(t.Percent));
            inner.Align = CrossAlign.Stretch;
            col.Children.Add(new PPCard(inner, M.Md));
        }
        if (list.Count == 0)
            col.Children.Add(Text(L.T("toolchain.noneInstalled"), Typo.Body, ColorToken.TextSecondary).Pad(0, M.Sm));
        else
        {
            var rows = VStack(0);
            rows.Align = CrossAlign.Stretch;
            for (int i = 0; i < list.Count; i++)
            {
                rows.Children.Add(Row(list[i]));
                if (i < list.Count - 1) rows.Children.Add(new PPDivider(subtle: true).Margins(M.Md, 0, M.Md, 0));
            }
            col.Children.Add(new PPCard(rows, M.Xs));
        }
        return col.Id("toolchain.kind." + kind.Kind);
    }

    private static UIElement Row(Toolchain tc)
    {
        var title = HStack(M.Sm,
            Text(tc.Version, Typo.BodyStrong).TabularDigits(),
            !string.IsNullOrEmpty(tc.Vendor) ? Text(tc.Vendor, Typo.Body, ColorToken.TextSecondary) : null,
            !string.IsNullOrEmpty(tc.Arch) ? new PPBadge(tc.Arch!, mono: true) : null,
            tc.Managed ? new PPBadge(L.T("toolchain.managed"), tone: "info") : null,
            !string.IsNullOrEmpty(tc.Source) && !tc.Managed ? new PPBadge(tc.Source!) : null);
        var info = VStack(2, title, new PPLink(Format.AbbreviatePath(tc.Path), () => Actions.RevealInExplorer(tc.Path), mono: true).Left());
        var row = HStack(M.Md,
            info.Shrink(),
            Spacer(M.Md),
            new PPIconButton("copy", L.T("toolchain.copyPath"), () => Actions.Copy(tc.Path), size: M.IconButtonSmall),
            new PPIconButton("trash-2", L.T("common.remove"), () => Dialogs.Confirm(L.T("toolchain.remove.title", tc.DisplayName),
                tc.Managed ? L.T("toolchain.remove.managed") : L.T("toolchain.remove.external"), async () =>
                {
                    await AppModel.Shared.Client().RemoveToolchainAsync(tc.Id);
                    await AppModel.Shared.ReloadToolchains();
                }, ok: L.T("common.remove"), danger: true), size: M.IconButtonSmall));
        row.Padding = new Thickness(M.Md, M.Sm, M.Md, M.Sm);
        return row;
    }

    /// <summary>手动添加：Windows 的系统对话框不能同时选文件和文件夹，这里选工具链的安装目录（插件按目录识别）。</summary>
    private void AddPath()
    {
        var kind = _kind.Kind;
        if (ImportFlow.ChooseFolder(L.T("toolchain.addPath")) is not { } path) return;
        AppModel.Shared.Perform(L.T("toolchain.addPath"), async api =>
        {
            await api.AddToolchainAsync(kind, path);
            await AppModel.Shared.ReloadToolchains();
        });
    }
}

/// <summary>可下载版本列表（toolchain.available），LTS / 推荐标记（对应 Mac AvailableVersionsPopover）。</summary>
internal sealed class AvailableVersionsPopover : PPView
{
    private readonly ToolchainKind _kind;
    private readonly Action _close;
    private readonly State<List<AvailableVersion>?> _versions;
    private readonly State<string?> _error;
    private readonly State<string> _filter;
    private readonly State<bool> _ltsOnly;
    private bool _started;

    public AvailableVersionsPopover(ToolchainKind kind, Action close)
    {
        _kind = kind;
        _close = close;
        _versions = UseState<List<AvailableVersion>?>(null);
        _error = UseState<string?>(null);
        _filter = UseState("");
        _ltsOnly = UseState(false);
        Width = 360;
    }

    protected override async void OnAppear()
    {
        if (_started) return;
        _started = true;
        try { _versions.Value = await AppModel.Shared.Client().AvailableToolchainsAsync(_kind.Kind); }
        catch (Exception e) { _error.Value = e.Message; }
    }

    private void Install(AvailableVersion v)
    {
        _close();
        var kind = _kind.Kind;
        AppModel.Shared.Perform(L.T("toolchain.download"), async api =>
        {
            var t = await api.InstallToolchainAsync(kind, v.Version);
            AppModel.Shared.Runs.ToolchainTaskKind[t.Id] = kind;
            AppModel.Shared.Track(t);
        });
    }

    protected override UIElement Build()
    {
        // 筛选框跨重建复用（输入中不被换掉）
        var field = Keep("filter." + L10n.Shared.Locale, () => new PPTextField(L.T("common.filter"), _filter, icon: "search", height: M.ControlHeightSmall).Id("toolchain.filter"));
        var top = HStack(M.Sm, field.Flex(), new PPCheckbox(_ltsOnly, "LTS"));
        top.Padding = new Thickness(M.Sm);
        var col = VStack(0, top, new PPDivider(subtle: true));
        col.Align = CrossAlign.Stretch;
        if (_versions.Value is { } versions)
        {
            var f = _filter.Value;
            bool Has(string? s) => !string.IsNullOrEmpty(s) && s!.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0;
            var shown = versions.Where(v => (!_ltsOnly.Value || v.Lts) && (f.Length == 0 || v.Version.Contains(f) || Has(v.Label) || Has(v.Vendor)));
            var rows = VStack(0, shown.Select(v =>
            {
                var label = v.Label ?? v.Version;
                return new PPRow(HStack(M.Sm,
                    Text(label),
                    !string.IsNullOrEmpty(v.Vendor) && !(v.Label ?? "").Contains(v.Vendor!) ? Text(v.Vendor, Typo.Body, ColorToken.TextTertiary) : null,
                    v.Lts ? new PPBadge("LTS", tone: "success") : null,
                    v.Recommended ? new PPBadge(L.T("toolchain.recommended"), tone: "info") : null,
                    Spacer(),
                    new PPIcon("download", M.IconSmall, ColorToken.IconSecondary)), action: () => Install(v)).A11y(label).Id("toolchain.version");
            }));
            rows.Align = CrossAlign.Stretch;
            rows.Padding = new Thickness(M.Xs);
            col.Children.Add(Scroll(rows).Key("toolchain.versions").H(320));
        }
        else if (_error.Value is { } err)
            col.Children.Add(Paragraph(err, Typo.Body, ColorToken.Danger).Pad(M.Md));
        else
            col.Children.Add(Box(new PPSpinner()).Pad(M.Lg));
        return col.Id("toolchain.versions.popover");
    }
}

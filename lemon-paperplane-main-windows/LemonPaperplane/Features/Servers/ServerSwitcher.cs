using System;
using System.Windows;
using System.Windows.Controls;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 窗口左上角的服务器切换器（docs/remote.md 5.1，替换原来的工作区标题；对应 Mac ServerSwitcher）：系统图标 + 当前服务器名 + ⌄，
/// 连着远程且没连上时名字后面有状态点。点开列出本机与已添加的服务器，底部「添加服务器…」「管理服务器…」。
/// </summary>
public sealed class ServerSwitcher : PPView
{
    private readonly AppModel _model = AppModel.Shared;
    // 不订阅：弹出框的锚点跨重建复用（锚点被换掉会把打开着的弹出框关掉）
    private readonly State<bool> _open = new(false);

    public ServerSwitcher()
    {
        Observe(_model, nameof(AppModel.Server), nameof(AppModel.SystemInfo), nameof(AppModel.LocalInfo), nameof(AppModel.Connection));
    }

    private string CurrentOs => _model.IsRemote ? _model.SystemInfo?.Os ?? _model.Server?.Os ?? "" : _model.LocalInfo?.Os ?? "windows";

    protected override UIElement Build()
    {
        var c = _model.Connection;
        string? phase = !_model.IsRemote || c.IsConnected ? null : c.IsFailed ? ColorToken.StatusFailed : ColorToken.StatusStarting;
        var row = HStack(M.Sm,
            new PPIcon(ServerDisplay.Icon(CurrentOs), M.Icon, ColorToken.IconPrimary),
            Text(_model.ServerName, Typo.Title).Shrink(),
            phase != null ? Box().Size(6, 6).Rounded(3).Bg(phase) : null,
            new PPIcon("chevron-down", 12, ColorToken.IconSecondary));
        return Keep("label", () =>
        {
            var label = Box().Pad(M.Sm, 0).H(M.IconButton).Rounded(M.RadiusMD);
            void Paint() => label.Bg(_open.Value ? ColorToken.BgPressed : label.IsMouseOver ? ColorToken.BgHover : ColorToken.Clear);
            label.MouseEnter += (_, _) => Paint();
            label.MouseLeave += (_, _) => Paint();
            _open.PropertyChanged += (_, _) => Paint();
            Paint();
            label.Tip(L.T("server.switcher.help")).A11y(L.T("server.switcher.help")).Id("toolbar.server");
            label.OnClick(() =>
            {
                if (!_open.Value && PopupController.WasJustDismissed(label)) return;
                _open.Value = !_open.Value;
            });
            return label.PPPopover(_open, () => new PopoverContent(new ServerSwitcherList(() => _open.Value = false), 340));
        }, label =>
        {
            Detach(row);
            label.Child = row;
            label.ToolTip = L.T("server.switcher.help");
        });
    }
}

/// <summary>切换器弹出框里的列表：本机永远第一行，当前那台打勾；每行系统图标、名称、第二行、运行中数量与状态点。</summary>
public sealed class ServerSwitcherList : PPView
{
    private readonly Action _close;
    private readonly AppModel _model = AppModel.Shared;
    private readonly ServerStore _store = ServerStore.Shared;

    public ServerSwitcherList(Action close)
    {
        _close = close;
        Observe(_model, nameof(AppModel.Server), nameof(AppModel.Connection), nameof(AppModel.SystemInfo), nameof(AppModel.LocalInfo));
        Observe(_model.Runs, nameof(RunStore.Runs));
        Observe(_store, nameof(ServerStore.Servers), nameof(ServerStore.Summaries));
    }

    protected override void OnAppear() => ServerSummaries.Refresh();

    protected override UIElement Build()
    {
        var col = VStack(0);
        col.Align = CrossAlign.Stretch;
        var list = VStack(0);
        list.Align = CrossAlign.Stretch;
        list.Children.Add(LocalRow());
        foreach (var s in _store.Servers) list.Children.Add(RemoteRow(s));
        col.Children.Add(list.Pad(M.Xs));
        col.Children.Add(new PPDivider(subtle: true));
        var actions = VStack(0,
            Footer(L.T("server.add"), "plus", () => { _close(); AddServerDialog.Present(); }).Id("server.add"),
            Footer(L.T("server.manage"), "settings", () => { _close(); ServerSettings.Open(); }).Id("server.manage"));
        actions.Align = CrossAlign.Stretch;
        col.Children.Add(actions.Pad(M.Xs));
        return col;
    }

    private UIElement LocalRow()
    {
        var current = !_model.IsRemote;
        var info = _model.LocalInfo;
        var summary = _store.Summary(ServerStore.LocalKey);
        var state = _model.LocalApi == null ? ServerSummary.Failed : current ? CurrentState() : summary.State == ServerSummary.Unknown ? ServerSummary.Online : summary.State;
        int? runs = current ? _model.Runs.ActiveRuns.Count : summary.Runs;
        var detail = ServerDisplay.HostLine(info?.Hostname, info?.Os ?? "windows");
        return Row(ServerDisplay.Icon(info?.Os ?? "windows"), L.T("server.local"), detail, runs, state, current, () => Pick(null)).Id("server.row.local");
    }

    private UIElement RemoteRow(ServerRecord s)
    {
        var current = _model.Server?.Id == s.Id;
        var state = current ? CurrentState() : _store.Summary(s.Id).State;
        var os = current ? _model.SystemInfo?.Os ?? s.Os : s.Os;
        var detail = state switch
        {
            ServerSummary.Online => ServerDisplay.HostLine(current ? _model.SystemInfo?.Hostname ?? s.Hostname : s.Hostname, os),
            ServerSummary.Connecting => s.Address + " · " + L.T("server.status.connecting"),
            ServerSummary.Failed => s.Address + " · " + L.T("server.status.failed"),
            _ => s.Address + " · " + L.T("server.status.disconnected"),
        };
        int? runs = current ? (_model.Connection.IsConnected ? _model.Runs.ActiveRuns.Count : null) : _store.Summary(s.Id).Runs;
        return Row(ServerDisplay.Icon(os), s.Name, detail, runs, state, current, () => Pick(s)).Id("server.row." + s.Id);
    }

    /// <summary>当前服务器的连接阶段。</summary>
    private string CurrentState()
    {
        var c = _model.Connection;
        return c.IsConnected ? ServerSummary.Online : c.IsFailed ? ServerSummary.Failed : ServerSummary.Connecting;
    }

    private void Pick(ServerRecord? s)
    {
        _close();
        AppModel.Shared.SwitchTo(s);
    }

    private static UIElement Row(string icon, string name, string detail, int? runs, string state, bool current, Action action)
    {
        var check = new Grid { Width = M.IconSmall, Height = M.IconSmall };
        if (current) check.Children.Add(new PPIcon("check", M.IconSmall, ColorToken.Accent).Center());
        var text = VStack(1, Text(name, current ? Typo.BodyStrong : Typo.Body),
            detail.Length > 0 ? TextMiddle(detail, Typo.Small, ColorToken.TextTertiary) : null);
        UIElement? count = runs is { } n && state == ServerSummary.Online
            ? HStack(M.Xs, new PPIcon("activity", M.IconSmall, n > 0 ? ColorToken.StatusRunning : ColorToken.IconSecondary),
                Text(n.ToString(), Typo.Label, ColorToken.TextSecondary).TabularDigits()).Tip(L.T("toolbar.runs"))
            : null;
        var content = HStack(M.Md, check, new PPIcon(icon, M.Icon, ColorToken.IconPrimary), text.Flex(), count, Dot(state)).Margins(0, M.Sm, 0, M.Sm);
        return new PPRow(content, action: action, height: 0).A11y(name);
    }

    private static UIElement Dot(string state)
    {
        if (state == ServerSummary.Connecting) return new PPSpinner(M.IconSmall).Tip(L.T("server.status.connecting"));
        var (color, tip) = state switch
        {
            ServerSummary.Online => (ColorToken.StatusRunning, L.T("server.status.connected")),
            ServerSummary.Failed => (ColorToken.StatusFailed, L.T("server.status.failed")),
            _ => (ColorToken.StatusIdle, L.T("server.status.disconnected")),
        };
        return Box().Size(M.StatusDot, M.StatusDot).Rounded(M.StatusDot / 2).Bg(color).Tip(tip);
    }

    private static UIElement Footer(string title, string icon, Action action) =>
        new PPRow(HStack(M.Md, Gap(M.IconSmall).H(1), new PPIcon(icon, M.Icon, ColorToken.IconPrimary), Text(title), Spacer()), action: action).A11y(title);
}

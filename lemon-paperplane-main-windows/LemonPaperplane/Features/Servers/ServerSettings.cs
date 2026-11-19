using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>设置 → 服务器（docs/remote.md 5.3，对应 Mac ServerSettings）：已添加的服务器；允许其他设备连接本机（连着远程时管理那台服务器）。</summary>
public static class ServerSettings
{
    /// <summary>设置页里服务器分组的锚点（自动化 ID）。</summary>
    public const string Anchor = "settings.servers";

    /// <summary>「管理服务器…」：打开设置并滚到服务器分组。</summary>
    public static void Open()
    {
        AppModel.Shared.Nav.Page = NavPage.SettingsPage;
        MainWindow.Current?.ShowMainWindow();
        // 设置页建好、排好版后再滚动
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow.Current is { } w && Find(w, Anchor) is { } el) el.BringIntoView(new Rect(0, 0, 1, 1));
        }), DispatcherPriority.ContextIdle);
    }

    private static FrameworkElement? Find(DependencyObject root, string id)
    {
        if (root is FrameworkElement fe && AutomationProperties.GetAutomationId(fe) == id) return fe;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            if (Find(VisualTreeHelper.GetChild(root, i), id) is { } hit) return hit;
        return null;
    }

    /// <summary>证书指纹的前 8 位（列表里显示）。</summary>
    public static string ShortFingerprint(string fp)
    {
        var s = fp.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? fp.Substring(7) : fp;
        s = s.Replace(" ", "").Replace(":", "");
        return (s.Length > 8 ? s.Substring(0, 8) : s).ToUpperInvariant();
    }

    public static IEnumerable<MenuEntry> Menu(ServerRecord s)
    {
        var app = AppModel.Shared;
        return new[]
        {
            MenuEntry.Item(L.T("server.action.connect"), () => app.SwitchTo(s), icon: "plug", disabled: app.Server?.Id == s.Id && app.Connection.IsConnected),
            MenuEntry.Item(L.T("server.action.rename"), () => Dialogs.Input(L.T("server.rename.title"), L.T("common.name"), name =>
            {
                if (ServerStore.Shared.Find(s.Id) is { } cur)
                {
                    var n = cur with { Name = name };
                    ServerStore.Shared.Put(n);
                    app.ServerUpdated(n);
                }
                return Task.CompletedTask;
            }, initial: s.Name, ok: L.T("common.save")), icon: "pencil"),
            MenuEntry.Item(L.T("server.action.relogin"), () => AddServerDialog.PresentRelogin(s), icon: "key-round"),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("server.action.remove"), () => Dialogs.Confirm(L.T("server.remove.confirm", s.Name), L.T("server.remove.message"), () =>
            {
                // 只删本机记录和凭据管理器里的密钥；正在用它时先切回本机
                app.ServerRemoved(s.Id);
                ServerStore.Shared.Remove(s.Id);
            }, ok: L.T("server.action.remove"), danger: true), icon: "trash-2", destructive: true),
        };
    }

    /// <summary>卡片里的空行文字。</summary>
    internal static UIElement EmptyRow(string text, string? icon = null) =>
        HStack(M.Sm, icon != null ? new PPIcon(icon, M.Icon, ColorToken.IconSecondary) : null, Text(text, Typo.Body, ColorToken.TextSecondary), Spacer()).Pad(M.Md);

    /// <summary>卡片里的多行（行间细分隔线）。</summary>
    internal static UIElement Rows(IEnumerable<UIElement> rows)
    {
        var col = VStack(0);
        col.Align = CrossAlign.Stretch;
        var first = true;
        foreach (var r in rows)
        {
            if (!first) col.Children.Add(new PPDivider(subtle: true).Margins(M.Md, 0, M.Md, 0));
            first = false;
            col.Children.Add(r);
        }
        return col;
    }

    internal static UIElement More(Func<IEnumerable<MenuEntry>> entries, string id) =>
        new PPMenuButton(entries, Box(new PPIcon("ellipsis", M.Icon, ColorToken.IconSecondary).Center()).Size(M.IconButtonSmall, M.IconButtonSmall).Bg(ColorToken.Clear),
            PopupAlignment.Trailing, L.T("common.more")).Id(id);
}

/// <summary>已添加的服务器：系统图标、名称、地址、指纹前 8 位，··· 菜单（连接、重命名、重新登录、删除）。</summary>
public sealed class SavedServersSection : PPView
{
    private readonly AppModel _model = AppModel.Shared;

    public SavedServersSection()
    {
        Observe(ServerStore.Shared, nameof(ServerStore.Servers));
        Observe(_model, nameof(AppModel.Server), nameof(AppModel.Connection));
    }

    protected override UIElement Build()
    {
        var list = ServerStore.Shared.Servers;
        var body = list.Count == 0
            ? ServerSettings.EmptyRow(L.T("server.saved.empty"), "server")
            : ServerSettings.Rows(list.Select(Row));
        var add = new PPButton(L.T("server.add"), AddServerDialog.Present, icon: "plus", size: PPButtonSize.Small).Id("settings.servers.add");
        return new PPSection(L.T("settings.servers"), new PPCard(body, padding: M.Xs), accessory: add).Id(ServerSettings.Anchor);
    }

    private UIElement Row(ServerRecord s)
    {
        var current = _model.Server?.Id == s.Id;
        var name = HStack(M.Sm, Text(s.Name, Typo.BodyStrong),
            current && _model.Connection.IsConnected ? new PPBadge(L.T("server.status.connected"), tone: "success") : null);
        var detail = HStack(M.Md, TextMiddle(s.Address, Typo.MonoSmall, ColorToken.TextSecondary).Shrink(),
            s.Fingerprint.Length > 0 ? Text(ServerSettings.ShortFingerprint(s.Fingerprint), Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false).Tip(L.T("host.fingerprint")) : null);
        var row = HStack(M.Md, new PPIcon(ServerDisplay.Icon(s.Os), M.Icon, ColorToken.IconPrimary),
            VStack(2, name, detail).Flex(), ServerSettings.More(() => ServerSettings.Menu(s), "settings.server.more." + s.Id));
        row.Margin = new Thickness(M.Md, M.Sm, M.Md, M.Sm);
        row.Bg(ColorToken.Clear).PPContextMenu(() => ServerSettings.Menu(s));
        return row.Id("settings.server." + s.Id);
    }
}

/// <summary>
/// 允许其他设备连接本机（连着远程服务器时管理那台：remote.* 都走当前客户端）：开关、端口、开机自动启动、地址、证书指纹；
/// 开着时再有 访问密钥、账号密码、已登录的设备、安全日志 几个分组。
/// </summary>
public sealed class HostSection : PPView
{
    private readonly AppModel _model = AppModel.Shared;
    private readonly State<RemoteStatus?> _status;
    private readonly State<List<RemoteKey>> _keys;
    private readonly State<List<RemoteUser>> _users;
    private readonly State<List<RemoteAuditEntry>> _audit;
    private readonly State<string?> _error;
    private readonly State<bool> _busy;
    private bool _started;

    public HostSection()
    {
        _status = UseState<RemoteStatus?>(null);
        _keys = UseState(new List<RemoteKey>());
        _users = UseState(new List<RemoteUser>());
        _audit = UseState(new List<RemoteAuditEntry>());
        _error = UseState<string?>(null);
        _busy = UseState(false);
        Observe(_model, nameof(AppModel.Server));
    }

    protected override void OnAppear()
    {
        _model.PropertyChanged += OnModelChanged;
        if (_started) return;
        _started = true;
        _ = Load();
    }

    protected override void OnDisappear() => _model.PropertyChanged -= OnModelChanged;

    /// <summary>连接恢复（重试、重新登录）后，之前没取到的重新取。</summary>
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.Connection) && _model.Connection.IsConnected && (_error.Value != null || _status.Value == null))
            _ = Load();
    }

    /// <summary>取状态，再取密钥、账号、安全日志。</summary>
    public async Task Load()
    {
        try
        {
            _status.Value = await _model.Client().RemoteStatusAsync();
            _error.Value = null;
            await LoadLists();
        }
        catch (Exception e)
        {
            _error.Value = e.Message;
        }
    }

    public async Task LoadLists()
    {
        if (_model.Api is not { } api) return;
        var k = Try(api.RemoteKeysAsync());
        var u = Try(api.RemoteUsersAsync());
        var a = Try(api.RemoteAuditAsync(50));
        if (await k is { } keys) _keys.Value = keys;
        if (await u is { } users) _users.Value = users;
        if (await a is { } audit) _audit.Value = audit;
    }

    private static async Task<T?> Try<T>(Task<T> t) where T : class
    {
        try { return await t; }
        catch { return null; }
    }

    private async void Configure(bool? enabled = null, int? port = null, bool? passwordLogin = null, bool? autostart = null)
    {
        _busy.Value = true;
        try
        {
            _status.Value = await _model.Client().RemoteConfigureAsync(enabled, port, passwordLogin, autostart);
            await LoadLists();
        }
        catch (Exception e)
        {
            _model.Toasts.Error(e, L.T("settings.saveFailed"));
            await Load();
        }
        finally
        {
            _busy.Value = false;
        }
    }

    /// <summary>开关远程连接。连着这台远程服务器时关掉会把自己断开：先确认。</summary>
    private void SetEnabled(bool on)
    {
        if (on || !_model.IsRemote)
        {
            Configure(enabled: on);
            return;
        }
        Dialogs.Confirm(L.T("host.enabled"), L.T("host.disable.remoteConfirm", _model.ServerName), () => Configure(enabled: false),
            ok: L.T("host.disable"), danger: true);
        Invalidate();
    }

    protected override UIElement Build()
    {
        var title = _model.IsRemote ? L.T("host.titleRemote", _model.ServerName) : L.T("host.title");
        var col = VStack(M.Xxl, new PPSection(title, new PPCard(MainCard()), info: L.T("host.enabled.info")).Id("settings.host"));
        col.Align = CrossAlign.Stretch;
        if (_status.Value is { Enabled: true } st)
        {
            col.Children.Add(KeysSection());
            col.Children.Add(AccountsSection(st));
            col.Children.Add(DevicesSection());
            col.Children.Add(AuditSection());
        }
        return col;
    }

    // MARK: 开关、端口、地址、指纹

    private UIElement MainCard()
    {
        if (_status.Value is not { } st)
        {
            if (_error.Value is { } e)
                return new PPAlert("danger", e, null, new PPButton(L.T("server.retry"), () => _ = Load(), icon: "refresh-cw", size: PPButtonSize.Small));
            return HStack(0, new PPSpinner(M.IconSmall), Spacer()).H(M.RowHeight);
        }
        var col = VStack(M.Md);
        col.Align = CrossAlign.Stretch;
        UIElement? state = st.Error.Length > 0
            ? Text(L.T("host.error", st.Error), Typo.Small, ColorToken.Danger).Shrink()
            : st.Running ? HStack(M.Xs, Box().Size(6, 6).Rounded(3).Bg(ColorToken.StatusRunning), Text(L.T("host.running", st.Listen), Typo.Small, ColorToken.TextSecondary)) : null;
        col.Children.Add(SettingsPage.SettingRow(L.T("host.enabled"),
            HStack(M.Md, new PPSwitch(Bind.Of(() => st.Enabled, SetEnabled)).Disabled(_busy.Value).Id("host.enabled"), state)));
        if (!st.Enabled) return col;
        col.Children.Add(SettingsPage.SettingRow(L.T("host.port"),
            Keep("port", () => new NumberSetting(st.Port, "", v => { if (v is >= 1 and <= 65535) Configure(port: v); }).Id("host.port"), n => n.SetValue(st.Port))));
        if (st.AutostartSupported)
            col.Children.Add(SettingsPage.SettingRow(L.T("host.autostart"),
                new PPSwitch(Bind.Of(() => st.Autostart, v => Configure(autostart: v))).Disabled(_busy.Value).Id("host.autostart"), L.T("host.autostart.info")));
        col.Children.Add(new PPDivider(subtle: true));
        if (st.Addresses.Count > 0)
        {
            var addrs = VStack(0, st.Addresses.Select(a => CopyLine(a, a)));
            col.Children.Add(TopRow(L.T("host.addresses"), addrs, null));
        }
        // 证书第一次开启远程（或生成密钥）时才生成：没有时不显示这一行
        var fp = st.FingerprintDisplay.Length > 0 ? st.FingerprintDisplay : st.Fingerprint;
        if (fp.Length > 0)
            col.Children.Add(TopRow(L.T("host.fingerprint"), CopyLine(AddServerDialog.GroupedFingerprint(fp), fp, wrap: true),
                L.T("host.fingerprint.info")).Id("host.fingerprint"));
        return col;
    }

    /// <summary>标题靠上的设置行（内容多行时）。</summary>
    private static UIElement TopRow(string title, UIElement content, string? info)
    {
        var label = HStack(M.Xs, Text(title), info != null ? new PPInfoButton(info) : null).W(M.FormLabelWidth).Shrink(false);
        label.MinHeight = M.RowHeight;
        var row = HStack(M.Md, label, content is FrameworkElement fe ? fe.Flex() : content);
        row.Align = CrossAlign.Start;
        return row;
    }

    private static UIElement CopyLine(string shown, string value, bool wrap = false)
    {
        var text = Selectable(shown, Typo.Mono, wrap: wrap);
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = HStack(M.Sm, text.Shrink(),
            new PPIconButton("copy", L.T("host.copy"), () => Actions.Copy(value, L.T("host.copied")), size: M.IconButtonSmall, iconSize: M.IconSmall), Spacer(0));
        row.MinHeight = M.RowHeight;
        return row;
    }

    // MARK: 访问密钥、已登录的设备

    private UIElement KeysSection()
    {
        var list = _keys.Value.Where(k => k.Kind != RemoteKeyKind.Device).ToList();
        var body = list.Count == 0 ? ServerSettings.EmptyRow(L.T("host.keys.empty"))
            : ServerSettings.Rows(list.Select(k => KeyRow(k, "key-round", k.Name, L.T("host.keys.created.at", VCSDate.Full(k.CreatedAt)))));
        var add = new PPButton(L.T("host.keys.add"), () => HostDialogs.CreateKey(this), icon: "key-round", size: PPButtonSize.Small).Id("host.keys.add");
        return new PPSection(L.T("host.keys"), new PPCard(body, padding: M.Xs), accessory: add).Id("host.keys");
    }

    private UIElement DevicesSection()
    {
        var list = _keys.Value.Where(k => k.Kind == RemoteKeyKind.Device).ToList();
        var body = list.Count == 0 ? ServerSettings.EmptyRow(L.T("host.devices.empty"))
            : ServerSettings.Rows(list.Select(k => KeyRow(k, "monitor", k.Name, k.User)));
        return new PPSection(L.T("host.devices"), new PPCard(body, padding: M.Xs)).Id("host.devices");
    }

    private UIElement KeyRow(RemoteKey k, string icon, string title, string subtitle)
    {
        var sub = string.Join("  ", new[] { subtitle, LastUsed(k) }.Where(x => x.Length > 0));
        var row = HStack(M.Md,
            new PPIcon(icon, M.Icon, ColorToken.IconSecondary),
            VStack(2,
                HStack(M.Sm, Text(title, Typo.BodyStrong), k.Current ? new PPBadge(L.T("host.devices.current"), tone: "info") : null),
                Text(sub, Typo.Small, ColorToken.TextTertiary)).Flex(),
            new PPButton(L.T("host.keys.revoke"), () => Dialogs.Confirm(L.T("host.keys.revoke.confirm", title), L.T("host.keys.revoke.message"), async () =>
            {
                await _model.Client().RemoteKeyRevokeAsync(k.Id);
                await Load();
            }, ok: L.T("host.keys.revoke"), danger: true), kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("host.revoke." + k.Id));
        row.Margin = new Thickness(M.Md, M.Sm, M.Md, M.Sm);
        return row.Id("host.key." + k.Id);
    }

    private static string LastUsed(RemoteKey k) =>
        k.LastUsedAt is { } t && t.Year > 1 ? L.T("host.keys.lastUsed", VCSDate.Short(t), k.LastIP.Length > 0 ? k.LastIP : "-") : L.T("host.keys.neverUsed");

    // MARK: 账号密码

    private UIElement AccountsSection(RemoteStatus st)
    {
        var col = VStack(0);
        col.Align = CrossAlign.Stretch;
        col.Children.Add(Box(SettingsPage.SettingRow(L.T("host.passwordLogin"),
            new PPSwitch(Bind.Of(() => st.PasswordLogin, v => Configure(passwordLogin: v))).Disabled(_busy.Value).Id("host.passwordLogin"),
            L.T("host.passwordLogin.info"))).Pad(M.Md, M.Xs));
        if (st.PasswordLogin)
        {
            col.Children.Add(new PPDivider(subtle: true).Margins(M.Md, 0, M.Md, 0));
            col.Children.Add(_users.Value.Count == 0 ? ServerSettings.EmptyRow(L.T("host.users.empty")) : ServerSettings.Rows(_users.Value.Select(UserRow)));
        }
        var add = st.PasswordLogin ? new PPButton(L.T("host.users.add"), () => HostDialogs.SetPassword(this, null), icon: "plus", size: PPButtonSize.Small).Id("host.users.add") : null;
        return new PPSection(L.T("host.users"), new PPCard(col, padding: M.Xs), accessory: add).Id("host.users");
    }

    private UIElement UserRow(RemoteUser u)
    {
        var when = u.UpdatedAt is { } d && d.Year > 1 ? VCSDate.Short(d) : null;
        IEnumerable<MenuEntry> Menu() => new[]
        {
            MenuEntry.Item(L.T("host.users.setPassword"), () => HostDialogs.SetPassword(this, u.Name), icon: "key-round"),
            MenuEntry.Divider,
            MenuEntry.Item(L.T("common.delete"), () => Dialogs.Confirm(L.T("host.users.remove.confirm", u.Name), L.T("host.users.remove.message"), async () =>
            {
                await _model.Client().RemoteUserRemoveAsync(u.Name);
                await Load();
            }, ok: L.T("common.delete"), danger: true), icon: "trash-2", destructive: true),
        };
        var row = HStack(M.Md, new PPIcon("user", M.Icon, ColorToken.IconSecondary), Text(u.Name, Typo.BodyStrong),
            when != null ? Text(when, Typo.Small, ColorToken.TextTertiary) : null, Spacer(), ServerSettings.More(Menu, "host.user.more." + u.Name));
        row.Margin = new Thickness(M.Md, M.Sm, M.Md, M.Sm);
        return row.Id("host.user." + u.Name);
    }

    // MARK: 安全日志

    private UIElement AuditSection()
    {
        UIElement body;
        if (_audit.Value.Count == 0) body = ServerSettings.EmptyRow(L.T("host.audit.empty"));
        else
        {
            var list = VStack(0, _audit.Value.Select(AuditRow));
            list.Align = CrossAlign.Stretch;
            body = Scroll(list).Do(s => s.MaxHeight = 320).Key("host.audit.scroll");
        }
        var refresh = new PPIconButton("refresh-cw", L.T("files.refresh"), () => _ = LoadLists(), size: M.IconButtonSmall, iconSize: M.IconSmall).Id("host.audit.refresh");
        return new PPSection(L.T("host.audit"), new PPCard(body, padding: M.Xs), accessory: refresh, collapsible: true, collapsed: true, stateKey: "settings.host.audit").Id("host.audit");
    }

    private static UIElement AuditRow(RemoteAuditEntry e)
    {
        var title = L.Has("host.audit.event." + e.Event) ? L.T("host.audit.event." + e.Event) : e.Event;
        var danger = e.Event is "login.fail" or "auth.fail" or "ban";
        var row = HStack(M.Md,
            Text(VCSDate.Full(e.Time), Typo.MonoSmall, ColorToken.TextTertiary).W(120).Shrink(false),
            Text(title, Typo.Small, danger ? ColorToken.Danger : ColorToken.TextPrimary).W(140).Shrink(false),
            Text(e.Who, Typo.Small).W(110).Shrink(false),
            Text(e.Ip, Typo.MonoSmall, ColorToken.TextSecondary).W(110).Shrink(false),
            TextMiddle(e.Detail, Typo.MonoSmall, ColorToken.TextSecondary).Flex());
        row.Margin = new Thickness(M.Md, 0, M.Md, 0);
        return row.H(M.RowHeightCompact);
    }
}

/// <summary>「生成密钥」「设置密码」对话框。</summary>
public static class HostDialogs
{
    /// <summary>生成密钥：设备名称 + 可选的连接地址 → remote.keyCreate → 只显示一次的连接码。</summary>
    public static void CreateKey(HostSection host)
    {
        var fields = new List<FormField>
        {
            new() { Key = "name", Title = L.T("host.keys.name"), Type = "string", Required = true, Placeholder = L.T("host.keys.name.placeholder") },
            new() { Key = "address", Title = L.T("host.keys.address"), Type = "string", Placeholder = L.T("server.add.address.placeholder"), Info = L.T("host.keys.address.info") },
        };
        Dialogs.Form(L.T("host.keys.add"), fields, async v =>
        {
            var name = v.TryGetValue("name", out var nv) ? nv.StringValue()?.Trim() ?? "" : "";
            var address = v.TryGetValue("address", out var av) ? av.StringValue()?.Trim() : null;
            var r = await AppModel.Shared.Client().RemoteKeyCreateAsync(name, address);
            await host.Load();
            Application.Current.Dispatcher.BeginInvoke(new Action(() => ShowCode(r.Code)), DispatcherPriority.Background);
        }, ok: L.T("host.keys.add"));
    }

    /// <summary>连接码只显示这一次（复制按钮；提示它等同于登录凭据）。</summary>
    public static void ShowCode(string code)
    {
        const string id = "connection-code";
        var close = new PPButton(L.T("common.close"), () => Dialogs.Dismiss(id)).Id("dialog.cancel");
        var copy = new PPButton(L.T("host.copy"), () => Actions.Copy(code, L.T("host.copied")), icon: "copy", kind: PPButtonKind.Primary).Id("host.code.copy");
        var col = VStack(M.Lg,
            new PPAlert("warning", L.T("host.keys.created.message")),
            Box(Selectable(code, Typo.Mono)).Pad(M.Md).Rounded(M.RadiusMD).Bg(ColorToken.BgCode).Id("host.code"));
        col.Align = CrossAlign.Stretch;
        Dialogs.Present(id, new PPDialog(L.T("host.keys.created"), col, new UIElement[] { close, copy }, M.DialogWidth, () => Dialogs.Dismiss(id))
        {
            DefaultButton = copy,
            CancelButton = close,
        });
    }

    /// <summary>添加账号（name 为 null）或给已有账号设置密码：至少 10 位、输两遍。</summary>
    public static void SetPassword(HostSection host, string? name)
    {
        var fields = new List<FormField>();
        if (name == null) fields.Add(new FormField { Key = "name", Title = L.T("host.users.name"), Type = "string", Required = true });
        fields.Add(new FormField { Key = "password", Title = L.T("host.users.password"), Type = "password", Required = true, Placeholder = L.T("host.users.password.rule") });
        fields.Add(new FormField { Key = "password2", Title = L.T("host.users.password2"), Type = "password", Required = true });
        var title = name == null ? L.T("host.users.add") : L.T("host.users.setPassword");
        Dialogs.Form(title, fields, async v =>
        {
            var user = name ?? (v.TryGetValue("name", out var nv) ? nv.StringValue()?.Trim() ?? "" : "");
            var p1 = v.TryGetValue("password", out var a) ? a.StringValue() ?? "" : "";
            var p2 = v.TryGetValue("password2", out var b) ? b.StringValue() ?? "" : "";
            if (p1.Length < 10) throw new InvalidOperationException(L.T("host.users.password.rule"));
            if (p1 != p2) throw new InvalidOperationException(L.T("host.users.password.mismatch"));
            await AppModel.Shared.Client().RemoteUserSetAsync(user, p1);
            await host.Load();
        }, message: name, ok: name == null ? L.T("host.users.add") : L.T("common.save"));
    }
}

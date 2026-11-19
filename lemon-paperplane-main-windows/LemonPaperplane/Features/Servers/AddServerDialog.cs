using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 添加服务器（docs/remote.md 5.2，对应 Mac AddServerDialog；也用于重新登录）：顶部分段「连接码 | 账号密码」。
/// 连接码：gateway.decode → 显示解析出的地址（可改）→ 先试这个地址，再依次试连接码里的其余地址 gateway.open；
/// 账号密码：gateway.probe → 不是 CA 证书时在对话框里「确认服务器证书」（和 SSH 第一次连接一样）→ gateway.login（device = 本机名）→ gateway.open。
/// 成功后记录进偏好、密钥进凭据管理器，并切到这台（已经是当前那台时用新密钥重新连接）。
/// </summary>
public sealed class AddServerDialog : PPView
{
    private const string ModeCode = "code";
    private const string ModePassword = "password";

    private readonly string _id;
    private readonly ServerRecord? _relogin;
    private readonly State<string> _mode;
    private readonly State<GatewayCode?> _decoded;
    private readonly State<bool> _decodeFailed;
    private readonly State<bool> _busy;
    private readonly State<string?> _error;
    /// <summary>需要用户确认的证书（账号密码登录、自签证书）</summary>
    private readonly State<GatewayProbe?> _pendingCert;
    private readonly State<bool> _certChanged;
    private readonly State<int> _inputs;
    // 输入值不订阅（打字时不重建对话框）；影响按钮、重复提示的变化经 _inputs 计数刷新
    private readonly State<string> _name = new("");
    private readonly State<string> _code = new("");
    private readonly State<string> _address = new("");
    private readonly State<string> _user = new("");
    private readonly State<string> _password = new("");
    private bool _nameEdited;
    private PPTextField? _nameField;
    private PPTextField? _addressField;
    private int _decodeGen;

    private AddServerDialog(string id, ServerRecord? relogin)
    {
        _id = id;
        _relogin = relogin;
        _mode = UseState(relogin != null ? ModePassword : ModeCode);
        _decoded = UseState<GatewayCode?>(null);
        _decodeFailed = UseState(false);
        _busy = UseState(false);
        _error = UseState<string?>(null);
        _pendingCert = UseState<GatewayProbe?>(null);
        _certChanged = UseState(false);
        _inputs = UseState(0);
        if (relogin != null)
        {
            _name.Value = relogin.Name;
            _nameEdited = true;
            _address.Value = relogin.Url;
        }
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public static void Present()
    {
        const string id = "server-add";
        Dialogs.Present(id, new AddServerDialog(id, null));
    }

    /// <summary>重新登录（换密钥）：连接码或账号密码，更新这条记录，不新增。</summary>
    public static void PresentRelogin(ServerRecord s)
    {
        var id = "server-relogin-" + s.Id;
        Dialogs.Present(id, new AddServerDialog(id, s));
    }

    private bool IsCode => _mode.Value == ModeCode;

    private static ApiClient? Local => AppModel.Shared.LocalApi;

    private void Close() => Dialogs.Dismiss(_id);

    private void Touched() => _inputs.Value++;

    // MARK: 界面

    protected override UIElement Build()
    {
        _ = _inputs.Value;
        if (_pendingCert.Value is { } probe)
        {
            var back = new PPButton(L.T("common.cancel"), () => _pendingCert.Value = null).Id("dialog.cancel");
            var trust = new PPButton(L.T("server.verify.trust"), () => _ = Login(probe), icon: "shield", kind: PPButtonKind.Primary, busy: _busy.Value).Id("server.verify.trust");
            return new PPDialog(Title, CertView(probe), new UIElement[] { back, trust }, M.DialogWidth, Close) { DefaultButton = trust, CancelButton = back };
        }
        var cancel = new PPButton(L.T("common.cancel"), Close).Id("dialog.cancel");
        var ok = new PPButton(_busy.Value ? L.T("server.add.connecting") : L.T("server.add.connect"), Submit, kind: PPButtonKind.Primary, busy: _busy.Value)
            .Disabled(!CanSubmit).Id("dialog.ok");
        return new PPDialog(Title, Form(), new UIElement[] { cancel, ok }, M.DialogWidth, Close) { DefaultButton = ok, CancelButton = cancel };
    }

    private string Title => _relogin == null ? L.T("server.add.title") : L.T("server.action.relogin");

    private static Stack Labeled(string title, UIElement control, string? info = null, bool top = false)
    {
        var l = HStack(M.Xs, Text(title), info != null ? new PPInfoButton(info) : null).W(96).Shrink(false);
        if (top) l.Margin = new Thickness(0, 6, 0, 0);
        var row = HStack(M.Md, l, control is FrameworkElement fe ? fe.Flex() : control);
        row.Align = top ? CrossAlign.Start : CrossAlign.Center;
        return row;
    }

    private UIElement Form()
    {
        var col = VStack(M.Lg);
        col.Align = CrossAlign.Stretch;
        var seg = new PPSegmented<string>(new[] { (ModeCode, L.T("server.add.mode.code")), (ModePassword, L.T("server.add.mode.password")) },
            Bind.Of(() => _mode.Value, v => { _mode.Value = v; _error.Value = null; })).Id("server.add.mode");
        seg.HorizontalAlignment = HorizontalAlignment.Left;
        col.Children.Add(seg);

        _nameField = Keep("name", () => new PPTextField(L.T("server.add.name.placeholder"), Bind.Of(() => _name.Value, v =>
        {
            _name.Value = v;
            _nameEdited = true;
        })).Id("server.add.name"));
        _addressField = Keep("address", () => new PPTextField(L.T("server.add.address.placeholder"), Bind.Of(() => _address.Value, v =>
        {
            _address.Value = v;
            Touched();
        }), mono: true, onSubmit: Submit).Id("server.add.address"));
        Detach(_nameField);
        Detach(_addressField);
        col.Children.Add(Labeled(L.T("server.add.name"), _nameField));
        if (IsCode)
        {
            var code = Keep("code", () => new PPTextArea(Bind.Of(() => _code.Value, v =>
            {
                _code.Value = v;
                _ = Decode();
            }), L.T("server.add.code.placeholder"), mono: true, minHeight: 64).Id("server.add.code"));
            Detach(code);
            var codeCol = VStack(M.Xs, code, _decodeFailed.Value ? Text(L.T("server.add.code.invalid"), Typo.Small, ColorToken.Danger) : null);
            codeCol.Align = CrossAlign.Stretch;
            col.Children.Add(Labeled(L.T("server.add.code"), codeCol, L.T("server.add.code.info"), top: true));
            if (_decoded.Value != null) col.Children.Add(Labeled(L.T("server.add.address"), _addressField, L.T("server.add.address.info")));
        }
        else
        {
            var user = Keep("user", () => new PPTextField("", Bind.Of(() => _user.Value, v => { _user.Value = v; Touched(); })).Id("server.add.user"));
            var password = Keep("password", () => new PPTextField("", Bind.Of(() => _password.Value, v => { _password.Value = v; Touched(); }),
                secure: true, onSubmit: Submit).Id("server.add.password"));
            Detach(user);
            Detach(password);
            col.Children.Add(Labeled(L.T("server.add.address"), _addressField, L.T("server.add.address.info")));
            col.Children.Add(Labeled(L.T("server.add.user"), user));
            col.Children.Add(Labeled(L.T("server.add.password"), password));
        }
        if (Duplicate is { } dup) col.Children.Add(new PPAlert("info", L.T("server.add.duplicate", dup.Name)));
        if (_error.Value is { } e) col.Children.Add(new PPAlert("danger", e));
        return col;
    }

    /// <summary>证书确认（和 SSH 第一次连接一样）。</summary>
    private UIElement CertView(GatewayProbe p)
    {
        var h = p.Hello;
        var fp = GroupedFingerprint(p.FingerprintDisplay.Length > 0 ? p.FingerprintDisplay : p.Fingerprint);
        var fpCol = VStack(M.Xs, Text(L.T("server.verify.fingerprint"), Typo.Label, ColorToken.TextSecondary),
            Box(Selectable(fp, Typo.Mono)).Pad(M.Md).Rounded(M.RadiusMD).Bg(ColorToken.BgCode).Id("server.verify.fingerprint"));
        fpCol.Align = CrossAlign.Stretch;
        var col = VStack(M.Lg,
            Text(L.T("server.verify.title"), Typo.BodyStrong),
            Paragraph(L.T("server.verify.message")),
            _certChanged.Value ? new PPAlert("warning", L.T("server.err.fingerprint")) : null,
            HStack(M.Sm, new PPIcon(ServerDisplay.Icon(h.Os), M.Icon, ColorToken.IconSecondary),
                TextMiddle(string.Join(" · ", new[] { h.Hostname, ServerDisplay.OsName(h.Os), Format.ShortURL(p.Url) }.Where(x => !string.IsNullOrEmpty(x))),
                    Typo.Body, ColorToken.TextSecondary)),
            fpCol,
            _error.Value is { } e ? new PPAlert("danger", e) : null);
        col.Align = CrossAlign.Stretch;
        return col;
    }

    /// <summary>16 组指纹分两行，方便逐组核对。</summary>
    public static string GroupedFingerprint(string s)
    {
        var g = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return g.Length <= 8 ? s : string.Join(" ", g.Take(8)) + "\n" + string.Join(" ", g.Skip(8));
    }

    // MARK: 校验

    private bool CanSubmit => IsCode
        ? _decoded.Value != null && _address.Value.Trim().Length > 0
        : _address.Value.Trim().Length > 0 && _user.Value.Trim().Length > 0 && _password.Value.Length > 0;

    /// <summary>已添加过的同一台服务器（重新登录时不提示）：连上后更新那条记录，不新增。</summary>
    private ServerRecord? Duplicate
    {
        get
        {
            if (_relogin != null) return null;
            var store = ServerStore.Shared;
            if (IsCode)
            {
                if (_decoded.Value is not { } d) return null;
                foreach (var u in new[] { _address.Value.Trim() }.Concat(d.Urls))
                    if (u.Length > 0 && store.Duplicate(Normalize(u), d.Fingerprint) is { } s) return s;
                return null;
            }
            var a = _address.Value.Trim();
            return a.Length == 0 ? null : store.Servers.FirstOrDefault(s => SameAddress(s.Url, a));
        }
    }

    /// <summary>地址比较：只写主机名 / IP 时按 https 与默认端口 47290 补全（同内核 NormalizeAddress）。</summary>
    private static bool SameAddress(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string a)
    {
        var s = a.Trim().TrimEnd('/');
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            s = "https://" + s;
            scheme = 5;
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return s;
        // 没写端口时用默认 47290（https 的 443 只有写明才算）
        var authority = s.Substring(scheme + 3);
        var slash = authority.IndexOf('/');
        if (slash >= 0) authority = authority.Substring(0, slash);
        var hasPort = authority.LastIndexOf(':') > authority.LastIndexOf(']');
        return u.Scheme + "://" + u.Host + ":" + (hasPort ? u.Port : 47290);
    }

    // MARK: 连接码

    private async Task Decode()
    {
        var gen = ++_decodeGen;
        var c = _code.Value.Trim();
        _error.Value = null;
        if (c.Length == 0)
        {
            _decoded.Value = null;
            _decodeFailed.Value = false;
            return;
        }
        if (Local is not { } local) return;
        try
        {
            var d = await local.GatewayDecodeAsync(c);
            if (gen != _decodeGen) return;
            _address.Value = d.Urls.FirstOrDefault() ?? "";
            _addressField?.Sync();
            if (!_nameEdited || _name.Value.Length == 0)
            {
                _name.Value = d.Name;
                _nameField?.Sync();
                _nameEdited = false;
            }
            _decoded.Value = d;
            _decodeFailed.Value = false;
        }
        catch
        {
            if (gen != _decodeGen) return;
            _decoded.Value = null;
            _decodeFailed.Value = true;
        }
    }

    // MARK: 连接

    private async void Submit()
    {
        if (!CanSubmit || _busy.Value) return;
        if (Local is not { } local)
        {
            _error.Value = L.T("server.localRequired");
            return;
        }
        _busy.Value = true;
        _error.Value = null;
        if (IsCode) await ConnectWithCode(local);
        else await Probe(local);
    }

    private async Task ConnectWithCode(ApiClient local)
    {
        if (_decoded.Value is not { } d)
        {
            _busy.Value = false;
            return;
        }
        // 先试（可能改过的）地址，再试连接码里的其余地址
        var urls = new List<string>();
        foreach (var u in new[] { _address.Value.Trim() }.Concat(d.Urls))
            if (u.Length > 0 && !urls.Any(x => SameAddress(x, u))) urls.Add(u);
        Exception? last = null;
        foreach (var u in urls)
        {
            try
            {
                var gw = await local.GatewayOpenAsync(u, d.Fingerprint, false, d.Token);
                Finish(Normalize(u), d.Fingerprint, false, d.Token, gw.Info, d.Name);
                return;
            }
            catch (Exception e)
            {
                last = e;
                // 密钥、指纹、版本的问题换地址也没用
                if (e is ApiException a && a.Code != RemoteErrorCode.Unreachable && a.Code != ApiException.CodeNetwork && a.Code != RemoteErrorCode.BadRequest) break;
            }
        }
        _error.Value = last != null ? ServerDisplay.ErrorMessage(last) : L.T("server.add.code.invalid");
        _busy.Value = false;
    }

    private async Task Probe(ApiClient local)
    {
        try
        {
            var p = await local.GatewayProbeAsync(_address.Value.Trim());
            if (!p.Hello.PasswordLogin)
            {
                _error.Value = L.T("server.err.passwordDisabled");
                _busy.Value = false;
                return;
            }
            if (!p.Trusted)
            {
                // 重新登录且指纹没变：不用再确认
                var known = _relogin is { } r && r.Fingerprint.Length > 0 && string.Equals(r.Fingerprint, p.Fingerprint, StringComparison.OrdinalIgnoreCase);
                if (!known)
                {
                    _certChanged.Value = _relogin is { Fingerprint.Length: > 0 };
                    _pendingCert.Value = p;
                    _busy.Value = false;
                    return;
                }
            }
            await Login(p);
        }
        catch (Exception e)
        {
            _error.Value = ServerDisplay.ErrorMessage(e);
            _busy.Value = false;
        }
    }

    private async Task Login(GatewayProbe p)
    {
        if (Local is not { } local) return;
        _busy.Value = true;
        _error.Value = null;
        try
        {
            var token = await local.GatewayLoginAsync(p.Url, p.Fingerprint, p.Trusted, _user.Value.Trim(), _password.Value, DeviceName());
            var gw = await local.GatewayOpenAsync(p.Url, p.Fingerprint, p.Trusted, token);
            _password.Value = "";
            Finish(p.Url, p.Fingerprint, p.Trusted, token, gw.Info, p.Hello.Name.Length > 0 ? p.Hello.Name : p.Hello.Hostname);
        }
        catch (Exception e)
        {
            _error.Value = ServerDisplay.ErrorMessage(e);
            // 账号密码错：回到表单改
            if (_pendingCert.Value != null && e is ApiException { Code: RemoteErrorCode.Unauthorized or RemoteErrorCode.RateLimited }) _pendingCert.Value = null;
            _busy.Value = false;
        }
    }

    /// <summary>本机名（账号密码登录时告诉服务器这是哪台设备）。</summary>
    private static string DeviceName()
    {
        try { return Environment.MachineName; }
        catch { return "Windows"; }
    }

    /// <summary>保存并切到这台（已经是当前那台时用新密钥重新连接）。</summary>
    private void Finish(string url, string fingerprint, bool trusted, string token, SystemInfo info, string fallbackName)
    {
        var store = ServerStore.Shared;
        var existing = (_relogin != null ? store.Find(_relogin.Id) : null) ?? Duplicate;
        var n = _name.Value.Trim();
        var name = n.Length > 0 ? n
            : existing is { Name.Length: > 0 } ? existing.Name
            : fallbackName.Length > 0 ? fallbackName
            : info.Hostname.Length > 0 ? info.Hostname : url;
        var s = (existing ?? new ServerRecord { Id = ServerStore.NewId() }) with
        {
            Name = name,
            Url = url,
            Fingerprint = fingerprint,
            Trusted = trusted,
            Os = info.Os.Length > 0 ? info.Os : existing?.Os ?? "",
            Hostname = info.Hostname.Length > 0 ? info.Hostname : existing?.Hostname ?? "",
        };
        // 先存密钥（凭据管理器写不进去时不留下没有密钥的记录）
        try { store.SetSecret(s.Id, token, s.Name); }
        catch (Exception e)
        {
            _error.Value = L.T("server.err.keychain") + "\n" + e.Message;
            _busy.Value = false;
            return;
        }
        store.Put(s);
        _busy.Value = false;
        Close();
        var app = AppModel.Shared;
        // 对话框关掉后再切换（切换会清掉全部对话框）
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (app.Server?.Id == s.Id) app.ServerRelogged(s);
            else app.SwitchTo(s);
        }), DispatcherPriority.Background);
    }
}

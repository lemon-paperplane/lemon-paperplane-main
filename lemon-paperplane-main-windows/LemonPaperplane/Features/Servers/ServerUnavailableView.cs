using System.Windows;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 连不上远程服务器（docs/remote.md 5.1）：「连不上 X」+ 原因 + 重试 / 切回本机；
/// 密钥失效或证书变了时多一个「重新登录」。经网关的 403 remote_unauthorized 走这里，不会去重启本机内核。
/// </summary>
public sealed class ServerUnavailableView : PPView
{
    private readonly ConnectionState _state;

    public ServerUnavailableView(ConnectionState state) => _state = state;

    protected override UIElement Build()
    {
        var model = AppModel.Shared;
        var server = model.Server;
        var name = model.ServerName;
        var local = _state.Code == ConnectionState.CodeLocal;
        // 密钥失效、证书变了：重新登录是主按钮；其他情况重试是主按钮
        var login = server != null && ServerDisplay.NeedsLogin(_state.Code);
        var buttons = HStack(M.Md,
            server != null && !local
                ? new PPButton(L.T("server.action.relogin"), () => AddServerDialog.PresentRelogin(server), icon: "key-round",
                    kind: login ? PPButtonKind.Primary : PPButtonKind.Secondary).Id("server.relogin")
                : null,
            new PPButton(L.T("server.retry"), () => model.Connect(), icon: "refresh-cw", kind: login ? PPButtonKind.Secondary : PPButtonKind.Primary).Id("server.retry"),
            new PPButton(L.T("server.backToLocal"), () => model.SwitchTo(null), icon: "monitor").Id("server.backToLocal"));
        var col = VStack(M.Lg,
            new PPIcon(local ? "plug" : "cloud-off", M.IconEmpty, ColorToken.IconSecondary),
            Text(L.T("server.connectFailed", name), Typo.Title),
            server != null && !local ? Text(server.Address, Typo.Mono, ColorToken.TextTertiary) : null,
            Selectable(_state.Message ?? "", Typo.Small, ColorToken.TextSecondary).Do(t => { t.TextAlignment = TextAlignment.Center; t.MaxWidth = 480; }),
            buttons.Margins(0, M.Sm, 0, 0));
        col.Align = CrossAlign.Center;
        col.HorizontalAlignment = HorizontalAlignment.Center;
        col.VerticalAlignment = VerticalAlignment.Center;
        return col;
    }
}

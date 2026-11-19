using System.Linq;
using System.Windows;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 主窗口内容（对应 Mac MainView）：把工具栏、侧栏、内容岛装进 MainWindow 的插槽。
/// 窗口本身（自绘标题栏、对话框层、提示层）在 App/MainWindow.cs。
/// </summary>
public static class MainView
{
    private static bool _localeHooked;

    public static void Install(MainWindow w)
    {
        w.ToolbarLeft = new MainToolbarLeft(w);
        w.ToolbarRight = new MainToolbarRight();
        w.Sidebar = new Sidebar();
        w.Page = new MainContent();
        // 切换语言：整套重装（跨重建复用的元素也换成新语言，和 Mac 一样状态都在 store 里）
        if (!_localeHooked)
        {
            _localeHooked = true;
            L10n.Shared.Changed += (_, _) => w.Dispatcher.BeginInvoke(new System.Action(() => Install(w)));
        }
    }
}

/// <summary>内容岛：连接中 / 连接失败 / 页面。页面路由单独一个视图，连接状态变化时不重建页面。</summary>
public sealed class MainContent : PPView
{
    private readonly AppModel _model = AppModel.Shared;

    public MainContent()
    {
        Observe(_model, nameof(AppModel.Connection));
        Observe(_model.Data, nameof(DataStore.Loaded));
    }

    protected override UIElement Build()
    {
        var c = _model.Connection;
        if (c.IsFailed && c.Remote) return new ServerUnavailableView(c);
        if (c.IsFailed) return new ConnectionFailedView(c.Message ?? "");
        if (c.Kind == ConnectionState.Connecting.Kind && !_model.Data.Loaded)
        {
            var col = VStack(M.Lg, new PPSpinner(M.IconLarge), Text(L.T("connection.connecting"), Typo.Body, ColorToken.TextSecondary));
            col.Align = CrossAlign.Center;
            col.HorizontalAlignment = HorizontalAlignment.Center;
            col.VerticalAlignment = VerticalAlignment.Center;
            return col;
        }
        return Keep("router", () => new PageRouter());
    }
}

/// <summary>按导航选择显示页面；工程详情盖在工作区 / 运行中页面之上（对应 Mac PageRouter）。</summary>
public sealed class PageRouter : PPView
{
    private readonly NavStore _nav = AppModel.Shared.Nav;
    private readonly DataStore _data = AppModel.Shared.Data;

    public PageRouter()
    {
        Observe(_nav, nameof(NavStore.Page), nameof(NavStore.OpenProjectId));
        Observe(_data, nameof(DataStore.Contributions));
    }

    protected override UIElement Build()
    {
        if (_nav.OpenProjectId is { } pid) return Keep("project:" + pid, () => new ProjectDetailPage(pid));
        var page = _nav.Page;
        switch (page.Kind)
        {
            case NavPage.KindWorkspace:
                return Keep("ws:" + page.Id, () => new WorkspacePage(page.Id));
            case NavPage.KindToolchains:
                return Keep("toolchains", () => new ToolchainsPage());
            case NavPage.KindPlugins:
                return Keep("plugins", () => new PluginsPage());
            case NavPage.KindSettings:
                return Keep("settings", () => new SettingsPage());
            case NavPage.KindPluginView:
            {
                var v = _data.Contributions.Views.FirstOrDefault(x => x.Key == page.Id);
                if (v == null) return new PPEmptyState("puzzle", L.T("plugin.viewMissing"));
                var col = VStack(0, new PPPageHeader(v.Title), new PPDivider(subtle: true),
                    Keep("pv:" + v.Key, () => new PluginViewHost(v, new ViewContext { Plugin = v.Plugin, View = v.Id })).Flex());
                col.Align = CrossAlign.Stretch;
                return col;
            }
            default:
                return Keep("running", () => new WorkspacePage(null));
        }
    }
}

/// <summary>连不上内核。</summary>
public sealed class ConnectionFailedView : PPView
{
    private readonly string _message;

    public ConnectionFailedView(string message) => _message = message;

    protected override UIElement Build()
    {
        var col = VStack(M.Lg,
            new PPIcon("plug", M.IconEmpty, ColorToken.IconSecondary),
            Text(L.T("connection.failed"), Typo.Title),
            Selectable(_message, Typo.Small, ColorToken.TextSecondary).W(480),
            new PPButton(L.T("connection.retry"), () => AppModel.Shared.Connect(), icon: "refresh-cw", kind: PPButtonKind.Primary).Id("connection.retry"));
        col.Align = CrossAlign.Center;
        col.HorizontalAlignment = HorizontalAlignment.Center;
        col.VerticalAlignment = VerticalAlignment.Center;
        return col;
    }
}

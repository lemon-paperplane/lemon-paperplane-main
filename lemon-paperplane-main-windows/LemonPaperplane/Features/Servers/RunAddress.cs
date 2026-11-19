using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 运行中服务的地址链接（docs/remote.md 5.4）。本机：一个链接直接打开。
/// 连着远程服务器、地址是服务器本机（localhost / 127.0.0.1 / 0.0.0.0）时，按运行配置的 remoteLinks 显示两个：
/// 「⇄ localhost:本机端口」（先在本机内核上 gateway.forward 再打开）和「服务器主机:端口」（把 localhost 换成服务器地址的主机）。
/// </summary>
public static class RunAddress
{
    /// <summary>地址链接。configId 用来取工程摘要里的 remoteLinks；localIcon 是本机时链接前的图标（如 external-link）。</summary>
    public static FrameworkElement Links(string url, string? projectId, string? configId, string? localIcon = null, double spacing = -1)
    {
        var app = AppModel.Shared;
        if (!app.IsRemote || app.Server is not { } server || !TryLocal(url, out var port))
            return new PPLink(Format.ShortURL(url), () => Actions.OpenURL(url), mono: true, icon: localIcon).Shrink();
        var links = Options(projectId, configId);
        var row = HStack(spacing >= 0 ? spacing : M.Md);
        if (links.Forward)
            // 转发地址短（localhost:端口）：不压缩，空间不够时先压服务器地址
            row.Children.Add(new PPLink("localhost:" + port + PathSuffix(url), () => app.OpenForwarded(url, port), mono: true, icon: "arrow-left-right")
                .Tip(L.T("remote.link.forward")).Shrink(false));
        if (links.Direct)
        {
            var direct = WithPort(url, server.Host, port);
            // 服务器地址放不下就整个让出来（不裁成半截），转发地址留着
            row.Children.Add(new HideWhenTight(new PPLink(Format.ShortURL(direct), () => Actions.OpenURL(direct), mono: true, icon: "server")
                .Tip(L.T("remote.link.direct"))).Shrink());
        }
        return row.Shrink();
    }

    /// <summary>按钮样式（工程详情页头部）：本机一个按钮；远程时按 remoteLinks 一到两个。</summary>
    public static FrameworkElement Buttons(string url, string? projectId, string? configId)
    {
        var app = AppModel.Shared;
        if (!app.IsRemote || app.Server is not { } server || !TryLocal(url, out var port))
            return new PPButton(Format.ShortURL(url), () => Actions.OpenURL(url), icon: "external-link", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("project.url");
        var links = Options(projectId, configId);
        var row = HStack(M.Xs);
        if (links.Forward)
            row.Children.Add(new PPButton("localhost:" + port + PathSuffix(url), () => app.OpenForwarded(url, port), icon: "arrow-left-right",
                kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Tip(L.T("remote.link.forward")).Id("project.url.forward"));
        if (links.Direct)
        {
            var direct = WithPort(url, server.Host, port);
            row.Children.Add(new HideWhenTight(new PPButton(Format.ShortURL(direct), () => Actions.OpenURL(direct), icon: "server",
                kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Tip(L.T("remote.link.direct")).Id("project.url.direct")).Shrink());
        }
        return row;
    }

    /// <summary>远程访问方式（没有记录时两种都要）。</summary>
    public static RemoteLinks Options(string? projectId, string? configId)
    {
        if (projectId == null || configId == null) return new RemoteLinks();
        return AppModel.Shared.Data.Project(projectId)?.LinksOf(configId) ?? new RemoteLinks();
    }

    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
        { "localhost", "127.0.0.1", "0.0.0.0", "::", "::1", "[::]", "[::1]" };

    /// <summary>地址是不是服务器本机（localhost 等），是的话给出端口。</summary>
    public static bool TryLocal(string url, out int port)
    {
        port = 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (!LoopbackHosts.Contains(u.Host) && !u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        port = u.Port;
        return port > 0;
    }

    /// <summary>换掉地址的主机和端口（保留协议、路径、查询）。</summary>
    public static string WithPort(string url, string host, int port)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var h = host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal) ? "[" + host + "]" : host;
        var b = new UriBuilder(u) { Host = h.Trim('[', ']'), Port = port };
        var s = b.Uri.AbsoluteUri;
        // 原地址没有路径时不要多出末尾的 /
        return u.AbsolutePath == "/" && !url.EndsWith("/", StringComparison.Ordinal) && s.EndsWith("/", StringComparison.Ordinal) ? s.Substring(0, s.Length - 1) : s;
    }

    /// <summary>地址里端口后面的路径（显示用，去掉末尾的 /）。</summary>
    private static string PathSuffix(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.PathAndQuery + u.Fragment).TrimEnd('/') : "";
}

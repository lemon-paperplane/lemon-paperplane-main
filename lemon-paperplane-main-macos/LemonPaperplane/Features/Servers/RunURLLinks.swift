import PaperplaneKit
import SwiftUI

/// 运行中服务的地址。本机：一个链接。连着远程服务器时按运行配置的 remoteLinks 显示两个：
/// ⇄ localhost:端口（先 gateway.forward 把服务器端口转到本机再打开）和 服务器主机:端口（直接访问服务器）。
struct RunURLLinks: View {
    enum Style { case link, button }

    let url: String
    let projectId: String
    let configId: String
    var icon: String? = "external-link"
    var style: Style = .link

    @ObservedObject private var servers = AppModel.shared.servers
    @ObservedObject private var data = AppModel.shared.data

    var body: some View {
        if servers.isRemote, let port = ServerURL.loopbackPort(url), let server = servers.current {
            let links = data.projects[projectId]?.remoteLinks(for: configId) ?? .both
            HStack(spacing: M.md) {
                if links.forward {
                    item(title: "localhost:\(port)", icon: "arrow-left-right", help: tr("remote.link.forward")) {
                        RemoteLinkActions.openForwarded(url)
                    }
                }
                if links.direct {
                    let direct = ServerURL.direct(url, serverHost: server.host)
                    item(title: "\(server.host):\(port)", icon: "server", help: tr("remote.link.direct")) {
                        Actions.openURL(direct)
                    }
                }
            }
        } else {
            item(title: Format.shortURL(url), icon: icon, help: nil) { Actions.openURL(url) }
        }
    }

    @ViewBuilder
    private func item(title: String, icon: String?, help: String?, action: @escaping () -> Void) -> some View {
        switch style {
        case .link:
            PPLink(title: title, mono: true, icon: icon, action: action).help(help ?? "")
        case .button:
            PPButton(title, icon: icon, kind: .ghost, size: .small, action: action).help(help ?? "")
        }
    }
}

@MainActor
enum RemoteLinkActions {
    /// 把服务器 localhost:端口 转到本机（优先同一个端口号）再打开
    static func openForwarded(_ url: String) {
        let model = AppModel.shared
        guard let port = ServerURL.loopbackPort(url), let gid = model.servers.gatewayId, let local = model.localAPI else {
            Actions.openURL(url)
            return
        }
        Task {
            do {
                let f = try await local.gatewayForward(id: gid, port: port)
                Actions.openURL(ServerURL.forwarded(url, localPort: f.localPort))
            } catch {
                model.toasts.error(error, title: tr("remote.forward.failed"))
            }
        }
    }
}

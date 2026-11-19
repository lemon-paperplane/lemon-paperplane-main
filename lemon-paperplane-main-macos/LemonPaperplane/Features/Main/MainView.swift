import PaperplaneKit
import SwiftUI

/// 主窗口：顶部工具栏（40px，横跨整个窗口）+ 左侧栏 + 内容区（圆角岛）+ 对话框层 + 提示层。
struct MainView: View {
    @ObservedObject private var model = AppModel.shared
    @ObservedObject private var nav = AppModel.shared.nav
    @ObservedObject private var servers = AppModel.shared.servers
    @Environment(\.theme) private var theme

    var body: some View {
        ZStack {
            VStack(spacing: 0) {
                MainToolbar()
                HStack(spacing: 0) {
                    if nav.sidebarVisible {
                        Sidebar()
                            .frame(width: nav.sidebarWidth)
                        SidebarResizer(width: $nav.sidebarWidth)
                    }
                    content
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .background(theme.bgContent)
                        .clipShape(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous))
                        .overlay(RoundedRectangle(cornerRadius: M.radiusLG, style: .continuous).strokeBorder(theme.border, lineWidth: 1))
                        .padding(.trailing, M.sm)
                        .padding(.bottom, M.sm)
                        .padding(.leading, nav.sidebarVisible ? 0 : M.sm)
                }
            }
            .background(theme.bgChrome)
            ToastHost(store: model.toasts)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomTrailing)
            DialogHost(store: model.dialogs)
        }
        .frame(minWidth: 760, minHeight: 480)
        .ignoresSafeArea()
    }

    @ViewBuilder private var content: some View {
        switch model.connection {
        case .failed(let msg):
            ConnectionFailedView(message: msg)
        case .connecting where !model.data.loaded:
            connecting(tr("connection.connecting"))
        default:
            if servers.isRemote {
                switch servers.phase {
                case .connecting:
                    connecting(tr("server.status.connecting"))
                case .failed(let f):
                    ServerFailedView(failure: f)
                case .ready:
                    PageRouter()
                }
            } else {
                PageRouter()
            }
        }
    }

    private func connecting(_ text: String) -> some View {
        VStack(spacing: M.lg) {
            PPSpinner(size: M.iconLarge)
            Text(text).foregroundColor(theme.textSecondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// 侧栏宽度拖动条
private struct SidebarResizer: View {
    @Binding var width: CGFloat
    @State private var start: CGFloat?

    var body: some View {
        Color.clear
            .frame(width: M.sm)
            .contentShape(Rectangle())
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .gesture(DragGesture(minimumDistance: 1).onChanged { v in
                if start == nil { start = width }
                width = min(max((start ?? width) + v.translation.width, M.sidebarMinWidth), M.sidebarMaxWidth)
            }.onEnded { _ in start = nil })
    }
}

/// 按导航选择显示页面；工程详情盖在工作区/运行中页面之上
struct PageRouter: View {
    @ObservedObject private var nav = AppModel.shared.nav
    @ObservedObject private var data = AppModel.shared.data

    var body: some View {
        Group {
            if let pid = nav.openProjectId {
                ProjectDetailPage(projectId: pid)
                    .id(pid)
            } else {
                switch nav.page {
                case .workspace(let id):
                    WorkspacePage(workspaceId: id).id(id)
                case .running:
                    WorkspacePage(workspaceId: nil)
                case .toolchains:
                    ToolchainsPage()
                case .plugins:
                    PluginsPage()
                case .settings:
                    SettingsPage()
                case .pluginView(let key):
                    if let v = data.contributions.views.first(where: { $0.key == key }) {
                        VStack(spacing: 0) {
                            PPPageHeader(title: v.title) { EmptyView() }
                            PPDivider(subtle: true)
                            PluginViewHost(view: v, context: ViewContext(plugin: v.plugin, view: v.id))
                        }
                        .id(key)
                    } else {
                        PPEmptyState(icon: "puzzle", title: tr("plugin.viewMissing"))
                    }
                }
            }
        }
    }
}

private struct ConnectionFailedView: View {
    let message: String
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(spacing: M.lg) {
            PPIcon("plug", size: M.iconEmpty, color: theme.iconSecondary)
            Text(tr("connection.failed")).font(.pp(.title))
            Text(message).font(.pp(.small)).foregroundColor(theme.textSecondary)
                .multilineTextAlignment(.center).frame(maxWidth: 480).textSelection(.enabled)
            PPButton(tr("connection.retry"), icon: "refresh-cw", kind: .primary) { AppModel.shared.connect() }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// 连不上远程服务器：原因 + 重试 / 重新登录 / 切回本机
private struct ServerFailedView: View {
    let failure: ServerFailure
    @ObservedObject private var servers = AppModel.shared.servers
    @Environment(\.theme) private var theme

    var body: some View {
        let local = failure.kind == .localDown
        // 密钥失效、证书变了：重新登录是主按钮；其他情况重试是主按钮（与 Windows 版一致）
        let login = failure.needsLogin
        VStack(spacing: M.lg) {
            PPIcon(local ? "plug" : "cloud-off", size: M.iconEmpty, color: theme.iconSecondary)
            Text(tr("server.connectFailed", servers.currentName)).font(.pp(.title))
            if let s = servers.current, !local {
                Text(s.displayAddress).font(.pp(.mono)).foregroundColor(theme.textTertiary).textSelection(.enabled)
            }
            Text(failure.text).font(.pp(.small)).foregroundColor(theme.textSecondary)
                .multilineTextAlignment(.center).frame(maxWidth: 480).textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: M.md) {
                if let s = servers.current, !local {
                    PPButton(tr("server.action.relogin"), icon: "key-round", kind: login ? .primary : .secondary) {
                        AddServerDialog.present(relogin: s)
                    }
                }
                PPButton(tr("server.retry"), icon: "refresh-cw", kind: login ? .secondary : .primary) { AppModel.shared.reconnectServer() }
                PPButton(tr("server.backToLocal"), icon: "monitor") { AppModel.shared.switchServer(to: nil) }
            }
            .padding(.top, M.sm)
        }
        .padding(M.huge)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

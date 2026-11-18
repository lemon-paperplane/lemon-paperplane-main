import PaperplaneKit
import SwiftUI

/// 主窗口：顶部工具栏（40px，横跨整个窗口）+ 左侧栏 + 内容区（圆角岛）+ 对话框层 + 提示层。
struct MainView: View {
    @ObservedObject private var model = AppModel.shared
    @ObservedObject private var nav = AppModel.shared.nav
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
            VStack(spacing: M.lg) {
                PPSpinner(size: M.iconLarge)
                Text(tr("connection.connecting")).foregroundColor(theme.textSecondary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        default:
            PageRouter()
        }
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

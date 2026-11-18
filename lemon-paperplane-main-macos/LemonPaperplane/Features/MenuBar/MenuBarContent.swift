import AppKit
import PaperplaneKit
import SwiftUI

/// 菜单栏窗口：按工程列出运行中的服务（状态、地址、停止），全部停止，打开主窗口，退出。
struct MenuBarContent: View {
    @ObservedObject private var model = AppModel.shared
    @ObservedObject private var runs = AppModel.shared.runs
    @ObservedObject private var data = AppModel.shared.data
    @Environment(\.theme) private var theme

    var body: some View {
        let active = runs.activeRuns
        let groups = Dictionary(grouping: active, by: \.projectId)
        let order = data.allProjects.map(\.id).filter { groups[$0] != nil } + groups.keys.filter { data.projects[$0] == nil }
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: M.sm) {
                Text(tr("app.name")).font(.pp(.title))
                Spacer()
                connectionBadge
            }
            .padding(.horizontal, M.lg)
            .frame(height: 40)
            PPDivider(subtle: true)
            if active.isEmpty {
                HStack(spacing: M.sm) {
                    PPIcon("activity", size: M.icon, color: theme.iconSecondary)
                    Text(tr("runs.none")).foregroundColor(theme.textSecondary)
                }
                .padding(M.lg)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: M.xs) {
                        ForEach(order, id: \.self) { pid in
                            Text(data.projects[pid]?.name ?? pid).font(.pp(.caption)).foregroundColor(theme.textTertiary)
                                .padding(.horizontal, M.md).padding(.top, M.sm)
                            ForEach(groups[pid] ?? []) { r in MenuBarRunRow(run: r) }
                        }
                    }
                    .padding(M.xs)
                }
                .frame(maxHeight: 420)
            }
            PPDivider(subtle: true)
            VStack(spacing: 0) {
                if !active.isEmpty {
                    MenuBarAction(title: tr("menu.stopAll"), icon: "square") { Actions.stopAll() }
                }
                MenuBarAction(title: tr("menubar.openWindow"), icon: "app-window") {
                    MenuBarWindow.dismiss()
                    AppDelegate.shared?.showMainWindow()
                }
                PPDivider(subtle: true).padding(.vertical, M.xs)
                MenuBarAction(title: tr("menu.quitUI"), icon: "log-out") { model.quit(stopServices: false) }
                MenuBarAction(title: tr("menu.quitAll"), icon: "power", destructive: true) { model.quit(stopServices: true) }
            }
            .padding(M.xs)
        }
        .frame(width: 340)
        .background(theme.bgElevated)
    }

    @ViewBuilder private var connectionBadge: some View {
        switch model.connection {
        case .connected: EmptyView()
        case .connecting, .reconnecting: PPSpinner(size: M.iconSmall)
        case .failed: PPBadge(text: tr("connection.failed"), tone: "danger")
        }
    }
}

private struct MenuBarRunRow: View {
    let run: Run
    @Environment(\.theme) private var theme

    var body: some View {
        PPRow(height: nil, action: {
            MenuBarWindow.dismiss()
            AppDelegate.shared?.showMainWindow()
            AppModel.shared.nav.openRunConsole(projectId: run.projectId, configId: run.configId)
        }) {
            HStack(spacing: M.sm) {
                StatusDot(state: run.state)
                VStack(alignment: .leading, spacing: 1) {
                    Text(run.name).lineLimit(1)
                    if let u = run.urls.first {
                        PPLink(title: Format.shortURL(u), mono: true) { Actions.openURL(u) }
                    } else {
                        Text(RunStatusStyle.title(run.state)).font(.pp(.small)).foregroundColor(theme.textTertiary)
                    }
                }
                Spacer(minLength: M.sm)
                RunResourceText(runId: run.id, showCPU: false, fallback: (run.cpu, run.rssMB))
                PPIconButton("square", help: tr("action.stop"), size: M.iconButtonSmall, tint: theme.danger) { Actions.stop(runId: run.id) }
            }
            .padding(.vertical, M.xs)
        }
    }
}

private struct MenuBarAction: View {
    let title: String
    let icon: String
    var destructive = false
    let action: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        PPRow(action: action) {
            HStack(spacing: M.md) {
                PPIcon(icon, size: M.icon, color: destructive ? theme.danger : theme.iconPrimary)
                Text(title).foregroundColor(destructive ? theme.danger : theme.textPrimary)
                Spacer()
            }
        }
    }
}

/// 关闭菜单栏弹出窗口（SwiftUI 没有公开 API，按窗口类名找）
@MainActor
enum MenuBarWindow {
    static func dismiss() {
        for w in NSApp.windows where String(describing: type(of: w)).contains("MenuBarExtra") && w.isVisible {
            w.orderOut(nil)
        }
    }
}

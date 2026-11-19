import PaperplaneKit
import SwiftUI

/// 窗口左上角的服务器切换器：系统图标 + 当前服务器名 + ⌄，点开列出本机与已添加的服务器。
struct ServerSwitcher: View {
    @ObservedObject private var servers = AppModel.shared.servers
    @ObservedObject private var model = AppModel.shared
    @State private var open = false
    @State private var hover = false
    @Environment(\.theme) private var theme

    var body: some View {
        Button { open.toggle() } label: {
            HStack(spacing: M.sm) {
                PPIcon(ServerStore.osIcon(currentOS), size: M.icon, color: theme.iconSecondary)
                Text(servers.currentName).font(.pp(.title)).lineLimit(1)
                PPIcon("chevron-down", size: 12, color: theme.iconSecondary)
            }
            .padding(.horizontal, M.sm)
            .frame(height: M.iconButton)
            .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(open ? theme.bgPressed : (hover ? theme.bgHover : .clear)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(tr("server.switcher.help"))
        .accessibilityLabel(tr("server.switcher.help"))
        .ppPopover(isPresented: $open) { ServerSwitcherPopover { open = false } }
    }

    private var currentOS: String {
        if let s = servers.current { return model.systemInfo?.os ?? s.os }
        return model.localInfo?.os ?? "darwin"
    }
}

/// 切换器弹出面板：本机永远第一行，当前那台打勾；底部「添加服务器…」「管理服务器…」
struct ServerSwitcherPopover: View {
    var close: () -> Void
    @ObservedObject private var servers = AppModel.shared.servers
    @ObservedObject private var model = AppModel.shared
    @ObservedObject private var runs = AppModel.shared.runs
    /// 连着远程时，本机的运行中数量单独问一次本机内核
    @State private var localRuns: Int?
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(spacing: 0) {
                localRow
                ForEach(servers.servers) { s in row(for: s) }
            }
            .padding(M.xs)
            PPDivider(subtle: true)
            VStack(spacing: 0) {
                footerRow(tr("server.add"), icon: "plus") {
                    close()
                    AddServerDialog.present()
                }
                footerRow(tr("server.manage"), icon: "settings") {
                    close()
                    AppDelegate.shared?.showMainWindow()
                    model.nav.page = .settings
                    model.nav.settingsAnchor = SettingsAnchor.servers
                }
            }
            .padding(M.xs)
        }
        .frame(width: 340)
        .task {
            guard servers.isRemote, let local = model.localAPI else { return }
            localRuns = (try? await local.runs(active: true))?.count
        }
    }

    private var localRow: some View {
        let current = !servers.isRemote
        let info = model.localInfo
        let sub = info.map { hostLine($0.hostname, $0.os) } ?? statusText(current: current)
        let dot = current ? currentDot : (info != nil ? theme.statusRunning : theme.statusIdle)
        return ServerRow(icon: ServerStore.osIcon(info?.os ?? "darwin"), name: tr("server.local"), detail: sub,
                         runs: current ? runs.activeRuns.count : localRuns, dot: dot, current: current) {
            close()
            model.switchServer(to: nil)
        }
    }

    private func row(for s: SavedServer) -> some View {
        let current = servers.currentId == s.id
        let sub: String
        if current, servers.phase == .ready, let info = model.systemInfo {
            sub = hostLine(info.hostname, info.os)
        } else {
            sub = s.displayAddress + " · " + statusText(current: current)
        }
        return ServerRow(icon: ServerStore.osIcon(current ? (model.systemInfo?.os ?? s.os) : s.os), name: s.name, detail: sub,
                         runs: current ? runs.activeRuns.count : nil, dot: current ? currentDot : theme.statusIdle, current: current) {
            close()
            model.switchServer(to: s.id)
        }
    }

    /// 「主机名 · 系统」
    private func hostLine(_ hostname: String, _ os: String) -> String {
        [hostname, ServerStore.osName(os)].filter { !$0.isEmpty }.joined(separator: " · ")
    }

    private func statusText(current: Bool) -> String {
        guard current else { return tr("server.status.disconnected") }
        switch servers.phase {
        case .ready: return tr("server.status.connected")
        case .connecting: return tr("server.status.connecting")
        case .failed: return tr("server.status.failed")
        }
    }

    private var currentDot: Color {
        switch servers.phase {
        case .ready: return model.connection == .connected ? theme.statusRunning : theme.warning
        case .connecting: return theme.warning
        case .failed: return theme.statusFailed
        }
    }

    private func footerRow(_ title: String, icon: String, action: @escaping () -> Void) -> some View {
        PPRow(action: action) {
            HStack(spacing: M.md) {
                PPIcon(icon, size: M.icon, color: theme.iconPrimary)
                Text(title)
                Spacer()
            }
        }
    }
}

/// 一台服务器：系统图标、名称与第二行、运行中数量、状态点、当前那台打勾
private struct ServerRow: View {
    let icon: String
    let name: String
    let detail: String
    let runs: Int?
    let dot: Color
    let current: Bool
    let action: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        PPRow(height: nil, action: action) {
            HStack(spacing: M.md) {
                PPIcon(icon, size: M.icon, color: theme.iconSecondary)
                VStack(alignment: .leading, spacing: 1) {
                    Text(name).lineLimit(1)
                    if !detail.isEmpty {
                        Text(detail).font(.pp(.small)).foregroundColor(theme.textSecondary).lineLimit(1).truncationMode(.middle)
                    }
                }
                Spacer(minLength: M.md)
                HStack(spacing: M.sm) {
                    if let runs, runs > 0 {
                        HStack(spacing: M.xxs) {
                            PPIcon("activity", size: M.iconSmall, color: theme.statusRunning)
                            Text("\(runs)").font(.pp(.small).monospacedDigit()).foregroundColor(theme.textSecondary)
                        }
                        .help(tr("toolbar.runs"))
                    }
                    Circle().fill(dot).frame(width: 7, height: 7)
                }
                ZStack {
                    if current { PPIcon("check", size: M.iconSmall, color: theme.accent) }
                }
                .frame(width: M.icon, height: M.icon)
            }
            .padding(.vertical, M.sm)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(name)
    }
}

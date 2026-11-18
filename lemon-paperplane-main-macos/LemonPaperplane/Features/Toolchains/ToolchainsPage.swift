import AppKit
import PaperplaneKit
import SwiftUI

/// 工具链：按种类分组列出已安装的，下载新版本（任务进度），手动添加路径，移除。
struct ToolchainsPage: View {
    @ObservedObject private var data = AppModel.shared.data
    @ObservedObject private var runs = AppModel.shared.runs
    @State private var refreshing = false
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(spacing: 0) {
            PPPageHeader(title: tr("sidebar.toolchains")) {
                PPButton(tr("toolchain.rescan"), icon: "refresh-cw", kind: .ghost, size: .small, busy: refreshing) {
                    refreshing = true
                    Task {
                        do { try await AppModel.shared.client().refreshToolchains() } catch { AppModel.shared.toasts.error(error) }
                        await AppModel.shared.reloadToolchains()
                        refreshing = false
                    }
                }
            }
            PPDivider(subtle: true)
            if data.toolchainKinds.isEmpty {
                PPEmptyState(icon: "boxes", title: tr("toolchain.noKinds"))
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: M.xxl) {
                        ForEach(data.toolchainKinds) { k in ToolchainKindSection(kind: k) }
                    }
                    .padding(M.xl)
                    .frame(maxWidth: 960, alignment: .leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .task { await AppModel.shared.reloadToolchains() }
    }
}

private struct ToolchainKindSection: View {
    let kind: ToolchainKind
    @ObservedObject private var data = AppModel.shared.data
    @ObservedObject private var runs = AppModel.shared.runs
    @State private var downloadOpen = false
    @Environment(\.theme) private var theme

    var body: some View {
        let list = data.toolchains(kind: kind.kind).sorted { compareVersions($0.version, $1.version) }
        let installing = runs.activeTasks.filter { t in
            if let k = runs.toolchainTaskKind[t.id] { return k == kind.kind }
            return t.kind == "toolchain" && t.title.localizedCaseInsensitiveContains(kind.name)
        }
        VStack(alignment: .leading, spacing: M.md) {
            HStack(spacing: M.sm) {
                PPIcon(raw: kind.icon, plugin: kind.plugin, fallback: "boxes", size: M.iconLarge)
                Text(kind.name).font(.pp(.title))
                PPCountBadge(count: list.count)
                Spacer()
                PPButton(tr("toolchain.addPath"), icon: "folder-plus", kind: .ghost, size: .small) { addPath() }
                if kind.installable {
                    PPButton(tr("toolchain.download"), icon: "download", size: .small) { downloadOpen.toggle() }
                        .ppPopover(isPresented: $downloadOpen, alignment: .trailing, wantsKey: true) {
                            AvailableVersionsPopover(kind: kind) { downloadOpen = false }
                        }
                }
            }
            ForEach(installing) { t in
                PPCard(padding: M.md) {
                    VStack(alignment: .leading, spacing: M.sm) {
                        HStack {
                            PPSpinner(size: M.iconSmall, color: theme.accent)
                            Text(t.title)
                            Spacer()
                            if let m = t.message { Text(m).font(.pp(.small)).foregroundColor(theme.textSecondary) }
                            PPIconButton("scroll-text", help: tr("task.viewLog"), size: M.iconButtonSmall) { Actions.showTaskLog(t.id) }
                            PPIconButton("x", help: tr("common.cancel"), size: M.iconButtonSmall) { Actions.cancelTask(t.id) }
                        }
                        PPProgressBar(value: t.percent)
                    }
                }
            }
            if list.isEmpty {
                Text(tr("toolchain.noneInstalled")).foregroundColor(theme.textSecondary).padding(.vertical, M.sm)
            } else {
                PPCard(padding: M.xs) {
                    VStack(spacing: 0) {
                        ForEach(list) { tc in
                            ToolchainRow(toolchain: tc)
                            if tc.id != list.last?.id { PPDivider(subtle: true).padding(.horizontal, M.md) }
                        }
                    }
                }
            }
        }
    }

    private func addPath() {
        let p = NSOpenPanel()
        p.canChooseDirectories = true
        p.canChooseFiles = true
        p.allowsMultipleSelection = false
        p.message = tr("toolchain.addPath")
        guard p.runModal() == .OK, let url = p.url else { return }
        AppModel.shared.perform(tr("toolchain.addPath")) { api in
            _ = try await api.addToolchain(kind: kind.kind, path: url.path)
            await AppModel.shared.reloadToolchains()
        }
    }
}

private struct ToolchainRow: View {
    let toolchain: Toolchain
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.md) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: M.sm) {
                    Text(toolchain.version).font(.pp(.bodyStrong).monospacedDigit())
                    if let v = toolchain.vendor, !v.isEmpty { Text(v).foregroundColor(theme.textSecondary) }
                    if let a = toolchain.arch, !a.isEmpty { PPBadge(text: a, mono: true) }
                    if toolchain.managed { PPBadge(text: tr("toolchain.managed"), tone: "info") }
                    if let s = toolchain.source, !s.isEmpty, !toolchain.managed { PPBadge(text: s) }
                }
                PPLink(title: Format.abbreviatePath(toolchain.path), mono: true) { Actions.revealInFinder(toolchain.path) }
            }
            Spacer(minLength: M.md)
            PPIconButton("copy", help: tr("toolchain.copyPath"), size: M.iconButtonSmall) { Actions.copy(toolchain.path) }
            PPIconButton("trash-2", help: tr("common.remove"), size: M.iconButtonSmall) {
                Dialogs.confirm(title: tr("toolchain.remove.title", toolchain.displayName),
                                message: toolchain.managed ? tr("toolchain.remove.managed") : tr("toolchain.remove.external"),
                                ok: tr("common.remove"), danger: true) {
                    try await AppModel.shared.client().removeToolchain(toolchain.id)
                    await AppModel.shared.reloadToolchains()
                }
            }
        }
        .padding(.horizontal, M.md)
        .padding(.vertical, M.sm)
    }
}

/// 可下载版本列表（toolchain.available），LTS / 推荐标记
private struct AvailableVersionsPopover: View {
    let kind: ToolchainKind
    var close: () -> Void
    @State private var versions: [AvailableVersion]?
    @State private var error: String?
    @State private var filter = ""
    @State private var ltsOnly = false
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: M.sm) {
                PPTextField(tr("common.filter"), text: $filter, icon: "search", height: M.controlHeightSmall)
                PPCheckbox(isOn: $ltsOnly, label: "LTS")
            }
            .padding(M.sm)
            PPDivider(subtle: true)
            if let versions {
                let shown = versions.filter { v in
                    (!ltsOnly || v.lts) && (filter.isEmpty || v.version.contains(filter) || (v.label ?? "").localizedCaseInsensitiveContains(filter)
                        || (v.vendor ?? "").localizedCaseInsensitiveContains(filter))
                }
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(shown) { v in
                            PPRow(action: { install(v) }) {
                                HStack(spacing: M.sm) {
                                    Text(v.label ?? v.version).lineLimit(1)
                                    if let vend = v.vendor, !(v.label ?? "").contains(vend) { Text(vend).foregroundColor(theme.textTertiary) }
                                    if v.lts { PPBadge(text: "LTS", tone: "success") }
                                    if v.recommended { PPBadge(text: tr("toolchain.recommended"), tone: "info") }
                                    Spacer()
                                    PPIcon("download", size: M.iconSmall, color: theme.iconSecondary)
                                }
                            }
                        }
                    }
                    .padding(M.xs)
                }
                .frame(height: 320)
            } else if let error {
                Text(error).foregroundColor(theme.danger).padding(M.md)
            } else {
                PPSpinner().padding(M.lg).frame(maxWidth: .infinity)
            }
        }
        .frame(width: 360)
        .task {
            do { versions = try await AppModel.shared.client().availableToolchains(kind: kind.kind) } catch { self.error = error.localizedDescription }
        }
    }

    private func install(_ v: AvailableVersion) {
        close()
        AppModel.shared.perform(tr("toolchain.download")) { api in
            let t = try await api.installToolchain(kind: kind.kind, version: v.version)
            AppModel.shared.runs.toolchainTaskKind[t.id] = kind.kind
            AppModel.shared.track(t)
        }
    }
}

/// 版本号降序比较（数字段逐段比较）
func compareVersions(_ a: String, _ b: String) -> Bool {
    let pa = a.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
    let pb = b.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
    for i in 0..<max(pa.count, pb.count) {
        let x = i < pa.count ? pa[i] : 0, y = i < pb.count ? pb[i] : 0
        if x != y { return x > y }
    }
    return a > b
}

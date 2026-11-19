import PaperplaneKit
import SwiftUI

/// 设置页里的分组锚点
enum SettingsAnchor {
    static let servers = "settings.servers"
}

/// 小标题 + 右侧附件 + 卡片（设置 → 服务器里的子区块，与 Windows 版一致）
private struct SubSection<Accessory: View, Content: View>: View {
    let title: String
    @ViewBuilder let accessory: Accessory
    @ViewBuilder let content: Content
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: M.sm) {
            HStack(spacing: M.sm) {
                Text(title).font(.pp(.label)).foregroundColor(theme.textSecondary)
                Spacer(minLength: M.md)
                accessory
            }
            .frame(minHeight: M.controlHeightSmall)
            PPCard { content.frame(maxWidth: .infinity, alignment: .leading) }
        }
    }
}

/// 卡片里的多行：行间细分隔线
private struct DividedRows<Item: Identifiable, Row: View>: View {
    let items: [Item]
    @ViewBuilder let row: (Item) -> Row

    var body: some View {
        VStack(spacing: 0) {
            ForEach(Array(items.enumerated()), id: \.element.id) { i, item in
                if i > 0 { PPDivider(subtle: true) }
                row(item).padding(.vertical, M.sm)
            }
        }
    }
}

// MARK: - 已添加的服务器

/// 设置 → 服务器：本机保存的服务器（系统图标、名称、地址、指纹前 8 位；··· 连接 / 重命名 / 重新登录 / 删除）
struct ServersSettingsSection: View {
    @ObservedObject private var servers = AppModel.shared.servers
    @Environment(\.theme) private var theme

    var body: some View {
        PPSection(tr("settings.servers")) {
            SubSection(title: tr("server.saved"), accessory: {
                PPButton(tr("server.add"), icon: "plus", size: .small) { AddServerDialog.present() }
            }) {
                if servers.servers.isEmpty {
                    Text(tr("server.saved.empty")).foregroundColor(theme.textTertiary).padding(.vertical, M.xs)
                } else {
                    DividedRows(items: servers.servers) { s in row(s) }
                }
            }
        }
    }

    @ViewBuilder private func currentBadge() -> some View {
        switch servers.phase {
        case .ready: PPBadge(text: tr("server.status.connected"), tone: "success")
        case .connecting: PPBadge(text: tr("server.status.connecting"), tone: "warning")
        case .failed: PPBadge(text: tr("server.status.failed"), tone: "danger")
        }
    }

    private func row(_ s: SavedServer) -> some View {
        HStack(spacing: M.md) {
            PPIcon(ServerStore.osIcon(s.os), size: M.icon, color: theme.iconSecondary)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: M.sm) {
                    Text(s.name).font(.pp(.bodyStrong)).lineLimit(1)
                    if servers.currentId == s.id { currentBadge() }
                }
                HStack(spacing: M.md) {
                    Text(s.displayAddress).font(.pp(.monoSmall)).foregroundColor(theme.textSecondary).lineLimit(1).truncationMode(.middle)
                    if !s.fingerprint.isEmpty {
                        HStack(spacing: M.xxs) {
                            PPIcon("shield", size: 12, color: theme.iconSecondary)
                            Text(s.shortFingerprint).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary)
                        }
                        .help(tr("host.fingerprint"))
                    }
                }
            }
            Spacer(minLength: M.md)
            PPMenuButton(alignment: .trailing, entries: { menu(s) }) {
                PPIcon("ellipsis", size: M.icon, color: theme.iconSecondary)
                    .frame(width: M.iconButtonSmall, height: M.iconButtonSmall)
                    .contentShape(Rectangle())
            }
            .help(tr("common.more"))
        }
        .ppContextMenu { menu(s) }
    }

    private func menu(_ s: SavedServer) -> [MenuEntry] {
        let model = AppModel.shared
        return [
            .item(tr("server.action.connect"), icon: "plug", disabled: servers.currentId == s.id && servers.phase == .ready) {
                model.switchServer(to: s.id)
            },
            .item(tr("server.action.rename"), icon: "pencil") {
                Dialogs.input(title: tr("server.rename.title"), label: tr("common.name"), initial: s.name, ok: tr("common.save")) { name in
                    model.servers.rename(s.id, to: name)
                }
            },
            .item(tr("server.action.relogin"), icon: "key-round") { AddServerDialog.present(relogin: s) },
            .divider,
            .item(tr("server.action.remove"), icon: "trash-2", destructive: true) {
                Dialogs.confirm(title: tr("server.remove.confirm", s.name), message: tr("server.remove.message"),
                                ok: tr("server.action.remove"), danger: true) {
                    // 正连着这台：先切回本机（编辑窗口有未保存时切不了，也就不删）
                    if model.servers.currentId == s.id, !model.switchServer(to: nil) {
                        throw APIError(code: APIError.codeUserError, message: tr("editorWindow.unsaved"))
                    }
                    model.servers.remove(s.id)
                }
            },
        ]
    }
}

// MARK: - 允许其他设备连接（当前服务器）

/// 当前服务器的远程连接：所有 remote.* 都走当前接口（连着远程时管理的是那台服务器）
@MainActor
final class HostSettingsModel: ObservableObject {
    @Published var status: RemoteStatus?
    @Published var keys: [RemoteKey] = []
    @Published var users: [RemoteUser] = []
    @Published var audit: [RemoteAuditEntry] = []
    @Published var error: String?
    @Published var busy = false

    func load() async {
        do {
            let api = try AppModel.shared.client()
            status = try await api.remoteStatus()
            error = nil
            await loadLists()
        } catch {
            self.error = error.localizedDescription
        }
    }

    func loadLists() async {
        guard let api = try? AppModel.shared.client() else { return }
        async let k = try? api.remoteKeys()
        async let u = try? api.remoteUsers()
        async let a = try? api.remoteAudit(limit: 50)
        if let k = await k { keys = k }
        if let u = await u { users = u }
        if let a = await a { audit = a }
    }

    func configure(enabled: Bool? = nil, port: Int? = nil, passwordLogin: Bool? = nil, autostart: Bool? = nil) {
        busy = true
        Task {
            defer { busy = false }
            do {
                status = try await AppModel.shared.client().remoteConfigure(enabled: enabled, port: port, passwordLogin: passwordLogin, autostart: autostart)
                await loadLists()
            } catch {
                AppModel.shared.toasts.error(error, title: tr("settings.saveFailed"))
                await load()
            }
        }
    }

    func reload() { Task { await load() } }
}

struct HostSettingsSection: View {
    @StateObject private var host = HostSettingsModel()
    @ObservedObject private var servers = AppModel.shared.servers
    @Environment(\.theme) private var theme

    private var title: String { servers.isRemote ? tr("host.titleRemote", servers.currentName) : tr("host.title") }

    var body: some View {
        PPSection(title, info: tr("host.enabled.info")) {
            VStack(alignment: .leading, spacing: M.lg) {
                if let e = host.error {
                    PPAlert(tone: "danger", title: e) {
                        PPButton(tr("server.retry"), icon: "refresh-cw", size: .small) { host.reload() }
                    }
                }
                if let st = host.status {
                    PPCard { statusCard(st) }
                    if st.enabled {
                        SubSection(title: tr("host.keys"), accessory: {
                            PPButton(tr("host.keys.add"), icon: "key-round", size: .small) { createKey() }
                        }) { keysCard }
                        SubSection(title: tr("host.users"), accessory: {
                            if st.passwordLogin {
                                PPButton(tr("host.users.add"), icon: "plus", size: .small) {
                                    AccountPasswordDialog.present(name: nil) { await host.load() }
                                }
                            }
                        }) { usersCard(st) }
                        SubSection(title: tr("host.devices"), accessory: { EmptyView() }) { devicesCard }
                    }
                    SubSection(title: tr("host.audit"), accessory: {
                        PPIconButton("refresh-cw", help: tr("files.refresh"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                            Task { await host.loadLists() }
                        }
                    }) { auditCard }
                } else if host.error == nil {
                    PPCard { HStack { PPSpinner(size: M.iconSmall); Spacer() } }
                }
            }
        }
        .task(id: servers.currentId ?? "") { await host.load() }
    }

    // MARK: 开关、端口、地址、指纹

    private func statusCard(_ st: RemoteStatus) -> some View {
        VStack(alignment: .leading, spacing: M.md) {
            SettingRow(tr("host.enabled")) {
                HStack(spacing: M.md) {
                    PPSwitch(isOn: Binding(get: { st.enabled }, set: { setEnabled($0) })).disabled(host.busy)
                    if st.enabled, st.running {
                        HStack(spacing: M.xs) {
                            Circle().fill(theme.statusRunning).frame(width: 7, height: 7)
                            Text(tr("host.running", st.listen)).font(.pp(.small)).foregroundColor(theme.textSecondary)
                        }
                    }
                    if st.enabled, !st.error.isEmpty {
                        Text(tr("host.error", st.error)).font(.pp(.small)).foregroundColor(theme.danger).lineLimit(2)
                    }
                    if host.busy { PPSpinner(size: M.iconSmall) }
                }
            }
            SettingRow(tr("host.port")) {
                NumberSetting(value: st.port) { p in
                    if (1...65535).contains(p) { host.configure(port: p) }
                }
            }
            if st.autostartSupported {
                SettingRow(tr("host.autostart"), info: tr("host.autostart.info")) {
                    PPSwitch(isOn: Binding(get: { st.autostart }, set: { host.configure(autostart: $0) })).disabled(host.busy)
                }
            }
            if st.enabled {
                PPDivider(subtle: true)
                if !st.addresses.isEmpty {
                    topRow(tr("host.addresses"), info: nil) {
                        VStack(alignment: .leading, spacing: M.xxs) {
                            ForEach(st.addresses, id: \.self) { a in copyLine(a, shown: Format.shortURL(a)) }
                        }
                    }
                }
                let fp = st.fingerprintDisplay.isEmpty ? st.fingerprint : st.fingerprintDisplay
                if !fp.isEmpty {
                    topRow(tr("host.fingerprint"), info: tr("host.fingerprint.info")) { copyLine(fp, shown: fp, wrap: true) }
                }
            }
        }
    }

    /// 标题靠上的设置行（内容多行时）
    private func topRow<C: View>(_ title: String, info: String?, @ViewBuilder _ content: () -> C) -> some View {
        HStack(alignment: .top, spacing: M.md) {
            HStack(spacing: M.xs) {
                Text(title)
                if let info { PPInfoButton(text: info) }
            }
            .frame(width: M.formLabelWidth, alignment: .leading)
            .frame(minHeight: M.controlHeightSmall)
            content()
            Spacer(minLength: 0)
        }
    }

    private func copyLine(_ value: String, shown: String, wrap: Bool = false) -> some View {
        HStack(alignment: .top, spacing: M.sm) {
            Text(shown).font(.pp(.mono)).textSelection(.enabled)
                .lineLimit(wrap ? nil : 1).truncationMode(.middle)
                .fixedSize(horizontal: false, vertical: wrap)
                .frame(minHeight: M.controlHeightSmall)
            PPIconButton("copy", help: tr("host.copy"), size: M.iconButtonSmall, iconSize: M.iconSmall) {
                Actions.copy(value, toast: tr("host.copied"))
            }
        }
    }

    private func setEnabled(_ on: Bool) {
        // 连着远程服务器时关掉它的远程连接，本机就和它断开了：先确认
        if !on, servers.isRemote {
            Dialogs.confirm(title: tr("host.disable"), message: tr("host.disable.remoteConfirm", servers.currentName),
                            ok: tr("host.disable"), danger: true) {
                host.configure(enabled: false)
            }
            return
        }
        host.configure(enabled: on)
    }

    // MARK: 访问密钥、已登录的设备

    private func lastUsed(_ k: RemoteKey) -> String {
        guard let d = k.lastUsedAt, !GoTime.isZero(d) else { return tr("host.keys.neverUsed") }
        return tr("host.keys.lastUsed", VCSDate.short(d), k.lastIP.isEmpty ? "-" : k.lastIP)
    }

    @ViewBuilder private var keysCard: some View {
        let keys = host.keys.filter { !$0.isDevice }
        if keys.isEmpty {
            Text(tr("host.keys.empty")).foregroundColor(theme.textTertiary)
        } else {
            DividedRows(items: keys) { k in
                HStack(spacing: M.md) {
                    PPIcon("key-round", size: M.icon, color: theme.iconSecondary)
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: M.sm) {
                            Text(k.name).font(.pp(.bodyStrong)).lineLimit(1)
                            if k.current { PPBadge(text: tr("host.devices.current"), tone: "info") }
                        }
                        Text(tr("host.keys.created.at", VCSDate.short(k.createdAt)) + " · " + lastUsed(k))
                            .font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1)
                    }
                    Spacer(minLength: M.md)
                    revokeButton(k)
                }
            }
        }
    }

    @ViewBuilder private var devicesCard: some View {
        let devices = host.keys.filter(\.isDevice)
        if devices.isEmpty {
            Text(tr("host.devices.empty")).foregroundColor(theme.textTertiary)
        } else {
            DividedRows(items: devices) { k in
                HStack(spacing: M.md) {
                    PPIcon("monitor", size: M.icon, color: theme.iconSecondary)
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: M.sm) {
                            Text(k.name).font(.pp(.bodyStrong)).lineLimit(1)
                            if !k.user.isEmpty { Text(k.user).font(.pp(.small)).foregroundColor(theme.textSecondary) }
                            if k.current { PPBadge(text: tr("host.devices.current"), tone: "info") }
                        }
                        Text(lastUsed(k)).font(.pp(.small)).foregroundColor(theme.textTertiary).lineLimit(1)
                    }
                    Spacer(minLength: M.md)
                    revokeButton(k)
                }
            }
        }
    }

    private func revokeButton(_ k: RemoteKey) -> some View {
        PPButton(tr("host.keys.revoke"), kind: .ghost, size: .small) {
            Dialogs.confirm(title: tr("host.keys.revoke.confirm", k.name), message: tr("host.keys.revoke.message"),
                            ok: tr("host.keys.revoke"), danger: true) {
                try await AppModel.shared.client().remoteKeyRevoke(k.id)
                await host.load()
            }
        }
    }

    private func createKey() {
        let fields = [
            FormField(key: "name", title: tr("host.keys.name"), type: "string", placeholder: tr("host.keys.name.placeholder"), required: true),
            FormField(key: "address", title: tr("host.keys.address"), type: "string", placeholder: tr("server.add.address.placeholder"),
                      info: tr("host.keys.address.info")),
        ]
        Dialogs.form(title: tr("host.keys.add"), fields: fields, ok: tr("host.keys.add")) { v in
            let name = v["name"]?.stringValue ?? ""
            let r = try await AppModel.shared.client().remoteKeyCreate(name: name, address: v["address"]?.stringValue)
            await host.load()
            DispatchQueue.main.async { ConnectionCodeDialog.present(code: r.code) }
        }
    }

    // MARK: 账号密码

    @ViewBuilder private func usersCard(_ st: RemoteStatus) -> some View {
        VStack(alignment: .leading, spacing: M.md) {
            SettingRow(tr("host.passwordLogin"), info: tr("host.passwordLogin.info")) {
                PPSwitch(isOn: Binding(get: { st.passwordLogin }, set: { host.configure(passwordLogin: $0) })).disabled(host.busy)
            }
            if st.passwordLogin || !host.users.isEmpty {
                PPDivider(subtle: true)
                if host.users.isEmpty {
                    Text(tr("host.users.empty")).foregroundColor(theme.textTertiary)
                } else {
                    DividedRows(items: host.users) { u in userRow(u) }
                }
            }
        }
    }

    private func userRow(_ u: RemoteUser) -> some View {
        HStack(spacing: M.md) {
            PPIcon("user", size: M.icon, color: theme.iconSecondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(u.name).font(.pp(.bodyStrong)).lineLimit(1)
                Text(VCSDate.short(u.updatedAt ?? u.createdAt)).font(.pp(.small)).foregroundColor(theme.textTertiary)
            }
            Spacer(minLength: M.md)
            PPButton(tr("host.users.setPassword"), kind: .ghost, size: .small) {
                AccountPasswordDialog.present(name: u.name) { await host.load() }
            }
            PPIconButton("trash-2", help: tr("common.delete"), size: M.iconButtonSmall, tint: theme.danger) {
                Dialogs.confirm(title: tr("host.users.remove.confirm", u.name), message: tr("host.users.remove.message"),
                                ok: tr("common.delete"), danger: true) {
                    try await AppModel.shared.client().remoteUserRemove(u.name)
                    await host.load()
                }
            }
        }
    }

    // MARK: 安全日志

    @ViewBuilder private var auditCard: some View {
        if host.audit.isEmpty {
            Text(tr("host.audit.empty")).foregroundColor(theme.textTertiary)
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(host.audit.enumerated()), id: \.offset) { _, e in auditRow(e) }
                }
            }
            .frame(maxHeight: 280)
        }
    }

    private func auditRow(_ e: RemoteAuditEntry) -> some View {
        let key = "host.audit.event." + e.event
        let title = tr(key) == key ? e.event : tr(key)
        let danger = ["login.fail", "auth.fail", "ban"].contains(e.event)
        return HStack(spacing: M.md) {
            Text(VCSDate.full(e.time)).font(.pp(.monoSmall)).foregroundColor(theme.textTertiary).frame(width: 118, alignment: .leading)
            Text(title).font(.pp(.small)).foregroundColor(danger ? theme.danger : theme.textPrimary).lineLimit(1)
                .frame(width: 96, alignment: .leading)
            Text([e.who, e.ip, e.detail].filter { !$0.isEmpty }.joined(separator: " · "))
                .font(.pp(.small)).foregroundColor(theme.textSecondary).lineLimit(1).truncationMode(.middle)
            Spacer(minLength: 0)
        }
        .frame(height: M.rowHeightCompact)
    }
}

// MARK: - 对话框

/// 生成密钥后：连接码只显示这一次
enum ConnectionCodeDialog {
    @MainActor
    static func present(code: String) {
        let id = "connection-code"
        AppModel.shared.dialogs.present(id: id) { Content(id: id, code: code) }
    }

    private struct Content: View {
        let id: String
        let code: String
        @Environment(\.theme) private var theme

        var body: some View {
            PPDialog(title: tr("host.keys.created"), width: M.dialogWidth, onClose: close) {
                VStack(alignment: .leading, spacing: M.lg) {
                    PPAlert(tone: "warning", title: tr("host.keys.created.message"))
                    Text(code)
                        .font(.pp(.mono))
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(M.md)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgCode))
                }
            } footer: {
                PPButton(tr("common.close"), action: close).keyboardShortcut(.cancelAction)
                PPButton(tr("host.copy"), icon: "copy", kind: .primary) { Actions.copy(code, toast: tr("host.copied")) }
                    .keyboardShortcut(.defaultAction)
            }
        }

        private func close() { AppModel.shared.dialogs.dismiss(id) }
    }
}

/// 添加账号 / 设置密码：至少 10 位，输两遍
struct AccountPasswordDialog: View {
    let id: String
    /// nil = 新建账号
    let existing: String?
    let onDone: () async -> Void
    @State private var name = ""
    @State private var password = ""
    @State private var password2 = ""
    @State private var showErrors = false
    @State private var busy = false
    @State private var error: String?
    @Environment(\.theme) private var theme

    @MainActor
    static func present(name: String?, onDone: @escaping () async -> Void) {
        let id = "account-password"
        AppModel.shared.dialogs.present(id: id) { AccountPasswordDialog(id: id, existing: name, onDone: onDone) }
    }

    private var tooShort: Bool { password.count < 10 }
    private var mismatch: Bool { password != password2 }

    var body: some View {
        PPDialog(title: existing == nil ? tr("host.users.add") : tr("host.users.setPassword"), width: 440, onClose: close) {
            VStack(alignment: .leading, spacing: M.md) {
                row(tr("host.users.name")) {
                    if let existing {
                        Text(existing).font(.pp(.bodyStrong)).frame(maxWidth: .infinity, alignment: .leading)
                    } else {
                        PPTextField("", text: $name, invalid: showErrors && name.trimmingCharacters(in: .whitespaces).isEmpty)
                    }
                }
                row(tr("host.users.password")) {
                    PPTextField(tr("host.users.password.rule"), text: $password, secure: true, invalid: showErrors && tooShort)
                }
                row(tr("host.users.password2")) {
                    PPTextField("", text: $password2, secure: true, invalid: showErrors && mismatch, onSubmit: submit)
                }
                if showErrors && tooShort {
                    Text(tr("host.users.password.rule")).font(.pp(.small)).foregroundColor(theme.danger)
                } else if showErrors && mismatch {
                    Text(tr("host.users.password.mismatch")).font(.pp(.small)).foregroundColor(theme.danger)
                }
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
            PPButton(tr("common.save"), kind: .primary, busy: busy, action: submit).keyboardShortcut(.defaultAction)
        }
    }

    private func row<C: View>(_ title: String, @ViewBuilder _ c: () -> C) -> some View {
        HStack(spacing: M.md) {
            Text(title).frame(width: 88, alignment: .leading)
            c()
        }
    }

    private func submit() {
        let n = existing ?? name.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty, !tooShort, !mismatch else {
            showErrors = true
            return
        }
        busy = true
        error = nil
        Task {
            do {
                try await AppModel.shared.client().remoteUserSet(name: n, password: password)
                password = ""
                password2 = ""
                close()
                await onDone()
            } catch {
                self.error = error.localizedDescription
            }
            busy = false
        }
    }

    private func close() { AppModel.shared.dialogs.dismiss(id) }
}

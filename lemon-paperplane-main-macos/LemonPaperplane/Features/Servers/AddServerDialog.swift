import AppKit
import PaperplaneKit
import SwiftUI

/// 添加服务器（也用于重新登录）：连接码 | 账号密码。
/// 连接码：gateway.decode → 依次 gateway.open 试地址；
/// 账号密码：gateway.probe → 不是 CA 证书时确认指纹 → gateway.login → gateway.open。
/// 成功后服务器记录进偏好、密钥进钥匙串，并切到这台。
struct AddServerDialog: View {
    enum Mode: Hashable { case code, password }

    let id: String
    /// 重新登录：更新这条记录的密钥，不新增
    let relogin: SavedServer?

    @State private var mode: Mode
    @State private var name: String
    @State private var nameEdited: Bool
    // 连接码
    @State private var code = ""
    @State private var decoded: GatewayCode?
    @State private var decodeFailed = false
    @State private var address: String
    // 账号密码
    @State private var user = ""
    @State private var password = ""
    // 状态
    @State private var busy = false
    @State private var error: String?
    /// 需要用户确认的证书（账号密码登录、自签证书）
    @State private var pendingCert: GatewayProbe?
    @State private var certChanged = false

    @ObservedObject private var servers = AppModel.shared.servers
    @Environment(\.theme) private var theme

    static func present(relogin: SavedServer? = nil) {
        let id = "add-server"
        AppDelegate.shared?.showMainWindow()
        AppModel.shared.dialogs.present(id: id) { AddServerDialog(id: id, relogin: relogin) }
    }

    init(id: String, relogin: SavedServer?) {
        self.id = id
        self.relogin = relogin
        _mode = State(initialValue: relogin == nil ? .code : .password)
        _name = State(initialValue: relogin?.name ?? "")
        _nameEdited = State(initialValue: relogin != nil)
        _address = State(initialValue: relogin?.url ?? "")
    }

    var body: some View {
        PPDialog(title: relogin == nil ? tr("server.add.title") : tr("server.action.relogin"), width: M.dialogWidth, onClose: close) {
            if let probe = pendingCert {
                certView(probe)
            } else {
                form
            }
        } footer: {
            if pendingCert != nil {
                PPButton(tr("common.cancel")) { pendingCert = nil }.keyboardShortcut(.cancelAction)
                PPButton(tr("server.verify.trust"), kind: .primary, busy: busy) {
                    if let p = pendingCert { Task { await login(p) } }
                }
                .keyboardShortcut(.defaultAction)
            } else {
                PPButton(tr("common.cancel"), action: close).keyboardShortcut(.cancelAction)
                PPButton(busy ? tr("server.add.connecting") : tr("server.add.connect"), kind: .primary, busy: busy, action: submit)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canSubmit)
            }
        }
        .onChange(of: code) { c in decode(c) }
        .onChange(of: mode) { _ in error = nil }
    }

    // MARK: 表单

    private var form: some View {
        VStack(alignment: .leading, spacing: M.lg) {
            PPSegmented(options: [(Mode.code, tr("server.add.mode.code")), (Mode.password, tr("server.add.mode.password"))],
                        selection: $mode)
            labeled(tr("server.add.name")) {
                PPTextField(tr("server.add.name.placeholder"), text: Binding(get: { name }, set: { name = $0; nameEdited = true }))
            }
            if mode == .code {
                labeled(tr("server.add.code"), info: tr("server.add.code.info"), top: true) {
                    VStack(alignment: .leading, spacing: M.xs) {
                        PPTextArea(text: $code, placeholder: tr("server.add.code.placeholder"), mono: true, minHeight: 64)
                        if decodeFailed {
                            Text(tr("server.add.code.invalid")).font(.pp(.small)).foregroundColor(theme.danger)
                        }
                    }
                }
                if decoded != nil {
                    labeled(tr("server.add.address"), info: tr("server.add.address.info")) {
                        PPTextField(tr("server.add.address.placeholder"), text: $address, mono: true)
                    }
                }
            } else {
                labeled(tr("server.add.address"), info: tr("server.add.address.info")) {
                    PPTextField(tr("server.add.address.placeholder"), text: $address, mono: true)
                }
                labeled(tr("server.add.user")) { PPTextField("", text: $user) }
                labeled(tr("server.add.password")) { PPTextField("", text: $password, secure: true, onSubmit: submit) }
            }
            if let dup = duplicate {
                PPAlert(tone: "info", title: tr("server.add.duplicate", dup.name))
            }
            if let error { PPAlert(tone: "danger", title: error) }
        }
    }

    private func labeled<C: View>(_ title: String, info: String? = nil, top: Bool = false, @ViewBuilder _ c: () -> C) -> some View {
        HStack(alignment: top ? .top : .center, spacing: M.md) {
            HStack(spacing: M.xs) {
                Text(title)
                if let info { PPInfoButton(text: info) }
            }
            .frame(width: 96, alignment: .leading)
            .padding(.top, top ? 6 : 0)
            c()
        }
    }

    /// 证书确认（和 SSH 第一次连接一样）
    private func certView(_ p: GatewayProbe) -> some View {
        VStack(alignment: .leading, spacing: M.lg) {
            Text(tr("server.verify.title")).font(.pp(.bodyStrong))
            Text(tr("server.verify.message")).fixedSize(horizontal: false, vertical: true)
            if certChanged { PPAlert(tone: "warning", title: tr("server.err.fingerprint")) }
            if let h = p.hello {
                HStack(spacing: M.sm) {
                    PPIcon(ServerStore.osIcon(h.os), size: M.icon, color: theme.iconSecondary)
                    Text([h.hostname, ServerStore.osName(h.os), Format.shortURL(p.url)].filter { !$0.isEmpty }.joined(separator: " · "))
                        .foregroundColor(theme.textSecondary).lineLimit(1).truncationMode(.middle)
                }
            }
            VStack(alignment: .leading, spacing: M.xs) {
                Text(tr("server.verify.fingerprint")).font(.pp(.label)).foregroundColor(theme.textSecondary)
                Text(groupedFingerprint(p.fingerprintDisplay))
                    .font(.pp(.mono))
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(M.md)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: M.radiusMD).fill(theme.bgCode))
            }
            if let error { PPAlert(tone: "danger", title: error) }
        }
    }

    /// 16 组指纹分两行，方便逐组核对
    private func groupedFingerprint(_ s: String) -> String {
        let groups = s.split(separator: " ")
        guard groups.count > 8 else { return s }
        return groups.prefix(8).joined(separator: " ") + "\n" + groups.dropFirst(8).joined(separator: " ")
    }

    // MARK: 校验

    private var canSubmit: Bool {
        switch mode {
        case .code: return decoded != nil && !address.trimmingCharacters(in: .whitespaces).isEmpty
        case .password:
            return ServerURL.normalizeAddress(address) != nil && !user.trimmingCharacters(in: .whitespaces).isEmpty && !password.isEmpty
        }
    }

    /// 已添加过的同一台服务器（重新登录时不提示）
    private var duplicate: SavedServer? {
        guard relogin == nil else { return nil }
        switch mode {
        case .code:
            guard let d = decoded else { return nil }
            for u in [address] + d.urls {
                guard let n = ServerURL.normalizeAddress(u) else { continue }
                if let s = servers.existing(url: n, fingerprint: d.fingerprint) { return s }
            }
            return nil
        case .password:
            guard let n = ServerURL.normalizeAddress(address) else { return nil }
            return servers.servers.first { ServerURL.normalize($0.url) == ServerURL.normalize(n) }
        }
    }

    // MARK: 连接码

    private func decode(_ raw: String) {
        let c = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        error = nil
        guard !c.isEmpty else {
            decoded = nil
            decodeFailed = false
            return
        }
        guard let local = AppModel.shared.localAPI else { return }
        Task {
            do {
                let d = try await local.gatewayDecode(code: c)
                guard c == code.trimmingCharacters(in: .whitespacesAndNewlines) else { return }
                decoded = d
                decodeFailed = false
                address = d.urls.first ?? ""
                if !nameEdited || name.isEmpty { name = d.name }
            } catch {
                guard c == code.trimmingCharacters(in: .whitespacesAndNewlines) else { return }
                decoded = nil
                decodeFailed = true
            }
        }
    }

    // MARK: 连接

    private func submit() {
        guard canSubmit, !busy else { return }
        guard let local = AppModel.shared.localAPI else {
            error = tr("server.localRequired")
            return
        }
        busy = true
        error = nil
        Task {
            switch mode {
            case .code: await connectWithCode(local)
            case .password: await probe(local)
            }
        }
    }

    private func connectWithCode(_ local: APIClient) async {
        guard let d = decoded else { busy = false; return }
        // 先试（可能改过的）地址，再试连接码里的其余地址
        var urls: [String] = []
        for u in [address] + d.urls {
            guard let n = ServerURL.normalizeAddress(u), !urls.contains(where: { ServerURL.normalize($0) == ServerURL.normalize(n) }) else { continue }
            urls.append(n)
        }
        var lastError: Error?
        for u in urls {
            do {
                let gw = try await local.gatewayOpen(url: u, fingerprint: d.fingerprint, trusted: false, token: d.token)
                finish(url: u, fingerprint: d.fingerprint, trusted: false, token: d.token, info: gw.info, fallbackName: d.name)
                return
            } catch {
                lastError = error
                // 密钥、指纹、版本的问题换地址也没用
                if let e = error as? APIError, e.code != APIError.codeUnreachable, e.code != APIError.codeNetwork { break }
            }
        }
        self.error = lastError.map(serverErrorText) ?? tr("server.add.code.invalid")
        busy = false
    }

    private func probe(_ local: APIClient) async {
        guard let addr = ServerURL.normalizeAddress(address) else { busy = false; return }
        do {
            let p = try await local.gatewayProbe(address: addr)
            if p.hello?.passwordLogin == false {
                error = tr("server.err.passwordDisabled")
                busy = false
                return
            }
            if !p.trusted {
                // 重新登录且指纹没变：不用再确认
                let known = relogin.map { !$0.fingerprint.isEmpty && $0.fingerprint.lowercased() == p.fingerprint.lowercased() } ?? false
                if !known {
                    certChanged = relogin.map { !$0.fingerprint.isEmpty } ?? false
                    pendingCert = p
                    busy = false
                    return
                }
            }
            await login(p)
        } catch {
            self.error = serverErrorText(error)
            busy = false
        }
    }

    private func login(_ p: GatewayProbe) async {
        guard let local = AppModel.shared.localAPI else { return }
        busy = true
        error = nil
        do {
            let device = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
            let token = try await local.gatewayLogin(url: p.url, fingerprint: p.fingerprint, trusted: p.trusted,
                                                     user: user.trimmingCharacters(in: .whitespaces), password: password, device: device)
            let gw = try await local.gatewayOpen(url: p.url, fingerprint: p.fingerprint, trusted: p.trusted, token: token)
            password = ""
            finish(url: p.url, fingerprint: p.fingerprint, trusted: p.trusted, token: token, info: gw.info,
                   fallbackName: p.hello?.name ?? p.hello?.hostname ?? "")
        } catch {
            self.error = serverErrorText(error)
            // 账号密码错：回到表单改
            if pendingCert != nil, let e = error as? APIError, e.code == APIError.codeUnauthorized || e.code == APIError.codeRateLimited {
                pendingCert = nil
            }
            busy = false
        }
    }

    /// 保存并切到这台
    private func finish(url: String, fingerprint: String, trusted: Bool, token: String, info: SystemInfo?, fallbackName: String) {
        let store = AppModel.shared.servers
        let existing = relogin.flatMap { store.server($0.id) } ?? duplicate
        var s = existing ?? SavedServer(name: "", url: url, fingerprint: fingerprint, trusted: trusted)
        let n = name.trimmingCharacters(in: .whitespaces)
        if !n.isEmpty {
            s.name = n
        } else if existing == nil || s.name.isEmpty {
            s.name = !fallbackName.isEmpty ? fallbackName : (info?.hostname.isEmpty == false ? info!.hostname : ServerURL.host(of: url))
        }
        s.url = url
        s.fingerprint = fingerprint
        s.trusted = trusted
        if let info {
            s.os = info.os
            s.hostname = info.hostname
        }
        guard store.save(s, token: token) else {
            error = tr("server.err.keychain")
            busy = false
            return
        }
        busy = false
        close()
        if store.currentId == s.id {
            AppModel.shared.reconnectServer()
        } else {
            AppModel.shared.switchServer(to: s.id)
        }
    }

    private func close() { AppModel.shared.dialogs.dismiss(id) }
}

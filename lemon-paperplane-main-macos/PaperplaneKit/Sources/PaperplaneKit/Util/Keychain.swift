import Foundation
import Security

/// 访问密钥存钥匙串（generic password：service 固定，account = 服务器 id），不落普通文件。
public struct ServerKeychain: Sendable {
    public static let defaultService = "cn.lemonit.paperplane.server"

    public let service: String

    public init(service: String = ServerKeychain.defaultService) {
        self.service = service
    }

    private func query(_ account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    public func token(for account: String) -> String? {
        var q = query(account)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    @discardableResult
    public func setToken(_ token: String, for account: String, label: String? = nil) -> Bool {
        let data = Data(token.utf8)
        var attrs: [String: Any] = [kSecValueData as String: data]
        if let label { attrs[kSecAttrLabel as String] = label }
        let st = SecItemUpdate(query(account) as CFDictionary, attrs as CFDictionary)
        if st == errSecSuccess { return true }
        guard st == errSecItemNotFound else { return false }
        var add = query(account)
        add[kSecValueData as String] = data
        add[kSecAttrLabel as String] = label ?? "Lemon Paperplane server"
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    public func removeToken(for account: String) {
        SecItemDelete(query(account) as CFDictionary)
    }
}

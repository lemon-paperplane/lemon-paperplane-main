import Foundation
import PaperplaneKit

/// 界面语言。文案在 shared/i18n/<locale>.json（三端共用），切换即时生效。
final class L10n: ObservableObject {
    static let shared = L10n()

    /// 生效语言：zh-Hans | en
    @Published private(set) var locale: String
    /// 设置值：system | zh-Hans | en
    private(set) var setting: String

    let localizer: Localizer

    private static let defaultsKey = "PaperplaneLocale"

    private init() {
        let saved = UserDefaults.standard.string(forKey: Self.defaultsKey) ?? "system"
        setting = saved
        let l = Localizer.resolve(setting: saved)
        locale = l
        localizer = Localizer(directory: SharedResources.i18n, locale: l)
    }

    /// 应用设置值（来自 daemon 的 settings.locale，或设置页切换）
    func apply(setting s: String?) {
        let s = (s?.isEmpty == false ? s! : "system")
        setting = s
        UserDefaults.standard.set(s, forKey: Self.defaultsKey)
        let l = Localizer.resolve(setting: s)
        guard l != locale else { return }
        localizer.setLocale(l)
        locale = l
    }

    /// 系统首选语言对应的界面语言（拉起 daemon 时告诉它）
    var systemLocale: String { Localizer.resolve(setting: "system") }
}

/// 取文案：tr("key")、tr("key", 参数…) 替换 {0}{1}…
func tr(_ key: String, _ args: Any...) -> String {
    L10n.shared.localizer.t(key, args.map { "\($0)" })
}

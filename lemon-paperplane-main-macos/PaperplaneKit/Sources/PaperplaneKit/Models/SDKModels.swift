import Foundation

// 对应插件 SDK（lemon-paperplane-main-plugin-sdk）的 protocol.go / manifest.go 中界面会用到的类型。
// 字段名以 Go json tag 为准。为避免与 SwiftUI 重名，TableColumn/TableRow 改名为 ViewTableColumn/ViewTableRow。

public struct Facet: Codable, Hashable {
    public var id: String
    public var plugin: String?
    public var data: [String: JSONValue]?
    public init(id: String, plugin: String? = nil, data: [String: JSONValue]? = nil) {
        self.id = id; self.plugin = plugin; self.data = data
    }
}

public struct Toolchain: Codable, Hashable, Identifiable {
    @DefString public var id: String
    public var kind: String
    @DefString public var version: String
    public var vendor: String?
    public var arch: String?
    @DefString public var path: String
    public var executable: String?
    @DefDict public var env: [String: String]
    @DefArray public var pathPrepend: [String]
    public var source: String?
    @DefBool public var managed: Bool
    public var label: String?

    public init(id: String, kind: String, version: String, vendor: String? = nil, path: String,
                source: String? = nil, managed: Bool = false, label: String? = nil) {
        self.id = id; self.kind = kind; self.version = version; self.vendor = vendor
        self.path = path; self.source = source; self.managed = managed; self.label = label
    }

    /// 界面显示名：优先 label，否则「厂商 版本」
    public var displayName: String {
        if let label, !label.isEmpty { return label }
        if let vendor, !vendor.isEmpty { return "\(vendor) \(version)" }
        return version
    }
}

public struct ToolchainRequirement: Codable, Hashable {
    public var kind: String
    public var constraint: String?
    public var preferred: String?
    public var source: String?
    public var purpose: String?
    @DefBool public var optional: Bool
}

public struct DependencyState: Codable, Hashable {
    @DefString public var state: String // ok | missing | outdated | unknown | none
    public var manager: String?
    public var message: String?
    public init(state: String, manager: String? = nil, message: String? = nil) {
        self.state = state; self.manager = manager; self.message = message
    }
}

public struct InfoItem: Codable, Hashable {
    @DefString public var label: String
    @DefString public var value: String
    public var icon: String?
    public init(label: String, value: String, icon: String? = nil) {
        self.label = label; self.value = value; self.icon = icon
    }
}

public struct Option: Codable, Hashable {
    public var value: String
    public var label: String?
    public init(value: String, label: String? = nil) { self.value = value; self.label = label }
    public var displayLabel: String { (label?.isEmpty == false ? label : nil) ?? value }
}

public struct Fix: Codable, Hashable, Identifiable {
    public var id: String
    @DefString public var title: String
    @DefString public var kind: String
    @DefDict public var params: [String: String]
    @DefBool public var recommended: Bool
    public var confirm: String?
    public var plugin: String?
    public init(id: String, title: String, kind: String, params: [String: String] = [:],
                recommended: Bool = false, confirm: String? = nil) {
        self.id = id; self.title = title; self.kind = kind; self.params = params
        self.recommended = recommended; self.confirm = confirm
    }
}

/// 修复动作的 kind。
public enum FixKind {
    public static let setToolchain = "setToolchain"
    public static let setEnv = "setEnv"
    public static let appendEnv = "appendEnv"
    public static let unsetEnv = "unsetEnv"
    public static let setRunOption = "setRunOption"
    public static let installDependencies = "installDependencies"
    public static let killPort = "killPort"
    public static let reimport = "reimport"
    public static let restart = "restart"
    public static let openURL = "openUrl"
    public static let custom = "custom"
}

public struct Diagnosis: Codable, Hashable {
    public var id: String?
    public var ruleId: String?
    public var plugin: String?
    @DefString public var severity: String // error | warning | info
    @DefString public var title: String
    public var message: String?
    public var detail: String?
    @DefArray public var fixes: [Fix]
    public var line: String?

    public init(id: String? = nil, severity: String, title: String, message: String? = nil,
                detail: String? = nil, fixes: [Fix] = []) {
        self.id = id; self.severity = severity; self.title = title; self.message = message
        self.detail = detail; self.fixes = fixes
    }

    /// 用于列表 diff 的稳定键
    public var stableKey: String { id ?? "\(ruleId ?? "")|\(title)|\(line ?? "")" }
}

public enum Severity {
    public static let error = "error"
    public static let warning = "warning"
    public static let info = "info"

    /// 严重度排序：error > warning > info
    public static func rank(_ s: String?) -> Int {
        switch s {
        case error: return 3
        case warning: return 2
        case info: return 1
        default: return 0
        }
    }
}

/// 声明式表单字段。type：string | text | boolean | number | select | multiselect | keyvalue | path | list
/// 另外界面识别 password / secret 为密码框（SDK 暂无此类型，见报告）。
public struct FormField: Codable, Hashable, Identifiable {
    public var key: String
    @DefString public var title: String
    @DefString public var type: String
    public var `default`: JSONValue?
    @DefArray public var options: [Option]
    public var optionsFrom: String?
    /// type=code 时代码输入框的语言（如 shell）
    public var language: String?
    public var placeholder: String?
    public var info: String?
    @DefBool public var required: Bool
    @DefBool public var advanced: Bool
    /// 选项随时会变（如已连接设备）：表单打开时及打开期间定期经 runconfig.options 实时取
    @DefBool public var live: Bool = false

    public var id: String { key }

    public init(key: String, title: String, type: String, default: JSONValue? = nil, options: [Option] = [],
                optionsFrom: String? = nil, placeholder: String? = nil, info: String? = nil,
                required: Bool = false, advanced: Bool = false) {
        self.key = key; self.title = title; self.type = type; self.default = `default`
        self.options = options; self.optionsFrom = optionsFrom; self.placeholder = placeholder
        self.info = info; self.required = required; self.advanced = advanced
    }

    /// 是否按密码框显示
    public var isSecret: Bool {
        if type == "password" || type == "secret" { return true }
        let k = key.lowercased()
        return type == "string" && (k.contains("password") || k.contains("passphrase") || k.contains("secret") || k == "token")
    }
}

public struct Engines: Codable, Hashable {
    @DefString public var paperplane: String
}

public struct Dependency: Codable, Hashable {
    public var id: String
    public var version: String?
    @DefBool public var optional: Bool
}

public struct ActionDef: Codable, Hashable {
    public var id: String
    @DefString public var title: String
    public var icon: String?
    @DefArray public var facets: [String]
    public var confirm: String?
    public var group: String?
    @DefArray public var locations: [String]
    @DefArray public var form: [FormField]
}

/// 界面挂载位置。
public enum ViewLocation {
    public static let projectTab = "project.tab"
    public static let projectSection = "project.section"
    public static let sidebar = "sidebar"
    public static let runPanel = "run.panel"
    public static let vcsTab = "vcs.tab"
    public static let settings = "settings"
}

/// 动作挂载位置。
public enum ActionLocation {
    public static let projectToolbar = "project.toolbar"
    public static let projectMenu = "project.menu"
    public static let cardMenu = "card.menu"
    public static let runToolbar = "run.toolbar"
    public static let vcsToolbar = "vcs.toolbar"
    public static let vcsFileMenu = "vcs.fileMenu"
    public static let vcsCommitMenu = "vcs.commitMenu"
    public static let globalMenu = "global.menu"
}

public struct NotifyParams: Codable, Hashable {
    @DefString public var level: String // info | warning | error
    @DefString public var title: String
    public var message: String?
    public init(level: String, title: String, message: String? = nil) {
        self.level = level; self.title = title; self.message = message
    }
}

public struct AvailableVersion: Codable, Hashable, Identifiable {
    public var version: String
    public var label: String?
    public var vendor: String?
    @DefBool public var lts: Bool
    @DefBool public var recommended: Bool
    public var id: String { "\(vendor ?? "")|\(version)" }
}

// MARK: - 插件界面描述语言（view.go）

public struct ViewNode: Codable, Hashable {
    @DefString public var type: String
    public var id: String?
    public var title: String?
    public var text: String?
    public var icon: String?
    public var tone: String?
    public var info: String?
    @DefBool public var mono: Bool
    public var style: String?
    public var action: String?
    public var href: String?
    public var confirm: String?
    @DefBool public var disabled: Bool
    @DefDouble public var value: Double
    @DefArray public var items: [KV]
    @DefArray public var columns: [ViewTableColumn]
    @DefArray public var rows: [ViewTableRow]
    @DefBool public var selectable: Bool
    @DefArray public var fields: [FormField]
    public var values: [String: JSONValue]?
    public var submitText: String?
    public var console: String?
    @DefBool public var collapsed: Bool
    @DefArray public var children: [ViewNode]
    public var data: [String: String]?

    public init(type: String, id: String? = nil, title: String? = nil, text: String? = nil,
                children: [ViewNode] = []) {
        self.type = type; self.id = id; self.title = title; self.text = text; self.children = children
    }
}

public struct KV: Codable, Hashable {
    @DefString public var key: String
    @DefString public var value: String
    @DefBool public var mono: Bool
    public var href: String?
    public var tone: String?
}

public struct ViewTableColumn: Codable, Hashable {
    public var key: String
    @DefString public var title: String
    @DefInt public var width: Int
    public var align: String?
    @DefBool public var mono: Bool
}

public struct ViewTableRow: Codable, Hashable, Identifiable {
    @DefString public var id: String
    @DefDict public var cells: [String: String]
    public var icon: String?
    public var tone: String?
    public var action: String?
    @DefArray public var actions: [ViewNode]
    @DefArray public var children: [ViewTableRow]
    @DefBool public var expanded: Bool
}

public struct ViewRenderResult: Codable, Hashable {
    public var root: ViewNode
    public var state: [String: JSONValue]?
    @DefInt public var refreshMs: Int
}

public struct ViewEventResult: Codable, Hashable {
    @DefBool public var refresh: Bool
    public var state: [String: JSONValue]?
    public var message: String?
    public var openUrl: String?
    @DefBool public var reimport: Bool
}

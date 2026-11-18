import AppKit
import PaperplaneKit
import SwiftUI

// 版本控制界面的公共小件：偏好、状态颜色、日期、引用徽标、大对话框外壳、插件动作调用。

/// 本机偏好（UserDefaults），只影响界面
enum VCSPrefs {
    private static let d = UserDefaults.standard

    /// 暂存区模式（IDEA 的 Enable staging area）
    static var stagingArea: Bool {
        get { d.bool(forKey: "PaperplaneVCSStagingArea") }
        set { d.set(newValue, forKey: "PaperplaneVCSStagingArea") }
    }

    static var groupByDirectory: Bool {
        get { d.object(forKey: "PaperplaneVCSGroupByDir") as? Bool ?? true }
        set { d.set(newValue, forKey: "PaperplaneVCSGroupByDir") }
    }

    static var diffSideBySide: Bool {
        get { d.object(forKey: "PaperplaneVCSDiffSideBySide") as? Bool ?? true }
        set { d.set(newValue, forKey: "PaperplaneVCSDiffSideBySide") }
    }

    static var diffWrap: Bool {
        get { d.object(forKey: "PaperplaneVCSDiffWrap") as? Bool ?? false }
        set { d.set(newValue, forKey: "PaperplaneVCSDiffWrap") }
    }

    static var diffIgnoreWhitespace: Bool {
        get { d.bool(forKey: "PaperplaneVCSDiffIgnoreWS") }
        set { d.set(newValue, forKey: "PaperplaneVCSDiffIgnoreWS") }
    }

    static var pullMode: String {
        get { d.string(forKey: "PaperplaneVCSPullMode") ?? "merge" }
        set { d.set(newValue, forKey: "PaperplaneVCSPullMode") }
    }

    static var pullAutostash: Bool {
        get { d.object(forKey: "PaperplaneVCSPullAutostash") as? Bool ?? true }
        set { d.set(newValue, forKey: "PaperplaneVCSPullAutostash") }
    }

    static var logAllBranches: Bool {
        get { d.object(forKey: "PaperplaneVCSLogAll") as? Bool ?? true }
        set { d.set(newValue, forKey: "PaperplaneVCSLogAll") }
    }

    /// 最近的提交信息（按仓库，最多 20 条，新的在前）
    static func messageHistory(_ root: String) -> [String] {
        d.stringArray(forKey: "PaperplaneVCSMessages:" + root) ?? []
    }

    static func remember(message: String, root: String) {
        let m = message.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !m.isEmpty else { return }
        var list = messageHistory(root).filter { $0 != m }
        list.insert(m, at: 0)
        d.set(Array(list.prefix(20)), forKey: "PaperplaneVCSMessages:" + root)
    }
}

/// 文件状态的颜色与名称
enum VCSStyle {
    static func color(_ status: String, theme: Theme) -> Color {
        switch status {
        case "A": return theme.success
        case "D": return theme.textTertiary
        case "?": return theme.warning
        case "U": return theme.danger
        case "R", "C": return theme.info
        case "M", "T": return theme.info
        default: return theme.textPrimary
        }
    }

    static func title(_ status: String) -> String {
        switch status {
        case "A": return tr("vcs.status.added")
        case "D": return tr("vcs.status.deleted")
        case "?": return tr("vcs.status.untracked")
        case "U": return tr("vcs.status.conflicted")
        case "R": return tr("vcs.status.renamed")
        case "C": return tr("vcs.status.copied")
        case "T": return tr("vcs.status.typeChanged")
        default: return tr("vcs.status.modified")
        }
    }

    /// 冲突类型（插件给 UU/AA… 两字母，SDK 注释里是 both-modified…，两种都认）
    static func conflictTitle(_ t: String?) -> String {
        switch t {
        case "UU", "both-modified": return tr("vcs.conflict.bothModified")
        case "AA", "both-added": return tr("vcs.conflict.bothAdded")
        case "DU", "deleted-by-us": return tr("vcs.conflict.deletedByUs")
        case "UD", "deleted-by-them": return tr("vcs.conflict.deletedByThem")
        case "AU", "added-by-us": return tr("vcs.conflict.addedByUs")
        case "UA", "added-by-them": return tr("vcs.conflict.addedByThem")
        case "DD", "both-deleted": return tr("vcs.conflict.bothDeleted")
        default: return tr("vcs.conflict.unknown")
        }
    }

    static func operationTitle(_ op: String?) -> String {
        switch op {
        case "merge": return tr("vcs.op.merge")
        case "rebase": return tr("vcs.op.rebase")
        case "cherry-pick": return tr("vcs.op.cherryPick")
        case "revert": return tr("vcs.op.revert")
        case "am": return tr("vcs.op.am")
        default: return op ?? ""
        }
    }

    static func shortHash(_ h: String?) -> String { String((h ?? "").prefix(8)) }
}

/// 路径工具
enum VCSPath {
    static func name(_ p: String) -> String {
        let t = p.hasSuffix("/") ? String(p.dropLast()) : p
        return t.split(separator: "/").last.map(String.init) ?? t
    }

    static func dir(_ p: String) -> String {
        let t = p.hasSuffix("/") ? String(p.dropLast()) : p
        guard let i = t.lastIndex(of: "/") else { return "" }
        return String(t[..<i])
    }

    static func ext(_ p: String) -> String {
        let n = name(p)
        guard let i = n.lastIndex(of: "."), i != n.startIndex else { return "" }
        return String(n[n.index(after: i)...]).lowercased()
    }

    static func join(_ root: String, _ rel: String) -> String {
        (root as NSString).appendingPathComponent(rel)
    }

    static let imageExtensions: Set<String> = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "ico", "icns", "heic"]

    static func isImage(_ p: String) -> Bool { imageExtensions.contains(ext(p)) }
}

/// 日期显示
enum VCSDate {
    private static var relative: RelativeDateTimeFormatter {
        let f = RelativeDateTimeFormatter()
        f.locale = Locale(identifier: L10n.shared.locale == "zh-Hans" ? "zh_Hans" : "en")
        f.unitsStyle = .short
        return f
    }

    private static var absolute: DateFormatter {
        let f = DateFormatter()
        f.locale = Locale(identifier: L10n.shared.locale == "zh-Hans" ? "zh_Hans" : "en")
        f.dateFormat = "yyyy-MM-dd HH:mm"
        return f
    }

    /// 一周内显示相对时间，否则显示日期时间
    static func short(_ d: Date?) -> String {
        guard let d else { return "" }
        if abs(d.timeIntervalSinceNow) < 7 * 86400 { return relative.localizedString(for: d, relativeTo: Date()) }
        return absolute.string(from: d)
    }

    static func full(_ d: Date?) -> String {
        guard let d else { return "" }
        return absolute.string(from: d)
    }

    static func full(_ s: String?) -> String { full(s.flatMap(GoTime.parse)) }
}

/// 引用徽标：HEAD / 本地 / 远程 / 标签 不同颜色
struct VCSRefBadge: View {
    let ref: VCSRef
    var current = false
    @Environment(\.theme) private var theme

    var body: some View {
        let (icon, fg, bg) = style
        HStack(spacing: 3) {
            PPIcon(icon, size: 11)
            Text(ref.name).font(.pp(.caption)).lineLimit(1)
        }
        .foregroundColor(fg)
        .padding(.horizontal, 5)
        .frame(height: 16)
        .background(RoundedRectangle(cornerRadius: M.radiusSM).fill(bg))
        .help(ref.name)
    }

    private var style: (String, Color, Color) {
        switch ref.kind {
        case "head": return ("circle-dot", theme.accent, theme.accentSubtle)
        case "local": return ("git-branch", current ? theme.accent : theme.success, current ? theme.accentSubtle : theme.successSubtle)
        case "remote": return ("cloud", theme.info, theme.infoSubtle)
        case "tag": return ("tag", theme.warning, theme.warningSubtle)
        default: return ("git-commit-horizontal", theme.textSecondary, theme.neutralSubtle)
        }
    }
}

/// 状态字母（M/A/D…），等宽着色
struct VCSStatusLetter: View {
    let status: String
    @Environment(\.theme) private var theme

    var body: some View {
        Text(status == "?" ? "U" : status)
            .font(.pp(.monoSmall))
            .foregroundColor(VCSStyle.color(status, theme: theme))
            .frame(width: 12)
            .help(VCSStyle.title(status))
    }
}

/// 增删行数（+3 −1）
struct VCSLineStats: View {
    let additions: Int
    let deletions: Int
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.xs) {
            if additions > 0 { Text("+\(additions)").foregroundColor(theme.diffAddedText) }
            if deletions > 0 { Text("−\(deletions)").foregroundColor(theme.diffRemovedText) }
        }
        .font(.pp(.monoSmall))
    }
}

/// 小号分组标题（列表里的「变更」「未跟踪文件」等）
struct VCSGroupHeader<Accessory: View>: View {
    let title: String
    var count: Int?
    var collapsed: Bool?
    var onToggle: (() -> Void)?
    @ViewBuilder let accessory: Accessory
    @Environment(\.theme) private var theme

    var body: some View {
        HStack(spacing: M.xs) {
            if let collapsed {
                PPIcon(collapsed ? "chevron-right" : "chevron-down", size: 12, color: theme.iconSecondary)
            }
            accessory
            Text(title).font(.pp(.label)).foregroundColor(theme.textSecondary)
            if let count { Text("\(count)").font(.pp(.caption)).foregroundColor(theme.textTertiary) }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, M.sm)
        .frame(height: M.rowHeightCompact)
        .contentShape(Rectangle())
        .onTapGesture { onToggle?() }
    }
}

extension VCSGroupHeader where Accessory == EmptyView {
    init(title: String, count: Int? = nil, collapsed: Bool? = nil, onToggle: (() -> Void)? = nil) {
        self.init(title: title, count: count, collapsed: collapsed, onToggle: onToggle) { EmptyView() }
    }
}

/// 三态复选框（分组全选）
struct VCSTriCheckbox: View {
    /// true 全选、false 全不选、nil 部分
    let state: Bool?
    let action: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        Button(action: action) {
            ZStack {
                RoundedRectangle(cornerRadius: M.radiusXS).fill(state == false ? theme.bgInput : theme.accent)
                RoundedRectangle(cornerRadius: M.radiusXS).strokeBorder(state == false ? theme.borderStrong : theme.accent, lineWidth: 1)
                if state == true { PPIcon("check", size: 12, color: theme.textOnAccent) }
                if state == nil { PPIcon("minus", size: 12, color: theme.textOnAccent) }
            }
            .frame(width: 14, height: 14)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

/// 大对话框外壳：与 PPDialog 同样的外观，但内容区占满给定尺寸、不包滚动（差异、日志等自带滚动的内容用）
struct VCSLargeDialog<Content: View, Footer: View>: View {
    let id: String
    let title: String
    var maxWidth: CGFloat = 1180
    var maxHeight: CGFloat = 820
    @ViewBuilder let content: Content
    @ViewBuilder let footer: Footer
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(title).font(.pp(.title)).lineLimit(1).truncationMode(.middle)
                Spacer()
                PPIconButton("x", help: tr("common.close"), size: M.iconButtonSmall) { AppModel.shared.dialogs.dismiss(id) }
            }
            .padding(.leading, M.xl)
            .padding(.trailing, M.md)
            .frame(height: 44)
            PPDivider(subtle: true)
            content.frame(maxWidth: .infinity, maxHeight: .infinity)
            PPDivider(subtle: true)
            HStack(spacing: M.md) {
                Spacer()
                footer
            }
            .padding(.horizontal, M.xl)
            .padding(.vertical, M.md)
        }
        .frame(maxWidth: maxWidth, maxHeight: maxHeight)
        .background(RoundedRectangle(cornerRadius: M.radiusXL, style: .continuous).fill(theme.bgElevated))
        .clipShape(RoundedRectangle(cornerRadius: M.radiusXL, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: M.radiusXL, style: .continuous).strokeBorder(theme.border, lineWidth: 1))
        // 阴影单独画在内容后面的形状上，不作用于内容里的原生控件
        .background(RoundedRectangle(cornerRadius: M.radiusXL, style: .continuous).fill(theme.bgElevated)
            .shadow(color: .black.opacity(theme.isDark ? 0.5 : 0.22), radius: 24, y: 10))
        .padding(M.huge)
    }
}

extension VCSLargeDialog where Footer == EmptyView {
    init(id: String, title: String, maxWidth: CGFloat = 1180, maxHeight: CGFloat = 820, @ViewBuilder content: () -> Content) {
        self.init(id: id, title: title, maxWidth: maxWidth, maxHeight: maxHeight, content: content, footer: { EmptyView() })
    }
}

/// 插件贡献的 vcs.* 动作：带上下文参数（root、hash/hashes、path/paths）调 project.action
@MainActor
enum VCSPluginActions {
    static func actions(_ location: String) -> [ActionInfo] {
        AppModel.shared.data.contributions.actions(at: location)
    }

    static func entries(_ location: String, repo: VCSRepo, args: [String: JSONValue]) -> [MenuEntry] {
        actions(location).map { a in
            .item(MenuItem(a.title, iconRef: IconSource.parse(a.icon, pluginId: a.plugin)) {
                invoke(a, repo: repo, args: args)
            })
        }
    }

    static func invoke(_ a: ActionInfo, repo: VCSRepo, args: [String: JSONValue]) {
        var base = args
        base["root"] = .string(repo.root)
        let send: ([String: JSONValue]) async throws -> Void = { extra in
            let api = try AppModel.shared.client()
            let moduleId = try await repo.firstModuleId()
            var all = base
            for (k, v) in extra { all[k] = v }
            let t = try await api.invokeAction(projectId: repo.projectId, moduleId: moduleId, plugin: a.plugin,
                                               action: a.id, args: all)
            AppModel.shared.track(t)
        }
        if !a.form.isEmpty {
            Dialogs.form(title: a.title, message: a.confirm, fields: a.form, ok: a.title) { v in try await send(v) }
        } else if let c = a.confirm, !c.isEmpty {
            Dialogs.confirm(title: a.title, message: c, ok: a.title) { try await send([:]) }
        } else {
            Task {
                do { try await send([:]) } catch { AppModel.shared.toasts.error(error, title: a.title) }
            }
        }
    }
}

extension View {
    /// 悬停时显示的手型指针以外，统一的「可点击行」形状
    func vcsHitArea() -> some View { contentShape(Rectangle()) }
}

/// 当前按下的修饰键（行点击时判断多选）
enum VCSModifiers {
    static var command: Bool { NSEvent.modifierFlags.contains(.command) }
    static var shift: Bool { NSEvent.modifierFlags.contains(.shift) }
}

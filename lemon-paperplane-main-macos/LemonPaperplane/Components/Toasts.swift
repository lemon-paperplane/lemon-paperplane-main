import PaperplaneKit
import SwiftUI

/// 右下角轻提示。info/success 4 秒、warning/error 8 秒自动消失。
@MainActor
final class ToastStore: ObservableObject {
    struct Toast: Identifiable, Equatable {
        let id = UUID()
        let level: String // info | success | warning | error
        let title: String
        var message: String?
        var actionTitle: String?
        var action: (() -> Void)?

        static func == (a: Toast, b: Toast) -> Bool { a.id == b.id }
    }

    @Published private(set) var toasts: [Toast] = []

    func show(_ level: String, _ title: String, message: String? = nil, actionTitle: String? = nil, action: (() -> Void)? = nil) {
        let t = Toast(level: level, title: title, message: message, actionTitle: actionTitle, action: action)
        toasts.append(t)
        if toasts.count > 4 { toasts.removeFirst(toasts.count - 4) }
        let motion = DesignSystem.shared.tokens?.motion
        let secs = (level == "error" || level == "warning") ? (motion?["toastErrorSeconds"] ?? 8) : (motion?["toastSeconds"] ?? 4)
        let id = t.id
        DispatchQueue.main.asyncAfter(deadline: .now() + secs) { [weak self] in self?.dismiss(id) }
    }

    func error(_ error: Error, title: String? = nil) {
        if error is CancellationError { return }
        show("error", title ?? tr("common.failed"), message: error.localizedDescription)
    }

    func dismiss(_ id: UUID) { toasts.removeAll { $0.id == id } }
}

struct ToastHost: View {
    @ObservedObject var store: ToastStore
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .trailing, spacing: M.md) {
            ForEach(store.toasts) { t in
                ToastView(toast: t) { store.dismiss(t.id) }
                    .transition(.move(edge: .trailing).combined(with: .opacity))
            }
        }
        .padding(M.xl)
        .animation(.easeOut(duration: 0.16), value: store.toasts)
    }
}

private struct ToastView: View {
    let toast: ToastStore.Toast
    let close: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        let tone = toast.level == "error" ? "danger" : toast.level
        let c = theme.tone(tone)
        HStack(alignment: .top, spacing: M.md) {
            PPIcon(icon, size: M.icon, color: c.fg)
            VStack(alignment: .leading, spacing: M.xxs) {
                Text(toast.title).font(.pp(.bodyStrong)).fixedSize(horizontal: false, vertical: true)
                if let m = toast.message, !m.isEmpty {
                    Text(m).font(.pp(.small)).foregroundColor(theme.textSecondary)
                        .lineLimit(6).fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                }
                if let a = toast.actionTitle, let action = toast.action {
                    PPButton(a, kind: .link) { action(); close() }.padding(.top, M.xxs)
                }
            }
            Spacer(minLength: 0)
            PPIconButton("x", help: tr("common.close"), size: M.iconButtonSmall, iconSize: M.iconSmall, action: close)
        }
        .padding(M.md)
        .frame(width: 340, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: M.radiusLG).fill(theme.bgElevated)
            .shadow(color: .black.opacity(theme.isDark ? 0.45 : 0.16), radius: 12, y: 4))
        .overlay(RoundedRectangle(cornerRadius: M.radiusLG).strokeBorder(theme.border, lineWidth: 1))
    }

    private var icon: String {
        switch toast.level {
        case "error": return "circle-alert"
        case "warning": return "triangle-alert"
        case "success": return "circle-check"
        default: return "info"
        }
    }
}

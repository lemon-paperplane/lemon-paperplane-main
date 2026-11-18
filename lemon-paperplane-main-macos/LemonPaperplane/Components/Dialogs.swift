import PaperplaneKit
import SwiftUI

// 对话框：主窗口内的模态层（遮罩 + 居中卡片），外观三端一致。Esc 取消，回车触发主按钮。

@MainActor
final class DialogStore: ObservableObject {
    struct Entry: Identifiable {
        let id: String
        let view: AnyView
        let onCancel: (() -> Void)?
    }

    @Published private(set) var stack: [Entry] = []

    func present<V: View>(id: String = UUID().uuidString, onCancel: (() -> Void)? = nil, @ViewBuilder _ content: () -> V) {
        AppDelegate.shared?.showMainWindow()
        PopupController.shared.dismissAll()
        stack.removeAll { $0.id == id }
        stack.append(Entry(id: id, view: AnyView(content()), onCancel: onCancel))
    }

    func dismiss(_ id: String) { stack.removeAll { $0.id == id } }

    func contains(_ id: String) -> Bool { stack.contains { $0.id == id } }

    func cancelTop() {
        guard let top = stack.last else { return }
        stack.removeLast()
        top.onCancel?()
    }
}

/// 对话框外壳：标题栏、可滚动内容、底部按钮区
struct PPDialog<Content: View, Footer: View>: View {
    let title: String
    var width: CGFloat = M.dialogWidth
    var onClose: (() -> Void)?
    @ViewBuilder let content: Content
    @ViewBuilder let footer: Footer
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(title).font(.pp(.title)).lineLimit(1)
                Spacer()
                if let onClose {
                    PPIconButton("x", help: tr("common.close"), size: M.iconButtonSmall, action: onClose)
                }
            }
            .padding(.leading, M.xl)
            .padding(.trailing, M.md)
            .frame(height: 44)
            ScrollView {
                content
                    .padding(.horizontal, M.xl)
                    .padding(.vertical, M.md)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxHeight: 560)
            .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: M.md) {
                Spacer()
                footer
            }
            .padding(.horizontal, M.xl)
            .padding(.vertical, M.lg)
        }
        .frame(width: width)
        // 阴影只加在背景形状上：加在整个容器上时，SwiftUI 会给里面每个原生控件（输入框、滚动视图）各画一份阴影，看起来像浮在对话框上
        .background(RoundedRectangle(cornerRadius: M.radiusXL, style: .continuous).fill(theme.bgElevated)
            .shadow(color: .black.opacity(theme.isDark ? 0.5 : 0.22), radius: 24, y: 10))
        .overlay(RoundedRectangle(cornerRadius: M.radiusXL, style: .continuous).strokeBorder(theme.border, lineWidth: 1))
    }
}

/// 主窗口里的对话框层
struct DialogHost: View {
    @ObservedObject var store: DialogStore
    @Environment(\.theme) private var theme

    var body: some View {
        ZStack {
            if let top = store.stack.last {
                theme.scrim
                    .ignoresSafeArea()
                    .contentShape(Rectangle())
                    .onTapGesture {}
                top.view
                    .id(top.id)
                    .transition(.opacity.combined(with: .scale(scale: 0.98)))
            }
        }
        .animation(.easeOut(duration: 0.12), value: store.stack.map(\.id))
    }
}

// MARK: - 常用对话框

/// 确认框
struct ConfirmDialog: View {
    let id: String
    let title: String
    var message: String?
    var okTitle: String
    var danger = false
    let onConfirm: () async throws -> Void
    var onCancel: (() -> Void)?
    @State private var busy = false
    @State private var error: String?
    @Environment(\.theme) private var theme

    var body: some View {
        PPDialog(title: title, width: 420, onClose: cancel) {
            VStack(alignment: .leading, spacing: M.md) {
                if let message, !message.isEmpty {
                    Text(message).fixedSize(horizontal: false, vertical: true)
                }
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel"), action: cancel).keyboardShortcut(.cancelAction)
            PPButton(okTitle, kind: danger ? .danger : .primary, busy: busy) { submit() }
                .keyboardShortcut(.defaultAction)
        }
    }

    private func cancel() {
        AppModel.shared.dialogs.dismiss(id)
        onCancel?()
    }

    private func submit() {
        busy = true
        error = nil
        Task {
            do {
                try await onConfirm()
                AppModel.shared.dialogs.dismiss(id)
            } catch {
                self.error = error.localizedDescription
            }
            busy = false
        }
    }
}

/// 单行输入框对话框（新建工作区、重命名）
struct TextInputDialog: View {
    let id: String
    let title: String
    let label: String
    @State var text: String
    var okTitle: String
    let onSubmit: (String) async throws -> Void
    @State private var busy = false
    @State private var error: String?

    var body: some View {
        PPDialog(title: title, width: 420, onClose: { AppModel.shared.dialogs.dismiss(id) }) {
            VStack(alignment: .leading, spacing: M.sm) {
                Text(label).font(.pp(.label))
                PPTextField("", text: $text, onSubmit: submit)
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel")) { AppModel.shared.dialogs.dismiss(id) }.keyboardShortcut(.cancelAction)
            PPButton(okTitle, kind: .primary, busy: busy, action: submit)
                .keyboardShortcut(.defaultAction)
                .disabled(text.trimmingCharacters(in: .whitespaces).isEmpty)
        }
    }

    private func submit() {
        let v = text.trimmingCharacters(in: .whitespaces)
        guard !v.isEmpty, !busy else { return }
        busy = true
        error = nil
        Task {
            do {
                try await onSubmit(v)
                AppModel.shared.dialogs.dismiss(id)
            } catch {
                self.error = error.localizedDescription
            }
            busy = false
        }
    }
}

/// 表单对话框（动作表单、插件 prompt、运行配置）
struct FormDialog: View {
    let id: String
    let title: String
    var message: String?
    let fields: [FormField]
    @State var values: [String: JSONValue]
    var optionValues: [String: [Option]] = [:]
    var okTitle: String
    var danger = false
    var width: CGFloat = M.dialogWidth
    let onSubmit: ([String: JSONValue]) async throws -> Void
    var onCancel: (() -> Void)?
    @State private var busy = false
    @State private var error: String?
    @State private var showErrors = false

    var body: some View {
        PPDialog(title: title, width: width, onClose: cancel) {
            VStack(alignment: .leading, spacing: M.lg) {
                if let message, !message.isEmpty {
                    Text(message).fixedSize(horizontal: false, vertical: true)
                }
                if !fields.isEmpty {
                    FormView(fields: fields, values: $values, optionValues: optionValues, showErrors: showErrors)
                }
                if let error { PPAlert(tone: "danger", title: error) }
            }
        } footer: {
            PPButton(tr("common.cancel"), action: cancel).keyboardShortcut(.cancelAction)
            PPButton(okTitle, kind: danger ? .danger : .primary, busy: busy, action: submit)
                .keyboardShortcut(.defaultAction)
        }
    }

    private func cancel() {
        AppModel.shared.dialogs.dismiss(id)
        onCancel?()
    }

    private func submit() {
        guard !busy else { return }
        if !FormView.missingRequired(fields, values).isEmpty {
            showErrors = true
            return
        }
        busy = true
        error = nil
        Task {
            do {
                try await onSubmit(FormView.normalized(fields, values))
                AppModel.shared.dialogs.dismiss(id)
            } catch {
                self.error = error.localizedDescription
            }
            busy = false
        }
    }
}

/// 打开对话框的快捷入口
@MainActor
enum Dialogs {
    static func confirm(title: String, message: String? = nil, ok: String? = nil, danger: Bool = false,
                        onCancel: (() -> Void)? = nil, onConfirm: @escaping () async throws -> Void) {
        let id = UUID().uuidString
        AppModel.shared.dialogs.present(id: id, onCancel: onCancel) {
            ConfirmDialog(id: id, title: title, message: message, okTitle: ok ?? tr("common.ok"), danger: danger,
                          onConfirm: onConfirm, onCancel: onCancel)
        }
    }

    static func input(title: String, label: String, initial: String = "", ok: String? = nil,
                      onSubmit: @escaping (String) async throws -> Void) {
        let id = UUID().uuidString
        AppModel.shared.dialogs.present(id: id) {
            TextInputDialog(id: id, title: title, label: label, text: initial, okTitle: ok ?? tr("common.ok"), onSubmit: onSubmit)
        }
    }

    static func form(id: String = UUID().uuidString, title: String, message: String? = nil, fields: [FormField],
                     values: [String: JSONValue] = [:], optionValues: [String: [Option]] = [:], ok: String? = nil,
                     danger: Bool = false, onCancel: (() -> Void)? = nil,
                     onSubmit: @escaping ([String: JSONValue]) async throws -> Void) {
        AppModel.shared.dialogs.present(id: id, onCancel: onCancel) {
            FormDialog(id: id, title: title, message: message, fields: fields,
                       values: FormView.initialValues(fields, values), optionValues: optionValues,
                       okTitle: ok ?? tr("common.ok"), danger: danger, onSubmit: onSubmit, onCancel: onCancel)
        }
    }
}

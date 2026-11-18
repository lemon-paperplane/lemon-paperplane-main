import Foundation
import PaperplaneKit

/// daemon 发来的对话框（插件 host/prompt、git 凭据 askpass）：按 Fields 渲染，回答走 ui.respond。
@MainActor
enum PromptPresenter {
    static func dialogId(_ promptId: String) -> String { "prompt-" + promptId }

    static func present(_ p: Prompt) {
        let id = dialogId(p.id)
        let model = AppModel.shared
        guard !model.dialogs.contains(id) else { return }
        let cancel: () -> Void = {
            Task { try? await model.client().respondPrompt(id: p.id, cancelled: true, values: [:]) }
        }
        let ok = p.okText?.isEmpty == false ? p.okText! : tr("common.ok")
        if p.fields.isEmpty {
            model.dialogs.present(id: id, onCancel: cancel) {
                ConfirmDialog(id: id, title: p.title, message: p.message, okTitle: ok, danger: p.danger, onConfirm: {
                    try await model.client().respondPrompt(id: p.id, cancelled: false, values: [:])
                }, onCancel: cancel)
            }
        } else {
            Dialogs.form(id: id, title: p.title, message: p.message, fields: p.fields, ok: ok, danger: p.danger,
                         onCancel: cancel) { values in
                try await model.client().respondPrompt(id: p.id, cancelled: false, values: values)
            }
        }
    }

    /// 已在别处回答（另一个界面或超时）
    static func dismiss(_ promptId: String) {
        AppModel.shared.dialogs.dismiss(dialogId(promptId))
    }
}

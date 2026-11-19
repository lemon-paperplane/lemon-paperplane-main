using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>内核发来的对话框（插件 host/prompt、git 凭据 askpass）：按 Fields 渲染，回答走 ui.respond（对应 Mac PromptPresenter）。</summary>
public static class PromptPresenter
{
    public static string DialogId(string promptId) => "prompt-" + promptId;

    public static void Present(Prompt p)
    {
        var id = DialogId(p.Id);
        var model = AppModel.Shared;
        if (model.Dialogs.Contains(id)) return;
        void Cancel()
        {
            _ = Respond(p.Id, true, new Dictionary<string, JToken>());
        }
        var ok = string.IsNullOrEmpty(p.OkText) ? L.T("common.ok") : p.OkText!;
        if (p.Fields.Count == 0)
        {
            // 用 prompt 的 id 管理，另一端回答后能关掉
            Dialogs.Present(id, new ConfirmDialog(id, p.Title, p.Message, ok, p.Danger,
                () => Respond(p.Id, false, new Dictionary<string, JToken>(), raise: true), Cancel), Cancel);
        }
        else
        {
            Dialogs.Form(p.Title, p.Fields, values => Respond(p.Id, false, values, raise: true), message: p.Message, ok: ok,
                danger: p.Danger, id: id, onCancel: Cancel);
        }
    }

    private static async Task Respond(string promptId, bool cancelled, Dictionary<string, JToken> values, bool raise = false)
    {
        try { await AppModel.Shared.Client().RespondPromptAsync(promptId, cancelled, values); }
        catch when (!raise) { }
    }

    /// <summary>已在别处回答（另一个界面或超时）。</summary>
    public static void Dismiss(string promptId) => Dialogs.Dismiss(DialogId(promptId));
}

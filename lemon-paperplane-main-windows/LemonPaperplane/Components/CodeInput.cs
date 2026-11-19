using System;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using PaperplaneKit;

namespace LemonPaperplane;

/// <summary>
/// 代码输入框（CodeMirror，带语法着色、行号、撤销、括号补全）：用于「命令」这类多行代码字段。对应 Mac PPCodeInput。
/// 网页是编辑器工程的 snippet.html（内核 /editor/ 提供，和编辑窗口共用 CodeMirror 内核）。
/// 内存：每个输入框一个 WebView2（InPrivate 会话），视图离开即释放。
/// 固定高度，内容多了在输入框内部滚动（不随内容伸缩：伸缩会让所在的对话框一跳一跳地变高）。
/// Ctrl+Enter = onSubmit，Esc = onEscape。没装 WebView2 运行时时退回普通多行文本框（等宽）。
///
/// 用法注意：会创建 WebView2，父视图重建时请用 Keep("…", () =&gt; new PPCodeInput(…)) 复用同一个实例；外部改了绑定值后调 Sync()。
/// </summary>
public sealed class PPCodeInput : PPView
{
    private readonly Bind<string> _text;
    private readonly Action? _onSubmit;
    private readonly Action? _onEscape;
    private readonly InputChrome? _chrome;
    private readonly PPWebView? _web;
    private readonly PPTextArea? _fallback;
    /// <summary>最近一次和网页同步过的值（避免回写循环）。</summary>
    private string? _lastSent;

    public PPCodeInput(Bind<string> text, string? placeholder = null, string? language = null, double height = 160,
                       Action? onSubmit = null, Action? onEscape = null, bool invalid = false)
    {
        _text = text;
        _onSubmit = onSubmit;
        _onEscape = onEscape;
        var lang = string.IsNullOrEmpty(language) ? "shell" : language!;
        if (!WebView2Env.IsAvailable)
        {
            _fallback = new PPTextArea(text, placeholder ?? "", mono: true, minHeight: height) { Height = height, Invalid = invalid };
            return;
        }
        _web = new PPWebView("snippet", () => Url(lang, placeholder ?? ""), ColorToken.BgInput)
        {
            HideUntilReady = true, // 网页就绪前不显示，避免闪白
            CornerRadius = M.RadiusMD - 1,
            ErrorIcon = "square-terminal",
        };
        _web.Ready += () =>
        {
            SetValue(_text.Value ?? "");
        };
        _web.Message += OnMessage;
        _web.ThemeChanged += dark => _web.Exec("window.paperplaneSnippet.setTheme(" + (dark ? "'dark'" : "'light'") + ")");
        // 外框：bgInput + 1px 描边，聚焦时 borderFocus + 外圈 focusRing（聚焦状态来自网页的 focus 消息）
        _chrome = new InputChrome(_web, height)
        {
            ContentPadding = new Thickness(1, 0, 1, 0), // 网页在 1px 描边以内，左右再各让 1px（和 Mac 的左右 2px 一致）
            FocusedOverride = false,
            Invalid = invalid,
        };
        _chrome.Id("codeinput");
    }

    /// <summary>红框（必填没填等；构造时也可以用 invalid 参数给）。</summary>
    public bool Invalid
    {
        get => _chrome?.Invalid ?? _fallback?.Invalid ?? false;
        set
        {
            if (_chrome != null) _chrome.Invalid = value;
            if (_fallback != null) _fallback.Invalid = value;
        }
    }

    protected override UIElement Build() => (UIElement?)_chrome ?? _fallback!;

    /// <summary>外部改了绑定值后调用：推给网页。</summary>
    public void Sync()
    {
        if (_fallback != null) { _fallback.Sync(); return; }
        if (_web is { IsReady: true } && _lastSent != (_text.Value ?? "")) SetValue(_text.Value ?? "");
    }

    /// <summary>把焦点放进输入框。</summary>
    public void FocusInput()
    {
        if (_fallback != null) { _fallback.TextBox.Focus(); return; }
        _web?.FocusPage();
        _web?.Exec("window.paperplaneSnippet && window.paperplaneSnippet.focus()");
    }

    private static string? Url(string language, string placeholder)
    {
        var api = AppModel.Shared.Api;
        if (api == null) return null;
        var b = api.BaseUrl.AbsoluteUri.TrimEnd('/');
        return b + "/editor/snippet.html#lang=" + Uri.EscapeDataString(language) + "&theme=" + (Theme.IsDark ? "dark" : "light")
               + "&placeholder=" + Uri.EscapeDataString(placeholder);
    }

    private void SetValue(string v)
    {
        _lastSent = v;
        _web?.Exec("window.paperplaneSnippet.setValue(" + WebView2Env.Js(v) + ")");
    }

    private void OnMessage(JObject m)
    {
        switch ((string?)m["type"])
        {
            case "change":
            {
                var v = (string?)m["value"] ?? "";
                _lastSent = v;
                if (_text.Value != v) _text.Value = v;
                break;
            }
            case "focus":
                if (_chrome != null) _chrome.FocusedOverride = (bool?)m["value"] ?? false;
                break;
            case "submit":
                _onSubmit?.Invoke();
                break;
            case "escape":
                _onEscape?.Invoke();
                break;
        }
    }
}

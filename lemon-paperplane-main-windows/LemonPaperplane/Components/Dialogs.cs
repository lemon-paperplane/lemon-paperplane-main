using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LemonPaperplane;

// 对话框：主窗口内的模态层（scrim 遮罩 + 居中卡片），外观三端一致。Esc 取消，回车触发主按钮。对应 Mac 的 Dialogs.swift。

/// <summary>对话框栈（只显示最上面一个）。主窗口的 DialogHost 显示它。对应 Mac 的 DialogStore（AppModel.shared.dialogs）。</summary>
public sealed class DialogStore
{
    public static readonly DialogStore Shared = new();

    public sealed class Entry
    {
        public string Id = "";
        public UIElement View = null!;
        public Action? OnCancel;
    }

    private readonly List<Entry> _stack = new();
    public IReadOnlyList<Entry> Stack => _stack;
    public Entry? Top => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

    public event EventHandler? Changed;

    /// <summary>主窗口要先显示出来（托盘状态下弹对话框时）。由主窗口设置。</summary>
    public Action? ShowMainWindow { get; set; }

    public void Present(string id, UIElement view, Action? onCancel = null)
    {
        ShowMainWindow?.Invoke();
        PopupController.DismissAll();
        _stack.RemoveAll(e => e.Id == id);
        _stack.Add(new Entry { Id = id, View = view, OnCancel = onCancel });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dismiss(string id)
    {
        if (_stack.RemoveAll(e => e.Id == id) > 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Contains(string id) => _stack.Any(e => e.Id == id);

    public void CancelTop()
    {
        var top = Top;
        if (top == null) return;
        _stack.RemoveAt(_stack.Count - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        top.OnCancel?.Invoke();
    }
}

/// <summary>
/// 对话框外壳：标题栏 44 + 关闭、可滚动内容（最高 560）、底部按钮右对齐（取消在左，主按钮在右）。
/// bgElevated、圆角 12、dialog 阴影、1px border。对应 Mac 的 PPDialog。
/// 回车 / Esc：DialogHost 找最上面对话框的 DefaultButton / CancelButton（或 OnSubmit / OnCancel）。
/// </summary>
public sealed class PPDialog : Grid
{
    public PPButton? DefaultButton { get; set; }
    public PPButton? CancelButton { get; set; }
    public Action? OnSubmit { get; set; }
    public Action? OnCancel { get; set; }

    public PPDialog(string title, UIElement content, IEnumerable<UIElement> footer, double width = 0, Action? onClose = null)
    {
        Width = width > 0 ? width : M.DialogWidth;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        OnCancel = onClose;
        var header = UI.HStack(0, UI.Text(title, Typo.Title).Id("dialog.title"), UI.Spacer(),
            onClose != null ? new PPIconButton("x", L.T("common.close"), onClose, size: M.IconButtonSmall).Id("dialog.close") : null);
        header.Height = 44;
        header.Margin = new Thickness(M.Xl, 0, M.Md, 0);
        var body = new Border { Padding = new Thickness(M.Xl, M.Md, M.Xl, M.Md), Child = content };
        var scroll = UI.Scroll(body);
        scroll.MaxHeight = 560;
        var foot = UI.HStack(M.Md, UI.Spacer());
        foreach (var b in footer) foot.Children.Add(b);
        foot.Margin = new Thickness(M.Xl, M.Lg, M.Xl, M.Lg);
        var col = UI.VStack(0, header, scroll, foot);
        col.Align = CrossAlign.Stretch;
        var shadow = new Border { CornerRadius = new CornerRadius(M.RadiusXL) }.Bg(ColorToken.BgElevated).Shadow(ShadowToken.Dialog);
        var box = new Border
        {
            CornerRadius = new CornerRadius(M.RadiusXL),
            BorderThickness = new Thickness(1),
            Child = col,
            SnapsToDevicePixels = true,
        }.Bg(ColorToken.BgElevated).Outline(ColorToken.Border);
        Children.Add(shadow);
        Children.Add(box);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Contained);
        this.Id("dialog");
    }
}

/// <summary>主窗口里的对话框层：遮罩 + 最上面的对话框（淡入 + 0.98→1 缩放，120ms）；回车 / Esc 处理；打开时把焦点移进对话框。</summary>
public sealed class DialogHost : Grid
{
    private readonly DialogStore _store;
    private readonly Border _scrim;
    private readonly Grid _layer;
    private string? _shownId;

    public DialogHost(DialogStore store)
    {
        _store = store;
        _scrim = new Border().Bg(ColorToken.Scrim);
        _scrim.MouseDown += (_, e) => e.Handled = true; // 遮罩吃掉点击
        _layer = new Grid();
        Children.Add(_scrim);
        Children.Add(_layer);
        Visibility = Visibility.Collapsed;
        Templates.ApplyRootDefaults(this);
        _store.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(Refresh));
        Refresh();
    }

    private void Refresh()
    {
        var top = _store.Top;
        if (top == null)
        {
            _layer.Children.Clear();
            _shownId = null;
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        if (_shownId == top.Id && _layer.Children.Count == 1 && ReferenceEquals(_layer.Children[0], top.View)) return;
        _shownId = top.Id;
        _layer.Children.Clear();
        UI.Detach(top.View);
        _layer.Children.Add(top.View);
        // 淡入 + 轻微放大
        var el = top.View;
        el.Opacity = 0;
        var st = new ScaleTransform(0.98, 0.98);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = st;
        var d = TimeSpan.FromMilliseconds(120);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusFirst(el)));
    }

    private static void FocusFirst(UIElement root)
    {
        var target = PPView.Descendants<UIElement>(root).FirstOrDefault(e => (e is TextBox tb && !tb.IsReadOnly) || e is PasswordBox);
        if (target != null)
        {
            Keyboard.Focus(target);
            // 和 Mac 一样：打开时全选已有文字（重命名直接输入新名字）
            if (target is TextBox t) t.SelectAll();
            else if (target is PasswordBox p) p.SelectAll();
        }
        else
        {
            var dlg = PPView.Descendants<PPDialog>(root).FirstOrDefault();
            if (dlg?.DefaultButton != null) Keyboard.Focus(dlg.DefaultButton);
            else root.Focus();
        }
    }

    /// <summary>窗口按键（主窗口 PreviewKeyDown 里调用）：Esc 取消、回车主按钮。返回 true 表示已处理。</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_store.Top == null) return false;
        var dlg = PPView.Descendants<PPDialog>(_layer).FirstOrDefault();
        if (e.Key == Key.Escape)
        {
            if (dlg?.CancelButton != null && dlg.CancelButton.IsEnabled) Invoke(dlg.CancelButton);
            else if (dlg?.OnCancel != null) dlg.OnCancel();
            else _store.CancelTop();
            return true;
        }
        if (e.Key == Key.Enter && KeyInput.Modifiers == ModifierKeys.None)
        {
            var focused = Keyboard.FocusedElement;
            if (focused is ButtonBase) return false; // 焦点在按钮上：回车按这个按钮
            if (focused is TextBox tb && tb.AcceptsReturn) return false;
            if (dlg?.DefaultButton != null)
            {
                if (dlg.DefaultButton.IsEnabled) Invoke(dlg.DefaultButton);
                return true;
            }
            if (dlg?.OnSubmit != null) { dlg.OnSubmit(); return true; }
        }
        return false;
    }

    private static void Invoke(ButtonBase b)
    {
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer((Button)b);
        (peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) as System.Windows.Automation.Provider.IInvokeProvider)?.Invoke();
    }
}

/// <summary>
/// 带「提交中 / 错误」状态的对话框基类：Submit 跑异步动作，成功关闭，失败在对话框里显示错误。
/// 确认框、输入框、表单对话框都基于它。
/// </summary>
public abstract class AsyncDialog : PPView
{
    protected readonly string DialogId;
    protected readonly State<bool> Busy;
    protected readonly State<string?> Error;

    protected AsyncDialog(string id)
    {
        DialogId = id;
        Busy = UseState(false);
        Error = UseState<string?>(null);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected async void Run(Func<Task> action)
    {
        if (Busy.Value) return;
        Busy.Value = true;
        Error.Value = null;
        try
        {
            await action();
            DialogStore.Shared.Dismiss(DialogId);
        }
        catch (OperationCanceledException)
        {
            // 取消不算失败
        }
        catch (Exception e)
        {
            Error.Value = e.Message;
        }
        finally
        {
            Busy.Value = false;
        }
    }

    protected PPAlert? ErrorAlert => Error.Value is string msg ? new PPAlert("danger", msg) : null;
}

/// <summary>确认框（宽 420）。对应 Mac 的 ConfirmDialog。</summary>
public sealed class ConfirmDialog : AsyncDialog
{
    private readonly string _title;
    private readonly string? _message;
    private readonly string _ok;
    private readonly bool _danger;
    private readonly Func<Task> _onConfirm;
    private readonly Action? _onCancel;

    public ConfirmDialog(string id, string title, string? message, string ok, bool danger, Func<Task> onConfirm, Action? onCancel) : base(id)
    {
        _title = title;
        _message = message;
        _ok = ok;
        _danger = danger;
        _onConfirm = onConfirm;
        _onCancel = onCancel;
    }

    private void Cancel()
    {
        DialogStore.Shared.Dismiss(DialogId);
        _onCancel?.Invoke();
    }

    protected override UIElement Build()
    {
        var cancel = new PPButton(L.T("common.cancel"), Cancel).Id("dialog.cancel");
        var ok = new PPButton(_ok, () => Run(_onConfirm), kind: _danger ? PPButtonKind.Danger : PPButtonKind.Primary, busy: Busy.Value).Id("dialog.ok");
        var content = UI.VStack(M.Md, !string.IsNullOrEmpty(_message) ? UI.Paragraph(_message) : null, ErrorAlert);
        content.Align = CrossAlign.Stretch;
        return new PPDialog(_title, content, new UIElement[] { cancel, ok }, 420, Cancel) { DefaultButton = ok, CancelButton = cancel };
    }
}

/// <summary>单行输入框对话框（新建工作区、重命名）。对应 Mac 的 TextInputDialog。</summary>
public sealed class TextInputDialog : AsyncDialog
{
    private readonly string _title;
    private readonly string _label;
    private readonly string _okTitle;
    private readonly Func<string, Task> _onSubmit;
    // 不订阅输入值：打字时不重建对话框（输入框不会被摘下重挂，输入法组字不受影响），只更新确定按钮
    private readonly State<string> _text;
    private PPButton? _ok;

    public TextInputDialog(string id, string title, string label, string initial, string ok, Func<string, Task> onSubmit) : base(id)
    {
        _title = title;
        _label = label;
        _okTitle = ok;
        _onSubmit = onSubmit;
        _text = new State<string>(initial);
    }

    private void Submit()
    {
        var v = _text.Value.Trim();
        if (v.Length == 0 || Busy.Value) return;
        Run(() => _onSubmit(v));
    }

    private void UpdateOk()
    {
        if (_ok != null) _ok.IsEnabled = !Busy.Value && _text.Value.Trim().Length > 0;
    }

    protected override UIElement Build()
    {
        // 输入框跨重建（提交中、出错）复用同一个实例
        var field = Keep("text", () =>
        {
            var tf = new PPTextField("", _text, onSubmit: Submit).Id("dialog.input");
            tf.TextChanged += _ => UpdateOk();
            return tf;
        });
        var cancel = new PPButton(L.T("common.cancel"), () => DialogStore.Shared.Dismiss(DialogId)).Id("dialog.cancel");
        _ok = new PPButton(_okTitle, Submit, kind: PPButtonKind.Primary, busy: Busy.Value).Id("dialog.ok");
        UpdateOk();
        var content = UI.VStack(M.Sm, UI.Text(_label, Typo.Label), field.FillW(), ErrorAlert);
        content.Align = CrossAlign.Stretch;
        return new PPDialog(_title, content, new UIElement[] { cancel, _ok }, 420, () => DialogStore.Shared.Dismiss(DialogId))
        {
            DefaultButton = _ok,
            CancelButton = cancel,
        };
    }
}

/// <summary>
/// 表单对话框外壳（动作表单、插件 prompt、运行配置）：fields 由表单渲染器提供（FormView），validate 返回 false 时不提交（渲染器自己标红）。
/// 表单渲染器做好后在 Dialogs 的另一个 partial 文件里补 Dialogs.Form(title, message, fields, values, optionValues, ok, danger, onCancel, onSubmit)，
/// 里面用这个外壳：Dialogs.Present(id, new FormDialogShell(…), onCancel)。
/// </summary>
public sealed class FormDialogShell : AsyncDialog
{
    private readonly string _title;
    private readonly string? _message;
    private readonly Func<UIElement> _fields;
    private readonly Func<bool> _validate;
    private readonly Func<Task> _submit;
    private readonly string _ok;
    private readonly bool _danger;
    private readonly double _width;
    private readonly Action? _onCancel;
    private UIElement? _fieldsEl;

    public FormDialogShell(string id, string title, string? message, Func<UIElement> fields, Func<bool> validate, Func<Task> submit,
                           string? ok = null, bool danger = false, double width = 0, Action? onCancel = null) : base(id)
    {
        _title = title;
        _message = message;
        _fields = fields;
        _validate = validate;
        _submit = submit;
        _ok = ok ?? L.T("common.ok");
        _danger = danger;
        _width = width > 0 ? width : M.DialogWidth;
        _onCancel = onCancel;
    }

    private void Cancel()
    {
        DialogStore.Shared.Dismiss(DialogId);
        _onCancel?.Invoke();
    }

    private void Submit()
    {
        if (Busy.Value || !_validate()) return;
        Run(_submit);
    }

    protected override UIElement Build()
    {
        // 表单只建一次（输入中的状态在表单自己的视图里），重建只换按钮与错误提示
        _fieldsEl ??= _fields();
        UI.Detach(_fieldsEl);
        var cancel = new PPButton(L.T("common.cancel"), Cancel).Id("dialog.cancel");
        var ok = new PPButton(_ok, Submit, kind: _danger ? PPButtonKind.Danger : PPButtonKind.Primary, busy: Busy.Value).Id("dialog.ok");
        var content = UI.VStack(M.Lg, !string.IsNullOrEmpty(_message) ? UI.Paragraph(_message) : null, _fieldsEl, ErrorAlert);
        content.Align = CrossAlign.Stretch;
        return new PPDialog(_title, content, new UIElement[] { cancel, ok }, _width, Cancel) { DefaultButton = ok, CancelButton = cancel };
    }
}

/// <summary>打开对话框的快捷入口。对应 Mac 的 Dialogs。Form 由表单渲染器在另一个 partial 文件里补上。</summary>
public static partial class Dialogs
{
    /// <summary>确认框：Dialogs.Confirm(L.T("…"), message, async () =&gt; await api.…, ok: L.T("common.delete"), danger: true)。onConfirm 抛异常时在框里显示错误。</summary>
    public static void Confirm(string title, string? message, Func<Task> onConfirm, string? ok = null, bool danger = false, Action? onCancel = null)
    {
        var id = Guid.NewGuid().ToString("N");
        Present(id, new ConfirmDialog(id, title, message, ok ?? L.T("common.ok"), danger, onConfirm, onCancel), onCancel);
    }

    /// <summary>同步版本。</summary>
    public static void Confirm(string title, string? message, Action onConfirm, string? ok = null, bool danger = false, Action? onCancel = null) =>
        Confirm(title, message, () => { onConfirm(); return Task.CompletedTask; }, ok, danger, onCancel);

    /// <summary>单行输入：Dialogs.Input(L.T("workspace.new"), L.T("common.name"), async name =&gt; …)。</summary>
    public static void Input(string title, string label, Func<string, Task> onSubmit, string initial = "", string? ok = null)
    {
        var id = Guid.NewGuid().ToString("N");
        Present(id, new TextInputDialog(id, title, label, initial, ok ?? L.T("common.ok"), onSubmit));
    }

    /// <summary>显示任意对话框视图（视图里用 PPDialog 做外壳，关闭时 DialogStore.Shared.Dismiss(id)）。</summary>
    public static void Present(string id, UIElement view, Action? onCancel = null) => DialogStore.Shared.Present(id, view, onCancel);

    public static void Dismiss(string id) => DialogStore.Shared.Dismiss(id);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LemonPaperplane;

/// <summary>右下角轻提示。info/success 4 秒、warning/error 8 秒自动消失，最多 4 条。对应 Mac 的 ToastStore（AppModel.shared.toasts）。</summary>
public sealed class ToastStore
{
    public static readonly ToastStore Shared = new();

    public sealed class Toast
    {
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>info | success | warning | error</summary>
        public string Level { get; init; } = "info";
        public string Title { get; init; } = "";
        public string? Message { get; init; }
        public string? ActionTitle { get; init; }
        public Action? Action { get; init; }
    }

    private readonly List<Toast> _toasts = new();
    public IReadOnlyList<Toast> Toasts => _toasts;

    public event Action<Toast>? Added;
    public event Action<Guid>? Removed;

    /// <summary>ToastStore.Shared.Show("success", L.T("common.copied"))。可以在任何线程调用。</summary>
    public void Show(string level, string title, string? message = null, string? actionTitle = null, Action? action = null)
    {
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => Show(level, title, message, actionTitle, action)));
            return;
        }
        var t = new Toast { Level = level, Title = title, Message = message, ActionTitle = actionTitle, Action = action };
        _toasts.Add(t);
        Added?.Invoke(t);
        while (_toasts.Count > 4) Dismiss(_toasts[0].Id);
        double secs = level is "error" or "warning" ? M.ToastErrorSeconds : M.ToastSeconds;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
        timer.Tick += (_, _) => { timer.Stop(); Dismiss(t.Id); };
        timer.Start();
    }

    /// <summary>失败提示（取消不提示）。</summary>
    public void Error(Exception error, string? title = null)
    {
        if (error is OperationCanceledException) return;
        Show("error", title ?? L.T("common.failed"), error.Message);
    }

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0) Removed?.Invoke(id);
    }
}

/// <summary>主窗口右下角的提示层（宽 340，新提示从右侧滑入淡入）。</summary>
public sealed class ToastHost : Stack
{
    private readonly ToastStore _store;
    private readonly Dictionary<Guid, FrameworkElement> _views = new();

    public ToastHost(ToastStore store) : base(Orientation.Vertical, M.Md)
    {
        _store = store;
        Align = CrossAlign.End;
        Margin = new Thickness(M.Xl);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Templates.ApplyRootDefaults(this);
        foreach (var t in store.Toasts) Add(t, animate: false);
        Loaded += (_, _) =>
        {
            _store.Added -= OnAdded;
            _store.Removed -= OnRemoved;
            _store.Added += OnAdded;
            _store.Removed += OnRemoved;
        };
        Unloaded += (_, _) =>
        {
            _store.Added -= OnAdded;
            _store.Removed -= OnRemoved;
        };
    }

    private void OnAdded(ToastStore.Toast t) => Add(t, animate: true);

    private void OnRemoved(Guid id)
    {
        if (!_views.TryGetValue(id, out var v)) return;
        _views.Remove(id);
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(M.MotionNormal)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        a.Completed += (_, _) => Children.Remove(v);
        v.BeginAnimation(OpacityProperty, a);
    }

    private void Add(ToastStore.Toast t, bool animate)
    {
        var v = new ToastView(t, () => _store.Dismiss(t.Id));
        _views[t.Id] = v;
        Children.Add(v);
        if (!animate) return;
        var d = TimeSpan.FromMilliseconds(M.MotionNormal);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var move = new TranslateTransform(24, 0);
        v.RenderTransform = move;
        v.Opacity = 0;
        v.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, d) { EasingFunction = ease });
    }
}

/// <summary>一条提示：语气图标、粗体标题、说明（最多 6 行、可选中）、可选链接按钮、关闭。</summary>
public sealed class ToastView : Grid
{
    public ToastView(ToastStore.Toast toast, Action close)
    {
        string tone = toast.Level == "error" ? "danger" : toast.Level;
        var c = Theme.Tone(tone);
        string icon = toast.Level switch { "error" => "circle-alert", "warning" => "triangle-alert", "success" => "circle-check", _ => "info" };
        var text = UI.VStack(M.Xxs, UI.Paragraph(toast.Title, Typo.BodyStrong).Id("toast.title"));
        if (!string.IsNullOrEmpty(toast.Message))
        {
            var m = UI.Selectable(toast.Message, Typo.Small, ColorToken.TextSecondary);
            m.MaxHeight = Typo.Small.LineHeight * 6 + 2;
            text.Children.Add(m);
        }
        if (toast.ActionTitle != null && toast.Action != null)
        {
            var action = toast.Action;
            text.Children.Add(new PPButton(toast.ActionTitle, () => { action(); close(); }, kind: PPButtonKind.Link).Margins(0, M.Xxs, 0, 0));
        }
        text.Align = CrossAlign.Stretch;
        text.Shrink();
        var row = UI.HStack(M.Md,
            new PPIcon(icon, M.Icon, c.Fg).Top().Margins(0, 1, 0, 0),
            text.Flex(),
            new PPIconButton("x", L.T("common.close"), close, size: M.IconButtonSmall, iconSize: M.IconSmall).Top().Id("toast.close"));
        row.Align = CrossAlign.Start;
        var box = new Border { Padding = new Thickness(M.Md), CornerRadius = new CornerRadius(M.RadiusLG), BorderThickness = new Thickness(1), Child = row, SnapsToDevicePixels = true }
            .Bg(ColorToken.BgElevated).Outline(ColorToken.Border);
        var shadow = new Border { CornerRadius = new CornerRadius(M.RadiusLG) }.Bg(ColorToken.BgElevated).Shadow(ShadowToken.Toast);
        Children.Add(shadow);
        Children.Add(box);
        Width = 340;
        this.Id("toast");
    }
}

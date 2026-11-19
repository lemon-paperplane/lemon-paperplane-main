using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LemonPaperplane;

/// <summary>
/// 输入框外框：bgInput + 1px borderStrong、圆角 6；聚焦时 borderFocus + 外圈 2px focusRing；invalid 红框；禁用 bgInputDisabled。
/// 默认跟随内部元素的键盘焦点（IsKeyboardFocusWithin）。对应 Mac 的 InputChrome。
/// </summary>
public class InputChrome : Grid, IVisualStateful
{
    private readonly Border _ring;
    protected readonly Border Box;
    private bool _invalid;
    private bool? _focusedOverride;

    public InputChrome(UIElement? content = null, double? height = null)
    {
        _ring = new Border
        {
            BorderThickness = new Thickness(M.FocusRingWidth),
            CornerRadius = new CornerRadius(M.RadiusMD + 2),
            Margin = new Thickness(-2),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        }.Outline(ColorToken.FocusRing, M.FocusRingWidth);
        Box = new Border
        {
            CornerRadius = new CornerRadius(M.RadiusMD),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(M.Md, 0, M.Md, 0),
            Child = content,
            SnapsToDevicePixels = true,
        };
        Children.Add(_ring);
        Children.Add(Box);
        double h = height ?? M.ControlHeight;
        if (h > 0) Height = h;
        else MinHeight = M.ControlHeight;
        VerticalAlignment = VerticalAlignment.Center;
        IsKeyboardFocusWithinChanged += (_, _) => UpdateVisual();
        IsEnabledChanged += (_, _) => UpdateVisual();
        UpdateVisual();
    }

    public UIElement? Content
    {
        get => Box.Child;
        set => Box.Child = value;
    }

    public Thickness ContentPadding
    {
        get => Box.Padding;
        set => Box.Padding = value;
    }

    public bool Invalid
    {
        get => _invalid;
        set { _invalid = value; UpdateVisual(); }
    }

    /// <summary>手动指定聚焦外观（下拉打开时用）；null 表示跟随键盘焦点。</summary>
    public bool? FocusedOverride
    {
        get => _focusedOverride;
        set { _focusedOverride = value; UpdateVisual(); }
    }

    public bool IsFocusedLook => _focusedOverride ?? (IsKeyboardFocusWithin || PPDebug.Is(this, "focus"));

    public virtual void UpdateVisual()
    {
        bool f = IsFocusedLook;
        Box.Bg(IsEnabled ? ColorToken.BgInput : ColorToken.BgInputDisabled);
        Box.Outline(_invalid ? ColorToken.Danger : f ? ColorToken.BorderFocus : ColorToken.BorderStrong);
        _ring.Visibility = f ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>
/// 单行输入：可带前置图标、清除按钮、等宽、密码；回车 onSubmit，Esc onEscape。对应 Mac 的 PPTextField。
/// new PPTextField(L.T("common.filter"), q, icon: "search", clearable: true)
/// </summary>
public sealed class PPTextField : InputChrome
{
    private readonly Bind<string> _bind;
    private readonly TextBox? _textBox;
    private readonly PasswordBox? _password;
    private readonly TextBlock _placeholder;
    private readonly FrameworkElement? _clear;
    private readonly bool _clearable;
    private bool _syncing;

    public Action? OnSubmit { get; set; }
    public Action? OnEscape { get; set; }

    /// <summary>文字变化（用户输入或 Text 赋值）。</summary>
    public event Action<string>? TextChanged;

    public PPTextField(string placeholder, Bind<string> text, string? icon = null, bool mono = false, bool secure = false,
                       bool invalid = false, bool clearable = false, double height = 0, Action? onSubmit = null, Action? onEscape = null)
        : base(null, height > 0 ? height : M.ControlHeight)
    {
        _bind = text;
        _clearable = clearable;
        OnSubmit = onSubmit;
        OnEscape = onEscape;
        var font = mono ? Typo.Mono : Typo.Body;
        Control input;
        if (secure)
        {
            _password = new PasswordBox { Password = text.Value ?? "", PasswordChar = '\u2022' };
            _password.PasswordChanged += (_, _) => Changed(_password.Password);
            input = _password;
        }
        else
        {
            _textBox = new TextBox { Text = text.Value ?? "", AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap };
            _textBox.TextChanged += (_, _) => Changed(_textBox.Text);
            input = _textBox;
        }
        font.Apply(input);
        input.VerticalAlignment = VerticalAlignment.Stretch;
        input.VerticalContentAlignment = VerticalAlignment.Center;
        input.PreviewKeyDown += OnInputKey;
        _placeholder = UI.Text(placeholder, font, ColorToken.TextTertiary);
        _placeholder.IsHitTestVisible = false;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.Margin = new Thickness(2, 0, 0, 0); // TextBox 文字自带约 2px 左边距
        var field = new Grid();
        field.Children.Add(_placeholder);
        field.Children.Add(input);
        field.Flex();
        var row = UI.HStack(M.Sm);
        row.Align = CrossAlign.Stretch;
        if (icon != null) row.Children.Add(new PPIcon(icon, M.IconSmall, ColorToken.IconSecondary).VCenter());
        row.Children.Add(field);
        if (clearable)
        {
            _clear = UI.Box(new PPIcon("x", M.IconSmall)).Fg(ColorToken.IconSecondary).VCenter()
                .OnClick(() => { Text = ""; Focus(); }).Id("textfield.clear");
            _clear.Cursor = Cursors.Arrow;
            row.Children.Add(_clear);
        }
        Content = row;
        Invalid = invalid;
        Cursor = Cursors.IBeam;
        UpdateChrome();
    }

    /// <summary>内部 TextBox（密码框时为 null）。</summary>
    public TextBox? TextBox => _textBox;
    public PasswordBox? PasswordBox => _password;

    public string Text
    {
        get => _textBox?.Text ?? _password?.Password ?? "";
        set
        {
            value ??= "";
            if (Text == value) return;
            if (_textBox != null) { _textBox.Text = value; _textBox.CaretIndex = value.Length; }
            else if (_password != null) _password.Password = value;
        }
    }

    /// <summary>从绑定值刷新（外部改了值时调用；内容相同时不动光标）。</summary>
    public void Sync()
    {
        _syncing = true;
        try { Text = _bind.Value ?? ""; }
        finally { _syncing = false; }
        UpdateChrome();
    }

    public new bool Focus()
    {
        Control? c = (Control?)_textBox ?? _password;
        return c != null && c.Focus();
    }

    public void SelectAll()
    {
        _textBox?.SelectAll();
        _password?.SelectAll();
    }

    private void Changed(string value)
    {
        UpdateChrome();
        if (_syncing) return;
        _bind.Value = value;
        TextChanged?.Invoke(value);
    }

    private void UpdateChrome()
    {
        bool empty = Text.Length == 0;
        _placeholder.Visibility = empty ? Visibility.Visible : Visibility.Hidden;
        if (_clear != null) _clear.Visibility = _clearable && !empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OnSubmit != null && KeyInput.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            OnSubmit();
        }
        else if (e.Key == Key.Escape && OnEscape != null)
        {
            e.Handled = true;
            OnEscape();
        }
    }
}

/// <summary>多行文本（最小高 72）。对应 Mac 的 PPTextArea。</summary>
public sealed class PPTextArea : InputChrome
{
    private readonly Bind<string> _bind;
    private readonly TextBlock _placeholder;
    private bool _syncing;

    public TextBox TextBox { get; }

    public PPTextArea(Bind<string> text, string placeholder = "", bool mono = false, double minHeight = 72)
        : base(null, 0)
    {
        _bind = text;
        MinHeight = minHeight;
        ContentPadding = new Thickness(M.Xs);
        TextBox = new TextBox
        {
            Text = text.Value ?? "",
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(M.Xs - 2, 2, M.Xs, 2),
        };
        (mono ? Typo.Mono : Typo.Body).Apply(TextBox);
        TextBox.TextChanged += (_, _) =>
        {
            _placeholder!.Visibility = TextBox.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            if (!_syncing) _bind.Value = TextBox.Text;
        };
        _placeholder = UI.Text(placeholder, mono ? Typo.Mono : Typo.Body, ColorToken.TextTertiary);
        _placeholder.IsHitTestVisible = false;
        _placeholder.VerticalAlignment = VerticalAlignment.Top;
        _placeholder.Margin = new Thickness(M.Xs, 2, 0, 0);
        _placeholder.Visibility = TextBox.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        var g = new Grid();
        g.Children.Add(_placeholder);
        g.Children.Add(TextBox);
        Content = g;
        Cursor = Cursors.IBeam;
    }

    public void Sync()
    {
        _syncing = true;
        try { if (TextBox.Text != (_bind.Value ?? "")) TextBox.Text = _bind.Value ?? ""; }
        finally { _syncing = false; }
    }
}

/// <summary>数字输入（等宽，非数字时红框，值为 double?）。对应 Mac 的 PPNumberField。</summary>
public static class PPNumberField
{
    public static PPTextField Make(string placeholder, Bind<double?> value, bool clearable = false)
    {
        PPTextField? field = null;
        var text = Bind.Of(
            () => value.Value is double d ? d.ToString(Math.Round(d) == d ? "0" : "R", CultureInfo.InvariantCulture) : "",
            s =>
            {
                var t = s.Trim();
                bool ok = double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d);
                if (field != null) field.Invalid = t.Length > 0 && !ok;
                value.Value = t.Length == 0 ? null : ok ? d : value.Value;
            });
        field = new PPTextField(placeholder, text, mono: true, clearable: clearable);
        return field;
    }
}

/// <summary>开关：30×18 胶囊，开 accent、关 switchOff，白色圆钮（120ms 滑动）。对应 Mac 的 PPSwitch。</summary>
public sealed class PPSwitch : PPButtonBase
{
    private readonly Bind<bool> _bind;
    private readonly Border _track;
    private readonly Border _ring;
    private readonly TranslateTransform _knobMove = new();

    public PPSwitch(Bind<bool> isOn, string? label = null)
    {
        _bind = isOn;
        _track = new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(9), SnapsToDevicePixels = true };
        var knob = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = _knobMove,
        }.Bg(ColorToken.TextOnAccent).Shadow(ShadowToken.Hairline);
        var g = new Grid();
        g.Children.Add(_track);
        g.Children.Add(knob);
        _ring = MakeFocusRing(9);
        var root = new Grid();
        root.Children.Add(_ring);
        root.Children.Add(g);
        Content = label == null ? root : (UIElement)UI.HStack(M.Sm, root, UI.Text(label)).Bg(ColorToken.Clear);
        this.A11y(label);
        _knobMove.X = isOn.Value ? 12 : 0;
        UpdateVisual();
    }

    public bool IsOn => _bind.Value;

    protected override void OnClick()
    {
        _bind.Value = !_bind.Value;
        var anim = new DoubleAnimation(_bind.Value ? 12 : 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        _knobMove.BeginAnimation(TranslateTransform.XProperty, anim);
        UpdateVisual();
        base.OnClick();
    }

    public override void UpdateVisual()
    {
        if (_track == null) return;
        _track.Bg(_bind.Value ? ColorToken.Accent : ColorToken.SwitchOff);
        Opacity = IsEnabled ? 1 : 0.5;
        _ring.Visibility = FocusVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>外部改了值时刷新（不播动画）。</summary>
    public void Sync()
    {
        _knobMove.BeginAnimation(TranslateTransform.XProperty, null);
        _knobMove.X = _bind.Value ? 12 : 0;
        UpdateVisual();
    }
}

/// <summary>复选框：16×16，圆角 3，选中强调色 + 白勾。对应 Mac 的 PPCheckbox。</summary>
public sealed class PPCheckbox : PPButtonBase
{
    private readonly Bind<bool> _bind;
    private readonly Border _box;
    private readonly PPIcon _check;
    private readonly Border _ring;

    public PPCheckbox(Bind<bool> isOn, string? label = null)
    {
        _bind = isOn;
        _check = new PPIcon("check", 12, ColorToken.TextOnAccent);
        _box = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(M.RadiusXS), BorderThickness = new Thickness(1), Child = _check, SnapsToDevicePixels = true };
        _ring = MakeFocusRing(M.RadiusXS);
        var boxRoot = new Grid();
        boxRoot.Children.Add(_ring);
        boxRoot.Children.Add(_box);
        var row = UI.HStack(M.Sm, boxRoot, label != null ? UI.Text(label) : null).Bg(ColorToken.Clear);
        Content = row;
        this.A11y(label);
        UpdateVisual();
    }

    protected override void OnClick()
    {
        _bind.Value = !_bind.Value;
        UpdateVisual();
        base.OnClick();
    }

    public void Sync() => UpdateVisual();

    public override void UpdateVisual()
    {
        if (_box == null) return;
        bool on = _bind.Value;
        _box.Bg(on ? ColorToken.Accent : ColorToken.BgInput);
        _box.Outline(on ? ColorToken.Accent : ColorToken.BorderStrong);
        _check.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        Opacity = IsEnabled ? 1 : 0.5;
        _ring.Visibility = FocusVisible ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>单选框（分组选项）：16 圆，选中强调色粗环。对应 Mac 的 PPRadio。</summary>
public sealed class PPRadio : PPButtonBase
{
    private readonly Border _circle;
    private readonly Border _ring;
    private bool _selected;

    public PPRadio(bool selected, string label, Action? onClick = null)
    {
        _selected = selected;
        Action = onClick;
        _circle = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), SnapsToDevicePixels = true }.Bg(ColorToken.BgInput);
        _ring = MakeFocusRing(8);
        var g = new Grid();
        g.Children.Add(_ring);
        g.Children.Add(_circle);
        Content = UI.HStack(M.Sm, g, UI.Text(label)).Bg(ColorToken.Clear);
        this.A11y(label);
        UpdateVisual();
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; UpdateVisual(); }
    }

    public override void UpdateVisual()
    {
        if (_circle == null) return;
        _circle.Outline(_selected ? ColorToken.Accent : ColorToken.BorderStrong, _selected ? 5 : 1);
        Opacity = IsEnabled ? 1 : 0.5;
        _ring.Visibility = FocusVisible ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>分段控件：neutralSubtle 底，选中段 bgSurface 底 + 细阴影。对应 Mac 的 PPSegmented。</summary>
public sealed class PPSegmented<T> : Border
{
    private readonly Bind<T> _bind;
    private readonly List<(T Value, Segment Button)> _segments = new();

    /// <summary>new PPSegmented&lt;string&gt;(new[] { ("a", "A"), ("b", "B") }, selection)</summary>
    public PPSegmented(IEnumerable<(T Value, string Title)> options, Bind<T> selection, PPButtonSize size = PPButtonSize.Regular)
    {
        _bind = selection;
        CornerRadius = new CornerRadius(M.RadiusMD);
        Padding = new Thickness(2);
        SnapsToDevicePixels = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        this.Bg(ColorToken.NeutralSubtle);
        var row = UI.HStack(2);
        foreach (var (v, title) in options)
        {
            var seg = new Segment(title, size.Height() - 4) { Action = () => Select(v) };
            seg.Id("segment." + title);
            _segments.Add((v, seg));
            row.Children.Add(seg);
        }
        Child = row;
        Sync();
    }

    private void Select(T v)
    {
        _bind.Value = v;
        Sync();
    }

    /// <summary>按绑定值刷新选中段。</summary>
    public void Sync()
    {
        var cur = _bind.Value;
        foreach (var (v, b) in _segments) b.Selected = EqualityComparer<T>.Default.Equals(v, cur);
    }

    private sealed class Segment : PPButtonBase
    {
        private readonly Border _bg;
        private readonly Border _shadow;
        private readonly Border _ring;
        private readonly TextBlock _text;
        private bool _selected;

        public Segment(string title, double height)
        {
            _text = UI.Text(title, Typo.Label).Shrink(false);
            _text.HorizontalAlignment = HorizontalAlignment.Center;
            _text.VerticalAlignment = VerticalAlignment.Center;
            _shadow = new Border { CornerRadius = new CornerRadius(M.RadiusSM), Visibility = Visibility.Collapsed }.Bg(ColorToken.BgSurface).Shadow(ShadowToken.Hairline);
            _bg = new Border { CornerRadius = new CornerRadius(M.RadiusSM), Padding = new Thickness(M.Lg, 0, M.Lg, 0), Height = height, Child = _text, SnapsToDevicePixels = true };
            _ring = MakeFocusRing(M.RadiusSM);
            var g = new Grid();
            g.Children.Add(_ring);
            g.Children.Add(_shadow);
            g.Children.Add(_bg);
            Content = g;
            this.A11y(title);
            UpdateVisual();
        }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; UpdateVisual(); }
        }

        public override void UpdateVisual()
        {
            if (_bg == null) return;
            _shadow.Visibility = _selected ? Visibility.Visible : Visibility.Collapsed;
            _bg.Bg(_selected ? ColorToken.BgSurface : ColorToken.Clear);
            _bg.Fg(_selected ? ColorToken.TextPrimary : ColorToken.TextSecondary);
            Opacity = IsEnabled ? 1 : 0.5;
            _ring.Visibility = FocusVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

/// <summary>标签栏的一项。</summary>
public sealed class PPTab<T>
{
    public T Value { get; }
    public string Title { get; }
    public IconKey? Icon { get; }
    public int? Badge { get; }

    public PPTab(T value, string title, string? icon = null, int? badge = null, IconKey? iconRef = null)
    {
        Value = value;
        Title = title;
        Icon = iconRef ?? (icon != null ? IconKey.Builtin(icon) : null);
        Badge = badge;
    }
}

/// <summary>下划线标签栏（工程详情、插件页）：高 36，选中粗体 + 2px 强调色下划线，可带图标和数量。对应 Mac 的 PPTabBar。</summary>
public sealed class PPTabBar<T> : Border
{
    private readonly Bind<T> _bind;
    private readonly List<(T Value, TabButton Button)> _buttons = new();

    public PPTabBar(IEnumerable<PPTab<T>> tabs, Bind<T> selection)
    {
        _bind = selection;
        Height = M.TabBarHeight;
        var row = UI.HStack(M.Xs);
        row.Align = CrossAlign.Stretch;
        foreach (var t in tabs)
        {
            var b = new TabButton(t) { Action = () => Select(t.Value) };
            b.Id("tab." + t.Value);
            _buttons.Add((t.Value, b));
            row.Children.Add(b);
        }
        Child = row;
        Sync();
    }

    private void Select(T v)
    {
        _bind.Value = v;
        Sync();
    }

    public void Sync()
    {
        var cur = _bind.Value;
        foreach (var (v, b) in _buttons) b.Selected = EqualityComparer<T>.Default.Equals(v, cur);
    }

    private sealed class TabButton : PPButtonBase
    {
        private readonly Border _bg;
        private readonly Border _line;
        private readonly TextBlock _text;
        private readonly Border _ring;
        private bool _selected;

        public TabButton(PPTab<T> tab)
        {
            _text = UI.Text(tab.Title, Typo.Body).Shrink(false);
            var row = UI.HStack(M.Sm, tab.Icon != null ? new PPIcon(tab.Icon, M.IconSmall) : null, _text,
                tab.Badge is int n && n > 0 ? new PPCountBadge(n) : null);
            row.HorizontalAlignment = HorizontalAlignment.Center;
            _bg = new Border { CornerRadius = new CornerRadius(M.RadiusSM), Padding = new Thickness(M.Md, 0, M.Md, 0), Height = M.TabBarHeight - 6, Child = row, SnapsToDevicePixels = true };
            _ring = MakeFocusRing(M.RadiusSM);
            var top = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            top.Children.Add(_ring);
            top.Children.Add(_bg);
            _line = new Border { Height = 2, SnapsToDevicePixels = true };
            var col = new Grid();
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            col.Children.Add(top.At(0, 0));
            col.Children.Add(_line.At(1, 0));
            col.Bg(ColorToken.Clear);
            Content = col;
            VerticalAlignment = VerticalAlignment.Stretch;
            this.A11y(tab.Title);
            UpdateVisual();
        }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; UpdateVisual(); }
        }

        public override void UpdateVisual()
        {
            if (_bg == null) return;
            (_selected ? Typo.BodyStrong : Typo.Body).Apply(_text);
            _bg.Fg(_selected ? ColorToken.TextPrimary : ColorToken.TextSecondary);
            _bg.Bg(Hover && !_selected ? ColorToken.BgHover : ColorToken.Clear);
            _line.Bg(_selected ? ColorToken.Accent : ColorToken.Clear);
            _ring.Visibility = FocusVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

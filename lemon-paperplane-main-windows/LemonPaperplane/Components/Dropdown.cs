using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LemonPaperplane;

/// <summary>下拉选项。</summary>
public sealed class DropdownOption<T>
{
    public T Value { get; }
    public string Title { get; }
    public IconKey? Icon { get; }
    public string? Detail { get; }

    public DropdownOption(T value, string title, string? icon = null, string? detail = null, IconKey? iconRef = null)
    {
        Value = value;
        Title = title;
        Icon = iconRef ?? (icon != null ? IconKey.Builtin(icon) : null);
        Detail = detail;
    }
}

/// <summary>
/// 下拉选择（自绘）：外观同输入框，右侧 chevrons-up-down；弹出列表勾选当前项、悬停强调色底；选项多于 12 个时带筛选框。
/// 键盘：聚焦时上下键 / 回车 / 空格打开；列表里上下移动、回车选择、Esc 关闭。对应 Mac 的 PPDropdown。
/// new PPDropdown&lt;string&gt;(new[] { new DropdownOption&lt;string&gt;("a", "A") }, selection)
/// </summary>
public sealed class PPDropdown<T> : PPButtonBase
{
    private readonly List<DropdownOption<T>> _options;
    private readonly Bind<T> _bind;
    private readonly string _placeholder;
    private readonly bool _borderless;
    private readonly Border _chrome;
    private readonly Border _ring;
    private readonly Stack _row;
    private PPPopup? _open;

    public PPDropdown(IEnumerable<DropdownOption<T>> options, Bind<T> selection, string placeholder = "", PPButtonSize size = PPButtonSize.Regular,
                      double minWidth = 120, double maxWidth = double.PositiveInfinity, bool fullWidth = false, bool borderless = false)
    {
        _options = options.ToList();
        _bind = selection;
        _placeholder = placeholder;
        _borderless = borderless;
        _row = UI.HStack(M.Sm);
        _chrome = new Border
        {
            Height = size.Height(),
            Padding = new Thickness(M.Md, 0, M.Md, 0),
            CornerRadius = new CornerRadius(M.RadiusMD),
            BorderThickness = new Thickness(1),
            Child = _row,
            SnapsToDevicePixels = true,
        };
        _ring = MakeFocusRing(M.RadiusMD);
        var root = new Grid();
        root.Children.Add(_ring);
        root.Children.Add(_chrome);
        Content = root;
        MinWidth = minWidth;
        MaxWidth = maxWidth;
        if (fullWidth) HorizontalAlignment = HorizontalAlignment.Stretch;
        Font = size == PPButtonSize.Small ? Typo.Small : Typo.Body;
        Action = Toggle;
        PreviewKeyDown += OnKey;
        Sync();
    }

    private Typo Font { get; }

    public bool IsOpen => _open != null;

    /// <summary>按绑定值刷新显示。</summary>
    public void Sync()
    {
        var cur = Current;
        _row.Children.Clear();
        if (cur?.Icon is IconKey k) _row.Children.Add(new PPIcon(k, M.IconSmall));
        var title = UI.TextMiddle(cur?.Title ?? _placeholder, Font, cur == null ? ColorToken.TextTertiary : ColorToken.TextPrimary);
        _row.Children.Add(title);
        _row.Children.Add(UI.Spacer(M.Xs));
        _row.Children.Add(new PPIcon("chevrons-up-down", M.IconSmall, ColorToken.IconSecondary));
        this.A11y(cur?.Title ?? _placeholder);
        UpdateVisual();
    }

    private DropdownOption<T>? Current => _options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, _bind.Value));

    private void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        if (PopupController.WasJustDismissed(this)) return;
        var cur = Current;
        var list = new DropdownList<T>(_options, cur != null ? cur.Value : default, cur != null, v =>
        {
            _bind.Value = v;
            _open?.Close();
            Sync();
        });
        _open = PopupController.Present(this, list, wantsKey: _options.Count > 12, onClose: () => { _open = null; UpdateVisual(); });
        _open.KeyHandler = list.HandleKey;
        UpdateVisual();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_open != null || !IsEnabled) return;
        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            Toggle();
        }
    }

    public override void UpdateVisual()
    {
        if (_chrome == null) return;
        bool open = _open != null || PPDebug.Is(this, "open");
        _chrome.Bg(_borderless ? ColorToken.Clear : IsEnabled ? ColorToken.BgInput : ColorToken.BgInputDisabled);
        _chrome.Outline(_borderless ? null : open ? ColorToken.BorderFocus : ColorToken.BorderStrong);
        if (_borderless && Hover) _chrome.Bg(ColorToken.BgHover);
        _chrome.Opacity = IsEnabled ? 1 : 0.6;
        _ring.Visibility = FocusVisible && !open ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>下拉弹出的列表。</summary>
internal sealed class DropdownList<T> : Border
{
    private readonly List<DropdownOption<T>> _options;
    private readonly T? _selected;
    private readonly bool _hasSelection;
    private readonly Action<T> _pick;
    private readonly Stack _list;
    private readonly ScrollViewer _scroll;
    private readonly List<(DropdownOption<T> Option, Border Row, PPIcon Check, TextBlock Title, TextBlock? Detail)> _rows = new();
    private int _hl = -1;
    private int _selectedIndex = -1;

    public DropdownList(List<DropdownOption<T>> options, T? selected, bool hasSelection, Action<T> pick)
    {
        _options = options;
        _selected = selected;
        _hasSelection = hasSelection;
        _pick = pick;
        _list = UI.VStack(0);
        _list.Align = CrossAlign.Stretch;
        _list.Margin = new Thickness(M.Xs);
        _scroll = UI.Scroll(_list);
        _scroll.MaxHeight = 320;
        var col = UI.VStack(0);
        col.Align = CrossAlign.Stretch;
        if (options.Count > 12)
        {
            var filter = new State<string>("");
            var field = new PPTextField(L.T("common.filter"), filter, icon: "search", height: M.ControlHeightSmall);
            field.Margin = new Thickness(M.Xs, M.Xs, M.Xs, 0);
            field.TextChanged += Fill;
            col.Children.Add(field);
        }
        col.Children.Add(_scroll);
        Child = new FitWidth(col, 200, 420);
        Fill("");
    }

    private void Fill(string filter)
    {
        _list.Children.Clear();
        _rows.Clear();
        _hl = -1;
        _selectedIndex = -1;
        var shown = filter.Length == 0 ? _options : _options.Where(o => CultureInfo.CurrentCulture.CompareInfo.IndexOf(o.Title, filter, CompareOptions.IgnoreCase) >= 0).ToList();
        foreach (var o in shown)
        {
            bool sel = _hasSelection && EqualityComparer<T>.Default.Equals(o.Value, _selected!);
            var check = new PPIcon("check", M.IconSmall) { Opacity = sel ? 1 : 0 };
            var title = UI.Text(o.Title);
            TextBlock? detail = o.Detail != null ? UI.Text(o.Detail, Typo.Small).Shrink(false) : null;
            var h = UI.HStack(M.Md, check, o.Icon is IconKey k ? new PPIcon(k, M.Icon) : null, title, UI.Spacer(M.Lg), detail);
            var row = new Border
            {
                Height = M.RowHeight,
                Padding = new Thickness(M.Md, 0, M.Md, 0),
                CornerRadius = new CornerRadius(M.RadiusSM),
                Child = h,
                SnapsToDevicePixels = true,
            };
            row.A11y(o.Title);
            int index = _rows.Count;
            var value = o.Value;
            row.MouseEnter += (_, _) => Highlight(index);
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; _pick(value); };
            row.SetValue(UI.ClickActionProperty, (Action)(() => _pick(value)));
            _rows.Add((o, row, check, title, detail));
            _list.Children.Add(row);
            Paint(index, false);
            if (sel) _selectedIndex = index; // 和 Mac 一样打开时不预先高亮；键盘上下从当前项开始
        }
    }

    private void Paint(int i, bool hl)
    {
        var r = _rows[i];
        r.Row.Bg(hl ? ColorToken.Accent : ColorToken.Clear);
        r.Row.Fg(hl ? ColorToken.TextOnAccent : ColorToken.TextPrimary);
        r.Check.Fg(hl ? ColorToken.TextOnAccent : ColorToken.Accent);
        if (r.Detail != null)
        {
            r.Detail.Fg(hl ? ColorToken.TextOnAccent : ColorToken.TextTertiary);
            r.Detail.Opacity = hl ? 0.85 : 1;
        }
    }

    private void Highlight(int i)
    {
        if (_hl == i) return;
        if (_hl >= 0 && _hl < _rows.Count) Paint(_hl, false);
        _hl = i;
        if (i >= 0 && i < _rows.Count)
        {
            Paint(i, true);
            _rows[i].Row.BringIntoView();
        }
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (_rows.Count == 0) return false;
        switch (e.Key)
        {
            case Key.Down: Highlight(_hl < 0 ? (_selectedIndex >= 0 ? _selectedIndex : 0) : Math.Min(_rows.Count - 1, _hl + 1)); return true;
            case Key.Up: Highlight(_hl < 0 ? (_selectedIndex >= 0 ? _selectedIndex : _rows.Count - 1) : Math.Max(0, _hl - 1)); return true;
            case Key.Enter:
                if (_hl >= 0) _pick(_rows[_hl].Option.Value);
                return true;
            default:
                return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LemonPaperplane;

// 自绘菜单：下拉菜单、「更多」菜单、右键菜单共用，支持子菜单与键盘（上下、回车、左右、Esc）。对应 Mac 的 Menu.swift。
// 项高 28、圆角 4、悬停强调色底白字、左侧 16px 图标位、右侧快捷键、子菜单向右弹出、分隔线、分组标题、危险项红字。

/// <summary>菜单项（Mac 的 MenuItem；改名是为了不和 WPF 的 System.Windows.Controls.MenuItem 混）。</summary>
public sealed class PPMenuItem
{
    public string Title { get; set; }
    public IconKey? Icon { get; set; }
    /// <summary>右侧显示的快捷键文字（"Ctrl+N"，只是显示；快捷键本身另外注册）。</summary>
    public string? Shortcut { get; set; }
    public bool Destructive { get; set; }
    public bool Disabled { get; set; }
    public bool Checked { get; set; }
    public IReadOnlyList<MenuEntry>? Children { get; set; }
    public Action? Action { get; set; }

    public PPMenuItem(string title, Action? action = null, string? icon = null, string? shortcut = null, bool destructive = false,
                      bool disabled = false, bool @checked = false, IconKey? iconRef = null)
    {
        Title = title;
        Action = action;
        Icon = iconRef ?? (icon != null ? IconKey.Builtin(icon) : null);
        Shortcut = shortcut;
        Destructive = destructive;
        Disabled = disabled;
        Checked = @checked;
    }
}

public enum MenuEntryKind { Item, Separator, Header }

/// <summary>菜单条目：项 / 分隔线 / 分组标题。</summary>
public sealed class MenuEntry
{
    public MenuEntryKind Kind { get; }
    public PPMenuItem? Data { get; }
    public string? HeaderText { get; }

    private MenuEntry(MenuEntryKind kind, PPMenuItem? data = null, string? header = null)
    {
        Kind = kind;
        Data = data;
        HeaderText = header;
    }

    /// <summary>MenuEntry.Item(L.T("common.delete"), () =&gt; …, icon: "trash-2", destructive: true)</summary>
    public static MenuEntry Item(string title, Action action, string? icon = null, string? shortcut = null, bool destructive = false,
                                 bool disabled = false, bool @checked = false) =>
        new(MenuEntryKind.Item, new PPMenuItem(title, action, icon, shortcut, destructive, disabled, @checked));

    public static MenuEntry Item(PPMenuItem item) => new(MenuEntryKind.Item, item);

    /// <summary>子菜单（没有子项时禁用）。</summary>
    public static MenuEntry Submenu(string title, IEnumerable<MenuEntry> children, string? icon = null)
    {
        var list = children.ToList();
        return new(MenuEntryKind.Item, new PPMenuItem(title, null, icon) { Children = list, Disabled = list.Count == 0 });
    }

    public static MenuEntry Divider => new(MenuEntryKind.Separator);

    public static MenuEntry Header(string text) => new(MenuEntryKind.Header, header: text);

    /// <summary>去掉首尾与连续的分隔线。</summary>
    public static List<MenuEntry> Normalize(IEnumerable<MenuEntry> entries)
    {
        var output = new List<MenuEntry>();
        foreach (var e in entries)
        {
            if (e.Kind == MenuEntryKind.Separator && (output.Count == 0 || output[output.Count - 1].Kind == MenuEntryKind.Separator)) continue;
            output.Add(e);
        }
        while (output.Count > 0 && output[output.Count - 1].Kind == MenuEntryKind.Separator) output.RemoveAt(output.Count - 1);
        return output;
    }
}

/// <summary>弹出菜单。</summary>
public static class MenuPresenter
{
    /// <summary>在锚点下方弹出（preferRight：子菜单贴右侧；at：相对锚点的位置，右键菜单用鼠标位置）。没有条目时返回 null。</summary>
    public static PPPopup? Show(IEnumerable<MenuEntry> entries, FrameworkElement anchor, PopupAlignment alignment = PopupAlignment.Leading,
                               bool preferRight = false, Action? onClose = null, Point? at = null)
    {
        var list = MenuEntry.Normalize(entries);
        if (list.Count == 0) return null;
        var view = new MenuView(list);
        var p = PopupController.Present(anchor, view, alignment, preferRight, onClose: onClose, at: at);
        view.Popup = p;
        p.KeyHandler = view.HandleKey;
        return p;
    }

    /// <summary>在某个元素里的某个位置弹出（右键菜单）。</summary>
    public static PPPopup? ShowAt(IEnumerable<MenuEntry> entries, FrameworkElement relativeTo, Point position) =>
        Show(entries, relativeTo, at: position);
}

/// <summary>菜单面板（一列行；各行等宽，取最宽的一行）。</summary>
internal sealed class MenuView : Border
{
    private readonly List<Row> _rows = new();
    private int _highlighted = -1;
    private PPPopup? _submenu;
    private Row? _submenuOwner;

    public PPPopup? Popup { get; set; }

    private sealed class Row
    {
        public PPMenuItem Item = null!;
        public Border Box = null!;
        public FrameworkElement? IconEl;
        public TextBlock Title = null!;
        public TextBlock? Shortcut;
        public PPIcon? Chevron;
    }

    public MenuView(IReadOnlyList<MenuEntry> entries)
    {
        var col = UI.VStack(0);
        col.Align = CrossAlign.Stretch;
        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case MenuEntryKind.Separator:
                    col.Children.Add(new Border { Height = 1, Margin = new Thickness(M.Xs, M.Xs, M.Xs, M.Xs), SnapsToDevicePixels = true }.Bg(ColorToken.BorderSubtle));
                    break;
                case MenuEntryKind.Header:
                    col.Children.Add(new Border
                    {
                        Height = M.RowHeightCompact,
                        Padding = new Thickness(M.Md, 0, M.Md, 0),
                        Child = UI.Text(e.HeaderText, Typo.Caption, ColorToken.TextTertiary).VCenter(),
                    });
                    break;
                default:
                    col.Children.Add(MakeRow(e.Data!));
                    break;
            }
        }
        // 各行等宽（取最宽的一行），整体 180–360（含 4px 内边距）
        Child = new FitWidth(col, 180 - 2 * M.Xs, 360 - 2 * M.Xs);
        Padding = new Thickness(M.Xs);
    }

    private Border MakeRow(PPMenuItem item)
    {
        var row = new Row { Item = item };
        FrameworkElement? icon = null;
        if (item.Checked) icon = new PPIcon("check", M.IconSmall);
        else if (item.Icon is IconKey k) icon = new PPIcon(k, M.Icon) { Opacity = item.Disabled ? 0.45 : 1 };
        var slot = new Grid { Width = M.Icon, Height = M.Icon };
        if (icon != null) slot.Children.Add(icon.Center());
        row.IconEl = icon;
        row.Title = UI.Text(item.Title);
        var h = UI.HStack(M.Md, slot, row.Title, UI.Spacer(M.Xl));
        if (item.Shortcut != null)
        {
            row.Shortcut = UI.Text(item.Shortcut, Typo.Small).Shrink(false);
            h.Children.Add(row.Shortcut);
        }
        if (item.Children != null)
        {
            row.Chevron = new PPIcon("chevron-right", M.IconSmall);
            h.Children.Add(row.Chevron);
        }
        row.Box = new Border
        {
            Height = M.RowHeight,
            Padding = new Thickness(M.Md, 0, M.Md, 0),
            CornerRadius = new CornerRadius(M.RadiusSM),
            Child = h,
            SnapsToDevicePixels = true,
        };
        row.Box.A11y(item.Title);
        int index = _rows.Count;
        _rows.Add(row);
        row.Box.MouseEnter += (_, _) =>
        {
            Highlight(index);
            if (item.Children != null && !item.Disabled) OpenSubmenu(row);
            else if (_submenuOwner != row) CloseSubmenu();
        };
        row.Box.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Activate(row);
        };
        row.Box.SetValue(UI.ClickActionProperty, (Action)(() => Activate(row)));
        UpdateRow(row, false);
        return row.Box;
    }

    private void UpdateRow(Row r, bool hl)
    {
        var item = r.Item;
        hl = hl && !item.Disabled;
        r.Box.Bg(hl ? ColorToken.Accent : ColorToken.Clear);
        string fg = item.Disabled ? ColorToken.TextDisabled : item.Destructive ? ColorToken.Danger : ColorToken.TextPrimary;
        r.Title.Fg(hl ? ColorToken.TextOnAccent : fg);
        if (r.IconEl != null)
            r.IconEl.Fg(hl ? ColorToken.TextOnAccent : item.Checked ? ColorToken.Accent : item.Destructive ? ColorToken.Danger : ColorToken.IconPrimary);
        if (r.Shortcut != null)
        {
            r.Shortcut.Fg(hl ? ColorToken.TextOnAccent : ColorToken.TextTertiary);
            r.Shortcut.Opacity = hl ? 0.85 : 1;
        }
        r.Chevron?.Fg(hl ? ColorToken.TextOnAccent : ColorToken.IconSecondary);
    }

    private void Highlight(int index)
    {
        if (_highlighted == index) return;
        if (_highlighted >= 0 && _highlighted < _rows.Count) UpdateRow(_rows[_highlighted], false);
        _highlighted = index;
        if (index >= 0 && index < _rows.Count) UpdateRow(_rows[index], true);
    }

    private void Activate(Row r)
    {
        var item = r.Item;
        if (item.Disabled) return;
        if (item.Children != null)
        {
            OpenSubmenu(r);
            return;
        }
        PopupController.DismissAll();
        if (item.Action is Action a) Dispatcher.BeginInvoke(DispatcherPriority.Normal, a);
    }

    private void OpenSubmenu(Row r)
    {
        if (r.Item.Children == null || r.Item.Children.Count == 0) return;
        if (_submenuOwner == r && _submenu != null && _submenu.IsOpen) return;
        CloseSubmenu();
        _submenuOwner = r;
        _submenu = MenuPresenter.Show(r.Item.Children, r.Box, preferRight: true, onClose: () =>
        {
            if (_submenuOwner == r) { _submenu = null; _submenuOwner = null; }
        });
    }

    private void CloseSubmenu()
    {
        var s = _submenu;
        _submenu = null;
        _submenuOwner = null;
        if (s != null) PopupController.Dismiss(s);
    }

    private void Move(int delta)
    {
        var selectable = Enumerable.Range(0, _rows.Count).Where(i => !_rows[i].Item.Disabled).ToList();
        if (selectable.Count == 0) return;
        int pos = selectable.IndexOf(_highlighted);
        if (pos < 0) pos = delta > 0 ? -1 : selectable.Count;
        int next = selectable[((pos + delta) % selectable.Count + selectable.Count) % selectable.Count];
        Highlight(next);
    }

    public bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(1); return true;
            case Key.Up: Move(-1); return true;
            case Key.Enter:
            case Key.Space:
                if (_highlighted >= 0) Activate(_rows[_highlighted]);
                return true;
            case Key.Right:
                if (_highlighted >= 0 && _rows[_highlighted].Item.Children != null)
                {
                    OpenSubmenu(_rows[_highlighted]);
                    return true;
                }
                return false;
            case Key.Left:
                // 子菜单：关闭自己回到父菜单
                if (Popup != null && PopupController.Open.Count > 1 && PopupController.Open[0] != Popup)
                {
                    PopupController.Dismiss(Popup);
                    return true;
                }
                return false;
            default:
                return false;
        }
    }
}

/// <summary>点击弹出菜单的按钮（「更多」、导入菜单等），外观由 label 自己画。对应 Mac 的 PPMenuButton。</summary>
public sealed class PPMenuButton : PPButtonBase
{
    private readonly Func<IEnumerable<MenuEntry>> _entries;
    private readonly PopupAlignment _alignment;
    private PPPopup? _open;

    /// <summary>new PPMenuButton(() =&gt; new[] { MenuEntry.Item(…), MenuEntry.Divider, MenuEntry.Submenu(…) }, label)</summary>
    public PPMenuButton(Func<IEnumerable<MenuEntry>> entries, UIElement label, PopupAlignment alignment = PopupAlignment.Leading, string? help = null)
    {
        _entries = entries;
        _alignment = alignment;
        Content = label;
        if (help != null) { ToolTip = help; this.A11y(help); }
        Action = Toggle;
    }

    public bool IsOpen => _open != null;

    private void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        if (PopupController.WasJustDismissed(this)) return;
        _open = MenuPresenter.Show(_entries(), this, _alignment, onClose: () => _open = null);
    }

    public override void UpdateVisual() { }
}

/// <summary>外观和 PPButton 一样、点击弹出菜单的按钮，标题后带下拉箭头。对应 Mac 的 PPButtonMenu。</summary>
public sealed class PPButtonMenu : PPButton
{
    private readonly Func<IEnumerable<MenuEntry>> _entries;
    private readonly PopupAlignment _alignment;
    private PPPopup? _open;

    public PPButtonMenu(string title, Func<IEnumerable<MenuEntry>> entries, string? icon = null, PPButtonKind kind = PPButtonKind.Secondary,
                        PPButtonSize size = PPButtonSize.Regular, PopupAlignment alignment = PopupAlignment.Leading)
        : base(title, null, icon, kind, size, trailingIcon: "chevron-down")
    {
        _entries = entries;
        _alignment = alignment;
        Action = Toggle;
    }

    private void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        if (PopupController.WasJustDismissed(this)) return;
        _open = MenuPresenter.Show(_entries(), this, _alignment, onClose: () => _open = null);
    }
}

/// <summary>右键菜单。</summary>
public static class ContextMenus
{
    /// <summary>登记的右键菜单（调试桥右键点击用）。</summary>
    public static readonly DependencyProperty EntriesProperty = DependencyProperty.RegisterAttached(
        "Entries", typeof(Func<IEnumerable<MenuEntry>>), typeof(ContextMenus), new PropertyMetadata(null));

    public static Func<IEnumerable<MenuEntry>>? GetEntries(DependencyObject d) => d.GetValue(EntriesProperty) as Func<IEnumerable<MenuEntry>>;

    /// <summary>
    /// 右键（或键盘菜单键 / Shift+F10）弹出自绘菜单；嵌套时最内层的先处理。对应 Mac 的 .ppContextMenu { entries }。
    /// </summary>
    public static T PPContextMenu<T>(this T el, Func<IEnumerable<MenuEntry>> entries) where T : FrameworkElement
    {
        el.SetValue(EntriesProperty, entries);
        if (el is Panel p && p.Background == null) p.Background = System.Windows.Media.Brushes.Transparent;
        if (el is Border b && b.Background == null) b.Background = System.Windows.Media.Brushes.Transparent;
        DateTime last = DateTime.MinValue;
        el.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            last = DateTime.UtcNow;
            Open(el, e.GetPosition(el));
        };
        el.ContextMenuOpening += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            if ((DateTime.UtcNow - last).TotalMilliseconds < 400) return;
            var pos = e.CursorLeft >= 0 ? new Point(e.CursorLeft, e.CursorTop) : new Point(M.Md, el.ActualHeight);
            Open(el, pos);
        };
        return el;
    }

    /// <summary>在元素的某个位置打开它登记的右键菜单。</summary>
    public static PPPopup? Open(FrameworkElement el, Point position)
    {
        var f = GetEntries(el);
        if (f == null) return null;
        PopupController.DismissAll();
        return MenuPresenter.ShowAt(f(), el, position);
    }
}

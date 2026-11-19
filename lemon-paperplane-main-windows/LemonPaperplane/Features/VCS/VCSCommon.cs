using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 版本控制界面的公共小件：偏好、状态颜色、日期、引用徽标、大对话框外壳、插件动作调用（对应 Mac VCSCommon.swift）。

/// <summary>本机偏好（存在 UiPrefs，只影响界面）。同一次运行内先读内存里的值（调试桥运行时 UiPrefs 不写注册表）。</summary>
public static class VCSPrefs
{
    private static readonly Dictionary<string, string?> Memory = new();

    private static string? Get(string name)
    {
        if (Memory.TryGetValue(name, out var v)) return v;
        v = UiPrefs.Get(name);
        Memory[name] = v;
        return v;
    }

    private static void Put(string name, string? value)
    {
        Memory[name] = value;
        UiPrefs.Set(name, value);
    }

    private static bool Bool(string name, bool fallback) => Get(name) is string s ? s == "1" || s == "true" : fallback;
    private static void SetBool(string name, bool v) => Put(name, v ? "1" : "0");

    /// <summary>暂存区模式（IDEA 的 Enable staging area）</summary>
    public static bool StagingArea { get => Bool("VCSStagingArea", false); set => SetBool("VCSStagingArea", value); }
    public static bool GroupByDirectory { get => Bool("VCSGroupByDir", true); set => SetBool("VCSGroupByDir", value); }
    public static bool DiffSideBySide { get => Bool("VCSDiffSideBySide", true); set => SetBool("VCSDiffSideBySide", value); }
    public static bool DiffWrap { get => Bool("VCSDiffWrap", false); set => SetBool("VCSDiffWrap", value); }
    public static bool DiffIgnoreWhitespace { get => Bool("VCSDiffIgnoreWS", false); set => SetBool("VCSDiffIgnoreWS", value); }
    public static string PullMode { get => Get("VCSPullMode") ?? "merge"; set => Put("VCSPullMode", value); }
    public static bool PullAutostash { get => Bool("VCSPullAutostash", true); set => SetBool("VCSPullAutostash", value); }
    public static bool LogAllBranches { get => Bool("VCSLogAll", true); set => SetBool("VCSLogAll", value); }

    /// <summary>任意字符串偏好（克隆目录、工作区分段、选中的仓库等）。</summary>
    public static string? String(string name) => Get(name);
    public static void SetString(string name, string? value) => Put(name, value);

    /// <summary>最近的提交信息（按仓库，最多 20 条，新的在前）</summary>
    public static List<string> MessageHistory(string root)
    {
        var raw = Get("VCSMessages:" + root);
        if (string.IsNullOrEmpty(raw)) return new List<string>();
        try { return Json.Deserialize<List<string>>(raw!) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    public static void Remember(string message, string root)
    {
        var m = message.Trim();
        if (m.Length == 0) return;
        var list = MessageHistory(root).Where(x => x != m).ToList();
        list.Insert(0, m);
        Put("VCSMessages:" + root, Json.Serialize(list.Take(20).ToList()));
    }
}

/// <summary>文件状态的颜色与名称</summary>
public static class VCSStyle
{
    /// <summary>状态字母 → 颜色键</summary>
    public static string Color(string status) => status switch
    {
        "A" => ColorToken.Success,
        "D" => ColorToken.TextTertiary,
        "?" => ColorToken.Warning,
        "U" => ColorToken.Danger,
        "R" or "C" => ColorToken.Info,
        "M" or "T" => ColorToken.Info,
        _ => ColorToken.TextPrimary,
    };

    public static string Title(string status) => status switch
    {
        "A" => L.T("vcs.status.added"),
        "D" => L.T("vcs.status.deleted"),
        "?" => L.T("vcs.status.untracked"),
        "U" => L.T("vcs.status.conflicted"),
        "R" => L.T("vcs.status.renamed"),
        "C" => L.T("vcs.status.copied"),
        "T" => L.T("vcs.status.typeChanged"),
        _ => L.T("vcs.status.modified"),
    };

    /// <summary>冲突类型（插件给 UU/AA… 两字母，SDK 注释里是 both-modified…，两种都认）</summary>
    public static string ConflictTitle(string? t) => t switch
    {
        "UU" or "both-modified" => L.T("vcs.conflict.bothModified"),
        "AA" or "both-added" => L.T("vcs.conflict.bothAdded"),
        "DU" or "deleted-by-us" => L.T("vcs.conflict.deletedByUs"),
        "UD" or "deleted-by-them" => L.T("vcs.conflict.deletedByThem"),
        "AU" or "added-by-us" => L.T("vcs.conflict.addedByUs"),
        "UA" or "added-by-them" => L.T("vcs.conflict.addedByThem"),
        "DD" or "both-deleted" => L.T("vcs.conflict.bothDeleted"),
        _ => L.T("vcs.conflict.unknown"),
    };

    public static string OperationTitle(string? op) => op switch
    {
        "merge" => L.T("vcs.op.merge"),
        "rebase" => L.T("vcs.op.rebase"),
        "cherry-pick" => L.T("vcs.op.cherryPick"),
        "revert" => L.T("vcs.op.revert"),
        "am" => L.T("vcs.op.am"),
        _ => op ?? "",
    };

    public static string ShortHash(string? h) => (h ?? "").Length > 8 ? h!.Substring(0, 8) : h ?? "";
}

/// <summary>路径工具（仓库内路径一律用 /）</summary>
public static class VCSPath
{
    public static string Name(string p)
    {
        var t = p.EndsWith("/", StringComparison.Ordinal) ? p.Substring(0, p.Length - 1) : p;
        var i = t.LastIndexOf('/');
        return i < 0 ? t : t.Substring(i + 1);
    }

    public static string Dir(string p)
    {
        var t = p.EndsWith("/", StringComparison.Ordinal) ? p.Substring(0, p.Length - 1) : p;
        var i = t.LastIndexOf('/');
        return i < 0 ? "" : t.Substring(0, i);
    }

    public static string Ext(string p)
    {
        var n = Name(p);
        var i = n.LastIndexOf('.');
        return i <= 0 ? "" : n.Substring(i + 1).ToLowerInvariant();
    }

    /// <summary>仓库根（服务器路径）+ 仓库内相对路径 → 绝对路径（按服务器的分隔符；连着远程服务器时不用本机的 System.IO.Path）</summary>
    public static string Join(string root, string rel)
    {
        if (string.IsNullOrEmpty(rel)) return root;
        return ServerPath.Join(root, rel, AppModel.Shared.PathSeparator);
    }

    public static readonly HashSet<string> ImageExtensions = new() { "png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "ico", "icns", "heic" };

    public static bool IsImage(string p) => ImageExtensions.Contains(Ext(p));

    /// <summary>本机路径（Windows 分隔符）的最后一段</summary>
    public static string LocalName(string p)
    {
        var t = p.TrimEnd('/', '\\');
        var i = t.LastIndexOfAny(new[] { '/', '\\' });
        return i < 0 ? t : t.Substring(i + 1);
    }
}

/// <summary>日期显示（一周内相对时间，否则日期时间；对应 Mac VCSDate）</summary>
public static class VCSDate
{
    /// <summary>一周内显示相对时间，否则显示日期时间</summary>
    public static string Short(DateTimeOffset? d)
    {
        if (d == null) return "";
        var delta = DateTimeOffset.Now - d.Value;
        var secs = delta.TotalSeconds;
        if (Math.Abs(secs) >= 7 * 86400) return Full(d);
        bool future = secs < 0;
        secs = Math.Abs(secs);
        string unit;
        long n;
        if (secs < 60) { unit = "Seconds"; n = (long)secs; }
        else if (secs < 3600) { unit = "Minutes"; n = (long)(secs / 60); }
        else if (secs < 86400) { unit = "Hours"; n = (long)(secs / 3600); }
        else { unit = "Days"; n = (long)(secs / 86400); }
        if (unit == "Days" && n == 1) unit = "Day";
        return L.T((future ? "vcs.date.in" : "vcs.date.ago") + unit, n);
    }

    public static string Full(DateTimeOffset? d) =>
        d == null ? "" : d.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Full(string? s) => Full(GoTime.Parse(s));
}

/// <summary>引用徽标的样式：HEAD / 本地 / 远程 / 标签 不同颜色</summary>
public static class VCSRefStyle
{
    public static (string Icon, string Fg, string Bg) Of(VCSRef r, bool current) => r.Kind switch
    {
        "head" => ("circle-dot", ColorToken.Accent, ColorToken.AccentSubtle),
        "local" => ("git-branch", current ? ColorToken.Accent : ColorToken.Success, current ? ColorToken.AccentSubtle : ColorToken.SuccessSubtle),
        "remote" => ("cloud", ColorToken.Info, ColorToken.InfoSubtle),
        "tag" => ("tag", ColorToken.Warning, ColorToken.WarningSubtle),
        _ => ("git-commit-horizontal", ColorToken.TextSecondary, ColorToken.NeutralSubtle),
    };
}

/// <summary>版本控制界面的小元素工厂（对应 Mac 的 VCSRefBadge / VCSStatusLetter / VCSLineStats / VCSGroupHeader）。</summary>
public static class VCSViews
{
    /// <summary>引用徽标</summary>
    public static Border RefBadge(VCSRef r, bool current = false)
    {
        var (icon, fg, bg) = VCSRefStyle.Of(r, current);
        var row = HStack(3, new PPIcon(icon, 11), Text(r.Name, Typo.Caption));
        return Box(row).Pad(5, 0, 5, 0).H(16).Rounded(M.RadiusSM).Bg(bg).Fg(fg).Tip(r.Name).Do(b => b.HorizontalAlignment = HorizontalAlignment.Left);
    }

    /// <summary>状态字母（M/A/D…），等宽着色</summary>
    public static TextBlock StatusLetter(string status)
    {
        var t = Text(status == "?" ? "U" : status, Typo.MonoSmall, VCSStyle.Color(status)).W(12).Shrink(false).Tip(VCSStyle.Title(status));
        t.TextTrimming = TextTrimming.None;
        return t;
    }

    /// <summary>增删行数（+3 −1）</summary>
    public static Stack LineStats(int additions, int deletions) =>
        HStack(M.Xs,
            additions > 0 ? Text("+" + additions, Typo.MonoSmall, ColorToken.DiffAddedText).Shrink(false) : null,
            deletions > 0 ? Text("−" + deletions, Typo.MonoSmall, ColorToken.DiffRemovedText).Shrink(false) : null);

    /// <summary>小号分组标题（列表里的「变更」「未跟踪文件」等）</summary>
    public static Stack GroupHeader(string title, int? count = null, bool? collapsed = null, Action? onToggle = null, UIElement? accessory = null)
    {
        var row = HStack(M.Xs,
            collapsed is bool c ? new PPIcon(c ? "chevron-right" : "chevron-down", 12, ColorToken.IconSecondary) : null,
            accessory,
            Text(title, Typo.Label, ColorToken.TextSecondary),
            count is int n ? Text(n.ToString(CultureInfo.InvariantCulture), Typo.Caption, ColorToken.TextTertiary).Shrink(false) : null,
            Spacer());
        row.Padding = new Thickness(M.Sm, 0, M.Sm, 0);
        row.Height = M.RowHeightCompact;
        row.Bg(ColorToken.Clear);
        if (onToggle != null) row.OnClick(onToggle);
        return row;
    }

    /// <summary>键值行（提交详情的元信息）：键 64 宽 tertiary</summary>
    public static Stack MetaRow(string key, UIElement value)
    {
        var k = Text(key, Typo.Small, ColorToken.TextTertiary).W(64).Shrink(false);
        k.VerticalAlignment = VerticalAlignment.Top;
        var row = HStack(M.Md, k, value is FrameworkElement fe ? fe.Flex() : value);
        row.Align = CrossAlign.Start;
        return row;
    }

    /// <summary>复选框标签用小号字（Mac 在外层 .font(.pp(.small))）</summary>
    public static PPCheckbox Small(PPCheckbox cb)
    {
        if (cb.Content is Panel p)
            foreach (var t in p.Children.OfType<TextBlock>()) Typo.Small.Apply(t);
        return cb;
    }

    /// <summary>菜单按钮的图标外观（小号方块，22×22）</summary>
    public static FrameworkElement MenuGlyph(string icon, double size = 0)
    {
        double s = size > 0 ? size : M.IconButtonSmall;
        return Box(new PPIcon(icon, s >= M.IconButton ? M.Icon : M.IconSmall, ColorToken.IconPrimary).Center()).Size(s, s).Bg(ColorToken.Clear);
    }

    /// <summary>完整提交信息（没有 message 字段时用主题 + 空行 + 正文）</summary>
    public static string FullMessage(VCSCommitDetail d) =>
        d.Message ?? (d.Body != null ? d.Commit.Subject + "\n\n" + d.Body : d.Commit.Subject);

    /// <summary>可点击行（选中 / 悬停底色、单击、双击）。对应 Mac VCSTableRow。</summary>
    public static VCSTableRow TableRow(UIElement content, bool selected, Action onTap, Action? onDoubleTap = null, double height = 0) =>
        new(content, selected, onTap, onDoubleTap, height);
}

/// <summary>表格行：选中 / 悬停底色、单击、双击。对应 Mac VCSTableRow。</summary>
public sealed class VCSTableRow : Border
{
    private bool _selected;

    public VCSTableRow(UIElement content, bool selected, Action onTap, Action? onDoubleTap = null, double height = 0)
    {
        _selected = selected;
        Child = content;
        Height = height > 0 ? height : M.RowHeight;
        CornerRadius = new CornerRadius(M.RadiusSM);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        SnapsToDevicePixels = true;
        if (content is FrameworkElement fe && fe.ReadLocalValue(VerticalAlignmentProperty) == DependencyProperty.UnsetValue) fe.VerticalAlignment = VerticalAlignment.Center;
        MouseEnter += (_, _) => Update();
        MouseLeave += (_, _) => Update();
        this.SetValue(UI.ClickActionProperty, onTap);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            if (e.ClickCount == 2 && onDoubleTap != null) { onDoubleTap(); e.Handled = true; return; }
            if (e.ClickCount == 1) { onTap(); e.Handled = true; }
        };
        Update();
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Update(); }
    }

    private void Update() => this.Bg(_selected ? ColorToken.BgSelected : IsMouseOver ? ColorToken.BgHover : ColorToken.Clear);
}

/// <summary>三态复选框（分组全选）：true 全选、false 全不选、null 部分。对应 Mac VCSTriCheckbox。</summary>
public sealed class VCSTriCheckbox : PPButtonBase
{
    private readonly Border _box;
    private readonly bool? _state;

    public VCSTriCheckbox(bool? state, Action action)
    {
        _state = state;
        Action = action;
        UIElement? glyph = state == true ? new PPIcon("check", 12, ColorToken.TextOnAccent) : state == null ? new PPIcon("minus", 12, ColorToken.TextOnAccent) : null;
        if (glyph is FrameworkElement g) g.Center();
        _box = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(M.RadiusXS), BorderThickness = new Thickness(1), Child = glyph, SnapsToDevicePixels = true };
        _box.Bg(state == false ? ColorToken.BgInput : ColorToken.Accent);
        _box.Outline(state == false ? ColorToken.BorderStrong : ColorToken.Accent);
        Content = new Border { Child = _box, Background = System.Windows.Media.Brushes.Transparent };
        Focusable = false;
        this.A11y(state == true ? "checked" : state == null ? "mixed" : "unchecked");
    }

    public override void UpdateVisual()
    {
        if (_box != null) Opacity = IsEnabled ? 1 : 0.5;
    }
}

/// <summary>
/// 大对话框外壳：与 PPDialog 同样的外观，但内容区占满给定尺寸、不包滚动（差异、日志等自带滚动的内容用）。对应 Mac VCSLargeDialog。
/// 回车触发 DefaultButton（PPDialog 以外的对话框，DialogHost 不处理回车）。
/// </summary>
public sealed class VCSLargeDialog : Grid
{
    public PPButton? DefaultButton { get; set; }

    public VCSLargeDialog(string id, string title, UIElement content, IEnumerable<UIElement>? footer = null, double maxWidth = 1180, double maxHeight = 820)
    {
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        Margin = new Thickness(M.Huge);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        var header = HStack(0, TextMiddle(title, Typo.Title).Flex().Id("dialog.title"), Spacer(M.Md),
            new PPIconButton("x", L.T("common.close"), () => Dialogs.Dismiss(id), size: M.IconButtonSmall).Id("dialog.close"));
        header.Height = 44;
        header.Margin = new Thickness(M.Xl, 0, M.Md, 0);
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.Children.Add(header.At(0, 0));
        g.Children.Add(new PPDivider(subtle: true).At(1, 0));
        g.Children.Add(content.At(2, 0));
        var foot = footer?.ToList();
        if (foot != null && foot.Count > 0)
        {
            g.Children.Add(new PPDivider(subtle: true).At(3, 0));
            var row = HStack(M.Md, Spacer());
            foreach (var f in foot) row.Children.Add(f);
            row.Margin = new Thickness(M.Xl, M.Md, M.Xl, M.Md);
            g.Children.Add(row.At(4, 0));
        }
        var shadow = new Border { CornerRadius = new CornerRadius(M.RadiusXL) }.Bg(ColorToken.BgElevated).Shadow(ShadowToken.Dialog);
        var box = new Border
        {
            CornerRadius = new CornerRadius(M.RadiusXL),
            BorderThickness = new Thickness(1),
            Child = g,
            SnapsToDevicePixels = true,
        }.Bg(ColorToken.BgElevated).Outline(ColorToken.Border);
        var clip = new System.Windows.Media.RectangleGeometry { RadiusX = M.RadiusXL, RadiusY = M.RadiusXL };
        box.SizeChanged += (_, e) => clip.Rect = new Rect(e.NewSize);
        box.Clip = clip;
        Children.Add(shadow);
        Children.Add(box);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        this.Id("dialog");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != Key.Enter || KeyInput.Modifiers != ModifierKeys.None || DefaultButton == null) return;
            var f = Keyboard.FocusedElement;
            if (f is System.Windows.Controls.Primitives.ButtonBase || (f is TextBox tb && tb.AcceptsReturn)) return;
            if (DefaultButton.IsEnabled) DefaultButton.Action?.Invoke();
            e.Handled = true;
        };
    }
}

/// <summary>插件贡献的 vcs.* 动作：带上下文参数（root、hash/hashes、path/paths）调 project.action。对应 Mac VCSPluginActions。</summary>
public static class VCSPluginActions
{
    public static List<ActionInfo> ActionsAt(string location) => AppModel.Shared.Data.Contributions.ActionsAt(location);

    public static List<MenuEntry> Entries(string location, VCSRepo repo, Dictionary<string, JToken> args) =>
        ActionsAt(location).Select(a => MenuEntry.Item(new PPMenuItem(a.Title, () => Invoke(a, repo, args), iconRef: IconKey.Parse(a.Icon, a.Plugin)))).ToList();

    public static void Invoke(ActionInfo a, VCSRepo repo, Dictionary<string, JToken> args)
    {
        var baseArgs = new Dictionary<string, JToken>(args) { ["root"] = repo.Root };
        async Task Send(Dictionary<string, JToken>? extra)
        {
            var api = AppModel.Shared.Client();
            var moduleId = await repo.FirstModuleId();
            var all = new Dictionary<string, JToken>(baseArgs);
            if (extra != null) foreach (var kv in extra) all[kv.Key] = kv.Value;
            var t = await api.InvokeActionAsync(repo.ProjectId, moduleId, a.Plugin, a.Id, all);
            AppModel.Shared.Track(t);
        }
        if (a.Form.Count > 0)
            Dialogs.Form(a.Title, a.Form, v => Send(v), message: a.Confirm, ok: a.Title);
        else if (!string.IsNullOrEmpty(a.Confirm))
            Dialogs.Confirm(a.Title, a.Confirm, () => Send(null), ok: a.Title);
        else
            _ = VCSAsync.Report(() => Send(null), a.Title);
    }
}

/// <summary>当前按下的修饰键（行点击时判断多选）</summary>
public static class VCSModifiers
{
    public static bool Command => (KeyInput.Modifiers & ModifierKeys.Control) != 0;
    public static bool Shift => (KeyInput.Modifiers & ModifierKeys.Shift) != 0;
}

/// <summary>异步小工具</summary>
public static class VCSAsync
{
    /// <summary>执行并在失败时弹提示（取消不提示）</summary>
    public static async Task Report(Func<Task> op, string? title = null)
    {
        try { await op(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { AppModel.Shared.Toasts.Error(e, title); }
    }

    /// <summary>后台跑一个任务（异常交给 Report）</summary>
    public static void Fire(Func<Task> op) => _ = Report(op);

    /// <summary>失败返回 null</summary>
    public static async Task<T?> Try<T>(Func<Task<T>> op) where T : class
    {
        try { return await op(); }
        catch { return null; }
    }
}

/// <summary>自然顺序比较（数字按数值、不分大小写，和资源管理器一致；对应 Mac 的 localizedStandardCompare）</summary>
public sealed class VCSNaturalComparer : IComparer<string>
{
    public static readonly VCSNaturalComparer Shared = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        try { return StrCmpLogicalW(a, b); }
        catch { return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase); }
    }
}

/// <summary>表单值的读取（字段值可能是布尔或字符串）</summary>
public static class VCSForm
{
    public static string Str(Dictionary<string, JToken> v, string key) => v.TryGetValue(key, out var t) ? t.StringValue() ?? "" : "";
    public static bool Bool(Dictionary<string, JToken> v, string key, bool fallback = false) => v.TryGetValue(key, out var t) ? t.BoolValue() ?? fallback : fallback;
}

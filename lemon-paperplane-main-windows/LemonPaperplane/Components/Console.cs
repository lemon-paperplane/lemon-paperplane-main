// 控制台（对应 Mac Components/Console.swift）：自绘 + 虚拟化（只画可见行），ANSI 着色、批量追加（约 50ms 合并一次，后台解析）、
// 只保留最近 consoleMaxLines 行（完整日志在磁盘）、搜索高亮、跟随滚动、选择复制、可点击的地址、运行中输入行。
// 只有可见时才订阅日志流：先取最近 2000 行，再从 lastSeq 起订阅，断线自动续传（ConsoleStream）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

#region 行数据

/// <summary>一段同样式的文字（下标是 Text 的 UTF-16 下标）。</summary>
internal readonly struct ConsoleSpan
{
    public readonly int Start;
    public readonly int Length;
    public readonly ANSIStyle Style;

    public ConsoleSpan(int start, int length, ANSIStyle style)
    {
        Start = start;
        Length = length;
        Style = style;
    }
}

/// <summary>行里的可点击地址。</summary>
internal readonly struct ConsoleLink
{
    public readonly int Start;
    public readonly int Length;
    public readonly string Url;

    public ConsoleLink(int start, int length, string url)
    {
        Start = start;
        Length = length;
        Url = url;
    }
}

/// <summary>控制台的一行（已解析 ANSI，后台线程生成，界面线程只读）。</summary>
internal sealed class ConsoleLineData
{
    public string Text = "";
    public ConsoleSpan[] Spans = Array.Empty<ConsoleSpan>();
    /// <summary>0 stdout、1 stderr、2 system</summary>
    public byte Kind;
    public ConsoleLink[]? Links;
    /// <summary>不换行时的显示宽度（格）</summary>
    public int Width;
    /// <summary>当前列数下的显示行数（RowsFor 为算它时的列数）</summary>
    public int Rows = 1;
    public int RowsFor = -1;

    private static readonly Regex UrlRegex = new("https?://[^\\s\"'<>\\x1b]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ConsoleLineData From(LogLine l, ANSIParser parser)
    {
        var spans = parser.Parse(l.Text);
        var line = new ConsoleLineData { Kind = l.S switch { "stderr" => (byte)1, "system" => (byte)2, _ => (byte)0 } };
        if (spans.Count == 1)
        {
            line.Text = spans[0].Text;
            line.Spans = new[] { new ConsoleSpan(0, line.Text.Length, spans[0].Style) };
        }
        else if (spans.Count > 1)
        {
            var sb = new StringBuilder();
            var arr = new ConsoleSpan[spans.Count];
            for (int i = 0; i < spans.Count; i++)
            {
                arr[i] = new ConsoleSpan(sb.Length, spans[i].Text.Length, spans[i].Style);
                sb.Append(spans[i].Text);
            }
            line.Text = sb.ToString();
            line.Spans = arr;
        }
        var t = line.Text;
        if (t.IndexOf("http", StringComparison.Ordinal) >= 0)
        {
            List<ConsoleLink>? links = null;
            foreach (Match m in UrlRegex.Matches(t))
            {
                var u = m.Value;
                while (u.Length > 0 && ".,;:)]}".IndexOf(u[u.Length - 1]) >= 0) u = u.Substring(0, u.Length - 1);
                if (u.Length == 0 || !Uri.TryCreate(u, UriKind.Absolute, out _)) continue;
                (links ??= new List<ConsoleLink>()).Add(new ConsoleLink(m.Index, u.Length, u));
            }
            line.Links = links?.ToArray();
        }
        line.Width = ConsoleLayout.Measure(t);
        return line;
    }
}

/// <summary>等宽网格排版：每个字占 1 或 2 格（东亚宽字符、emoji），组合符号 0 格，tab 到下一个 4 格位置。</summary>
internal static class ConsoleLayout
{
    public const int TabSize = 4;

    public static int CellWidth(int cp)
    {
        if (cp < 0x300) return 1;
        if (cp <= 0xFFFF)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory((char)cp);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        }
        if (cp is 0xFE0E or 0xFE0F) return 0;
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) || (cp >= 0x2E80 && cp <= 0x303E) || (cp >= 0x3041 && cp <= 0x33FF) ||
        (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0xA000 && cp <= 0xA4CF) ||
        (cp >= 0xAC00 && cp <= 0xD7A3) || (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFE30 && cp <= 0xFE4F) ||
        (cp >= 0xFF00 && cp <= 0xFF60) || (cp >= 0xFFE0 && cp <= 0xFFE6) || (cp >= 0x1F300 && cp <= 0x1F64F) ||
        (cp >= 0x1F900 && cp <= 0x1F9FF) || (cp >= 0x20000 && cp <= 0x3FFFD);

    /// <summary>不换行的显示宽度（格）。</summary>
    public static int Measure(string text)
    {
        int col = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x300 && c != '\t') { col++; continue; }
            if (c == '\t') { col += TabSize - col % TabSize; continue; }
            int cp = c;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(c, text[i + 1]); i++; }
            col += CellWidth(cp);
        }
        return col;
    }

    /// <summary>
    /// 排版一行：cols &gt; 0 时按列数换行。rowOf/colOf/widthOf 为每个 UTF-16 下标的显示行、起始列、宽度（低代理项宽度 0）；
    /// 传 null 只数行数。返回显示行数。
    /// </summary>
    public static int Layout(string text, int cols, int[]? rowOf, int[]? colOf, int[]? widthOf)
    {
        int row = 0, col = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            int cp = c, units = 1;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(c, text[i + 1]); units = 2; }
            int w = c == '\t' ? TabSize - col % TabSize : (c < 0x300 ? 1 : CellWidth(cp));
            if (cols > 0 && w > 0)
            {
                if (col >= cols || (col + w > cols && c != '\t' && col > 0)) { row++; col = 0; if (c == '\t') w = TabSize; }
                if (c == '\t' && col + w > cols) w = cols - col;
            }
            if (rowOf != null) { rowOf[i] = row; colOf![i] = col; widthOf![i] = w; }
            if (units == 2)
            {
                i++;
                if (rowOf != null) { rowOf[i] = row; colOf![i] = col + w; widthOf![i] = 0; }
            }
            col += w;
        }
        return row + 1;
    }
}

#endregion

#region 字体

/// <summary>控制台字体：等宽主字体 + 回退字体（中文、符号、emoji），按码位找字形（四种样式各一套）。</summary>
internal sealed class ConsoleFonts
{
    private static ConsoleFonts? _shared;
    public static ConsoleFonts Shared => _shared ??= new ConsoleFonts();

    public readonly double Size;
    private readonly GlyphTypeface[][] _faces; // [样式：0 常规 1 粗 2 斜 3 粗斜][回退序号]
    private readonly Dictionary<int, uint>[] _maps;

    private ConsoleFonts()
    {
        Size = Typo.Mono.Size;
        var names = new List<string> { UIFonts.MonoName };
        foreach (var n in new[] { "Cascadia Mono", "Consolas", "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI Symbol", "Segoe UI Emoji", "Segoe UI" })
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        _faces = new GlyphTypeface[4][];
        _maps = new Dictionary<int, uint>[4];
        for (int s = 0; s < 4; s++)
        {
            var list = new List<GlyphTypeface>();
            foreach (var n in names)
            {
                var tf = new Typeface(new FontFamily(n), (s & 2) != 0 ? FontStyles.Italic : FontStyles.Normal,
                    (s & 1) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                if (tf.TryGetGlyphTypeface(out var gt)) list.Add(gt);
            }
            _faces[s] = list.ToArray();
            _maps[s] = new Dictionary<int, uint>();
        }
    }

    public GlyphTypeface Primary => _faces[0][0];

    public GlyphTypeface Face(int style, int index) => _faces[style][index];

    /// <summary>码位的字形：(回退序号, 字形号)。</summary>
    public (int Face, ushort Glyph) Glyph(int cp, int style)
    {
        var map = _maps[style];
        if (map.TryGetValue(cp, out var v)) return ((int)(v >> 16), (ushort)(v & 0xFFFF));
        var faces = _faces[style];
        int face = 0;
        ushort glyph = 0;
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i].CharacterToGlyphMap.TryGetValue(cp, out var g)) { face = i; glyph = g; break; }
        }
        if (glyph == 0 && faces.Length > 0 && faces[0].CharacterToGlyphMap.TryGetValue('?', out var q)) glyph = q;
        map[cp] = ((uint)face << 16) | glyph;
        return (face, glyph);
    }
}

#endregion

#region 文本区

/// <summary>
/// 控制台文本区（自绘 + 虚拟化）：只排版、绘制可见的行；自带两条细滚动条（不放在 ScrollViewer 里，
/// 用户滚动和程序滚动分得清，外层视图重建时也不会被「恢复滚动位置」打断跟随）。
/// </summary>
internal sealed class ConsoleTextView : FrameworkElement
{
    // MARK: 数据
    private readonly List<ConsoleLineData> _lines = new();
    private readonly List<int> _rowStart = new(); // 每行之前的显示行数（换行时）
    private long _firstNo;                        // _lines[0] 的绝对行号
    private int _totalRows;
    private int _maxWidth;
    private readonly int _maxLines = Math.Max(100, M.ConsoleMaxLines);

    // MARK: 排版
    private bool _wrap = true;
    private int _cols;
    private double _cellW = 7, _lineH = 16, _baseline = 12;
    private double _metricsDpi;
    private const double PadX = 13, PadY = 6; // Mac：textContainerInset 8×6 + lineFragmentPadding 5

    // MARK: 滚动
    private double _offsetY, _offsetX;
    private readonly ScrollBar _vbar;
    private readonly ScrollBar _hbar;
    private bool _follow = true;

    // MARK: 选择、搜索
    private (long Line, int Index)? _selAnchor;
    private (long Line, int Index)? _selActive;
    private bool _dragging;
    private bool _dragMoved;
    private Point _downAt;
    private string? _downLink;
    private readonly DispatcherTimer _autoScroll;
    private double _autoScrollDy;
    private readonly List<(long Line, int Start, int Length)> _matches = new();
    private readonly Dictionary<long, List<int>> _matchesByLine = new();
    private int _matchIndex;

    // MARK: 画刷
    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    private Color[] _ansi16 = new Color[16];
    private int[] _rowOf = new int[256], _colOf = new int[256], _widthOf = new int[256];

    public event Action<bool>? FollowChanged;
    public event Action? SearchChanged;

    /// <summary>调试统计（调试桥开着时写进无障碍名字，tree 能看到）。</summary>
    public long LinesAppended, Batches;
    public double MaxAppendMs, TotalAppendMs, MaxRenderMs, TotalRenderMs;
    public long Renders;

    public ConsoleTextView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
        _vbar = new ScrollBar { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, Minimum = 0, Visibility = Visibility.Collapsed };
        _hbar = new ScrollBar { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Minimum = 0, Visibility = Visibility.Collapsed };
        _vbar.Scroll += (_, e) => ScrollTo(e.NewValue, user: true);
        _hbar.Scroll += (_, e) => SetOffsetX(e.NewValue);
        _autoScroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _autoScroll.Tick += (_, _) => AutoScrollTick();
        RefreshPalette();
        Loaded += (_, _) => Theme.Changed += OnThemeChanged;
        Unloaded += (_, _) => Theme.Changed -= OnThemeChanged;
        SizeChanged += (_, _) => OnResized();
        this.PPContextMenu(() => new[]
        {
            MenuEntry.Item(L.T("common.copy"), () => Actions.Copy(SelectedText() ?? AllText()), icon: "copy"),
        });
    }

    /// <summary>叠在文本区上的两条滚动条（由外层 Grid 放置）。</summary>
    public ScrollBar VerticalBar => _vbar;
    public ScrollBar HorizontalBar => _hbar;

    public int LineCount => _lines.Count;

    public bool Follow
    {
        get => _follow;
        set
        {
            if (_follow == value) return;
            _follow = value;
            FollowChanged?.Invoke(value);
            if (value) ScrollToEnd();
        }
    }

    public bool Wrap
    {
        get => _wrap;
        set
        {
            if (_wrap == value) return;
            _wrap = value;
            _offsetX = 0;
            RecomputeRows(ColsFor(ActualWidth));
            if (_follow) ScrollToEnd(); else { ClampOffsets(); UpdateBars(); }
            InvalidateVisual();
        }
    }

    private void OnThemeChanged(object? s, EventArgs e)
    {
        RefreshPalette();
        InvalidateVisual();
    }

    private void RefreshPalette()
    {
        for (int i = 0; i < 16; i++) _ansi16[i] = Theme.Ansi(i);
    }

    // MARK: 追加 / 清空

    public void Clear()
    {
        _firstNo += _lines.Count;
        _lines.Clear();
        _rowStart.Clear();
        _totalRows = 0;
        _maxWidth = 0;
        _offsetY = 0;
        _offsetX = 0;
        _selAnchor = _selActive = null;
        _matches.Clear();
        _matchesByLine.Clear();
        _matchIndex = 0;
        UpdateBars();
        InvalidateVisual();
    }

    /// <summary>追加一批已解析的行（超过上限时一次删掉多出的行，再多删 10%）。</summary>
    public void Append(IReadOnlyList<ConsoleLineData> batch)
    {
        if (batch.Count == 0) return;
        var sw = Stopwatch.StartNew();
        int start = batch.Count > _maxLines ? batch.Count - _maxLines : 0;
        EnsureLayout();
        for (int i = start; i < batch.Count; i++)
        {
            var l = batch[i];
            _rowStart.Add(_totalRows);
            _lines.Add(l);
            int rows = RowsOf(l);
            _totalRows += rows;
            if (l.Width > _maxWidth) _maxWidth = l.Width;
        }
        int alive = _lines.Count;
        if (alive > _maxLines)
        {
            int drop = Math.Min(alive, alive - _maxLines + _maxLines / 10);
            double droppedHeight = _rowStart.Count > drop ? _rowStart[drop] * _lineH : _totalRows * _lineH;
            _lines.RemoveRange(0, drop);
            _firstNo += drop;
            RebuildRowStart();
            if (!_follow) _offsetY = Math.Max(0, _offsetY - droppedHeight); // 不跟随时保持看到的内容不动
        }
        if (_follow) ScrollToEnd(); else { ClampOffsets(); UpdateBars(); }
        InvalidateVisual();
        sw.Stop();
        LinesAppended += batch.Count;
        Batches++;
        TotalAppendMs += sw.Elapsed.TotalMilliseconds;
        MaxAppendMs = Math.Max(MaxAppendMs, sw.Elapsed.TotalMilliseconds);
    }

    private int RowsOf(ConsoleLineData l)
    {
        if (!_wrap || _cols <= 0 || l.Width <= _cols) { l.Rows = 1; l.RowsFor = _wrap ? _cols : 0; return 1; }
        if (l.RowsFor == _cols) return l.Rows;
        l.Rows = ConsoleLayout.Layout(l.Text, _cols, null, null, null);
        l.RowsFor = _cols;
        return l.Rows;
    }

    private void RebuildRowStart()
    {
        _rowStart.Clear();
        int total = 0, maxW = 0;
        foreach (var l in _lines)
        {
            _rowStart.Add(total);
            total += RowsOf(l);
            if (l.Width > maxW) maxW = l.Width;
        }
        _totalRows = total;
        _maxWidth = maxW;
    }

    private int ColsFor(double width) => !_wrap || width <= 0 ? 0 : Math.Max(10, (int)Math.Floor((width - PadX * 2) / _cellW));

    /// <summary>按列数重算各行的显示行数；不跟随时保持顶部那一行不动。</summary>
    private void RecomputeRows(int cols)
    {
        long topLine = TopLine(out _);
        _cols = cols;
        RebuildRowStart();
        if (!_follow && topLine >= 0)
        {
            int idx = (int)(topLine - _firstNo);
            if (idx >= 0 && idx < _rowStart.Count) _offsetY = _rowStart[idx] * _lineH;
        }
    }

    /// <summary>按当前尺寸与 DPI 校正排版；有变化返回 true（调用方负责滚动位置与滚动条）。</summary>
    private bool EnsureLayout()
    {
        bool changed = EnsureMetrics();
        int cols = ColsFor(ActualWidth);
        if (!changed && cols == _cols) return false;
        RecomputeRows(cols);
        return true;
    }

    private long TopLine(out int rowInLine)
    {
        rowInLine = 0;
        if (_lines.Count == 0 || _lineH <= 0) return -1;
        int row = (int)Math.Floor(Math.Max(0, _offsetY) / _lineH);
        int li = LineAtRow(row);
        rowInLine = row - _rowStart[li];
        return _firstNo + li;
    }

    /// <summary>显示行 → 行下标（二分）。</summary>
    private int LineAtRow(int row)
    {
        int lo = 0, hi = _rowStart.Count - 1;
        if (hi < 0) return 0;
        if (row <= 0) return 0;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_rowStart[mid] <= row) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    // MARK: 尺寸与滚动

    private bool EnsureMetrics()
    {
        double dpi = PresentationSource.FromVisual(this) != null ? VisualTreeHelper.GetDpi(this).PixelsPerDip : 1;
        if (dpi == _metricsDpi) return false;
        _metricsDpi = dpi;
        var f = ConsoleFonts.Shared;
        var face = f.Primary;
        double adv = 0.6 * f.Size;
        if (face.CharacterToGlyphMap.TryGetValue('M', out var gm)) adv = face.AdvanceWidths[gm] * f.Size;
        _cellW = Math.Max(1, Math.Round(adv * dpi)) / dpi;          // 格宽对齐到物理像素，文字清晰
        double natural = face.Height * f.Size;
        _lineH = Math.Ceiling(natural * dpi) / dpi;
        _baseline = Math.Round(((_lineH - natural) / 2 + face.Baseline * f.Size) * dpi) / dpi;
        foreach (var l in _lines) l.RowsFor = -1;
        return true;
    }

    private void OnResized()
    {
        EnsureLayout();
        if (_follow) ScrollToEnd(); else { ClampOffsets(); UpdateBars(); }
        InvalidateVisual();
    }

    private double ContentHeight => _totalRows * _lineH + PadY * 2;
    private double ContentWidth => _wrap ? ActualWidth : _maxWidth * _cellW + PadX * 2;
    private double MaxOffsetY => Math.Max(0, ContentHeight - ActualHeight);
    private double MaxOffsetX => _wrap ? 0 : Math.Max(0, ContentWidth - ActualWidth);

    private void ClampOffsets()
    {
        _offsetY = Math.Max(0, Math.Min(_offsetY, MaxOffsetY));
        _offsetX = Math.Max(0, Math.Min(_offsetX, MaxOffsetX));
    }

    private void UpdateBars()
    {
        double h = ActualHeight, w = ActualWidth;
        double maxY = MaxOffsetY, maxX = MaxOffsetX;
        _vbar.Maximum = maxY;
        _vbar.ViewportSize = h;
        _vbar.LargeChange = Math.Max(_lineH, h - _lineH);
        _vbar.SmallChange = _lineH;
        _vbar.Value = _offsetY;
        _vbar.Visibility = maxY > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        _hbar.Maximum = maxX;
        _hbar.ViewportSize = w;
        _hbar.LargeChange = Math.Max(_cellW, w - _cellW * 4);
        _hbar.SmallChange = _cellW * 4;
        _hbar.Value = _offsetX;
        _hbar.Visibility = maxX > 0.5 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ScrollToEnd()
    {
        _offsetY = MaxOffsetY;
        UpdateBars();
        InvalidateVisual();
    }

    /// <summary>滚动到某个位置；user 时据此开关跟随（离开底部关闭，回到底部打开）。</summary>
    private void ScrollTo(double y, bool user)
    {
        _offsetY = Math.Max(0, Math.Min(y, MaxOffsetY));
        UpdateBars();
        InvalidateVisual();
        if (user)
        {
            bool atBottom = _offsetY >= MaxOffsetY - 6;
            if (atBottom != _follow) { _follow = atBottom; FollowChanged?.Invoke(atBottom); }
        }
    }

    private void SetOffsetX(double x)
    {
        _offsetX = Math.Max(0, Math.Min(x, MaxOffsetX));
        UpdateBars();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        e.Handled = true;
        int lines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
        double delta = -e.Delta / 120.0 * lines * _lineH;
        if (KeyInput.Modifiers.HasFlag(ModifierKeys.Shift) && !_wrap) SetOffsetX(_offsetX + delta);
        else ScrollTo(_offsetY + delta, user: true);
    }

    // MARK: 绘制

    private SolidColorBrush Brush(Color c)
    {
        if (_brushes.TryGetValue(c, out var b)) return b;
        b = new SolidColorBrush(c);
        b.Freeze();
        if (_brushes.Count > 512) _brushes.Clear();
        _brushes[c] = b;
        return b;
    }

    private Color Resolve(ANSIColor c, Color fallback)
    {
        switch (c.Kind)
        {
            case ANSIColorKind.Palette:
                if (c.Index < 16) return _ansi16[c.Index];
                var rgb = ANSIPalette.Rgb(c.Index);
                return rgb is { } v ? Color.FromRgb(v.R, v.G, v.B) : fallback;
            case ANSIColorKind.Rgb:
                return Color.FromRgb(c.R, c.G, c.B);
            default:
                return fallback;
        }
    }

    private void EnsureScratch(int n)
    {
        if (_rowOf.Length >= n) return;
        int size = Math.Max(n, _rowOf.Length * 2);
        _rowOf = new int[size];
        _colOf = new int[size];
        _widthOf = new int[size];
    }

    protected override void OnRender(DrawingContext dc)
    {
        var sw = Stopwatch.StartNew();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Theme.Brush(ColorToken.ConsoleBg), null, new Rect(0, 0, w, h));
        if (_lines.Count == 0 || w <= 0 || h <= 0) return;
        if (EnsureLayout())
        {
            if (_follow) _offsetY = MaxOffsetY;
            ClampOffsets();
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(UpdateBars));
        }
        int firstRow = (int)Math.Floor(Math.Max(0, _offsetY - PadY) / _lineH);
        int li = LineAtRow(firstRow);
        double dpi = _metricsDpi;
        var selRange = SelectionRange();
        for (; li < _lines.Count; li++)
        {
            double top = PadY + _rowStart[li] * _lineH - _offsetY;
            if (top >= h) break;
            RenderLine(dc, _lines[li], _firstNo + li, top, selRange, dpi);
        }
        sw.Stop();
        Renders++;
        TotalRenderMs += sw.Elapsed.TotalMilliseconds;
        MaxRenderMs = Math.Max(MaxRenderMs, sw.Elapsed.TotalMilliseconds);
    }

    private void RenderLine(DrawingContext dc, ConsoleLineData line, long no, double top, ((long Line, int Index) A, (long Line, int Index) B)? sel, double dpi)
    {
        var text = line.Text;
        int n = text.Length;
        if (n == 0) return;
        EnsureScratch(n);
        ConsoleLayout.Layout(text, _wrap ? _cols : 0, _rowOf, _colOf, _widthOf);
        double x0 = PadX - _offsetX;
        var baseKey = line.Kind switch { 1 => ColorToken.ConsoleStderr, 2 => ColorToken.ConsoleSystem, _ => ColorToken.ConsoleText };
        var baseColor = Theme.Color(baseKey);
        var bgDefault = Theme.Color(ColorToken.ConsoleBg);
        double w = ActualWidth;

        // 1) ANSI 背景
        foreach (var sp in line.Spans)
        {
            var st = sp.Style;
            if (st.Bg.IsDefault && !st.Inverse) continue;
            var fg = Resolve(st.Fg, baseColor);
            var bg = st.Bg.IsDefault ? (Color?)null : Resolve(st.Bg, Colors.Transparent);
            if (st.Inverse) bg = fg;
            if (bg is { } c) FillCells(dc, sp.Start, sp.Length, top, x0, Brush(c));
        }
        // 2) 搜索高亮
        if (_matchesByLine.TryGetValue(no, out var ms))
        {
            foreach (var mi in ms)
            {
                var m = _matches[mi];
                FillCells(dc, m.Start, m.Length, top, x0, Theme.Brush(mi == _matchIndex ? ColorToken.SearchMatchCurrent : ColorToken.SearchMatch));
            }
        }
        // 3) 选择
        if (sel is { } s && no >= s.A.Line && no <= s.B.Line)
        {
            int a = no == s.A.Line ? Math.Min(s.A.Index, n) : 0;
            int b = no == s.B.Line ? Math.Min(s.B.Index, n) : n;
            if (b > a) FillCells(dc, a, b - a, top, x0, Theme.Brush(ColorToken.ConsoleSelection));
            if (no != s.B.Line && b == n)
            {
                // 选择跨到下一行：行尾补一格，看得出换行也被选中
                int last = n - 1;
                double ex = x0 + (_colOf[last] + _widthOf[last]) * _cellW;
                double ey = top + _rowOf[last] * _lineH;
                dc.DrawRectangle(Theme.Brush(ColorToken.ConsoleSelection), null, new Rect(ex, ey, _cellW, _lineH));
            }
        }
        // 4) 文字（按样式、字体、显示行分组成 GlyphRun）
        var fonts = ConsoleFonts.Shared;
        var links = line.Links;
        var linkColor = Theme.Color(ColorToken.TextLink);
        var glyphs = new List<ushort>(64);
        var advances = new List<double>(64);
        foreach (var sp in line.Spans)
        {
            var st = sp.Style;
            var fg = Resolve(st.Fg, baseColor);
            if (st.Inverse) fg = st.Bg.IsDefault ? bgDefault : Resolve(st.Bg, bgDefault);
            if (st.Dim) fg = Color.FromArgb((byte)(fg.A * 0.6), fg.R, fg.G, fg.B);
            int style = (st.Bold ? 1 : 0) | (st.Italic ? 2 : 0);
            int end = sp.Start + sp.Length;
            int i = sp.Start;
            while (i < end)
            {
                // 一组：同一显示行、同一字体、同一链接状态
                int row = _rowOf[i];
                bool inLink = InLink(links, i);
                int face = -1;
                int groupStart = i;
                double gx = x0 + _colOf[i] * _cellW;
                glyphs.Clear();
                advances.Clear();
                while (i < end && _rowOf[i] == row && InLink(links, i) == inLink)
                {
                    char c = text[i];
                    int cw = _widthOf[i];
                    if (c == '\t' || c == ' ' || cw == 0 && !char.IsHighSurrogate(c))
                    {
                        // 空白：并进前一个字形的步进；组还没开始时挪起点
                        if (advances.Count > 0) advances[advances.Count - 1] += cw * _cellW;
                        else gx += cw * _cellW;
                        i++;
                        continue;
                    }
                    int cp = c;
                    int units = 1;
                    if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(c, text[i + 1]); units = 2; }
                    var (f, g) = fonts.Glyph(cp, style);
                    if (face >= 0 && f != face) break;
                    face = f;
                    glyphs.Add(g);
                    advances.Add(Math.Max(cw, 1) * _cellW);
                    i += units;
                }
                double y = top + row * _lineH;
                var brush = Brush(inLink ? linkColor : fg);
                if (glyphs.Count > 0 && face >= 0 && y + _lineH > 0 && y < ActualHeight)
                {
                    double sx = gx, ex = gx + advances.Sum();
                    if (ex > 0 && sx < w)
                    {
                        var origin = new Point(Math.Round(gx * dpi) / dpi, y + _baseline);
                        var run = new GlyphRun(fonts.Face(style, face), 0, false, fonts.Size, (float)dpi, glyphs.ToArray(), origin,
                            advances.ToArray(), null, null, null, null, null, null);
                        dc.DrawGlyphRun(brush, run);
                    }
                }
                // 下划线、删除线（链接带下划线）
                if ((st.Underline || inLink || st.Strikethrough) && i > groupStart)
                {
                    double lx = x0 + _colOf[groupStart] * _cellW;
                    int last = i - 1;
                    double rx = x0 + (_colOf[last] + _widthOf[last]) * _cellW;
                    var pen = new Pen(brush, 1);
                    if (st.Underline || inLink)
                    {
                        double uy = Math.Round((y + _baseline + 2) * dpi) / dpi + 0.5 / dpi;
                        dc.DrawLine(pen, new Point(lx, uy), new Point(rx, uy));
                    }
                    if (st.Strikethrough)
                    {
                        double sy = Math.Round((y + _baseline - fonts.Size * 0.3) * dpi) / dpi + 0.5 / dpi;
                        dc.DrawLine(pen, new Point(lx, sy), new Point(rx, sy));
                    }
                }
            }
        }
    }

    private static bool InLink(ConsoleLink[]? links, int i)
    {
        if (links == null) return false;
        foreach (var l in links) if (i >= l.Start && i < l.Start + l.Length) return true;
        return false;
    }

    /// <summary>给 [start, start+len) 的字画底色（按显示行合并成矩形）。</summary>
    private void FillCells(DrawingContext dc, int start, int len, double top, double x0, Brush brush)
    {
        int i = start;
        int last = start + len;
        while (i < last)
        {
            int row = _rowOf[i];
            int c0 = _colOf[i];
            int c1 = c0 + _widthOf[i];
            i++;
            while (i < last && _rowOf[i] == row) { c1 = Math.Max(c1, _colOf[i] + _widthOf[i]); i++; }
            if (c1 > c0) dc.DrawRectangle(brush, null, new Rect(x0 + c0 * _cellW, top + row * _lineH, (c1 - c0) * _cellW, _lineH));
        }
    }

    // MARK: 命中测试与选择

    /// <summary>坐标 → (绝对行号, 光标下标)；charUnder 时返回压在下面的字（找链接用）。</summary>
    private (long Line, int Index)? HitTest(Point p, bool charUnder = false)
    {
        if (_lines.Count == 0) return null;
        double cy = p.Y + _offsetY - PadY;
        int row = (int)Math.Floor(cy / _lineH);
        if (row < 0) return (_firstNo, 0);
        if (row >= _totalRows) return (_firstNo + _lines.Count - 1, _lines[_lines.Count - 1].Text.Length);
        int li = LineAtRow(row);
        var line = _lines[li];
        int rowIn = row - _rowStart[li];
        var text = line.Text;
        int n = text.Length;
        if (n == 0) return (_firstNo + li, 0);
        EnsureScratch(n);
        ConsoleLayout.Layout(text, _wrap ? _cols : 0, _rowOf, _colOf, _widthOf);
        double cell = (p.X + _offsetX - PadX) / _cellW;
        int lastOnRow = -1;
        for (int i = 0; i < n; i++)
        {
            if (_rowOf[i] != rowIn) { if (_rowOf[i] > rowIn) break; continue; }
            if (_widthOf[i] == 0) continue;
            lastOnRow = i;
            double c0 = _colOf[i], c1 = _colOf[i] + _widthOf[i];
            if (charUnder) { if (cell >= c0 && cell < c1) return (_firstNo + li, i); continue; }
            if (cell < (c0 + c1) / 2) return (_firstNo + li, i);
        }
        if (charUnder) return null;
        if (lastOnRow < 0) return (_firstNo + li, 0);
        int after = lastOnRow + (char.IsHighSurrogate(text[lastOnRow]) ? 2 : 1);
        return (_firstNo + li, Math.Min(after, n));
    }

    private ((long Line, int Index) A, (long Line, int Index) B)? SelectionRange()
    {
        if (_selAnchor is not { } a || _selActive is not { } b) return null;
        if (a.Line > b.Line || (a.Line == b.Line && a.Index > b.Index)) (a, b) = (b, a);
        if (a.Line == b.Line && a.Index == b.Index) return null;
        return (a, b);
    }

    public bool HasSelection => SelectionRange() != null;

    public string? SelectedText()
    {
        if (SelectionRange() is not { } r) return null;
        var sb = new StringBuilder();
        for (long no = Math.Max(r.A.Line, _firstNo); no <= r.B.Line && no - _firstNo < _lines.Count; no++)
        {
            var t = _lines[(int)(no - _firstNo)].Text;
            int a = no == r.A.Line ? Math.Min(r.A.Index, t.Length) : 0;
            int b = no == r.B.Line ? Math.Min(r.B.Index, t.Length) : t.Length;
            if (no > Math.Max(r.A.Line, _firstNo)) sb.Append(Environment.NewLine);
            if (b > a) sb.Append(t, a, b - a);
        }
        return sb.ToString();
    }

    public string AllText()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.Append(l.Text).Append(Environment.NewLine);
        return sb.ToString();
    }

    private string? LinkAt(Point p)
    {
        if (HitTest(p, charUnder: true) is not { } h) return null;
        var line = _lines[(int)(h.Line - _firstNo)];
        if (line.Links == null) return null;
        foreach (var l in line.Links) if (h.Index >= l.Start && h.Index < l.Start + l.Length) return l.Url;
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        if (hit == null) return;
        e.Handled = true;
        if (e.ClickCount == 2) { SelectWord(hit.Value); return; }
        if (e.ClickCount >= 3) { SelectLine(hit.Value.Line); return; }
        if (KeyInput.Modifiers.HasFlag(ModifierKeys.Shift) && _selAnchor != null) _selActive = hit;
        else { _selAnchor = hit; _selActive = hit; }
        _downAt = p;
        _dragMoved = false;
        _downLink = LinkAt(p);
        _dragging = true;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            Cursor = LinkAt(p) != null ? Cursors.Hand : Cursors.IBeam;
            return;
        }
        if (!_dragMoved && (Math.Abs(p.X - _downAt.X) > 3 || Math.Abs(p.Y - _downAt.Y) > 3)) _dragMoved = true;
        if (!_dragMoved) return;
        if (HitTest(p) is { } hit) _selActive = hit;
        _autoScrollDy = p.Y < 0 ? p.Y : (p.Y > ActualHeight ? p.Y - ActualHeight : 0);
        if (_autoScrollDy != 0) { if (!_autoScroll.IsEnabled) _autoScroll.Start(); }
        else _autoScroll.Stop();
        InvalidateVisual();
    }

    private void AutoScrollTick()
    {
        if (!_dragging || _autoScrollDy == 0) { _autoScroll.Stop(); return; }
        ScrollTo(_offsetY + Math.Sign(_autoScrollDy) * Math.Max(_lineH, Math.Abs(_autoScrollDy)), user: true);
        var p = Mouse.GetPosition(this);
        if (HitTest(p) is { } hit) _selActive = hit;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        _autoScroll.Stop();
        ReleaseMouseCapture();
        if (!_dragMoved)
        {
            _selAnchor = _selActive = null;
            InvalidateVisual();
            if (_downLink != null && LinkAt(e.GetPosition(this)) == _downLink) Actions.OpenURL(_downLink);
        }
        e.Handled = true;
    }

    private void SelectWord((long Line, int Index) hit)
    {
        var t = _lines[(int)(hit.Line - _firstNo)].Text;
        if (t.Length == 0) return;
        int i = Math.Min(hit.Index, t.Length - 1);
        bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        bool kind = Word(t[i]);
        bool space = char.IsWhiteSpace(t[i]);
        bool Same(char c) => space ? char.IsWhiteSpace(c) : (kind ? Word(c) : !Word(c) && !char.IsWhiteSpace(c));
        int a = i, b = i + 1;
        while (a > 0 && Same(t[a - 1])) a--;
        while (b < t.Length && Same(t[b])) b++;
        _selAnchor = (hit.Line, a);
        _selActive = (hit.Line, b);
        InvalidateVisual();
    }

    private void SelectLine(long no)
    {
        var t = _lines[(int)(no - _firstNo)].Text;
        _selAnchor = (no, 0);
        _selActive = (no, t.Length);
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (_lines.Count == 0) return;
        _selAnchor = (_firstNo, 0);
        _selActive = (_firstNo + _lines.Count - 1, _lines[_lines.Count - 1].Text.Length);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var mods = KeyInput.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.C)
        {
            if (SelectedText() is { } s) Actions.Copy(s);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.A) { SelectAll(); e.Handled = true; }
        else if (e.Key == Key.PageDown) { ScrollTo(_offsetY + ActualHeight - _lineH, user: true); e.Handled = true; }
        else if (e.Key == Key.PageUp) { ScrollTo(_offsetY - ActualHeight + _lineH, user: true); e.Handled = true; }
        else if (e.Key == Key.Down) { ScrollTo(_offsetY + _lineH, user: true); e.Handled = true; }
        else if (e.Key == Key.Up) { ScrollTo(_offsetY - _lineH, user: true); e.Handled = true; }
        else if (e.Key == Key.End && mods == ModifierKeys.Control) { ScrollTo(MaxOffsetY, user: true); e.Handled = true; }
        else if (e.Key == Key.Home && mods == ModifierKeys.Control) { ScrollTo(0, user: true); e.Handled = true; }
    }

    // MARK: 搜索

    public int MatchCount => _matches.Count;
    public int MatchIndex => _matchIndex;

    /// <summary>重新查找（不区分大小写，最多 5000 处）。keepIndex=false 时当前项定到最后一处并滚过去。</summary>
    public void RunSearch(string query, bool keepIndex)
    {
        _matches.Clear();
        _matchesByLine.Clear();
        if (string.IsNullOrEmpty(query))
        {
            _matchIndex = 0;
            InvalidateVisual();
            SearchChanged?.Invoke();
            return;
        }
        for (int li = 0; li < _lines.Count && _matches.Count < 5000; li++)
        {
            var t = _lines[li].Text;
            int from = 0;
            while (from <= t.Length - query.Length)
            {
                int f = t.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                if (f < 0) break;
                long no = _firstNo + li;
                if (!_matchesByLine.TryGetValue(no, out var list)) _matchesByLine[no] = list = new List<int>();
                list.Add(_matches.Count);
                _matches.Add((no, f, query.Length));
                from = f + Math.Max(query.Length, 1);
                if (_matches.Count >= 5000) break;
            }
        }
        if (!keepIndex || _matchIndex >= _matches.Count) _matchIndex = Math.Max(0, _matches.Count - 1);
        if (!keepIndex) RevealMatch();
        InvalidateVisual();
        SearchChanged?.Invoke();
    }

    /// <summary>跳到下一处 / 上一处。</summary>
    public void FindNext(string query, int delta)
    {
        if (_matches.Count == 0) { RunSearch(query, keepIndex: false); return; }
        _matchIndex = (_matchIndex + delta + _matches.Count) % _matches.Count;
        RevealMatch();
        InvalidateVisual();
        SearchChanged?.Invoke();
    }

    private void RevealMatch()
    {
        if (_matchIndex < 0 || _matchIndex >= _matches.Count) return;
        var m = _matches[_matchIndex];
        int li = (int)(m.Line - _firstNo);
        if (li < 0 || li >= _lines.Count) return;
        if (_follow) { _follow = false; FollowChanged?.Invoke(false); }
        var line = _lines[li];
        EnsureScratch(line.Text.Length);
        ConsoleLayout.Layout(line.Text, _wrap ? _cols : 0, _rowOf, _colOf, _widthOf);
        int idx = Math.Min(m.Start, Math.Max(0, line.Text.Length - 1));
        double y = PadY + (_rowStart[li] + (line.Text.Length > 0 ? _rowOf[idx] : 0)) * _lineH;
        if (y < _offsetY) _offsetY = y - PadY;
        else if (y + _lineH > _offsetY + ActualHeight) _offsetY = y + _lineH + PadY - ActualHeight;
        if (!_wrap && line.Text.Length > 0)
        {
            double x = PadX + _colOf[idx] * _cellW;
            double xe = x + m.Length * _cellW;
            if (x < _offsetX + PadX) _offsetX = Math.Max(0, x - PadX * 2);
            else if (xe > _offsetX + ActualWidth - PadX) _offsetX = xe - ActualWidth + PadX * 2;
        }
        ClampOffsets();
        UpdateBars();
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
}

#endregion

#region 控制器

/// <summary>
/// 一个控制台的数据管道：取最近 2000 行 → 从 lastSeq 订阅流；后台线程每约 50ms 把新行合并成一批、解析 ANSI，
/// 再一次性交给界面线程追加（界面线程只做追加和画可见行）。对应 Mac ConsoleController 的订阅部分。
/// </summary>
internal sealed class ConsoleController
{
    private readonly ConsoleTextView _view;
    private readonly Dispatcher _ui;
    private string? _consoleId;
    private ConsoleStream? _stream;
    private int _generation;
    private readonly object _pendingLock = new();
    private readonly object _parseLock = new();
    private List<LogLine> _pending = new();
    private bool _pumpScheduled;
    private ANSIParser _parser = new();
    private readonly int _maxLines = Math.Max(100, M.ConsoleMaxLines);

    public event Action? Ended;

    public ConsoleController(ConsoleTextView view)
    {
        _view = view;
        _ui = view.Dispatcher;
    }

    public string? ConsoleId => _consoleId;
    public bool IsAttached => _stream != null || _attaching;
    private bool _attaching;

    public async void Attach(string? id)
    {
        if (id == _consoleId && IsAttached) return;
        Detach();
        _consoleId = id;
        _view.Clear();
        _view.Follow = true; // 每次进入 / 切换到一个运行，都从日志末尾开始跟随
        if (id == null || AppModel.Shared.Api is not { } api) return;
        int gen = ++_generation;
        var parser = new ANSIParser();
        lock (_parseLock) _parser = parser;
        _attaching = true;
        long since = 0;
        try
        {
            // raw=true：内核默认去掉 ANSI（给 AI / 命令行看的），控制台要保留颜色
            var res = await api.CallAsync<LogsResult>("run.logs", Json.Object(("consoleId", id), ("tail", 2000), ("raw", true)));
            if (gen != _generation) return;
            Enqueue(gen, res.Lines, immediate: true);
            since = res.LastSeq;
        }
        catch (Exception)
        {
            if (gen != _generation) return;
        }
        finally
        {
            if (gen == _generation) _attaching = false;
        }
        if (gen != _generation) return;
        var s = new ConsoleStream(id, since);
        _stream = s;
        s.Start(api, u =>
        {
            switch (u)
            {
                case ConsoleStream.Update.Lines l:
                    Enqueue(gen, l.Value, immediate: false);
                    break;
                case ConsoleStream.Update.Ended:
                    _ui.BeginInvoke(new Action(() => { if (gen == _generation) Ended?.Invoke(); }));
                    break;
            }
        });
    }

    public void Detach()
    {
        _generation++;
        _attaching = false;
        _stream?.Stop();
        _stream = null;
        _consoleId = null;
        lock (_pendingLock) _pending = new List<LogLine>();
    }

    /// <summary>后台线程收到新行：放进待处理，约 50ms 后合并成一批处理。</summary>
    private void Enqueue(int gen, List<LogLine> lines, bool immediate)
    {
        if (lines.Count == 0) return;
        bool schedule;
        lock (_pendingLock)
        {
            if (gen != Volatile.Read(ref _generation)) return;
            _pending.AddRange(lines);
            // 积压过多时只留最后会显示的部分（完整日志在磁盘）
            if (_pending.Count > _maxLines * 4) _pending.RemoveRange(0, _pending.Count - _maxLines * 2);
            schedule = !_pumpScheduled;
            _pumpScheduled = true;
        }
        if (!schedule) return;
        if (immediate) Task.Run(() => Pump(gen));
        else Task.Delay(50).ContinueWith(_ => Pump(gen), TaskScheduler.Default);
    }

    private void Pump(int gen)
    {
        lock (_parseLock)
        {
            List<LogLine> batch;
            lock (_pendingLock)
            {
                batch = _pending;
                _pending = new List<LogLine>();
                _pumpScheduled = false;
            }
            if (gen != Volatile.Read(ref _generation) || batch.Count == 0) return;
            // 只解析会留下来的最后 maxLines 行
            int start = batch.Count > _maxLines ? batch.Count - _maxLines : 0;
            var parsed = new List<ConsoleLineData>(batch.Count - start);
            for (int i = start; i < batch.Count; i++) parsed.Add(ConsoleLineData.From(batch[i], _parser));
            _ui.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (gen != _generation) return;
                _view.Append(parsed);
                BatchAppended?.Invoke();
            }));
        }
    }

    public event Action? BatchAppended;
}

#endregion

/// <summary>
/// 控制台面板：工具栏（前面可放运行控件）+ 搜索条 + 文本区 + 可选的输入行（对应 Mac ConsolePanel）。
/// 文本区、搜索框、输入框跨重建复用同一个实例；工具栏按钮的选中态直接更新，不重建面板。
/// </summary>
public sealed class ConsolePanel : PPView
{
    private string _consoleId;
    private string? _runId;
    private string? _logFile;
    private readonly bool _compact;
    private bool? _inputEnabled;
    private UIElement? _leading;

    private readonly ConsoleTextView _text;
    private readonly ConsoleController _controller;
    private readonly Grid _surface;
    private readonly TextBox _searchBox;
    private readonly TextBlock _searchPlaceholder;
    private readonly TextBlock _matchText;
    private readonly TextBox _inputBox;
    private PPIconButton? _sendButton;
    private PPIconButton? _searchButton, _wrapButton, _followButton;
    private UIElement? _searchBar;
    private bool _searching;
    private string _query = "";
    private readonly DispatcherTimer _searchTimer;
    private bool _counted;
    private bool _subscribedFind;

    /// <param name="consoleId">控制台 id（运行 id 或任务 id）</param>
    /// <param name="runId">运行 id（有就显示输入行、清空时同时调 run.clear）</param>
    /// <param name="logFile">日志文件（「打开日志文件」按钮）</param>
    /// <param name="compact">紧凑模式（任务日志对话框、插件视图里的控制台）</param>
    /// <param name="leading">工具栏左侧附加控件（运行控件等）</param>
    /// <param name="inputEnabled">是否显示输入行（null：有 runId 就显示）</param>
    public ConsolePanel(string consoleId, string? runId, string? logFile, bool compact = false, UIElement? leading = null, bool? inputEnabled = null)
    {
        _consoleId = consoleId;
        _runId = runId;
        _logFile = logFile;
        _compact = compact;
        _leading = leading;
        _inputEnabled = inputEnabled;
        _text = new ConsoleTextView().Id("console.text");
        _text.Wrap = UiPrefs.Get("ConsoleWrap") != "0";
        _controller = new ConsoleController(_text);
        _controller.BatchAppended += () => { if (_query.Length > 0) ScheduleSearch(); UpdateStats(); };
        _text.FollowChanged += on => { if (_followButton != null) _followButton.Active = on; };
        _text.SearchChanged += UpdateMatchText;
        _surface = new Grid();
        _surface.Children.Add(_text);
        _surface.Children.Add(_text.VerticalBar);
        _surface.Children.Add(_text.HorizontalBar);

        // 搜索框（无边框，和 Mac 的 .plain 文本框一样）
        _searchBox = new TextBox { AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Center };
        Typo.Body.Apply(_searchBox);
        _searchBox.Id("console.searchField");
        _searchPlaceholder = Text(L.T("console.search"), Typo.Body, ColorToken.TextTertiary);
        _searchPlaceholder.IsHitTestVisible = false;
        _searchPlaceholder.Margin = new Thickness(2, 0, 0, 0);
        _searchPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        _searchBox.TextChanged += (_, _) =>
        {
            _query = _searchBox.Text;
            _searchPlaceholder.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            ScheduleSearch();
        };
        _searchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _text.FindNext(_query, 1);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseSearch();
            }
        };
        _matchText = Text("", Typo.Small, ColorToken.TextTertiary).TabularDigits();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _text.RunSearch(_query, keepIndex: true); };

        // 输入行（运行中写 stdin）
        _inputBox = new TextBox { AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Center };
        Typo.Mono.Apply(_inputBox);
        _inputBox.Id("console.input");
        _inputBox.TextChanged += (_, _) => { if (_sendButton != null) _sendButton.Visibility = _inputBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; };
        _inputBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && KeyInput.Modifiers == ModifierKeys.None) { e.Handled = true; SendInput(); }
        };
    }

    private bool InputShown => (_inputEnabled ?? _runId != null) && _runId != null;

    /// <summary>换成另一个控制台（运行重启后是新的运行 id）或更新运行信息；同一个控制台不会重新订阅。</summary>
    public void Update(string consoleId, string? runId, string? logFile, bool? inputEnabled)
    {
        bool chrome = runId != _runId || logFile != _logFile || inputEnabled != _inputEnabled;
        _runId = runId;
        _logFile = logFile;
        _inputEnabled = inputEnabled;
        if (consoleId != _consoleId)
        {
            _consoleId = consoleId;
            if (IsAppeared) _controller.Attach(consoleId);
        }
        if (chrome) Invalidate();
    }

    /// <summary>工具栏左侧附加控件。</summary>
    public UIElement? Leading
    {
        get => _leading;
        set
        {
            if (ReferenceEquals(_leading, value)) return;
            _leading = value;
            Invalidate();
        }
    }

    protected override void OnAppear()
    {
        if (!_counted)
        {
            _counted = true;
            AppModel.Shared.Nav.VisibleConsoles++;
        }
        if (!_subscribedFind)
        {
            _subscribedFind = true;
            AppModel.Shared.Nav.ConsoleFind += OnConsoleFind;
        }
        if (_controller.ConsoleId != _consoleId || !_controller.IsAttached) _controller.Attach(_consoleId);
    }

    protected override void OnDisappear()
    {
        // 外层视图重建时元素会被摘下又挂回去：等这一轮处理完还没回来才真正退订
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsAppeared) return;
            _controller.Detach();
            if (_counted)
            {
                _counted = false;
                var nav = AppModel.Shared.Nav;
                nav.VisibleConsoles = Math.Max(0, nav.VisibleConsoles - 1);
            }
            if (_subscribedFind)
            {
                _subscribedFind = false;
                AppModel.Shared.Nav.ConsoleFind -= OnConsoleFind;
            }
            _searchTimer.Stop();
        }));
    }

    private void OnConsoleFind(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        OpenSearch();
    }

    private void OpenSearch()
    {
        _searching = true;
        if (_searchBar != null) _searchBar.Visibility = Visibility.Visible;
        if (_searchButton != null) _searchButton.Active = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { _searchBox.Focus(); _searchBox.SelectAll(); }));
    }

    private void CloseSearch()
    {
        _searching = false;
        if (_searchBar != null) _searchBar.Visibility = Visibility.Collapsed;
        if (_searchButton != null) _searchButton.Active = false;
        _searchBox.Text = "";
        _text.Focus();
    }

    private void ScheduleSearch()
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void UpdateMatchText()
    {
        _matchText.Text = _query.Length == 0 ? "" : (_text.MatchCount == 0 ? L.T("console.noMatch") : $"{_text.MatchIndex + 1}/{_text.MatchCount}");
    }

    private void UpdateStats()
    {
        if (!DebugBridge.IsEnabled) return;
        _text.A11y(string.Format(CultureInfo.InvariantCulture,
            "lines={0} appended={1} batches={2} appendAvgMs={3:F2} appendMaxMs={4:F2} renders={5} renderAvgMs={6:F2} renderMaxMs={7:F2}",
            _text.LineCount, _text.LinesAppended, _text.Batches, _text.Batches == 0 ? 0 : _text.TotalAppendMs / _text.Batches, _text.MaxAppendMs,
            _text.Renders, _text.Renders == 0 ? 0 : _text.TotalRenderMs / _text.Renders, _text.MaxRenderMs));
    }

    private void SendInput()
    {
        if (_runId is not { } rid || _inputBox.Text.Length == 0) return;
        var text = _inputBox.Text;
        _inputBox.Text = "";
        AppModel.Shared.Perform(async api => await api.RunInputAsync(rid, text));
    }

    private void CopyAll() => Actions.Copy(_text.SelectedText() ?? _text.AllText());

    private void ClearAll()
    {
        if (_runId is { } rid) AppModel.Shared.Perform(async api => await api.ClearRunAsync(rid));
        _text.Clear();
        UpdateMatchText();
    }

    protected override UIElement Build()
    {
        UI.Detach(_surface);
        UI.Detach(_searchBox);
        UI.Detach(_searchPlaceholder);
        UI.Detach(_matchText);
        UI.Detach(_inputBox);
        if (_leading != null)
        {
            UI.Detach(_leading);
            _leading.Shrink(); // 窄的时候先压缩左侧的运行信息，右侧按钮不被挤出去
        }

        // 工具栏
        _searchButton = new PPIconButton("search", L.T("console.search") + "  Ctrl+F", () =>
        {
            if (_searching) CloseSearch(); else OpenSearch();
        }, active: _searching).Id("console.search");
        _wrapButton = new PPIconButton("wrap-text", L.T("console.wrap"), () =>
        {
            _text.Wrap = !_text.Wrap;
            UiPrefs.Set("ConsoleWrap", _text.Wrap ? "1" : "0");
            if (_wrapButton != null) _wrapButton.Active = _text.Wrap;
        }, active: _text.Wrap).Id("console.wrap");
        _followButton = new PPIconButton("arrow-down-to-line", L.T("console.follow"), () => _text.Follow = !_text.Follow, active: _text.Follow).Id("console.follow");
        var toolbar = HStack(M.Xxs,
            _leading,
            Spacer(M.Md),
            _searchButton,
            _wrapButton,
            _followButton,
            new PPIconButton("copy", L.T("console.copy"), CopyAll).Id("console.copy"),
            new PPIconButton("eraser", L.T("console.clear"), ClearAll).Id("console.clear"),
            !string.IsNullOrEmpty(_logFile) && !_compact ? new PPIconButton("file-text", L.T("console.openLog"), () => Actions.OpenFile(_logFile!)).Id("console.openLog") : null);
        var toolbarBox = Box(toolbar).Pad(M.Sm, 0).H(M.ToolbarHeight).Bg(ColorToken.BgContent);

        // 搜索条
        _searchPlaceholder.Text = L.T("console.search");
        var field = new Grid();
        field.Children.Add(_searchPlaceholder);
        field.Children.Add(_searchBox);
        var searchRow = HStack(M.Sm,
            new PPIcon("search", M.IconSmall, ColorToken.IconSecondary),
            field.Flex(),
            _matchText.Shrink(false),
            new PPIconButton("chevron-up", L.T("console.prev"), () => _text.FindNext(_query, -1), size: M.IconButtonSmall, iconSize: M.IconSmall).Id("console.prev"),
            new PPIconButton("chevron-down", L.T("console.next"), () => _text.FindNext(_query, 1), size: M.IconButtonSmall, iconSize: M.IconSmall).Id("console.next"),
            new PPIconButton("x", L.T("common.close"), CloseSearch, size: M.IconButtonSmall, iconSize: M.IconSmall).Id("console.searchClose"));
        searchRow.Align = CrossAlign.Stretch;
        foreach (UIElement c in searchRow.Children) if (c is FrameworkElement fe && !(c is Grid)) fe.VerticalAlignment = VerticalAlignment.Center;
        _searchBar = Box(searchRow).Pad(M.Md, 0).H(32).Bg(ColorToken.BgSubtle).Visible(_searching);
        UpdateMatchText();

        // 输入行
        UIElement? inputRow = null;
        if (InputShown)
        {
            _sendButton = new PPIconButton("corner-down-left", L.T("console.send"), SendInput, size: M.IconButtonSmall, iconSize: M.IconSmall).Id("console.send");
            _sendButton.Visibility = _inputBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var row = HStack(M.Sm, new PPIcon("chevron-right", M.IconSmall, ColorToken.IconSecondary), _inputBox.Flex(), _sendButton);
            row.Align = CrossAlign.Stretch;
            foreach (UIElement c in row.Children) if (c is FrameworkElement fe && c != _inputBox) fe.VerticalAlignment = VerticalAlignment.Center;
            var inputBox = Box(row).Pad(M.Md, 0).H(M.RowHeight).Bg(ColorToken.ConsoleBg);
            var col2 = VStack(0, new PPDivider(subtle: true), inputBox);
            col2.Align = CrossAlign.Stretch;
            inputRow = col2;
        }
        else _sendButton = null;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(toolbarBox.At(0, 0));
        root.Children.Add(_searchBar.At(1, 0));
        root.Children.Add(new PPDivider(subtle: true).At(2, 0));
        root.Children.Add(_surface.At(3, 0));
        if (inputRow != null) root.Children.Add(inputRow.At(4, 0));
        root.Bg(ColorToken.ConsoleBg);
        return root;
    }
}

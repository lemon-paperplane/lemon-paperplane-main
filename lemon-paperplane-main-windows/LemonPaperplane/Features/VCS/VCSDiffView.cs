using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

// 只读差异视图：并排 / 统一两种模式、行号、增删着色、块间折叠的上下文（可展开）、二进制与过大文件占位、
// 图片左右对比、上一处 / 下一处变更、按块暂存 / 取消暂存 / 丢弃 / 勾选提交。绝不编辑文件内容。（对应 Mac VCSDiffView.swift）
// 文字行自绘、只画可见行（VCSDiffPanel）；块头和折叠行是真实元素，只为可见的建。

/// <summary>差异的种类：工作区（mode）/ 提交对第 parent 个父提交 / base → head（head 为空表示工作区）</summary>
public sealed record VCSDiffKind(string Type, string? Mode = null, string? Hash = null, int Parent = 1, string? Base = null, string? Head = null)
{
    public static VCSDiffKind Working(string mode) => new("working", Mode: mode);
    public static VCSDiffKind Commit(string hash, int parent) => new("commit", Hash: hash, Parent: parent);
    public static VCSDiffKind Range(string @base, string? head) => new("range", Base: @base, Head: head);
}

/// <summary>要看的差异。对应 Mac VCSDiffTarget。</summary>
public sealed record VCSDiffTarget(string Path, string? OldPath, VCSDiffKind Kind, string? Status = null)
{
    public VCSDiffArgs Args(int context, bool ignoreWhitespace, bool noLimit)
    {
        var a = new VCSDiffArgs { Path = Path, OldPath = OldPath, Context = context };
        switch (Kind.Type)
        {
            case "working":
                a.Mode = Kind.Mode;
                break;
            case "commit":
                a.Mode = VCSDiffMode.Commit;
                a.Commit = Kind.Hash;
                if (Kind.Parent > 1) a.Parent = Kind.Parent;
                break;
            case "range":
                a.Mode = VCSDiffMode.Range;
                a.Base = Kind.Base;
                a.Head = Kind.Head;
                break;
        }
        if (ignoreWhitespace) a.IgnoreWhitespace = true;
        if (noLimit) a.NoLimit = true;
        return a;
    }

    /// <summary>旧侧版本（fileContent 的 ref；null 表示不存在）</summary>
    public string? OldRef => Kind.Type switch
    {
        "working" => Kind.Mode == VCSDiffMode.Unstaged ? ":" : "HEAD",
        "commit" => Kind.Hash + "^" + Kind.Parent,
        _ => Kind.Base,
    };

    /// <summary>新侧版本（"" 为工作区）</summary>
    public string NewRef => Kind.Type switch
    {
        "working" => Kind.Mode == VCSDiffMode.Staged ? ":" : "",
        "commit" => Kind.Hash ?? "",
        _ => Kind.Head ?? "",
    };

    public bool IsWorking => Kind.Type == "working";
}

/// <summary>块上的操作（由宿主决定）</summary>
public enum VCSHunkMode
{
    None,
    /// <summary>非暂存区模式：勾选块是否提交 + 丢弃块</summary>
    Include,
    /// <summary>暂存区模式未暂存一侧：暂存块 + 丢弃块</summary>
    Stage,
    /// <summary>暂存区模式已暂存一侧：取消暂存块</summary>
    Unstage,
}

/// <summary>差异数据与加载。对应 Mac VCSDiffModel。</summary>
public sealed class VCSDiffModel : PPObservable
{
    private VCSDiffTarget? _target;
    private VCSDiff? _diff;
    private string? _error;
    private bool _loading;
    private List<string>? _newText;
    private HashSet<int> _expandedGaps = new();
    private BitmapSource? _oldImage;
    private BitmapSource? _newImage;
    private int _oldSize;
    private int _newSize;
    private int? _busyHunk;
    private int _loadGen;
    private int _version;

    public VCSDiffTarget? Target { get => _target; private set { _target = value; Notify(); } }
    public VCSDiff? Diff { get => _diff; private set { _diff = value; _version++; Notify(); } }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }
    public bool NoLimit { get; set; }
    /// <summary>新侧全文（展开折叠的上下文时才取）</summary>
    public List<string>? NewText { get => _newText; private set { _newText = value; _version++; Notify(); } }
    public HashSet<int> ExpandedGaps { get => _expandedGaps; private set { _expandedGaps = value; Notify(); } }
    public BitmapSource? OldImage { get => _oldImage; private set { _oldImage = value; Notify(); } }
    public BitmapSource? NewImage { get => _newImage; private set { _newImage = value; Notify(); } }
    public int OldSize { get => _oldSize; private set => Set(ref _oldSize, value); }
    public int NewSize { get => _newSize; private set => Set(ref _newSize, value); }
    public int? BusyHunk { get => _busyHunk; private set { _busyHunk = value; Notify(); } }

    public VCSRepo? Repo { get; private set; }
    public const int Context = 3;

    // 布局缓存：父视图因别的状态重绘时不重新生成几千行
    private (int Version, bool SideBySide, string Expanded, VCSDiffLayout.Result Value)? _layoutCache;

    public VCSDiffLayout.Result? Layout(bool sideBySide)
    {
        if (_diff is not { } d) return null;
        var exp = string.Join(",", _expandedGaps.OrderBy(x => x));
        if (_layoutCache is { } c && c.Version == _version && c.SideBySide == sideBySide && c.Expanded == exp) return c.Value;
        var sw = Stopwatch.StartNew();
        var v = VCSDiffLayout.Build(d, sideBySide, _newText, _expandedGaps);
        VCSPerf.Record("diff.layout", sw.Elapsed.TotalMilliseconds);
        _layoutCache = (_version, sideBySide, exp, v);
        return v;
    }

    public void Set(VCSDiffTarget? t, VCSRepo repo)
    {
        Repo = repo;
        if (Equals(t, _target)) return;
        Target = t;
        Diff = null;
        Error = null;
        NoLimit = false;
        Reload(resetExpansion: true);
    }

    public async void Reload(bool resetExpansion = false)
    {
        var gen = ++_loadGen;
        if (resetExpansion)
        {
            if (_newText != null) NewText = null;
            if (_expandedGaps.Count > 0) ExpandedGaps = new HashSet<int>();
            if (_oldImage != null) OldImage = null;
            if (_newImage != null) NewImage = null;
        }
        if (_target is not { } t || Repo is not { } repo)
        {
            if (_diff != null) Diff = null;
            Error = null;
            return;
        }
        Loading = _diff == null || resetExpansion;
        try
        {
            var api = AppModel.Shared.Client();
            var d = await api.VcsDiffAsync(repo.ProjectId, t.Args(Context, VCSPrefs.DiffIgnoreWhitespace, NoLimit), repo.Root);
            if (gen != _loadGen || !Equals(_target, t)) return;
            Diff = d;
            Error = null;
            Loading = false;
            if (t.Kind == VCSDiffKind.Working(VCSDiffMode.Head)) repo.Changes.HunkCounts[t.Path] = d.Hunks.Count;
            if (d.Binary && VCSPath.IsImage(t.Path)) await LoadImages(t);
            if (_expandedGaps.Count > 0 || _newText != null) await LoadNewText();
        }
        catch (Exception e)
        {
            if (gen != _loadGen || !Equals(_target, t)) return;
            if (e is OperationCanceledException) return;
            Error = e.Message;
            Diff = null;
            Loading = false;
        }
    }

    private async Task LoadImages(VCSDiffTarget t)
    {
        if (Repo is not { } repo || AppModel.Shared.Api is not { } api) return;
        const int max = 32 << 20;
        var o = t.OldRef == null ? Task.FromResult<VCSFileContent?>(null)
            : VCSAsync.Try(() => api.VcsFileContentAsync(repo.ProjectId, new VCSFileContentArgs { Path = t.OldPath ?? t.Path, Ref = t.OldRef, MaxBytes = max }, repo.Root));
        var n = VCSAsync.Try(() => api.VcsFileContentAsync(repo.ProjectId, new VCSFileContentArgs { Path = t.Path, Ref = t.NewRef.Length == 0 ? null : t.NewRef, MaxBytes = max }, repo.Root));
        var oc = await o;
        var nc = await n;
        if (!Equals(_target, t)) return;
        OldImage = oc is { Exists: true } ? Decode(oc.Data) : null;
        NewImage = nc is { Exists: true } ? Decode(nc.Data) : null;
        OldSize = oc?.Size ?? 0;
        NewSize = nc?.Size ?? 0;
    }

    private static BitmapSource? Decode(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(data);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>取新侧全文（展开上下文用）</summary>
    public async Task LoadNewText()
    {
        if (_target is not { } t || Repo is not { } repo || _newText != null || AppModel.Shared.Api is not { } api) return;
        var c = await VCSAsync.Try(() => api.VcsFileContentAsync(repo.ProjectId,
            new VCSFileContentArgs { Path = t.Path, Ref = t.NewRef.Length == 0 ? null : t.NewRef, MaxBytes = 8 << 20 }, repo.Root));
        if (c == null || !Equals(_target, t) || !c.Exists || c.Text is not { } text) return;
        var lines = text.Split('\n').ToList();
        if (text.EndsWith("\n", StringComparison.Ordinal)) lines.RemoveAt(lines.Count - 1);
        NewText = lines.Select(l => l.EndsWith("\r", StringComparison.Ordinal) ? l.Substring(0, l.Length - 1) : l).ToList();
    }

    public async void Expand(int gap)
    {
        await LoadNewText();
        ExpandedGaps = new HashSet<int>(_expandedGaps) { gap };
    }

    public async void ExpandAll()
    {
        await LoadNewText();
        if (_diff is { } d) ExpandedGaps = new HashSet<int>(Enumerable.Range(0, d.Hunks.Count + 1));
    }

    // MARK: 按块操作

    public async void HunkOp(string op, int index, string title)
    {
        if (_diff is not { } d || _target is not { } t || Repo is not { } repo || index < 0 || index >= d.Hunks.Count) return;
        BusyHunk = index;
        var args = new VCSHunksArgs
        {
            Path = t.Path,
            Hunks = new List<int> { index },
            Context = d.Context ?? Context,
            Mode = d.Mode,
            Headers = new List<string> { d.Hunks[index].Header },
            OldPath = t.OldPath,
        };
        await repo.Run(op, args, title, quietSuccess: true);
        BusyHunk = null;
        // 状态字母可能不变（同一文件部分暂存），差异要自己刷新
        Reload();
    }
}

/// <summary>一行（统一模式）或一格（并排模式的一侧）。Kind：' ' | '+' | '-' | '~'（展开出来的上下文）</summary>
public sealed class VCSDiffCell
{
    public char Kind;
    public int Old;
    public int New;
    public string Text = "";
    public bool NoEol;
    /// <summary>显示用文字（Tab 换成 4 个空格）</summary>
    public string Display = "";

    public VCSDiffCell(char kind, int old, int @new, string text, bool noEol = false)
    {
        Kind = kind;
        Old = old;
        New = @new;
        Text = text;
        NoEol = noEol;
        Display = text.IndexOf('\t') >= 0 ? text.Replace("\t", "    ") : text;
    }
}

public enum VCSDiffRowKind { Hunk, Gap, Line, Pair }

public sealed class VCSDiffRow
{
    public VCSDiffRowKind Kind;
    /// <summary>块序号（Hunk）、折叠序号（Gap）</summary>
    public int Index;
    public string Header = "";
    /// <summary>Gap 的行数（-1 表示未知，文件末尾）</summary>
    public int Count;
    public VCSDiffCell? Cell;
    public VCSDiffCell? Left;
    public VCSDiffCell? Right;
}

/// <summary>由 diff 生成要显示的行。对应 Mac VCSDiffLayout。</summary>
public static class VCSDiffLayout
{
    public sealed class Result
    {
        public List<VCSDiffRow> Rows = new();
        /// <summary>每处变更（连续的增删）第一行的行号</summary>
        public List<int> ChangeStarts = new();
        public int MaxLine;
    }

    public static Result Build(VCSDiff d, bool sideBySide, List<string>? newText, HashSet<int> expanded)
    {
        var res = new Result();
        var rows = res.Rows;
        var hunks = d.Hunks;

        static int Range(int start, int count) => count == 0 ? start + 1 : start;

        List<VCSDiffCell> GapLines(int a, int b, int offset)
        {
            var output = new List<VCSDiffCell>();
            if (newText == null || a > b) return output;
            for (int n = a; n <= b; n++)
                if (n >= 1 && n <= newText.Count) output.Add(new VCSDiffCell('~', n + offset, n, newText[n - 1]));
            return output;
        }

        void AppendContext(List<VCSDiffCell> cells)
        {
            foreach (var c in cells)
            {
                rows.Add(sideBySide ? new VCSDiffRow { Kind = VCSDiffRowKind.Pair, Left = c, Right = c } : new VCSDiffRow { Kind = VCSDiffRowKind.Line, Cell = c });
                res.MaxLine = Math.Max(res.MaxLine, Math.Max(c.Old, c.New));
            }
        }

        int prevNewEnd = 1;
        for (int i = 0; i < hunks.Count; i++)
        {
            var h = hunks[i];
            int newA = Range(h.NewStart, h.NewLines);
            int oldA = Range(h.OldStart, h.OldLines);
            int gap = newA - prevNewEnd;
            bool isNewFile = h.OldStart == 0 && h.OldLines == 0;
            if (gap > 0 && !isNewFile)
            {
                if (expanded.Contains(i) && newText != null) AppendContext(GapLines(prevNewEnd, newA - 1, oldA - newA));
                else rows.Add(new VCSDiffRow { Kind = VCSDiffRowKind.Gap, Index = i, Count = gap });
            }
            rows.Add(new VCSDiffRow { Kind = VCSDiffRowKind.Hunk, Index = i, Header = h.Header });
            var dels = new List<VCSDiffCell>();
            var adds = new List<VCSDiffCell>();
            bool inChange = false;
            void Flush()
            {
                if (sideBySide)
                {
                    int n = Math.Max(dels.Count, adds.Count);
                    for (int k = 0; k < n; k++)
                        rows.Add(new VCSDiffRow { Kind = VCSDiffRowKind.Pair, Left = k < dels.Count ? dels[k] : null, Right = k < adds.Count ? adds[k] : null });
                }
                else
                {
                    foreach (var c in dels) rows.Add(new VCSDiffRow { Kind = VCSDiffRowKind.Line, Cell = c });
                    foreach (var c in adds) rows.Add(new VCSDiffRow { Kind = VCSDiffRowKind.Line, Cell = c });
                }
                dels = new List<VCSDiffCell>();
                adds = new List<VCSDiffCell>();
            }
            foreach (var l in h.Lines)
            {
                char kind = l.Kind.Length > 0 ? l.Kind[0] : ' ';
                var c = new VCSDiffCell(kind, l.Old, l.New, l.Text, l.NoEol);
                res.MaxLine = Math.Max(res.MaxLine, Math.Max(l.Old, l.New));
                if (kind == '-' || kind == '+')
                {
                    if (!inChange)
                    {
                        res.ChangeStarts.Add(rows.Count);
                        inChange = true;
                    }
                    if (kind == '-') dels.Add(c); else adds.Add(c);
                }
                else
                {
                    Flush();
                    inChange = false;
                    rows.Add(sideBySide ? new VCSDiffRow { Kind = VCSDiffRowKind.Pair, Left = c, Right = c } : new VCSDiffRow { Kind = VCSDiffRowKind.Line, Cell = c });
                }
            }
            Flush();
            prevNewEnd = newA + h.NewLines;
            // 文件末尾的折叠：只有取到全文才知道还剩多少行
            if (i == hunks.Count - 1 && !isNewFile && d.Status != "D")
            {
                int oldEnd = oldA + h.OldLines;
                if (newText != null)
                {
                    int remain = newText.Count - prevNewEnd + 1;
                    if (remain > 0)
                    {
                        if (expanded.Contains(hunks.Count)) AppendContext(GapLines(prevNewEnd, newText.Count, oldEnd - prevNewEnd));
                        else rows.Add(new VCSDiffRow { Kind = VCSDiffRowKind.Gap, Index = hunks.Count, Count = remain });
                    }
                }
                else
                {
                    rows.Add(new VCSDiffRow { Kind = VCSDiffRowKind.Gap, Index = hunks.Count, Count = -1 });
                }
            }
        }
        return res;
    }
}

/// <summary>
/// 差异文字区（自绘）：只画可见的行；块头、折叠行用 MakeInteractive 建真实元素（也只建可见的）。
/// 统一模式不换行时可横向滚动（块头固定在视口宽度）；并排模式每格裁剪，悬停显示全文。
/// </summary>
public sealed class VCSDiffPanel : VCSScrollPanel
{
    public const double LineH = 17;
    public const double MinRowH = 18;
    public const double GapH = 22;

    private List<VCSDiffRow> _rows = new();
    private double[] _tops = { 0 };
    private bool _sideBySide;
    private bool _wrap;
    private object? _key;
    private int _maxCells;
    private double _gutter;
    private double _lastWidth = -1;
    private readonly Dictionary<int, FrameworkElement> _realized = new();
    private readonly Dictionary<long, object> _cache = new();
    private readonly Typeface _mono = new(UIFonts.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly double _textSize = Typo.Mono.Size;
    private readonly double _numSize = Typo.MonoSmall.Size;
    private double _cw;
    private double _ncw;
    private string? _tipText;

    /// <summary>为块头 / 折叠行建元素</summary>
    public Func<VCSDiffRow, FrameworkElement>? MakeInteractive { get; set; }

    public VCSDiffPanel()
    {
        Loaded += (_, _) => Theme.Changed += OnTheme;
        Unloaded += (_, _) => Theme.Changed -= OnTheme;
        ToolTipService.SetInitialShowDelay(this, 600);
    }

    private void OnTheme(object? sender, EventArgs e)
    {
        _cache.Clear();
        InvalidateVisual();
    }

    private double Pdip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private void EnsureMetrics()
    {
        if (_cw > 0) return;
        _cw = Fmt("0", _textSize, Brushes.Black).WidthIncludingTrailingWhitespace;
        _ncw = Fmt("0", _numSize, Brushes.Black).WidthIncludingTrailingWhitespace;
        if (_cw <= 0) _cw = 7.2;
        if (_ncw <= 0) _ncw = 6.6;
    }

    private FormattedText Fmt(string s, double size, Brush b) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _mono, size, b, null, TextOptions.GetTextFormattingMode(this), Pdip);

    /// <summary>换数据（key 变了时回到顶部）</summary>
    public void SetData(List<VCSDiffRow> rows, int maxLine, bool sideBySide, bool wrap, object? key)
    {
        EnsureMetrics();
        bool reset = !Equals(key, _key);
        _rows = rows;
        _sideBySide = sideBySide;
        _wrap = wrap;
        _key = key;
        int digits = Math.Max(maxLine.ToString(CultureInfo.InvariantCulture).Length, 2);
        _gutter = digits * _ncw + 10;
        _maxCells = 0;
        if (!sideBySide && !wrap)
            foreach (var r in rows)
                if (r.Cell != null) _maxCells = Math.Max(_maxCells, Cells(r.Cell.Display) + (r.Cell.NoEol ? 3 : 0));
        _cache.Clear();
        _lastWidth = -1;
        CanHorizontallyScroll = !sideBySide && !wrap;
        if (reset) { OffsetX = 0; OffsetY = 0; }
        if (!CanHorizontallyScroll) OffsetX = 0;
        RefreshInteractive();
    }

    /// <summary>块头里的状态变了（勾选、忙碌）：重建可见的块头</summary>
    public void RefreshInteractive()
    {
        foreach (var el in _realized.Values) Children.Remove(el);
        _realized.Clear();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double LeftCols => _sideBySide ? 0 : 2 * (_gutter + 6) + 14;

    /// <summary>字符占几格（全角 2 格）</summary>
    private static int Cells(string s)
    {
        int n = 0;
        foreach (var ch in s) n += IsWide(ch) ? 2 : 1;
        return n;
    }

    private static bool IsWide(char c) =>
        c >= 0x1100 && (c <= 0x115F || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF)
                        || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6));

    /// <summary>按格数折行（贪心，宽字符放不下就换行）</summary>
    private static List<string> WrapChunks(string s, int cols)
    {
        var output = new List<string>();
        if (cols <= 0 || s.Length == 0) { output.Add(s); return output; }
        int start = 0, used = 0;
        for (int i = 0; i < s.Length; i++)
        {
            int w = IsWide(s[i]) ? 2 : 1;
            if (used + w > cols && i > start)
            {
                output.Add(s.Substring(start, i - start));
                start = i;
                used = 0;
            }
            used += w;
        }
        output.Add(s.Substring(start));
        return output;
    }

    private static int WrapCount(string s, int cols)
    {
        if (cols <= 0 || s.Length == 0) return 1;
        int lines = 1, used = 0, inLine = 0;
        foreach (var ch in s)
        {
            int w = IsWide(ch) ? 2 : 1;
            if (used + w > cols && inLine > 0) { lines++; used = 0; inLine = 0; }
            used += w;
            inLine++;
        }
        return lines;
    }

    private int ColsFor(double textWidth) => Math.Max(1, (int)Math.Floor(textWidth / _cw));

    private double UnifiedTextWidth(double w) => Math.Max(_cw * 4, w - LeftCols - 8);
    private double HalfWidth(double w) => Math.Floor((w - 1) / 2);
    private double SideTextWidth(double w) => Math.Max(_cw * 4, HalfWidth(w) - (_gutter + 6) - 8);

    private void ComputeTops(double width)
    {
        _tops = new double[_rows.Count + 1];
        double y = 0;
        int ucols = ColsFor(UnifiedTextWidth(width) - 14);
        int scols = ColsFor(SideTextWidth(width) - 14);
        for (int i = 0; i < _rows.Count; i++)
        {
            _tops[i] = y;
            var r = _rows[i];
            double h;
            switch (r.Kind)
            {
                case VCSDiffRowKind.Hunk: h = M.RowHeightCompact; break;
                case VCSDiffRowKind.Gap: h = GapH; break;
                default:
                    if (!_wrap) { h = MinRowH; break; }
                    int lines = r.Kind == VCSDiffRowKind.Line
                        ? WrapCount(r.Cell!.Display, ucols)
                        : Math.Max(r.Left == null ? 1 : WrapCount(r.Left.Display, scols), r.Right == null ? 1 : WrapCount(r.Right.Display, scols));
                    h = Math.Max(MinRowH, lines * LineH);
                    break;
            }
            y += h;
        }
        _tops[_rows.Count] = y;
    }

    private int IndexAt(double y)
    {
        int lo = 0, hi = _rows.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_tops[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return Math.Max(0, lo);
    }

    private (int First, int Last) Visible(double h)
    {
        if (_rows.Count == 0) return (0, -1);
        return (IndexAt(OffsetY), IndexAt(OffsetY + h));
    }

    protected override Size MeasureOverride(Size available)
    {
        EnsureMetrics();
        var w = double.IsInfinity(available.Width) ? 800 : available.Width;
        var h = double.IsInfinity(available.Height) ? 600 : available.Height;
        if (_lastWidth < 0 || (_wrap && Math.Abs(w - _lastWidth) > 0.5) || _tops.Length != _rows.Count + 1)
        {
            ComputeTops(w);
            if (_wrap) _cache.Clear();
            _lastWidth = w;
        }
        else if (!_wrap) _lastWidth = w;
        var extentW = CanHorizontallyScroll ? Math.Max(w, LeftCols + _maxCells * _cw + 24) : w;
        UpdateScrollInfo(new Size(w, h), new Size(extentW, _tops[_rows.Count] + M.Xl));
        var (first, last) = Visible(h);
        foreach (var k in _realized.Keys.Where(i => i < first || i > last).ToList())
        {
            Children.Remove(_realized[k]);
            _realized.Remove(k);
        }
        for (int i = first; i <= last; i++)
        {
            var r = _rows[i];
            if (r.Kind != VCSDiffRowKind.Hunk && r.Kind != VCSDiffRowKind.Gap) continue;
            if (!_realized.TryGetValue(i, out var el))
            {
                if (MakeInteractive == null) continue;
                el = MakeInteractive(r);
                _realized[i] = el;
                Children.Add(el);
            }
            el.Measure(new Size(w, _tops[i + 1] - _tops[i]));
        }
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (var kv in _realized)
            kv.Value.Arrange(new Rect(0, _tops[kv.Key] - OffsetY, final.Width, _tops[kv.Key + 1] - _tops[kv.Key]));
        return final;
    }

    /// <summary>把某一行滚到视口中间</summary>
    public void ScrollToRow(int index)
    {
        if (index < 0 || index >= _rows.Count || _tops.Length <= index) return;
        SetVerticalOffset(_tops[index] - (ViewportHeight - (_tops[index + 1] - _tops[index])) / 2);
    }

    // MARK: 绘制

    private Brush B(string key) => Theme.Brush(key);

    private static Brush? RowBg(char kind) => kind switch
    {
        '+' => Theme.Brush(ColorToken.DiffAddedBg),
        '-' => Theme.Brush(ColorToken.DiffRemovedBg),
        _ => null,
    };

    private FormattedText CachedNum(long key, int n)
    {
        if (_cache.TryGetValue(key, out var o) && o is FormattedText ft) return ft;
        ft = Fmt(n.ToString(CultureInfo.InvariantCulture), _numSize, B(ColorToken.TextTertiary));
        _cache[key] = ft;
        return ft;
    }

    private List<FormattedText> CachedText(long key, VCSDiffCell c, double textWidth)
    {
        if (_cache.TryGetValue(key, out var o) && o is List<FormattedText> list) return list;
        var brush = B(c.Kind == '~' ? ColorToken.TextSecondary : ColorToken.TextPrimary);
        list = new List<FormattedText>();
        if (_wrap)
        {
            foreach (var chunk in WrapChunks(c.Display, ColsFor(textWidth - (c.NoEol ? 14 : 0))))
                list.Add(Fmt(chunk, _textSize, brush));
        }
        else
        {
            // 不换行：太长的行只排前 2000 个字符（再多也看不到）
            var s = c.Display.Length > 2000 ? c.Display.Substring(0, 2000) : c.Display;
            list.Add(Fmt(s, _textSize, brush));
        }
        _cache[key] = list;
        return list;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var sw = Stopwatch.StartNew();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_rows.Count == 0 || _tops.Length != _rows.Count + 1) return;
        if (_cache.Count > 6000) _cache.Clear();
        var (first, last) = Visible(h);
        for (int i = first; i <= last; i++)
        {
            var r = _rows[i];
            double y = _tops[i] - OffsetY;
            double rh = _tops[i + 1] - _tops[i];
            if (r.Kind == VCSDiffRowKind.Line) DrawUnified(dc, i, r.Cell!, y, rh, w);
            else if (r.Kind == VCSDiffRowKind.Pair) DrawPair(dc, i, r, y, rh, w);
        }
        VCSPerf.Record("diff.render", sw.Elapsed.TotalMilliseconds);
    }

    private void DrawNumber(DrawingContext dc, long key, int n, double right, double y)
    {
        if (n <= 0) return;
        var ft = CachedNum(key, n);
        dc.DrawText(ft, new Point(right - ft.WidthIncludingTrailingWhitespace, y + 1.5));
    }

    private void DrawLines(DrawingContext dc, List<FormattedText> lines, double x, double y)
    {
        for (int k = 0; k < lines.Count; k++) dc.DrawText(lines[k], new Point(x, y + 0.5 + k * LineH));
    }

    private void DrawNoEol(DrawingContext dc, List<FormattedText> lines, double x, double y)
    {
        var icon = IconStore.Shared.Icon("circle-minus");
        if (icon == null || lines.Count == 0) return;
        var last = lines[lines.Count - 1];
        double ix = x + last.WidthIncludingTrailingWhitespace + 4;
        double iy = y + (lines.Count - 1) * LineH + 3;
        IconRenderer.Draw(dc, icon, new Rect(ix, iy, 11, 11), B(ColorToken.TextTertiary), M.StrokeScale(11));
    }

    private void DrawUnified(DrawingContext dc, int i, VCSDiffCell c, double y, double rh, double w)
    {
        double x0 = -OffsetX;
        double fullW = Math.Max(w, ExtentWidth);
        if (RowBg(c.Kind) is { } bg) dc.DrawRectangle(bg, null, new Rect(0, y, fullW, rh));
        DrawNumber(dc, i * 8L + 0, c.Kind == '+' ? 0 : c.Old, x0 + _gutter, y);
        DrawNumber(dc, i * 8L + 1, c.Kind == '-' ? 0 : c.New, x0 + 2 * _gutter + 6, y);
        if (c.Kind == '+' || c.Kind == '-')
        {
            var signKey = i * 8L + 2;
            if (!_cache.TryGetValue(signKey, out var so) || so is not FormattedText sft)
            {
                sft = Fmt(c.Kind.ToString(), _textSize, B(c.Kind == '+' ? ColorToken.DiffAddedText : ColorToken.DiffRemovedText));
                _cache[signKey] = sft;
            }
            dc.DrawText(sft, new Point(x0 + 2 * (_gutter + 6), y + 0.5));
        }
        double tx = x0 + LeftCols;
        var lines = CachedText(i * 8L + 3, c, UnifiedTextWidth(w) - 14);
        DrawLines(dc, lines, tx, y);
        if (c.NoEol) DrawNoEol(dc, lines, tx, y);
    }

    private void DrawPair(DrawingContext dc, int i, VCSDiffRow r, double y, double rh, double w)
    {
        double half = HalfWidth(w);
        var leftRect = new Rect(0, y, half, rh);
        var rightRect = new Rect(half + 1, y, Math.Max(0, w - half - 1), rh);
        DrawSide(dc, i, r.Left, true, leftRect, w);
        DrawSide(dc, i, r.Right, false, rightRect, w);
        dc.DrawRectangle(B(ColorToken.BorderSubtle), null, new Rect(half, y, 1, rh));
    }

    private void DrawSide(DrawingContext dc, int i, VCSDiffCell? c, bool left, Rect rect, double w)
    {
        if (c == null)
        {
            dc.DrawRectangle(B(ColorToken.BgSubtle), null, rect);
            return;
        }
        if (RowBg(c.Kind) is { } bg) dc.DrawRectangle(bg, null, rect);
        dc.PushClip(new RectangleGeometry(rect));
        DrawNumber(dc, i * 8L + (left ? 4 : 5), left ? c.Old : c.New, rect.X + _gutter, rect.Y);
        double tx = rect.X + _gutter + 6;
        var lines = CachedText(i * 8L + (left ? 6 : 7), c, SideTextWidth(w));
        DrawLines(dc, lines, tx, rect.Y);
        if (c.NoEol) DrawNoEol(dc, lines, tx, rect.Y);
        dc.Pop();
    }

    // MARK: 悬停：并排模式被截断的格子显示全文

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? tip = null;
        if (_sideBySide && !_wrap && _rows.Count > 0 && _tops.Length == _rows.Count + 1)
        {
            var p = e.GetPosition(this);
            var i = IndexAt(p.Y + OffsetY);
            if (i < _rows.Count && _rows[i].Kind == VCSDiffRowKind.Pair)
            {
                var half = HalfWidth(ActualWidth);
                var c = p.X < half ? _rows[i].Left : _rows[i].Right;
                if (c != null && Cells(c.Display) * _cw > SideTextWidth(ActualWidth)) tip = c.Display;
            }
        }
        if (tip != _tipText)
        {
            _tipText = tip;
            ToolTip = tip == null ? null : new ToolTip { Content = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, FontFamily = UIFonts.Mono } };
        }
    }
}

/// <summary>差异视图（含工具栏）。对应 Mac VCSDiffView。</summary>
public sealed class VCSDiffView : PPView
{
    private readonly VCSDiffModel _model;
    private readonly VCSChangesModel _changes;
    private readonly Func<VCSHunkMode>? _hunkMode;
    private readonly bool _showPath;
    private readonly State<bool> _sideBySide = new(VCSPrefs.DiffSideBySide);
    private readonly State<bool> _wrap = new(VCSPrefs.DiffWrap);
    private readonly State<bool> _ignoreWS = new(VCSPrefs.DiffIgnoreWhitespace);
    private int _currentChange = -1;
    private VCSDiffTarget? _lastTarget;
    private VCSDiffLayout.Result? _shownLayout;
    private bool _shownSide;
    private bool _shownWrap;

    /// <param name="hunkMode">块上的操作（随宿主状态变化，每次重建时取）</param>
    public VCSDiffView(VCSDiffModel model, VCSChangesModel changes, Func<VCSHunkMode>? hunkMode = null, bool showPath = true)
    {
        _model = model;
        _changes = changes;
        _hunkMode = hunkMode;
        _showPath = showPath;
        Observe(model);
        Observe(changes, VCSChangesModel.ChecksProperty, nameof(VCSChangesModel.Selection), nameof(VCSChangesModel.StagingArea));
        Observe(_sideBySide);
        Observe(_wrap);
        Observe(_ignoreWS);
    }

    /// <summary>忽略空白时块的划分与插件按块操作用的 diff 不同，不给块操作</summary>
    private VCSHunkMode EffectiveHunkMode => _ignoreWS.Value ? VCSHunkMode.None : _hunkMode?.Invoke() ?? VCSHunkMode.None;

    protected override UIElement Build()
    {
        if (!Equals(_model.Target, _lastTarget))
        {
            _lastTarget = _model.Target;
            _currentChange = -1;
        }
        var layout = _model.Layout(_sideBySide.Value);
        var col = VStack(0, Toolbar(layout), new PPDivider(subtle: true), Content(layout).Flex());
        col.Align = CrossAlign.Stretch;
        return Box(col).Bg(ColorToken.BgContent);
    }

    // MARK: 工具栏

    private UIElement Toolbar(VCSDiffLayout.Result? layout)
    {
        var d = _model.Diff;
        var row = HStack(M.Sm);
        if (_showPath && _model.Target is { } t)
        {
            if ((d?.Status ?? t.Status) is { } s) row.Children.Add(VCSViews.StatusLetter(s));
            row.Children.Add(Text(VCSPath.Name(t.Path), Typo.BodyStrong).MinW(48));
            var old = t.OldPath ?? d?.OldPath;
            row.Children.Add(old != null && old != t.Path
                ? TextMiddle(old + " → " + t.Path, Typo.Small, ColorToken.TextTertiary)
                : TextMiddle(VCSPath.Dir(t.Path), Typo.Small, ColorToken.TextTertiary));
            if (d != null) row.Children.Add(VCSViews.LineStats(d.Additions, d.Deletions));
        }
        row.Children.Add(Spacer(M.Md));
        if (_model.Loading) row.Children.Add(new PPSpinner(M.IconSmall));
        if (layout != null && layout.ChangeStarts.Count > 0)
        {
            var starts = layout.ChangeStarts;
            row.Children.Add(Text($"{Math.Max(_currentChange + 1, 0)}/{starts.Count}", Typo.MonoSmall, ColorToken.TextTertiary).Shrink(false));
            row.Children.Add(new PPIconButton("arrow-up", L.T("vcs.diff.prev"), () => Jump(-1, starts), size: M.IconButtonSmall, iconSize: M.IconSmall)
                .Disabled(_currentChange <= 0).Id("vcs.diff.prev"));
            row.Children.Add(new PPIconButton("arrow-down", L.T("vcs.diff.next"), () => Jump(1, starts), size: M.IconButtonSmall, iconSize: M.IconSmall)
                .Disabled(_currentChange >= starts.Count - 1).Id("vcs.diff.next"));
        }
        row.Children.Add(new PPIconButton("unfold-vertical", L.T("vcs.diff.expandAll"), _model.ExpandAll, size: M.IconButtonSmall, iconSize: M.IconSmall)
            .Disabled(d == null || d.Hunks.Count == 0));
        row.Children.Add(new PPIconButton("wrap-text", L.T("vcs.diff.wrap"), () => { _wrap.Value = !_wrap.Value; VCSPrefs.DiffWrap = _wrap.Value; },
            size: M.IconButtonSmall, iconSize: M.IconSmall, active: _wrap.Value).Id("vcs.diff.wrap"));
        row.Children.Add(new PPIconButton("case-sensitive", L.T("vcs.diff.ignoreWhitespace"), () =>
        {
            _ignoreWS.Value = !_ignoreWS.Value;
            VCSPrefs.DiffIgnoreWhitespace = _ignoreWS.Value;
            _model.Reload();
        }, size: M.IconButtonSmall, iconSize: M.IconSmall, active: _ignoreWS.Value));
        row.Children.Add(new PPSegmented<bool>(new[] { (true, L.T("vcs.diff.sideBySide")), (false, L.T("vcs.diff.unified")) },
            Bind.Of(() => _sideBySide.Value, v => { _sideBySide.Value = v; VCSPrefs.DiffSideBySide = v; _currentChange = -1; }), PPButtonSize.Small).Id("vcs.diff.mode"));
        row.Padding = new Thickness(M.Md, 0, M.Md, 0);
        row.Height = M.ToolbarHeight;
        return row;
    }

    private void Jump(int delta, List<int> starts)
    {
        _currentChange = Math.Min(Math.Max(_currentChange + delta, 0), starts.Count - 1);
        Invalidate();
        var target = starts[_currentChange];
        // 在重建与滚动位置恢复（Loaded 优先级）之后再滚
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => Panel().ScrollToRow(target)));
    }

    // MARK: 内容

    private UIElement Content(VCSDiffLayout.Result? layout)
    {
        if (_model.Target == null) return new PPEmptyState("file-diff", L.T("vcs.diff.none"));
        if (_model.Error is { } e)
            return new PPAlert("danger", L.T("vcs.diff.failed"), e, new PPButton(L.T("connection.retry"), () => _model.Reload(), size: PPButtonSize.Small))
                .Margins(M.Lg).Top();
        if (_model.Diff is { } d)
        {
            if (d.Binary) return Binary(d);
            if (d.TooLarge)
                return new PPEmptyState("file-text", L.T("vcs.diff.tooLarge"), L.T("vcs.diff.showAnyway"), () =>
                {
                    _model.NoLimit = true;
                    _model.Reload();
                });
            if (d.Hunks.Count == 0) return Identical(d);
            if (layout != null) return Lines(layout);
        }
        return new PPSpinner().Center();
    }

    private UIElement Identical(VCSDiff d)
    {
        var col = VStack(M.Md,
            new PPIcon("file-check", M.IconEmpty, ColorToken.IconSecondary).HCenter(),
            Text(L.T("vcs.diff.identical"), Typo.Body, ColorToken.TextSecondary).HCenter(),
            d.OldMode != null && d.NewMode != null && d.OldMode != d.NewMode
                ? Text(L.T("vcs.diff.modeChanged", d.OldMode, d.NewMode), Typo.MonoSmall, ColorToken.TextTertiary).HCenter() : null,
            d.Similarity is int s && s > 0 && d.OldPath != null
                ? Text(L.T("vcs.diff.similarity", s), Typo.Small, ColorToken.TextTertiary).HCenter() : null);
        col.Align = CrossAlign.Center;
        return col.Center();
    }

    private UIElement Binary(VCSDiff d)
    {
        if (_model.Target is { } t && VCSPath.IsImage(t.Path))
        {
            var g = Grid("*,1,*", null,
                ImagePane(_model.OldImage, _model.OldSize, L.T("vcs.diff.before")).At(0, 0),
                new PPDivider(vertical: true).At(0, 1),
                ImagePane(_model.NewImage, _model.NewSize, L.T("vcs.diff.after")).At(0, 2));
            return g;
        }
        return new PPEmptyState("binary", L.T("vcs.diff.binary"));
    }

    private UIElement ImagePane(BitmapSource? img, int size, string title)
    {
        var head = HStack(M.Sm, Text(title, Typo.Label, ColorToken.TextSecondary).Shrink(false),
            img != null ? Text($"{img.PixelWidth}×{img.PixelHeight}  ·  {Format.Bytes(size)}", Typo.MonoSmall, ColorToken.TextTertiary) : null,
            Spacer());
        head.Padding = new Thickness(M.Md, 0, M.Md, 0);
        head.Height = M.RowHeight;
        UIElement body;
        if (img != null)
        {
            var image = new Image { Source = img, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            image.MaxWidth = Math.Max(img.PixelWidth, 64) * 4;
            image.MaxHeight = Math.Max(img.PixelHeight, 64) * 4;
            image.Margin = new Thickness(M.Lg);
            body = ZStack(new VCSCheckerboard(), image);
        }
        else
        {
            body = ZStack(Text(L.T("vcs.diff.noFile"), Typo.Body, ColorToken.TextTertiary).Center());
        }
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(head.At(0, 0));
        g.Children.Add(body.At(1, 0));
        return g;
    }

    // 文字区跨重建复用同一个实例（滚动位置、绘制缓存都在里面）
    private VCSDiffPanel? _panel;
    private ScrollViewer? _scroll;

    private VCSDiffPanel Panel()
    {
        if (_panel != null) return _panel;
        _panel = new VCSDiffPanel { MakeInteractive = r => r.Kind == VCSDiffRowKind.Hunk ? HunkHeader(r.Index, r.Header) : GapRow(r.Index, r.Count) };
        _scroll = _panel.InScroll(horizontal: true);
        _scroll.Bg(ColorToken.BgContent).Id("vcs.diff.lines");
        return _panel;
    }

    private UIElement Lines(VCSDiffLayout.Result layout)
    {
        var panel = Panel();
        bool side = _sideBySide.Value, wrap = _wrap.Value;
        if (!ReferenceEquals(layout, _shownLayout) || side != _shownSide || wrap != _shownWrap)
        {
            panel.SetData(layout.Rows, layout.MaxLine, side, wrap, _model.Target);
            _shownLayout = layout;
            _shownSide = side;
            _shownWrap = wrap;
        }
        else
        {
            panel.RefreshInteractive();
        }
        var sv = _scroll!;
        UI.Detach(sv);
        sv.HorizontalScrollBarVisibility = !side && !wrap ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        // 重建后 PPView 按键恢复滚动位置：键里带上目标，换文件时不会把上一个文件的位置恢复回来
        sv.Key("vcs.diff:" + (_model.Target?.GetHashCode() ?? 0).ToString(CultureInfo.InvariantCulture));
        return sv;
    }

    private FrameworkElement HunkHeader(int i, string header)
    {
        var mode = EffectiveHunkMode;
        var d = _model.Diff;
        var row = HStack(M.Sm);
        if (mode == VCSHunkMode.Include && _model.Target?.Path is { } path && d != null)
        {
            bool excluded = _changes.ExcludedHunks.TryGetValue(path, out var ex) && ex.Contains(i);
            int total = d.Hunks.Count;
            row.Children.Add(new PPCheckbox(Bind.Of(() => !excluded, on => _changes.SetHunk(path, i, on, total))).Tip(L.T("vcs.diff.includeHunk")));
        }
        row.Children.Add(Text(header, Typo.MonoSmall, ColorToken.TextTertiary));
        row.Children.Add(Spacer(M.Md));
        if (_model.BusyHunk == i) row.Children.Add(new PPSpinner(M.IconSmall));
        switch (mode)
        {
            case VCSHunkMode.Stage:
                row.Children.Add(new PPButton(L.T("vcs.diff.stageHunk"), () => _model.HunkOp(VCSOp.StageHunks, i, L.T("vcs.diff.stageHunk")),
                    icon: "plus", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("vcs.diff.stageHunk." + i));
                row.Children.Add(DiscardHunkButton(i));
                break;
            case VCSHunkMode.Unstage:
                row.Children.Add(new PPButton(L.T("vcs.diff.unstageHunk"), () => _model.HunkOp(VCSOp.UnstageHunks, i, L.T("vcs.diff.unstageHunk")),
                    icon: "minus", kind: PPButtonKind.Ghost, size: PPButtonSize.Small).Id("vcs.diff.unstageHunk." + i));
                break;
            case VCSHunkMode.Include:
                row.Children.Add(DiscardHunkButton(i));
                break;
        }
        row.Padding = new Thickness(M.Md, 0, M.Md, 0);
        row.Height = M.RowHeightCompact;
        return row.Bg(ColorToken.DiffHunkBg);
    }

    private UIElement DiscardHunkButton(int i) =>
        new PPButton(L.T("vcs.diff.discardHunk"), () =>
            Dialogs.Confirm(L.T("vcs.diff.discardHunk.title"), L.T("vcs.diff.discardHunk.message"),
                () => _model.HunkOp(VCSOp.DiscardHunks, i, L.T("vcs.diff.discardHunk")), ok: L.T("vcs.discard"), danger: true),
            icon: "undo-2", kind: PPButtonKind.Ghost, size: PPButtonSize.Small);

    private FrameworkElement GapRow(int i, int count)
    {
        var row = HStack(M.Sm, new PPIcon("unfold-vertical", 12, ColorToken.IconSecondary),
            Text(count < 0 ? L.T("vcs.diff.expandRest") : L.T("vcs.diff.unchangedLines", count), Typo.Small, ColorToken.TextTertiary),
            Spacer());
        row.Padding = new Thickness(M.Md, 0, M.Md, 0);
        row.Height = VCSDiffPanel.GapH;
        row.Bg(ColorToken.BgSubtle);
        return row.OnClick(() => _model.Expand(i)).Tip(L.T("vcs.diff.expand")).Id("vcs.diff.gap." + i);
    }
}

/// <summary>透明图片底下的棋盘格</summary>
public sealed class VCSCheckerboard : FrameworkElement
{
    public VCSCheckerboard()
    {
        Loaded += (_, _) => Theme.Changed += OnTheme;
        Unloaded += (_, _) => Theme.Changed -= OnTheme;
        IsHitTestVisible = false;
    }

    private void OnTheme(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        const double s = 8;
        var b = Theme.Brush(ColorToken.NeutralSubtle);
        int row = 0;
        for (double y = 0; y < ActualHeight; y += s, row++)
            for (double x = row % 2 == 0 ? 0 : s; x < ActualWidth; x += s * 2)
                dc.DrawRectangle(b, null, new Rect(x, y, s, s));
    }
}

/// <summary>
/// 性能采样（自测用）：环境变量 PAPERPLANE_VCS_PERF=&lt;文件&gt; 时把差异 / 提交图的布局与绘制耗时汇总写到这个文件（每 2 秒一次）。
/// </summary>
public static class VCSPerf
{
    private static readonly string? FilePath = Environment.GetEnvironmentVariable("PAPERPLANE_VCS_PERF");
    private static readonly Dictionary<string, List<double>> Samples = new();
    private static System.Windows.Threading.DispatcherTimer? _timer;
    private static bool _dirty;

    public static bool Enabled => !string.IsNullOrEmpty(FilePath);

    public static void Record(string name, double ms)
    {
        if (!Enabled) return;
        if (!Samples.TryGetValue(name, out var list)) Samples[name] = list = new List<double>();
        list.Add(ms);
        if (list.Count > 5000) list.RemoveRange(0, 1000);
        _dirty = true;
        if (_timer == null)
        {
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Flush();
            _timer.Start();
        }
    }

    private static void Flush()
    {
        if (!_dirty || FilePath == null) return;
        _dirty = false;
        var lines = Samples.OrderBy(kv => kv.Key).Select(kv =>
        {
            var s = kv.Value.OrderBy(x => x).ToList();
            double P(double q) => s[Math.Min(s.Count - 1, (int)Math.Floor(q * s.Count))];
            return string.Format(CultureInfo.InvariantCulture, "{0}\tn={1}\tavg={2:F2}\tp50={3:F2}\tp95={4:F2}\tmax={5:F2}",
                kv.Key, s.Count, s.Average(), P(0.5), P(0.95), s[s.Count - 1]);
        });
        try { File.WriteAllLines(FilePath, lines); } catch { }
    }

    public static void Reset() => Samples.Clear();
}

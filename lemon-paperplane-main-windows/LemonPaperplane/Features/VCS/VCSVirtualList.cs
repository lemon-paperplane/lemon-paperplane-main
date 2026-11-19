using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LemonPaperplane;

// 版本控制里的长列表：只建 / 只画可见的行（对应 Mac 的 LazyVStack）。
// VCSScrollPanel 实现 IScrollInfo（放进 CanContentScroll 的 ScrollViewer，滚动条仍是全局的细滚动条）；
// VCSVirtualList 为可见行建真实元素（行里有复选框、按钮、右键菜单）；差异视图和提交图在各自文件里继承它自绘。

/// <summary>像素滚动的 IScrollInfo 面板基类：子类在 MeasureOverride 里调 UpdateScrollInfo(视口, 内容尺寸)。</summary>
public abstract class VCSScrollPanel : Panel, IScrollInfo
{
    protected double OffsetX;
    protected double OffsetY;
    private Size _viewport;
    private Size _extent;

    protected VCSScrollPanel()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
    }

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => OffsetX;
    public double VerticalOffset => OffsetY;
    public ScrollViewer? ScrollOwner { get; set; }

    protected virtual double LineStep => 16;
    private static double WheelStep => Math.Max(1, SystemParameters.WheelScrollLines) * 16;

    public void LineUp() => SetVerticalOffset(OffsetY - LineStep);
    public void LineDown() => SetVerticalOffset(OffsetY + LineStep);
    public void LineLeft() => SetHorizontalOffset(OffsetX - LineStep);
    public void LineRight() => SetHorizontalOffset(OffsetX + LineStep);
    public void PageUp() => SetVerticalOffset(OffsetY - Math.Max(LineStep, _viewport.Height - LineStep));
    public void PageDown() => SetVerticalOffset(OffsetY + Math.Max(LineStep, _viewport.Height - LineStep));
    public void PageLeft() => SetHorizontalOffset(OffsetX - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(OffsetX + _viewport.Width);
    public void MouseWheelUp() => SetVerticalOffset(OffsetY - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(OffsetY + WheelStep);
    public void MouseWheelLeft() => SetHorizontalOffset(OffsetX - WheelStep);
    public void MouseWheelRight() => SetHorizontalOffset(OffsetX + WheelStep);

    public void SetHorizontalOffset(double offset)
    {
        var o = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Width - _viewport.Width)));
        if (double.IsNaN(o) || Math.Abs(o - OffsetX) < 0.01) return;
        OffsetX = o;
        OnScrolled();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        var o = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (double.IsNaN(o) || Math.Abs(o - OffsetY) < 0.01) return;
        OffsetY = o;
        OnScrolled();
        ScrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>滚动后：默认重新布局并重画</summary>
    protected virtual void OnScrolled()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    public virtual Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (!IsAncestorOf(visual)) return Rect.Empty;
        var r = visual.TransformToAncestor(this).TransformBounds(rectangle);
        if (r.Top < 0) SetVerticalOffset(OffsetY + r.Top);
        else if (r.Bottom > _viewport.Height) SetVerticalOffset(OffsetY + Math.Min(r.Top, r.Bottom - _viewport.Height));
        return rectangle;
    }

    /// <summary>更新视口与内容尺寸（MeasureOverride 里调用），越界的偏移收回来</summary>
    protected void UpdateScrollInfo(Size viewport, Size extent)
    {
        if (double.IsInfinity(viewport.Width)) viewport.Width = extent.Width;
        if (double.IsInfinity(viewport.Height)) viewport.Height = extent.Height;
        bool changed = viewport != _viewport || extent != _extent;
        _viewport = viewport;
        _extent = extent;
        var maxY = Math.Max(0, extent.Height - viewport.Height);
        var maxX = Math.Max(0, extent.Width - viewport.Width);
        if (OffsetY > maxY) { OffsetY = maxY; changed = true; }
        if (OffsetX > maxX) { OffsetX = maxX; changed = true; }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>放进 ScrollViewer（细滚动条叠在内容上）</summary>
    public ScrollViewer InScroll(bool horizontal = false) => new()
    {
        Content = this,
        CanContentScroll = true,
        HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Focusable = false,
        Padding = new Thickness(0),
    };
}

/// <summary>虚拟列表的一行：键（重建时认出同一行）、高度、建元素的函数。</summary>
public sealed class VRow
{
    public string Key { get; }
    public double Height { get; }
    public Func<FrameworkElement> Make { get; }

    public VRow(string key, double height, Func<FrameworkElement> make)
    {
        Key = key;
        Height = height;
        Make = make;
    }
}

/// <summary>只为可见行建元素的纵向列表（行高固定在 VRow 里）。SetRows 换数据后可见行重建，滚动位置保留。</summary>
public sealed class VCSVirtualList : VCSScrollPanel
{
    private IReadOnlyList<VRow> _rows = Array.Empty<VRow>();
    private double[] _tops = new double[1];
    private readonly Dictionary<int, FrameworkElement> _realized = new();
    private Thickness _padding;
    private int _endNotified = -1;

    /// <summary>滚到接近末尾（剩 threshold 行以内）时调用（分页加载）</summary>
    public Action? OnEndReached { get; set; }
    public int EndThreshold { get; set; } = 20;
    /// <summary>按内容定高（不撑满可用高度），弹出框、对话框里的短列表用</summary>
    public bool FitContent { get; set; }

    public VCSVirtualList(Thickness padding = default)
    {
        _padding = padding;
    }

    public int Count => _rows.Count;

    public void SetRows(IReadOnlyList<VRow> rows)
    {
        _rows = rows;
        _tops = new double[rows.Count + 1];
        double y = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            _tops[i] = y;
            y += rows[i].Height;
        }
        _tops[rows.Count] = y;
        foreach (var el in _realized.Values) Children.Remove(el);
        _realized.Clear();
        _endNotified = -1;
        InvalidateMeasure();
    }

    private double ContentHeight => _tops[_rows.Count] + _padding.Top + _padding.Bottom;

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

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? 400 : available.Width;
        var h = double.IsInfinity(available.Height) ? ContentHeight : available.Height;
        UpdateScrollInfo(new Size(w, h), new Size(w, ContentHeight));
        int first = -1, last = -2;
        if (_rows.Count > 0)
        {
            var top = OffsetY - _padding.Top;
            first = IndexAt(Math.Max(0, top));
            last = IndexAt(Math.Max(0, top + h));
        }
        // 移除不可见的
        foreach (var k in _realized.Keys.Where(i => i < first || i > last).ToList())
        {
            Children.Remove(_realized[k]);
            _realized.Remove(k);
        }
        var rowW = Math.Max(0, w - _padding.Left - _padding.Right);
        for (int i = first; i <= last && i >= 0; i++)
        {
            if (!_realized.TryGetValue(i, out var el))
            {
                el = _rows[i].Make();
                _realized[i] = el;
                Children.Add(el);
            }
            el.Measure(new Size(rowW, _rows[i].Height));
        }
        if (OnEndReached != null && last >= 0 && last >= _rows.Count - EndThreshold && _endNotified != _rows.Count)
        {
            _endNotified = _rows.Count;
            var cb = OnEndReached;
            Dispatcher.BeginInvoke(new Action(() => cb()));
        }
        // 内容比可用高度矮时只要内容高度（弹出框里按内容定高）
        return new Size(w, FitContent ? Math.Min(h, ContentHeight) : h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var rowW = Math.Max(0, final.Width - _padding.Left - _padding.Right);
        foreach (var kv in _realized)
        {
            var y = _tops[kv.Key] - OffsetY + _padding.Top;
            kv.Value.Arrange(new Rect(_padding.Left, y, rowW, _rows[kv.Key].Height));
        }
        return final;
    }

    /// <summary>滚到某一行（居中）</summary>
    public void ScrollTo(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        SetVerticalOffset(_tops[index] + _padding.Top - (ViewportHeight - _rows[index].Height) / 2);
    }

    public int IndexOf(string key)
    {
        for (int i = 0; i < _rows.Count; i++) if (_rows[i].Key == key) return i;
        return -1;
    }
}

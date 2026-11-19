using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace LemonPaperplane;

public enum CaptionKind { Minimize, Maximize, Close }

/// <summary>
/// 窗口按钮（Windows 11 样式）：46×40，最小化 / 最大化（还原）/ 关闭，图形自绘（不用 Segoe 图标字体）。
/// 悬停 bgHover、按下 bgPressed；关闭按钮悬停 danger 底白色图形。窗口不活动时图形变淡。
/// 最大化按钮的悬停 / 按下由窗口的 WM_NCHITTEST（HTMAXBUTTON，触发系统贴靠布局）驱动，见 MainWindow。
/// </summary>
public sealed class CaptionButton : PPButtonBase
{
    private readonly Border _bg;
    private readonly CaptionGlyph _glyph;
    private bool _ncHover;
    private bool _ncPressed;

    public CaptionKind Kind { get; }

    public CaptionButton(CaptionKind kind)
    {
        Kind = kind;
        Focusable = false;
        IsTabStop = false;
        _glyph = new CaptionGlyph(kind);
        _bg = new Border { Width = 46, Height = M.ToolbarHeight, Child = _glyph, SnapsToDevicePixels = true };
        Content = _bg;
        VerticalAlignment = VerticalAlignment.Top;
        WindowChrome.SetIsHitTestVisibleInChrome(this, true);
        this.Id(kind switch { CaptionKind.Minimize => "window.minimize", CaptionKind.Maximize => "window.maximize", _ => "window.close" });
        UpdateName();
        UpdateVisual();
    }

    /// <summary>窗口最大化时最大化按钮显示「还原」图形。</summary>
    public bool Restore
    {
        get => _glyph.Restore;
        set { _glyph.Restore = value; _glyph.InvalidateVisual(); UpdateName(); }
    }

    /// <summary>无障碍名字与提示（最大化按钮在窗口最大化后叫「向下还原」）。</summary>
    private void UpdateName()
    {
        var name = Kind switch
        {
            CaptionKind.Minimize => L.T("window.minimize"),
            CaptionKind.Maximize => L.T(_glyph.Restore ? "window.restore" : "window.maximize"),
            _ => L.T("common.close"),
        };
        this.A11y(name);
        // 最大化按钮悬停时系统会弹出贴靠布局，不再叠一个提示
        if (Kind != CaptionKind.Maximize) ToolTip = name;
    }

    /// <summary>窗口是否活动（不活动时图形变淡）。</summary>
    public bool WindowActive { get; set; } = true;

    /// <summary>非客户区悬停 / 按下（最大化按钮走 WM_NCHITTEST，WPF 收不到鼠标事件）。</summary>
    public bool NcHover
    {
        get => _ncHover;
        set { if (_ncHover != value) { _ncHover = value; UpdateVisual(); } }
    }

    public bool NcPressed
    {
        get => _ncPressed;
        set { if (_ncPressed != value) { _ncPressed = value; UpdateVisual(); } }
    }

    public override void UpdateVisual()
    {
        if (_bg == null) return;
        bool hover = Hover || _ncHover, pressed = Pressed || _ncPressed;
        if (Kind == CaptionKind.Close)
        {
            _bg.Bg(pressed ? ColorToken.DangerHover : hover ? ColorToken.Danger : ColorToken.Clear);
            _bg.Fg(hover || pressed ? ColorToken.TextOnAccent : WindowActive ? ColorToken.TextPrimary : ColorToken.TextTertiary);
        }
        else
        {
            _bg.Bg(pressed ? ColorToken.BgPressed : hover ? ColorToken.BgHover : ColorToken.Clear);
            _bg.Fg(WindowActive || hover ? ColorToken.TextPrimary : ColorToken.TextTertiary);
        }
    }

    /// <summary>窗口按钮里的图形（10×10，1px 线）。</summary>
    private sealed class CaptionGlyph : FrameworkElement
    {
        public static readonly DependencyProperty ForegroundProperty = PPIcon.ForegroundProperty.AddOwner(typeof(CaptionGlyph),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        private readonly CaptionKind _kind;
        public bool Restore;

        public CaptionGlyph(CaptionKind kind)
        {
            _kind = kind;
            Width = 10;
            Height = 10;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var brush = (Brush)GetValue(ForegroundProperty);
            double px = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX; // 1 个设备像素
            var pen = new Pen(brush, Math.Max(px, Math.Round(1 / px) * px)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            double h = pen.Thickness / 2;
            switch (_kind)
            {
                case CaptionKind.Minimize:
                    dc.DrawLine(pen, new Point(0, 5 + h), new Point(10, 5 + h));
                    break;
                case CaptionKind.Maximize when !Restore:
                    dc.DrawRoundedRectangle(null, pen, new Rect(h, h, 10 - 2 * h, 10 - 2 * h), 1.5, 1.5);
                    break;
                case CaptionKind.Maximize:
                {
                    // 还原：前面一个 8×8，后面露出右上角
                    dc.DrawRoundedRectangle(null, pen, new Rect(h, 2 + h, 8 - 2 * h, 8 - 2 * h), 1.5, 1.5);
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(2 + h, h + 0.5), false, false);
                        c.LineTo(new Point(8.5, h), true, true);
                        c.ArcTo(new Point(10 - h, 1.5 + h), new Size(1.5, 1.5), 0, false, SweepDirection.Clockwise, true, true);
                        c.LineTo(new Point(10 - h, 7.5), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, pen, g);
                    break;
                }
                default:
                {
                    var p = new Pen(brush, pen.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawLine(p, new Point(0.5, 0.5), new Point(9.5, 9.5));
                    dc.DrawLine(p, new Point(9.5, 0.5), new Point(0.5, 9.5));
                    break;
                }
            }
        }
    }
}

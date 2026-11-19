using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperplaneKit;
using MColor = System.Windows.Media.Color;

namespace LemonPaperplane;

/// <summary>
/// 图标引用：内置（shared/icons/&lt;name&gt;.svg）或插件（GET /api/v1/plugins/&lt;id&gt;/files/&lt;path&gt;）。
/// 字符串形式：builtin:&lt;name&gt;、plugin:&lt;id&gt;/&lt;path&gt;；没有前缀时给了 pluginId 就当作该插件里的相对路径，否则当作内置图标名。
/// （与 PaperplaneKit 的 IconSource 规则相同；这里单独定义，界面层不依赖数据层。）
/// </summary>
public readonly struct IconKey : IEquatable<IconKey>
{
    /// <summary>插件 id；内置图标为 null。</summary>
    public readonly string? PluginId;
    /// <summary>内置图标名，或插件里的相对路径。</summary>
    public readonly string Path;

    private IconKey(string? pluginId, string path) { PluginId = pluginId; Path = path; }

    public static IconKey Builtin(string name) => new(null, name.EndsWith(".svg", StringComparison.Ordinal) ? name.Substring(0, name.Length - 4) : name);
    public static IconKey Plugin(string id, string path) => new(id, path);

    public bool IsPlugin => PluginId != null;

    public static IconKey? Parse(string? raw, string? pluginId = null)
    {
        var r = raw?.Trim();
        if (string.IsNullOrEmpty(r)) return null;
        if (r!.StartsWith("builtin:", StringComparison.Ordinal)) return Builtin(r.Substring(8));
        if (r.StartsWith("plugin:", StringComparison.Ordinal))
        {
            var rest = r.Substring(7);
            int slash = rest.IndexOf('/');
            if (slash < 0) return null;
            return Plugin(rest.Substring(0, slash), rest.Substring(slash + 1));
        }
        if (!string.IsNullOrEmpty(pluginId)) return Plugin(pluginId!, r);
        return Builtin(r);
    }

    public string Key => PluginId == null ? "builtin:" + Path : "plugin:" + PluginId + "/" + Path;
    public override string ToString() => Key;
    public bool Equals(IconKey o) => PluginId == o.PluginId && Path == o.Path;
    public override bool Equals(object? obj) => obj is IconKey k && Equals(k);
    public override int GetHashCode() => Key.GetHashCode();
}

/// <summary>编译好的图标：每个图形一个冻结的 Geometry（viewBox 坐标）+ 样式。</summary>
public sealed class CompiledIcon
{
    public sealed class Part
    {
        public Geometry Geometry = Geometry.Empty;
        public SvgShapeStyle Style;
        /// <summary>固定颜色的填充 / 描边画刷（currentColor 或无色时为 null）。</summary>
        public Brush? FillBrush;
        public Brush? StrokeBrush;
    }

    public SvgDocument Document { get; }
    public Rect ViewBox { get; }
    public IReadOnlyList<Part> Parts { get; }
    public bool IsMonochrome => Document.IsMonochrome;

    public CompiledIcon(SvgDocument doc)
    {
        Document = doc;
        ViewBox = new Rect(doc.ViewBox.X, doc.ViewBox.Y, doc.ViewBox.Width, doc.ViewBox.Height);
        Parts = doc.Shapes.Select(s => new Part
        {
            Geometry = IconRenderer.ToGeometry(s.Path, s.Style.EvenOdd),
            Style = s.Style,
            FillBrush = FixedBrush(s.Style.Fill),
            StrokeBrush = FixedBrush(s.Style.Stroke),
        }).ToList();
    }

    private static Brush? FixedBrush(SvgPaint p)
    {
        if (p.Kind != SvgPaintKind.Color) return null;
        var b = new SolidColorBrush(MColor.FromArgb(B(p.A), B(p.R), B(p.G), B(p.B)));
        b.Freeze();
        return b;
    }

    private static byte B(double v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
}

/// <summary>SVG → WPF：路径转 Geometry；按设计规范绘制（保持比例居中，线宽按显示尺寸调细，currentColor 用前景色）。</summary>
public static class IconRenderer
{
    public static Geometry ToGeometry(SvgPath path, bool evenOdd)
    {
        // 先按子路径分组（StreamGeometry 要在 BeginFigure 时就知道是否闭合）
        var figures = new List<(Point Start, List<SvgSegment> Segs, bool Closed)>();
        List<SvgSegment>? segs = null;
        var cur = new Point();
        foreach (var s in path.Segments)
        {
            var p = new Point(s.P.X, s.P.Y);
            switch (s.Kind)
            {
                case SvgSegmentKind.Move:
                    segs = new List<SvgSegment>();
                    figures.Add((p, segs, false));
                    break;
                case SvgSegmentKind.Close:
                    if (segs != null)
                    {
                        var last = figures[figures.Count - 1];
                        figures[figures.Count - 1] = (last.Start, last.Segs, true);
                    }
                    segs = null; // 闭合后没有 M 接着画时，从子路径起点开始新的一段
                    break;
                default:
                    if (segs == null)
                    {
                        segs = new List<SvgSegment>();
                        figures.Add((cur, segs, false));
                    }
                    segs.Add(s);
                    break;
            }
            cur = p;
        }
        var g = new StreamGeometry { FillRule = evenOdd ? FillRule.EvenOdd : FillRule.Nonzero };
        using (var ctx = g.Open())
        {
            foreach (var (start, list, closed) in figures)
            {
                ctx.BeginFigure(start, true, closed);
                foreach (var s in list)
                {
                    switch (s.Kind)
                    {
                        case SvgSegmentKind.Line: ctx.LineTo(new Point(s.P.X, s.P.Y), true, true); break;
                        case SvgSegmentKind.Quad: ctx.QuadraticBezierTo(new Point(s.C1.X, s.C1.Y), new Point(s.P.X, s.P.Y), true, true); break;
                        case SvgSegmentKind.Cubic: ctx.BezierTo(new Point(s.C1.X, s.C1.Y), new Point(s.C2.X, s.C2.Y), new Point(s.P.X, s.P.Y), true, true); break;
                    }
                }
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// 把图标画进 rect（保持比例居中，相当于 xMidYMid meet）。
    /// currentColor：currentColor 用的画刷；strokeScale：线宽系数（16px 显示时 0.75，让 Lucide 的 2 单位线宽变成 1.5）。
    /// region：只画 viewBox 里的这一块（App 图标裁掉 macOS 网格的留白用），默认整个 viewBox。
    /// </summary>
    public static void Draw(DrawingContext dc, CompiledIcon icon, Rect rect, Brush currentColor, double strokeScale, Rect? region = null)
    {
        var vb = region ?? icon.ViewBox;
        if (vb.Width <= 0 || vb.Height <= 0) return;
        double scale = Math.Min(rect.Width / vb.Width, rect.Height / vb.Height);
        double w = vb.Width * scale, h = vb.Height * scale;
        double ox = rect.X + (rect.Width - w) / 2, oy = rect.Y + (rect.Height - h) / 2;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, ox - vb.X * scale, oy - vb.Y * scale));
        foreach (var p in icon.Parts)
        {
            var st = p.Style;
            var fill = p.Style.Fill.Kind switch
            {
                SvgPaintKind.CurrentColor => currentColor,
                SvgPaintKind.Color => p.FillBrush,
                _ => null,
            };
            if (fill != null)
            {
                double o = st.Opacity * st.FillOpacity;
                if (o < 1) dc.PushOpacity(o);
                dc.DrawGeometry(fill, null, p.Geometry);
                if (o < 1) dc.Pop();
            }
            var stroke = p.Style.Stroke.Kind switch
            {
                SvgPaintKind.CurrentColor => currentColor,
                SvgPaintKind.Color => p.StrokeBrush,
                _ => null,
            };
            if (stroke != null && st.StrokeWidth > 0)
            {
                double o = st.Opacity * st.StrokeOpacity;
                var cap = st.LineCap switch { SvgLineCap.Round => PenLineCap.Round, SvgLineCap.Square => PenLineCap.Square, _ => PenLineCap.Flat };
                var pen = new Pen(stroke, st.StrokeWidth * strokeScale)
                {
                    StartLineCap = cap,
                    EndLineCap = cap,
                    DashCap = cap,
                    LineJoin = st.LineJoin switch { SvgLineJoin.Round => PenLineJoin.Round, SvgLineJoin.Bevel => PenLineJoin.Bevel, _ => PenLineJoin.Miter },
                    MiterLimit = 4,
                };
                if (o < 1) dc.PushOpacity(o);
                dc.DrawGeometry(null, pen, p.Geometry);
                if (o < 1) dc.Pop();
            }
        }
        dc.Pop();
    }

    /// <summary>渲染成位图（App 图标、托盘图标用）。</summary>
    public static BitmapSource Bitmap(CompiledIcon icon, int pixels, MColor currentColor, double strokeScale = 1, Rect? region = null)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var brush = new SolidColorBrush(currentColor);
            brush.Freeze();
            Draw(dc, icon, new Rect(0, 0, pixels, pixels), brush, strokeScale, region);
        }
        RenderOptions.SetEdgeMode(dv, EdgeMode.Unspecified);
        var rtb = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}

/// <summary>
/// SVG 图标缓存：内置图标同步从嵌入资源读；插件图标经 PluginLoader（由接 API 的同事设置）异步取，按插件 id + 版本缓存。
/// 对应 Mac 的 IconStore。
/// </summary>
public sealed class IconStore
{
    public static readonly IconStore Shared = new();

    private readonly Dictionary<string, CompiledIcon> _icons = new();
    private readonly HashSet<string> _failed = new();
    private readonly HashSet<string> _loading = new();
    private readonly Dictionary<string, DrawingImage> _images = new();

    /// <summary>插件图标加载完成时递增；显示中的 PPIcon 订阅 Changed 后重绘。</summary>
    public int Revision { get; private set; }
    public event EventHandler? Changed;

    /// <summary>取插件文件：(插件 id, 相对路径) → SVG 字节。由 API 层接上（AppModel 启动时设置），没设置时插件图标显示后备图标。</summary>
    public Func<string, string, Task<byte[]>>? PluginLoader { get; set; }

    /// <summary>插件 id → 版本（缓存键的一部分，插件更新后取新图标）。</summary>
    public Dictionary<string, string> PluginVersions { get; } = new();

    private string CacheKey(IconKey k) => k.PluginId == null ? k.Key : k.Key + "@" + (PluginVersions.TryGetValue(k.PluginId, out var v) ? v : "");

    /// <summary>编译好的图标；插件图标第一次取时返回 null 并在后台加载，加载完成触发 Changed。</summary>
    public CompiledIcon? Icon(IconKey key)
    {
        var ck = CacheKey(key);
        if (_icons.TryGetValue(ck, out var c)) return c;
        if (_failed.Contains(ck)) return null;
        if (key.PluginId == null)
        {
            try
            {
                var data = SharedAssets.ReadBytes("icons/" + key.Path + ".svg");
                if (data != null)
                {
                    var icon = new CompiledIcon(SvgParser.Parse(data));
                    _icons[ck] = icon;
                    return icon;
                }
            }
            catch
            {
                // 坏图标按缺失处理
            }
            _failed.Add(ck);
            return null;
        }
        if (PluginLoader != null && _loading.Add(ck)) _ = LoadPlugin(key, ck);
        return null;
    }

    public CompiledIcon? Icon(string name) => Icon(IconKey.Builtin(name));

    /// <summary>内置图标是否存在。</summary>
    public bool Has(string name) => Icon(name) != null;

    private async Task LoadPlugin(IconKey key, string ck)
    {
        try
        {
            var data = await PluginLoader!(key.PluginId!, key.Path);
            _icons[ck] = new CompiledIcon(SvgParser.Parse(data));
            Revision++;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            _failed.Add(ck);
        }
        finally
        {
            _loading.Remove(ck);
        }
    }

    /// <summary>插件变化（安装、更新、禁用）后清掉插件图标缓存。</summary>
    public void ClearPluginIcons()
    {
        foreach (var k in _icons.Keys.Where(k => !k.StartsWith("builtin:", StringComparison.Ordinal)).ToList()) _icons.Remove(k);
        foreach (var k in _images.Keys.Where(k => !k.StartsWith("builtin:", StringComparison.Ordinal)).ToList()) _images.Remove(k);
        _failed.RemoveWhere(k => !k.StartsWith("builtin:", StringComparison.Ordinal));
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 需要 ImageSource 的地方（窗口图标、第三方控件）用的冻结 DrawingImage：尺寸固定为 size×size，currentColor 用给定颜色。
    /// 界面里一般直接用 PPIcon（随前景资源着色、随主题变化）。
    /// </summary>
    public DrawingImage? Image(IconKey key, double size, MColor color)
    {
        var ik = CacheKey(key) + "#" + size + "#" + color;
        if (_images.TryGetValue(ik, out var img)) return img;
        var icon = Icon(key);
        if (icon == null) return null;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // 透明底框让图片边界固定为 size×size（否则按图形实际范围计算，摆放时会偏）
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            IconRenderer.Draw(dc, icon, new Rect(0, 0, size, size), brush, M.StrokeScale(size));
        }
        group.Freeze();
        img = new DrawingImage(group);
        img.Freeze();
        _images[ik] = img;
        return img;
    }

    public DrawingImage? Image(string name, double size, MColor color) => Image(IconKey.Builtin(name), size, color);

    // MARK: 品牌图形

    private CompiledIcon? _appIcon;
    private CompiledIcon? _menubar;

    /// <summary>shared/branding/app-icon.svg（彩色）。</summary>
    public CompiledIcon AppIconSource => _appIcon ??= new CompiledIcon(SvgParser.Parse(SharedAssets.ReadBytes("branding/app-icon.svg") ?? throw new FileNotFoundException("branding/app-icon.svg")));

    /// <summary>shared/branding/menubar.svg（单色模板）。</summary>
    public CompiledIcon MenubarSource => _menubar ??= new CompiledIcon(SvgParser.Parse(SharedAssets.ReadBytes("branding/menubar.svg") ?? throw new FileNotFoundException("branding/menubar.svg")));

    /// <summary>
    /// App 图标的取景范围：app-icon.svg 是 1024 画布、824 圆角方块（macOS 图标网格四周留白 100）。
    /// Windows 图标几乎铺满画布，所以只裁掉大部分留白（四周各留 32，方块约占 93%）。
    /// </summary>
    public static readonly Rect AppIconRegion = new(68, 68, 888, 888);

    /// <summary>App 图标位图（pixels 见方）。</summary>
    public BitmapSource AppIconBitmap(int pixels) => IconRenderer.Bitmap(AppIconSource, pixels, Colors.Black, 1, AppIconRegion);

    /// <summary>托盘单色图标位图：menubar.svg 按模板图用 color 着色（浅色任务栏用深色、深色任务栏用白色）。</summary>
    public BitmapSource TrayBitmap(int pixels, MColor color) => IconRenderer.Bitmap(MenubarSource, pixels, color, 1);

    /// <summary>托盘图标（System.Windows.Forms.NotifyIcon 用）：pixels 一般取 16×DPI 缩放；color 见 TrayBitmap。</summary>
    public System.Drawing.Icon TrayIcon(int pixels, MColor color)
    {
        var bytes = IcoWriter.Write(new[] { TrayBitmap(pixels, color) });
        return new System.Drawing.Icon(new MemoryStream(bytes), pixels, pixels);
    }

    /// <summary>当前任务栏配色下托盘图标该用的颜色（浅色任务栏 #1E1F22，深色任务栏白色）。</summary>
    public static MColor TrayColor => Theme.SystemUsesLightTheme ? Theme.Color(ColorToken.TextPrimary, dark: false) : Colors.White;
}

/// <summary>
/// 写 .ico：小于 256 的尺寸用 32 位 BMP（老 API 与资源编译器兼容最好），256 用 PNG。
/// </summary>
public static class IcoWriter
{
    public static readonly int[] AppIconSizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    public static byte[] Write(IReadOnlyList<BitmapSource> frames)
    {
        var images = frames.Select(f => (f.PixelWidth, f.PixelHeight, Data: f.PixelWidth >= 256 ? Png(f) : Dib(f))).ToList();
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0); // reserved
        w.Write((ushort)1); // type = icon
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (pw, ph, data) in images)
        {
            w.Write((byte)(pw >= 256 ? 0 : pw));
            w.Write((byte)(ph >= 256 ? 0 : ph));
            w.Write((byte)0); // palette
            w.Write((byte)0); // reserved
            w.Write((ushort)1); // planes
            w.Write((ushort)32); // bpp
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, _, data) in images) w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Png(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>BITMAPINFOHEADER + 自下而上的 BGRA 像素（直通 alpha）+ 1 位 AND 掩码。</summary>
    private static byte[] Dib(BitmapSource src)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        int maskStride = ((w + 31) / 32) * 4;
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(40); // biSize
        bw.Write(w);
        bw.Write(h * 2); // 高度含 AND 掩码
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0); // BI_RGB
        bw.Write(stride * h + maskStride * h);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        for (int y = h - 1; y >= 0; y--) bw.Write(px, y * stride, stride);
        var mask = new byte[maskStride];
        for (int y = h - 1; y >= 0; y--)
        {
            Array.Clear(mask, 0, mask.Length);
            for (int x = 0; x < w; x++)
                if (px[y * stride + x * 4 + 3] == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
            bw.Write(mask);
        }
        bw.Flush();
        return ms.ToArray();
    }
}

/// <summary>
/// SVG 图标元素。所有界面图标都走它（不用 Segoe 图标字体、不用 emoji）。
/// 单色图标的 currentColor 跟随前景色（继承的 TextElement.Foreground，可用 icon.Fg("iconSecondary") 单独指定）；
/// 线宽按显示尺寸调细；彩色图标保留原色。对应 Mac 的 PPIcon。
/// </summary>
public sealed class PPIcon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(PPIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private IconKey? _key;
    private IconKey? _fallback;
    private double _size;
    private bool _subscribed;

    /// <summary>内置图标：new PPIcon("play")、new PPIcon("x", M.IconSmall, ColorToken.IconSecondary)。</summary>
    public PPIcon(string name, double size = 0, string? color = null) : this(IconKey.Builtin(name), size, color) { }

    public PPIcon(IconKey? key, double size = 0, string? color = null, IconKey? fallback = null)
    {
        _key = key;
        _fallback = fallback;
        IconSize = size > 0 ? size : M.Icon;
        if (color != null) this.Fg(color);
        SnapsToDevicePixels = true;
        Focusable = false;
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    /// <summary>图标引用字符串（builtin:/plugin:/插件相对路径），没有或取不到时用 fallback 内置图标。对应 Mac 的 PPIcon(raw:plugin:fallback:)。</summary>
    public static PPIcon Raw(string? raw, string? plugin = null, string? fallback = null, double size = 0, string? color = null) =>
        new(IconKey.Parse(raw, plugin) ?? (fallback != null ? IconKey.Builtin(fallback) : null), size, color,
            fallback != null ? IconKey.Builtin(fallback) : null);

    /// <summary>显示尺寸（宽高相同）。</summary>
    public double IconSize
    {
        get => _size;
        set
        {
            _size = value;
            Width = value;
            Height = value;
            InvalidateVisual();
        }
    }

    /// <summary>换图标（同一个元素复用时）。</summary>
    public IconKey? Key
    {
        get => _key;
        set
        {
            if (Nullable.Equals(_key, value)) return;
            _key = value;
            InvalidateVisual();
            if (IsLoaded) Subscribe();
        }
    }

    private void Subscribe()
    {
        if (_subscribed || _key is not IconKey k || !k.IsPlugin) return;
        IconStore.Shared.Changed += OnStoreChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        IconStore.Shared.Changed -= OnStoreChanged;
        _subscribed = false;
    }

    private void OnStoreChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        CompiledIcon? icon = null;
        if (_key is IconKey k) icon = IconStore.Shared.Icon(k);
        if (icon == null && _fallback is IconKey f) icon = IconStore.Shared.Icon(f);
        if (icon == null) return;
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        IconRenderer.Draw(dc, icon, new Rect(0, 0, ActualWidth, ActualHeight), Foreground, M.StrokeScale(s));
    }

    // 纯装饰：不进自动化树（按钮自己有名字）
    protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;
}

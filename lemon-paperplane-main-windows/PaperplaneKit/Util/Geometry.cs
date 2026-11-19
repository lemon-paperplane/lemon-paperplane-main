namespace PaperplaneKit;

// 不依赖 WPF 的点和矩形（对应 Mac 的 CGPoint / CGRect），给趋势线、提交图等几何计算用；界面层自己换成 System.Windows.Point / Rect。

/// <summary>点（y 向下）。</summary>
public readonly record struct KitPoint(double X, double Y);

/// <summary>矩形（原点在左上，y 向下）。</summary>
public readonly record struct KitRect(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
}

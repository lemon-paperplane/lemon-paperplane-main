using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaperplaneKit;

// SVG 子集渲染器用的几何类型（纯 .NET，不依赖 WPF；界面层把 SvgPath 转成 WPF Geometry）。
// 对应 Mac 用的 CGPoint / CGRect / CGAffineTransform / CGPath。

public readonly struct SvgPoint : IEquatable<SvgPoint>
{
    public readonly double X;
    public readonly double Y;
    public SvgPoint(double x, double y) { X = x; Y = y; }
    public static readonly SvgPoint Zero = new(0, 0);
    public bool Equals(SvgPoint o) => X == o.X && Y == o.Y;
    public override bool Equals(object? obj) => obj is SvgPoint p && Equals(p);
    public override int GetHashCode() => X.GetHashCode() * 31 + Y.GetHashCode();
    public static bool operator ==(SvgPoint a, SvgPoint b) => a.Equals(b);
    public static bool operator !=(SvgPoint a, SvgPoint b) => !a.Equals(b);
    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1})", X, Y);
}

public readonly struct SvgRect : IEquatable<SvgRect>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Width;
    public readonly double Height;
    public SvgRect(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public static readonly SvgRect Null = new(double.PositiveInfinity, double.PositiveInfinity, 0, 0);
    public bool IsNull => double.IsInfinity(X);

    public bool Contains(SvgRect r) => r.MinX >= MinX && r.MinY >= MinY && r.MaxX <= MaxX && r.MaxY <= MaxY;

    public bool Equals(SvgRect o) => X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;
    public override bool Equals(object? obj) => obj is SvgRect r && Equals(r);
    public override int GetHashCode() => (X, Y, Width, Height).GetHashCode();
    public static bool operator ==(SvgRect a, SvgRect b) => a.Equals(b);
    public static bool operator !=(SvgRect a, SvgRect b) => !a.Equals(b);
    public override string ToString() => string.Format(CultureInfo.InvariantCulture, "[{0}, {1}, {2}×{3}]", X, Y, Width, Height);
}

/// <summary>仿射变换：x' = a·x + c·y + tx，y' = b·x + d·y + ty（与 CGAffineTransform 相同）。</summary>
public readonly struct SvgMatrix : IEquatable<SvgMatrix>
{
    public readonly double A, B, C, D, Tx, Ty;
    public SvgMatrix(double a, double b, double c, double d, double tx, double ty) { A = a; B = b; C = c; D = d; Tx = tx; Ty = ty; }
    public static readonly SvgMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public static SvgMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);
    public static SvgMatrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);
    public static SvgMatrix Rotate(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new SvgMatrix(c, s, -s, c, 0, 0);
    }

    /// <summary>先应用 this，再应用 then（等同 CGAffineTransform.concatenating）。</summary>
    public SvgMatrix Then(SvgMatrix then) => new(
        A * then.A + B * then.C,
        A * then.B + B * then.D,
        C * then.A + D * then.C,
        C * then.B + D * then.D,
        Tx * then.A + Ty * then.C + then.Tx,
        Tx * then.B + Ty * then.D + then.Ty);

    public SvgPoint Apply(SvgPoint p) => new(A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty);
    public double Determinant => A * D - B * C;
    public bool IsIdentity => Equals(Identity);

    public bool Equals(SvgMatrix o) => A == o.A && B == o.B && C == o.C && D == o.D && Tx == o.Tx && Ty == o.Ty;
    public override bool Equals(object? obj) => obj is SvgMatrix m && Equals(m);
    public override int GetHashCode() => (A, B, C, D, Tx, Ty).GetHashCode();
}

public enum SvgSegmentKind { Move, Line, Quad, Cubic, Close }

/// <summary>路径的一段。Quad 用 C1 作控制点；Cubic 用 C1、C2；P 是终点（Close 的 P 是子路径起点）。</summary>
public readonly struct SvgSegment
{
    public readonly SvgSegmentKind Kind;
    public readonly SvgPoint C1;
    public readonly SvgPoint C2;
    public readonly SvgPoint P;
    public SvgSegment(SvgSegmentKind kind, SvgPoint c1, SvgPoint c2, SvgPoint p) { Kind = kind; C1 = c1; C2 = c2; P = p; }
}

/// <summary>由 move / line / quad / cubic / close 组成的路径（弧线已转成三次贝塞尔）。</summary>
public sealed class SvgPath
{
    private readonly List<SvgSegment> _segments = new();
    private SvgPoint _start;

    public IReadOnlyList<SvgSegment> Segments => _segments;
    public SvgPoint CurrentPoint { get; private set; }
    public bool IsEmpty => _segments.Count == 0;

    public void MoveTo(SvgPoint p) { _segments.Add(new SvgSegment(SvgSegmentKind.Move, p, p, p)); _start = p; CurrentPoint = p; }
    public void LineTo(SvgPoint p) { _segments.Add(new SvgSegment(SvgSegmentKind.Line, p, p, p)); CurrentPoint = p; }
    public void QuadTo(SvgPoint c, SvgPoint p) { _segments.Add(new SvgSegment(SvgSegmentKind.Quad, c, c, p)); CurrentPoint = p; }
    public void CubicTo(SvgPoint c1, SvgPoint c2, SvgPoint p) { _segments.Add(new SvgSegment(SvgSegmentKind.Cubic, c1, c2, p)); CurrentPoint = p; }

    public void Close()
    {
        _segments.Add(new SvgSegment(SvgSegmentKind.Close, _start, _start, _start));
        CurrentPoint = _start;
    }

    public void AddRect(SvgRect r)
    {
        MoveTo(new SvgPoint(r.MinX, r.MinY));
        LineTo(new SvgPoint(r.MaxX, r.MinY));
        LineTo(new SvgPoint(r.MaxX, r.MaxY));
        LineTo(new SvgPoint(r.MinX, r.MaxY));
        Close();
    }

    private const double Kappa = 0.5522847498307936; // 4/3·(√2−1)：四段三次贝塞尔逼近椭圆

    public void AddEllipse(SvgRect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rx = r.Width / 2, ry = r.Height / 2;
        double kx = rx * Kappa, ky = ry * Kappa;
        MoveTo(new SvgPoint(cx + rx, cy));
        CubicTo(new SvgPoint(cx + rx, cy + ky), new SvgPoint(cx + kx, cy + ry), new SvgPoint(cx, cy + ry));
        CubicTo(new SvgPoint(cx - kx, cy + ry), new SvgPoint(cx - rx, cy + ky), new SvgPoint(cx - rx, cy));
        CubicTo(new SvgPoint(cx - rx, cy - ky), new SvgPoint(cx - kx, cy - ry), new SvgPoint(cx, cy - ry));
        CubicTo(new SvgPoint(cx + kx, cy - ry), new SvgPoint(cx + rx, cy - ky), new SvgPoint(cx + rx, cy));
        Close();
    }

    public void AddRoundedRect(SvgRect r, double rx, double ry)
    {
        double x0 = r.MinX, y0 = r.MinY, x1 = r.MaxX, y1 = r.MaxY;
        double kx = rx * Kappa, ky = ry * Kappa;
        MoveTo(new SvgPoint(x0 + rx, y0));
        LineTo(new SvgPoint(x1 - rx, y0));
        CubicTo(new SvgPoint(x1 - rx + kx, y0), new SvgPoint(x1, y0 + ry - ky), new SvgPoint(x1, y0 + ry));
        LineTo(new SvgPoint(x1, y1 - ry));
        CubicTo(new SvgPoint(x1, y1 - ry + ky), new SvgPoint(x1 - rx + kx, y1), new SvgPoint(x1 - rx, y1));
        LineTo(new SvgPoint(x0 + rx, y1));
        CubicTo(new SvgPoint(x0 + rx - kx, y1), new SvgPoint(x0, y1 - ry + ky), new SvgPoint(x0, y1 - ry));
        LineTo(new SvgPoint(x0, y0 + ry));
        CubicTo(new SvgPoint(x0, y0 + ry - ky), new SvgPoint(x0 + rx - kx, y0), new SvgPoint(x0 + rx, y0));
        Close();
    }

    /// <summary>变换后的新路径。</summary>
    public SvgPath Transformed(SvgMatrix m)
    {
        if (m.IsIdentity) return this;
        var p = new SvgPath();
        foreach (var s in _segments)
        {
            var seg = new SvgSegment(s.Kind, m.Apply(s.C1), m.Apply(s.C2), m.Apply(s.P));
            p._segments.Add(seg);
        }
        p._start = m.Apply(_start);
        p.CurrentPoint = m.Apply(CurrentPoint);
        return p;
    }

    /// <summary>紧包围盒（不含控制点，与 CGPath.boundingBoxOfPath 相同）。空路径返回 SvgRect.Null。</summary>
    public SvgRect Bounds()
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        void Add(SvgPoint p)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        var cur = SvgPoint.Zero;
        foreach (var s in _segments)
        {
            switch (s.Kind)
            {
                case SvgSegmentKind.Move:
                case SvgSegmentKind.Line:
                case SvgSegmentKind.Close:
                    Add(s.P);
                    break;
                case SvgSegmentKind.Quad:
                    Add(s.P);
                    foreach (var t in QuadExtrema(cur.X, s.C1.X, s.P.X)) Add(QuadAt(cur, s.C1, s.P, t));
                    foreach (var t in QuadExtrema(cur.Y, s.C1.Y, s.P.Y)) Add(QuadAt(cur, s.C1, s.P, t));
                    break;
                case SvgSegmentKind.Cubic:
                    Add(s.P);
                    foreach (var t in CubicExtrema(cur.X, s.C1.X, s.C2.X, s.P.X)) Add(CubicAt(cur, s.C1, s.C2, s.P, t));
                    foreach (var t in CubicExtrema(cur.Y, s.C1.Y, s.C2.Y, s.P.Y)) Add(CubicAt(cur, s.C1, s.C2, s.P, t));
                    break;
            }
            cur = s.P;
        }
        if (double.IsInfinity(minX)) return SvgRect.Null;
        return new SvgRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static SvgPoint QuadAt(SvgPoint p0, SvgPoint c, SvgPoint p1, double t)
    {
        double u = 1 - t;
        return new SvgPoint(u * u * p0.X + 2 * u * t * c.X + t * t * p1.X, u * u * p0.Y + 2 * u * t * c.Y + t * t * p1.Y);
    }

    private static SvgPoint CubicAt(SvgPoint p0, SvgPoint c1, SvgPoint c2, SvgPoint p1, double t)
    {
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new SvgPoint(a * p0.X + b * c1.X + c * c2.X + d * p1.X, a * p0.Y + b * c1.Y + c * c2.Y + d * p1.Y);
    }

    private static IEnumerable<double> QuadExtrema(double p0, double c, double p1)
    {
        double den = p0 - 2 * c + p1;
        if (Math.Abs(den) < 1e-12) yield break;
        double t = (p0 - c) / den;
        if (t > 0 && t < 1) yield return t;
    }

    private static IEnumerable<double> CubicExtrema(double p0, double c1, double c2, double p1)
    {
        // B'(t) = 3[(c1−p0)(1−t)² + 2(c2−c1)(1−t)t + (p1−c2)t²] → at² + bt + c = 0
        double a = -p0 + 3 * c1 - 3 * c2 + p1;
        double b = 2 * (p0 - 2 * c1 + c2);
        double c = c1 - p0;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) < 1e-12) yield break;
            double t = -c / b;
            if (t > 0 && t < 1) yield return t;
            yield break;
        }
        double disc = b * b - 4 * a * c;
        if (disc < 0) yield break;
        double sq = Math.Sqrt(disc);
        double t1 = (-b + sq) / (2 * a), t2 = (-b - sq) / (2 * a);
        if (t1 > 0 && t1 < 1) yield return t1;
        if (t2 > 0 && t2 < 1) yield return t2;
    }
}

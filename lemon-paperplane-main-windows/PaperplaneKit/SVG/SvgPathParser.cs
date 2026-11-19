using System;

namespace PaperplaneKit;

/// <summary>SVG path 的 d 属性解析：M L H V C S Q T A Z（含相对形式），弧线转换为三次贝塞尔。对应 Mac 的 SVGPathParser.swift。</summary>
public static class SvgPathParser
{
    public static SvgPath Parse(string d)
    {
        var path = new SvgPath();
        var s = new SvgNumberScanner(d);
        char cmd = '\0';
        var cur = SvgPoint.Zero;
        var start = SvgPoint.Zero;
        SvgPoint? lastCtrl = null; // 上一个 C/S 的第二控制点（用于 S）
        SvgPoint? lastQuad = null; // 上一个 Q/T 的控制点（用于 T）
        bool hasCurrent = false;

        void EnsureStart(SvgPoint p)
        {
            if (!hasCurrent) { path.MoveTo(p); hasCurrent = true; }
        }

        while (true)
        {
            s.SkipSeparators();
            if (s.AtEnd) break;
            char c = s.Current;
            if (IsCommand(c))
            {
                cmd = c;
                s.Pos++;
                if (c == 'Z' || c == 'z')
                {
                    if (hasCurrent) path.Close();
                    cur = start;
                    lastCtrl = null;
                    lastQuad = null;
                    continue;
                }
            }
            else if (cmd == '\0')
            {
                s.Pos++; // 开头的垃圾字符
                continue;
            }
            else if (!s.PeekNumberAvailable())
            {
                s.Pos++;
                continue;
            }

            bool rel = char.IsLower(cmd);
            SvgPoint Pt(double x, double y) => rel ? new SvgPoint(cur.X + x, cur.Y + y) : new SvgPoint(x, y);

            switch (char.ToLowerInvariant(cmd))
            {
                case 'm':
                {
                    if (s.ReadNumber() is not double x || s.ReadNumber() is not double y) return path;
                    cur = Pt(x, y);
                    start = cur;
                    path.MoveTo(cur);
                    hasCurrent = true;
                    cmd = rel ? 'l' : 'L'; // 后续的坐标对按 L/l 处理
                    lastCtrl = null;
                    lastQuad = null;
                    break;
                }
                case 'l':
                {
                    if (s.ReadNumber() is not double x || s.ReadNumber() is not double y) return path;
                    cur = Pt(x, y);
                    EnsureStart(cur);
                    path.LineTo(cur);
                    lastCtrl = null;
                    lastQuad = null;
                    break;
                }
                case 'h':
                {
                    if (s.ReadNumber() is not double x) return path;
                    cur = new SvgPoint(rel ? cur.X + x : x, cur.Y);
                    EnsureStart(cur);
                    path.LineTo(cur);
                    lastCtrl = null;
                    lastQuad = null;
                    break;
                }
                case 'v':
                {
                    if (s.ReadNumber() is not double y) return path;
                    cur = new SvgPoint(cur.X, rel ? cur.Y + y : y);
                    EnsureStart(cur);
                    path.LineTo(cur);
                    lastCtrl = null;
                    lastQuad = null;
                    break;
                }
                case 'c':
                {
                    if (s.ReadNumber() is not double x1 || s.ReadNumber() is not double y1 || s.ReadNumber() is not double x2 ||
                        s.ReadNumber() is not double y2 || s.ReadNumber() is not double x || s.ReadNumber() is not double y) return path;
                    SvgPoint c1 = Pt(x1, y1), c2 = Pt(x2, y2), e = Pt(x, y);
                    EnsureStart(cur);
                    path.CubicTo(c1, c2, e);
                    lastCtrl = c2;
                    lastQuad = null;
                    cur = e;
                    break;
                }
                case 's':
                {
                    if (s.ReadNumber() is not double x2 || s.ReadNumber() is not double y2 || s.ReadNumber() is not double x ||
                        s.ReadNumber() is not double y) return path;
                    var c1 = lastCtrl is SvgPoint lc ? new SvgPoint(2 * cur.X - lc.X, 2 * cur.Y - lc.Y) : cur;
                    SvgPoint c2 = Pt(x2, y2), e = Pt(x, y);
                    EnsureStart(cur);
                    path.CubicTo(c1, c2, e);
                    lastCtrl = c2;
                    lastQuad = null;
                    cur = e;
                    break;
                }
                case 'q':
                {
                    if (s.ReadNumber() is not double x1 || s.ReadNumber() is not double y1 || s.ReadNumber() is not double x ||
                        s.ReadNumber() is not double y) return path;
                    SvgPoint c1 = Pt(x1, y1), e = Pt(x, y);
                    EnsureStart(cur);
                    path.QuadTo(c1, e);
                    lastQuad = c1;
                    lastCtrl = null;
                    cur = e;
                    break;
                }
                case 't':
                {
                    if (s.ReadNumber() is not double x || s.ReadNumber() is not double y) return path;
                    var c1 = lastQuad is SvgPoint lq ? new SvgPoint(2 * cur.X - lq.X, 2 * cur.Y - lq.Y) : cur;
                    var e = Pt(x, y);
                    EnsureStart(cur);
                    path.QuadTo(c1, e);
                    lastQuad = c1;
                    lastCtrl = null;
                    cur = e;
                    break;
                }
                case 'a':
                {
                    if (s.ReadNumber() is not double rx || s.ReadNumber() is not double ry || s.ReadNumber() is not double rot ||
                        s.NextFlag() is not bool large || s.NextFlag() is not bool sweep ||
                        s.ReadNumber() is not double x || s.ReadNumber() is not double y) return path;
                    var e = Pt(x, y);
                    EnsureStart(cur);
                    AddArc(path, cur, e, rx, ry, rot, large, sweep);
                    cur = e;
                    lastCtrl = null;
                    lastQuad = null;
                    break;
                }
                default:
                    s.Pos++;
                    break;
            }
        }
        return path;
    }

    private static bool IsCommand(char c) => "MmLlHhVvCcSsQqTtAaZz".IndexOf(c) >= 0;

    /// <summary>椭圆弧（端点参数化）→ 若干段三次贝塞尔，算法见 SVG 1.1 附录 F.6。</summary>
    public static void AddArc(SvgPath path, SvgPoint p0, SvgPoint p1, double rxIn, double ryIn, double xRotation, bool largeArc, bool sweep)
    {
        if (p0 == p1) return;
        double rx = Math.Abs(rxIn), ry = Math.Abs(ryIn);
        if (rx == 0 || ry == 0) { path.LineTo(p1); return; }
        double phi = xRotation * Math.PI / 180;
        double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);
        double dx = (p0.X - p1.X) / 2, dy = (p0.Y - p1.Y) / 2;
        double x1p = cosPhi * dx + sinPhi * dy;
        double y1p = -sinPhi * dx + cosPhi * dy;
        // 半径不够时放大
        double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1) { double k = Math.Sqrt(lambda); rx *= k; ry *= k; }
        double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        double coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
        if (largeArc == sweep) coef = -coef;
        double cxp = coef * (rx * y1p / ry);
        double cyp = coef * -(ry * x1p / rx);
        double cx = cosPhi * cxp - sinPhi * cyp + (p0.X + p1.X) / 2;
        double cy = sinPhi * cxp + cosPhi * cyp + (p0.Y + p1.Y) / 2;

        static double Angle(double ux, double uy, double vx, double vy)
        {
            double dot = ux * vx + uy * vy;
            double len = Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy);
            double a = Math.Acos(Math.Max(-1, Math.Min(1, dot / len)));
            if (ux * vy - uy * vx < 0) a = -a;
            return a;
        }

        double theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        if (sweep && delta < 0) delta += 2 * Math.PI;

        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-7));
        double step = delta / segments;
        double t = 4.0 / 3.0 * Math.Tan(step / 4);
        double a1 = theta1;
        SvgPoint Point(double a) => new(cx + rx * Math.Cos(a) * cosPhi - ry * Math.Sin(a) * sinPhi,
            cy + rx * Math.Cos(a) * sinPhi + ry * Math.Sin(a) * cosPhi);
        SvgPoint Deriv(double a) => new(-rx * Math.Sin(a) * cosPhi - ry * Math.Cos(a) * sinPhi,
            -rx * Math.Sin(a) * sinPhi + ry * Math.Cos(a) * cosPhi);
        for (int i = 0; i < segments; i++)
        {
            double a2 = a1 + step;
            var s0 = Point(a1);
            var s1 = i == segments - 1 ? p1 : Point(a2);
            SvgPoint d0 = Deriv(a1), d1 = Deriv(a2);
            var c1 = new SvgPoint(s0.X + t * d0.X, s0.Y + t * d0.Y);
            var c2 = new SvgPoint(s1.X - t * d1.X, s1.Y - t * d1.Y);
            path.CubicTo(c1, c2, s1);
            a1 = a2;
        }
    }
}

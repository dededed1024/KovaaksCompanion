using System.Windows;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Shared curve geometry for the charts: monotone cubic (never overshoots) and closed Catmull-Rom.</summary>
public static class ChartPaths
{
    /// <summary>Fritsch-Carlson tangents (dy/dx per point); x must be non-decreasing.</summary>
    static double[] Tangents(IReadOnlyList<Point> p)
    {
        var n = p.Count;
        var d = new double[n - 1];
        for (var i = 0; i < n - 1; i++) { var dx = p[i + 1].X - p[i].X; d[i] = dx <= 0 ? 0 : (p[i + 1].Y - p[i].Y) / dx; }
        var m = new double[n];
        m[0] = d[0]; m[n - 1] = d[n - 2];
        for (var i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (var i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
            var a = m[i] / d[i]; var b = m[i + 1] / d[i];
            var s = a * a + b * b;
            if (s > 9) { var t = 3 / Math.Sqrt(s); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
        }
        return m;
    }

    /// <summary>Appends the monotone curve through <paramref name="p"/> (already positioned at p[0]) to the context.</summary>
    public static void CurveTo(StreamGeometryContext c, IReadOnlyList<Point> p, bool stroke = true)
    {
        if (p.Count < 2) return;
        if (p.Count == 2) { c.LineTo(p[1], stroke, true); return; }
        var m = Tangents(p);
        for (var i = 0; i < p.Count - 1; i++)
        {
            var dx = (p[i + 1].X - p[i].X) / 3;
            c.BezierTo(new Point(p[i].X + dx, p[i].Y + m[i] * dx), new Point(p[i + 1].X - dx, p[i + 1].Y - m[i + 1] * dx), p[i + 1], stroke, true);
        }
    }

    public static Geometry Monotone(IReadOnlyList<Point> p)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(p[0], false, false); CurveTo(c, p); }
        g.Freeze();
        return g;
    }

    /// <summary>The monotone curve closed down to <paramref name="baseY"/>, for gradient area fills.</summary>
    public static Geometry MonotoneArea(IReadOnlyList<Point> p, double baseY)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(p[0].X, baseY), true, true);
            c.LineTo(p[0], false, false);
            CurveTo(c, p, false);
            c.LineTo(new Point(p[^1].X, baseY), false, false);
        }
        g.Freeze();
        return g;
    }

    static double Lum(Color c)
    {
        static double L(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    /// <summary>A brush safe to use for text: the colour blended towards white until its relative luminance is about 0.35 (hue kept). Fills and bars keep the raw brush.</summary>
    public static Brush TextTone(Brush b)
    {
        if (b is not SolidColorBrush s || Lum(s.Color) >= 0.35) return b;
        var c = s.Color;
        double t = 0;
        Color Mix(double k) => Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));
        while (t < 1 && Lum(Mix(t)) < 0.35) t += 0.02;
        var r = new SolidColorBrush(Mix(t));
        r.Freeze();
        return r;
    }

    /// <summary>Closed Catmull-Rom spline through the points (passes exactly through them); <paramref name="tension"/> 0.5 is the classic curve, smaller is tighter. Control points are kept within <paramref name="maxR"/> of <paramref name="center"/>.</summary>
    public static Geometry ClosedSpline(IReadOnlyList<Point> p, Point center, double maxR, double tension = 0.5)
    {
        var n = p.Count;
        var g = new StreamGeometry();
        Point Clamp(Point q)
        {
            var v = q - center;
            return v.Length > maxR && v.Length > 0 ? center + v * (maxR / v.Length) : q;
        }
        using (var c = g.Open())
        {
            c.BeginFigure(p[0], true, true);
            for (var i = 0; i < n; i++)
            {
                Point p0 = p[(i + n - 1) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
                c.BezierTo(Clamp(p1 + (p2 - p0) * (tension / 3)), Clamp(p2 - (p3 - p1) * (tension / 3)), p2, true, true);
            }
        }
        g.Freeze();
        return g;
    }
}

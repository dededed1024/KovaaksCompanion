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

    /// <summary>Y of the curve <see cref="CurveTo"/> draws at <paramref name="x"/>: the Bezier x is linear in t, so each segment is a cubic Hermite in x. NaN outside the points.</summary>
    public static double YAt(IReadOnlyList<Point> p, double x)
    {
        var n = p.Count;
        if (n == 0 || x < p[0].X) return double.NaN;
        if (n == 1 || x >= p[^1].X) return p[^1].Y;
        var i = 0;
        while (p[i + 1].X <= x) i++;
        var dx = p[i + 1].X - p[i].X;
        var t = (x - p[i].X) / dx;
        if (n == 2) return p[i].Y + (p[i + 1].Y - p[i].Y) * t;
        var m = Tangents(p);
        double y0 = p[i].Y, y3 = p[i + 1].Y, y1 = y0 + m[i] * dx / 3, y2 = y3 - m[i + 1] * dx / 3, u = 1 - t;
        return u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3;
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

    public static double Lum(Color c)
    {
        static double L(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    /// <summary>A brush safe to use for text on the current theme: on the light theme the colour is blended towards black until its relative luminance is at most about 0.17, otherwise towards white until it is at least about 0.35 (hue kept). Fills and bars keep the raw brush.</summary>
    public static Brush TextTone(Brush b) => Theme.IsLight ? TextToneOn(b, light: true) : TextToneOn(b, light: false);

    /// <summary>Like <see cref="TextTone"/> for text on a dark surface whatever the theme (the profile card).</summary>
    public static Brush TextToneOnDark(Brush b) => TextToneOn(b, light: false);

    static Brush TextToneOn(Brush b, bool light)
    {
        if (b is not SolidColorBrush s) return b;
        var c = s.Color;
        if (light ? Lum(c) <= 0.17 : Lum(c) >= 0.35) return b;
        var target = light ? 0 : 255;
        Color Mix(double k) => Color.FromRgb((byte)(c.R + (target - c.R) * k), (byte)(c.G + (target - c.G) * k), (byte)(c.B + (target - c.B) * k));
        double t = 0;
        while (t < 1 && (light ? Lum(Mix(t)) > 0.17 : Lum(Mix(t)) < 0.35)) t += 0.02;
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

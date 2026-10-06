namespace KovaaksCompanion.Core.Trajectory;

/// <summary>Normalized device coords: x right, y up, both in [-1, 1] when on screen. Visible is false behind the camera.</summary>
public readonly record struct ProjectedPoint(double X, double Y, bool Visible)
{
    public (double X, double Y) ToPixels(double width, double height) => ((X + 1) / 2 * width, (1 - Y) / 2 * height);
}

public static class ViewProjection
{
    /// <summary>
    /// Horizontal FOV at the actual aspect ratio. KovaaK's FOV is Hor+ for "Overwatch" (the value is the horizontal
    /// FOV at 16:9, vertical FOV stays fixed for other aspects). "CS:GO"/"Source" use 4:3 as the reference.
    /// "Vertical" means the value is the vertical FOV. Anything unknown is treated as Overwatch.
    /// </summary>
    public static double HorizontalFovDeg(double fov, string fovScale, double aspect)
    {
        var scale = fovScale.Trim().ToLowerInvariant();
        if (scale.Contains("vertical")) return FromVertical(fov, aspect);
        var refAspect = scale.Contains("cs") || scale.Contains("source") || scale.Contains("4:3") ? 4.0 / 3 : 16.0 / 9;
        var tanV = Math.Tan(Rad(fov) / 2) / refAspect;
        return Deg(2 * Math.Atan(tanV * aspect));
    }

    static double FromVertical(double vfov, double aspect) => Deg(2 * Math.Atan(Math.Tan(Rad(vfov) / 2) * aspect));

    /// <summary>Projects a past view direction onto the frame seen with the camera at (current yaw, pitch).</summary>
    public static ProjectedPoint Project(ViewDirection past, ViewDirection current, double hfovDeg, double aspect)
    {
        // World: x right, y up, z forward. Rotate the point by -yaw about y, then -pitch about x.
        var (py, pp) = (Rad(past.YawDeg - current.YawDeg), Rad(past.PitchDeg));
        double x = Math.Cos(pp) * Math.Sin(py), y = Math.Sin(pp), z = Math.Cos(pp) * Math.Cos(py);
        var cp = Rad(current.PitchDeg);
        double y2 = y * Math.Cos(cp) - z * Math.Sin(cp);
        double z2 = y * Math.Sin(cp) + z * Math.Cos(cp);
        if (z2 <= 1e-9) return new ProjectedPoint(0, 0, false);
        var tanH = Math.Tan(Rad(hfovDeg) / 2);
        return new ProjectedPoint(x / z2 / tanH, y2 / z2 / (tanH / aspect), true);
    }

    static double Rad(double d) => d * Math.PI / 180;
    static double Deg(double r) => r * 180 / Math.PI;
}

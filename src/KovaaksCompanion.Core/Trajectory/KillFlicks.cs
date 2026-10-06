namespace KovaaksCompanion.Core.Trajectory;

/// <param name="KillIndex">Index into the kill list (0 = first kill, measured from run start).</param>
/// <param name="IntervalSec">Time since the previous kill (or run start).</param>
/// <param name="FlickTimeSec">From the first movement above the speed threshold after the previous kill until this kill.</param>
/// <param name="PathDeg">Summed angular distance travelled between the previous kill and this one.</param>
public readonly record struct KillFlick(int KillIndex, double IntervalSec, double FlickTimeSec, double PathDeg);

public static class KillFlicks
{
    /// <summary>Angular speed (deg/s) above which the mouse counts as flicking toward the next target.</summary>
    public const double MoveThresholdDegPerSec = 30;

    public static IReadOnlyList<KillFlick> Compute(IReadOnlyList<CameraSample> samples, IReadOnlyList<double> killTimesSec)
    {
        var result = new List<KillFlick>();
        double prev = 0;
        for (var i = 0; i < killTimesSec.Count; i++)
        {
            var kill = killTimesSec[i];
            double path = 0, begin = double.NaN;
            var a = CameraPath.At(samples, prev);
            var at = prev;
            foreach (var t in Times(samples, prev, kill))
            {
                var b = CameraPath.At(samples, t);
                var d = AngleBetween(a, b);
                path += d;
                if (double.IsNaN(begin) && t > at && d / (t - at) > MoveThresholdDegPerSec) begin = at;
                a = b;
                at = t;
            }
            result.Add(new KillFlick(i, kill - prev, kill - (double.IsNaN(begin) ? prev : begin), path));
            prev = kill;
        }
        return result;
    }

    static IEnumerable<double> Times(IReadOnlyList<CameraSample> samples, double from, double to)
    {
        foreach (var s in samples) if (s.TSec > from && s.TSec < to) yield return s.TSec;
        if (to > from) yield return to;
    }

    /// <summary>Great-circle angle between two view directions in degrees.</summary>
    public static double AngleBetween(ViewDirection a, ViewDirection b)
    {
        static (double, double, double) V(ViewDirection d)
        {
            double yaw = d.YawDeg * Math.PI / 180, pitch = d.PitchDeg * Math.PI / 180;
            return (Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw));
        }
        var (x1, y1, z1) = V(a);
        var (x2, y2, z2) = V(b);
        var cross = Math.Sqrt(Math.Pow(y1 * z2 - z1 * y2, 2) + Math.Pow(z1 * x2 - x1 * z2, 2) + Math.Pow(x1 * y2 - y1 * x2, 2));
        return Math.Atan2(cross, x1 * x2 + y1 * y2 + z1 * z2) * 180 / Math.PI;
    }
}

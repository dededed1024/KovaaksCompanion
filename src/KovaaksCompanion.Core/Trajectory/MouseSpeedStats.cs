namespace KovaaksCompanion.Core.Trajectory;

/// <summary>How a scenario is aimed, which decides the mouse-speed figures that mean something for it.</summary>
public enum AimKind { Tracking, Switching, Flick }

/// <summary>
/// Mouse-speed figures of one run (null where the run has nothing to measure). Speeds are angular, in deg/s, over a ~40 ms window
/// so the quantised mouse counts do not spike them. The kill figures are medians over the kills, so one slow target does not skew them.
/// </summary>
public sealed record RunSpeed(
    double Mean, double Peak, double Max, double? FireSpeed, double? IdleSpeed, double HoldRatio, double ReversalsPerSec,
    double? KillInterval, double? FlickDistance, double? FlickPeak, double? FlickTime, double? Reaction, double? Efficiency, int Kills);

public static class MouseSpeedStats
{
    /// <summary>Window the speed is measured over.</summary>
    public const double WindowSec = 0.04;
    /// <summary>Angular speed (deg/s) above which the mouse counts as moving.</summary>
    public const double MoveDegPerSec = KillFlicks.MoveThresholdDegPerSec;
    /// <summary>Horizontal speed (deg/s) below which a direction change is jitter, not a reversal.</summary>
    const double ReversalDegPerSec = 15;

    /// <summary>The aim kind from the scenario name; null when the name says nothing.</summary>
    public static AimKind? KindFromName(string scenario)
    {
        var n = scenario.ToLowerInvariant();
        if (n.Contains("track") || n.Contains("strafe") || n.Contains("smooth") || n.Contains("pasu")) return AimKind.Tracking;
        if (n.Contains("switch") || n.Contains("6targets") || n.Contains("multi")) return AimKind.Switching;
        if (n.Contains("flick") || n.Contains("1wall") || n.Contains("pop")) return AimKind.Flick;
        return null;
    }

    /// <summary>The aim kind from how the runs were played: holding the trigger most of the time is tracking, anything else is target-to-target.</summary>
    public static AimKind KindFromRuns(IReadOnlyList<RunSpeed> runs) =>
        runs.Count > 0 && runs.Average(r => r.HoldRatio) >= 0.5 ? AimKind.Tracking : AimKind.Switching;

    /// <summary>Per-sample speed: the angle covered from this sample to the one a window later, over the time between them.</summary>
    static double[] Speeds(IReadOnlyList<CameraSample> s)
    {
        var v = new double[s.Count];
        var k = 1;
        for (var i = 0; i < s.Count; i++)
        {
            if (k <= i) k = i + 1;
            while (k < s.Count - 1 && s[k].TSec - s[i].TSec < WindowSec) k++;
            if (k >= s.Count || s[k].TSec <= s[i].TSec) { v[i] = i > 0 ? v[i - 1] : 0; continue; }
            v[i] = KillFlicks.AngleBetween(new(s[i].YawDeg, s[i].PitchDeg), new(s[k].YawDeg, s[k].PitchDeg)) / (s[k].TSec - s[i].TSec);
        }
        return v;
    }

    /// <summary>Mean speed (deg/s) over the stretch ending at each of <paramref name="ends"/> (ascending, mouse seconds) and starting at the previous one. A stretch without samples is a still mouse: 0, so the line stays continuous.</summary>
    public static double[] Averages(TrajectoryData data, IReadOnlyList<double> ends)
    {
        var s = data.Samples;
        var res = new double[ends.Count];
        if (s.Count < 2) return res;
        var v = Speeds(s);
        var i = 0;
        for (var e = 0; e < ends.Count; e++)
        {
            double sum = 0;
            var n = 0;
            while (i < s.Count && s[i].TSec <= ends[e]) { sum += v[i]; n++; i++; }
            if (n > 0) res[e] = sum / n;
        }
        return res;
    }

    static double? Median(List<double> l)
    {
        if (l.Count == 0) return null;
        l.Sort();
        return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
    }

    /// <summary>Null when the run has less than a second of samples.</summary>
    public static RunSpeed? Analyze(TrajectoryData data)
    {
        var s = data.Samples;
        if (s.Count < 2 || s[^1].TSec - s[0].TSec < 1) return null;
        var v = Speeds(s);
        var span = s[^1].TSec - s[0].TSec;

        // Left-button hold intervals (a hold still open at the end runs to the last sample).
        var holds = new List<(double From, double To)>();
        double? down = null;
        foreach (var e in data.Events)
        {
            if (e.Type == TrajectoryEventType.Fire && e.Button == 0) down ??= e.TSec;
            else if (e.Type == TrajectoryEventType.Release && e.Button == 0 && down is { } d) { holds.Add((d, e.TSec)); down = null; }
        }
        if (down is { } open) holds.Add((open, s[^1].TSec));

        double fireSum = 0, idleSum = 0;
        int fireN = 0, idleN = 0;
        var h = 0;
        for (var i = 0; i < s.Count; i++)
        {
            while (h < holds.Count && holds[h].To < s[i].TSec) h++;
            if (h < holds.Count && s[i].TSec >= holds[h].From) { fireSum += v[i]; fireN++; }
            else { idleSum += v[i]; idleN++; }
        }

        // Direction changes of the horizontal velocity, between stretches faster than the jitter floor.
        var reversals = 0;
        var lastSign = 0;
        for (var i = 1; i < s.Count; i++)
        {
            var dt = s[i].TSec - s[i - 1].TSec;
            if (dt <= 0) continue;
            var yawSpeed = CameraPath.WrapDelta(s[i].YawDeg - s[i - 1].YawDeg) / dt;
            if (Math.Abs(yawSpeed) < ReversalDegPerSec) continue;
            var sign = Math.Sign(yawSpeed);
            if (lastSign != 0 && sign != lastSign) reversals++;
            lastSign = sign;
        }

        var sorted = v.Order().ToArray();
        var peak = sorted[(int)Math.Min(sorted.Length - 1, sorted.Length * 0.95)];
        var holdTime = holds.Sum(x => Math.Max(0, x.To - x.From));

        // One flick per kill: from the previous kill (or the start) to this one.
        var kills = data.Events.Where(e => e.Type == TrajectoryEventType.Kill).Select(e => (double)e.TSec).Order().ToList();
        List<double> interval = [], dist = [], fpeak = [], ftime = [], react = [], eff = [];
        double prev = s[0].TSec;
        foreach (var kill in kills)
        {
            var from = prev;
            prev = kill;
            if (kill - from < 0.05) continue;
            var a = CameraPath.At(s, from);
            var end = CameraPath.At(s, kill);
            var straight = KillFlicks.AngleBetween(a, end);
            if (straight < 2) continue; // the target was already under the crosshair
            double path = 0, begin = double.NaN, top = 0;
            var at = from;
            var last = a;
            for (var i = 0; i < s.Count; i++)
            {
                var t = s[i].TSec;
                if (t <= from) continue;
                if (t >= kill) break;
                var b = new ViewDirection(s[i].YawDeg, s[i].PitchDeg);
                var step = KillFlicks.AngleBetween(last, b);
                path += step;
                if (double.IsNaN(begin) && t > at && step / (t - at) > MoveDegPerSec) begin = at;
                top = Math.Max(top, v[i]);
                last = b;
                at = t;
            }
            path += KillFlicks.AngleBetween(last, end);
            if (double.IsNaN(begin)) begin = from;
            interval.Add(kill - from);
            dist.Add(straight);
            fpeak.Add(top);
            ftime.Add(kill - begin);
            react.Add(begin - from);
            eff.Add(path > 0 ? Math.Min(1, straight / path) : 1);
        }

        return new RunSpeed(v.Average(), peak, sorted[^1], fireN > 0 ? fireSum / fireN : null, idleN > 0 ? idleSum / idleN : null, Math.Min(1, holdTime / span), reversals / span,
            Median(interval), Median(dist), Median(fpeak), Median(ftime), Median(react), Median(eff), interval.Count);
    }
}

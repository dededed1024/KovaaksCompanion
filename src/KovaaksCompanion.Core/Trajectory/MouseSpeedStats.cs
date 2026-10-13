namespace KovaaksCompanion.Core.Trajectory;

/// <summary>How a scenario is aimed, which decides the mouse-speed figures that mean something for it.</summary>
public enum AimKind { Tracking, Switching, Flick }

/// <summary>
/// Mouse-speed figures of one run (null where the run has nothing to measure). Speeds are angular, in deg/s, over a ~40 ms window
/// so the quantised mouse counts do not spike them. The kill figures are medians over the kills, so one slow target does not skew them.
/// </summary>
public sealed record RunSpeed(
    double Mean, double Peak, double Max, double? FireSpeed, double? IdleSpeed, double HoldRatio, double ReversalsPerSec,
    double? KillInterval, double? FlickDistance, double? FlickPeak, double? FlickTime, double? Reaction, double? Efficiency, int Kills,
    double? Jerk = null, double? SpeedCv = null);

public static class MouseSpeedStats
{
    /// <summary>Window the speed is measured over.</summary>
    public const double WindowSec = 0.04;
    /// <summary>Angular speed (deg/s) above which the mouse counts as moving.</summary>
    public const double MoveDegPerSec = KillFlicks.MoveThresholdDegPerSec;
    /// <summary>Horizontal speed (deg/s) below which a direction change is jitter, not a reversal.</summary>
    const double ReversalDegPerSec = 15;
    /// <summary>Samples between the two points a horizontal velocity is taken from (~8 ms): single mouse counts at 1 kHz are all jitter.</summary>
    const int ReversalStride = (int)(AnalysisHz / 120);

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

    /// <summary>Rate the metrics are measured at, so they do not depend on how finely the mouse movement was logged.</summary>
    const double AnalysisHz = 1000;

    /// <summary>The samples to measure: raw movement (samples at the mouse's own timing, up to 1 kHz) is resampled to a fixed rate, older fixed-rate samples are used as they are.</summary>
    static IReadOnlyList<CameraSample> Uniform(TrajectoryData data)
    {
        var s = data.Samples;
        if (data.Raw == null || s.Count < 2) return s;
        var n = (int)Math.Floor((s[^1].TSec - s[0].TSec) * AnalysisHz) + 1;
        var res = new CameraSample[n];
        for (var i = 0; i < n; i++)
        {
            var t = s[0].TSec + i / AnalysisHz;
            var d = CameraPath.At(s, t);
            res[i] = new CameraSample((float)t, (float)d.YawDeg, (float)d.PitchDeg);
        }
        return res;
    }

    /// <summary>Index of the first sample later than <paramref name="t"/> (the sample count when there is none).</summary>
    static int FirstAfter(IReadOnlyList<CameraSample> s, double t)
    {
        int lo = 0, hi = s.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (s[mid].TSec <= t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

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
        var s = Uniform(data);
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
        var s = Uniform(data);
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
        var stride = data.Raw != null ? ReversalStride : 1; // older samples are already ~8 ms apart
        for (var i = stride; i < s.Count; i++)
        {
            var dt = s[i].TSec - s[i - stride].TSec;
            if (dt <= 0) continue;
            var yawSpeed = CameraPath.WrapDelta(s[i].YawDeg - s[i - stride].YawDeg) / dt;
            if (Math.Abs(yawSpeed) < ReversalDegPerSec) continue;
            var sign = Math.Sign(yawSpeed);
            if (lastSign != 0 && sign != lastSign) reversals++;
            lastSign = sign;
        }

        var sorted = v.Order().ToArray();
        var peak = sorted[(int)Math.Min(sorted.Length - 1, sorted.Length * 0.95)];
        var holdTime = holds.Sum(x => Math.Max(0, x.To - x.From));

        // Smoothness while firing: the speed sampled every 50 ms inside the holds, its RMS jerk (third difference) and its variation around the mean.
        const double Step = 0.05;
        double jerkSq = 0;
        int jerkN = 0, j = 0;
        var held = new List<double>();
        foreach (var (from, to) in holds)
        {
            if (to - from < 4 * Step) continue;
            var grid = new List<double>();
            for (var t = from; t <= to; t += Step)
            {
                while (j + 1 < s.Count && s[j + 1].TSec <= t) j++;
                grid.Add(v[j]);
            }
            held.AddRange(grid);
            for (var i = 3; i < grid.Count; i++)
            {
                var jerk = (grid[i] - 3 * grid[i - 1] + 3 * grid[i - 2] - grid[i - 3]) / (Step * Step * Step);
                jerkSq += jerk * jerk;
                jerkN++;
            }
        }
        double? rmsJerk = jerkN >= 10 ? Math.Sqrt(jerkSq / jerkN) : null;
        double? speedCv = null;
        if (held.Count >= 10 && held.Average() > 0)
        {
            var m = held.Average();
            speedCv = Math.Sqrt(held.Sum(x => (x - m) * (x - m)) / held.Count) / m;
        }

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
            for (var i = FirstAfter(s, from); i < s.Count; i++)
            {
                var t = s[i].TSec;
                if (t >= kill) break;
                var b = new ViewDirection(s[i].YawDeg, s[i].PitchDeg);
                var step = KillFlicks.AngleBetween(last, b);
                path += step;
                if (double.IsNaN(begin) && v[i] > MoveDegPerSec) begin = t;
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
            Median(interval), Median(dist), Median(fpeak), Median(ftime), Median(react), Median(eff), interval.Count, rmsJerk, speedCv);
    }
}

using KovaaksCompanion.Core.Perf;

namespace KovaaksCompanion.Core.Trajectory;

public enum ShotOutcome : byte { Unknown = 0, Hit = 1, Miss = 2 }

/// <summary>A kill row from the stats CSV on the run time axis. <see cref="Shots"/>/<see cref="Hits"/> count since the previous kill.</summary>
public readonly record struct KillShots(double TSec, int Shots, int Hits);

/// <summary>
/// Labels each Fire event as hit or miss. KovaaK's records no per-shot result, so the labels are derived:
/// kill rows give hits per kill (the last shot before a kill hit), the 1 s perf buckets narrow the rest.
/// Anything not provable stays <see cref="ShotOutcome.Unknown"/>; a wrong colour is worse than a grey one.
/// </summary>
public static class ShotClassifier
{
    /// <summary>The last shot of a kill must land this close to the kill time (clock offset + logging latency).</summary>
    const double KillShotBefore = 0.30, KillShotAfter = 0.10;

    /// <summary>Search range and step for the mouse/CSV clock offset.</summary>
    const double MaxOffset = 1.0, OffsetStep = 0.005;

    /// <summary>Where the kill shot sits relative to the (offset-corrected) kill time while estimating the offset.</summary>
    const double OffsetFireBefore = 0.05, OffsetFireAfter = 0.02;

    /// <summary>Outcome per Fire event of the left button, keyed by the event's time.</summary>
    public static IReadOnlyDictionary<float, ShotOutcome> Classify(IReadOnlyList<TrajectoryEvent> events, IReadOnlyList<KillShots> kills, PerfData? perf) =>
        Classify(events, kills, perf, out _);

    /// <summary>As above; <paramref name="killOffsetSec"/> is the estimated kill-time minus fire-time clock offset that was removed before matching.</summary>
    public static IReadOnlyDictionary<float, ShotOutcome> Classify(IReadOnlyList<TrajectoryEvent> events, IReadOnlyList<KillShots> kills, PerfData? perf, out double killOffsetSec)
    {
        var fires = events.Where(e => e.Type == TrajectoryEventType.Fire && e.Button == 0).Select(e => e.TSec).Order().ToList();
        var result = new Dictionary<float, ShotOutcome>();
        foreach (var f in fires) result[f] = ShotOutcome.Unknown;

        killOffsetSec = EstimateKillOffset(fires, kills);
        var pos = 0;
        foreach (var k in kills)
        {
            var kt = k.TSec - killOffsetSec;
            // The last shot before a kill hit, whatever the shot counts say. Search from pos so a disagreeing kill cannot reuse earlier shots.
            var j = -1;
            var first = -1;
            for (var i = pos; i < fires.Count && fires[i] <= kt + KillShotAfter; i++)
                if (fires[i] >= kt - KillShotBefore) { j = i; if (first < 0) first = i; }
            if (j < 0) continue; // no shot near this kill: leave its shots unknown, resync on the next kill

            // A miss of the next kill can fall in this kill's window: prefer the candidate that makes the shot count agree.
            if (k.Shots >= 1 && j - pos + 1 != k.Shots && pos + k.Shots - 1 is var m && m >= first && m < j) j = m;

            if (k.Shots >= 1 && j - pos + 1 == k.Shots)
            {
                for (var i = pos; i <= j; i++)
                    result[fires[i]] = k.Hits >= k.Shots ? ShotOutcome.Hit
                        : i == j ? ShotOutcome.Hit
                        : k.Hits <= 1 ? ShotOutcome.Miss
                        : ShotOutcome.Unknown;
            }
            else result[fires[j]] = ShotOutcome.Hit; // counts disagree: only the kill shot is provable
            pos = j + 1;
        }

        if (perf is not null) RefineWithBuckets(fires, result, perf);
        return result;
    }

    /// <summary>
    /// Offset d (kill time minus fire time, ±1 s) at which most kills have a shot just before them once shifted by -d.
    /// 0 when there is too little evidence (fewer than max(3, half the kills) aligned) or nothing to align; ties go to the smallest |d|.
    /// </summary>
    public static double EstimateKillOffset(IReadOnlyList<float> fires, IReadOnlyList<KillShots> kills)
    {
        if (fires.Count == 0 || kills.Count == 0) return 0;

        var bestScore = 0;
        var bestStep = 0;
        var steps = (int)Math.Round(MaxOffset / OffsetStep);
        for (var s = -steps; s <= steps; s++)
        {
            var d = s * OffsetStep;
            var score = 0;
            foreach (var k in kills)
            {
                var lo = k.TSec - d - OffsetFireBefore;
                var i = LowerBound(fires, lo);
                if (i < fires.Count && fires[i] <= k.TSec - d + OffsetFireAfter) score++;
            }
            if (score > bestScore || (score == bestScore && Math.Abs(s) < Math.Abs(bestStep))) { bestScore = score; bestStep = s; }
        }
        return bestScore < Math.Max(3, kills.Count / 2) ? 0 : bestStep * OffsetStep;
    }

    /// <summary>Index of the first element >= value in a sorted list.</summary>
    static int LowerBound(IReadOnlyList<float> sorted, double value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// In a 1 s bucket whose shot count matches the Fire events, the hits not yet explained are either none
    /// (all unknown shots missed) or all of the unknown shots.
    /// </summary>
    static void RefineWithBuckets(List<float> fires, Dictionary<float, ShotOutcome> result, PerfData perf)
    {
        var buckets = perf.Buckets;
        var byBucket = new Dictionary<int, List<float>>();
        foreach (var f in fires)
        {
            var b = BucketOf(buckets, f);
            if (b < 0) continue;
            if (!byBucket.TryGetValue(b, out var l)) byBucket[b] = l = [];
            l.Add(f);
        }

        foreach (var (b, list) in byBucket)
        {
            var bucket = buckets[b];
            if (bucket.Shots != list.Count) continue;
            var unknown = list.Where(f => result[f] == ShotOutcome.Unknown).ToList();
            if (unknown.Count == 0) continue;
            var knownHits = list.Count(f => result[f] == ShotOutcome.Hit);
            var unexplained = bucket.Hits - knownHits;
            if (unexplained == 0) foreach (var f in unknown) result[f] = ShotOutcome.Miss;
            else if (unexplained == unknown.Count) foreach (var f in unknown) result[f] = ShotOutcome.Hit;
        }
    }

    /// <summary>Bucket i covers (T[i-1], T[i]]; the first covers one second before its end. -1 when outside.</summary>
    public static int BucketOf(IReadOnlyList<PerfBucket> buckets, double t)
    {
        for (var i = 0; i < buckets.Count; i++)
        {
            var from = i == 0 ? buckets[0].TSec - 1.0 : buckets[i - 1].TSec;
            if (t > from && t <= buckets[i].TSec) return i;
        }
        return -1;
    }
}

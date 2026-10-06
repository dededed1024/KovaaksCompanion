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

    /// <summary>Outcome per Fire event of the left button, keyed by the event's time.</summary>
    public static IReadOnlyDictionary<float, ShotOutcome> Classify(IReadOnlyList<TrajectoryEvent> events, IReadOnlyList<KillShots> kills, PerfData? perf)
    {
        var fires = events.Where(e => e.Type == TrajectoryEventType.Fire && e.Button == 0).Select(e => e.TSec).Order().ToList();
        var result = new Dictionary<float, ShotOutcome>();
        foreach (var f in fires) result[f] = ShotOutcome.Unknown;

        var pos = 0;
        foreach (var k in kills)
        {
            if (k.Shots < 1 || pos + k.Shots > fires.Count) break;
            var last = fires[pos + k.Shots - 1];
            if (last < k.TSec - KillShotBefore || last > k.TSec + KillShotAfter) break; // events and CSV disagree: stop trusting
            for (var i = pos; i < pos + k.Shots; i++)
                result[fires[i]] = k.Hits >= k.Shots ? ShotOutcome.Hit
                    : i == pos + k.Shots - 1 ? ShotOutcome.Hit
                    : k.Hits <= 1 ? ShotOutcome.Miss
                    : ShotOutcome.Unknown;
            pos += k.Shots;
        }

        if (perf is not null) RefineWithBuckets(fires, result, perf);
        return result;
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
    static int BucketOf(IReadOnlyList<PerfBucket> buckets, double t)
    {
        for (var i = 0; i < buckets.Count; i++)
        {
            var from = i == 0 ? buckets[0].TSec - 1.0 : buckets[i - 1].TSec;
            if (t > from && t <= buckets[i].TSec) return i;
        }
        return -1;
    }
}

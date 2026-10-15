using KovaaksCompanion.Core.Replays;

namespace KovaaksCompanion.Core.Perf;

/// <summary>
/// Cache of each replay's running totals per second (<see cref="PerfSeries"/>), keyed by replay id and checked against the size and write time
/// of the replay's data file. Reading and unpacking a replay is the costly part. Thread-safe; call <see cref="Save"/> once after a batch of lookups.
/// </summary>
public sealed class SeriesCache(string file)
{
    /// <summary>Bump when <see cref="Entry"/> or <see cref="PerfPoint"/> changes shape, so older series caches are rebuilt.</summary>
    const int Schema = 1;

    /// <summary>A point as stored: the figures a replay does not have are null (JSON has no NaN).</summary>
    record Cached(double T, double Acc, double? Score, int Kills, int Shots, int Hits, double? Eff, double? Reloads, double? Distance)
    {
        static double? Of(double v) => double.IsNaN(v) ? null : v;
        public static Cached From(PerfPoint p) => new(p.TSec, p.Accuracy, Of(p.Score), p.Kills, p.Shots, p.Hits, Of(p.DamageEff), Of(p.Reloads), Of(p.Distance));
        public PerfPoint ToPoint() => new(T, Acc, Score ?? double.NaN, Kills, Shots, Hits, Eff ?? double.NaN, Reloads ?? double.NaN, Distance ?? double.NaN);
    }

    record Entry(FileStamp Stamp, List<Cached>? Points);

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _items = CacheFile.Read<Entry>(file, Schema);
    bool _dirty;

    /// <summary>The replay's running totals; null when it has neither per-second data nor kills, or cannot be read.</summary>
    public IReadOnlyList<PerfPoint>? Get(ReplayArchive archive, ReplayInfo info)
    {
        var key = info.Id.ToString("N");
        try
        {
            var stamp = archive.DataStamp(info.Id);
            lock (_gate)
                if (_items.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit.Points?.Select(c => c.ToPoint()).ToList();
            var points = Compute(archive.ReadParts(info.Id), info);
            lock (_gate) { _items[key] = new(stamp, points?.Select(Cached.From).ToList()); _dirty = true; }
            return points;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReplayFormatException or PerfFormatException) { return null; }
    }

    /// <summary>Per second from the replay's per-second data, else (older recordings) one point per kill from its statistics, closed by a point at the play's end. The score is unknown between kills there, so it is NaN.</summary>
    static List<PerfPoint>? Compute(RepParts parts, ReplayInfo info)
    {
        if (parts.Perf is { Buckets.Count: > 0 } perf) return PerfSeries.Build(perf).ToList();
        if (parts.Stats is not { KillList.Count: > 0 } st) return null;
        var pts = new List<PerfPoint>();
        int shots = 0, hits = 0, kills = 0;
        foreach (var k in st.KillList)
        {
            shots += k.Shots; hits += k.Hits; kills++;
            pts.Add(new PerfPoint(k.TimeMs / 1000.0, shots == 0 ? 0 : (double)hits / shots, double.NaN, kills, shots, hits, double.NaN));
        }
        var length = (info.End - info.Start).TotalSeconds;
        if (length > pts[^1].TSec) pts.Add(pts[^1] with { TSec = length });
        return pts;
    }

    public void Save()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            CacheFile.Write(file, Schema, new Dictionary<string, Entry>(_items));
            _dirty = false;
        }
    }
}

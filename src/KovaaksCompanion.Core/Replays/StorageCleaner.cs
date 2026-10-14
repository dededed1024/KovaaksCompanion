using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.Core.Replays;

/// <summary>A replay with a video file, and the size of that file.</summary>
public sealed record StoredReplay(ReplayInfo Info, long Bytes);

/// <summary>Picks replay videos to delete: earlier runs beaten later in the same play session, and the oldest ones while the videos folder is over its size limit.</summary>
public static class StorageCleaner
{
    const long Gb = 1024L * 1024 * 1024;

    public static long LimitBytes(int gb) => gb <= 0 ? 0 : gb * Gb;

    /// <summary>Replays that have a video file, with its size, newest first.</summary>
    public static List<StoredReplay> Scan(ReplayArchive archive) =>
        archive.List().Where(i => i.HasVideo).Select(i => new StoredReplay(i, SizeOf(archive.VideoPath(i)))).ToList();

    static long SizeOf(string? path)
    {
        try { return path is null ? 0 : new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The replays of the newest play session (the one a replay was just saved into).</summary>
    public static List<StoredReplay> NewestSession(IEnumerable<StoredReplay> all, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? PlaySession.DefaultGap;
        var session = new List<StoredReplay>();
        StoredReplay? next = null;
        foreach (var s in all.OrderByDescending(s => s.Info.Start))
        {
            if (next != null && PlaySession.NewSession(s.Info.Scenario, s.Info.End, next.Info.Scenario, next.Info.Start, next.Info.End, gap)) break;
            session.Add(s);
            next = s;
        }
        return session;
    }

    /// <summary>Runs that a later run of the same scenario in the same play session scored higher than. Runs for which <paramref name="keep"/> is true stay.</summary>
    public static List<StoredReplay> Superseded(IEnumerable<StoredReplay> all, Func<ReplayInfo, bool> keep, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? PlaySession.DefaultGap;
        var result = new List<StoredReplay>();
        var group = new List<StoredReplay>();
        StoredReplay? last = null;

        void Flush()
        {
            foreach (var g in group.GroupBy(s => s.Info.Scenario, StringComparer.OrdinalIgnoreCase))
            {
                var later = double.MinValue;
                foreach (var s in g.Reverse())
                {
                    if (s.Info.Score < later && !keep(s.Info)) result.Add(s);
                    later = Math.Max(later, s.Info.Score);
                }
            }
            group.Clear();
        }

        foreach (var s in all.OrderBy(s => s.Info.Start))
        {
            if (group.Count > 0 && (maxGap is { } g ? s.Info.Start - last!.Info.End > g : PlaySession.NewSession(last!.Info.Scenario, last.Info.End, s.Info.Scenario, s.Info.Start, s.Info.End, gap))) Flush();
            if (group.Count == 0 || s.Info.End > last!.Info.End) last = s;
            group.Add(s);
        }
        Flush();
        return result;
    }

    /// <summary>Replays that beat every earlier run of their scenario, counting the cached scores of <paramref name="cached"/> (scenario to scores).</summary>
    public static HashSet<StoredReplay> PersonalBests(IEnumerable<StoredReplay> all, IReadOnlyDictionary<string, List<CloudScore>> cached)
    {
        var pbs = new HashSet<StoredReplay>();
        foreach (var g in all.GroupBy(s => s.Info.Scenario, StringComparer.OrdinalIgnoreCase))
        {
            var gone = cached.FirstOrDefault(d => d.Key.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).Value ?? [];
            var best = double.MinValue;
            foreach (var s in g.OrderBy(s => s.Info.Start))
            {
                var earlier = gone.Where(d => d.Time < s.Info.End).Select(d => d.Score).DefaultIfEmpty(double.MinValue).Max();
                if (s.Info.Score > Math.Max(best, earlier)) pbs.Add(s);
                best = Math.Max(best, s.Info.Score);
            }
        }
        return pbs;
    }

    /// <summary>
    /// Replays to delete so the rest fits <paramref name="limitBytes"/> (0 = no limit). The newest replay and the ones <paramref name="keep"/> accepts are never chosen,
    /// so the result can still leave the videos over the limit.
    /// </summary>
    public static List<StoredReplay> OverLimit(IReadOnlyList<StoredReplay> all, long limitBytes, Func<ReplayInfo, bool> keep)
    {
        var result = new List<StoredReplay>();
        if (limitBytes <= 0) return result;
        var total = all.Sum(s => s.Bytes);
        if (total <= limitBytes) return result;
        var newest = all.MaxBy(s => s.Info.Start);
        foreach (var s in all.Where(s => s != newest && !keep(s.Info)).OrderBy(s => s.Info.Start))
        {
            if (total <= limitBytes) break;
            result.Add(s);
            total -= s.Bytes;
        }
        return result;
    }

    /// <summary>Replays that ended more than <paramref name="days"/> days before <paramref name="now"/> (0 = no limit), except the ones <paramref name="keep"/> accepts.</summary>
    public static List<StoredReplay> Expired(IReadOnlyList<StoredReplay> all, int days, DateTime now, Func<ReplayInfo, bool> keep) =>
        days <= 0 ? [] : all.Where(s => s.Info.End < now.AddDays(-days) && !keep(s.Info)).ToList();

    /// <summary>Deletes the video files of the replays and keeps everything else of them. Returns how many were deleted; a video in use is skipped.</summary>
    public static int Delete(IEnumerable<StoredReplay> victims, ReplayArchive archive) => victims.Count(s => archive.DeleteVideo(s.Info));
}

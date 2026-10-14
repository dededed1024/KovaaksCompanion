using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.Core.Replays;

public sealed record StoredReplay(string Folder, ReplayInfo Info, long Bytes);

/// <summary>Picks replay folders to delete: earlier runs beaten later in the same play session, and the oldest ones while the replays folder is over its size limit.</summary>
public static class StorageCleaner
{
    const long Gb = 1024L * 1024 * 1024;

    public static long LimitBytes(int gb) => gb <= 0 ? 0 : gb * Gb;

    public static long FolderBytes(string folder)
    {
        try { return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Complete replay folders with their sizes, newest first.</summary>
    public static List<StoredReplay> Scan(string root) =>
        ReplayStore.List(root).Select(s => new StoredReplay(s.Folder, s.Info, FolderBytes(s.Folder))).ToList();

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
    /// so the result can still leave the folder over the limit.
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

    /// <summary>
    /// Deletes the videos of the replay folders and keeps the rest of each replay in the archive (<paramref name="archiveRoot"/>). The statistics are stored first: when that
    /// fails (<see cref="IOException"/>) no folder is touched. Returns how many were deleted; a folder in use is skipped.
    /// </summary>
    public static int Delete(IEnumerable<StoredReplay> victims, StatsStore store, string archiveRoot)
    {
        var list = victims.ToList();
        store.Archive(list.Select(s => ReplayStore.ReadRecord(s.Folder)).OfType<RunRecord>().ToList()); // one transaction, not one per replay
        return list.Count(s => ReplayStore.Retire(s.Folder, archiveRoot) != null);
    }
}

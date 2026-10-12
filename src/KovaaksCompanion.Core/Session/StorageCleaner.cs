using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.Core.Session;

public sealed record StoredSession(string Folder, SessionInfo Info, long Bytes);

/// <summary>Picks session folders to delete: earlier runs beaten later in the same play session, and the oldest ones while the sessions folder is over its size limit.</summary>
public static class StorageCleaner
{
    const long Gb = 1024L * 1024 * 1024;

    public static long LimitBytes(int gb) => gb <= 0 ? 0 : gb * Gb;

    public static long FolderBytes(string folder)
    {
        try { return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Complete session folders with their sizes, newest first.</summary>
    public static List<StoredSession> Scan(string root) =>
        SessionStore.List(root).Select(s => new StoredSession(s.Folder, s.Info, FolderBytes(s.Folder))).ToList();

    /// <summary>Runs that a later run of the same scenario in the same play session scored higher than. Runs for which <paramref name="keep"/> is true stay.</summary>
    public static List<StoredSession> Superseded(IEnumerable<StoredSession> all, Func<SessionInfo, bool> keep, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? PlaySession.DefaultGap;
        var result = new List<StoredSession>();
        var group = new List<StoredSession>();
        StoredSession? last = null;

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

    /// <summary>Sessions that beat every earlier run of their scenario, counting the deleted runs of <paramref name="deleted"/> (scenario to scores).</summary>
    public static HashSet<StoredSession> PersonalBests(IEnumerable<StoredSession> all, IReadOnlyDictionary<string, List<CloudScore>> deleted)
    {
        var pbs = new HashSet<StoredSession>();
        foreach (var g in all.GroupBy(s => s.Info.Scenario, StringComparer.OrdinalIgnoreCase))
        {
            var gone = deleted.FirstOrDefault(d => d.Key.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).Value ?? [];
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
    /// Sessions to delete so the rest fits <paramref name="limitBytes"/> (0 = no limit). The newest session and the ones <paramref name="keep"/> accepts are never chosen,
    /// so the result can still leave the folder over the limit.
    /// </summary>
    public static List<StoredSession> OverLimit(IReadOnlyList<StoredSession> all, long limitBytes, Func<SessionInfo, bool> keep)
    {
        var result = new List<StoredSession>();
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

    /// <summary>Permanently deletes the session folders and keeps each run's score in the cache like a server score. Returns how many were deleted; a folder in use is skipped.</summary>
    public static int Delete(IEnumerable<StoredSession> victims, StatsCache cache)
    {
        var n = 0;
        foreach (var s in victims)
        {
            try { Directory.Delete(s.Folder, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            cache.HideRun(s.Info.Scenario, s.Info.End, s.Info.Score);
            n++;
        }
        return n;
    }
}

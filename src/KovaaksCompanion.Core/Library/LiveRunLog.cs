using System.Text;
using System.Text.Json;

namespace KovaaksCompanion.Core.Library;

/// <summary>
/// Session id per run in live-runs.json, a signed masked file (<see cref="SignedFile"/>): run key (<see cref="UiState.RunKey"/>) to the session's id.
/// Runs sharing an id are one <see cref="PlaySession"/>. Ids &gt; 0 are live sessions (game launch ticks); ids &lt; 0 are sessions
/// grouped once by <see cref="PlaySession.DefaultGap"/> (legacy local runs, server records) and then kept. A tampered file reads as empty.
/// </summary>
public static class LiveRunLog
{
    public static Dictionary<string, long> Load(string path)
    {
        try
        {
            if (SignedFile.TryReadMasked(path) is { } b) return JsonSerializer.Deserialize<Dictionary<string, long>>(b) ?? [];
            // Older plain-JSON file: accepted once; the next save signs it.
            if (File.Exists(path) && SignedFile.TryRead(path) == null && IsPlainJson(path)) return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? [];
            return [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    static bool IsPlainJson(string path) => File.ReadAllBytes(path) is { Length: > 0 } b && b[0] == (byte)'{';

    static void Save(string path, Dictionary<string, long> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SignedFile.WriteMasked(path, JsonSerializer.SerializeToUtf8Bytes(all));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Tags a run with its live session id and returns the updated map.</summary>
    public static Dictionary<string, long> Add(string path, string scenario, DateTime end, long liveId)
    {
        var all = Load(path);
        all[UiState.RunKey(scenario, end)] = liveId;
        Save(path, all);
        return all;
    }

    /// <summary>
    /// Gives plays without a session id one: it joins the previous play's session when that is not a live one and the gap
    /// to it is at most <see cref="PlaySession.DefaultGap"/>, else starts a new session. Persists when anything changed and returns the map.
    /// </summary>
    public static Dictionary<string, long> Assign(string path, IEnumerable<(string Scenario, DateTime Start, DateTime End)> plays)
    {
        var all = Load(path);
        var changed = false;
        long? prevId = null;
        var prevEnd = DateTime.MinValue;
        foreach (var (scenario, start, end) in plays.OrderBy(p => p.Start))
        {
            var key = UiState.RunKey(scenario, end);
            if (!all.TryGetValue(key, out var id))
            {
                id = prevId is < 0 and var p && start - prevEnd <= PlaySession.DefaultGap ? p : -start.Ticks;
                all[key] = id;
                changed = true;
            }
            prevId = id;
            prevEnd = end;
        }
        if (changed) Save(path, all);
        return all;
    }

    /// <summary>Legacy local runs (no live id) and server records, in one pass each, so a record never joins a run's session.</summary>
    public static Dictionary<string, long> AssignRuns(string path, IEnumerable<RunRecord> runs) =>
        Assign(path, runs.Select(r => (r.Scenario, r.Start, r.End)));

    public static Dictionary<string, long> AssignServer(string path, IEnumerable<(string Scenario, DateTime Time)> records) =>
        Assign(path, records.Select(r => (r.Scenario, r.Time, r.Time)));
}

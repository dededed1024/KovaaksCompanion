using System.Text;
using System.Text.Json;

namespace KovaaksCompanion.Core.Library;

/// <summary>
/// Session id per run in sessions.dat, a signed masked file (<see cref="SignedFile"/>): run key (<see cref="UiState.RunKey"/>) to the session's id.
/// Plays (local runs and server-only scores alike) sharing an id are one <see cref="PlaySession"/>. Ids are GUIDs. A live session is one game launch;
/// plays without an id (legacy local runs, server records) are grouped once by <see cref="PlaySession.DefaultGap"/> and then kept. A tampered file reads as empty.
/// </summary>
public static class LiveRunLog
{
    static readonly object Gate = new();

    static readonly Dictionary<string, Guid> Pending = [];

    /// <summary>The main map plus the live session's runs.</summary>
    public static Dictionary<string, Guid> Load(string path)
    {
        lock (Gate)
        {
            var all = LoadFile(path);
            foreach (var kv in Pending) all[kv.Key] = kv.Value;
            return all;
        }
    }

    /// <summary>Writes the live session's runs into the main file.</summary>
    public static void Flush(string path)
    {
        lock (Gate)
        {
            if (Pending.Count == 0) return;
            var all = LoadFile(path);
            foreach (var kv in Pending) all[kv.Key] = kv.Value;
            SaveFile(path, all);
            Pending.Clear();
        }
    }

    static Dictionary<string, Guid> LoadFile(string path)
    {
        try
        {
            if (SignedFile.TryReadMasked(path) is { } b) return Parse(b);
            // Older plain-JSON file: accepted once; the next save signs it.
            if (File.Exists(path) && SignedFile.TryRead(path) == null && IsPlainJson(path)) return Parse(File.ReadAllBytes(path));
            return [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>GUID strings; files from before GUIDs hold numbers, which map to a fixed GUID each so their sessions stay together.</summary>
    static Dictionary<string, Guid> Parse(byte[] json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Guid>>(json) ?? []; }
        catch (JsonException)
        {
            var old = JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? [];
            return old.ToDictionary(kv => kv.Key, kv => new Guid(0, 0, 0, BitConverter.GetBytes(kv.Value)));
        }
    }

    static bool IsPlainJson(string path) => File.ReadAllBytes(path) is { Length: > 0 } b && b[0] == (byte)'{';

    static void SaveFile(string path, Dictionary<string, Guid> all)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            AccessGuard.Run(dir, true, () => { Directory.CreateDirectory(dir); SignedFile.WriteMasked(path, JsonSerializer.SerializeToUtf8Bytes(all)); });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Diagnostics.ErrorReporter.Report("live runs save " + path, e); }
    }

    /// <summary>A new id for one session.</summary>
    public static Guid NewId() => Guid.NewGuid();

    /// <summary>Tags a run with its live session id (memory only until <see cref="Flush"/>) and returns <paramref name="current"/> with it added.</summary>
    public static Dictionary<string, Guid> Add(IReadOnlyDictionary<string, Guid> current, string scenario, DateTime end, Guid liveId)
    {
        var key = UiState.RunKey(scenario, end);
        lock (Gate) Pending[key] = liveId;
        return new Dictionary<string, Guid>(current) { [key] = liveId };
    }

    /// <summary>
    /// Gives plays without a session id one: it joins the previous play's session when the gap
    /// to it is at most <see cref="PlaySession.DefaultGap"/>, else starts a new session. Persists when anything changed and returns the map.
    /// </summary>
    public static Dictionary<string, Guid> Assign(string path, IEnumerable<(string Scenario, DateTime Start, DateTime End)> plays)
    {
        lock (Gate) return AssignLocked(path, plays);
    }

    static Dictionary<string, Guid> AssignLocked(string path, IEnumerable<(string Scenario, DateTime Start, DateTime End)> plays)
    {
        var all = Load(path);
        var changed = false;
        Guid? prevId = null;
        var prevEnd = DateTime.MinValue;
        foreach (var (scenario, start, end) in plays.OrderBy(p => p.Start))
        {
            var key = UiState.RunKey(scenario, end);
            if (!all.TryGetValue(key, out var id))
            {
                id = prevId is { } p && start - prevEnd <= PlaySession.DefaultGap ? p : NewId();
                all[key] = id;
                changed = true;
            }
            prevId = id;
            prevEnd = end;
        }
        if (changed)
        {
            SaveFile(path, all.Where(kv => !Pending.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)); // the live session's runs wait for Flush
        }
        return all;
    }

    /// <summary>Runs without a session id (local runs and server-only scores alike).</summary>
    public static Dictionary<string, Guid> AssignAll(string path, IEnumerable<RunRecord> runs) =>
        Assign(path, runs.Select(r => (r.Scenario, r.Start, r.End)));
}

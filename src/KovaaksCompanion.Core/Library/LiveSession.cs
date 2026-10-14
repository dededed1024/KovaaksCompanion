using System.Collections.Concurrent;
using KovaaksCompanion.Core.Replays;

namespace KovaaksCompanion.Core.Library;

/// <summary>
/// The play session of the running game: one game launch. Gives its runs the session id (kept in memory by <see cref="RunSessionIds"/> until the game exits or the app closes),
/// remembers the replays and personal bests saved in it, and says which of them a later run beat. Nothing here scans the disk.
/// </summary>
public sealed class LiveSession(string runsFile)
{
    readonly object _gate = new();
    readonly List<StoredReplay> _replays = [];
    readonly ConcurrentDictionary<string, byte> _pbs = new();
    volatile Tuple<Guid>? _id;
    Guid _replaysId;

    /// <summary>Id of the open session; null while the game is not running.</summary>
    public Guid? Id => _id?.Item1;

    /// <summary>When the current game launch started (the game process start time).</summary>
    public DateTime Since { get; private set; }

    public void Launch(DateTime since)
    {
        Since = since;
        lock (_gate) _replays.Clear();
        _id = Tuple.Create(RunSessionIds.NewId());
    }

    /// <summary>Closes the session; its runs are written to the main file in the background.</summary>
    public Task Exit()
    {
        _id = null;
        return Task.Run(Flush);
    }

    /// <summary>Writes the session's run ids to the main file now (the app is closing).</summary>
    public void Flush() => RunSessionIds.Flush(runsFile);

    /// <summary>Tags a finished run with the open session (no-op while the game is not running).</summary>
    public void Tag(string scenario, DateTime end)
    {
        if (Id is { } id) PlaySession.RunSessionIds = RunSessionIds.Add(PlaySession.RunSessionIds, scenario, end, id);
    }

    /// <summary>Records that the run set a personal best, so it can be kept.</summary>
    public void NotePersonalBest(string scenario, DateTime end) => _pbs.TryAdd(UiState.RunKey(scenario, end), 0);

    /// <summary>
    /// A replay was saved. Returns the replays of its session that a later run of the same scenario beat (empty unless <paramref name="deleteBeaten"/>),
    /// never a <paramref name="favorite"/> nor, when <paramref name="keepPersonalBests"/>, a personal best; they are forgotten here, the caller deletes them.
    /// </summary>
    public List<StoredReplay> Saved(ReplayInfo info, bool deleteBeaten, Func<ReplayInfo, bool> favorite, bool keepPersonalBests)
    {
        if (!PlaySession.RunSessionIds.TryGetValue(UiState.RunKey(info.Scenario, info.End), out var id)) return [];
        lock (_gate)
        {
            if (id != _replaysId) { _replays.Clear(); _replaysId = id; }
            _replays.Add(new StoredReplay(info, 0));
            if (!deleteBeaten) return [];
            var victims = StorageCleaner.Superseded(_replays.ToList(), i => favorite(i) || (keepPersonalBests && _pbs.ContainsKey(UiState.RunKey(i.Scenario, i.End))));
            _replays.RemoveAll(victims.Contains);
            return victims;
        }
    }

    /// <summary>The open session among <paramref name="sessions"/> (the one holding its runs), else null.</summary>
    public PlaySession? Find(IEnumerable<PlaySession> sessions)
    {
        if (Id is not { } id) return null;
        var ids = PlaySession.RunSessionIds;
        return sessions.FirstOrDefault(s => s.Runs.Any(r => ids.TryGetValue(UiState.RunKey(r.Scenario, r.End), out var x) && x == id));
    }
}

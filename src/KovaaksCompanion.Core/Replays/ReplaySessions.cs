using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Replays;

/// <summary>
/// Which play session a replay belongs to, and so which video folder it goes in. The rules are here and work on what they are given (no disk access of their own),
/// so the host only supplies the replays and the session ids.
/// </summary>
public static class ReplaySessions
{
    static Guid? IdOf(IReadOnlyDictionary<string, Guid> ids, ReplayInfo i) => ids.TryGetValue(UiState.RunKey(i.Scenario, i.End), out var g) ? g : null;

    /// <summary>
    /// The session a finished run belongs to (not a run of an open live session, which takes that session's id): the session of the replay before it when that ended at most
    /// <see cref="PlaySession.DefaultGap"/> earlier (the rule that gives every run a session id, see <see cref="RunSessionIds.Assign"/>), else a new one.
    /// <paramref name="lastEndedBy"/> gives the replay that ended last at or before a moment (<see cref="ReplayArchive.LastEndedBy"/>).
    /// Returns the id and the start of the session's earliest replay, found by walking back through the replays with that id.
    /// </summary>
    public static (DateTime Since, Guid Id) Of(RunStats run, IReadOnlyDictionary<string, Guid> ids, Func<DateTime, ReplayInfo?> lastEndedBy)
    {
        if (lastEndedBy(run.Start) is { } prev && PlaySession.WithinGap(prev.End, run.Start) && IdOf(ids, prev) is { } id)
        {
            var since = prev.Start;
            for (var p = lastEndedBy(prev.Start); p != null && IdOf(ids, p) == id; p = lastEndedBy(p.Start)) since = p.Start;
            return (since, id);
        }
        return (run.Start, RunSessionIds.NewId());
    }

    /// <summary>
    /// Names the session folder of a replay among <paramref name="replays"/>: the session's id and the start of its earliest replay.
    /// Null for a run that has no session id yet.
    /// </summary>
    public static Func<ReplayInfo, string?> FolderOf(IEnumerable<ReplayInfo> replays, IReadOnlyDictionary<string, Guid> ids)
    {
        var began = new Dictionary<Guid, DateTime>();
        foreach (var i in replays)
            if (IdOf(ids, i) is { } id && (!began.TryGetValue(id, out var b) || i.Start < b)) began[id] = i.Start;
        return i => IdOf(ids, i) is { } id && began.TryGetValue(id, out var since) ? ReplayArchive.SessionFolder(since, id) : null;
    }
}

using KovaaksCompanion.Core.Replays;

namespace KovaaksCompanion.Core.Trajectory;

/// <summary>
/// Cache of each replay's mouse-speed figures (reading and analysing a trajectory is the costly part), keyed by replay folder name and checked against
/// the trajectory file's size and write time. Thread-safe; call <see cref="Save"/> once after a batch of lookups.
/// </summary>
public sealed class SpeedCache(string file)
{
    /// <summary>Bump when <see cref="Entry"/> or <see cref="RunSpeed"/> changes shape, so older speed caches are rebuilt.</summary>
    const int Schema = 2;

    record Entry(FileStamp Stamp, RunSpeed? Speed);

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _items = CacheFile.Read<Entry>(file, Schema);
    bool _dirty;

    /// <summary>The replay's speed figures; null when it has no trajectory, an unreadable one, or one too short to analyse.</summary>
    public RunSpeed? Get(string folder)
    {
        var path = Path.Combine(folder, ReplayFolder.TrajectoryFile);
        var key = Path.GetFileName(folder);
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = FileStamp.Of(path);
            lock (_gate)
                if (_items.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit.Speed;
            var speed = ReplayStore.ReadSpeed(path);
            lock (_gate) { _items[key] = new(stamp, speed); _dirty = true; }
            return speed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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

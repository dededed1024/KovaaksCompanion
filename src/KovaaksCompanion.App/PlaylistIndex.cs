using System.IO;
using System.Text.Json;
using KovaaksCompanion.Core;

namespace KovaaksCompanion.App;

/// <summary>Scenario names per playlist (by KovaaK's benchmark id), remembered from the server progress we already load, so Home can tell which playlists were played without fetching.</summary>
sealed class PlaylistIndex
{
    readonly string _file;
    readonly Dictionary<int, string[]> _map = [];

    public PlaylistIndex(string file)
    {
        _file = file;
        try
        {
            if (SignedFile.TryRead(_file) is { } b && JsonSerializer.Deserialize<Dictionary<int, string[]>>(b) is { } m) foreach (var (k, v) in m) _map[k] = v;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<string> Scenarios(int id) => _map.TryGetValue(id, out var v) ? v : [];

    public void Set(int id, IEnumerable<string> names)
    {
        var arr = names.ToArray();
        if (arr.Length == 0 || (_map.TryGetValue(id, out var old) && old.SequenceEqual(arr))) return;
        _map[id] = arr;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            SignedFile.Write(_file, JsonSerializer.SerializeToUtf8Bytes(_map));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

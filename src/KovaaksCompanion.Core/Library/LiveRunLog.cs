using System.Text.Json;

namespace KovaaksCompanion.Core.Library;

/// <summary>Runs that finished while a live session was open, in live-runs.json: run key (<see cref="UiState.RunKey"/>) to the live session's id. Runs sharing an id are one <see cref="PlaySession"/>.</summary>
public static class LiveRunLog
{
    public static Dictionary<string, long> Load(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Tags a run with its live session id and returns the updated map.</summary>
    public static Dictionary<string, long> Add(string path, string scenario, DateTime end, long liveId)
    {
        var all = Load(path);
        all[UiState.RunKey(scenario, end)] = liveId;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return all;
    }
}

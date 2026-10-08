using KovaaksCompanion.Core.Input;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;
using KovaaksCompanion.Core.Trajectory;
using KovaaksCompanion.Core.Video;

namespace KovaaksCompanion.Core.Session;

/// <summary>Cuts one finished run into a session folder (SES-001): clip, CSV copy, trajectory.bin, session.json. Runs off the caller's thread (SES-004).</summary>
public sealed class SessionSaver
{
    /// <param name="progress">0..1 of the clip work done.</param>
    public delegate Task<ClipResult?> ClipExtractor(DateTime startUtc, DateTime endUtc, string outputPath, Action<double> progress);

    readonly string _root;
    readonly MouseRingBuffer _buffer;
    readonly Func<ClockAnchor> _anchor;
    readonly ClipExtractor _extract;

    public event Action<SessionInfo, string>? Saved;
    public event Action<string>? Error;

    /// <param name="anchor">Called once per run: QPC and the wall clock drift apart (sleep, clock sync), so a startup anchor misplaces the mouse window.</param>
    public SessionSaver(string sessionsRoot, MouseRingBuffer buffer, Func<ClockAnchor> anchor, ClipExtractor extract)
    {
        _root = sessionsRoot; _buffer = buffer; _anchor = anchor; _extract = extract;
    }

    /// <summary>Fire-and-forget save on a thread-pool thread; failures go to <see cref="Error"/>.</summary>
    public Task Enqueue(RunStats run, string csvPath, Action<double>? progress = null) => Task.Run(async () =>
    {
        try { await SaveAsync(run, csvPath, progress); }
        catch (Exception e) { Error?.Invoke($"Saving '{run.Scenario}' failed: {e.Message}"); }
    });

    /// <summary>The .perf is written a moment after the CSV; for a fresh CSV wait briefly, and skip it when it never shows up.</summary>
    static async Task CopyPerfAsync(string csvPath, string folder)
    {
        var perf = PerfParser.PathFor(csvPath);
        var attempts = DateTime.Now - File.GetLastWriteTime(csvPath) < TimeSpan.FromSeconds(10) ? 20 : 1;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (File.Exists(perf))
                {
                    File.Copy(perf, Path.Combine(folder, Path.GetFileName(perf)), overwrite: true);
                    return;
                }
            }
            catch (IOException) { } // still being written
            if (i < attempts - 1) await Task.Delay(250);
        }
    }

    public async Task<string> SaveAsync(RunStats run, string csvPath, Action<double>? progress = null)
    {
        var folder = Path.Combine(_root, SessionFolder.Name(run.Start, run.Scenario));
        Directory.CreateDirectory(folder);

        var startUtc = DateTime.SpecifyKind(run.Start, DateTimeKind.Local).ToUniversalTime();
        var endUtc = DateTime.SpecifyKind(run.End, DateTimeKind.Local).ToUniversalTime();

        ClipResult? clip = null;
        try { clip = await _extract(startUtc, endUtc, Path.Combine(folder, SessionFolder.VideoFile), progress ?? (_ => { })); }
        catch (Exception e) { Error?.Invoke($"Video clip failed: {e.Message}"); }
        var videoCovers = clip != null && clip.ClipStartUtc <= startUtc && clip.ClipStartUtc + clip.Duration >= endUtc;

        var built = TrajectoryBuilder.Build(run, _buffer, _anchor());
        if (built.Trajectory != null)
            await File.WriteAllBytesAsync(Path.Combine(folder, SessionFolder.TrajectoryFile), TrajectorySerializer.Serialize(built.Trajectory));
        File.Copy(csvPath, Path.Combine(folder, Path.GetFileName(csvPath)), overwrite: true);
        await CopyPerfAsync(csvPath, folder);

        var info = new SessionInfo
        {
            Scenario = run.Scenario, Start = run.Start, End = run.End,
            VideoOffsetSec = clip?.ToClipTime(startUtc).TotalSeconds ?? 0,
            SampleRateHz = TrajectoryBuilder.DefaultRateHz,
            Settings = run.Settings,
            Partial = built.Partial || !videoCovers,
            HasVideo = clip != null,
            DegreesAvailable = built.DegreesAvailable,
            Score = run.Score, Kills = run.Kills, HitCount = run.HitCount, MissCount = run.MissCount,
        };
        SessionStore.WriteInfo(folder, info); // last: a folder with session.json is complete
        Saved?.Invoke(info, folder);
        return folder;
    }
}

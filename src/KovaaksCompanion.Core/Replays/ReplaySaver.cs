using KovaaksCompanion.Core.Input;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;
using KovaaksCompanion.Core.Trajectory;
using KovaaksCompanion.Core.Video;

namespace KovaaksCompanion.Core.Replays;

/// <summary>Cuts one finished run into a replay folder (SES-001): clip, CSV copy, trajectory.bin, session.json. Runs off the caller's thread (SES-004).</summary>
public sealed class ReplaySaver
{
    /// <param name="progress">0..1 of the clip work done.</param>
    public delegate Task<ClipResult?> ClipExtractor(long startQpc, long endQpc, string outputPath, Action<double> progress);

    readonly string _root;
    readonly MouseRingBuffer _buffer;
    readonly Func<ClockAnchor> _anchor;
    readonly ClipExtractor _extract;
    readonly Func<RunStats, bool> _isPersonalBest;
    readonly Func<bool>? _pbOnly;

    public event Action<ReplayInfo, string>? Saved;
    public event Action<string>? Error;

    /// <param name="anchor">Called once per run: QPC and the wall clock drift apart (sleep, clock sync), so a startup anchor misplaces the mouse window.</param>
    /// <param name="isPersonalBest">Whether the run beat every earlier run of its scenario; marks the folder name with PB.</param>
    /// <param name="pbOnly">Read per run: when true, only a personal best keeps its video clip.</param>
    public ReplaySaver(string replaysRoot, MouseRingBuffer buffer, Func<ClockAnchor> anchor, ClipExtractor extract, Func<RunStats, bool> isPersonalBest, Func<bool>? pbOnly = null)
    {
        _root = replaysRoot; _buffer = buffer; _anchor = anchor; _extract = extract; _isPersonalBest = isPersonalBest; _pbOnly = pbOnly;
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
        var pb = _isPersonalBest(run); // asked once: it also records the run as the new best
        var folder = Path.Combine(_root, ReplayFolder.Name(run.Start, run.Scenario, run.Score, pb));
        AccessGuard.Run(_root, true, () => Directory.CreateDirectory(folder));

        // one wall-clock -> QPC mapping for the whole save: the mouse trajectory and the video offset both hang off the same instant
        var anchor = _anchor();
        var startQpc = anchor.ToQpc(run.Start);
        var endQpc = anchor.ToQpc(run.End);

        ClipResult? clip = null;
        var skipVideo = _pbOnly?.Invoke() == true && !pb; // "save video for PB only": the clip of any other run is never cut
        if (!skipVideo)
        {
            try { clip = await _extract(startQpc, endQpc, Path.Combine(folder, ReplayFolder.VideoFile), progress ?? (_ => { })); }
            catch (Exception e) { Error?.Invoke($"Video clip failed: {e.Message}"); }
        }
        var clipEndQpc = clip == null ? 0 : clip.ClipStartQpc + (long)(clip.Duration.TotalSeconds * anchor.Frequency);
        var videoCovers = skipVideo || clip != null && clip.ClipStartQpc <= startQpc && clipEndQpc >= endQpc;

        var built = TrajectoryBuilder.Build(run, _buffer, anchor);
        if (built.Trajectory != null)
            await File.WriteAllBytesAsync(Path.Combine(folder, ReplayFolder.TrajectoryFile), TrajectorySerializer.Serialize(built.Trajectory));
        File.Copy(csvPath, Path.Combine(folder, Path.GetFileName(csvPath)), overwrite: true);
        await CopyPerfAsync(csvPath, folder);

        var info = new ReplayInfo
        {
            Scenario = run.Scenario, Start = run.Start, End = run.End,
            VideoOffsetSec = clip == null ? 0 : (startQpc - clip.ClipStartQpc) / (double)anchor.Frequency,
            SampleRateHz = 0, // the trajectory holds raw mouse movement, not fixed-rate samples
            Settings = run.Settings,
            Partial = built.Partial || !videoCovers,
            HasVideo = clip != null,
            DegreesAvailable = built.DegreesAvailable,
            Score = run.Score, Kills = run.Kills, HitCount = run.HitCount, MissCount = run.MissCount,
        };
        ReplayStore.WriteInfo(folder, info); // last: a folder with session.json is complete
        Saved?.Invoke(info, folder);
        return folder;
    }
}

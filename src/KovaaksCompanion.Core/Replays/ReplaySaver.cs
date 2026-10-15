using KovaaksCompanion.Core.Input;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;
using KovaaksCompanion.Core.Trajectory;
using KovaaksCompanion.Core.Video;

namespace KovaaksCompanion.Core.Replays;

/// <summary>Cuts one finished run into a replay (SES-001): the clip goes to the videos folder, the info, trajectory, perf and statistics into the <see cref="ReplayArchive"/>. Runs off the caller's thread (SES-004).</summary>
public sealed class ReplaySaver
{
    /// <param name="progress">0..1 of the clip work done.</param>
    public delegate Task<ClipResult?> ClipExtractor(long startQpc, long endQpc, string outputPath, Action<double> progress);

    readonly ReplayArchive _archive;
    readonly MouseRingBuffer _buffer;
    readonly Func<ClockAnchor> _anchor;
    readonly ClipExtractor _extract;
    readonly Func<RunStats, bool> _isPersonalBest;
    readonly Func<RunStats, (DateTime Since, Guid Id)> _session;
    readonly Func<bool>? _pbOnly;
    readonly Func<RunStats, string?>? _tier;

    public event Action<ReplayInfo>? Saved;
    public event Action<string>? Error;

    /// <param name="anchor">Called once per run: QPC and the wall clock drift apart (sleep, clock sync), so a startup anchor misplaces the mouse window.</param>
    /// <param name="isPersonalBest">Whether the run beat every earlier run of its scenario; marks the video file name with PB.</param>
    /// <param name="session">The play session a run belongs to: when it began and its id. The video goes in that session's folder.</param>
    /// <param name="pbOnly">Read per run: when true, only a personal best keeps its video clip.</param>
    /// <param name="tier">The tier name the run's score reaches, or null; goes into the video file name.</param>
    public ReplaySaver(ReplayArchive archive, MouseRingBuffer buffer, Func<ClockAnchor> anchor, ClipExtractor extract, Func<RunStats, bool> isPersonalBest, Func<RunStats, (DateTime Since, Guid Id)> session, Func<bool>? pbOnly = null, Func<RunStats, string?>? tier = null)
    {
        _archive = archive; _buffer = buffer; _anchor = anchor; _extract = extract; _isPersonalBest = isPersonalBest; _session = session; _pbOnly = pbOnly; _tier = tier;
    }

    /// <summary>Fire-and-forget save on a thread-pool thread; failures go to <see cref="Error"/>.</summary>
    public Task Enqueue(RunStats run, string csvPath, Action<double>? progress = null) => Task.Run(async () =>
    {
        try { await SaveAsync(run, csvPath, progress); }
        catch (Exception e) { Error?.Invoke($"Saving '{run.Scenario}' failed: {e.Message}"); }
    });

    /// <summary>The .perf is written a moment after the CSV; for a fresh CSV wait briefly, and skip it when it never shows up.</summary>
    static async Task<PerfData?> ReadPerfAsync(string csvPath)
    {
        var perf = PerfParser.PathFor(csvPath);
        var attempts = DateTime.Now - File.GetLastWriteTime(csvPath) < TimeSpan.FromSeconds(10) ? 20 : 1;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (File.Exists(perf)) return PerfParser.Parse(await File.ReadAllBytesAsync(perf));
            }
            catch (Exception e) when (e is IOException or PerfFormatException) { } // still being written
            if (i < attempts - 1) await Task.Delay(250);
        }
        return null;
    }

    public async Task<ReplayInfo> SaveAsync(RunStats run, string csvPath, Action<double>? progress = null)
    {
        var pb = _isPersonalBest(run); // asked once: it also records the run as the new best
        var (since, sessionId) = _session(run);
        var folder = ReplayArchive.SessionFolder(since, sessionId);
        var videoFile = _archive.NewVideoFile(folder, run.Start, run.Scenario, run.Score, pb, _tier?.Invoke(run));
        var videoPath = _archive.VideoPathOf(videoFile);
        _archive.EnsureVideoFolder(videoFile);

        // one wall-clock -> QPC mapping for the whole save: the mouse trajectory and the video offset both hang off the same instant
        var anchor = _anchor();
        var startQpc = anchor.ToQpc(run.Start);
        var endQpc = anchor.ToQpc(run.End);

        ClipResult? clip = null;
        var skipVideo = _pbOnly?.Invoke() == true && !pb; // "save video for PB only": the clip of any other run is never cut
        if (!skipVideo)
        {
            try { clip = await _extract(startQpc, endQpc, videoPath, progress ?? (_ => { })); }
            catch (Exception e) { Error?.Invoke($"Video clip failed: {e.Message}"); }
        }
        var clipEndQpc = clip == null ? 0 : clip.ClipStartQpc + (long)(clip.Duration.TotalSeconds * anchor.Frequency);
        var videoCovers = skipVideo || clip != null && clip.ClipStartQpc <= startQpc && clipEndQpc >= endQpc;

        if (clip == null && File.Exists(videoPath)) { try { File.Delete(videoPath); } catch (IOException) { } } // a failed cut leaves no half file behind

        var built = TrajectoryBuilder.Build(run, _buffer, anchor);
        var parts = new RepParts(built.Trajectory is { } t ? TrajectorySerializer.Serialize(t) : null, await ReadPerfAsync(csvPath), RepStats.From(run));

        var info = new ReplayInfo
        {
            Id = Guid.NewGuid(), VideoFile = clip == null ? null : videoFile,
            Scenario = run.Scenario, Start = run.Start, End = run.End,
            VideoOffsetSec = clip == null ? 0 : (startQpc - clip.ClipStartQpc) / (double)anchor.Frequency,
            SampleRateHz = 0, // the trajectory holds raw mouse movement, not fixed-rate samples
            Partial = built.Partial || !videoCovers,
            DegreesAvailable = built.DegreesAvailable,
            Score = run.Score,
        };
        _archive.Add(info, parts);
        Saved?.Invoke(info);
        return info;
    }
}

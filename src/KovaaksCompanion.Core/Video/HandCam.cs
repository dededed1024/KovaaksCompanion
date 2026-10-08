using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video;

/// <summary>Where and how large the hand-cam is composited into the game clip. Percentages: size of the video width; gaps from the right / top edge of the video.</summary>
public sealed record HandCamLayout(int SizePercent = 25, int RightPercent = 0, int TopPercent = 20);

/// <summary>One configured hand-cam: DirectShow name, its index among same-named devices, and its place in the video.</summary>
public sealed record HandCamSlot(string Device = "", int Number = 0, int Size = 25, int Right = 0, int Top = 20)
{
    public const int Max = 3;
    public HandCamLayout Layout => new(Size, Right, Top);
}

/// <summary>The configured cams as a list with value equality, so <see cref="AppSettings"/> still compares by content.</summary>
public sealed class HandCamSlots : List<HandCamSlot>, IEquatable<HandCamSlots>
{
    public bool Equals(HandCamSlots? other) => other != null && this.SequenceEqual(other);
    public override bool Equals(object? obj) => Equals(obj as HandCamSlots);
    public override int GetHashCode() => this.Aggregate(0, (h, s) => HashCode.Combine(h, s));
}

/// <summary>A DirectShow video device; <see cref="Number"/> tells apart devices that share a name (0 = first).</summary>
public sealed record HandCamDevice(string Name, int Number)
{
    public override string ToString() => Number == 0 ? Name : $"{Name} ({Number + 1})";
}

/// <summary>What to keep per run: the cam composited into the game video, game and cam as separate files, or all three.</summary>
public enum HandCamSave { Composite, Separate }

/// <summary>Reads a saved name; unknown ones (e.g. the removed "Both") become <see cref="HandCamSave.Composite"/> instead of failing the whole settings file.</summary>
public sealed class HandCamSaveConverter : System.Text.Json.Serialization.JsonConverter<HandCamSave>
{
    public override HandCamSave Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        => reader.TokenType == System.Text.Json.JsonTokenType.String && Enum.TryParse<HandCamSave>(reader.GetString(), out var v) ? v : HandCamSave.Composite;
    public override void Write(System.Text.Json.Utf8JsonWriter writer, HandCamSave value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>Hand-cam config read from the settings: empty <see cref="Device"/> = off.</summary>
public sealed record HandCamConfig(IReadOnlyList<HandCamSlot> Cams, HandCamSave Save)
{
    public static HandCamConfig From(AppSettings s)
        => new(s.HandCamEnabled ? s.HandCams.Where(c => c.Device.Length > 0).Take(HandCamSlot.Max).ToList() : [], s.HandCamSave);
}

/// <summary>DirectShow video device names via `ffmpeg -list_devices`.</summary>
public static class HandCamDeviceList
{
    static readonly Regex VideoLine = new("\"(?<n>.+)\" \\(video\\)", RegexOptions.Compiled);

    /// <summary>Video devices in the order ffmpeg lists them (alternative-name lines and audio devices ignored); repeated names get increasing numbers.</summary>
    public static List<HandCamDevice> Parse(string ffmpegStderr)
    {
        var devices = new List<HandCamDevice>();
        foreach (var line in ffmpegStderr.Split('\n'))
            if (VideoLine.Match(line) is { Success: true } m)
            {
                var name = m.Groups["n"].Value;
                devices.Add(new HandCamDevice(name, devices.Count(d => d.Name == name)));
            }
        return devices;
    }

    public static async Task<List<HandCamDevice>> ListAsync(string ffmpegPath)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, "-hide_banner -list_devices true -f dshow -i dummy")
            { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return Parse(await err);
        }
        catch (Exception e) { AppLog.Write("handcam", "device list failed: " + e.Message); return []; }
    }
}

/// <summary>Records the hand-cam (DirectShow) with the same wall-clock timestamps as the game capture into short rolling segments, so a clip can be cut and aligned to the game footage.</summary>
public sealed class HandCamRecorder : IAsyncDisposable
{
    public const int SegmentSeconds = 2;
    public const int Fps = 30;
    public const string SegmentFilePattern = "cam_%03d.mp4";

    readonly string _ffmpeg, _device, _dir, _encoder;
    readonly int _quality, _slots, _number, _targetWidth;
    readonly object _lock = new();
    readonly List<(string File, TimeSpan Start, TimeSpan End)> _raw = [];
    readonly List<SegmentInfo> _segments = [];
    DateTime _epochUtc;
    Process? _proc;
    Task? _read;

    /// <param name="index">Position in the cam list; names the buffer folder (cam0, cam1, ...).</param>
    public HandCamRecorder(string ffmpegPath, HandCamSlot slot, int index, string bufferDir, string encoder, int quality, int bufferMinutes, int targetWidth = 0)
    {
        (_ffmpeg, _device, _number, _dir, _encoder, _quality, _targetWidth) = (ffmpegPath, slot.Device, slot.Number, Path.Combine(bufferDir, "cam" + index), encoder, quality, targetWidth);
        _slots = Math.Max(3, bufferMinutes * 60 / SegmentSeconds) + 2;
    }

    public bool IsRunning => _proc is { HasExited: false };
    public string LastError { get; private set; } = "";
    public IReadOnlyList<SegmentInfo> Segments { get { lock (_lock) return _segments.ToList(); } }

    /// <summary>Tries 1280x720@30, then the device default. False (LastError set) when ffmpeg cannot open the device.</summary>
    public async Task<bool> StartAsync()
    {
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir, "cam_*.mp4")) { try { File.Delete(f); } catch { } }
        return await TryLaunchAsync(true) || await TryLaunchAsync(false);
    }

    async Task<bool> TryLaunchAsync(bool forceMode)
    {
        lock (_lock) { _raw.Clear(); _segments.Clear(); }
        _epochUtc = DateTime.UtcNow;
        var psi = new ProcessStartInfo(_ffmpeg)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in FfmpegCommand.BuildHandCamRecord(_device, _number, _encoder, _quality, _dir, (_epochUtc - DateTime.UnixEpoch).Ticks / 10, _slots, forceMode, _targetWidth)) psi.ArgumentList.Add(a);
        AppLog.Write("handcam", $"start: {string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        _proc = p;
        var err = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        _read = Task.Run(() => ReadSegmentList(p));
        await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(2500));
        if (!p.HasExited) return true;
        string log; lock (err) log = err.ToString();
        LastError = AppLog.Tail(log);
        AppLog.Write("handcam", $"exited early (forceMode={forceMode}): {LastError}");
        p.Dispose(); _proc = null;
        return false;
    }

    void ReadSegmentList(Process p)
    {
        string? line;
        var first = true;
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            if (first) { first = false; continue; } // first segment reports start 0 instead of its real pts (same as VideoRecorder)
            var parts = line.Split(',');
            if (parts.Length < 3 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var e)) continue;
            lock (_lock)
            {
                _raw.RemoveAll(r => r.File == parts[0]);
                _raw.Add((parts[0], TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e)));
                _segments.Clear();
                foreach (var r in _raw) _segments.Add(new SegmentInfo(Path.Combine(_dir, r.File), _epochUtc + r.Start, r.End - r.Start));
            }
        }
    }

    public async Task StopAsync()
    {
        var p = _proc;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                try { await p.StandardInput.WriteAsync("q"); await p.StandardInput.FlushAsync(); } catch { }
                await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(4000));
                if (!p.HasExited) { try { p.Kill(true); } catch { } await p.WaitForExitAsync(); }
            }
            if (_read != null) await Task.WhenAny(_read, Task.Delay(2000));
        }
        finally { p.Dispose(); _proc = null; }
    }

    /// <summary>
    /// Concatenates the cam segments covering [startUtc, endUtc] into <paramref name="outputPath"/>. Waits (up to maxWait) for the segment holding endUtc to close.
    /// Returns the wall-clock start of the result (first segment start) or null when nothing is buffered.
    /// </summary>
    public async Task<DateTime?> ExtractAsync(DateTime startUtc, DateTime endUtc, string outputPath, Func<IEnumerable<string>, Task> runFfmpeg, TimeSpan? maxWait = null)
    {
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(SegmentSeconds + 3));
        List<SegmentInfo> sel;
        while (true)
        {
            sel = SegmentSelector.Select(Segments, startUtc, endUtc, TimeSpan.Zero);
            if (sel.Count > 0 && sel[^1].EndUtc >= endUtc) break;
            if (!IsRunning || DateTime.UtcNow > deadline) break;
            await Task.Delay(100);
        }
        if (sel.Count == 0) return null;
        var list = outputPath + ".txt";
        await File.WriteAllTextAsync(list, FfmpegCommand.BuildConcatList(sel));
        try { await runFfmpeg(FfmpegCommand.BuildConcat(list, outputPath)); }
        finally { try { File.Delete(list); } catch { } }
        return sel[0].StartUtc;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.App;

/// <summary>Animated WebP writer: frames go to a temp folder as PNG, then the embedded libwebp img2webp.exe packs them (it keeps only the changed rectangle per frame).</summary>
internal sealed class WebpFrameEncoder : IDisposable
{
    readonly int _w, _h, _delayMs;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "KovaaksCompanion", "webp-" + Guid.NewGuid().ToString("N"));
    readonly List<string> _frames = [];

    public WebpFrameEncoder(int width, int height, int delayMs)
    {
        _w = width; _h = height; _delayMs = delayMs;
        Directory.CreateDirectory(_dir);
    }

    /// <param name="bgra">Premultiplied BGRA pixels, width * height * 4 bytes.</param>
    public void AddFrame(byte[] bgra)
    {
        var src = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Pbgra32, null, bgra, _w * 4);
        var enc = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0)));
        var path = Path.Combine(_dir, $"{_frames.Count:D4}.png");
        using (var fs = File.Create(path)) enc.Save(fs);
        _frames.Add(path);
    }

    /// <summary>Packs the frames into <paramref name="file"/>; false when the encoder is unavailable or fails.</summary>
    public async Task<bool> FinishAsync(string file)
    {
        var exe = EnsureExe();
        if (exe == null || _frames.Count == 0) return false;
        // Frame names are relative to the working folder: 234 absolute temp paths would overflow the command line limit.
        var args = new List<string> { "-loop", "0", "-d", _delayMs.ToString(), "-lossy", "-q", "90", "-m", "4", "-sharp_yuv" };
        args.AddRange(_frames.Select(Path.GetFileName)!);
        args.Add("-o"); args.Add($"\"{file}\"");
        var psi = new ProcessStartInfo(exe, string.Join(' ', args)) { WorkingDirectory = _dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi);
        if (p == null) return false;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) { AppLog.Write("webp", $"img2webp exit {p.ExitCode}: {await err}"); return false; }
        return File.Exists(file);
    }

    static string? EnsureExe()
    {
        using var src = typeof(WebpFrameEncoder).Assembly.GetManifestResourceStream("img2webp.exe");
        if (src == null) return null;
        var key = Convert.ToHexString(SHA256.HashData(src))[..12].ToLowerInvariant();
        foreach (var root in new[] { Path.Combine(AppSettings.DefaultDataFolder, "libwebp"), Path.Combine(Path.GetTempPath(), "KovaaksCompanion", "libwebp") })
        {
            try
            {
                var exe = Path.Combine(root, key, "img2webp.exe");
                if (File.Exists(exe)) return exe;
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                var tmp = exe + "." + Guid.NewGuid().ToString("N") + ".tmp";
                src.Position = 0;
                using (var o = File.Create(tmp)) src.CopyTo(o);
                try { File.Move(tmp, exe); }
                catch (IOException) when (File.Exists(exe)) { File.Delete(tmp); }
                return exe;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorReporter.Report($"img2webp extract to {root}", e, ErrorSeverity.High); }
        }
        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorReporter.Report("webp temp delete", e, ErrorSeverity.Low); }
    }
}

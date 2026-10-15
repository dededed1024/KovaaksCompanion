using System.Diagnostics;
using System.Security.Cryptography;

namespace KovaaksCompanion.Core.Update;

/// <summary>In-place update of the portable exe: download the release asset, check its SHA-256, swap it for the running exe (a running exe can be renamed but not overwritten) and start it; the new instance takes over from the old one.</summary>
public static class SelfUpdate
{
    const string OldSuffix = ".old", NewSuffix = ".new";

    /// <summary>Whether <paramref name="release"/> can be installed in place: exe asset with a digest on a GitHub HTTPS URL.</summary>
    public static bool CanInstall(ReleaseInfo release) =>
        release.Sha256.Length == 64 && IsTrustedUrl(release.AssetUrl);

    /// <summary>HTTPS on github.com (the release asset redirects to githubusercontent.com, which only the HTTP handler follows).</summary>
    public static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com";

    /// <summary>Downloads the asset next to <paramref name="exePath"/> as "&lt;exe&gt;.new" and returns that path; deletes it and throws when the hash differs.</summary>
    public static async Task<string> Download(HttpClient http, ReleaseInfo release, string exePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!CanInstall(release)) throw new InvalidOperationException("release has no verifiable exe asset");
        var path = exePath + NewSuffix;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl);
            req.Headers.UserAgent.ParseAdd("KovaaksCompanion");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            using var sha = SHA256.Create();
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
                sha.TransformFinalBlock([], 0, 0);
            }
            if (!string.Equals(Convert.ToHexString(sha.Hash!), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("downloaded update does not match its published SHA-256");
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Swaps <paramref name="newPath"/> in for <paramref name="exePath"/> (the old exe is kept as "&lt;exe&gt;.old") and starts it; puts the old exe back if anything fails.</summary>
    public static void Install(string exePath, string newPath)
    {
        var old = exePath + OldSuffix;
        TryDelete(old);
        File.Move(exePath, old);
        try
        {
            File.Move(newPath, exePath);
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath)! })?.Dispose();
        }
        catch
        {
            if (!File.Exists(exePath)) File.Move(old, exePath);
            TryDelete(newPath);
            throw;
        }
    }

    /// <summary>Removes leftovers of a previous update; call at startup.</summary>
    public static void Cleanup(string exePath)
    {
        TryDelete(exePath + OldSuffix);
        TryDelete(exePath + NewSuffix);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException e) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("update cleanup", e, KovaaksCompanion.Core.Diagnostics.ErrorSeverity.Low); } catch (UnauthorizedAccessException e) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("update cleanup", e, KovaaksCompanion.Core.Diagnostics.ErrorSeverity.Low); }
    }
}

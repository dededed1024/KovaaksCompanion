using System.Globalization;

namespace KovaaksCompanion.Core.Diagnostics;

/// <summary>Append-only diagnostic log in %LOCALAPPDATA%\KovaaksCompanion\logs\app.log. Never throws.</summary>
public static class AppLog
{
    const long MaxBytes = 5 * 1024 * 1024;
    static readonly object Lock = new();
    static string _path = DefaultPath;
    static bool _rotated;

    public static string DefaultPath => Path.Combine(Folder, "app.log");
    public static string Folder => Path.Combine(AppSettings.DefaultDataFolder, "logs");

    /// <summary>Log file location; settable for tests. Setting re-arms the once-per-process rotation check.</summary>
    public static string FilePath
    {
        get { lock (Lock) return _path; }
        set { lock (Lock) { _path = value; _rotated = false; } }
    }

    public static void Write(string tag, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{tag}] {message}{Environment.NewLine}";
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (!_rotated)
                {
                    _rotated = true;
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                        File.Move(_path, Path.Combine(Path.GetDirectoryName(_path)!, "app.old.log"), overwrite: true);
                }
                File.AppendAllText(_path, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Last <paramref name="n"/> non-empty lines of <paramref name="text"/>.</summary>
    public static string Tail(string text, int n = 50) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(n));
}

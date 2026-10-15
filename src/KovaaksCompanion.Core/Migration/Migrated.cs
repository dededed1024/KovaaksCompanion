namespace KovaaksCompanion.Core;

/// <summary>The rename every imported file or folder of an earlier version gets, so nothing is ever deleted.</summary>
internal static class Migrated
{
    public const string Suffix = ".migrated";

    /// <summary>Where an imported file or folder is moved to: its name plus <c>.migrated</c>, with a time stamp when that name is taken.</summary>
    public static string Name(string path)
    {
        var to = path + Suffix;
        return File.Exists(to) || Directory.Exists(to) ? $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}{Suffix}" : to;
    }
}

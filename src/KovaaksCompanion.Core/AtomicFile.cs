namespace KovaaksCompanion.Core;

public static class AtomicFile
{
    /// <summary>Atomic write (temp file + move): the file is either the old content or the complete new one.</summary>
    public static void Write(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}

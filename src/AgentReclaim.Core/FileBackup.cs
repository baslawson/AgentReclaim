namespace AgentReclaim.Core;

public static class FileBackup
{
    /// <summary>Copies <paramref name="path"/> to a new, never-overwritten backup next to it. Returns the backup path, or null if there was nothing to back up.</summary>
    public static string? Create(string path, string tag)
    {
        if (!File.Exists(path)) return null;
        var stem = $"{path}.bak-{tag}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var backup = stem;
        for (var i = 2; File.Exists(backup); i++) backup = $"{stem}-{i}";
        File.Copy(path, backup);
        return backup;
    }
}

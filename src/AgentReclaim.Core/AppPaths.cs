using System.Security.Cryptography;
using System.Text;

namespace AgentReclaim.Core;

public static class AppPaths
{
    /// <summary>AGENTRECLAIM_DATA_DIR, canonicalised so every spelling of one folder means one instance.</summary>
    private static readonly string? CustomDataDirectory = Canonical(
        Environment.GetEnvironmentVariable("AGENTRECLAIM_DATA_DIR") is { Length: > 0 } custom ? custom : null);

    /// <summary>%LOCALAPPDATA%\AgentReclaim, or AGENTRECLAIM_DATA_DIR (used to test in isolation).</summary>
    public static string DataDirectory { get; } = CustomDataDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentReclaim");

    public static string LineageFile => Path.Combine(DataDirectory, "lineage.json");
    public static string ActionLogFile => Path.Combine(DataDirectory, "actions.jsonl");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string HistoryFile => Path.Combine(DataDirectory, "history.json");

    /// <summary>
    /// Empty normally. With AGENTRECLAIM_DATA_DIR set, a suffix derived from it, so an isolated test
    /// instance gets its own single-instance lock and pipe and can run next to the real app.
    /// </summary>
    public static string InstanceSuffix { get; } = CustomDataDirectory is null
        ? ""
        : InstanceSuffixFor(CustomDataDirectory);

    internal static string? Canonical(string? directory) =>
        directory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    internal static string InstanceSuffixFor(string dataDirectory) =>
        "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(dataDirectory)!.ToUpperInvariant())))[..12];

    /// <summary>Per-user pipe the Claude Code hook uses to tell the running app a session ended.</summary>
    public static string PipeName => $"AgentReclaim-{Environment.UserName}{InstanceSuffix}";

    public static string SingleInstanceMutexName => $@"Local\AgentReclaim.SingleInstance{InstanceSuffix}";
}

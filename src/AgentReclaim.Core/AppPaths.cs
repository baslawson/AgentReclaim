namespace AgentReclaim.Core;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\AgentReclaim, or AGENTRECLAIM_DATA_DIR (used to test in isolation).</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("AGENTRECLAIM_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentReclaim");

    public static string LineageFile => Path.Combine(DataDirectory, "lineage.json");
    public static string ActionLogFile => Path.Combine(DataDirectory, "actions.jsonl");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Per-user pipe the Claude Code hook uses to tell the running app a session ended.</summary>
    public static string PipeName => $"AgentReclaim-{Environment.UserName}";
}

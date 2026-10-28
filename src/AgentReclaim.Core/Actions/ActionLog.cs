using System.Text.Json;

namespace AgentReclaim.Core.Actions;

/// <summary>
/// One line per action. Destructive actions write an "intent" line before acting and a "done" line after,
/// so even a crash mid-action leaves a record. The command line and directory are kept for relaunching.
/// </summary>
public sealed record ActionLogEntry(
    DateTime TimeUtc,
    string Action,
    string Phase,
    string Trigger,
    bool Success,
    string Message,
    int Pid,
    string Name,
    string? ExePath,
    string? CommandLine,
    string? WorkingDirectory,
    int ProcessCount,
    long FreedBytes,
    string Agent,
    string Attribution)
{
    public bool CanRelaunch => Phase == "done" && Success && Action == "stop-tree"
                               && (ExePath is not null || !string.IsNullOrWhiteSpace(CommandLine))
                               && !string.IsNullOrWhiteSpace(WorkingDirectory);
}

public sealed class ActionLog(string path)
{
    private readonly object _gate = new();

    public string Path { get; } = path;

    public bool TryWrite(ActionLogEntry entry, out string? error)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Most recent entries first. Unreadable lines are skipped.</summary>
    public IReadOnlyList<ActionLogEntry> ReadRecent(int max)
    {
        try
        {
            string[] lines;
            lock (_gate)
            {
                if (!File.Exists(Path)) return [];
                lines = File.ReadAllLines(Path);
            }

            var result = new List<ActionLogEntry>();
            for (var i = lines.Length - 1; i >= 0 && result.Count < max; i--)
            {
                try
                {
                    if (JsonSerializer.Deserialize<ActionLogEntry>(lines[i]) is { Phase: not null } entry) result.Add(entry);
                }
                catch (JsonException)
                {
                }
            }
            return result;
        }
        catch (IOException)
        {
            return [];
        }
    }
}

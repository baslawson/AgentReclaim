using System.Text;
using System.Text.Json;

namespace AgentReclaim.Core.Actions;

/// <summary>
/// Reads lines appended to the action log since the last call, so the app sees what another process
/// (the MCP server) did without restarting. Starts at the end of the file: older lines are history.
/// </summary>
public sealed class ActionLogTail
{
    private const int MaxReadBytes = 1 << 20;
    private readonly string _path;
    private long _offset;

    public ActionLogTail(string path)
    {
        _path = path;
        _offset = Length();
    }

    /// <summary>Entries in complete lines written since the last call, oldest first. A half-written last line waits for the next call.</summary>
    public IReadOnlyList<ActionLogEntry> ReadNew()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _offset) _offset = 0; // replaced or truncated: read it from the start
            if (stream.Length == _offset) return [];

            stream.Position = _offset;
            var buffer = new byte[Math.Min(stream.Length - _offset, MaxReadBytes)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var end = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (end < 0)
            {
                // One line longer than the read limit is junk; skip it rather than stall forever.
                if (read == MaxReadBytes) _offset += read;
                return [];
            }
            _offset += end + 1;

            var entries = new List<ActionLogEntry>();
            foreach (var line in Encoding.UTF8.GetString(buffer, 0, end + 1).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<ActionLogEntry>(line.TrimEnd('\r')) is { Phase: not null } entry) entries.Add(entry);
                }
                catch (JsonException)
                {
                }
            }
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // missing or locked: try again next time
        }
    }

    private long Length()
    {
        try
        {
            return new FileInfo(_path).Exists ? new FileInfo(_path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

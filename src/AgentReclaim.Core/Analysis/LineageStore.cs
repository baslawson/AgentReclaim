using System.Text.Json;
using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Analysis;

/// <param name="Guessed">Owner inferred from file paths rather than seen under the agent.</param>
public sealed record LineageEntry(AgentKind Kind, int OwnerPid, string OwnerName, bool Guessed = false, long OwnerStartTicks = 0);

/// <summary>
/// Remembers which agent session each process was seen under. Once the agent exits, Windows
/// no longer links its children back to it, so this record is how leftovers get attributed.
/// Persisted to disk so attribution survives restarts of this app.
/// </summary>
public sealed class LineageStore
{
    private readonly string? _path;
    private readonly Dictionary<ProcessKey, LineageEntry> _entries = new();
    private readonly bool _readOnly;
    private bool _dirty;

    /// <param name="readOnly">Read the file but never write it (for side processes such as the MCP server).</param>
    public LineageStore(string? path = null, bool readOnly = false)
    {
        _path = path;
        _readOnly = readOnly;
        Load();
    }

    public LineageEntry? Get(ProcessKey key) => _entries.GetValueOrDefault(key);

    public void Record(ProcessKey key, LineageEntry entry)
    {
        if (_entries.TryGetValue(key, out var existing) && existing == entry) return;
        _entries[key] = entry;
        _dirty = true;
    }

    public void RetainOnly(IReadOnlySet<ProcessKey> alive)
    {
        foreach (var key in _entries.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _entries.Remove(key);
            _dirty = true;
        }
    }

    public void SaveIfDirty()
    {
        if (!_dirty || _path is null || _readOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var rows = _entries.Select(e => new Row(e.Key.Pid, e.Key.StartTicks, e.Value.Kind, e.Value.OwnerPid, e.Value.OwnerName, e.Value.Guessed, e.Value.OwnerStartTicks));
            var temp = $"{_path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(rows));
            File.Move(temp, _path, overwrite: true);
            _dirty = false;
        }
        catch (IOException)
        {
            // Not fatal: we try again on the next save.
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(_path)) ?? [];
            foreach (var row in rows)
                _entries[new ProcessKey(row.Pid, row.StartTicks)] = new LineageEntry(row.Kind, row.OwnerPid, row.OwnerName, row.Guessed, row.OwnerStartTicks);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A corrupt file only costs us attribution history.
        }
    }

    private sealed record Row(int Pid, long StartTicks, AgentKind Kind, int OwnerPid, string OwnerName, bool Guessed = false, long OwnerStartTicks = 0);
}

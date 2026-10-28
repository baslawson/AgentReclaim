using System.Text.Json;
using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Analysis;

/// <param name="Guessed">Owner inferred from file paths rather than seen under the agent.</param>
/// <param name="Agents">Every agent it ran under, as "pid:startTicks;…" (a string so entries compare by value). More than the owner when agents are nested.</param>
public sealed record LineageEntry(AgentKind Kind, int OwnerPid, string OwnerName, bool Guessed = false, long OwnerStartTicks = 0,
    string? Agents = null)
{
    public IReadOnlySet<ProcessKey> AgentKeys => ParseAgents(Agents);

    /// <summary>Distinct and sorted, so the same set always gives the same string.</summary>
    public static string FormatAgents(IEnumerable<ProcessKey> agents) =>
        string.Join(';', agents.Distinct().OrderBy(a => a.StartTicks).ThenBy(a => a.Pid).Select(a => $"{a.Pid}:{a.StartTicks}"));

    public static IReadOnlySet<ProcessKey> ParseAgents(string? text)
    {
        var keys = new HashSet<ProcessKey>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (part.Split(':') is [var pid, var ticks] && int.TryParse(pid, out var p) && long.TryParse(ticks, out var t) && t != 0)
                keys.Add(new ProcessKey(p, t));
        return keys;
    }
}

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
            var rows = _entries.Select(e => new Row(e.Key.Pid, e.Key.StartTicks, e.Value.Kind, e.Value.OwnerPid, e.Value.OwnerName, e.Value.Guessed, e.Value.OwnerStartTicks, e.Value.Agents));
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

    /// <summary>
    /// Re-reads the file and adds what the app recorded since, keeping what this store saw itself. Lets a
    /// long-running read-only store (the MCP server) combine its own observations with the app's.
    /// </summary>
    public void MergeFromDisk() => Load(onlyMissing: true);

    private void Load(bool onlyMissing = false)
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(_path)) ?? [];
            foreach (var row in rows.Where(r => r is not null && r.OwnerName is not null))
            {
                var key = new ProcessKey(row.Pid, row.StartTicks);
                var theirs = new LineageEntry(row.Kind, row.OwnerPid, row.OwnerName, row.Guessed, row.OwnerStartTicks, row.Agents);
                if (!onlyMissing || !_entries.TryGetValue(key, out var mine) || (mine.Guessed && !theirs.Guessed))
                    _entries[key] = theirs;
                else if (!mine.Guessed && !theirs.Guessed)
                    // Both seen: keep this store's owner, but learn any agent the other side saw it under.
                    _entries[key] = mine with { Agents = LineageEntry.FormatAgents(mine.AgentKeys.Concat(theirs.AgentKeys)) };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A corrupt file only costs us attribution history.
        }
    }

    private sealed record Row(int Pid, long StartTicks, AgentKind Kind, int OwnerPid, string OwnerName, bool Guessed = false, long OwnerStartTicks = 0,
        string? Agents = null);
}

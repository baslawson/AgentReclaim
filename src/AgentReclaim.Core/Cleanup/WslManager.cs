using System.Text.RegularExpressions;
using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Cleanup;

public sealed record WslStatus(bool Installed, long VmMemoryBytes, IReadOnlyList<string> RunningDistros, string? AutoMemoryReclaim);

/// <summary>
/// WSL (and Docker Desktop, which runs inside it) keeps memory its Linux side has finished with.
/// Two remedies: shut WSL down now, or let it hand cached memory back automatically.
/// </summary>
public static partial class WslManager
{
    private static readonly Dictionary<string, string> Utf8 = new() { ["WSL_UTF8"] = "1" };

    /// <summary>Tests point this at a temp file so they never touch the real .wslconfig.</summary>
    public static string? ConfigPathOverride { get; set; }

    public static string ConfigPath => ConfigPathOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");

    public static WslStatus GetStatus(ProcessSnapshot snapshot)
    {
        // vmmem is protected, so the handle-based snapshot can't read it; the system-wide
        // process list (what Task Manager uses) can.
        var vmBytes = snapshot.Processes
            .Where(p => p.Name.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.WorkingSetBytes);
        if (vmBytes == 0)
        {
            foreach (var name in new[] { "vmmemWSL", "vmmem" })
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                using (process)
                    vmBytes += process.WorkingSet64;
        }

        var wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (!File.Exists(wsl)) return new WslStatus(false, vmBytes, [], null);

        var running = ToolRunner.Run(wsl, "--list --running --quiet", TimeSpan.FromSeconds(10), environment: Utf8);
        var distros = running.Success
            ? running.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return new WslStatus(true, vmBytes, distros, ReadAutoMemoryReclaim());
    }

    /// <summary>Stops every distro, Docker Desktop's included. Anything running inside them is killed.</summary>
    public static ToolResult Shutdown() =>
        ToolRunner.Run(Path.Combine(Environment.SystemDirectory, "wsl.exe"), "--shutdown", TimeSpan.FromSeconds(60), environment: Utf8);

    public static string? ReadAutoMemoryReclaim()
    {
        if (!File.Exists(ConfigPath)) return null;
        string? section = null;
        foreach (var raw in File.ReadAllLines(ConfigPath))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
            else if (string.Equals(section, "experimental", StringComparison.OrdinalIgnoreCase)
                     && KeyValue().Match(line) is { Success: true } m
                     && m.Groups[1].Value.Equals("autoMemoryReclaim", StringComparison.OrdinalIgnoreCase))
                return m.Groups[2].Value.Trim();
        }
        return null;
    }

    /// <summary>
    /// Sets [experimental] autoMemoryReclaim in .wslconfig, keeping everything else and a timestamped backup.
    /// "dropCache" (WSL's default) is the mode known to coexist with Docker. Takes effect after WSL restarts.
    /// </summary>
    public static string EnableAutoMemoryReclaim(string value = DefaultAutoMemoryReclaim)
    {
        var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath).ToList() : [];
        FileBackup.Create(ConfigPath, "agentreclaim");

        var start = lines.FindIndex(l => l.Trim().Equals("[experimental]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[experimental]");
            lines.Add($"autoMemoryReclaim={value}");
        }
        else
        {
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            var existing = lines.FindIndex(start + 1, end - start - 1,
                l => KeyValue().Match(l.Trim()) is { Success: true } m && m.Groups[1].Value.Equals("autoMemoryReclaim", StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) lines[existing] = $"autoMemoryReclaim={value}";
            else lines.Insert(start + 1, $"autoMemoryReclaim={value}");
        }

        File.WriteAllLines(ConfigPath, lines);
        return ConfigPath;
    }

    /// <summary>WSL's own default when .wslconfig doesn't set autoMemoryReclaim (and for unknown values).</summary>
    public const string DefaultAutoMemoryReclaim = "dropCache";

    /// <summary>
    /// Whether WSL reclaims memory automatically. Only an explicit "disabled" turns it off: a missing key
    /// or an unknown value means dropCache (see learn.microsoft.com/windows/wsl/wsl-config, [experimental]).
    /// </summary>
    public static bool IsAutoMemoryReclaimOn(string? configuredValue) =>
        !string.Equals(configuredValue?.Trim(), "disabled", StringComparison.OrdinalIgnoreCase);

    public static bool IsAutoMemoryReclaimOn() => IsAutoMemoryReclaimOn(ReadAutoMemoryReclaim());

    /// <summary>
    /// Turns automatic reclaim off by writing autoMemoryReclaim=disabled (removing the key would fall back to
    /// WSL's default, which is on). Keeps everything else and a timestamped backup. Returns false if it was
    /// already off. Takes effect after WSL restarts.
    /// </summary>
    public static bool DisableAutoMemoryReclaim()
    {
        if (!IsAutoMemoryReclaimOn()) return false;
        EnableAutoMemoryReclaim("disabled");
        return true;
    }

    [GeneratedRegex(@"^([A-Za-z0-9_.]+)\s*=\s*(.*?)\s*(?:#.*)?$")]
    private static partial Regex KeyValue();
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentReclaim.Core.Cleanup;

/// <summary>A running container and the compose project folder it was started from, if any.</summary>
public sealed record ContainerInfo(
    string Id,
    string Name,
    string Image,
    string State,
    DateTime? StartedAtUtc,
    string? ComposeProject,
    string? ComposeService,
    string? WorkingDirectory,
    IReadOnlyList<string> Ports)
{
    /// <summary>Memory use from docker stats, or 0 if unknown.</summary>
    public long MemoryBytes { get; init; }

    /// <summary>Started with --rm: stopping it makes Docker delete the container and its anonymous volumes.</summary>
    public bool AutoRemove { get; init; }

    public string ShortId => Id.Length > 12 ? Id[..12] : Id;
}

/// <summary>How a container relates to AI agents, by its compose folder.</summary>
public enum ContainerLink
{
    /// <summary>Not in any folder an agent worked in recently.</summary>
    None,

    /// <summary>An agent is working in that project right now.</summary>
    LiveSession,

    /// <summary>The project has leftovers: its agent has gone.</summary>
    Leftover,

    /// <summary>An agent worked in that project during the last day.</summary>
    RecentActivity,
}

public sealed record ContainerList(IReadOnlyList<ContainerInfo> Containers, string? Error);

/// <summary>
/// Lists running Docker containers (with their compose working folder, so they can be tied to a
/// project) and stops one by its full id. Never removes containers, images or volumes.
/// </summary>
public static partial class DockerContainers
{
    private const string WorkingDirLabel = "com.docker.compose.project.working_dir";
    private const string ProjectLabel = "com.docker.compose.project";
    private const string ServiceLabel = "com.docker.compose.service";

    public static ContainerList List(Func<string, string, TimeSpan, ToolResult>? run = null)
    {
        run ??= (file, args, timeout) => ToolRunner.Run(file, args, timeout);
        var docker = ToolRunner.Find("docker");
        if (docker is null) return new ContainerList([], "Docker isn't installed (no docker on PATH).");

        var ids = run(docker, "ps -q --no-trunc", TimeSpan.FromSeconds(15));
        if (!ids.Success) return new ContainerList([], FirstLine(ids.Output) ?? "Docker isn't running.");

        var list = ids.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsFullId).ToList();
        if (list.Count == 0) return new ContainerList([], null);

        var inspect = run(docker, "inspect " + string.Join(' ', list), TimeSpan.FromSeconds(20));
        // A container that stopped between the two calls makes inspect fail as a whole; its JSON still lists the rest.
        var containers = ParseInspect(inspect.Output);
        if (containers.Count == 0 && !inspect.Success)
            return new ContainerList([], FirstLine(inspect.Output) ?? "docker inspect failed.");
        var memory = ParseStats(run(docker, "stats --no-stream --no-trunc --format \"{{.ID}}|{{.MemUsage}}\"", TimeSpan.FromSeconds(20)).Output);
        return new ContainerList(containers.Where(c => c.State == "running")
            .Select(c => c with { MemoryBytes = memory.GetValueOrDefault(c.Id) }).ToList(), null);
    }

    /// <summary>Parses <c>docker inspect</c> output. Anything malformed is skipped.</summary>
    public static IReadOnlyList<ContainerInfo> ParseInspect(string json)
    {
        var start = json.IndexOf('[');
        if (start < 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(json[start..]);
            var result = new List<ContainerInfo>();
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var id = Str(c, "Id");
                if (id is null || !IsFullId(id)) continue;

                var config = c.TryGetProperty("Config", out var cfg) && cfg.ValueKind == JsonValueKind.Object ? cfg : default;
                var labels = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object
                    ? l.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                        .ToDictionary(p => p.Name, p => p.Value.GetString()!)
                    : new Dictionary<string, string>();
                var state = c.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

                DateTime? started = null;
                if (state.ValueKind == JsonValueKind.Object && Str(state, "StartedAt") is { } at
                    && DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                    && t.Year > 2000)
                    started = t;

                result.Add(new ContainerInfo(
                    id,
                    (Str(c, "Name") ?? id[..12]).TrimStart('/'),
                    config.ValueKind == JsonValueKind.Object ? Str(config, "Image") ?? "" : "",
                    state.ValueKind == JsonValueKind.Object ? Str(state, "Status") ?? "" : "",
                    started,
                    labels.GetValueOrDefault(ProjectLabel),
                    labels.GetValueOrDefault(ServiceLabel),
                    labels.GetValueOrDefault(WorkingDirLabel),
                    Ports(c)) { AutoRemove = c.TryGetProperty("HostConfig", out var host) && host.ValueKind == JsonValueKind.Object
                    && host.TryGetProperty("AutoRemove", out var rm) && rm.ValueKind == JsonValueKind.True });
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Stops (doesn't remove) the container with this exact full id.</summary>
    public static ToolResult Stop(string id)
    {
        if (!IsFullId(id)) return new ToolResult(false, -1, "Not a full container id.");
        var docker = ToolRunner.Find("docker");
        if (docker is null) return new ToolResult(false, -1, "Docker isn't installed.");
        return ToolRunner.Run(docker, $"stop --time 10 {id}", TimeSpan.FromSeconds(40));
    }

    /// <summary>
    /// Stops the container and records it in the action log: an intent line first, then the outcome.
    /// <paramref name="project"/> is the folder it was tied to, for the log.
    /// </summary>
    public static ToolResult StopLogged(ContainerInfo container, Actions.ActionLog log, string trigger, string? project,
        Func<string, ToolResult>? stop = null)
    {
        stop ??= Stop;
        Actions.ActionLogEntry Entry(string phase, bool success, string message) => new(
            DateTime.UtcNow, "docker-stop", phase, trigger, success, message, 0, container.Name, null,
            $"{container.Image} ({container.Id})", project ?? container.WorkingDirectory, 1,
            success && phase == "done" ? container.MemoryBytes : 0, "", "compose folder");

        if (!log.TryWrite(Entry("intent", true, $"About to stop container {container.Name}"), out var error))
            return new ToolResult(false, -1, $"Didn't stop {container.Name}: couldn't write the action log ({error}).");

        var result = stop(container.Id);
        var message = result.Success
            ? $"Stopped container {container.Name} ({container.Image}){(container.MemoryBytes > 0 ? $", about {container.MemoryBytes / (1024 * 1024)} MB" : "")}" +
              (container.AutoRemove ? "; Docker deleted it (it was started with --rm)." : ".")
            : $"Couldn't stop container {container.Name}: {FirstLine(result.Output) ?? "docker stop failed"}";
        log.TryWrite(Entry("done", result.Success, message), out _);
        return result with { Output = message };
    }

    /// <summary>Parses "id|123.4MiB / 15.5GiB" lines from docker stats into bytes per full id.</summary>
    public static IReadOnlyDictionary<string, long> ParseStats(string output)
    {
        var map = new Dictionary<string, long>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Split('|') is not [var id, var usage] || !IsFullId(id)) continue;
            if (ParseSize(usage.Split('/')[0].Trim()) is { } bytes) map[id] = bytes;
        }
        return map;
    }

    private static long? ParseSize(string text)
    {
        var match = Size().Match(text);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
        var unit = match.Groups[2].Value.ToUpperInvariant();
        double factor = unit switch
        {
            "B" => 1,
            "KIB" => 1024, "KB" => 1000,
            "MIB" => 1024d * 1024, "MB" => 1e6,
            "GIB" => 1024d * 1024 * 1024, "GB" => 1e9,
            "TIB" => 1024d * 1024 * 1024 * 1024, "TB" => 1e12,
            _ => 0,
        };
        return factor == 0 ? null : (long)(value * factor);
    }

    public static bool IsFullId(string id) => FullId().IsMatch(id);

    /// <summary>
    /// True if the container's compose folder is <paramref name="project"/> or inside it. Paths are compared as
    /// folders, so C:\src\app doesn't match C:\src\app2.
    /// </summary>
    public static bool IsInProject(ContainerInfo container, string project)
    {
        if (string.IsNullOrWhiteSpace(container.WorkingDirectory) || string.IsNullOrWhiteSpace(project)) return false;
        var dir = Normalize(container.WorkingDirectory);
        var root = Normalize(project);
        return dir.Equals(root, StringComparison.OrdinalIgnoreCase)
               || dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

        static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
    }

    /// <summary>
    /// The AI project a container belongs to: the deepest project folder containing its compose folder, preferring
    /// one with a live session, then one with leftovers, then one an agent worked in during the last day.
    /// </summary>
    public static (string? Project, ContainerLink Link) Relate(ContainerInfo container, IReadOnlyList<Analysis.ProjectSummary> live,
        IEnumerable<string> recent)
    {
        string? Deepest(IEnumerable<string?> paths) => paths
            .Where(p => p is not null && IsInProject(container, p))
            .OrderByDescending(p => p!.Length)
            .FirstOrDefault();

        if (Deepest(live.Where(p => p.Sessions.Count > 0).Select(p => p.Path)) is { } session) return (session, ContainerLink.LiveSession);
        if (Deepest(live.Where(p => p.Leftovers.Count > 0).Select(p => p.Path)) is { } leftover) return (leftover, ContainerLink.Leftover);
        if (Deepest(recent) is { } earlier) return (earlier, ContainerLink.RecentActivity);
        return (null, ContainerLink.None);
    }

    private static IReadOnlyList<string> Ports(JsonElement c)
    {
        var ports = new List<string>();
        if (c.TryGetProperty("NetworkSettings", out var net) && net.ValueKind == JsonValueKind.Object
            && net.TryGetProperty("Ports", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in map.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var binding in p.Value.EnumerateArray())
                    if (binding.ValueKind == JsonValueKind.Object && Str(binding, "HostPort") is { Length: > 0 } host)
                        ports.Add($"{host}→{p.Name}");
            }
        }
        return ports.Distinct().ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex FullId();

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z]+)$")]
    private static partial Regex Size();
}

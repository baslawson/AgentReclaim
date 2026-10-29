using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Cleanup;

namespace AgentReclaim.Core.Integration;

public enum HealthStatus
{
    Ok,
    Warning,
    Failed,
    NotInstalled,
}

/// <summary>One thing a CLI's health check reported as not OK.</summary>
public sealed record HealthProblem(string Name, HealthStatus Status, string Summary, string? Fix = null);

/// <summary>What one agent's health check found. <see cref="Problems"/> holds only what isn't OK.</summary>
public sealed record DoctorReport(AgentKind Agent, HealthStatus Status, string Summary, IReadOnlyList<HealthProblem> Problems,
    TimeSpan Took, DateTime RanAtUtc);

/// <summary>
/// Runs each installed agent CLI's own health check: `claude doctor`, `codex doctor --json`, and for Gemini CLI (which
/// has none) its version, `gemini mcp list` and whether its settings.json reads. One at a time, at below-normal
/// priority, with no window and an empty stdin. They check installs, sign-in, config and MCP servers, not what makes
/// the machine slow. Only summaries are kept, with anything that looks like a secret removed.
/// </summary>
public sealed class AgentDoctor
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly Func<string, string?> _find;
    private readonly Func<string, string, ToolResult> _run;
    private readonly string _home;

    /// <param name="find">Full path of a CLI by name, or null when it isn't installed.</param>
    /// <param name="run">Runs a CLI with arguments and returns its output.</param>
    /// <param name="home">The user's profile folder (where .gemini lives).</param>
    public AgentDoctor(Func<string, string?>? find = null, Func<string, string, ToolResult>? run = null, string? home = null)
    {
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _find = find ?? FindCli;
        _run = run ?? RunCli;
    }

    /// <summary>
    /// Processes a health check started (in this app), until they exit. Never treated as agent sessions: `claude doctor`
    /// runs claude.exe, which would otherwise show up as a session and then as a leftover.
    /// </summary>
    public static bool IsHealthCheckProcess(int pid) => Running.ContainsKey(pid);

    private static readonly ConcurrentDictionary<int, byte> Running = new();

    public IReadOnlyList<DoctorReport> RunAll() => [Claude(), Codex(), Gemini()];

    // Plain-text output is redacted whole, before it's split into fields: a secret's name and value stay together.
    // (Codex's JSON report is redacted by codex itself, and each field again afterwards.)
    private ToolResult RunRedacted(string exe, string arguments)
    {
        var result = _run(exe, arguments);
        return result with { Output = Redact(result.Output) };
    }

    public DoctorReport Claude() => Timed(AgentKind.ClaudeCode, "claude", exe => ParseClaude(RunRedacted(exe, "doctor")));

    public DoctorReport Codex() => Timed(AgentKind.Codex, "codex", exe => ParseCodex(_run(exe, "doctor --json")));

    public DoctorReport Gemini() => Timed(AgentKind.GeminiCli, "gemini", exe =>
    {
        var problems = new List<HealthProblem>();

        var version = RunRedacted(exe, "--version");
        var versionText = version.Success ? FirstLine(version.Output) : "";
        if (!version.Success)
            problems.Add(new HealthProblem("version", HealthStatus.Failed, $"gemini --version failed: {Tail(version.Output)}",
                "Reinstall Gemini CLI (npm install -g @google/gemini-cli)."));

        // Its lines carry each server's command and arguments, which can hold credentials: keep only the server's name.
        var mcp = RunRedacted(exe, "mcp list");
        if (!mcp.Success)
            problems.Add(new HealthProblem("mcp", HealthStatus.Warning, $"gemini mcp list failed (exit code {mcp.ExitCode}).",
                "Run gemini mcp list in a terminal to see why."));
        else
            foreach (var line in Lines(mcp.Output).Where(l => l.Contains("Disconnected", StringComparison.OrdinalIgnoreCase)))
                problems.Add(new HealthProblem("mcp", HealthStatus.Warning, $"MCP server {McpServerName(line)} is disconnected.",
                    "Check that server's command and settings (gemini mcp list shows them)."));

        var settings = Path.Combine(_home, ".gemini", "settings.json");
        if (SettingsError(settings) is { } error)
            problems.Add(new HealthProblem("settings", HealthStatus.Failed, $"{settings} doesn't read: {error}",
                "Fix the JSON in that file; Gemini CLI ignores or rejects a broken settings file."));

        var status = Worst(problems);
        var summary = status == HealthStatus.Ok
            ? $"Gemini CLI {versionText}: version, MCP servers and settings look fine."
            : $"Gemini CLI {versionText}: {Count(problems)}.";
        return (status, summary, problems);
    });

    private DoctorReport Timed(AgentKind agent, string cli,
        Func<string, (HealthStatus Status, string Summary, IReadOnlyList<HealthProblem> Problems)> check)
    {
        var started = DateTime.UtcNow;
        var exe = _find(cli);
        if (exe is null)
            return new DoctorReport(agent, HealthStatus.NotInstalled, $"{AgentSignatures.DisplayName(agent)} isn't installed (no {cli} on PATH).",
                [], TimeSpan.Zero, started);

        var (status, summary, problems) = check(exe);
        return new DoctorReport(agent, status, Redact(summary),
            problems.Select(p => p with { Name = Redact(p.Name), Summary = Redact(p.Summary), Fix = p.Fix is null ? null : Redact(p.Fix) }).ToList(),
            DateTime.UtcNow - started, started);
    }

    // ---- Claude Code ----

    private static readonly Regex ClaudeProblem = new(
        @"\b(failed|failure|error|errors|invalid|missing|not found|outdated|corrupt|broken|unreachable)\b|^\s*[⚠✗×✖]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// `claude doctor` prints plain text, e.g. "Running: native (2.1.312)" … "No installation issues found". A non-zero
    /// exit is a failure; lines that sound like a problem ("Last update attempt: failed (install_failed)") are warnings.
    /// </summary>
    internal static (HealthStatus, string, IReadOnlyList<HealthProblem>) ParseClaude(ToolResult result)
    {
        var lines = Lines(result.Output);
        var version = lines.Select(l => Regex.Match(l, @"^Running:\s*(.+)$")).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim();
        var name = version is null ? "Claude Code" : $"Claude Code {version}";

        if (!result.Success)
            return (HealthStatus.Failed, $"{name}: claude doctor failed: {Tail(result.Output)}",
                [new HealthProblem("doctor", HealthStatus.Failed, Tail(result.Output), "Run claude doctor in a terminal, or /doctor in a Claude Code session.")]);

        var problems = lines
            .Where(l => ClaudeProblem.IsMatch(l) && !l.StartsWith("For a full", StringComparison.OrdinalIgnoreCase))
            .Select(l =>
            {
                var colon = l.IndexOf(':');
                var named = colon > 0 && colon < 50;
                return new HealthProblem(named ? l[..colon].Trim(' ', '⚠', '✗', '×', '✖') : "doctor", HealthStatus.Warning,
                    (named ? l[(colon + 1)..] : l).Trim(' ', '⚠', '✗', '×', '✖'),
                    "For a full checkup that can also fix it, run /doctor in a Claude Code session.");
            })
            .ToList();

        if (problems.Count == 0)
            return (HealthStatus.Ok, $"{name}: no installation issues found.", problems);
        return (HealthStatus.Warning, $"{name}: {Count(problems)}.", problems);
    }

    // ---- Codex ----

    /// <summary>`codex doctor --json`: a redacted report with an overallStatus and one entry per check.</summary>
    internal static (HealthStatus, string, IReadOnlyList<HealthProblem>) ParseCodex(ToolResult result)
    {
        // Output is stdout followed by stderr: the report is the outermost {…}.
        var text = result.Output;
        var from = text.IndexOf('{');
        var to = text.LastIndexOf('}');
        JsonDocument document;
        try
        {
            if (from < 0 || to <= from) throw new JsonException("no report in the output");
            document = JsonDocument.Parse(text[from..(to + 1)]);
        }
        catch (JsonException)
        {
            return (HealthStatus.Failed, $"Codex: codex doctor gave no report: {Tail(text)}",
                [new HealthProblem("doctor", HealthStatus.Failed, Tail(text), "Run codex doctor in a terminal.")]);
        }

        using (document)
        {
            var root = document.RootElement;
            var version = Str(root, "codexVersion");
            var name = version is null ? "Codex" : $"Codex {version}";

            // Only a report of the known shape counts: an error envelope or a new format must not read as healthy.
            if (Str(root, "overallStatus") is null
                || !root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Object)
                return (HealthStatus.Failed, $"{name}: codex doctor's report isn't in a format AgentReclaim knows.",
                    [new HealthProblem("doctor", HealthStatus.Failed, $"Unrecognized report (exit code {result.ExitCode}).", "Run codex doctor in a terminal.")]);

            var problems = new List<HealthProblem>();
            var checkCount = 0;
            foreach (var check in checks.EnumerateObject())
            {
                checkCount++;
                var status = check.Value.ValueKind == JsonValueKind.Object ? CodexStatus(Str(check.Value, "status")) : HealthStatus.Warning;
                if (status == HealthStatus.Ok) continue;
                problems.Add(new HealthProblem(
                    check.Value.ValueKind == JsonValueKind.Object ? Str(check.Value, "id") ?? check.Name : check.Name, status,
                    check.Value.ValueKind == JsonValueKind.Object ? Str(check.Value, "summary") ?? "(no summary)" : "(unreadable check)",
                    check.Value.ValueKind == JsonValueKind.Object ? Str(check.Value, "remediation") : null));
            }

            // The overall status counts even if no single check explains it, and so does a failed run.
            var overall = Worst([.. problems.Select(p => p.Status), CodexStatus(Str(root, "overallStatus"))]);
            if (!result.Success && overall == HealthStatus.Ok)
            {
                overall = HealthStatus.Failed;
                problems.Add(new HealthProblem("doctor", HealthStatus.Failed,
                    $"codex doctor exited with code {result.ExitCode} but reported no problem.", "Run codex doctor in a terminal."));
            }
            problems = problems.OrderBy(p => p.Status == HealthStatus.Failed ? 0 : 1).ToList();
            var summary = overall == HealthStatus.Ok
                ? $"{name}: all {checkCount} checks passed."
                : problems.Count == 0
                    ? $"{name}: codex doctor reported \"{Str(root, "overallStatus")}\"."
                    : $"{name}: {Count(problems)} of {checkCount} checks.";
            return (overall, summary, problems);
        }
    }

    private static HealthStatus CodexStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "ok" or "pass" or "passed" or "success" or "skip" or "skipped" or "info" or "not_applicable" or "n/a" => HealthStatus.Ok,
        "fail" or "failed" or "error" or "critical" => HealthStatus.Failed,
        _ => HealthStatus.Warning, // "warning", a missing status, and anything new: shown rather than hidden
    };

    private static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ---- Gemini CLI ----

    /// <summary>Why the settings file can't be read as JSON (comments and trailing commas are allowed), or null if it can or doesn't exist.</summary>
    internal static string? SettingsError(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var _ = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return null;
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>"✗ files: npx files --token x (stdio) - Disconnected" → "files"; a line without that shape → "(unnamed)".</summary>
    internal static string McpServerName(string line)
    {
        var match = Regex.Match(line, @"^\s*[^\w\s]*\s*([\w.@/-]{1,64}):");
        return match.Success ? $"\"{match.Groups[1].Value}\"" : "(unnamed)";
    }

    // ---- Shared ----

    private static readonly Regex[] Secrets =
    [
        new(@"sk-ant-[A-Za-z0-9_\-]+", RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled),
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"github_pat_[A-Za-z0-9_]+", RegexOptions.Compiled),
        new(@"\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled),
        new(@"\bxox[abprs]-[A-Za-z0-9\-]+", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*", RegexOptions.Compiled), // JWTs
        new(@"(?i)\bbearer\s+[A-Za-z0-9._~+/\-]+=*", RegexOptions.Compiled),
    ];

    /// <summary>Values whose name says they're secret (group 1 is kept): "--api-key x", "token=x", "user:pass@" in a URL.</summary>
    private static readonly Regex[] NamedSecrets =
    [
        // "Authorization: Basic dXNlcjpwYXNz": the scheme and the credential after it.
        new(@"(?i)(""?(?:proxy-)?authorization""?\s*[=:]\s*)""?(?:(?:basic|bearer|digest|negotiate|ntlm|token)\s+)?[^\s"",;]+""?", RegexOptions.Compiled),
        new(@"(?i)(--?[\w-]*(?:key|token|secret|password|passwd|auth|credential)[\w-]*(?:=|\s+))(?!-)(?:""(?:[^""\\]|\\.)*""?|'[^']*'?|\S+)", RegexOptions.Compiled),
        new(@"(?i)(""?\b[\w-]*(?:key|token|secret|password|passwd|auth|credential)[\w-]*""?\s*[=:]\s*)(?:""(?:[^""\\]|\\.)*""?|'[^']*'?|[^\s,;&]+)", RegexOptions.Compiled),
        new(@"(://)[^/\s:@]+:[^/\s@]+(?=@)", RegexOptions.Compiled),
    ];

    /// <summary>Replaces anything that looks like an API key, token or password with "[hidden]".</summary>
    public static string Redact(string text)
    {
        text = Secrets.Aggregate(text, (t, pattern) => pattern.Replace(t, "[hidden]"));
        return NamedSecrets.Aggregate(text, (t, pattern) => pattern.Replace(t, "$1[hidden]"));
    }

    public static HealthStatus Worst(IEnumerable<HealthStatus> statuses) =>
        statuses.Where(s => s != HealthStatus.NotInstalled).DefaultIfEmpty(HealthStatus.Ok).Max();

    private static HealthStatus Worst(IEnumerable<HealthProblem> problems) => Worst(problems.Select(p => p.Status));

    private static string Count(IReadOnlyCollection<HealthProblem> problems)
    {
        var failed = problems.Count(p => p.Status == HealthStatus.Failed);
        var warnings = problems.Count - failed;
        var parts = new List<string>();
        if (failed > 0) parts.Add($"{failed} failed");
        if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
        return string.Join(", ", parts);
    }

    private static List<string> Lines(string text) =>
        text.Split('\n').Select(l => Regex.Replace(l, @"\x1B\[[0-9;]*[A-Za-z]", "").TrimEnd('\r', ' ')).Where(l => l.Trim().Length > 0).ToList();

    private static string FirstLine(string text) => Lines(text).FirstOrDefault()?.Trim() ?? "";

    /// <summary>The last few lines of output, for an error message.</summary>
    private static string Tail(string text)
    {
        var lines = Lines(text);
        var tail = string.Join(" ", lines.Skip(Math.Max(0, lines.Count - 3)).Select(l => l.Trim()));
        return tail.Length == 0 ? "(no output)" : tail.Length > 300 ? tail[..300] + "…" : tail;
    }

    /// <summary>
    /// A line for a notification or the log: "Claude Code OK · Codex 1 failed (sandbox.helpers) · Gemini CLI not installed".
    /// Built from <see cref="ForSaving"/>, so no CLI output reaches it.
    /// </summary>
    public static string OneLine(IEnumerable<DoctorReport> reports) => string.Join(" · ", reports.Select(ForSaving).Select(r =>
    {
        var name = AgentSignatures.DisplayName(r.Agent);
        return r.Status switch
        {
            HealthStatus.Ok => $"{name} OK",
            HealthStatus.NotInstalled => $"{name} not installed",
            _ when r.Problems.Count == 0 => $"{name} {(r.Status == HealthStatus.Failed ? "failed" : "has warnings")}",
            _ => $"{name} {Count(r.Problems)} ({string.Join(", ", r.Problems.Take(2).Select(p => p.Name))})",
        };
    }));

    /// <summary>
    /// The report as it's kept (health.json, the log) and handed to agents (MCP): no text the CLIs printed. Summaries are
    /// rebuilt from the counts, each problem keeps its status and fix (AgentReclaim's own advice, or Codex's remediation,
    /// redacted), and its name only if it's a plain label ("Last update attempt", "sandbox.helpers"): anything else could be
    /// a value, so it becomes "doctor". The full text is only ever shown live, on the Health tab.
    /// </summary>
    public static DoctorReport ForSaving(DoctorReport report)
    {
        var name = AgentSignatures.DisplayName(report.Agent);
        var problems = report.Problems
            .Select(p => new HealthProblem(SafeName(p.Name), p.Status, "", p.Fix is null ? null : Redact(p.Fix)))
            .ToList();
        var summary = report.Status switch
        {
            HealthStatus.NotInstalled => $"{name} isn't installed.",
            HealthStatus.Ok => $"{name}: no problems found.",
            _ when problems.Count == 0 => $"{name}: {(report.Status == HealthStatus.Failed ? "the check failed" : "warnings")}.",
            _ => $"{name}: {Count(problems)}.",
        };
        return report with { Summary = summary, Problems = problems };
    }

    /// <summary>Letters, spaces and . _ - only, at most 40 characters: a label, not a value.</summary>
    private static string SafeName(string name) =>
        Regex.IsMatch(name, @"^[A-Za-z][A-Za-z ._-]{0,39}$") ? name : "doctor";

    // ---- Running the CLIs ----

    private static readonly Dictionary<string, Func<string, string>[]> KnownLocations = new()
    {
        ["claude"] = [home => Path.Combine(home, ".local", "bin", "claude.exe")],
        ["codex"] = [_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe")],
        ["gemini"] = [_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "gemini.cmd")],
    };

    private string? FindCli(string name)
    {
        if (ToolRunner.Find(name) is { } onPath) return onPath;
        return KnownLocations.TryGetValue(name, out var places)
            ? places.Select(p => p(_home)).FirstOrDefault(File.Exists)
            : null;
    }

    private static ToolResult RunCli(string exe, string arguments)
    {
        // A neutral folder: claude doctor reads the settings of the folder it runs in.
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var pid = 0;
        try
        {
            return ToolRunner.Run(exe, arguments, Timeout, AppPaths.DataDirectory, lowPriority: true, closeInput: true,
                started: id => Running[pid = id] = 0);
        }
        finally
        {
            if (pid != 0) Running.TryRemove(pid, out _);
        }
    }

    // ---- Last report on disk ----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, IReadOnlyList<DoctorReport> reports)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(reports.Select(ForSaving), JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The last saved report (already <see cref="ForSaving"/>; a file from an older build is cleaned the same way).</summary>

    public static IReadOnlyList<DoctorReport> Load(string path)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<List<DoctorReport?>>(File.ReadAllText(path), JsonOptions) is { } reports
                && reports.All(IsComplete))
                return reports.Select(r => ForSaving(r!)).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
        }
        // No usable last report (missing, unreadable or incomplete): the Health tab just offers to run one.
        return [];
    }

    /// <summary>JSON can leave required fields null ([null], [{}]): such a file is treated as absent, never shown.</summary>
    private static bool IsComplete(DoctorReport? r) =>
        r is { Summary: not null, Problems: not null }
        && Enum.IsDefined(r.Agent) && Enum.IsDefined(r.Status)
        && r.Problems.All(p => p is { Name: not null, Summary: not null } && Enum.IsDefined(p.Status));
}

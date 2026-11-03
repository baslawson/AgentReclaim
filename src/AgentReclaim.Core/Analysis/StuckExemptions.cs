using System.Globalization;
using System.Text.RegularExpressions;
using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Analysis;

/// <summary>
/// Commands whose lack of local CPU says nothing about whether they're stuck. "No activity" only means something for
/// work done on this machine's CPU: a command waiting on a container, on another agent or on its own time limit looks
/// exactly the same as one waiting at a prompt nobody can see. These are never reported as waiting, so they get no
/// notice and are never stopped automatically.
/// </summary>
public static class StuckExemptions
{
    /// <summary>Clients whose work runs in a VM, cluster or service, reached in ways the TCP table doesn't show.</summary>
    private static readonly HashSet<string> RemoteWorkClients = new(StringComparer.OrdinalIgnoreCase)
    {
        "docker.exe", "docker-compose.exe", "podman.exe", "nerdctl.exe", "kubectl.exe", "wsl.exe", "gh.exe",
    };

    /// <summary>An agent's own long-lived helpers, started whenever it first needs them rather than with the agent.</summary>
    private static readonly HashSet<string> AgentHelpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_repl.exe", "codex-code-mode-host.exe",
    };

    // What starts a command name: the start of the text, a shell separator, a quote or a path.
    private const string Start = @"(?:^|[\s;&|'""(\\/])";

    private static readonly Regex RemoteWorkCommand = new(
        Start + @"(?:docker|docker-compose|podman|nerdctl|kubectl|wsl|gh)(?:\.exe)?\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AgentCommand = new(
        Start + @"(?:codex(?:\.exe)?\s+(?:exec|review)\b|(?:claude|gemini)(?:\.exe)?\s+(?:-p|--print|--prompt)\b)|codex-companion",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HookOrHelperCommand = new(
        @"CLAUDE_PLUGIN_ROOT|-hook\.(?:mjs|cjs|js|ts|py|sh|ps1)\b|[\\/]openai[\\/]codex[\\/]runtimes[\\/]|@oai[\\/]" +
        @"|(?:^|[\s\\/@_-])mcp(?:[-_]server)?(?:$|[\s.""'-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // GNU timeout: "timeout 3000 cmd", "timeout -k 5 90s cmd". Windows' own "timeout /t 30" has no bare number here.
    private static readonly Regex TimeLimit = new(
        Start + @"timeout(?:\.exe)?(?:\s+(?:-[ks]\s+\S+|-\S+))*\s+(\d+(?:\.\d+)?)([smhd]?)(?=[\s;&|'""]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Why this command shouldn't count as waiting (null: it can).</summary>
    /// <param name="tool">The command the agent started.</param>
    /// <param name="tree">The command and everything under it.</param>
    /// <param name="now">The current snapshot's time.</param>
    public static string? Reason(ProcessInfo tool, IReadOnlyList<ProcessNode> tree, DateTime now)
    {
        foreach (var node in tree)
        {
            var p = node.Process;
            if (AgentSignatures.MightBeAgent(p)) return "another agent is working under it";
            if (RemoteWorkClients.Contains(p.Name)) return $"{p.Name} does its work elsewhere";
            if (AgentHelpers.Contains(p.Name)) return "an agent's own helper";
            if (p.CommandLine is not { } command) continue;
            if (RemoteWorkCommand.IsMatch(command)) return "its work runs in a container or on another machine";
            if (AgentCommand.IsMatch(command)) return "it runs another agent";
            if (HookOrHelperCommand.IsMatch(command)) return "an agent's hook or helper";
        }

        var limit = tree.Select(n => TimeLimitOf(n.Process.CommandLine)).Max();
        return now - tool.StartTimeUtc < limit ? "it's within its own time limit" : null;
    }

    /// <summary>The longest time limit a command line gives itself with GNU timeout (zero: none).</summary>
    public static TimeSpan TimeLimitOf(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return TimeSpan.Zero;
        var longest = TimeSpan.Zero;
        foreach (Match match in TimeLimit.Matches(commandLine))
        {
            if (!double.TryParse(match.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) continue;
            var seconds = amount * match.Groups[2].Value.ToLowerInvariant() switch
            {
                "m" => 60,
                "h" => 3600,
                "d" => 86400,
                _ => 1,
            };
            // Far past anything real; TimeSpan.FromSeconds throws on overflow.
            var limit = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(365).TotalSeconds));
            if (limit > longest) longest = limit;
        }
        return longest;
    }
}

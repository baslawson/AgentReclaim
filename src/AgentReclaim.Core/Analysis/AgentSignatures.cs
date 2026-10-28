using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Analysis;

public enum AgentKind
{
    ClaudeCode,
    Codex,
    GeminiCli,
}

public static class AgentSignatures
{
    public static string DisplayName(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.Codex => "Codex",
        AgentKind.GeminiCli => "Gemini CLI",
        _ => kind.ToString(),
    };

    /// <summary>Is this process an AI agent itself (the root of a session)?</summary>
    public static AgentKind? AgentRootKind(ProcessInfo process)
    {
        var name = process.Name.ToLowerInvariant();
        if (name == "claude.exe") return AgentKind.ClaudeCode;
        if (name == "codex.exe") return AgentKind.Codex;
        if (name == "gemini.exe") return AgentKind.GeminiCli;

        // npm installs run the agent as a script under node. Only the script node runs counts: a path to an agent's
        // package elsewhere in the arguments (a watcher given it as a folder, say) doesn't make a process an agent.
        if (name == "node.exe" && process.CommandLine is { } cmd && NodeScript(cmd) is { } script)
        {
            if (InPackage(script, @"@anthropic-ai\claude-code\")) return AgentKind.ClaudeCode;
            if (InPackage(script, @"@openai\codex\") && script.Contains(@"@openai\codex\bin\")) return AgentKind.Codex;
            // Global installs and npx caches alike: …\node_modules\@google\gemini-cli\(dist|bundle)\…
            if (InPackage(script, @"@google\gemini-cli\")) return AgentKind.GeminiCli;
        }

        return null;
    }

    /// <summary>
    /// Might this be an agent? Broader than <see cref="AgentRootKind"/>: any agent package anywhere in a node
    /// command line. Used only to refuse stopping or capping, never to attribute.
    /// </summary>
    public static bool MightBeAgent(ProcessInfo process)
    {
        if (AgentRootKind(process) is not null) return true;
        if (!process.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase) || process.CommandLine is not { } cmd) return false;
        var text = Normalize(cmd);
        return text.Contains(@"@anthropic-ai\claude-code") || text.Contains(@"@openai\codex") || text.Contains(@"@google\gemini-cli");
    }

    /// <summary>
    /// Node options that may take their value as the next argument ("--title x" as well as "--title=x"). One missing
    /// here makes its value look like the script, so the agent goes unrecognized: <see cref="MightBeAgent"/> still
    /// protects it, and the MCP caller lookup stops there rather than guess past it.
    /// </summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-r", "--require", "--import", "--loader", "--experimental-loader", "-C", "--conditions", "--title",
        "--redirect-warnings", "--diagnostic-dir", "--env-file", "--env-file-if-exists", "--icu-data-dir", "--input-type",
        "--openssl-config", "--report-dir", "--report-directory", "--report-filename", "--report-signal",
        "--heapsnapshot-signal", "--heapsnapshot-near-heap-limit", "--secure-heap", "--secure-heap-min", "--tls-cipher-list",
        "--tls-keylog", "--use-largepages", "--experimental-default-type", "--watch-path", "--inspect-port", "--debug-port",
        "--cpu-prof-dir", "--cpu-prof-name", "--cpu-prof-interval", "--heap-prof-dir", "--heap-prof-name", "--heap-prof-interval",
        "--localstorage-file", "--disable-warning", "--trace-event-categories", "--trace-event-file-pattern",
        "--unhandled-rejections", "--dns-result-order", "--max-http-header-size", "--stack-trace-limit", "--run",
        "--experimental-config-file", "--test-reporter", "--test-reporter-destination", "--test-name-pattern",
        "--test-skip-pattern", "--test-concurrency", "--test-timeout", "--test-shard", "--network-family-autoselection-attempt-timeout",
    };

    /// <summary>The script a node command line runs (normalized), or null if it runs none (a REPL, -e …).</summary>
    internal static string? NodeScript(string commandLine)
    {
        var tokens = Tokenize(commandLine);
        for (var i = 1; i < tokens.Count; i++) // tokens[0] is node itself
        {
            var t = tokens[i];
            // Evaluates code instead (--eval=…, -pe and the like): any path after it is only an argument.
            if (t.StartsWith("--eval") || t.StartsWith("--print")
                || (t.Length > 1 && t[0] == '-' && t[1] != '-' && !t.Contains('=') && (t.Contains('e') || t.Contains('p'))))
                return null;
            if (ValueOptions.Contains(t))
            {
                i++; // takes the next token as its value
                continue;
            }
            if (t.StartsWith('-')) continue;
            return Normalize(t);
        }
        return null;
    }

    /// <summary>The script is the package's own (not a file in one of its dependencies).</summary>
    private static bool InPackage(string script, string package)
    {
        var at = script.IndexOf(@"node_modules\" + package, StringComparison.Ordinal);
        return at >= 0 && !script[(at + @"node_modules\".Length + package.Length)..].Contains(@"node_modules\");
    }

    /// <summary>Windows command-line splitting, enough for paths: quotes group, backslashes stay.</summary>
    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) tokens.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// Best guess at which agent a process belongs to, from its path and command line alone.
    /// Only used for processes whose parent has already exited, since lineage is unknown for them.
    /// </summary>
    public static AgentKind? GuessOwnerFromPaths(ProcessInfo process)
    {
        var text = Normalize($"{process.ExePath} {process.CommandLine}");
        if (text.Contains(@"\.claude\") || text.Contains(@"\temp\claude\") || text.Contains(@"@anthropic-ai\claude-code"))
            return AgentKind.ClaudeCode;
        if (text.Contains(@"\.codex\") || text.Contains(@"\openai\codex\") || text.Contains(@"@openai\codex"))
            return AgentKind.Codex;
        if (text.Contains(@"\.gemini\") || text.Contains(@"@google\gemini-cli"))
            return AgentKind.GeminiCli;
        return null;
    }

    private static string Normalize(string text) => text.Replace('/', '\\').ToLowerInvariant();
}

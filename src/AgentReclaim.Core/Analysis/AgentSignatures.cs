using AgentReclaim.Core.Model;

namespace AgentReclaim.Core.Analysis;

public enum AgentKind
{
    ClaudeCode,
    Codex,
}

public static class AgentSignatures
{
    public static string DisplayName(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.Codex => "Codex",
        _ => kind.ToString(),
    };

    /// <summary>Is this process an AI agent itself (the root of a session)?</summary>
    public static AgentKind? AgentRootKind(ProcessInfo process)
    {
        var name = process.Name.ToLowerInvariant();
        if (name == "claude.exe") return AgentKind.ClaudeCode;
        if (name == "codex.exe") return AgentKind.Codex;

        // npm installs run the agent as a script under node.
        if (name == "node.exe" && process.CommandLine is { } cmd)
        {
            var normalized = Normalize(cmd);
            if (normalized.Contains(@"@anthropic-ai\claude-code\")) return AgentKind.ClaudeCode;
            if (normalized.Contains(@"@openai\codex\bin\")) return AgentKind.Codex;
        }

        return null;
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
        return null;
    }

    private static string Normalize(string text) => text.Replace('/', '\\').ToLowerInvariant();
}

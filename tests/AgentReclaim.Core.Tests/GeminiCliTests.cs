using AgentReclaim.Core.Analysis;
using static AgentReclaim.Core.Tests.FakeProcesses;

namespace AgentReclaim.Core.Tests;

public class GeminiCliTests
{
    private const string GlobalInstall = @"""C:\Program Files\nodejs\node.exe"" C:\Users\me\AppData\Roaming\npm\node_modules\@google\gemini-cli\dist\index.js";
    private const string Npx = @"node C:/Users/me/AppData/Local/npm-cache/_npx/1a2b3c/node_modules/@google/gemini-cli/bundle/gemini.js --yolo";

    [Fact]
    public void Npm_installed_and_npx_gemini_are_agents()
    {
        Assert.Equal(AgentKind.GeminiCli, AgentSignatures.AgentRootKind(Proc(1, 0, "node.exe", GlobalInstall)));
        Assert.Equal(AgentKind.GeminiCli, AgentSignatures.AgentRootKind(Proc(2, 0, "node.exe", Npx)));
        Assert.Equal(AgentKind.GeminiCli, AgentSignatures.AgentRootKind(Proc(3, 0, "gemini.exe")));
        Assert.Equal("Gemini CLI", AgentSignatures.DisplayName(AgentKind.GeminiCli));
    }

    [Fact]
    public void Similar_looking_processes_are_not_gemini()
    {
        Assert.Null(AgentSignatures.AgentRootKind(Proc(1, 0, "node.exe", @"node C:\work\gemini-app\server.js")));
        Assert.Null(AgentSignatures.AgentRootKind(Proc(2, 0, "node.exe", @"node C:\npm\node_modules\@google\genai\index.js")));
        Assert.Null(AgentSignatures.AgentRootKind(Proc(3, 0, "bash.exe", GlobalInstall))); // only node runs the script
    }

    [Fact]
    public void Gemini_relaunching_itself_is_one_session_with_its_children()
    {
        // Gemini CLI restarts itself in a child node with more memory; its tools run under that child.
        var result = new Analyzer(new LineageStore()).Analyze(Snapshot(5,
            Proc(1, 0, "explorer.exe"),
            Proc(10, 1, "node.exe", GlobalInstall, startMinute: 1),
            Proc(11, 10, "node.exe", GlobalInstall, startMinute: 1),
            Proc(12, 11, "bash.exe", startMinute: 2),
            Proc(13, 12, "node.exe", "node vite", startMinute: 3, mb: 300)));

        var session = Assert.Single(result.Sessions);
        Assert.Equal(AgentKind.GeminiCli, session.Kind);
        Assert.Equal(10, session.Root.Process.Pid);
        Assert.Equal(4, session.Root.TotalCount);
    }

    [Fact]
    public void Children_left_behind_by_gemini_become_its_leftovers()
    {
        var analyzer = new Analyzer(new LineageStore());
        var bash = Proc(11, 10, "bash.exe", startMinute: 2);
        var vite = Proc(12, 11, "node.exe", "node vite", startMinute: 3, mb: 300);
        analyzer.Analyze(Snapshot(5, Proc(10, 1, "node.exe", Npx, startMinute: 1), bash, vite));

        var orphan = Assert.Single(analyzer.Analyze(Snapshot(10, bash, vite)).Orphans);
        Assert.Equal(AgentKind.GeminiCli, orphan.Kind);
        Assert.Equal(Attribution.Observed, orphan.Attribution);
    }

    [Fact]
    public void An_unknown_parent_is_guessed_from_gemini_paths()
    {
        Assert.Equal(AgentKind.GeminiCli, AgentSignatures.GuessOwnerFromPaths(Proc(1, 999, "python.exe", @"python C:\Users\me\.gemini\tmp\abc\script.py")));
        Assert.Equal(AgentKind.GeminiCli, AgentSignatures.GuessOwnerFromPaths(Proc(2, 999, "node.exe", Npx)));
        Assert.Null(AgentSignatures.GuessOwnerFromPaths(Proc(3, 999, "python.exe", @"python C:\work\gemini\script.py")));
    }
}

/// <summary>Only the script node runs makes it an agent, not an agent's path elsewhere on its command line.</summary>
public class NodeScriptMatchingTests
{
    [Theory]
    [InlineData(@"node C:\tools\supervisor.js --watch C:\npm\node_modules\@google\gemini-cli\dist")]
    [InlineData(@"node C:\npm\node_modules\@google\gemini-cli\node_modules\some-dep\bin\worker.js")]
    [InlineData(@"node -e ""require('C:/npm/node_modules/@anthropic-ai/claude-code/cli.js')""")]
    [InlineData(@"node C:\work\scan.js C:\npm\node_modules\@openai\codex\bin\codex.js")]
    [InlineData(@"node --eval=0 C:/x/node_modules/@google/gemini-cli/dist/index.js")]
    [InlineData(@"node --print=1 C:/x/node_modules/@google/gemini-cli/dist/index.js")]
    [InlineData(@"node -pe 1 C:/x/node_modules/@google/gemini-cli/dist/index.js")]
    [InlineData(@"node -e 0 C:/x/node_modules/@anthropic-ai/claude-code/cli.js")]
    public void An_agent_package_that_is_not_the_script_is_not_an_agent(string cmd)
    {
        var p = Proc(1, 0, "node.exe", cmd);
        Assert.Null(AgentSignatures.AgentRootKind(p));
        Assert.True(AgentSignatures.MightBeAgent(p)); // but never stopped or capped
    }

    [Theory]
    [InlineData(@"node --max-old-space-size=8192 C:\npm\node_modules\@google\gemini-cli\dist\index.js", AgentKind.GeminiCli)]
    [InlineData(@"node --require C:\x\preload.js ""C:\Program Files\npm\node_modules\@anthropic-ai\claude-code\cli.js""", AgentKind.ClaudeCode)]
    [InlineData(@"node C:/npm/node_modules/@openai/codex/bin/codex.js exec", AgentKind.Codex)]
    public void The_script_after_node_options_is_matched(string cmd, AgentKind kind) =>
        Assert.Equal(kind, AgentSignatures.AgentRootKind(Proc(1, 0, "node.exe", cmd)));

    [Fact]
    public void A_watcher_given_an_agent_path_is_not_a_session()
    {
        var watcher = Proc(10, 1, "node.exe", @"node C:\tools\supervisor.js --watch C:\npm\node_modules\@google\gemini-cli\dist", startMinute: 1);
        var child = Proc(11, 10, "node.exe", "node vite", startMinute: 2);
        var analyzer = new Analyzer(new LineageStore());
        var explorer = Proc(1, 0, "explorer.exe");
        Assert.Empty(analyzer.Analyze(Snapshot(5, explorer, watcher, child)).Sessions);
        Assert.Empty(analyzer.Analyze(Snapshot(10, explorer, child)).Orphans); // nor are its children leftovers
    }

    [Fact]
    public void An_eval_wrapper_naming_an_agent_is_not_a_session()
    {
        var explorer = Proc(1, 0, "explorer.exe");
        var wrapper = Proc(10, 1, "node.exe", "node --eval=0 C:/x/node_modules/@google/gemini-cli/dist/index.js", startMinute: 1);
        var child = Proc(11, 10, "node.exe", "node vite", startMinute: 2);
        var analyzer = new Analyzer(new LineageStore());
        Assert.Empty(analyzer.Analyze(Snapshot(5, explorer, wrapper, child)).Sessions);
        Assert.Empty(analyzer.Analyze(Snapshot(10, explorer, child)).Orphans);
    }
}

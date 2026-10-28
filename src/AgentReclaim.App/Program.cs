using System.IO;
using System.Text;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Updates;
using AgentReclaim.Core.Integration;

namespace AgentReclaim.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `AgentReclaim.exe hook session-end`: run by Claude Code's SessionEnd hook.
        if (args is ["hook", "session-end", ..])
            return SessionEndHook.Run(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));

        // `AgentReclaim.exe mcp`: read-only MCP server over stdio.
        if (args is ["mcp", ..])
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            new McpServer(stdin, stdout).Run();
            return 0;
        }

        // `AgentReclaim.exe.old update-supervise <exe> <new pid> <ready file> <version> <old app pid>`: rolls back a failed update.
        if (args is ["update-supervise", var exe, var pid, var readyFile, var version, var previousPid])
        {
            Log.Initialize();
            return UpdateInstaller.RunSupervisor(exe, int.Parse(pid), readyFile, version, TimeSpan.FromSeconds(90), int.Parse(previousPid));
        }

        // `AgentReclaim.exe uninstall-cleanup`: run by the uninstaller (build/installer.iss).
        if (args is ["uninstall-cleanup"])
        {
            Log.Initialize();
            return InstalledApp.RunUninstallCleanup(Environment.ProcessPath!);
        }

        // `AgentReclaim.exe send-ctrl-c <pids> <approved pids>`: helper for graceful stops (see ConsoleSignal).
        if (args is ["send-ctrl-c", var candidates, var approved])
            return ConsoleSignal.RunHelper(candidates, approved);

        ConsoleSignal.HelperPath = Environment.ProcessPath;
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

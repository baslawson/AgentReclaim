using System.Runtime.InteropServices;
using System.Text;
using AgentReclaim.Core;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Integration;
using AgentReclaim.Core.Watchdog;

// `agentreclaim-watchdog run`: watches until SIGTERM or SIGINT. `agentreclaim-watchdog mcp`: MCP server over stdio.
// Data (settings.json, watchdog.json, logs) is in ~/.local/share/AgentReclaim, or AGENTRECLAIM_DATA_DIR.
switch (args)
{
    case ["run"]:
    {
        Log.Initialize();
        using var stop = new CancellationTokenSource();
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true; // the service closes itself, saving what it knows
            stop.Cancel();
        }
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        return WatchdogHost.Run(AppPaths.SettingsFile, AppPaths.WatchdogFile, Console.Out, stop.Token);
    }
    case ["mcp"]:
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        new McpServer(stdin, stdout, analyze: () => throw new PlatformNotSupportedException("needs Windows"),
            onlyTools: WatchdogHost.McpTools).Run();
        return 0;
    }
    default:
        Console.Error.WriteLine("usage: agentreclaim-watchdog run | mcp");
        return 2;
}

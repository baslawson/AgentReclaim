using System.Runtime.CompilerServices;

namespace AgentReclaim.Core.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Before any test touches AppPaths: everything the tests write by default (the debug log included) goes to a
    /// throwaway folder, never the real %LOCALAPPDATA%\AgentReclaim of whoever runs them.
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateDataDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AgentReclaim.Tests", Environment.ProcessId.ToString());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("AGENTRECLAIM_DATA_DIR", dir);
    }
}

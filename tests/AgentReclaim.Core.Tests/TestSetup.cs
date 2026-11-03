using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AgentReclaim.Core.Tests;

internal static class TestSetup
{
    /// <summary>Runs a day old are done, even if their pid has since been reused.</summary>
    internal static readonly TimeSpan OldRun = TimeSpan.FromDays(1);

    /// <summary>
    /// Before any test touches AppPaths: everything the tests write (the debug log, and anything in the temp folder,
    /// from any test or the programs tests start) goes to a throwaway folder for this run, never the real
    /// %LOCALAPPDATA%\AgentReclaim or %TEMP% of whoever runs them. It's deleted when the run ends, and a run that was
    /// killed is cleaned up by the next one.
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentReclaim.Tests");
        SweepFinishedRuns(root, IsRunning, DateTime.UtcNow);

        var dir = Path.Combine(root, Environment.ProcessId.ToString());
        var temp = Path.Combine(dir, "temp");
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("AGENTRECLAIM_DATA_DIR", dir);
        Environment.SetEnvironmentVariable("TMP", temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteQuietly(dir);
    }

    /// <summary>Deletes what earlier runs left in <paramref name="root"/>: every folder, unless it belongs to a run still going.</summary>
    internal static void SweepFinishedRuns(string root, Func<int, bool> isRunning, DateTime nowUtc)
    {
        if (!Directory.Exists(root)) return;
        foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
        {
            var stillGoing = int.TryParse(folder.Name, out var pid) && isRunning(pid)
                             && nowUtc - folder.CreationTimeUtc < OldRun;
            if (!stillGoing) DeleteQuietly(folder.FullName);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Best effort: a file still in use is left for the next run. Clears read-only first, since git marks its objects so.</summary>
    private static void DeleteQuietly(string dir)
    {
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                if (file.IsReadOnly) file.IsReadOnly = false;
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Settings;

namespace AgentReclaim.Core.Watchdog;

/// <summary>
/// The watchdog with no app around it, for a Linux machine (`agentreclaim-watchdog run`, under systemd). It reads the
/// same settings.json "Watchdog" section and writes the same watchdog.json as the app. Hung commands aren't watched:
/// that takes the app's Windows process scan.
/// </summary>
public static class WatchdogHost
{
    /// <summary>All the MCP server offers there: the process tools need Windows.</summary>
    public static readonly IReadOnlySet<string> McpTools = new HashSet<string> { "watchdog_status" };

    /// <summary>
    /// Watches until <paramref name="stop"/> is cancelled. 1: another one is already running with this status file
    /// (two would fight over the port and the file).
    /// </summary>
    /// <param name="output">Problems and alerts meant for the user (the service's journal), one line each.</param>
    /// <param name="logLevel">Given settings.json's LogLevel each time the settings are read, so "Debug" applies without a
    /// restart. Null: the log level is left alone (tests, which share the static log).</param>
    public static int Run(string settingsFile, string statusFile, TextWriter output, CancellationToken stop, Action<LogLevel>? logLevel = null)
    {
        var dataFolder = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statusFile))!);
        // watchdog.json holds the socket's token. %LOCALAPPDATA% is private on Windows; on Linux the folder would be made
        // with the umask, often readable by everyone.
        if (!OperatingSystem.IsWindows()) dataFolder.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var settingsFolder = Path.GetDirectoryName(Path.GetFullPath(settingsFile))!;
        Directory.CreateDirectory(settingsFolder);
        FileStream held;
        try
        {
            held = new FileStream(statusFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Say(output, $"another watchdog is already running with {statusFile}");
            return 1;
        }

        using (held)
        {
            using var service = new WatchdogService(() =>
                {
                    var settings = AppSettings.Load(settingsFile);
                    logLevel?.Invoke(settings.LogLevel);
                    return settings;
                }, statusFile,
                alert => Say(output, alert.Text.ReplaceLineEndings(" | ")));
            // An edit applies at once, as in the app, rather than at the next round, which can be minutes away.
            using var watcher = new FileSystemWatcher(settingsFolder, Path.GetFileName(settingsFile))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => service.SettingsChanged();
            watcher.Created += (_, _) => service.SettingsChanged();
            watcher.Renamed += (_, _) => service.SettingsChanged(); // editors and AppSettings.Save write a temp file, then rename
            watcher.Deleted += (_, _) => service.SettingsChanged();
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch (IOException ex)
            {
                // Out of inotify instances or watches: only the speed-up is lost, since every round reads the settings.
                Say(output, $"settings changes apply at the next round, not at once: {ex.Message}");
            }
            service.Start();
            Say(output, $"watching, settings {settingsFile}");
            stop.WaitHandle.WaitOne();
            Say(output, "stopping");
        }
        return 0;
    }

    private static void Say(TextWriter output, string line)
    {
        lock (output) output.WriteLine(line);
    }
}

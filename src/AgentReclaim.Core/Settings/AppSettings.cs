using System.Text.Json;

namespace AgentReclaim.Core.Settings;

/// <summary>User settings. Every automatic behaviour is off until the user turns it on.</summary>
public sealed class AppSettings
{
    // Phase 2: automatic leftover cleanup
    public bool AutoStopLeftovers { get; set; }
    public int AutoStopIdleMinutes { get; set; } = 15;

    /// <summary>Leftovers of a session that the Claude Code hook reported as ended only need to be idle this long.</summary>
    public int HookGraceSeconds { get; set; } = 60;

    // Phase 3: throttling, leaks, cleanup
    public bool AutoThrottleBuilds { get; set; }
    public int ThrottleCpuPercent { get; set; } = 50;
    public int ThrottleAfterSeconds { get; set; } = 60;
    public bool LeakWarnings { get; set; } = true;
    public int CleanupMinAgeDays { get; set; } = 3;
    public List<string> ProjectRoots { get; set; } =
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")];

    // Phase 5
    public bool StartWithWindows { get; set; }

    /// <summary>One notification when memory stays above <see cref="LowMemoryAlertPercent"/> (a notice, not an action).</summary>
    public bool LowMemoryAlerts { get; set; } = true;
    public int LowMemoryAlertPercent { get; set; } = 90;

    /// <summary>One notification when CPU stays above <see cref="CpuAlertPercent"/> for <see cref="CpuAlertMinutes"/> (a notice, not an action).</summary>
    public bool CpuAlerts { get; set; }
    public int CpuAlertPercent { get; set; } = 90;
    public int CpuAlertMinutes { get; set; } = 3;

    /// <summary>
    /// Opt-in: when a memory or CPU alert fires, run the agents' health checks (claude doctor, codex doctor, Gemini
    /// checks) in the background and report what they found. Otherwise they only run from the Health tab.
    /// </summary>
    public bool HealthChecksOnSlowdown { get; set; }

    /// <summary>
    /// The MCP server's stop_my_process tool: an agent may stop processes in its own tree (and its own
    /// leftovers), nothing else. Off until the user turns it on.
    /// </summary>
    public bool AllowAgentSelfStop { get; set; }

    /// <summary>A notification when an agent leaves a process behind, e.g. "Claude Code left node.exe running on port 5173".</summary>
    public bool NotifyNewLeftovers { get; set; }

    /// <summary>A notification when a process in a session may be stuck (see <see cref="StuckWaitingMinutes"/>). It doesn't stop anything.</summary>
    public bool NotifyStuck { get; set; }

    /// <summary>A command an agent started counts as possibly stuck after this long with no activity.</summary>
    public int StuckWaitingMinutes { get; set; } = 10;

    /// <summary>Anything in a session counts as possibly stuck after keeping a whole CPU core busy this long.</summary>
    public int StuckSpinningMinutes { get; set; } = 15;

    /// <summary>Opt-in: stop a command that has shown no activity for <see cref="StuckWaitingMinutes"/>. The agent sees it fail.</summary>
    public bool AutoStopStuckWaiting { get; set; }

    /// <summary>Opt-in: stop a command that has kept a whole core busy for <see cref="StuckSpinningMinutes"/> (long builds do this too).</summary>
    public bool AutoStopStuckSpinning { get; set; }

    /// <summary>
    /// Opt-in: cap the memory each agent session's processes may use together (Windows job object). The
    /// agent itself is never capped. Reaching the cap makes a build or server fail with out-of-memory.
    /// </summary>
    public bool CapSessionMemory { get; set; }
    public int SessionMemoryCapGb { get; set; } = 8;

    /// <summary>
    /// Text that marks a command as not to be judged stuck: no notice, and never stopped by the stuck rules.
    /// Compared with each command line under it, ignoring case.
    /// </summary>
    public List<string> StuckExemptCommands { get; set; } = [];

    /// <summary>Opt-in: tell agents about stuck work and changes to repos and servers, and the user about servers and problems.</summary>
    public Watchdog.WatchdogSettings Watchdog { get; set; } = new();

    /// <summary>Why the file couldn't be read, when it couldn't and these are the defaults. Not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadError { get; private set; }

    /// <summary>The unreadable file has a watchdog section: someone may be setting it up by hand. Not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LoadErrorMentionsWatchdog { get; private set; }

    /// <summary>Keys that aren't settings, such as a misspelt one: kept on save rather than dropped.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    // Appearance
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    // Diagnostics and updates
    public Diagnostics.LogLevel LogLevel { get; set; } = Diagnostics.LogLevel.Info;
    public Updates.UpdateMode UpdateMode { get; set; } = Updates.UpdateMode.Notify;
    public DateTime? LastUpdateCheckUtc { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
                loaded.Watchdog ??= new(); // "Watchdog": null
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Fall back to defaults; the broken file is overwritten on the next save.
            KeepUnreadable(path);
            return new AppSettings { LoadError = ex.Message, LoadErrorMentionsWatchdog = Mentions(path, "watchdog") };
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The app has written a good file over the one it couldn't read.</summary>
    public void ForgetLoadError()
    {
        LoadError = null;
        LoadErrorMentionsWatchdog = false;
    }

    /// <summary>The next save writes the defaults over it, and what was in it may be a lot of hand-written settings.</summary>
    private static void KeepUnreadable(string path)
    {
        try
        {
            File.Copy(path, path + ".unreadable", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Mentions(string path, string word)
    {
        try
        {
            return File.ReadAllText(path).Contains(word, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A copy. Carries <see cref="LoadError"/>, which isn't saved, so whoever is handed the copy still knows.</summary>
    public AppSettings Clone()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
        copy.LoadError = LoadError;
        copy.LoadErrorMentionsWatchdog = LoadErrorMentionsWatchdog;
        return copy;
    }
}

/// <summary>Window theme. System follows the Windows "app mode" setting, live.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Fall back to defaults; the broken file is overwritten on the next save.
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

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
}

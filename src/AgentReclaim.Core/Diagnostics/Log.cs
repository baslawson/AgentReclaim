using System.Globalization;
using System.Text;

namespace AgentReclaim.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Plain-text debug log: one file per day in %LOCALAPPDATA%\AgentReclaim\logs, the last 7 days kept,
/// each capped at 10 MB. Local only; nothing is ever sent anywhere. Debug level includes command lines,
/// which can contain secrets passed as arguments, so check before sharing a debug log.
/// </summary>
public static class Log
{
    private const long MaxFileBytes = 10L << 20;
    private const int KeepDays = 7;
    private static readonly object Gate = new();
    private static string? _directory;
    private static string? _currentPath;
    private static bool _capped;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static string Directory => _directory ?? Path.Combine(AppPaths.DataDirectory, "logs");

    /// <summary>The log's clock; tests replace it to cross midnight.</summary>
    internal static Func<DateTime> Now { get; set; } = () => DateTime.Now;

    /// <summary>Points the log somewhere else (tests) and prunes old files.</summary>
    public static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            _directory = directory;
            _currentPath = null;
            Prune();
        }
    }

    /// <summary>Deletes logs older than the retention window. Runs at startup and at every daily rollover.</summary>
    private static void Prune()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var cutoff = Now().Date.AddDays(1 - KeepDays); // today plus the 6 days before
            foreach (var file in new DirectoryInfo(Directory).GetFiles("agentreclaim-*.log"))
            {
                // Judge by the (Gregorian) date in the name. Names that are odd or dated in the future, e.g.
                // written under another calendar, fall back to the write time.
                var day = DateTime.TryParseExact(file.Name["agentreclaim-".Length..^".log".Length], "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d <= Now().Date.AddDays(1)
                    ? d
                    : file.LastWriteTime;
                if (day < cutoff) file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warning(string message, Exception? ex = null) => Write(LogLevel.Warning, message, ex);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public static void Write(LogLevel level, string message, Exception? ex)
    {
        if (!IsEnabled(level)) return;

        var line = new StringBuilder()
            .Append(Now().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(7))
            .Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
            .Append(message);
        if (ex is not null) line.AppendLine().Append(ex);

        lock (Gate)
        {
            try
            {
                var path = Path.Combine(Directory, $"agentreclaim-{Now().ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log"); // Gregorian, whatever the culture
                if (path != _currentPath)
                {
                    Prune(); // first write or a new day; also creates the folder
                    _currentPath = path;
                    _capped = false;
                }
                if (_capped) return;
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                {
                    _capped = true;
                    File.AppendAllText(path, $"{Now().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} WARNING Log file reached 10 MB; further lines for today are dropped.{Environment.NewLine}");
                    return;
                }
                File.AppendAllText(path, line.AppendLine().ToString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never break the app.
            }
        }
    }

    /// <summary>Environment summary for bug reports (no command lines or paths beyond the app's own).</summary>
    public static string Diagnostics() => new StringBuilder()
        .AppendLine($"AgentReclaim {AppInfo.Version}")
        .AppendLine($"Windows {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit)")
        .AppendLine($".NET {Environment.Version}")
        .AppendLine($"Processors: {Environment.ProcessorCount}")
        .AppendLine($"Single-file build: {AppInfo.IsSingleFileBuild}")
        .AppendLine($"Log level: {MinimumLevel}")
        .ToString();
}

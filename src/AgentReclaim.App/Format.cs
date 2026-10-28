namespace AgentReclaim.App;

internal static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    public static string Percent(double value) => value < 0.05 ? "0%" : $"{value:0.0}%";

    public static string Duration(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "<1m",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)span.TotalHours}h {span.Minutes}m",
        _ => $"{(int)span.TotalDays}d {span.Hours}h",
    };
}

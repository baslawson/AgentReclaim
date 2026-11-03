using AgentReclaim.Core.Actions;

namespace AgentReclaim.Core.Analysis;

public enum MascotMood { Content, Spotted, Proud, Concerned }

/// <summary>How full a meter is, which sets its colour. Always shown with a word or number too, never colour alone.</summary>
public enum MeterState { Good, Warn, Danger }

/// <summary>The mascot's mood and voice: friendly about good news, plain whenever something needs attention.</summary>
public static class Personality
{
    public static MeterState Meter(double fraction) => fraction switch
    {
        >= 0.9 => MeterState.Danger,
        >= 0.7 => MeterState.Warn,
        _ => MeterState.Good,
    };

    public static string MemoryWord(double fraction) => Meter(fraction) switch
    {
        MeterState.Danger => "Running low",
        MeterState.Warn => "Getting full",
        _ => "Plenty free",
    };

    public static MascotMood Mood(double memoryLoad, int leftovers) =>
        Meter(memoryLoad) == MeterState.Danger ? MascotMood.Concerned
        : leftovers > 0 ? MascotMood.Spotted
        : MascotMood.Content;

    public static string Greeting(int hour) => hour switch
    {
        < 5 => "Still up?",
        < 12 => "Good morning!",
        < 18 => "Good afternoon!",
        _ => "Good evening!",
    };

    public static string StatusLine(double memoryLoad, int leftovers)
    {
        if (Meter(memoryLoad) == MeterState.Danger) return "Memory is running low.";
        return leftovers switch
        {
            0 => "Everything's tidy.",
            1 => "Spotted 1 leftover.",
            _ => $"Spotted {leftovers} leftovers.",
        };
    }

    /// <summary>
    /// Whether a stop is worth a little party. Not a command stopped because it looked stuck: that's a guess made inside a
    /// live session, and the agent sees it fail, so it gets the plain notice only.
    /// </summary>
    public static bool ShouldCelebrate(ActionLogEntry entry) =>
        MemoryHistory.IsFreed(entry) && entry.FreedBytes > 0
        && entry.Trigger?.Contains("stuck", StringComparison.OrdinalIgnoreCase) != true;
}

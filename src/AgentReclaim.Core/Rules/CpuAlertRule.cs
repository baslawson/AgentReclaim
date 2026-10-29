using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Settings;

namespace AgentReclaim.Core.Rules;

public sealed record CpuAlert(string Title, string Text);

/// <summary>
/// One notification when the whole machine's CPU stays above the threshold for a few minutes, naming the busiest
/// AI-related processes. Like <see cref="MemoryAlertRule"/>: once per episode (CPU has to drop clearly below the
/// threshold first), and never more than once per <see cref="Cooldown"/>.
/// </summary>
public sealed class CpuAlertRule
{
    private DateTime? _highSince;
    private bool _armed = true;
    private DateTime? _lastAlertUtc;

    /// <summary>How far (percentage points) CPU has to drop below the threshold to re-arm.</summary>
    public double RearmGap { get; init; } = 10;

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(30);

    public CpuAlert? Evaluate(AnalysisResult result, AppSettings settings, DateTime nowUtc)
    {
        if (!settings.CpuAlerts)
        {
            _highSince = null;
            return null;
        }

        var threshold = Math.Clamp(settings.CpuAlertPercent, 50, 100);
        var cpu = result.Snapshot.System.CpuPercent;
        if (cpu < threshold)
        {
            _highSince = null;
            if (cpu < threshold - RearmGap) _armed = true;
            return null;
        }

        _highSince ??= nowUtc;
        var minutes = Math.Clamp(settings.CpuAlertMinutes, 1, 60);
        if (nowUtc - _highSince.Value < TimeSpan.FromMinutes(minutes) || !_armed) return null;
        if (_lastAlertUtc is { } last && nowUtc - last < Cooldown) return null;

        var consumers = Consumers(result);
        var text = $"CPU has been above {threshold}% for {minutes} min (now {cpu:0}%).";
        text += consumers.Count == 0
            ? " Claude Code, Codex and Gemini CLI aren't using much of it."
            : " Busiest AI-related: " + string.Join(" · ", consumers.Select(c => $"{c.What} {c.Percent:0}%")) + ".";
        _armed = false;
        _lastAlertUtc = nowUtc;
        return new CpuAlert("The CPU is busy", text);
    }

    /// <summary>The busiest sessions and leftovers, at most four, ignoring anything under 5% of the machine.</summary>
    public static IReadOnlyList<(string What, double Percent)> Consumers(AnalysisResult result)
    {
        var list = new List<(string What, double Percent)>();
        foreach (var s in result.Sessions)
            list.Add(($"{AgentSignatures.DisplayName(s.Kind)} (pid {s.Root.Process.Pid})", s.Root.TotalCpuPercent));
        foreach (var o in result.Orphans)
            list.Add(($"leftover {o.Root.Process.Name} (pid {o.Root.Process.Pid})", o.Root.TotalCpuPercent));
        return list.Where(c => c.Percent >= 5).OrderByDescending(c => c.Percent).Take(4).ToList();
    }
}

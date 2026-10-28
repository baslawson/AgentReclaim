using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Settings;

namespace AgentReclaim.Core.Rules;

public sealed record MemoryAlert(string Title, string Text, IReadOnlyList<(string What, long Bytes)> Consumers);

/// <summary>
/// One notification when system memory stays above the threshold, naming the biggest AI-related users.
/// It fires once per episode: memory has to drop clearly below the threshold before it can fire again,
/// and never more than once per <see cref="Cooldown"/>.
/// </summary>
public sealed class MemoryAlertRule
{
    private int _highPolls;
    private bool _armed = true;
    private DateTime? _lastAlertUtc;

    /// <summary>Consecutive polls above the threshold before alerting, so a brief spike doesn't.</summary>
    public int SustainedPolls { get; init; } = 2;

    /// <summary>How far (0..1) memory has to drop below the threshold to re-arm.</summary>
    public double RearmGap { get; init; } = 0.05;

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(30);

    /// <param name="wslVmBytes">Only called when an alert is about to fire.</param>
    public MemoryAlert? Evaluate(AnalysisResult result, AppSettings settings, DateTime nowUtc, Func<long> wslVmBytes)
    {
        var system = result.Snapshot.System;
        if (!settings.LowMemoryAlerts || system.TotalMemoryBytes <= 0)
        {
            _highPolls = 0;
            return null;
        }

        var threshold = Math.Clamp(settings.LowMemoryAlertPercent, 50, 99) / 100.0;
        var load = system.MemoryLoad;
        if (load < threshold)
        {
            _highPolls = 0;
            if (load < threshold - RearmGap) _armed = true;
            return null;
        }

        if (++_highPolls < SustainedPolls || !_armed) return null;
        if (_lastAlertUtc is { } last && nowUtc - last < Cooldown) return null;

        // The VM size only adds detail: if it can't be read, still warn. State is committed only once the alert exists.
        long vmBytes;
        try
        {
            vmBytes = wslVmBytes();
        }
        catch (Exception)
        {
            vmBytes = 0;
        }

        var consumers = Consumers(result, vmBytes);
        var text = $"Memory is {load * 100:0}% full ({Size(system.UsedMemoryBytes)} of {Size(system.TotalMemoryBytes)}).";
        text += consumers.Count == 0
            ? " Claude Code, Codex and WSL aren't using much of it."
            : " Biggest AI-related users: " + string.Join(" · ", consumers.Select(c => $"{c.What} {Size(c.Bytes)}")) + ".";
        var alert = new MemoryAlert("Memory is running low", text, consumers);
        _armed = false;
        _lastAlertUtc = nowUtc;
        return alert;
    }

    /// <summary>The biggest AI-related consumers, at most four, ignoring anything under 50 MB.</summary>
    public static IReadOnlyList<(string What, long Bytes)> Consumers(AnalysisResult result, long wslVmBytes)
    {
        var list = new List<(string What, long Bytes)>();
        if (result.Orphans.Count > 0)
            list.Add(($"{result.Orphans.Count} leftover{(result.Orphans.Count == 1 ? "" : "s")}", result.OrphanPrivateBytes));
        if (wslVmBytes > 0)
            list.Add(("WSL/Docker VM", wslVmBytes));
        foreach (var s in result.Sessions)
            list.Add(($"{AgentSignatures.DisplayName(s.Kind)} (pid {s.Root.Process.Pid})", s.Root.TotalPrivateBytes));
        // Piles of idle children are part of a session's total, but often the part worth acting on.
        foreach (var f in result.Findings.Where(f => f.Kind == FindingKind.IdlePile))
            list.Add((f.Title, f.Bytes));

        return list.Where(c => c.Bytes >= 50L << 20).OrderByDescending(c => c.Bytes).Take(4).ToList();
    }

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20} MB";
}

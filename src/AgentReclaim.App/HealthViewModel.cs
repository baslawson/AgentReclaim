using System.Collections.ObjectModel;
using System.IO;
using AgentReclaim.Core;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Integration;

namespace AgentReclaim.App;

public sealed class HealthProblemRow(HealthProblem problem)
{
    // A saved report has no summaries (see AgentDoctor.ForSaving): just the name then.
    public string Text => $"{(problem.Status == HealthStatus.Failed ? "✗" : "⚠")} {problem.Name}" +
                          (problem.Summary.Length > 0 ? $": {problem.Summary}" : "");
    public string Fix => problem.Fix is null ? "" : $"Fix: {problem.Fix}";
    public bool HasFix => problem.Fix is not null;
}

public sealed class HealthReportRow(DoctorReport report)
{
    public string Agent => AgentSignatures.DisplayName(report.Agent);
    public string Status => report.Status switch
    {
        HealthStatus.Ok => "✓ OK",
        HealthStatus.Warning => "⚠ Warnings",
        HealthStatus.Failed => "✗ Problems",
        _ => "Not installed",
    };
    public string Summary => report.Summary;
    public string When => report.Status == HealthStatus.NotInstalled
        ? ""
        : $"Checked {report.RanAtUtc.ToLocalTime():g}, took {report.Took.TotalSeconds:0.0} s";
    public IReadOnlyList<HealthProblemRow> Problems { get; } = report.Problems.Select(p => new HealthProblemRow(p)).ToList();
    public bool HasProblems => Problems.Count > 0;
}

/// <summary>The Health tab: the agents' own health checks, run on request (or on a slowdown, if turned on).</summary>
public sealed class HealthViewModel : ObservableObject
{
    /// <summary>Automatic runs (on a slowdown) at most this often.</summary>
    public static readonly TimeSpan AutoRunCooldown = TimeSpan.FromMinutes(30);

    private bool _isBusy;
    private string _status = "Not run yet.";
    private string _slowdown = "";
    private DateTime? _lastAutoRunUtc;

    public HealthViewModel()
    {
        var last = AgentDoctor.Load(AppPaths.HealthFile);
        if (last.Count > 0) Show(last, "Last check");
    }

    public ObservableCollection<HealthReportRow> Reports { get; } = [];

    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Raise(); Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_isBusy;
    public string Status { get => _status; private set { _status = value; Raise(); } }

    /// <summary>The last slowdown alert, shown above the button so it can be acted on from there.</summary>
    public string Slowdown { get => _slowdown; set { _slowdown = value; Raise(); Raise(nameof(HasSlowdown)); } }
    public bool HasSlowdown => _slowdown.Length > 0;

    /// <summary>An automatic run may start now (none running, none in the last <see cref="AutoRunCooldown"/>).</summary>
    public bool TryStartAutoRun(DateTime nowUtc)
    {
        if (_isBusy || _lastAutoRunUtc is { } last && nowUtc - last < AutoRunCooldown) return false;
        _lastAutoRunUtc = nowUtc;
        return true;
    }

    /// <summary>Runs every check (UI thread in, UI thread out). Null if a run is already going.</summary>
    public async Task<IReadOnlyList<DoctorReport>?> RunAsync()
    {
        if (_isBusy) return null;
        IsBusy = true;
        Status = "Running the health checks, one agent at a time (up to a minute each)…";
        try
        {
            var reports = await Task.Run(() => new AgentDoctor().RunAll());
            try
            {
                AgentDoctor.Save(AppPaths.HealthFile, reports);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning("Health report not saved", ex);
            }
            Slowdown = "";
            Show(reports, "Checked");
            return reports;
        }
        catch (Exception ex)
        {
            Log.Error("Health checks failed", ex);
            Status = $"The health checks couldn't run: {ex.Message}";
            return [];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(IReadOnlyList<DoctorReport> reports, string label)
    {
        Reports.Clear();
        foreach (var report in reports.OrderBy(r => r.Status == HealthStatus.NotInstalled)) Reports.Add(new HealthReportRow(report));
        var at = reports.Select(r => r.RanAtUtc).DefaultIfEmpty(DateTime.UtcNow).Max().ToLocalTime();
        Status = $"{label} {at:g}: {AgentDoctor.OneLine(reports)}";
    }
}

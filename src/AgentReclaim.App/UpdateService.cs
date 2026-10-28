using System.Net.Http;
using AgentReclaim.Core;
using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Settings;
using AgentReclaim.Core.Updates;

namespace AgentReclaim.App;

/// <summary>Checks GitHub for a newer release once a day (unless off) and installs on request or automatically.</summary>
public sealed class UpdateService(Func<AppSettings> settings, Action<DateTime> recordCheck, Func<bool> userIsBusy) : IDisposable
{
    private readonly HttpClient _http = UpdateChecker.CreateHttpClient();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _busy = new(1, 1);

    public UpdateInfo? Available { get; private set; }
    public string Status { get; private set; } = $"You're on {AppInfo.Version}.";

    /// <summary>Status text changed or an update appeared. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>A newer version was found by the scheduled check (not a manual one). Background thread.</summary>
    public event Action<UpdateInfo>? Found;

    /// <summary>The new exe is in place and started; the app must exit now. Background thread.</summary>
    public event Action? RestartRequired;

    public void Start() => Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token); // don't slow down startup
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await ScheduledCheckAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error("Scheduled update check failed", ex);
                }
                await Task.Delay(TimeSpan.FromHours(1), _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

    private async Task ScheduledCheckAsync()
    {
        var s = settings();
        if (s.UpdateMode == UpdateMode.Off || (s.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last <= TimeSpan.FromHours(24))) return;

        var update = await CheckAsync(manual: false);
        if (update is null || settings().UpdateMode != UpdateMode.AutoInstall) return;
        if (UpdateInstaller.LastFailedVersion() == update.Version)
            Log.Info($"Update: not auto-installing {update.Version}; it was rolled back before. Install it manually to retry.");
        else if (userIsBusy())
            Log.Info("Update: auto-install postponed while the window is open.");
        else
            await InstallAsync(update);
    }

    /// <summary>Returns the newer release, or null if up to date (or the check failed; see Status).</summary>
    public async Task<UpdateInfo?> CheckAsync(bool manual)
    {
        if (!await _busy.WaitAsync(0)) return Available;
        try
        {
            SetStatus("Checking for updates…");
            var latest = await new UpdateChecker(_http).GetLatestAsync(_stop.Token);
            recordCheck(DateTime.UtcNow);

            if (latest is null || !UpdateChecker.IsNewer(latest, AppInfo.Version))
            {
                Available = null;
                SetStatus($"You're on {AppInfo.Version}, the latest version. Checked {DateTime.Now:g}.");
                Log.Info($"Update check: up to date ({AppInfo.Version}; latest {latest?.Version.ToString() ?? "none"}).");
                return null;
            }

            Available = latest;
            var note = UpdateInstaller.CannotInstallReason(latest) is { } reason ? $" {reason}" : "";
            SetStatus($"Version {latest.Version} is available (you're on {AppInfo.Version}).{note}");
            Log.Info($"Update check: {latest.Version} available.");
            if (!manual) Found?.Invoke(latest);
            return latest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            SetStatus($"Couldn't check for updates: {ex.Message}");
            Log.Warning("Update check failed", ex);
            return null;
        }
        finally
        {
            _busy.Release();
        }
    }

    public async Task<bool> InstallAsync(UpdateInfo update)
    {
        if (!await _busy.WaitAsync(0)) return false;
        try
        {
            SetStatus($"Downloading {update.Version}…");
            var exe = await UpdateInstaller.DownloadAndVerifyAsync(_http, update, _stop.Token);
            SetStatus($"Installing {update.Version}…");
            UpdateInstaller.ApplyAndRestart(exe, update.Version);
            RestartRequired?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Update failed: {ex.Message}");
            Log.Error($"Update to {update.Version} failed", ex);
            return false;
        }
        finally
        {
            _busy.Release();
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgentReclaim.Core.Diagnostics;

namespace AgentReclaim.Core.Updates;

/// <summary>
/// Downloads a release, checks it against the SHA-256 published next to it, and swaps the running exe:
/// the current exe is renamed to .old (Windows allows renaming a running exe), the new one takes its
/// place, and it is started with --wait-for-exit so it takes over once this process has exited.
///
/// The checksum catches corrupted or truncated downloads. It comes from the same GitHub release, so it
/// does not protect against a compromised GitHub account; code signing would.
/// </summary>
public static partial class UpdateInstaller
{
    public static string StagingRoot => Path.Combine(AppPaths.DataDirectory, "updates");

    /// <summary>Why this copy can't update itself, or null if it can.</summary>
    public static string? CannotInstallReason(UpdateInfo update)
    {
        if (!AppInfo.IsSingleFileBuild) return "This is a development build; download the release instead.";
        if (update.ZipUrl is null || update.ChecksumUrl is null) return "The release has no installable package with a checksum.";
        if (Environment.ProcessPath is not { } exe) return "Can't tell where AgentReclaim is installed.";
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exe)!, $".agentreclaim-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"No write access to {Path.GetDirectoryName(exe)}; download the release instead.";
        }
        return null;
    }

    /// <summary>Downloads and verifies the update. Returns the path of the verified new exe.</summary>
    public static async Task<string> DownloadAndVerifyAsync(HttpClient http, UpdateInfo update, CancellationToken cancel = default)
    {
        if (CannotInstallReason(update) is { } reason) throw new InvalidOperationException(reason);

        var dir = Path.Combine(StagingRoot, update.Version.ToString());
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var zipPath = Path.Combine(dir, update.ZipName!);
        Log.Info($"Update: downloading {update.ZipUrl}");
        await using (var source = await http.GetStreamAsync(update.ZipUrl!, cancel))
        await using (var target = File.Create(zipPath))
            await source.CopyToAsync(target, cancel);

        var expected = ParseChecksum(await http.GetStringAsync(update.ChecksumUrl!, cancel))
                       ?? throw new InvalidDataException("The checksum file is not a SHA-256 hash.");
        string actual;
        await using (var zip = File.OpenRead(zipPath))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(zip, cancel));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Checksum mismatch: expected {expected}, got {actual}. The download was not installed.");

        var newExe = Path.Combine(dir, "AgentReclaim.exe");
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("AgentReclaim.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("The package has no AgentReclaim.exe.");
            entry.ExtractToFile(newExe, overwrite: true);
        }

        var packaged = AppInfo.ParseVersion(FileVersionInfo.GetVersionInfo(newExe).ProductVersion);
        if (packaged != update.Version)
            throw new InvalidDataException($"The package contains version {packaged}, not {update.Version}.");

        Log.Info($"Update: {update.Version} downloaded and verified (SHA-256 {actual}).");
        return newExe;
    }

    /// <summary>A version whose install was rolled back; automatic installs skip it (manual ones still can).</summary>
    public static string FailedVersionFile => Path.Combine(AppPaths.DataDirectory, "update-failed.txt");

    public static Version? LastFailedVersion() =>
        File.Exists(FailedVersionFile) ? AppInfo.ParseVersion(File.ReadAllText(FailedVersionFile)) : null;

    /// <summary>
    /// Swaps in the new exe, starts it, and starts a supervisor (the previous exe, now .old) that rolls
    /// back if the new version doesn't report ready in time. The caller must exit right after this returns.
    /// </summary>
    public static void ApplyAndRestart(string verifiedExe, Version version)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path.");
        if (!AppInfo.IsSingleFileBuild) throw new InvalidOperationException("Refusing to replace a host that isn't AgentReclaim's own exe.");
        var old = current + ".old";

        if (File.Exists(old)) File.Delete(old);
        File.Move(current, old);
        Process started;
        var readyFile = Path.Combine(AppPaths.DataDirectory, $"update-ready-{Guid.NewGuid():N}.flag");
        try
        {
            File.Copy(verifiedExe, current);

            var start = new ProcessStartInfo(current) { UseShellExecute = false };
            foreach (var arg in new[] { "--tray", "--wait-for-exit", Environment.ProcessId.ToString(), "--ready-file", readyFile })
                start.ArgumentList.Add(arg);
            started = Process.Start(start) ?? throw new InvalidOperationException("The new version didn't start.");
        }
        catch
        {
            Restore(current, old);
            throw;
        }

        var supervisor = new ProcessStartInfo(old) { UseShellExecute = false };
        foreach (var arg in new[] { "update-supervise", current, started.Id.ToString(), readyFile, version.ToString(), Environment.ProcessId.ToString() })
            supervisor.ArgumentList.Add(arg);
        try
        {
            if (Process.Start(supervisor) is null) throw new InvalidOperationException("The rollback supervisor didn't start.");
        }
        catch (Exception ex)
        {
            // No safety net, no update. The new instance is still waiting for this one to exit, so it hasn't done anything yet.
            Log.Error("Update: couldn't start the rollback supervisor; cancelling the update", ex);
            try
            {
                started.Kill();
                started.WaitForExit(10_000);
            }
            catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            Restore(current, old);
            throw new InvalidOperationException("Couldn't start the rollback supervisor, so the update was cancelled.", ex);
        }

        Log.Info($"Update: installed {version} at {current}; handing over to pid {started.Id}.");
    }

    private static void Restore(string current, string old)
    {
        if (File.Exists(current)) File.Delete(current);
        File.Move(old, current);
        Log.Warning($"Update: restored the previous version at {current}.");
    }

    /// <summary>
    /// `AgentReclaim.exe.old update-supervise ...`: waits for the new version to report ready. If it exits
    /// or stays silent past the timeout, stops it, copies this (previous) exe back and starts it again.
    /// </summary>
    public static int RunSupervisor(string exe, int newPid, string readyFile, string version, TimeSpan timeout, int previousPid = 0)
    {
        Process? newProcess = null;
        try
        {
            newProcess = Process.GetProcessById(newPid);
        }
        catch (ArgumentException)
        {
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(readyFile))
            {
                TryDelete(readyFile);
                Log.Info($"Update: {version} started and reported ready.");
                return 0;
            }
            if (newProcess is null || newProcess.HasExited) break;
            Thread.Sleep(500);
        }

        Log.Error($"Update: {version} {(newProcess is null || newProcess.HasExited ? "exited" : "didn't report ready")}; rolling back.");
        try
        {
            if (newProcess is { HasExited: false })
            {
                newProcess.Kill(entireProcessTree: true); // the instance we launched moments ago
                newProcess.WaitForExit(10_000);
            }
            File.Copy(Environment.ProcessPath!, exe, overwrite: true);
            try
            {
                File.WriteAllText(FailedVersionFile, version); // bookkeeping only; never let it block the restart
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning("Update: couldn't record the failed version", ex);
            }
            var relaunch = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in new[] { "--tray", "--wait-for-exit", previousPid.ToString() })
                relaunch.ArgumentList.Add(arg); // so it doesn't collide with the old instance still shutting down
            Process.Start(relaunch);
            Log.Info($"Update: rolled back; previous version restarted from {exe}.");
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error($"Update: rollback failed. Reinstall from {AppInfo.ReleasesUrl}", ex);
            return 2;
        }
        finally
        {
            TryDelete(readyFile);
        }
    }

    /// <summary>The new version calls this once it is up, so the supervisor stands down.</summary>
    public static void ReportReady(string readyFile)
    {
        try
        {
            File.WriteAllText(readyFile, AppInfo.Version.ToString());
            if (File.Exists(FailedVersionFile) && LastFailedVersion() == AppInfo.Version) File.Delete(FailedVersionFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Update: couldn't report ready", ex);
        }
    }

    /// <summary>Called at startup: removes the previous exe (once its supervisor has exited) and downloaded packages.</summary>
    public static void CleanUpAfterUpdate()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
            if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Update cleanup deferred: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string? ParseChecksum(string text) => Sha256Hex().Match(text) is { Success: true } m ? m.Value : null;

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b")]
    private static partial Regex Sha256Hex();
}

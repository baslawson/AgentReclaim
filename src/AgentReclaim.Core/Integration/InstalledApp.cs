using System.Diagnostics;
using AgentReclaim.Core.Diagnostics;
using Microsoft.Win32;

namespace AgentReclaim.Core.Integration;

/// <summary>
/// Ties the app to its per-user installer (build/installer.iss): uninstall cleanup and keeping the
/// "Installed apps" version in step with self-updates. A portable copy is never affected.
/// </summary>
public static class InstalledApp
{
    /// <summary>The installer's AppId; Inno Setup registers the uninstaller under "{AppId}_is1".</summary>
    public const string AppId = "{C651E385-0118-4F7B-B868-0CF9F11D502B}";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";

    /// <summary>
    /// `AgentReclaim.exe uninstall-cleanup`, run by the uninstaller before it deletes files: stops this
    /// copy if it's running, and removes start-with-Windows and the Claude Code hook if they point at it.
    /// </summary>
    public static int RunUninstallCleanup(string exePath)
    {
        // An update's rollback supervisor (running from AgentReclaim.exe.old) goes first: if the app were
        // stopped while it watches, it would "roll back" and start the app again mid-uninstall.
        StopInstances(exePath + ".old");
        StopInstances(exePath);

        try
        {
            if (AutoStart.DisableIfFor(exePath)) Log.Info("Uninstall: removed start with Windows.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warning("Uninstall: couldn't remove start with Windows", ex);
        }

        try
        {
            if (ClaudeHookInstaller.Uninstall(onlyForExe: exePath) is { } settings)
                Log.Info($"Uninstall: removed the Claude Code hook from {settings} (backup kept).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            Log.Warning("Uninstall: couldn't remove the Claude Code hook", ex);
        }
        return 0; // never block the uninstall
    }

    /// <summary>Stops every process running exactly <paramref name="imagePath"/> (never another copy, never this one).</summary>
    private static void StopInstances(string imagePath)
    {
        // .NET drops only an ".exe" suffix from process names: AgentReclaim.exe runs as "AgentReclaim",
        // AgentReclaim.exe.old as "AgentReclaim.exe.old". Look under both spellings to be safe.
        var file = Path.GetFileName(imagePath);
        var names = new[] { file, Path.GetFileNameWithoutExtension(file) }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var process in names.SelectMany(System.Diagnostics.Process.GetProcessesByName))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainModule?.FileName is not { } path
                        || !ClaudeHookInstaller.SamePath(path, imagePath))
                        continue;
                    process.Kill();
                    process.WaitForExit(10_000);
                    Log.Info($"Uninstall: stopped pid {process.Id} ({Path.GetFileName(imagePath)}).");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or someone else's process.
                }
            }
        }
    }

    /// <summary>After a self-update, shows the new version in Settings → Apps if this is the installed copy.</summary>
    public static void RecordVersion(string exePath, Version version)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location
                || !ClaudeHookInstaller.SamePath(Path.Combine(location, "AgentReclaim.exe"), exePath))
                return;
            key.SetValue("DisplayVersion", version.ToString());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Debug($"Couldn't update the installed version: {ex.Message}");
        }
    }
}

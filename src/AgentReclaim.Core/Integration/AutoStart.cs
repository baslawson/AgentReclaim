using Microsoft.Win32;

namespace AgentReclaim.Core.Integration;

/// <summary>Per-user "start with Windows" via HKCU\...\Run. No admin needed; off unless the user turns it on.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentReclaim";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --tray");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

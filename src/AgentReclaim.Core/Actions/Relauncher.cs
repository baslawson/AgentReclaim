using System.ComponentModel;
using System.Diagnostics;

namespace AgentReclaim.Core.Actions;

/// <summary>Starts a stopped process again from its logged command line and working directory ("undo").</summary>
public static class Relauncher
{
    public static (bool Success, string Message) Relaunch(ActionLogEntry entry)
    {
        if (!entry.CanRelaunch) return (false, "This entry can't be relaunched.");

        var (file, arguments) = Split(entry.CommandLine ?? "");
        if (entry.ExePath is { Length: > 0 } exe) file = exe;
        if (string.IsNullOrWhiteSpace(file)) return (false, "No executable recorded.");

        // Relative paths in the command line mean something only in the original folder; never guess another.
        if (entry.WorkingDirectory is not { Length: > 0 } dir)
            return (false, $"Not relaunched: the folder {entry.Name} ran in wasn't recorded.");
        if (!Directory.Exists(dir))
            return (false, $"Not relaunched: {dir} no longer exists.");

        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = false, // console tools get their own window, so the user can see and stop them
            WorkingDirectory = dir,
        };

        try
        {
            using var process = Process.Start(start);
            return process is null
                ? (false, "Windows didn't start the process.")
                : (true, $"Relaunched {entry.Name} (new pid {process.Id}) in {dir}.");
        }
        catch (Win32Exception ex)
        {
            return (false, $"Couldn't relaunch {entry.Name}: {ex.Message}");
        }
    }

    /// <summary>Splits a Windows command line into the program (first token) and the untouched remainder.</summary>
    public static (string File, string Arguments) Split(string commandLine)
    {
        var text = commandLine.TrimStart();
        if (text.Length == 0) return ("", "");

        int end;
        string file;
        if (text[0] == '"')
        {
            end = text.IndexOf('"', 1);
            if (end < 0) return (text.Trim('"'), "");
            file = text[1..end];
            end++;
        }
        else
        {
            end = text.IndexOf(' ');
            if (end < 0) return (text, "");
            file = text[..end];
        }

        // Strip the \\?\ prefix some launchers use; ProcessStartInfo doesn't need it.
        if (file.StartsWith(@"\\?\")) file = file[4..];
        return (file, text[end..].TrimStart());
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentReclaim.Core.Cleanup;

public sealed record ToolResult(bool Success, int ExitCode, string Output);

/// <summary>Runs a command-line tool without a window and captures its output.</summary>
public static class ToolRunner
{
    /// <summary>How long to keep reading output after the tool exited.</summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(5);

    /// <param name="lowPriority">Below-normal priority (what it starts after that inherits it), for checks run while the machine is busy.</param>
    /// <param name="closeInput">Give the tool an empty, closed stdin, so one that would ask a question gets an answer at once.</param>
    /// <param name="started">Called with the new process's id as soon as it exists.</param>
    public static ToolResult Run(string file, string arguments, TimeSpan timeout, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null, bool lowPriority = false, bool closeInput = false,
        Action<int>? started = null)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = closeInput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        if (environment is not null)
            foreach (var (key, value) in environment) start.Environment[key] = value;

        try
        {
            using var process = Process.Start(start)!;
            started?.Invoke(process.Id);
            if (closeInput) process.StandardInput.Close();
            if (lowPriority)
            {
                try
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Already exited, or not allowed: it just runs at normal priority.
                }
            }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true); // our own child, started just now
                return new ToolResult(false, -1, $"{file} timed out after {timeout.TotalSeconds:0}s");
            }
            // A helper the tool left running (a daemon it started) can hold the pipes open after the tool exits: don't
            // wait for that forever, and fail, since callers act on the output (a partial `git status` must not pass).
            if (!Task.WaitAll([stdout, stderr], OutputGrace))
                return new ToolResult(false, process.ExitCode, $"{file} exited, but its output was still open after {OutputGrace.TotalSeconds:0}s");
            var output = (stdout.Result + stderr.Result).Trim();
            return new ToolResult(process.ExitCode == 0, process.ExitCode, output);
        }
        catch (Win32Exception ex)
        {
            return new ToolResult(false, -1, ex.Message);
        }
    }

    /// <summary>Full path of a tool on PATH (including .cmd shims such as npm.cmd), or null.</summary>
    public static string? Find(string name)
    {
        var extensions = Path.HasExtension(name) ? [""] : new[] { ".exe", ".cmd", ".bat" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                }
            }
        }
        return null;
    }
}

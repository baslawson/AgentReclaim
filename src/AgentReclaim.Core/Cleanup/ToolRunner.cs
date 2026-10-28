using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentReclaim.Core.Cleanup;

public sealed record ToolResult(bool Success, int ExitCode, string Output);

/// <summary>Runs a command-line tool without a window and captures its output.</summary>
public static class ToolRunner
{
    public static ToolResult Run(string file, string arguments, TimeSpan timeout, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        if (environment is not null)
            foreach (var (key, value) in environment) start.Environment[key] = value;

        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true); // our own child, started just now
                return new ToolResult(false, -1, $"{file} timed out after {timeout.TotalSeconds:0}s");
            }
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

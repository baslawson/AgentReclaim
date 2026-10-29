using System.Reflection;

namespace AgentReclaim.Core;

public static class AppInfo
{
    public const string RepoOwner = "baslawson";
    /// <summary>The public repo releases are published to (the source repo is private): updates are checked there.</summary>
    public const string RepoName = "AgentReclaim-releases";
    public static string ReleasesUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";

    /// <summary>The app's version, from the entry assembly (the exe), e.g. 0.2.0.</summary>
    public static Version Version { get; } = ParseVersion(
        (Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    /// <summary>
    /// True for the published single-file exe. Only that build can update itself; a dev build
    /// (exe + DLLs in bin\) only offers the release page.
    /// </summary>
#pragma warning disable IL3000 // An empty Location is exactly the single-file signal we test for.
    public static bool IsSingleFileBuild => IsOwnBundledExe(Environment.ProcessPath, typeof(AppInfo).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>
    /// The running process is AgentReclaim's own single-file apphost: the exe is named AgentReclaim.exe
    /// (never a shared host such as dotnet.exe), our assembly was loaded from inside the bundle (its
    /// Location is empty), and no loose DLLs sit next to it.
    /// </summary>
    internal static bool IsOwnBundledExe(string? processPath, string assemblyLocation) =>
        processPath is { Length: > 0 } exe
        && Path.GetFileName(exe).Equals("AgentReclaim.exe", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(assemblyLocation)
        && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "AgentReclaim.Core.dll"));

    /// <summary>Parses "v1.2.3", "1.2.3+commit" or "1.2.3-beta" into 1.2.3.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }
}

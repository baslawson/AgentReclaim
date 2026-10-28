using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentReclaim.Core.Diagnostics;

namespace AgentReclaim.Core.Updates;

public enum UpdateMode
{
    Off,

    /// <summary>Check and tell the user; installing is a click.</summary>
    Notify,

    /// <summary>Check, then download, verify and install by itself (when the window isn't open).</summary>
    AutoInstall,
}

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Notes,
    string PageUrl,
    string? ZipName,
    string? ZipUrl,
    string? ChecksumUrl);

/// <summary>Asks GitHub Releases for the latest published (non-draft, non-prerelease) version.</summary>
public sealed class UpdateChecker(HttpClient http)
{
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AgentReclaim", AppInfo.Version.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The latest release, or null when the repo has none yet.</summary>
    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken cancel = default)
    {
        // AGENTRECLAIM_UPDATE_API lets the update flow be tested end to end against a local server.
        var url = Environment.GetEnvironmentVariable("AGENTRECLAIM_UPDATE_API") is { Length: > 0 } custom
            ? custom
            : $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";
        Log.Debug($"Update check: GET {url}");
        using var response = await http.GetAsync(url, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>
    /// Parses a GitHub release. Null for drafts and prereleases; a malformed response throws
    /// <see cref="InvalidDataException"/> (or <see cref="JsonException"/>) so the caller reports a failed check.
    /// </summary>
    public static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The release response isn't a JSON object.");
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        if (AppInfo.ParseVersion(Text(root, "tag_name")) is not { } version)
            throw new InvalidDataException("The release has no usable version tag.");

        string? zipName = null, zipUrl = null, checksumUrl = null;
        var assets = root.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => (Name: Text(x, "name") ?? "", Url: Text(x, "browser_download_url")))
                .Where(x => x.Url is not null)
                .ToList()
            : [];
        foreach (var asset in assets)
            if (asset.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) (zipName, zipUrl) = (asset.Name, asset.Url);
        if (zipName is not null)
            checksumUrl = assets.FirstOrDefault(x => x.Name == zipName + ".sha256").Url;

        return new UpdateInfo(version, Text(root, "tag_name")!, Text(root, "body") ?? "",
            Text(root, "html_url") ?? AppInfo.ReleasesUrl, zipName, zipUrl, checksumUrl);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static bool IsNewer(UpdateInfo update, Version current) => update.Version > current;
}

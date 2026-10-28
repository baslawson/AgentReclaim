using AgentReclaim.Core.Diagnostics;
using AgentReclaim.Core.Updates;

namespace AgentReclaim.Core.Tests;

[Collection("Global path overrides")]
public class LoggingTests
{
    [Fact]
    public void Writes_at_or_above_the_minimum_level_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ar-log-{Guid.NewGuid():N}");
        var previous = Log.MinimumLevel;
        try
        {
            Log.Initialize(dir);
            Log.MinimumLevel = LogLevel.Info;
            Log.Debug("hidden detail");
            Log.Info("visible info");
            Log.Error("boom", new InvalidOperationException("inner"));

            var text = File.ReadAllText(Directory.GetFiles(dir, "agentreclaim-*.log").Single());
            Assert.DoesNotContain("hidden detail", text);
            Assert.Contains("INFO    ", text);
            Assert.Contains("visible info", text);
            Assert.Contains("InvalidOperationException: inner", text);
        }
        finally
        {
            Log.MinimumLevel = previous;
            Log.Initialize(Path.Combine(Path.GetTempPath(), "ar-log-unused"));
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class UpdateTests
{
    private const string Release = """
        {
          "tag_name": "v0.3.1",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/baslawson/AgentReclaim/releases/tag/v0.3.1",
          "body": "Fixes",
          "assets": [
            { "name": "AgentReclaim-0.3.1-win-x64.zip.sha256", "browser_download_url": "https://example/zip.sha256" },
            { "name": "AgentReclaim-0.3.1-win-x64.zip", "browser_download_url": "https://example/zip" }
          ]
        }
        """;

    [Fact]
    public void Parses_release_and_pairs_the_zip_with_its_checksum()
    {
        var update = UpdateChecker.Parse(Release)!;

        Assert.Equal(new Version(0, 3, 1), update.Version);
        Assert.Equal("https://example/zip", update.ZipUrl);
        Assert.Equal("https://example/zip.sha256", update.ChecksumUrl);
        Assert.True(UpdateChecker.IsNewer(update, new Version(0, 2, 0)));
        Assert.False(UpdateChecker.IsNewer(update, new Version(0, 3, 1)));
    }

    [Fact]
    public void Prereleases_and_drafts_are_ignored()
    {
        Assert.Null(UpdateChecker.Parse(Release.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(UpdateChecker.Parse(Release.Replace("\"draft\": false", "\"draft\": true")));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    public void Parses_versions(string text, string expected)
    {
        Assert.Equal(Version.Parse(expected), AppInfo.ParseVersion(text));
    }

    [Fact]
    public void Reads_sha256_from_checksum_file_formats()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateInstaller.ParseChecksum($"{hash}  AgentReclaim-0.3.1-win-x64.zip"));
        Assert.Equal(hash, UpdateInstaller.ParseChecksum(hash.ToUpperInvariant())?.ToLowerInvariant());
        Assert.Null(UpdateInstaller.ParseChecksum("not a hash"));
    }

    [Fact]
    public void A_dev_build_never_installs_updates()
    {
        // Tests run from bin\ with AgentReclaim.Core.dll next to the host: not a single-file build.
        var update = UpdateChecker.Parse(Release)!;
        Assert.NotNull(UpdateInstaller.CannotInstallReason(update));
    }
}

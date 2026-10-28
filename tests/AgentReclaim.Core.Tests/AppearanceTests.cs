using AgentReclaim.Core.Settings;

namespace AgentReclaim.Core.Tests;

public class AppearanceTests
{
    [Fact]
    public void Theme_defaults_to_matching_windows_and_round_trips()
    {
        Assert.Equal(ThemePreference.System, new AppSettings().Theme);

        var path = Path.Combine(Path.GetTempPath(), $"ar-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { Theme = ThemePreference.Dark }.Save(path);
            Assert.Contains("\"Dark\"", File.ReadAllText(path)); // stored by name, readable in the file
            Assert.Equal(ThemePreference.Dark, AppSettings.Load(path).Theme);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Settings_from_before_themes_load_as_auto()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "UpdateMode": "Notify" }""");
            Assert.Equal(ThemePreference.System, AppSettings.Load(path).Theme);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Normal_runs_share_one_instance_lock_and_pipe()
    {
        // Tests run without AGENTRECLAIM_DATA_DIR, like the real app.
        if (Environment.GetEnvironmentVariable("AGENTRECLAIM_DATA_DIR") is { Length: > 0 }) return;
        Assert.Equal("", AppPaths.InstanceSuffix);
        Assert.Equal(@"Local\AgentReclaim.SingleInstance", AppPaths.SingleInstanceMutexName);
        Assert.Equal($"AgentReclaim-{Environment.UserName}", AppPaths.PipeName);
    }
}

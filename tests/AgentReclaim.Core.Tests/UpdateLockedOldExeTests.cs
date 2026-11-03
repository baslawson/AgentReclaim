using System.Diagnostics;
using AgentReclaim.Core.Updates;

namespace AgentReclaim.Core.Tests;

/// <summary>
/// An update failed with "access denied" deleting AgentReclaim.exe.old, because an agent's MCP server was still running
/// from it. These run a real exe, since only a running image behaves like that: it can't be deleted but can be renamed.
/// </summary>
public class UpdateLockedOldExeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("old-exe-").FullName;
    private Process? _running;

    private string Exe => Path.Combine(_dir, "AgentReclaim.exe");
    private string Old => Exe + ".old";

    /// <summary>A copy of ping named like the old exe, kept running from that path.</summary>
    private void RunFromOld()
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), Old);
        _running = Process.Start(new ProcessStartInfo(Old, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        Assert.Throws<UnauthorizedAccessException>(() => File.Delete(Old)); // the failure this is about
    }

    private void StopRunning()
    {
        _running?.Kill();
        _running?.WaitForExit(10_000);
        _running?.Dispose();
        _running = null;
    }

    [Fact]
    public void An_old_exe_still_running_is_moved_aside_so_the_update_can_go_on()
    {
        RunFromOld();

        UpdateInstaller.ClearPreviousExe(Old);

        Assert.False(File.Exists(Old));
        File.WriteAllText(Exe, "current");
        File.Move(Exe, Old); // the update's next step
        Assert.True(File.Exists(Old));
        Assert.False(_running!.HasExited);
    }

    [Fact]
    public void An_old_exe_not_in_use_is_just_deleted()
    {
        File.WriteAllText(Old, "old");

        UpdateInstaller.ClearPreviousExe(Old);

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Nothing_to_clear_is_fine() => UpdateInstaller.ClearPreviousExe(Old);

    [Fact]
    public void Cleanup_removes_what_it_can_and_keeps_what_is_running_until_a_later_start()
    {
        RunFromOld();
        UpdateInstaller.ClearPreviousExe(Old);
        File.WriteAllText(Old, "the next old one");
        File.WriteAllText(Exe, "current");
        File.WriteAllText(Exe + ".config", "not an old exe");

        UpdateInstaller.DeletePreviousExes(Exe);

        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        Assert.Contains("AgentReclaim.exe", left);
        Assert.Contains("AgentReclaim.exe.config", left);
        Assert.DoesNotContain("AgentReclaim.exe.old", left);
        Assert.Single(left, name => name!.StartsWith("AgentReclaim.exe.old-")); // still running

        StopRunning();
        UpdateInstaller.DeletePreviousExes(Exe);
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => Path.GetFileName(f).StartsWith("AgentReclaim.exe.old"));
    }

    public void Dispose()
    {
        StopRunning();
        Directory.Delete(_dir, recursive: true);
    }
}

using AgentReclaim.Core.Settings;
using static AgentReclaim.Core.Native.NativeMethods;

namespace AgentReclaim.Core.Cleanup;

public enum CleanupMethod
{
    /// <summary>Folders go to the Recycle Bin, so they can be restored.</summary>
    RecycleFolders,

    /// <summary>The tool's own cleanup command (caches it can rebuild).</summary>
    ToolCommand,

    /// <summary>`git worktree remove`, which refuses if the worktree has changes.</summary>
    GitWorktreeRemove,
}

public sealed record CleanupTarget(
    string Id,
    string Title,
    string Description,
    long SizeBytes,
    CleanupMethod Method,
    IReadOnlyList<string> Paths,
    string? Command = null,
    string? Arguments = null,
    string? WorkingDirectory = null);

/// <summary>Finds disk space AI coding work leaves behind. Scanning never changes anything.</summary>
public static class DiskCleanup
{
    public static IReadOnlyList<CleanupTarget> Scan(AppSettings settings, CancellationToken cancel = default)
    {
        var minAge = TimeSpan.FromDays(settings.CleanupMinAgeDays);
        var temp = Path.GetTempPath();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targets = new List<CleanupTarget>();

        // Claude Code keeps per-project/per-session scratch folders in %TEMP%\claude.
        var claudeTemp = Path.Combine(temp, "claude");
        if (Directory.Exists(claudeTemp))
            AddOldFolders(targets, "claude-temp", "Old Claude Code temp folders",
                $"Scratch folders in {claudeTemp} untouched for {settings.CleanupMinAgeDays}+ days.",
                SafeDirectories(claudeTemp).SelectMany(project => SafeDirectories(project)), // one folder per session
                minAge, cancel);

        // The Codex plugin's app-server broker leaves cxc-* folders in %TEMP%.
        AddOldFolders(targets, "codex-temp", "Old Codex broker temp folders",
            $"cxc-* folders in {temp} untouched for {settings.CleanupMinAgeDays}+ days.",
            SafeDirectories(temp, "cxc-*"), minAge, cancel);

        AddToolCache(targets, "npm-cache", "npm cache", Path.Combine(local, "npm-cache"), "npm", "cache clean --force", cancel);
        AddToolCache(targets, "pip-cache", "pip cache", Path.Combine(local, "pip", "cache"), "pip", "cache purge", cancel);
        AddToolCache(targets, "uv-cache", "uv cache", Path.Combine(local, "uv", "cache"), "uv", "cache clean", cancel);

        foreach (var root in settings.ProjectRoots.Where(Directory.Exists))
            AddStaleWorktrees(targets, root, minAge, cancel);

        return targets.Where(t => t.SizeBytes > 0 || t.Method == CleanupMethod.GitWorktreeRemove).ToList();
    }

    public static (bool Success, string Message) Clean(CleanupTarget target)
    {
        var outcome = CleanCore(target);
        Diagnostics.Log.Info($"Cleanup {target.Id}: {outcome.Message}");
        return outcome;
    }

    private static (bool Success, string Message) CleanCore(CleanupTarget target)
    {
        switch (target.Method)
        {
            case CleanupMethod.RecycleFolders:
                var existing = target.Paths.Where(Directory.Exists).ToList();
                if (existing.Count == 0) return (true, "Already gone.");
                var op = new SHFILEOPSTRUCTW
                {
                    wFunc = FO_DELETE,
                    pFrom = string.Join('\0', existing) + "\0\0",
                    // WANTNUKEWARNING: if something is too big for the Recycle Bin, ask instead of deleting permanently.
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING),
                };
                var code = SHFileOperationW(ref op);
                return code == 0 && !op.fAnyOperationsAborted
                    ? (true, $"Moved {existing.Count} folder(s) to the Recycle Bin.")
                    : (false, op.fAnyOperationsAborted ? "Cancelled." : $"Some folders couldn't be moved (code {code}); they may be in use.");

            case CleanupMethod.GitWorktreeRemove:
                // The scan may be days old: check again right before deleting anything.
                if (WorktreeRemovalBlocker(target.Command!, target.Paths[0]) is { } blocker)
                    return (false, $"{target.Title}: not removed, {blocker}.");
                goto case CleanupMethod.ToolCommand;

            case CleanupMethod.ToolCommand:
                var result = ToolRunner.Run(target.Command!, target.Arguments ?? "", TimeSpan.FromMinutes(5), target.WorkingDirectory);
                return result.Success
                    ? (true, $"{target.Title}: done.")
                    : (false, $"{target.Title}: {Truncate(result.Output, 300)}");

            default:
                return (false, "Unknown cleanup method.");
        }
    }

    private static void AddOldFolders(List<CleanupTarget> targets, string id, string title, string description,
        IEnumerable<string> folders, TimeSpan minAge, CancellationToken cancel)
    {
        var old = new List<string>();
        long size = 0;
        foreach (var folder in folders)
        {
            cancel.ThrowIfCancellationRequested();
            var (bytes, newest) = Measure(folder, cancel);
            if (DateTime.UtcNow - newest < minAge) continue; // something in it is recent: may be a live session
            old.Add(folder);
            size += bytes;
        }
        if (old.Count > 0)
            targets.Add(new CleanupTarget(id, $"{title} ({old.Count})", description, size, CleanupMethod.RecycleFolders, old));
    }

    private static void AddToolCache(List<CleanupTarget> targets, string id, string title, string path, string tool,
        string arguments, CancellationToken cancel)
    {
        if (!Directory.Exists(path) || ToolRunner.Find(tool) is not { } exe) return;
        var (bytes, _) = Measure(path, cancel);
        targets.Add(new CleanupTarget(id, title, $"{path}. Rebuilt automatically as packages are installed again.",
            bytes, CleanupMethod.ToolCommand, [path], exe, arguments));
    }

    /// <summary>Linked worktrees (agents create these for parallel work) that are clean and untouched for a while.</summary>
    private static void AddStaleWorktrees(List<CleanupTarget> targets, string root, TimeSpan minAge, CancellationToken cancel)
    {
        if (ToolRunner.Find("git") is not { } git) return;

        foreach (var repo in FindRepos(root, depth: 2))
        {
            cancel.ThrowIfCancellationRequested();
            var list = ToolRunner.Run(git, "worktree list --porcelain", TimeSpan.FromSeconds(15), repo);
            if (!list.Success) continue;

            var worktrees = list.Output.Split('\n')
                .Where(l => l.StartsWith("worktree "))
                .Select(l => l["worktree ".Length..].Trim().Replace('/', '\\'))
                .Skip(1); // the first entry is the main checkout

            foreach (var worktree in worktrees)
            {
                if (!Directory.Exists(worktree)) continue; // `git worktree prune` territory, nothing on disk
                if (WorktreeRemovalBlocker(git, worktree) is not null) continue;

                var (bytes, newest) = Measure(worktree, cancel);
                if (DateTime.UtcNow - newest < minAge) continue;

                targets.Add(new CleanupTarget($"worktree:{worktree}", $"Stale worktree {Path.GetFileName(worktree)}",
                    $"{worktree} (repo {repo}). No changes, no ignored files except build output, untouched for {(int)(DateTime.UtcNow - newest).TotalDays} days. Deleted permanently.",
                    bytes, CleanupMethod.GitWorktreeRemove, [worktree], git, $"worktree remove \"{worktree}\"", repo));
            }
        }
    }

    private static readonly HashSet<string> RegenerableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", ".next", ".nuxt", ".turbo", ".cache",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".venv", "venv", "coverage", ".gradle",
    };

    /// <summary>
    /// Why a worktree must not be removed, or null if it's safe. `git worktree remove` refuses on changes
    /// and untracked files, but deletes ignored files (.env, local databases, ...) permanently, so only
    /// worktrees whose ignored content is regenerable build/dependency output qualify. Checked at scan
    /// time and again right before removal.
    /// </summary>
    internal static string? WorktreeRemovalBlocker(string git, string worktree)
    {
        var status = ToolRunner.Run(git, "status --porcelain --ignored --untracked-files=normal", TimeSpan.FromSeconds(30), worktree);
        if (!status.Success) return $"git status failed: {Truncate(status.Output, 200)}";

        var lines = status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.FirstOrDefault(l => !l.StartsWith("!! ")) is { } change) return $"it has changes ({change})";
        var keeper = lines.Select(l => l[3..].Trim('"')).FirstOrDefault(path => !IsRegenerable(path));
        return keeper is null ? null : $"it has ignored files that may matter ({keeper})";
    }

    /// <summary>
    /// An ignored entry from `git status --ignored` that is pure build/dependency output. Directories are
    /// listed with a trailing slash; only whole directories with a known build-output name qualify, so an
    /// individually listed file (say, local.db) never does.
    /// </summary>
    internal static bool IsRegenerable(string ignoredEntry)
    {
        var path = ignoredEntry.Replace('\\', '/');
        if (path.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.EndsWith('/')) return false;
        return RegenerableNames.Contains(path.TrimEnd('/').Split('/')[^1]);
    }

    private static IEnumerable<string> FindRepos(string root, int depth)
    {
        if (Directory.Exists(Path.Combine(root, ".git"))) yield return root;
        if (depth == 0) yield break;
        foreach (var dir in SafeDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj") continue;
            foreach (var repo in FindRepos(dir, depth - 1)) yield return repo;
        }
    }

    /// <summary>Total size and newest write time, not following junctions/symlinks (so nothing outside is counted or touched).</summary>
    internal static (long Bytes, DateTime NewestUtc) Measure(string folder, CancellationToken cancel)
    {
        long bytes = 0;
        var newest = Directory.GetLastWriteTimeUtc(folder);
        var stack = new Stack<string>([folder]);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var dir = new DirectoryInfo(stack.Pop());
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
                {
                    if (entry.LastWriteTimeUtc > newest) newest = entry.LastWriteTimeUtc;
                    if (entry is FileInfo file) bytes += file.Length;
                    else stack.Push(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return (bytes, newest);
    }

    private static IEnumerable<string> SafeDirectories(string path, string pattern = "*")
    {
        try
        {
            return Directory.GetDirectories(path, pattern, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

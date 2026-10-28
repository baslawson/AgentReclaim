using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Diagnostics;

namespace AgentReclaim.Core.Firewall;

/// <summary>What an apply changes, compared with the firewall as last read.</summary>
/// <param name="Repair">Rules that exist but were changed outside AgentReclaim: they're replaced.</param>
/// <param name="Blocked">Wanted, but a rule AgentReclaim didn't create has the name: left alone.</param>
public sealed record FirewallPlan(
    IReadOnlyList<BlockRule> Desired,
    IReadOnlyList<BlockRule> Add,
    IReadOnlyList<BlockRule> Repair,
    IReadOnlyList<BlockRule> Remove,
    IReadOnlyList<BlockRule> Blocked,
    IReadOnlyList<string> RemoveRenamed)
{
    public bool IsEmpty => Add.Count == 0 && Repair.Count == 0 && Remove.Count == 0 && RemoveRenamed.Count == 0;

    /// <summary>
    /// Fingerprints (<see cref="FirewallRequest.Fingerprint"/>) of exactly the rules this plan removes or replaces,
    /// as they were when the user looked: every copy under each name, with all its settings.
    /// </summary>
    public IReadOnlySet<string> Approved { get; init; } = new HashSet<string>();

    /// <summary>
    /// The confirmation's lines for what goes, by what the rules really do now (every copy), not by their names: an
    /// AgentReclaim rule broadened outside it takes more away than its name says.
    /// </summary>
    public IReadOnlyList<string> RemovalLines(FirewallStatus status)
    {
        string Now(string name) => string.Join("; ", status.OurRules
            .Where(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)).Select(FirewallSafety.DescribeActual));
        bool AsNamed(BlockRule rule) => status.OurRules
            .Where(o => string.Equals(o.Name, rule.Name, StringComparison.OrdinalIgnoreCase)).All(o => WindowsFirewall.Matches(o, rule));
        return Repair.Select(r => $"Replace {r.Describe()} (changed outside AgentReclaim; now: {Now(r.Name)})")
            .Concat(Remove.Select(r => AsNamed(r) ? $"Unblock {r.Describe()}" : $"Unblock {r.Describe()} (changed outside AgentReclaim; removing it takes away: {Now(r.Name)})"))
            .Concat(RemoveRenamed.Select(n => $"Remove \"{n}\" (an AgentReclaim rule renamed outside it; it now does: {Now(n)})"))
            .ToList();
    }

    /// <summary>The rules as they are now that this plan removes or rewrites (for checking what that takes away).</summary>
    public IReadOnlyList<FirewallRuleInfo> Touched(FirewallStatus status)
    {
        var names = Remove.Concat(Repair).Select(r => r.Name).Concat(RemoveRenamed).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return status.OurRules.Where(r => names.Contains(r.Name)).ToList();
    }

    public static FirewallPlan Create(FirewallStatus status, IEnumerable<BlockRule> desired)
    {
        var wanted = desired.Distinct().ToList();
        var names = wanted.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var plan = new FirewallPlan(
            wanted.Where(r => status.StateOf(r) != RuleState.NameTaken).ToList(),
            wanted.Where(r => status.StateOf(r) == RuleState.Missing).ToList(),
            wanted.Where(r => status.StateOf(r) == RuleState.Changed).ToList(),
            status.Present().Where(r => !names.Contains(r.Name)).ToList(),
            wanted.Where(r => status.StateOf(r) == RuleState.NameTaken).ToList(),
            status.Renamed());
        return plan with
        {
            Approved = plan.Touched(status).GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(FirewallRequest.Fingerprint).ToHashSet(StringComparer.Ordinal),
        };
    }
}

/// <summary>
/// What the user approved, as the elevated helper receives it: the blocks wanted, and fingerprints of exactly the
/// existing rules that may be removed or replaced. A rule added, renamed or changed after approval doesn't match, so
/// it stays.
/// </summary>
/// <param name="Approved">
/// Fingerprints (<see cref="Fingerprint"/>) of the approved rules, one per name; null means any (the uninstaller's
/// remove-all). Fingerprints also keep arbitrary rule names off the elevated command line.
/// </param>
public sealed record FirewallRequest(IReadOnlyList<BlockRule> Desired, IReadOnlySet<string>? Approved)
{
    public static FirewallRequest RemoveEverything { get; } = new([], null);

    public static FirewallRequest From(FirewallPlan plan) => new(plan.Desired, plan.Approved);

    /// <summary>
    /// 16 hex digits of SHA-256 over every copy under one name, with all its settings, in a fixed order: a copy more
    /// or less, or any setting changed, gives a different fingerprint.
    /// </summary>
    public static string Fingerprint(IEnumerable<FirewallRuleInfo> copies)
    {
        var lines = copies.Select(r => string.Join('\u001f', r.Name, r.Grouping, r.Enabled ? "1" : "0",
                r.Action.ToString(CultureInfo.InvariantCulture), r.Direction.ToString(CultureInfo.InvariantCulture),
                r.Protocol.ToString(CultureInfo.InvariantCulture), r.LocalPorts, r.RemotePorts, r.Profiles.ToString(CultureInfo.InvariantCulture),
                r.ApplicationName, r.ServiceName, r.LocalAddresses, r.RemoteAddresses, r.InterfaceTypes, r.Restrictions, r.Details,
                r.PartlyUnreadable ? "?" : ""))
            .Order(StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>May these rules (every copy under one name, as read now) be removed or replaced?</summary>
    public bool MayRemove(IEnumerable<FirewallRuleInfo> copies) => Approved is null || Approved.Contains(Fingerprint(copies));

    /// <summary><c>firewall apply &lt;wanted…&gt; [--remove &lt;fingerprints…&gt;]</c>, or <c>firewall remove-all</c>.</summary>
    public string Arguments =>
        Approved is null
            ? Desired.Count == 0 ? "firewall remove-all" : throw new InvalidOperationException("Approve removals explicitly.")
            : string.Join(' ', new[] { "firewall", "apply" }
                .Concat(Desired.Select(r => r.Token))
                .Concat(Approved.Count > 0 ? ["--remove"] : [])
                .Concat(Approved.Order(StringComparer.Ordinal)));

    /// <summary>Strictly parses the helper's arguments (after <c>firewall</c>). Anything off rejects the whole request.</summary>
    public static FirewallRequest? Parse(string[] args, out string? error)
    {
        error = null;
        if (args is ["remove-all"]) return RemoveEverything;
        if (args is not ["apply", .. var rest])
        {
            error = "Unknown command.";
            return null;
        }
        var items = rest.ToList();
        var remove = items.IndexOf("--remove");
        if (items.Count(a => a == "--remove") > 1)
        {
            error = "Malformed options.";
            return null;
        }
        var wantedTokens = remove < 0 ? items : items[..remove];
        var fingerprints = remove < 0 ? [] : items[(remove + 1)..];
        if (wantedTokens.Count + fingerprints.Count > BlockRule.MaxRules)
        {
            error = "Too many rules.";
            return null;
        }
        if (fingerprints.Any(f => !Regex.IsMatch(f, "^[0-9a-f]{16}$")) || fingerprints.Distinct().Count() != fingerprints.Count)
        {
            error = "Malformed fingerprints.";
            return null;
        }
        var wanted = BlockRule.ParseTokens(wantedTokens, out error);
        return wanted is null ? null : new FirewallRequest(wanted, fingerprints.ToHashSet(StringComparer.Ordinal));
    }
}

/// <param name="Cancelled">The user said no to the Windows admin prompt.</param>
public sealed record HelperRun(int ExitCode, bool Cancelled, string? Error);

public sealed record FirewallApplyResult(bool Success, string Message, ActionLogEntry? Entry);

/// <summary>
/// The elevated side (<c>AgentReclaim.exe firewall …</c>) and the app side that starts it, checks the
/// firewall afterwards and logs what happened.
/// </summary>
public static class FirewallChange
{
    public const int ExitOk = 0, ExitPartial = 1, ExitBadArguments = 2, ExitNotElevated = 3, ExitFailed = 4, ExitNoRules = 5, ExitUnknown = 6, ExitBusy = 7;

    /// <summary>Machine-wide: one firewall change at a time, across Windows sessions and leftover helpers.</summary>
    public const string LockName = @"Global\AgentReclaim.Firewall";

    /// <summary>ShellExecute's parameter limit is 2048 characters; stay under it.</summary>
    public const int MaxArgumentLength = 2000;

    /// <summary>
    /// <c>firewall apply &lt;wanted…&gt; [--remove &lt;fingerprints…&gt;]</c> puts the wanted blocks in place and removes or replaces only the approved rules (needs admin);
    /// <c>firewall remove-all</c> removes all of AgentReclaim's (the uninstaller); <c>firewall has-rules</c> exits 0 if any exist, 5 if none, 6 if the firewall couldn't be read (for the uninstaller).
    /// </summary>
    public static int RunHelper(string[] args)
    {
        if (args is ["has-rules"])
        {
            var status = WindowsFirewall.ReadStatus();
            if (!status.Readable || !status.Complete) return ExitUnknown;
            return status.HasAny ? ExitOk : ExitNoRules;
        }

        var request = FirewallRequest.Parse(args, out var error);
        if (request is null)
        {
            Log.Warning($"Firewall helper: refused its arguments: {error}");
            return ExitBadArguments;
        }

        if (!WindowsFirewall.IsElevated())
        {
            Log.Warning("Firewall helper: not running as administrator.");
            return ExitNotElevated;
        }

        try
        {
            // The whole read-then-change runs under the lock, so a second helper always plans from what the first left.
            using var gate = TryLock(TimeSpan.FromSeconds(60));
            if (gate is null)
            {
                Log.Warning("Firewall helper: another firewall change is still running.");
                return ExitBusy;
            }
            var errors = WindowsFirewall.Apply(request);
            foreach (var e in errors) Log.Warning($"Firewall helper: {e}");
            return errors.Count == 0 ? ExitOk : ExitPartial;
        }
        catch (Exception ex)
        {
            Log.Error("Firewall helper failed", ex);
            return ExitFailed;
        }
    }

    /// <summary>Takes the machine-wide firewall lock, or null if it stays taken for <paramref name="wait"/>. Dispose releases it.</summary>
    public static IDisposable? TryLock(TimeSpan wait, string name = LockName)
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (!mutex.WaitOne(wait))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // A helper died holding it: the firewall is re-read under the lock anyway, so carry on.
        }
        return new Releaser(mutex);
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    public static string HelperArguments(FirewallRequest request) => request.Arguments;

    /// <summary>Starts the helper through the Windows admin prompt and waits for it.</summary>
    public static async Task<HelperRun> RunElevatedAsync(string exePath, FirewallRequest request)
    {
        var start = new ProcessStartInfo(exePath, HelperArguments(request))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new HelperRun(-1, false, "The helper didn't start.");
            // Each removal re-reads the firewall first (about a third of a second), so a long list takes a while.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
            return new HelperRun(process.ExitCode, false, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the user said no
        {
            return new HelperRun(-1, true, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
        {
            return new HelperRun(-1, false, ex.Message);
        }
    }

    /// <summary>
    /// Applies <paramref name="plan"/>: an intent line in the action log, the elevated helper, then a fresh read of
    /// the firewall to report what's really in place (the helper's exit code alone isn't trusted).
    /// </summary>
    public static async Task<FirewallApplyResult> ApplyLoggedAsync(FirewallPlan plan, ActionLog log, string trigger,
        Func<FirewallRequest, Task<HelperRun>> runHelper, Func<FirewallStatus> readStatus)
    {
        var summary = Summarize(plan);
        var request = FirewallRequest.From(plan);
        if (HelperArguments(request).Length > MaxArgumentLength)
            return new FirewallApplyResult(false, $"Too many ports to apply at once ({plan.Desired.Count} rules). Use ranges or fewer ports.", null);

        if (!log.TryWrite(Entry("intent", true, $"About to change the firewall: {summary}"), out var error))
            return new FirewallApplyResult(false, $"Didn't change the firewall: couldn't write the action log ({error}).", null);

        var run = await runHelper(request);
        string message;
        bool success;
        if (run.Cancelled)
        {
            (success, message) = (false, "Firewall not changed: the administrator prompt was declined.");
        }
        else if (run.Error is not null || run.ExitCode is ExitBadArguments or ExitNotElevated or ExitFailed or ExitBusy)
        {
            (success, message) = (false, $"Firewall change failed: {run.Error ?? ExitText(run.ExitCode)}. See the log for details.");
        }
        else
        {
            var after = readStatus();
            if (!after.Readable || !after.Complete)
            {
                (success, message) = (false, $"Applied, but couldn't read the firewall back completely to check what's in place{(after.Error is null ? "" : $": {after.Error}")}.");
            }
            else
            {
                var missing = plan.Desired.Where(r => after.StateOf(r) != RuleState.Active).ToList();
                // Anything of AgentReclaim's still there that wasn't asked for (planned removals, or added meanwhile).
                var wanted = plan.Desired.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
                var left = after.Present().Where(r => !wanted.Contains(r.Name)).Select(r => r.Describe())
                    .Concat(after.Renamed().Select(n => $"\"{n}\"")).ToList();
                success = missing.Count == 0 && left.Count == 0;
                message = success
                    ? $"Firewall updated: {summary}." + (run.ExitCode == ExitPartial ? " The helper reported problems along the way; see the log." : "")
                    : $"Firewall partly updated ({summary}). Not in place: {string.Join(", ", missing.Select(r => r.Describe()))}{(missing.Count > 0 && left.Count > 0 ? "; " : "")}{(left.Count > 0 ? "still there: " + string.Join(", ", left) : "")}.";
            }
        }
        var done = Entry("done", success, message);
        log.TryWrite(done, out _);
        Log.Info(message);
        return new FirewallApplyResult(success, message, done);

        ActionLogEntry Entry(string phase, bool ok, string text) =>
            new(DateTime.UtcNow, "firewall", phase, trigger, ok, text, 0, "Windows Firewall", null, null, null, 0, 0, "", "");
    }

    public static string Summarize(FirewallPlan plan)
    {
        var parts = new List<string>();
        if (plan.Add.Count > 0) parts.Add($"block {string.Join(", ", plan.Add.Select(r => r.Describe()))}");
        if (plan.Repair.Count > 0) parts.Add($"restore {string.Join(", ", plan.Repair.Select(r => r.Describe()))}");
        if (plan.Remove.Count > 0) parts.Add($"unblock {string.Join(", ", plan.Remove.Select(r => r.Describe()))}");
        if (plan.RemoveRenamed.Count > 0) parts.Add($"remove renamed AgentReclaim rule(s) {string.Join(", ", plan.RemoveRenamed.Select(n => $"\"{n}\""))}");
        return parts.Count == 0 ? "no changes" : string.Join("; ", parts);
    }

    private static string ExitText(int code) => code switch
    {
        ExitBadArguments => "the helper refused its arguments",
        ExitNotElevated => "the helper didn't get administrator rights",
        ExitFailed => "Windows Firewall reported an error",
        ExitBusy => "another firewall change was still running; try again",
        _ => $"exit code {code}",
    };
}

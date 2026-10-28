using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using AgentReclaim.Core.Diagnostics;

namespace AgentReclaim.Core.Firewall;

/// <summary>A Windows Firewall rule as read through the firewall's COM API.</summary>
public sealed record FirewallRuleInfo(
    string Name,
    string Grouping,
    bool Enabled,
    int Action,
    int Direction,
    int Protocol,
    string LocalPorts,
    string RemotePorts,
    int Profiles,
    string ApplicationName,
    string ServiceName,
    string LocalAddresses,
    string RemoteAddresses,
    string InterfaceTypes)
{
    /// <summary>What else narrows the rule (network cards, apps, users, IPsec), or empty. Anything here means it doesn't block everywhere.</summary>
    public string Restrictions { get; init; } = "";

    /// <summary>
    /// The values behind <see cref="Restrictions"/> and the remaining settings (which network cards, users, ICMP
    /// types…), so a change to any of them changes the rule's fingerprint.
    /// </summary>
    public string Details { get; init; } = "";

    /// <summary>Some setting couldn't be read: what the rule does isn't fully known, so it's never removed or replaced.</summary>
    public bool PartlyUnreadable { get; init; }
}

public enum RuleState
{
    Missing,

    /// <summary>In place and blocking exactly what its name says.</summary>
    Active,

    /// <summary>There, but changed outside AgentReclaim (disabled, turned into an allow rule, narrowed…).</summary>
    Changed,

    /// <summary>A rule with this name exists that AgentReclaim didn't create: it's left alone.</summary>
    NameTaken,
}

/// <param name="CurrentProfiles">The active network profiles (1 domain, 2 private, 4 public).</param>
/// <param name="PolicyOverride">Group Policy decides: local rules (AgentReclaim's included) may be ignored.</param>
/// <param name="OtherFirewalls">Third-party firewalls registered with Windows Security.</param>
/// <param name="OurRules">Every rule in AgentReclaim's group.</param>
/// <param name="ForeignNames">Rule names that look like AgentReclaim's but belong to rules outside its group.</param>
public sealed record FirewallStatus(
    string? Error,
    int CurrentProfiles,
    IReadOnlyDictionary<int, bool> EnabledByProfile,
    bool PolicyOverride,
    IReadOnlyList<string> OtherFirewalls,
    IReadOnlyList<FirewallRuleInfo> OurRules,
    IReadOnlySet<string> ForeignNames)
{
    /// <summary>False if some rule couldn't even be identified: a name collision could then go unseen.</summary>
    public bool Complete { get; init; } = true;

    /// <summary>
    /// Enabled allow rules that require IPsec authentication from listed users or machines: Windows lets those
    /// override block rules, so a block isn't absolute while any exist.
    /// </summary>
    public IReadOnlyList<string> BypassRules { get; init; } = [];

    public static FirewallStatus Unreadable(string error) =>
        new(error, 0, new Dictionary<int, bool>(), false, [], [], new HashSet<string>());

    public bool Readable => Error is null;

    /// <summary>Windows Firewall is on for every network profile in use now.</summary>
    public bool OnForCurrentNetworks =>
        Readable && WindowsFirewall.Profiles.Where(p => (CurrentProfiles & p) != 0).All(p => EnabledByProfile.GetValueOrDefault(p));

    public RuleState StateOf(BlockRule rule)
    {
        if (ForeignNames.Contains(rule.Name)) return RuleState.NameTaken;
        var found = OurRules.Where(r => string.Equals(r.Name, rule.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0) return RuleState.Missing;
        return found.All(r => WindowsFirewall.Matches(r, rule)) ? RuleState.Active : RuleState.Changed;
    }

    /// <summary>AgentReclaim's rules in the firewall now (whatever their state), by what their names say.</summary>
    public IReadOnlyList<BlockRule> Present() =>
        OurRules.Select(r => BlockRule.FromName(r.Name)).OfType<BlockRule>().Distinct().ToList();

    /// <summary>Rules in AgentReclaim's group whose name was changed: not understood, but still AgentReclaim's to remove.</summary>
    public IReadOnlyList<string> Renamed() =>
        OurRules.Where(r => BlockRule.FromName(r.Name) is null).Select(r => r.Name).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Any rule at all in AgentReclaim's group.</summary>
    public bool HasAny => OurRules.Count > 0;
}

/// <summary>
/// Reads Windows Firewall through its COM API (no admin needed) and, when elevated, adds or removes
/// AgentReclaim's own block rules. Never creates an allow rule and never touches a rule outside its group.
/// </summary>
public static class WindowsFirewall
{
    public const int ProfileDomain = 1, ProfilePrivate = 2, ProfilePublic = 4, ProfilesAll = 0x7FFFFFFF;
    public static readonly int[] Profiles = [ProfileDomain, ProfilePrivate, ProfilePublic];

    private const int ActionBlock = 0, DirectionIn = 1, DirectionOut = 2;

    public static string ProfileName(int profile) => profile switch
    {
        ProfileDomain => "domain",
        ProfilePrivate => "private",
        ProfilePublic => "public",
        _ => "unknown",
    };

    /// <summary>True only if <paramref name="info"/> blocks exactly what <paramref name="rule"/> describes, everywhere.</summary>
    public static bool Matches(FirewallRuleInfo info, BlockRule rule)
    {
        var inbound = rule.Direction == FwDirection.In;
        var ports = inbound ? info.LocalPorts : info.RemotePorts;
        var other = inbound ? info.RemotePorts : info.LocalPorts;
        return info.Name == rule.Name
               && info.Grouping == BlockRule.Group
               && info.Enabled
               && info.Action == ActionBlock
               && info.Direction == (inbound ? DirectionIn : DirectionOut)
               && info.Protocol == rule.ProtocolNumber
               && ports == rule.Ports.ToString()
               && other is "*" or ""
               && (info.Profiles & (ProfileDomain | ProfilePrivate | ProfilePublic)) == (ProfileDomain | ProfilePrivate | ProfilePublic)
               && info.ApplicationName.Length == 0
               && info.ServiceName.Length == 0
               && info.LocalAddresses is "*" or ""
               && info.RemoteAddresses is "*" or ""
               && info.InterfaceTypes.Equals("All", StringComparison.OrdinalIgnoreCase)
               && info.Restrictions.Length == 0;
    }

    /// <summary>
    /// An allow rule that may override block rules ("authenticated bypass"). The COM API doesn't expose the override
    /// setting itself, so any enabled allow rule requiring IPsec counts (outgoing ones need no user or machine list).
    /// </summary>
    public static bool IsAuthenticatedBypass(FirewallRuleInfo info) =>
        info.Enabled && info.Action == 1 && info.Restrictions.Contains("IPsec");

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static FirewallStatus ReadStatus()
    {
        try
        {
            var policy = CreatePolicy();
            var enabled = new Dictionary<int, bool>();
            foreach (var profile in Profiles) enabled[profile] = (bool)policy.FirewallEnabled[profile];

            var ours = new List<FirewallRuleInfo>();
            var foreign = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Windows matches rule names ignoring case
            var (rules, complete) = ((List<FirewallRuleInfo>, bool))ReadRules(policy);
            foreach (var rule in rules)
            {
                if (rule.Grouping == BlockRule.Group) ours.Add(rule);
                else if (rule.Name.StartsWith(BlockRule.NamePrefix, StringComparison.OrdinalIgnoreCase)) foreign.Add(rule.Name);
            }

            return new FirewallStatus(null, (int)policy.CurrentProfileTypes, enabled,
                (int)policy.LocalPolicyModifyState == 1, OtherFirewalls(), ours, foreign)
            {
                Complete = complete,
                BypassRules = rules.Where(IsAuthenticatedBypass).Select(r => r.Name).ToList(),
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning("Couldn't read Windows Firewall", ex);
            return FirewallStatus.Unreadable($"Couldn't read Windows Firewall: {ex.Message}");
        }
    }

    /// <summary>
    /// Carries out what the user approved. Needs admin. Runs in two stages. First everything is added: missing blocks,
    /// and a correct replacement (under a temporary name) for each rule changed outside AgentReclaim; then the
    /// firewall is read back to check they're all in place. Only then is anything removed, and each removal is checked
    /// again against the firewall as it is at that moment: still AgentReclaim's alone (the firewall removes by name),
    /// still exactly what the user approved (another program may have changed it meanwhile), fully readable, and not
    /// what may let a Remote Desktop session in. Returns one line per problem.
    /// </summary>
    public static IReadOnlyList<string> Apply(FirewallRequest request) =>
        Apply(new ComFirewallRules(), request, FirewallSafety.IsRemoteSession(), FirewallSafety.RdpPort());

    /// <summary>The same, against any rule store (tests use a fake one).</summary>
    /// <remarks>
    /// The lock-out checks run here too, with this (elevated) process's own readings, so nothing that starts the
    /// helper (the uninstaller included) can block or revoke what may let a Remote Desktop session in.
    /// </remarks>
    internal static IReadOnlyList<string> Apply(IFirewallRules store, FirewallRequest request, bool remoteSession = false,
        int rdpPort = FirewallSafety.DefaultRdpPort)
    {
        var errors = new List<string>();
        var (all, complete) = store.Read();
        var wanted = request.Desired.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        // Windows matches names ignoring case, and removes by name: compare the same way.
        var foreign = all.Where(r => r.Grouping != BlockRule.Group).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sameName = all.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        // Every rule in the group, including ones renamed since (they were AgentReclaim's, so they're removed when not wanted).
        var ours = all.Where(r => r.Grouping == BlockRule.Group)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        string? Revokes(IEnumerable<FirewallRuleInfo> current) =>
            FirewallSafety.CheckRevocations(current, remoteSession, rdpPort).FirstOrDefault(i => i.Blocking)?.Text;

        // Whether every rule called name (any group, any capitals), as read just now, may go.
        string? WhyNotRemove(IReadOnlyList<FirewallRuleInfo> copies, bool readComplete)
        {
            if (!readComplete) return "some firewall rules couldn't be read, so AgentReclaim can't be sure the name is only its own.";
            if (copies.Count == 0) return "it's no longer there.";
            if (copies.Any(r => r.Grouping != BlockRule.Group)) return "a rule outside AgentReclaim has the same name.";
            if (copies.Any(r => r.PartlyUnreadable))
                return "some of its settings couldn't be read, so AgentReclaim can't tell what removing it takes away. Remove it in Windows Defender Firewall if you want it gone.";
            if (!request.MayRemove(copies)) return "it appeared or changed after the change was approved. Reload the Firewall tab and apply again.";
            return Revokes(copies);
        }

        // 1. Add: missing blocks, and a replacement for each changed rule. Nothing is removed or edited here.
        var replacements = new List<(BlockRule Rule, string Temp)>();
        foreach (var rule in request.Desired)
        {
            if (foreign.Contains(rule.Name))
            {
                errors.Add($"Left {rule.Name} alone: a rule outside AgentReclaim has the same name.");
                continue;
            }
            var existing = ours.GetValueOrDefault(rule.Name);
            if (existing is not null && existing.All(r => Matches(r, rule))) continue;
            if (FirewallSafety.CheckLockout(rule, remoteSession, rdpPort) is { } lockout)
            {
                errors.Add($"Didn't apply {rule.Name}: {lockout.Text}");
                continue;
            }
            if (existing is null)
            {
                try
                {
                    store.Add(rule, rule.Name);
                    Log.Info($"Firewall: added {rule.Name}.");
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
                {
                    errors.Add($"Couldn't add {rule.Name}: {ex.Message}");
                }
                continue;
            }

            // Changed outside AgentReclaim: it's replaced, never edited, so it keeps whatever it covers until the
            // correct block is in place and it's removed in stage 2. Checked now too, so nothing is added for nothing.
            if (WhyNotRemove(existing, complete) is { } stays)
            {
                errors.Add($"Left {rule.Name} as it is: {stays}");
                continue;
            }
            // A leftover replacement is only reused if it's a single copy blocking exactly the same traffic.
            var temp = ours.Where(o => IsTemporaryFor(o.Key, rule) && !foreign.Contains(o.Key) && sameName.GetValueOrDefault(o.Key) == 1
                                       && o.Value is [var only] && Matches(only with { Name = rule.Name }, rule))
                .Select(o => o.Key).FirstOrDefault();
            if (temp is null)
            {
                temp = Enumerable.Range(1, 50).Select(n => TemporaryName(rule, n)).FirstOrDefault(n => !sameName.ContainsKey(n));
                if (temp is null)
                {
                    errors.Add($"Didn't restore {rule.Name}: no free name for its replacement.");
                    continue;
                }
                try
                {
                    store.Add(rule, temp);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
                {
                    errors.Add($"Didn't restore {rule.Name}: couldn't add its replacement ({ex.Message}).");
                    continue;
                }
            }
            replacements.Add((rule, temp));
        }

        var temps = replacements.Select(p => p.Temp).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unwanted = ours.Keys.Where(n => !wanted.ContainsKey(n) && !temps.Contains(n)).ToList();
        if (replacements.Count == 0 && unwanted.Count == 0) return errors;
        if (errors.Count > 0)
        {
            errors.Add("Didn't unblock or replace anything, because not every block could be put in place.");
            return errors;
        }

        // Every wanted block in place in a reading of the firewall: under its own name, or as its replacement.
        string? Unprotected(List<FirewallRuleInfo> now)
        {
            bool InPlace(BlockRule rule, string name)
            {
                var copies = now.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                return copies.Count > 0 && copies.All(r => r.Grouping == BlockRule.Group && Matches(r with { Name = rule.Name }, rule));
            }
            var missing = request.Desired.Where(r => !foreign.Contains(r.Name))
                .Where(r => !(InPlace(r, r.Name) || replacements.Any(p => p.Rule == r && InPlace(r, p.Temp)))).ToList();
            return missing.Count == 0 ? null : string.Join(", ", missing.Select(r => r.Name));
        }

        var (after, afterComplete) = store.Read();
        if (!afterComplete)
        {
            errors.Add("Didn't unblock or replace anything: some firewall rules couldn't be read back to check the new blocks are in place.");
            return errors;
        }
        if (Unprotected(after) is { } notAdded)
        {
            errors.Add($"Didn't unblock or replace anything: {notAdded} isn't in place after adding it.");
            return errors;
        }

        // 2. Remove, one copy at a time. Before each, the whole firewall is read again: every wanted block must still
        // be in place, and what's left under the name must be exactly the approved copies not yet removed (another
        // program may change the firewall at any moment; it doesn't take AgentReclaim's lock).
        bool RemoveChecked(string name, string what)
        {
            List<FirewallRuleInfo>? left = null; // the approved copies not yet removed
            while (true)
            {
                var (now, nowComplete) = store.Read();
                var copies = now.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (left is not null && copies.Count == 0) return true;
                string? why;
                if (!nowComplete) why = "some firewall rules couldn't be read, so AgentReclaim can't be sure the name is only its own.";
                else if (Unprotected(now) is { } gone) why = $"{gone} isn't in place any more, so nothing it would leave open is unblocked.";
                else if (left is null) why = WhyNotRemove(copies, nowComplete);
                else why = copies.Count == left.Count && Remaining(copies, left) ? null : "it changed while it was being removed.";
                if (why is not null)
                {
                    errors.Add($"Left {name} {what}: {why}");
                    return false;
                }
                try
                {
                    store.Remove(name);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
                {
                    errors.Add($"Couldn't remove {name}: {ex.Message}");
                    return false;
                }
                // One copy went (whichever Windows picked): the rest must be what's left of the approved ones.
                left = RemoveOneOf(copies, store.ReadName(name).Rules);
                if (left is null)
                {
                    errors.Add($"Left the rest of {name} {what}: it changed while it was being removed.");
                    return false;
                }
                if (left.Count == 0) return true;
            }
        }

        foreach (var (rule, temp) in replacements)
        {
            if (!RemoveChecked(rule.Name, "as it is (a correct block is in place as \"" + temp + "\")")) continue;
            try
            {
                store.Rename(temp, rule.Name);
                Log.Info($"Firewall: replaced {rule.Name} (it had been changed).");
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"Replaced {rule.Name}, but the new block is still named \"{temp}\" ({ex.Message}). The port stays blocked.");
            }
        }

        if (errors.Count > 0 && unwanted.Count > 0)
        {
            errors.Add("Didn't unblock anything, because not every replacement finished.");
            return errors;
        }
        foreach (var name in unwanted)
        {
            if (RemoveChecked(name, "alone")) Log.Info($"Firewall: removed {name}.");
        }
        return errors;
    }

    /// <summary>Whether <paramref name="now"/> is <paramref name="approved"/> with nothing changed (same copies, any order).</summary>
    private static bool Remaining(IReadOnlyList<FirewallRuleInfo> now, IReadOnlyList<FirewallRuleInfo> approved)
    {
        var pool = approved.ToList();
        return now.All(pool.Remove) && pool.Count == 0;
    }

    /// <summary>
    /// After one copy was removed: what's left, if it's exactly the approved copies less one; otherwise null.
    /// </summary>
    private static List<FirewallRuleInfo>? RemoveOneOf(IReadOnlyList<FirewallRuleInfo> before, IReadOnlyList<FirewallRuleInfo> after)
    {
        if (after.Count != before.Count - 1) return null;
        var pool = before.ToList();
        return after.All(pool.Remove) ? after.ToList() : null;
    }

    /// <summary>Makes the rules exactly <paramref name="desired"/>; any other of AgentReclaim's may go (tests).</summary>
    internal static IReadOnlyList<string> ApplyExactly(IFirewallRules store, IReadOnlyList<BlockRule> desired, bool remoteSession = false,
        int rdpPort = FirewallSafety.DefaultRdpPort) =>
        Apply(store, new FirewallRequest(desired, null), remoteSession, rdpPort);

    /// <summary>The name a replacement block has until the changed rule it replaces is gone.</summary>
    public static string TemporaryName(BlockRule rule, int n = 1) => rule.Name + (n == 1 ? " (replacement)" : $" (replacement {n})");

    public static bool IsTemporaryFor(string name, BlockRule rule) =>
        Enumerable.Range(1, 50).Any(n => string.Equals(name, TemporaryName(rule, n), StringComparison.OrdinalIgnoreCase));

    internal static dynamic CreateRule(BlockRule rule, string? name = null)
    {
        dynamic r = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
        r.Name = name ?? rule.Name;
        r.Description = BlockRule.Description;
        r.Grouping = BlockRule.Group;
        r.Protocol = rule.ProtocolNumber; // before the ports: ports are only valid for TCP and UDP
        if (rule.Direction == FwDirection.In) r.LocalPorts = rule.Ports.ToString();
        else r.RemotePorts = rule.Ports.ToString();
        r.Direction = rule.Direction == FwDirection.In ? DirectionIn : DirectionOut;
        r.Action = ActionBlock;
        r.Profiles = ProfilesAll;
        r.InterfaceTypes = "All";
        r.Enabled = true;
        return r;
    }

    internal static dynamic CreatePolicy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    /// <summary>
    /// Every rule (or, with <paramref name="only"/>, every rule with that name in any capitals), and whether every
    /// rule could at least be identified (name and group). A rule whose other settings can't be read is kept, marked
    /// as unreadable, so it never counts as a working block and is never removed.
    /// </summary>
    internal static (List<FirewallRuleInfo> Rules, bool Complete) ReadRules(dynamic policy, string? only = null)
    {
        var list = new List<FirewallRuleInfo>();
        var complete = true;
        foreach (dynamic r in policy.Rules)
        {
            try
            {
                string name, grouping;
                try
                {
                    name = (string?)r.Name ?? "";
                    grouping = (string?)r.Grouping ?? "";
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    complete = false;
                    continue;
                }
                if (only is not null && !string.Equals(name, only, StringComparison.OrdinalIgnoreCase)) continue; // names only: much quicker

                try
                {
                    list.Add(ReadOne(r, name, grouping));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    list.Add(new FirewallRuleInfo(name, grouping, false, -1, -1, -1, "", "", 0, "", "", "", "", "")
                        { Restrictions = "settings couldn't be read", PartlyUnreadable = true });
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(r);
            }
        }
        return (list, complete);
    }

    /// <summary>One rule's settings (its name and group already read).</summary>
    internal static FirewallRuleInfo ReadOne(dynamic r, string name, string grouping)
    {
        var info = new FirewallRuleInfo(name, grouping, (bool)r.Enabled, (int)r.Action, (int)r.Direction,
            (int)r.Protocol, (string?)r.LocalPorts ?? "", (string?)r.RemotePorts ?? "", (int)r.Profiles,
            (string?)r.ApplicationName ?? "", (string?)r.serviceName ?? "", (string?)r.LocalAddresses ?? "",
            (string?)r.RemoteAddresses ?? "", (string?)r.InterfaceTypes ?? "");
        var (restrictions, details, unreadable) = ((string, string, bool))Restrictions(r);
        return info with { Restrictions = restrictions, Details = details, PartlyUnreadable = unreadable };
    }

    /// <summary>
    /// Settings that narrow a rule beyond ports and addresses (specific network cards, apps, users, an owner, IPsec),
    /// as labels for people, and every such value (plus ICMP types and edge traversal) for fingerprints.
    /// </summary>
    private static (string Labels, string Details, bool Unreadable) Restrictions(dynamic r)
    {
        var labels = new List<string>();
        var details = new List<string>();
        var unreadable = false;
        Narrows("interfaces", "interfaces", () => r.Interfaces is Array a ? string.Join("|", a.Cast<object?>().Select(o => o?.ToString())) : r.Interfaces?.ToString());
        Narrows("app package", "package", () => (string?)r.LocalAppPackageId);
        Narrows("local users", "localusers", () => (string?)r.LocalUserAuthorizedList);
        Narrows("remote users", "remoteusers", () => (string?)r.RemoteUserAuthorizedList);
        Narrows("remote machines", "remotemachines", () => (string?)r.RemoteMachineAuthorizedList);
        Narrows("owner", "owner", () => (string?)r.LocalUserOwner); // scopes the rule to one user's traffic
        Narrows("IPsec", "secure", () => (int)r.SecureFlags is var flags and not 0 ? flags.ToString(CultureInfo.InvariantCulture) : null);
        Narrows(null, "icmp", () => (string?)r.IcmpTypesAndCodes);
        Narrows(null, "edge", () => ((int)r.EdgeTraversalOptions).ToString(CultureInfo.InvariantCulture));
        return (string.Join(", ", labels), string.Join(";", details), unreadable);

        // label null: recorded for the fingerprint only (it doesn't narrow a block rule on any port it names).
        void Narrows(string? label, string key, Func<string?> read)
        {
            try
            {
                var value = read();
                details.Add($"{key}={value}");
                if (label is not null && !string.IsNullOrEmpty(value)) labels.Add(label);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                details.Add($"{key}=?");
                unreadable = true;
                if (label is not null) labels.Add(label + " (unreadable)"); // fail closed
            }
        }
    }

    /// <summary>Third-party firewalls registered with Windows Security (not available on Windows Server).</summary>
    private static IReadOnlyList<string> OtherFirewalls()
    {
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic service = locator.ConnectServer(".", "root/SecurityCenter2");
            var names = new List<string>();
            foreach (dynamic product in service.ExecQuery("SELECT displayName FROM FirewallProduct"))
                if ((string?)product.displayName is { Length: > 0 } name) names.Add(name);
            return names;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}

/// <summary>Where <see cref="WindowsFirewall.Apply(IReadOnlyList{BlockRule})"/> reads and changes rules.</summary>
internal interface IFirewallRules
{
    /// <summary>All rules, and whether every one could be identified.</summary>
    (List<FirewallRuleInfo> Rules, bool Complete) Read();

    /// <summary>Every rule with this name in any capitals and any group, as it is right now, and whether every rule could be identified.</summary>
    (List<FirewallRuleInfo> Rules, bool Complete) ReadName(string name);

    /// <summary>Adds a block for <paramref name="rule"/> under <paramref name="name"/> (its own name, or a temporary one).</summary>
    void Add(BlockRule rule, string name);

    /// <summary>Renames the one rule called <paramref name="from"/>.</summary>
    void Rename(string from, string to);

    /// <summary>Removes one rule with this name (the firewall removes by name only).</summary>
    void Remove(string name);
}

internal sealed class ComFirewallRules : IFirewallRules
{
    private readonly dynamic _policy = WindowsFirewall.CreatePolicy();

    public (List<FirewallRuleInfo> Rules, bool Complete) Read() => WindowsFirewall.ReadRules(_policy);

    public (List<FirewallRuleInfo> Rules, bool Complete) ReadName(string name) => WindowsFirewall.ReadRules(_policy, name);

    public void Add(BlockRule rule, string name) => _policy.Rules.Add(WindowsFirewall.CreateRule(rule, name));

    public void Rename(string from, string to)
    {
        dynamic rule = _policy.Rules.Item(from);
        try
        {
            rule.Name = to;
        }
        finally
        {
            Marshal.FinalReleaseComObject(rule);
        }
    }

    public void Remove(string name) => _policy.Rules.Remove(name);
}

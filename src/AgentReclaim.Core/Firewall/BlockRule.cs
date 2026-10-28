using System.Text.RegularExpressions;

namespace AgentReclaim.Core.Firewall;

public enum FwProtocol { Tcp, Udp }

public enum FwDirection { In, Out }

[Flags]
public enum Protocols { Tcp = 1, Udp = 2, Both = Tcp | Udp }

public enum Directions { Inbound, Outbound, Both }

/// <summary>One port or an inclusive range, 1–65535.</summary>
public readonly partial record struct PortRange(int From, int To)
{
    public static PortRange Single(int port) => new(port, port);

    public bool Contains(int port) => port >= From && port <= To;

    public bool IsValid => From is >= 1 and <= 65535 && To is >= 1 and <= 65535 && From <= To;

    public override string ToString() => From == To ? From.ToString() : $"{From}-{To}";

    /// <summary>"22" or "3000-3010" (spaces allowed around the dash). Nothing else.</summary>
    public static bool TryParse(string? text, out PortRange range)
    {
        range = default;
        var match = Pattern().Match(text?.Trim() ?? "");
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, out var from)) return false;
        var to = from;
        if (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, out to)) return false;
        range = new PortRange(from, to);
        return range.IsValid;
    }

    [GeneratedRegex(@"^([0-9]{1,5})(?:\s*-\s*([0-9]{1,5}))?$")]
    private static partial Regex Pattern();
}

/// <summary>
/// One Windows Firewall rule AgentReclaim owns: block one protocol on one port (or range) in one direction.
/// Incoming rules match the local port (something on this PC listening); outgoing rules match the remote port
/// (this PC connecting to that port elsewhere). The name encodes all three, so the firewall itself is the record.
/// </summary>
public sealed partial record BlockRule(FwProtocol Protocol, PortRange Ports, FwDirection Direction)
{
    public const string Group = "AgentReclaim";
    public const string NamePrefix = "AgentReclaim: block ";
    public const string Description = "Created by AgentReclaim. Remove it from AgentReclaim's Firewall tab, or with Windows Defender Firewall.";

    /// <summary>Most rules one apply may ask for (keeps the elevated command line bounded).</summary>
    public const int MaxRules = 400;

    public string Name => $"{NamePrefix}{(Protocol == FwProtocol.Tcp ? "TCP" : "UDP")} {Ports} {(Direction == FwDirection.In ? "in" : "out")}";

    /// <summary>The form passed to the elevated helper: tcp:22:in, udp:137-139:out.</summary>
    public string Token => $"{(Protocol == FwProtocol.Tcp ? "tcp" : "udp")}:{Ports}:{(Direction == FwDirection.In ? "in" : "out")}";

    /// <summary>IANA protocol number, as the firewall stores it.</summary>
    public int ProtocolNumber => Protocol == FwProtocol.Tcp ? 6 : 17;

    public string Describe() =>
        $"{(Protocol == FwProtocol.Tcp ? "TCP" : "UDP")} {Ports} {(Direction == FwDirection.In ? "incoming" : "outgoing")}";

    public static bool TryParseToken(string? token, out BlockRule rule)
    {
        rule = null!;
        var match = TokenPattern().Match(token ?? "");
        if (!match.Success || !PortRange.TryParse(match.Groups[2].Value, out var ports)) return false;
        rule = new BlockRule(match.Groups[1].Value == "tcp" ? FwProtocol.Tcp : FwProtocol.Udp, ports,
            match.Groups[3].Value == "in" ? FwDirection.In : FwDirection.Out);
        return true;
    }

    /// <summary>The rule a name stands for, or null if it isn't exactly a name AgentReclaim would give.</summary>
    public static BlockRule? FromName(string? name)
    {
        var match = NamePattern().Match(name ?? "");
        if (!match.Success || !PortRange.TryParse(match.Groups[2].Value, out var ports)) return null;
        var rule = new BlockRule(match.Groups[1].Value.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? FwProtocol.Tcp : FwProtocol.Udp, ports,
            match.Groups[3].Value.Equals("in", StringComparison.OrdinalIgnoreCase) ? FwDirection.In : FwDirection.Out);
        // Canonical form only ("022" or "22-22" aren't ours), but capitals ignored, as Windows compares names.
        return string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase) ? rule : null;
    }

    /// <summary>
    /// Strictly parses the elevated helper's arguments: every token valid, no duplicates, at most <see cref="MaxRules"/>.
    /// Any problem rejects the whole list, so nothing is half-applied from a malformed command line.
    /// </summary>
    public static IReadOnlyList<BlockRule>? ParseTokens(IReadOnlyList<string> tokens, out string? error)
    {
        error = null;
        if (tokens.Count > MaxRules)
        {
            error = $"Too many rules ({tokens.Count}; at most {MaxRules}).";
            return null;
        }
        var rules = new List<BlockRule>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (!TryParseToken(token, out var rule))
            {
                error = $"Not a valid rule: {(token.Length > 40 ? token[..40] + "…" : token)}";
                return null;
            }
            if (!names.Add(rule.Name))
            {
                error = $"Listed twice: {token}";
                return null;
            }
            rules.Add(rule);
        }
        return rules;
    }

    [GeneratedRegex("^(tcp|udp):([0-9]{1,5}(?:-[0-9]{1,5})?):(in|out)$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^AgentReclaim: block (TCP|UDP) ([0-9]{1,5}(?:-[0-9]{1,5})?) (in|out)$", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();
}

/// <summary>A line in the Firewall tab: a service's port(s), which protocols and which directions to block.</summary>
public sealed record PortChoice(string Service, PortRange Ports, Protocols Protocols, Directions Direction)
{
    public IEnumerable<BlockRule> Rules()
    {
        foreach (var protocol in new[] { FwProtocol.Tcp, FwProtocol.Udp })
        {
            if (!Protocols.HasFlag(protocol == FwProtocol.Tcp ? Protocols.Tcp : Protocols.Udp)) continue;
            if (Direction != Directions.Outbound) yield return new BlockRule(protocol, Ports, FwDirection.In);
            if (Direction != Directions.Inbound) yield return new BlockRule(protocol, Ports, FwDirection.Out);
        }
    }
}

/// <param name="Why">One line on why someone would block these.</param>
public sealed record FirewallPreset(string Name, string Why, IReadOnlyList<PortChoice> Ports);

public static class FirewallPresets
{
    private static PortChoice P(string service, int port, Protocols protocols = Protocols.Tcp) =>
        new(service, PortRange.Single(port), protocols, Directions.Inbound);

    private static PortChoice P(string service, int from, int to, Protocols protocols = Protocols.Tcp) =>
        new(service, new PortRange(from, to), protocols, Directions.Inbound);

    /// <summary>Nothing here is blocked until the user ticks it. All default to incoming only.</summary>
    public static IReadOnlyList<FirewallPreset> All { get; } =
    [
        new("Remote access", "Logins and remote control from other machines.",
        [
            P("SSH", 22), P("Telnet", 23), P("FTP", 21), P("Remote Desktop", 3389, Protocols.Both),
            P("VNC", 5900), P("WinRM (HTTP)", 5985), P("WinRM (HTTPS)", 5986),
        ]),
        new("File sharing", "Windows file and printer sharing (SMB and NetBIOS).",
        [
            P("SMB", 445), P("NetBIOS name", 137, Protocols.Udp), P("NetBIOS datagram", 138, Protocols.Udp),
            P("NetBIOS session", 139),
        ]),
        new("Databases", "Database servers an agent may have started with a default, password-less setup.",
        [
            P("SQL Server", 1433), P("MySQL / MariaDB", 3306), P("PostgreSQL", 5432), P("Redis", 6379),
            P("MongoDB", 27017), P("Elasticsearch", 9200),
        ]),
        new("Docker API", "Docker's remote API: anyone who reaches it controls Docker on this PC.",
        [
            P("Docker API", 2375), P("Docker API (TLS)", 2376),
        ]),
        new("Keep dev servers private", "Dev servers and debuggers agents start, often listening on every network.",
        [
            P("Node / React / Next", 3000, 3010), P("Phoenix / misc", 4000), P("Angular", 4200), P("Flask / misc", 5000),
            P("Vite", 5173, 5174), P("Django / misc", 8000), P("HTTP alt", 8080), P("Jupyter", 8888),
            P("Node debugger", 9229),
        ]),
    ];
}

/// <summary>Maps the firewall's AgentReclaim rules back onto the listed ports, exactly.</summary>
public static class FirewallSelection
{
    /// <summary>
    /// For each listed choice, the direction it's fully blocked in (null if not exactly: none, or only part of it),
    /// and the rules left over as custom choices, one per port and protocol (merged when both protocols agree).
    /// Ticking the listed ones in those directions plus the custom ones asks for exactly <paramref name="present"/>.
    /// </summary>
    public static (IReadOnlyList<Directions?> Listed, IReadOnlyList<PortChoice> Custom) Map(IReadOnlyList<PortChoice> listed,
        IReadOnlyList<BlockRule> present)
    {
        var claimed = new HashSet<BlockRule>();
        var states = new List<Directions?>();
        foreach (var choice in listed)
        {
            var mine = present.Where(r => r.Ports == choice.Ports && choice.Protocols.HasFlag(Flag(r.Protocol))).ToHashSet();
            Directions? match = mine.Count == 0 ? null
                : Enum.GetValues<Directions>().Cast<Directions?>()
                    .FirstOrDefault(d => (choice with { Direction = d!.Value }).Rules().ToHashSet().SetEquals(mine));
            if (match is not null) claimed.UnionWith(mine);
            states.Add(match);
        }

        var custom = present.Where(r => !claimed.Contains(r))
            .GroupBy(r => (r.Ports, r.Protocol))
            .Select(g => (g.Key.Ports, Protocol: Flag(g.Key.Protocol), Direction: DirectionOf(g)))
            .GroupBy(x => (x.Ports, x.Direction))
            .Select(g => new PortChoice("Custom port", g.Key.Ports, g.Aggregate((Protocols)0, (p, x) => p | x.Protocol), g.Key.Direction))
            .OrderBy(c => c.Ports.From).ThenBy(c => c.Ports.To).ThenBy(c => c.Protocols)
            .ToList();
        return (states, custom);
    }

    public static Directions DirectionOf(IEnumerable<BlockRule> rules)
    {
        var list = rules.ToList();
        var inbound = list.Any(r => r.Direction == FwDirection.In);
        var outbound = list.Any(r => r.Direction == FwDirection.Out);
        return inbound && outbound ? Directions.Both : outbound ? Directions.Outbound : Directions.Inbound;
    }

    /// <summary>
    /// A port's status line: exactly which of its protocols and directions are blocked (never "blocked" for
    /// partial coverage), and who listens on it, flagged "Open" when other machines could reach it unblocked.
    /// </summary>
    public static string Describe(PortRange ports, Protocols protocols, FirewallStatus status, SocketSnapshot sockets,
        Func<int, string> processName)
    {
        var parts = new List<string>();
        var rules = status.Present().Where(r => r.Ports == ports && protocols.HasFlag(Flag(r.Protocol))).ToList();
        var active = rules.Where(r => status.StateOf(r) == RuleState.Active).ToHashSet();
        if (rules.Count > 0)
        {
            string text;
            if (active.Count < rules.Count) text = "Changed outside AgentReclaim: apply to restore";
            else if (Enum.GetValues<Directions>().Where(x => new PortChoice("", ports, protocols, x).Rules().ToHashSet().SetEquals(active))
                         .Select(x => (Directions?)x).FirstOrDefault() is { } d)
                text = $"Blocked ({(d == Directions.Both ? "both ways" : d == Directions.Outbound ? "outgoing" : "incoming")})";
            else text = $"Partly blocked: {string.Join(", ", active.OrderBy(r => r.Protocol).ThenBy(r => r.Direction).Select(r => r.Describe()))}";
            if (active.Count > 0 && !status.OnForCurrentNetworks) text += " (but Windows Firewall is off)";
            else if (active.Count > 0 && status.BypassRules.Count > 0) text += " (except authenticated-bypass traffic)";
            parts.Add(text);
        }

        var listening = sockets.Listening.Where(s => ports.Contains(s.Port) && protocols.HasFlag(Flag(s.Protocol))).ToList();
        if (listening.Count == 0)
        {
            if (rules.Count == 0) parts.Add("Not in use");
            return string.Join(" · ", parts);
        }
        // Reachable from other machines: listening beyond localhost, with no working incoming block for its protocol.
        bool BlockedIn(FwProtocol p) => status.OnForCurrentNetworks && active.Any(r => r.Protocol == p && r.Direction == FwDirection.In);
        var open = listening.Where(s => !s.LoopbackOnly && !BlockedIn(s.Protocol)).ToList();
        string Names(IEnumerable<ListeningSocket> s) => string.Join(", ", s.Select(x => processName(x.Pid)).Distinct().Take(3));
        if (open.Count > 0) parts.Add($"Open: {Names(open)} listening on the network");
        else if (listening.All(s => s.LoopbackOnly)) parts.Add($"{Names(listening)} listening on this PC only");
        else parts.Add($"{Names(listening)} listening");
        return string.Join(" · ", parts);
    }

    private static Protocols Flag(FwProtocol protocol) => protocol == FwProtocol.Tcp ? Protocols.Tcp : Protocols.Udp;
}

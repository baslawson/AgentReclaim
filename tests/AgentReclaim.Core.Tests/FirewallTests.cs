using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Firewall;

namespace AgentReclaim.Core.Tests;

public class FirewallRuleTests
{
    [Theory]
    [InlineData("22", 22, 22)]
    [InlineData(" 3000-3010 ", 3000, 3010)]
    [InlineData("8000 - 8001", 8000, 8001)]
    [InlineData("65535", 65535, 65535)]
    public void Parses_ports_and_ranges(string text, int from, int to)
    {
        Assert.True(PortRange.TryParse(text, out var range));
        Assert.Equal(new PortRange(from, to), range);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("10-5")]
    [InlineData("22,23")]
    [InlineData("-1")]
    [InlineData("ssh")]
    [InlineData("123456")]
    [InlineData(null)]
    public void Rejects_anything_else(string? text) => Assert.False(PortRange.TryParse(text, out _));

    [Fact]
    public void Name_and_token_describe_the_rule_and_round_trip()
    {
        var rule = new BlockRule(FwProtocol.Tcp, new PortRange(3000, 3010), FwDirection.In);
        Assert.Equal("AgentReclaim: block TCP 3000-3010 in", rule.Name);
        Assert.Equal("tcp:3000-3010:in", rule.Token);
        Assert.Equal(rule, BlockRule.FromName(rule.Name));
        Assert.True(BlockRule.TryParseToken(rule.Token, out var parsed));
        Assert.Equal(rule, parsed);

        var udpOut = new BlockRule(FwProtocol.Udp, PortRange.Single(137), FwDirection.Out);
        Assert.Equal(udpOut, BlockRule.FromName("AgentReclaim: block UDP 137 out"));
        Assert.Equal(udpOut, BlockRule.FromName("agentreclaim: BLOCK udp 137 OUT")); // Windows ignores case in names
        Assert.Equal(17, udpOut.ProtocolNumber);
    }

    [Theory]
    [InlineData("AgentReclaim: block TCP 022 in")]  // not the canonical spelling
    [InlineData("AgentReclaim: block TCP 22-22 in")]
    [InlineData("AgentReclaim: block TCP 22 in ")]
    [InlineData("AgentReclaim: block ICMP 22 in")]
    [InlineData("AgentReclaim: allow TCP 22 in")]
    [InlineData("Remote Desktop - User Mode (TCP-In)")]
    public void Only_canonical_AgentReclaim_names_are_recognised(string name) => Assert.Null(BlockRule.FromName(name));

    [Theory]
    [InlineData("tcp:22:in & calc")]
    [InlineData("TCP:22:in")]
    [InlineData("tcp:22")]
    [InlineData("tcp:0:in")]
    [InlineData("tcp:70000:in")]
    [InlineData("tcp:22:sideways")]
    [InlineData("icmp:22:in")]
    [InlineData("tcp:9-1:in")]
    [InlineData("\"tcp:22:in\"")]
    public void The_helper_refuses_a_whole_list_with_one_bad_token(string bad)
    {
        Assert.Null(BlockRule.ParseTokens(["tcp:22:in", bad], out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void The_helper_refuses_duplicates_and_overlong_lists()
    {
        Assert.Null(BlockRule.ParseTokens(["tcp:22:in", "tcp:22:in"], out _));
        Assert.Null(BlockRule.ParseTokens(Enumerable.Range(1, BlockRule.MaxRules + 1).Select(p => $"tcp:{p}:in").ToList(), out _));
        var ok = BlockRule.ParseTokens(["tcp:22:in", "tcp:22:out", "udp:137-139:in"], out var error);
        Assert.Null(error);
        Assert.Equal(3, ok!.Count);
    }

    [Fact]
    public void A_choice_expands_to_one_rule_per_protocol_and_direction()
    {
        Assert.Single(new PortChoice("SSH", PortRange.Single(22), Protocols.Tcp, Directions.Inbound).Rules());
        Assert.Equal(2, new PortChoice("SSH", PortRange.Single(22), Protocols.Tcp, Directions.Both).Rules().Count());
        var all = new PortChoice("RDP", PortRange.Single(3389), Protocols.Both, Directions.Both).Rules().ToList();
        Assert.Equal(4, all.Count);
        Assert.Equal(4, all.Select(r => r.Name).Distinct().Count());
        Assert.All(new PortChoice("x", PortRange.Single(1), Protocols.Udp, Directions.Outbound).Rules(),
            r => Assert.Equal((FwProtocol.Udp, FwDirection.Out), (r.Protocol, r.Direction)));
    }

    [Fact]
    public void Presets_are_valid_distinct_incoming_only_and_include_dev_servers()
    {
        var ports = FirewallPresets.All.SelectMany(p => p.Ports).ToList();
        Assert.All(ports, p => Assert.True(p.Ports.IsValid));
        Assert.All(ports, p => Assert.Equal(Directions.Inbound, p.Direction));
        Assert.Equal(ports.Count, ports.Select(p => p.Ports).Distinct().Count());
        Assert.Contains(FirewallPresets.All, p => p.Name == "Keep dev servers private" && p.Ports.Any(x => x.Ports.Contains(5173)));
        Assert.Contains(ports, p => p.Ports.Contains(22));
        // Everything fits in one elevated command line even with every preset ticked both ways.
        var everything = ports.SelectMany(p => (p with { Direction = Directions.Both }).Rules()).ToList();
        var request = new FirewallRequest(everything, new HashSet<string>());
        Assert.True(FirewallChange.HelperArguments(request).Length <= FirewallChange.MaxArgumentLength);
    }
}

public class FirewallStateTests
{
    internal static FirewallRuleInfo Info(BlockRule rule) => new(
        rule.Name, BlockRule.Group, true, 0, rule.Direction == FwDirection.In ? 1 : 2, rule.ProtocolNumber,
        rule.Direction == FwDirection.In ? rule.Ports.ToString() : "*", rule.Direction == FwDirection.In ? "*" : rule.Ports.ToString(),
        WindowsFirewall.ProfilesAll, "", "", "*", "*", "All");

    internal static FirewallStatus Status(IEnumerable<FirewallRuleInfo> rules, params string[] foreign) =>
        new(null, WindowsFirewall.ProfilePrivate, new Dictionary<int, bool> { [1] = true, [2] = true, [4] = true }, false, [], rules.ToList(),
            foreign.ToHashSet());

    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule SshOut = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.Out);

    [Fact]
    public void A_rule_counts_only_if_it_blocks_exactly_what_its_name_says()
    {
        Assert.True(WindowsFirewall.Matches(Info(Ssh), Ssh));
        Assert.True(WindowsFirewall.Matches(Info(SshOut), SshOut));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { Enabled = false }, Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { Action = 1 }, Ssh)); // turned into an allow rule
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { LocalPorts = "23" }, Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { Direction = 2 }, Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { Protocol = 17 }, Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { Profiles = WindowsFirewall.ProfilePublic }, Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { ApplicationName = @"C:\x.exe" }, Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { RemoteAddresses = "10.0.0.0/8" }, Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { InterfaceTypes = "Wireless" }, Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { Grouping = "Other" }, Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { Restrictions = "interfaces" }, Ssh)); // narrowed to one network card
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { Restrictions = "owner" }, Ssh)); // scoped to one user's traffic
        Assert.Equal(RuleState.Changed, Status([Info(Ssh) with { Restrictions = "owner" }]).StateOf(Ssh));
        Assert.False(WindowsFirewall.Matches(Info(Ssh) with { Restrictions = "settings couldn't be read" }, Ssh));
    }

    [Fact]
    public void States_cover_missing_active_changed_duplicated_and_taken_names()
    {
        var status = Status([Info(Ssh), Info(SshOut) with { Enabled = false }]);
        Assert.Equal(RuleState.Active, status.StateOf(Ssh));
        Assert.Equal(RuleState.Changed, status.StateOf(SshOut));
        Assert.Equal(RuleState.Missing, status.StateOf(new BlockRule(FwProtocol.Udp, PortRange.Single(22), FwDirection.In)));
        Assert.Equal(RuleState.Active, Status([Info(Ssh), Info(Ssh)]).StateOf(Ssh)); // identical copies all block
        Assert.Equal(RuleState.Changed, Status([Info(Ssh), Info(Ssh) with { Enabled = false }]).StateOf(Ssh));
        Assert.Equal(RuleState.NameTaken, Status([], Ssh.Name).StateOf(Ssh));
    }

    [Fact]
    public void The_plan_adds_repairs_removes_and_skips_taken_names()
    {
        var smb = new BlockRule(FwProtocol.Tcp, PortRange.Single(445), FwDirection.In);
        var rdp = new BlockRule(FwProtocol.Tcp, PortRange.Single(3389), FwDirection.In);
        var vnc = new BlockRule(FwProtocol.Tcp, PortRange.Single(5900), FwDirection.In);
        var status = Status([Info(Ssh), Info(SshOut) with { Action = 1 }, Info(smb)], vnc.Name);

        var plan = FirewallPlan.Create(status, [Ssh, SshOut, rdp, vnc]);
        Assert.Equal([rdp], plan.Add);
        Assert.Equal([SshOut], plan.Repair);
        Assert.Equal([smb], plan.Remove);
        Assert.Equal([vnc], plan.Blocked);
        Assert.DoesNotContain(vnc, plan.Desired); // never sent to the helper
        Assert.False(plan.IsEmpty);

        Assert.True(FirewallPlan.Create(status with { OurRules = [Info(Ssh)] }, [Ssh]).IsEmpty);
        Assert.Equal(3, FirewallPlan.Create(status, []).Remove.Count); // changed ones are still AgentReclaim's to remove
    }

    [Fact]
    public void Removing_everything_never_touches_rules_outside_the_group()
    {
        var other = Info(Ssh) with { Name = "OpenSSH Server (sshd)", Grouping = "OpenSSH Server" };
        var plan = FirewallPlan.Create(Status([Info(Ssh), other]), []);
        Assert.Equal([Ssh], plan.Remove);
        Assert.Equal("firewall remove-all", FirewallChange.HelperArguments(FirewallRequest.RemoveEverything));
    }

    [Fact]
    public void Firewall_off_for_the_current_network_is_reported()
    {
        var status = Status([Info(Ssh)]) with { EnabledByProfile = new Dictionary<int, bool> { [1] = true, [2] = false, [4] = true } };
        Assert.False(status.OnForCurrentNetworks);
        Assert.True((status with { CurrentProfiles = WindowsFirewall.ProfilePublic }).OnForCurrentNetworks);
        Assert.False(FirewallStatus.Unreadable("x").OnForCurrentNetworks);
    }
}

public class FirewallSafetyTests
{
    private static readonly BlockRule RdpIn = new(FwProtocol.Tcp, PortRange.Single(3389), FwDirection.In);
    private static readonly BlockRule SshIn = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule SshOut = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.Out);

    private static SocketSnapshot Sockets(IEnumerable<ListeningSocket> listening, IEnumerable<TcpConnection> established, bool complete = true) =>
        new(listening.ToList(), established.ToList(), complete);

    private static string Name(int pid) => $"proc{pid}";

    [Fact]
    public void Refuses_blocking_the_remote_desktop_port_from_a_remote_desktop_session()
    {
        var issues = FirewallSafety.Check([RdpIn], Sockets([], []), Name, remoteSession: true, rdpPort: 3389);
        Assert.Contains(issues, i => i.Blocking);

        var custom = FirewallSafety.Check([new BlockRule(FwProtocol.Tcp, new PortRange(3300, 3400), FwDirection.In)], Sockets([], []), Name, true, 3390);
        Assert.Contains(custom, i => i.Blocking); // a moved RDP port inside a range

        Assert.DoesNotContain(FirewallSafety.Check([RdpIn], Sockets([], []), Name, remoteSession: false, rdpPort: 3389), i => i.Blocking);
        Assert.DoesNotContain(FirewallSafety.Check([RdpIn with { Direction = FwDirection.Out }], Sockets([], []), Name, true, 3389), i => i.Blocking);
    }

    [Fact]
    public void Warns_about_live_incoming_connections_but_not_localhost_ones()
    {
        var listening = new[] { new ListeningSocket(FwProtocol.Tcp, 22, 100, false) };
        var live = FirewallSafety.Check([SshIn], Sockets(listening, [new TcpConnection(22, 51000, false, 100)]), Name, false, 3389);
        Assert.Contains(live, i => !i.Blocking && i.Text.Contains("proc100") && i.Text.Contains("22"));

        Assert.Empty(FirewallSafety.Check([SshIn], Sockets(listening, [new TcpConnection(22, 51000, true, 100)]), Name, false, 3389));
        // An outgoing connection that happens to use local port 22 isn't "incoming SSH".
        Assert.Empty(FirewallSafety.Check([SshIn], Sockets([], [new TcpConnection(22, 443, false, 7)]), Name, false, 3389));
    }

    [Fact]
    public void Warns_about_live_outgoing_connections()
    {
        var issues = FirewallSafety.Check([SshOut], Sockets([], [new TcpConnection(50123, 22, false, 9)]), Name, false, 3389);
        Assert.Contains(issues, i => i.Text.Contains("proc9") && i.Text.Contains("another machine"));
        Assert.Empty(FirewallSafety.Check([SshOut], Sockets([], [new TcpConnection(50123, 443, false, 9)]), Name, false, 3389));
    }

    [Fact]
    public void Unreadable_connections_are_never_treated_as_none()
    {
        var issues = FirewallSafety.Check([SshIn], Sockets([], [], complete: false), Name, false, 3389);
        Assert.Contains(issues, i => i.Text.Contains("Couldn't read"));
    }

    [Fact]
    public void Loopback_addresses_are_recognised()
    {
        Assert.True(SocketSnapshot.IsLoopback4(0x0100007F)); // 127.0.0.1 in network order
        Assert.False(SocketSnapshot.IsLoopback4(0x0100000A)); // 10.0.0.1
        Assert.False(SocketSnapshot.IsLoopback4(0));          // 0.0.0.0: every network
        var v6 = new byte[16];
        v6[15] = 1;
        Assert.True(SocketSnapshot.IsLoopback6(v6));
        Assert.False(SocketSnapshot.IsLoopback6(new byte[16])); // ::
        var mapped = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 127, 0, 0, 1 };
        Assert.True(SocketSnapshot.IsLoopback6(mapped));
    }

    [Fact]
    public void Reads_this_machines_sockets_and_firewall_without_admin()
    {
        var sockets = SocketSnapshot.Read();
        Assert.True(sockets.Complete);
        Assert.NotEmpty(sockets.Listening);

        var status = WindowsFirewall.ReadStatus();
        Assert.True(status.Readable, status.Error);
        Assert.Equal(3, status.EnabledByProfile.Count);
    }
}

public class FirewallApplyTests
{
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);

    private static (ActionLog Log, string Path) NewLog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-fw-{Guid.NewGuid():N}.jsonl");
        return (new ActionLog(path), path);
    }

    private static FirewallPlan PlanAdd() => FirewallPlan.Create(FirewallStateTests.Status([]), [Ssh]);

    [Fact]
    public async Task Success_is_judged_by_reading_the_firewall_back_not_by_the_exit_code()
    {
        var (log, path) = NewLog();
        try
        {
            var calls = 0;
            var lied = await FirewallChange.ApplyLoggedAsync(PlanAdd(), log, "manual: firewall",
                _ => { calls++; return Task.FromResult(new HelperRun(0, false, null)); },
                () => FirewallStateTests.Status([]));
            Assert.Equal(1, calls);
            Assert.False(lied.Success);
            Assert.Contains("partly", lied.Message);

            var ok = await FirewallChange.ApplyLoggedAsync(PlanAdd(), log, "manual: firewall",
                d => { Assert.Equal([Ssh], d.Desired); return Task.FromResult(new HelperRun(0, false, null)); },
                () => FirewallStateTests.Status([FirewallStateTests.Info(Ssh)]));
            Assert.True(ok.Success);

            var entries = log.ReadRecent(10);
            Assert.Equal(4, entries.Count);
            Assert.All(entries, e => Assert.Equal("firewall", e.Action));
            Assert.Equal(["intent", "done", "intent", "done"], entries.OrderBy(e => e.TimeUtc).Select(e => e.Phase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_declined_admin_prompt_is_logged_as_not_changed()
    {
        var (log, path) = NewLog();
        try
        {
            var result = await FirewallChange.ApplyLoggedAsync(PlanAdd(), log, "manual: firewall",
                _ => Task.FromResult(new HelperRun(-1, true, null)), () => throw new InvalidOperationException("not read"));
            Assert.False(result.Success);
            Assert.Contains("declined", result.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Nothing_runs_if_the_action_log_cant_be_written()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ar-fw-{Guid.NewGuid():N}")).FullName;
        try
        {
            var ran = false;
            var result = await FirewallChange.ApplyLoggedAsync(PlanAdd(), new ActionLog(dir), "manual: firewall",
                _ => { ran = true; return Task.FromResult(new HelperRun(0, false, null)); }, () => FirewallStateTests.Status([]));
            Assert.False(ran);
            Assert.False(result.Success);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void The_helper_rejects_bad_arguments_before_touching_anything()
    {
        Assert.Equal(FirewallChange.ExitBadArguments, FirewallChange.RunHelper(["apply", "tcp:22:in", "tcp:22:in"]));
        Assert.Equal(FirewallChange.ExitBadArguments, FirewallChange.RunHelper(["apply", "tcp:22:in;netsh"]));
        Assert.Equal(FirewallChange.ExitBadArguments, FirewallChange.RunHelper(["allow", "tcp:22:in"]));
        Assert.Equal(FirewallChange.ExitBadArguments, FirewallChange.RunHelper([]));
    }

    [Fact]
    public void The_helper_needs_admin_to_change_anything()
    {
        if (WindowsFirewall.IsElevated()) return; // can't test the refusal from an elevated test run
        Assert.Equal(FirewallChange.ExitNotElevated, FirewallChange.RunHelper(["apply", "tcp:22:in"]));
        Assert.Equal(FirewallChange.ExitNotElevated, FirewallChange.RunHelper(["remove-all"]));
    }
}

public class FirewallRoundTripTests
{
    private static BlockRule R(FwProtocol p, int port, FwDirection d) => new(p, PortRange.Single(port), d);

    private static IReadOnlyList<PortChoice> Listed => FirewallPresets.All.SelectMany(p => p.Ports).ToList();

    /// <summary>What ticking the mapped rows would ask for.</summary>
    private static HashSet<BlockRule> Ticked(IReadOnlyList<BlockRule> present)
    {
        var (listed, custom) = FirewallSelection.Map(Listed, present);
        return Listed.Zip(listed).Where(x => x.Second is not null).SelectMany(x => (x.First with { Direction = x.Second!.Value }).Rules())
            .Concat(custom.SelectMany(c => c.Rules())).ToHashSet();
    }

    public static TheoryData<BlockRule[]> Cases => new()
    {
        Array.Empty<BlockRule>(),
        new[] { R(FwProtocol.Tcp, 22, FwDirection.In) },
        new[] { R(FwProtocol.Tcp, 22, FwDirection.In), R(FwProtocol.Tcp, 22, FwDirection.Out) },
        new[] { R(FwProtocol.Udp, 3389, FwDirection.In) },                                         // only half of the RDP row
        new[] { R(FwProtocol.Tcp, 3389, FwDirection.In), R(FwProtocol.Udp, 3389, FwDirection.Out) }, // mixed directions
        new[] { R(FwProtocol.Tcp, 7777, FwDirection.In), R(FwProtocol.Udp, 7777, FwDirection.Out) }, // custom, mixed
        new[] { R(FwProtocol.Tcp, 7777, FwDirection.In), R(FwProtocol.Udp, 7777, FwDirection.In) },
        new[] { R(FwProtocol.Udp, 22, FwDirection.In) },                                           // a protocol the row doesn't have
        new[] { new BlockRule(FwProtocol.Tcp, new PortRange(3000, 3005), FwDirection.In), R(FwProtocol.Tcp, 3000, FwDirection.Out) },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Reloading_never_widens_or_narrows_what_is_blocked(BlockRule[] present) =>
        Assert.True(Ticked(present).SetEquals(present));

    [Fact]
    public void Nothing_is_ticked_without_rules()
    {
        var (listed, custom) = FirewallSelection.Map(Listed, []);
        Assert.All(listed, Assert.Null);
        Assert.Empty(custom);
    }

    [Fact]
    public void A_partial_preset_row_stays_unticked()
    {
        var (listed, custom) = FirewallSelection.Map(Listed, [R(FwProtocol.Udp, 3389, FwDirection.In)]);
        var rdp = Listed.ToList().FindIndex(c => c.Ports.From == 3389);
        Assert.Null(listed[rdp]);
        Assert.Equal(new PortChoice("Custom port", PortRange.Single(3389), Protocols.Udp, Directions.Inbound), Assert.Single(custom));
    }
}

public class FirewallVerifyTests
{
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule Smb = new(FwProtocol.Tcp, PortRange.Single(445), FwDirection.In);

    [Fact]
    public async Task A_block_nobody_asked_for_left_after_applying_is_reported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-fw-{Guid.NewGuid():N}.jsonl");
        try
        {
            var plan = FirewallPlan.Create(FirewallStateTests.Status([]), [Ssh]);
            var result = await FirewallChange.ApplyLoggedAsync(plan, new ActionLog(path), "manual: firewall",
                _ => Task.FromResult(new HelperRun(FirewallChange.ExitPartial, false, null)),
                () => FirewallStateTests.Status([FirewallStateTests.Info(Ssh), FirewallStateTests.Info(Smb)]));
            Assert.False(result.Success);
            Assert.Contains("still there: TCP 445 incoming", result.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Helper_problems_are_mentioned_even_when_the_result_checks_out() =>
        Assert.Contains("helper reported problems", Run(FirewallChange.ExitPartial).Message);

    private static FirewallApplyResult Run(int exit)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-fw-{Guid.NewGuid():N}.jsonl");
        try
        {
            var plan = FirewallPlan.Create(FirewallStateTests.Status([]), [Ssh]);
            return FirewallChange.ApplyLoggedAsync(plan, new ActionLog(path), "manual: firewall",
                _ => Task.FromResult(new HelperRun(exit, false, null)),
                () => FirewallStateTests.Status([FirewallStateTests.Info(Ssh)])).GetAwaiter().GetResult();
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class FirewallApplyOrderTests
{
    private sealed class FakeRules(IEnumerable<FirewallRuleInfo> rules, bool complete = true) : IFirewallRules
    {
        public List<FirewallRuleInfo> Rules { get; } = rules.ToList();
        public List<string> Calls { get; } = [];
        public HashSet<string> FailAdd { get; } = [];
        public HashSet<string> FailRemove { get; } = [];
        public bool FailRename { get; set; }

        public (List<FirewallRuleInfo> Rules, bool Complete) Read() => (Rules.ToList(), complete);
        public (List<FirewallRuleInfo> Rules, bool Complete) ReadName(string name) =>
            (Rules.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList(), complete);

        public void Add(BlockRule rule, string name)
        {
            Calls.Add("add " + name);
            if (FailAdd.Contains(name)) throw new System.Runtime.InteropServices.COMException("denied");
            Rules.Add(FirewallStateTests.Info(rule) with { Name = name });
        }

        public void Rename(string from, string to)
        {
            Calls.Add($"rename {from} -> {to}");
            if (FailRename) throw new System.Runtime.InteropServices.COMException("denied");
            var i = Rules.FindIndex(r => string.Equals(r.Name, from, StringComparison.OrdinalIgnoreCase));
            Rules[i] = Rules[i] with { Name = to };
        }

        public void Remove(string name)
        {
            Calls.Add("remove " + name);
            if (FailRemove.Contains(name)) throw new System.Runtime.InteropServices.COMException("denied");
            var i = Rules.FindIndex(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)); // as Windows does
            if (i >= 0) Rules.RemoveAt(i);
        }
    }

    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule Range = new(FwProtocol.Tcp, new PortRange(22, 23), FwDirection.In);
    private static readonly BlockRule Smb = new(FwProtocol.Tcp, PortRange.Single(445), FwDirection.In);

    [Fact]
    public void Adds_before_it_removes()
    {
        var fake = new FakeRules([FirewallStateTests.Info(Ssh)]);
        Assert.Empty(WindowsFirewall.ApplyExactly(fake, [Range]));
        Assert.Equal(["add " + Range.Name, "remove " + Ssh.Name], fake.Calls);
    }

    [Fact]
    public void A_failed_add_keeps_every_existing_block()
    {
        var fake = new FakeRules([FirewallStateTests.Info(Ssh)]);
        fake.FailAdd.Add(Range.Name);
        var errors = WindowsFirewall.ApplyExactly(fake, [Range]);
        Assert.Contains(errors, e => e.Contains("Didn't unblock or replace anything"));
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("remove"));
        Assert.Contains(fake.Rules, r => r.Name == Ssh.Name);
    }

    [Fact]
    public void A_changed_rule_is_replaced_by_a_correct_block_added_first()
    {
        var fake = new FakeRules([FirewallStateTests.Info(Ssh) with { Action = 1 }]);
        Assert.Empty(WindowsFirewall.ApplyExactly(fake, [Ssh]));
        var temp = WindowsFirewall.TemporaryName(Ssh);
        Assert.Equal(["add " + temp, "remove " + Ssh.Name, $"rename {temp} -> {Ssh.Name}"], fake.Calls);
        Assert.Equal(FirewallStateTests.Info(Ssh), Assert.Single(fake.Rules));

        // The changed rule can't be removed: it stays as it was, the correct block stays next to it, nothing is unblocked.
        var stuck = new FakeRules([FirewallStateTests.Info(Ssh) with { Enabled = false }, FirewallStateTests.Info(Smb)]);
        stuck.FailRemove.Add(Ssh.Name);
        var errors = WindowsFirewall.ApplyExactly(stuck, [Ssh]);
        Assert.Contains(errors, e => e.Contains("Couldn't remove " + Ssh.Name));
        Assert.Equal(["add " + temp, "remove " + Ssh.Name], stuck.Calls); // SMB stays blocked too
        Assert.Contains(stuck.Rules, r => r.Name == temp && WindowsFirewall.Matches(r with { Name = Ssh.Name }, Ssh));
    }

    [Fact]
    public void A_failed_rename_still_leaves_the_port_blocked()
    {
        var fake = new FakeRules([FirewallStateTests.Info(Ssh) with { Enabled = false }]) { FailRename = true };
        var errors = WindowsFirewall.ApplyExactly(fake, [Ssh]);
        Assert.Contains(errors, e => e.Contains("still named"));
        var left = Assert.Single(fake.Rules);
        Assert.True(WindowsFirewall.Matches(left with { Name = Ssh.Name }, Ssh));

        // The next apply adds the block under its own name and takes the replacement out.
        fake.FailRename = false;
        Assert.Empty(WindowsFirewall.ApplyExactly(fake, [Ssh]));
        Assert.Equal(FirewallStateTests.Info(Ssh), Assert.Single(fake.Rules));
    }

    [Fact]
    public void Nothing_is_removed_when_some_rule_couldnt_be_identified()
    {
        var fake = new FakeRules([FirewallStateTests.Info(Ssh)], complete: false);
        var errors = WindowsFirewall.ApplyExactly(fake, []);
        Assert.Contains(errors, e => e.Contains("couldn't be read"));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Names_shared_with_rules_outside_the_group_are_never_added_restored_or_removed()
    {
        var foreign = FirewallStateTests.Info(Ssh) with { Grouping = "Someone else" };
        var fake = new FakeRules([FirewallStateTests.Info(Smb), foreign, FirewallStateTests.Info(Ssh) with { Enabled = false }]);
        var errors = WindowsFirewall.ApplyExactly(fake, [Smb]);
        Assert.Contains(errors, e => e.Contains("Left " + Ssh.Name));
        Assert.Empty(fake.Calls);
        Assert.Contains(foreign, fake.Rules);

        var adding = new FakeRules([foreign]);
        Assert.NotEmpty(WindowsFirewall.ApplyExactly(adding, [Ssh]));
        Assert.Empty(adding.Calls);
    }

    [Fact]
    public void Renamed_rules_in_the_group_are_removed_but_never_a_same_named_rule_outside_it()
    {
        var renamed = FirewallStateTests.Info(Ssh) with { Name = "my ssh block" };
        var fake = new FakeRules([renamed]);
        Assert.Empty(WindowsFirewall.ApplyExactly(fake, []));
        Assert.Equal(["remove my ssh block"], fake.Calls);

        var shared = new FakeRules([renamed, renamed with { Grouping = "Someone else" }]);
        Assert.NotEmpty(WindowsFirewall.ApplyExactly(shared, []));
        Assert.Empty(shared.Calls);
    }

    [Fact]
    public void Names_are_compared_ignoring_case_like_windows_does()
    {
        var foreignUpper = FirewallStateTests.Info(Ssh) with { Name = Ssh.Name.ToUpperInvariant(), Grouping = "Someone else" };
        var fake = new FakeRules([FirewallStateTests.Info(Ssh), foreignUpper]);
        Assert.Contains(WindowsFirewall.ApplyExactly(fake, []), e => e.Contains("Left " + Ssh.Name));
        Assert.Empty(fake.Calls);
        Assert.Contains(foreignUpper, fake.Rules);

        var ownLower = FirewallStateTests.Info(Ssh) with { Name = Ssh.Name.ToLowerInvariant() };
        var both = new FakeRules([FirewallStateTests.Info(Ssh), ownLower]);
        Assert.Empty(WindowsFirewall.ApplyExactly(both, [])); // both are its own: removing by name takes both, nothing else
        Assert.Empty(both.Rules);

        // Only a copy renamed in capitals exists: it is replaced, with the correct block in place first.
        var onlyLower = new FakeRules([ownLower]);
        Assert.Empty(WindowsFirewall.ApplyExactly(onlyLower, [Ssh]));
        var temp = WindowsFirewall.TemporaryName(Ssh);
        Assert.Equal(["add " + temp, "remove " + Ssh.Name, $"rename {temp} -> {Ssh.Name}"], onlyLower.Calls);
        Assert.Equal(Ssh.Name, Assert.Single(onlyLower.Rules).Name);
        var lowerStatus = FirewallStateTests.Status([ownLower]);
        Assert.Equal(RuleState.Changed, lowerStatus.StateOf(Ssh)); // shown as changed, restored by the next apply
        Assert.Empty(lowerStatus.Renamed());

        var adding = new FakeRules([foreignUpper]);
        Assert.NotEmpty(WindowsFirewall.ApplyExactly(adding, [Ssh]));
        Assert.Empty(adding.Calls);
    }

    [Fact]
    public void Never_creates_anything_but_block_rules_in_its_group()
    {
        var fake = new FakeRules([]);
        WindowsFirewall.ApplyExactly(fake, [Ssh, Smb, Ssh with { Direction = FwDirection.Out }]);
        Assert.All(fake.Rules, r => Assert.Equal((0, BlockRule.Group), (r.Action, r.Grouping)));
        Assert.Equal(3, fake.Rules.Count);
        Assert.Empty(WindowsFirewall.ApplyExactly(fake, [Ssh, Smb, Ssh with { Direction = FwDirection.Out }])); // idempotent
        Assert.Equal(3, fake.Calls.Count);
    }
}

public class FirewallRound26Tests
{
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule RdpUdpIn = new(FwProtocol.Udp, PortRange.Single(3389), FwDirection.In);
    private static readonly BlockRule RdpTcpIn = new(FwProtocol.Tcp, PortRange.Single(3389), FwDirection.In);
    private static readonly BlockRule RdpUdpOut = new(FwProtocol.Udp, PortRange.Single(3389), FwDirection.Out);

    private static string Describe(SocketSnapshot sockets, params BlockRule[] rules) =>
        FirewallSelection.Describe(PortRange.Single(3389), Protocols.Both,
            FirewallStateTests.Status(rules.Select(FirewallStateTests.Info)), sockets, pid => $"proc{pid}");

    private static readonly SocketSnapshot TcpListener = new([new ListeningSocket(FwProtocol.Tcp, 3389, 5, false)], [], true);

    [Fact]
    public void Partial_protocol_coverage_is_never_called_blocked()
    {
        var udpOnly = Describe(TcpListener, RdpUdpIn);
        Assert.StartsWith("Partly blocked: UDP 3389 incoming", udpOnly);
        Assert.Contains("Open: proc5 listening on the network", udpOnly); // TCP is still reachable

        Assert.StartsWith("Partly blocked", Describe(TcpListener, RdpTcpIn, RdpUdpOut));
        Assert.DoesNotContain("Open", Describe(TcpListener, RdpTcpIn, RdpUdpOut));

        Assert.StartsWith("Blocked (incoming)", Describe(TcpListener, RdpTcpIn, RdpUdpIn));
        Assert.Equal("Not in use", Describe(new SocketSnapshot([], [], true)));
    }

    [Fact]
    public async Task An_incomplete_read_back_is_never_reported_as_success()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-fw-{Guid.NewGuid():N}.jsonl");
        try
        {
            var plan = FirewallPlan.Create(FirewallStateTests.Status([FirewallStateTests.Info(Ssh)]), []);
            var result = await FirewallChange.ApplyLoggedAsync(plan, new ActionLog(path), "manual: firewall remove all",
                _ => Task.FromResult(new HelperRun(FirewallChange.ExitPartial, false, null)),
                () => FirewallStateTests.Status([]) with { Complete = false });
            Assert.False(result.Success);
            Assert.Contains("couldn't read the firewall back completely", result.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Renamed_rules_in_the_group_are_seen_planned_for_removal_and_verified()
    {
        var renamed = FirewallStateTests.Info(Ssh) with { Name = "my ssh block" };
        var status = FirewallStateTests.Status([renamed]);
        Assert.True(status.HasAny);
        Assert.Empty(status.Present());
        Assert.Equal(["my ssh block"], status.Renamed());

        var plan = FirewallPlan.Create(status, []);
        Assert.False(plan.IsEmpty);
        Assert.Equal(["my ssh block"], plan.RemoveRenamed);

        var path = Path.Combine(Path.GetTempPath(), $"ar-fw-{Guid.NewGuid():N}.jsonl");
        try
        {
            var result = await FirewallChange.ApplyLoggedAsync(plan, new ActionLog(path), "manual: firewall remove all",
                _ => Task.FromResult(new HelperRun(0, false, null)), () => status);
            Assert.False(result.Success);
            Assert.Contains("my ssh block", result.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class FirewallBypassTests
{
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);

    [Fact]
    public void Authenticated_bypass_rules_are_recognised_and_qualify_blocked()
    {
        var allow = FirewallStateTests.Info(Ssh) with { Name = "IPsec admin", Grouping = "", Action = 1 };
        Assert.True(WindowsFirewall.IsAuthenticatedBypass(allow with { Restrictions = "remote machines, IPsec" }));
        Assert.True(WindowsFirewall.IsAuthenticatedBypass(allow with { Restrictions = "remote users, IPsec" }));
        Assert.True(WindowsFirewall.IsAuthenticatedBypass(allow with { Restrictions = "IPsec", Direction = 2 })); // outgoing: no lists needed
        Assert.False(WindowsFirewall.IsAuthenticatedBypass(allow with { Restrictions = "remote machines" }));
        Assert.False(WindowsFirewall.IsAuthenticatedBypass(allow with { Restrictions = "remote machines, IPsec", Enabled = false }));
        Assert.False(WindowsFirewall.IsAuthenticatedBypass(allow with { Restrictions = "remote machines, IPsec", Action = 0 }));

        var status = FirewallStateTests.Status([FirewallStateTests.Info(Ssh)]) with { BypassRules = ["IPsec admin"] };
        var text = FirewallSelection.Describe(PortRange.Single(22), Protocols.Tcp, status, new SocketSnapshot([], [], true), p => "x");
        Assert.StartsWith("Blocked (incoming) (except authenticated-bypass traffic)", text);
    }
}

/// <summary>Real COM rule objects that are never added to the firewall (no admin needed, nothing changes).</summary>
public class FirewallDetachedComTests
{
    private static readonly BlockRule SshIn = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule SshOut = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.Out);
    private static readonly BlockRule UdpRange = new(FwProtocol.Udp, new PortRange(137, 139), FwDirection.In);

    [Fact]
    public void A_new_rule_matches_what_it_was_created_for()
    {
        foreach (var rule in new[] { SshIn, SshOut, UdpRange })
        {
            var created = WindowsFirewall.CreateRule(rule);
            Assert.True(WindowsFirewall.Matches(WindowsFirewall.ReadOne(created, rule.Name, BlockRule.Group), rule), rule.Name);
        }
    }
}

public class FirewallRound32Tests
{
    private static readonly BlockRule Rdp = new(FwProtocol.Tcp, PortRange.Single(3389), FwDirection.In);
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);

    [Fact]
    public void Removing_a_rule_turned_into_the_rdp_allow_is_refused_from_a_remote_session()
    {
        var allowRdp = FirewallStateTests.Info(Rdp) with { Action = 1 };
        var status = FirewallStateTests.Status([allowRdp]);
        var plan = FirewallPlan.Create(status, []);
        Assert.Equal([allowRdp], plan.Touched(status));

        Assert.Contains(FirewallSafety.CheckRevocations(plan.Touched(status), remoteSession: true, rdpPort: 3389), i => i.Blocking);
        var local = FirewallSafety.CheckRevocations(plan.Touched(status), remoteSession: false, rdpPort: 3389);
        Assert.Contains(local, i => !i.Blocking && i.Text.Contains("allows traffic"));
    }

    [Fact]
    public void Restoring_an_ssh_named_rule_that_now_allows_rdp_is_refused_from_a_remote_session()
    {
        var sneaky = FirewallStateTests.Info(Ssh) with { Action = 1, LocalPorts = "3389" };
        var status = FirewallStateTests.Status([sneaky]);
        var plan = FirewallPlan.Create(status, [Ssh]);
        Assert.Equal([Ssh], plan.Repair);
        Assert.Contains(FirewallSafety.CheckRevocations(plan.Touched(status), true, 3389), i => i.Blocking);
    }

    [Fact]
    public void Plain_block_rules_and_unrelated_allows_are_judged_correctly()
    {
        var status = FirewallStateTests.Status([FirewallStateTests.Info(Ssh)]);
        Assert.Empty(FirewallSafety.CheckRevocations(FirewallPlan.Create(status, []).Touched(status), true, 3389));

        var allow22 = FirewallStateTests.Info(Ssh) with { Action = 1 };
        Assert.DoesNotContain(FirewallSafety.CheckRevocations([allow22], true, 3389), i => i.Blocking);
        Assert.Contains(FirewallSafety.CheckRevocations([allow22 with { LocalPorts = "RPC" }], true, 3389), i => i.Blocking); // not understood: assume it might
        Assert.Contains(FirewallSafety.CheckRevocations([allow22 with { LocalPorts = "80, 3000-4000" }], true, 3389), i => i.Blocking);
        Assert.DoesNotContain(FirewallSafety.CheckRevocations([allow22 with { LocalPorts = "3389", Enabled = false }], true, 3389), i => i.Blocking);
    }

    [Fact]
    public void Only_one_firewall_change_runs_at_a_time()
    {
        var name = $@"Local\AgentReclaim.Firewall.Test.{Guid.NewGuid():N}";
        // Each attempt on its own thread, like separate helpers (a mutex is re-entrant for the thread holding it).
        static bool OnOtherThread(string name, TimeSpan wait)
        {
            var got = false;
            var thread = new Thread(() =>
            {
                using var held = FirewallChange.TryLock(wait, name);
                got = held is not null;
            });
            thread.Start();
            thread.Join();
            return got;
        }

        var first = FirewallChange.TryLock(TimeSpan.Zero, name);
        Assert.NotNull(first);
        Assert.False(OnOtherThread(name, TimeSpan.FromMilliseconds(200)));
        first!.Dispose();
        Assert.True(OnOtherThread(name, TimeSpan.FromSeconds(2)));
    }
}

public class FirewallRound33Tests
{
    private static readonly BlockRule Rdp = new(FwProtocol.Tcp, PortRange.Single(3389), FwDirection.In);
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);

    [Fact]
    public void The_lock_is_machine_wide() => Assert.StartsWith(@"Global\", FirewallChange.LockName);

    [Fact]
    public void The_helper_itself_refuses_to_revoke_a_possible_rdp_allow_from_a_remote_session()
    {
        var allowRdp = FirewallStateTests.Info(Rdp) with { Action = 1 };
        var store = new FirewallTestStore([allowRdp]);
        var errors = WindowsFirewall.ApplyExactly(store, [], remoteSession: true, rdpPort: 3389); // what remove-all (and the uninstaller) does
        Assert.Contains(errors, e => e.Contains("Remote Desktop"));
        Assert.Empty(store.Calls);

        var sneaky = new FirewallTestStore([FirewallStateTests.Info(Ssh) with { Action = 1, LocalPorts = "3389" }]);
        Assert.NotEmpty(WindowsFirewall.ApplyExactly(sneaky, [Ssh], remoteSession: true, rdpPort: 3389));
        Assert.Empty(sneaky.Calls); // not restored either

        var local = new FirewallTestStore([allowRdp]);
        Assert.Empty(WindowsFirewall.ApplyExactly(local, [], remoteSession: false, rdpPort: 3389));
        Assert.Single(local.Calls);
    }

    [Fact]
    public void Rules_with_unreadable_settings_are_left_alone_from_a_remote_session()
    {
        var unreadable = new FirewallRuleInfo(Rdp.Name, BlockRule.Group, false, -1, -1, -1, "", "", 0, "", "", "", "", "")
            { Restrictions = "settings couldn't be read" };
        Assert.Contains(FirewallSafety.CheckRevocations([unreadable], true, 3389), i => i.Blocking);
        Assert.Contains(FirewallSafety.CheckRevocations([unreadable], false, 3389), i => !i.Blocking);

        var store = new FirewallTestStore([unreadable]);
        Assert.NotEmpty(WindowsFirewall.ApplyExactly(store, [], remoteSession: true, rdpPort: 3389));
        Assert.Empty(store.Calls);
    }
}

/// <summary>An in-memory rule store that removes and renames by name ignoring case, as Windows does.</summary>
internal sealed class FirewallTestStore(IEnumerable<FirewallRuleInfo> rules) : IFirewallRules
{
    public List<FirewallRuleInfo> Rules { get; } = rules.ToList();
    public List<string> Calls { get; } = [];
    public HashSet<string> FailRemove { get; } = new(StringComparer.OrdinalIgnoreCase);
    public (List<FirewallRuleInfo> Rules, bool Complete) Read() => (Rules.ToList(), true);
    public (List<FirewallRuleInfo> Rules, bool Complete) ReadName(string name) =>
        (Rules.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList(), true);
    public HashSet<string> FailAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SilentlyDropAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Runs after each add: another program changing the firewall meanwhile.</summary>
    public Action<FirewallTestStore>? AfterAdd { get; init; }

    public void Add(BlockRule rule, string name)
    {
        Calls.Add("add " + name);
        if (FailAdd.Contains(name)) throw new System.Runtime.InteropServices.COMException("denied");
        if (!SilentlyDropAdd.Contains(name)) Rules.Add(FirewallStateTests.Info(rule) with { Name = name });
        AfterAdd?.Invoke(this);
    }

    public void Rename(string from, string to)
    {
        Calls.Add($"rename {from} -> {to}");
        var i = Rules.FindIndex(r => string.Equals(r.Name, from, StringComparison.OrdinalIgnoreCase));
        Rules[i] = Rules[i] with { Name = to };
    }

    public void Remove(string name)
    {
        Calls.Add("remove " + name);
        if (FailRemove.Contains(name)) throw new System.Runtime.InteropServices.COMException("denied");
        var i = Rules.FindIndex(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Rules.RemoveAt(i);
    }
}

public class FirewallRound34Tests
{
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule Smb = new(FwProtocol.Tcp, PortRange.Single(445), FwDirection.In);

    [Fact]
    public void The_helper_refuses_blocking_its_own_reading_of_the_rdp_port()
    {
        var moved = new BlockRule(FwProtocol.Tcp, PortRange.Single(3390), FwDirection.In);
        var store = new FirewallTestStore([]);
        var errors = WindowsFirewall.Apply(store, new FirewallRequest([moved], new HashSet<string>()), remoteSession: true, rdpPort: 3390);
        Assert.Contains(errors, e => e.Contains("Remote Desktop"));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public void Only_approved_removals_happen_so_a_block_added_meanwhile_stays()
    {
        var store = new FirewallTestStore([FirewallStateTests.Info(Ssh), FirewallStateTests.Info(Smb)]); // SMB added by another session
        var errors = WindowsFirewall.Apply(store, new FirewallRequest([Ssh], new HashSet<string>()));
        Assert.Contains(errors, e => e.Contains("appeared or changed after"));
        Assert.Empty(store.Calls);

        var approved = new FirewallTestStore([FirewallStateTests.Info(Ssh), FirewallStateTests.Info(Smb)]);
        var request = new FirewallRequest([Ssh], new HashSet<string> { FirewallRequest.Fingerprint([FirewallStateTests.Info(Smb)]) });
        Assert.Empty(WindowsFirewall.Apply(approved, request));
        Assert.Equal(["remove " + Smb.Name], approved.Calls);
    }

    [Fact]
    public void The_request_round_trips_through_the_command_line_and_rejects_anything_odd()
    {
        var renamed = FirewallStateTests.Info(Ssh) with { Name = "renamed" };
        var plan = FirewallPlan.Create(FirewallStateTests.Status([FirewallStateTests.Info(Smb), renamed]), [Ssh]);
        var request = FirewallRequest.From(plan);
        var fingerprints = new[] { FirewallRequest.Fingerprint([FirewallStateTests.Info(Smb)]), FirewallRequest.Fingerprint([renamed]) }
            .Order(StringComparer.Ordinal);
        Assert.Equal("firewall apply tcp:22:in --remove " + string.Join(' ', fingerprints), request.Arguments);

        var parsed = FirewallRequest.Parse(request.Arguments.Split(' ')[1..], out var error);
        Assert.Null(error);
        Assert.Equal(request.Desired, parsed!.Desired);
        Assert.True(parsed.Approved!.SetEquals(request.Approved!));

        Assert.Same(FirewallRequest.RemoveEverything, FirewallRequest.Parse(["remove-all"], out _));
        Assert.Null(FirewallRequest.Parse(["apply", "--remove", "0123456789abcdef", "--remove", "0123456789abcdee"], out _)); // twice
        Assert.Null(FirewallRequest.Parse(["apply", "--remove", "tcp:22:in"], out _));                     // not a fingerprint
        Assert.Null(FirewallRequest.Parse(["apply", "--remove", "0123456789ABCDEF"], out _));              // not lower-case hex
        Assert.Null(FirewallRequest.Parse(["apply", "--remove", "0123456789abcdef", "0123456789abcdef"], out _)); // duplicate
        Assert.Null(FirewallRequest.Parse(["apply", "anything"], out _));
        Assert.Null(FirewallRequest.Parse(["allow", "tcp:22:in"], out _));

        var onlyRemove = FirewallRequest.Parse(["apply", "--remove", FirewallRequest.Fingerprint([FirewallStateTests.Info(Smb)])], out _);
        Assert.Empty(onlyRemove!.Desired);
        Assert.True(onlyRemove.MayRemove([FirewallStateTests.Info(Smb)]));
        Assert.False(onlyRemove.MayRemove([FirewallStateTests.Info(Ssh)]));
    }
}

public class FirewallRound35Tests
{
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);

    [Fact]
    public void Only_the_renamed_rules_that_were_approved_are_removed()
    {
        var seen = FirewallStateTests.Info(Ssh) with { Name = "seen" };
        var store = new FirewallTestStore([FirewallStateTests.Info(Ssh), seen, FirewallStateTests.Info(Ssh) with { Name = "appeared later" }]);
        var errors = WindowsFirewall.Apply(store, new FirewallRequest([Ssh], new HashSet<string> { FirewallRequest.Fingerprint([seen]) }));
        Assert.Equal(["remove seen"], store.Calls);
        Assert.Contains(errors, e => e.Contains("appeared later") && e.Contains("appeared or changed after"));
    }

    [Fact]
    public void A_foreign_rule_sharing_the_temporary_name_is_never_deleted()
    {
        var foreignTemp = FirewallStateTests.Info(Ssh) with { Name = WindowsFirewall.TemporaryName(Ssh), Grouping = "Someone else", Action = 1 };
        var store = new FirewallTestStore([FirewallStateTests.Info(Ssh) with { Enabled = false }, foreignTemp]);
        WindowsFirewall.ApplyExactly(store, [Ssh]);
        Assert.DoesNotContain(store.Calls, c => c == "remove " + WindowsFirewall.TemporaryName(Ssh));
        Assert.Contains(foreignTemp, store.Rules);
        Assert.Contains("add " + WindowsFirewall.TemporaryName(Ssh, 2), store.Calls);
    }

    [Fact]
    public void A_disabled_leftover_replacement_is_not_trusted_and_a_fresh_one_protects_the_port()
    {
        var store = new FirewallTestStore([FirewallStateTests.Info(Ssh) with { Enabled = false },
            FirewallStateTests.Info(Ssh) with { Name = WindowsFirewall.TemporaryName(Ssh), Enabled = false }]);
        store.FailRemove.Add(Ssh.Name);
        var errors = WindowsFirewall.ApplyExactly(store, [Ssh]);
        Assert.Contains("add " + WindowsFirewall.TemporaryName(Ssh, 2), store.Calls);
        Assert.Contains(store.Rules, r => r.Name == WindowsFirewall.TemporaryName(Ssh, 2) && r.Enabled);
        Assert.NotEmpty(errors);

        store.FailRemove.Clear();
        Assert.Empty(WindowsFirewall.ApplyExactly(store, [Ssh]));
        Assert.Equal(FirewallStateTests.Info(Ssh), Assert.Single(store.Rules));
    }
}

public class FirewallRound36Tests
{
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule Smb = new(FwProtocol.Tcp, PortRange.Single(445), FwDirection.In);

    [Fact]
    public void A_same_named_rule_added_after_approval_is_not_removed()
    {
        var renamed = FirewallStateTests.Info(Ssh) with { Name = "my block" };
        var request = FirewallRequest.From(FirewallPlan.Create(FirewallStateTests.Status([renamed]), []));

        // While the admin prompt was up, someone added an SMB block under the same name.
        var store = new FirewallTestStore([renamed, FirewallStateTests.Info(Smb) with { Name = "my block" }]);
        var errors = WindowsFirewall.Apply(store, request);
        Assert.Contains(errors, e => e.Contains("appeared or changed after"));
        Assert.Empty(store.Calls);

        // Changed rather than added: the same.
        var changed = new FirewallTestStore([renamed with { LocalPorts = "22,445" }]);
        Assert.NotEmpty(WindowsFirewall.Apply(changed, request));
        Assert.Empty(changed.Calls);

        var unchanged = new FirewallTestStore([renamed]);
        Assert.Empty(WindowsFirewall.Apply(unchanged, request));
        Assert.Equal(["remove my block"], unchanged.Calls);
    }

    [Fact]
    public void A_broadened_rule_keeps_all_it_covered_until_its_replacement_is_in_and_it_can_go()
    {
        var broadened = FirewallStateTests.Info(Ssh) with { LocalPorts = "22,445" };
        var request = FirewallRequest.From(FirewallPlan.Create(FirewallStateTests.Status([broadened]), [Ssh]));

        var store = new FirewallTestStore([broadened]);
        store.FailRemove.Add(Ssh.Name);
        var errors = WindowsFirewall.Apply(store, request);
        Assert.NotEmpty(errors);
        Assert.Contains(broadened, store.Rules); // 445 still blocked, never edited or switched off
        Assert.Contains(store.Rules, r => r.Name == WindowsFirewall.TemporaryName(Ssh) && WindowsFirewall.Matches(r with { Name = Ssh.Name }, Ssh));

        var works = new FirewallTestStore([broadened]);
        Assert.Empty(WindowsFirewall.Apply(works, request));
        Assert.Equal(["add " + WindowsFirewall.TemporaryName(Ssh), "remove " + Ssh.Name, $"rename {WindowsFirewall.TemporaryName(Ssh)} -> {Ssh.Name}"], works.Calls);
        Assert.Equal(FirewallStateTests.Info(Ssh), Assert.Single(works.Rules));
    }

    [Fact]
    public void A_repair_is_refused_if_the_rule_changed_again_after_approval()
    {
        var disabled = FirewallStateTests.Info(Ssh) with { Enabled = false };
        var request = FirewallRequest.From(FirewallPlan.Create(FirewallStateTests.Status([disabled]), [Ssh]));
        var store = new FirewallTestStore([disabled with { LocalPorts = "22,445" }]);
        var errors = WindowsFirewall.Apply(store, request);
        Assert.Contains(errors, e => e.Contains("appeared or changed after"));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public void A_fingerprint_covers_every_copy_and_setting_but_not_their_order()
    {
        var a = FirewallStateTests.Info(Ssh);
        var b = a with { Enabled = false };
        Assert.Equal(FirewallRequest.Fingerprint([a, b]), FirewallRequest.Fingerprint([b, a]));
        Assert.NotEqual(FirewallRequest.Fingerprint([a]), FirewallRequest.Fingerprint([a, a]));
        Assert.NotEqual(FirewallRequest.Fingerprint([a]), FirewallRequest.Fingerprint([a with { Restrictions = "IPsec" }]));
        Assert.NotEqual(FirewallRequest.Fingerprint([a]), FirewallRequest.Fingerprint([a with { Name = a.Name.ToUpperInvariant() }]));
    }

    [Fact]
    public void The_confirmation_can_say_what_a_changed_rule_does_now()
    {
        var text = FirewallSafety.DescribeActual(FirewallStateTests.Info(Ssh) with { LocalPorts = "22,445", Enabled = false });
        Assert.Equal("switched off: blocks TCP incoming on port 22,445", text);
    }
}

public class FirewallRound37Tests
{
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule Smb = new(FwProtocol.Tcp, PortRange.Single(445), FwDirection.In);

    [Fact]
    public void Restriction_values_are_read_and_change_the_fingerprint()
    {
        var a = FirewallStateTests.Info(Ssh) with { Restrictions = "interfaces", Details = "interfaces=Ethernet" };
        Assert.NotEqual(FirewallRequest.Fingerprint([a]), FirewallRequest.Fingerprint([a with { Details = "interfaces=Wi-Fi" }]));

        // A real (detached) rule object: the values themselves are read, and a fresh rule reads completely.
        dynamic r = WindowsFirewall.CreateRule(Ssh);
        var fresh = WindowsFirewall.ReadOne(r, Ssh.Name, BlockRule.Group);
        Assert.False(fresh.PartlyUnreadable);
        Assert.True(WindowsFirewall.Matches(fresh, Ssh));
        r.LocalUserOwner = "S-1-5-21-1-2-3-1001";
        var owned = WindowsFirewall.ReadOne(r, Ssh.Name, BlockRule.Group);
        Assert.Contains("S-1-5-21-1-2-3-1001", owned.Details);
        Assert.NotEqual(FirewallRequest.Fingerprint([fresh]), FirewallRequest.Fingerprint([owned]));
    }

    [Fact]
    public void A_failed_addition_keeps_a_broadened_rule_whole_even_when_its_replacement_went_in()
    {
        var broadened = FirewallStateTests.Info(Ssh) with { LocalPorts = "22,445" };
        var request = FirewallRequest.From(FirewallPlan.Create(FirewallStateTests.Status([broadened]), [Ssh, Smb]));
        var store = new FirewallTestStore([broadened]);
        store.FailAdd.Add(Smb.Name);
        var errors = WindowsFirewall.Apply(store, request);
        Assert.Contains(errors, e => e.Contains("Didn't unblock or replace anything"));
        Assert.Contains(broadened, store.Rules); // 445 still blocked by it
        Assert.DoesNotContain(store.Calls, c => c.StartsWith("remove") || c.StartsWith("rename"));
    }

    [Fact]
    public void An_addition_that_doesnt_read_back_stops_every_removal()
    {
        var request = FirewallRequest.From(FirewallPlan.Create(FirewallStateTests.Status([FirewallStateTests.Info(Ssh)]), [Smb]));
        var store = new FirewallTestStore([FirewallStateTests.Info(Ssh)]);
        store.SilentlyDropAdd.Add(Smb.Name);
        Assert.Contains(WindowsFirewall.Apply(store, request), e => e.Contains("isn't in place"));
        Assert.DoesNotContain(store.Calls, c => c.StartsWith("remove"));
    }

    [Fact]
    public void A_rule_changed_by_another_program_during_the_apply_is_not_removed()
    {
        var request = FirewallRequest.From(FirewallPlan.Create(FirewallStateTests.Status([FirewallStateTests.Info(Ssh)]), [Smb]));
        var store = new FirewallTestStore([FirewallStateTests.Info(Ssh)])
        {
            // While SMB is being added, someone broadens the approved SSH block to 22 and 445.
            AfterAdd = s => s.Rules[0] = s.Rules[0] with { LocalPorts = "22,445" },
        };
        var errors = WindowsFirewall.Apply(store, request);
        Assert.Contains(errors, e => e.Contains("appeared or changed after"));
        Assert.DoesNotContain(store.Calls, c => c.StartsWith("remove"));
    }

    [Fact]
    public void A_repair_whose_original_changes_during_the_apply_keeps_the_original()
    {
        var disabled = FirewallStateTests.Info(Ssh) with { Enabled = false };
        var request = FirewallRequest.From(FirewallPlan.Create(FirewallStateTests.Status([disabled]), [Ssh]));
        var store = new FirewallTestStore([disabled]) { AfterAdd = s => s.Rules[0] = s.Rules[0] with { LocalPorts = "22,445" } };
        Assert.NotEmpty(WindowsFirewall.Apply(store, request));
        Assert.Contains(store.Rules, r => r.Name == Ssh.Name && r.LocalPorts == "22,445");
        Assert.DoesNotContain(store.Calls, c => c.StartsWith("remove") || c.StartsWith("rename"));
    }

    [Fact]
    public void Rules_with_unreadable_settings_are_never_removed_even_locally()
    {
        var unreadable = FirewallStateTests.Info(Ssh) with { PartlyUnreadable = true, Details = "owner=?" };
        var store = new FirewallTestStore([unreadable]);
        Assert.Contains(WindowsFirewall.ApplyExactly(store, [], remoteSession: false), e => e.Contains("couldn't be read"));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public void The_confirmation_says_what_removals_really_take_away()
    {
        var broadened = FirewallStateTests.Info(Ssh) with { LocalPorts = "22,445" };
        var renamed = FirewallStateTests.Info(Smb) with { Name = "my smb block" };
        var status = FirewallStateTests.Status([broadened, renamed]);
        var lines = FirewallPlan.Create(status, []).RemovalLines(status);
        Assert.Contains(lines, l => l.StartsWith("Unblock ") && l.Contains("blocks TCP incoming on port 22,445"));
        Assert.Contains(lines, l => l.Contains("\"my smb block\"") && l.Contains("blocks TCP incoming on port 445"));

        var plain = FirewallStateTests.Status([FirewallStateTests.Info(Ssh)]);
        Assert.Equal(["Unblock " + Ssh.Describe()], FirewallPlan.Create(plain, []).RemovalLines(plain));
    }
}

public class FirewallRound38Tests
{
    private static readonly BlockRule Ssh = new(FwProtocol.Tcp, PortRange.Single(22), FwDirection.In);
    private static readonly BlockRule Smb = new(FwProtocol.Tcp, PortRange.Single(445), FwDirection.In);

    /// <summary>A store where another program changes the firewall right after a given removal.</summary>
    private sealed class RacingStore(IEnumerable<FirewallRuleInfo> rules, int afterRemoval, Action<List<FirewallRuleInfo>> change) : IFirewallRules
    {
        private int _removals;
        public List<FirewallRuleInfo> Rules { get; } = rules.ToList();
        public List<string> Calls { get; } = [];
        public (List<FirewallRuleInfo> Rules, bool Complete) Read() => (Rules.ToList(), true);
        public (List<FirewallRuleInfo> Rules, bool Complete) ReadName(string name) =>
            (Rules.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList(), true);
        public void Add(BlockRule rule, string name) { Calls.Add("add " + name); Rules.Add(FirewallStateTests.Info(rule) with { Name = name }); }
        public void Rename(string from, string to) => throw new NotSupportedException();

        public void Remove(string name)
        {
            Calls.Add("remove " + name);
            Rules.RemoveAt(Rules.FindIndex(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (++_removals == afterRemoval) change(Rules);
        }
    }

    [Fact]
    public void A_surviving_copy_changed_between_deletions_is_left_alone()
    {
        var ssh = FirewallStateTests.Info(Ssh);
        var request = FirewallRequest.From(FirewallPlan.Create(FirewallStateTests.Status([ssh, ssh]), []));
        // After the first copy goes, the other is turned into an allow for Remote Desktop.
        var store = new RacingStore([ssh, ssh], 1, rules => rules[0] = rules[0] with { Action = 1, LocalPorts = "3389" });
        var errors = WindowsFirewall.Apply(store, request, remoteSession: true, rdpPort: 3389);
        Assert.Contains(errors, e => e.Contains("changed while it was being removed"));
        Assert.Single(store.Calls, c => c.StartsWith("remove"));
        Assert.Contains(store.Rules, r => r.Action == 1 && r.LocalPorts == "3389");

        // Unchanged duplicates both go.
        var plain = new RacingStore([ssh, ssh], 99, _ => { });
        Assert.Empty(WindowsFirewall.Apply(plain, request));
        Assert.Empty(plain.Rules);
    }

    [Fact]
    public void Removals_stop_when_a_wanted_block_disappears_meanwhile()
    {
        var wide = FirewallStateTests.Info(Ssh) with { Name = "wide", LocalPorts = "22-445" };
        var other = FirewallStateTests.Info(Ssh) with { Name = "other" };
        var status = FirewallStateTests.Status([FirewallStateTests.Info(Smb), other, wide]);
        var request = FirewallRequest.From(FirewallPlan.Create(status, [Smb]));
        // After the first removal, someone switches off the SMB block AgentReclaim wants in place.
        var store = new RacingStore([FirewallStateTests.Info(Smb), other, wide], 1,
            rules => rules[0] = rules[0] with { Enabled = false });
        var errors = WindowsFirewall.Apply(store, request);
        Assert.Contains(errors, e => e.Contains("isn't in place any more"));
        Assert.Single(store.Calls, c => c.StartsWith("remove"));
        Assert.Contains(wide, store.Rules); // still covering 445
    }
}

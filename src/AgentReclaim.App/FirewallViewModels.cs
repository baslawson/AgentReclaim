using System.Collections.ObjectModel;
using AgentReclaim.Core.Firewall;

namespace AgentReclaim.App;

/// <summary>A port (or range) in the Firewall tab: tick it to block it.</summary>
public sealed class FirewallRow(string group, string service, PortRange ports, Protocols protocols, Directions direction, bool isCustom)
    : ObservableObject
{
    private bool _isChecked;
    private Directions _direction = direction;
    private string _status = "";

    public static readonly string[] DirectionNames = ["Incoming", "Outgoing", "Both"];

    public string Group { get; } = group;
    public string Service { get; } = service;
    public PortRange Ports { get; } = ports;
    public Protocols Protocols { get; } = protocols;
    public bool IsCustom { get; } = isCustom;

    public string PortsText => Ports.ToString();
    public string ProtocolText => Protocols switch { Protocols.Tcp => "TCP", Protocols.Udp => "UDP", _ => "TCP+UDP" };

    /// <summary>Raised when the tick or the direction changes.</summary>
    public event Action? Changed;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            Raise();
            Changed?.Invoke();
        }
    }

    public Directions Direction
    {
        get => _direction;
        set
        {
            if (_direction == value) return;
            _direction = value;
            Raise();
            Raise(nameof(DirectionIndex));
            Changed?.Invoke();
        }
    }

    public int DirectionIndex
    {
        get => (int)_direction;
        set { if (value is >= 0 and <= 2) Direction = (Directions)value; }
    }

    public string Status { get => _status; set { _status = value; Raise(); } }

    public PortChoice Choice => new(Service, Ports, Protocols, Direction);

    /// <summary>Sets the tick and direction from the firewall without raising <see cref="Changed"/>.</summary>
    internal void Reset(bool isChecked, Directions direction)
    {
        _isChecked = isChecked;
        _direction = direction;
        Raise(nameof(IsChecked));
        Raise(nameof(Direction));
        Raise(nameof(DirectionIndex));
    }

    public bool Covers(BlockRule rule) =>
        rule.Ports == Ports && Protocols.HasFlag(rule.Protocol == FwProtocol.Tcp ? Protocols.Tcp : Protocols.Udp);
}

/// <summary>A preset's tick box: ticked if all its ports are, half if some are.</summary>
public sealed class PresetToggle(FirewallPreset preset, IReadOnlyList<FirewallRow> rows) : ObservableObject
{
    public string Name => preset.Name;
    public string Why => $"{preset.Why}\n{string.Join(", ", rows.Select(r => $"{r.Service} {r.PortsText}"))}";

    public bool? IsChecked
    {
        get => rows.All(r => r.IsChecked) ? true : rows.Any(r => r.IsChecked) ? null : false;
        set
        {
            var tick = value == true;
            foreach (var row in rows) row.IsChecked = tick;
            Raise();
        }
    }

    public void Refresh() => Raise(nameof(IsChecked));
}

public sealed class FirewallViewModel : ObservableObject
{
    private FirewallStatus? _status;
    private string _summary = "Reading Windows Firewall…";
    private string _pending = "";
    private bool _isBusy;
    private bool _hasChanges;

    public ObservableCollection<FirewallRow> Rows { get; } = [];
    public IReadOnlyList<PresetToggle> Presets { get; }

    public FirewallViewModel()
    {
        var presets = new List<PresetToggle>();
        foreach (var preset in FirewallPresets.All)
        {
            var rows = preset.Ports.Select(p => new FirewallRow(preset.Name, p.Service, p.Ports, p.Protocols, p.Direction, false)).ToList();
            foreach (var row in rows) Add(row);
            presets.Add(new PresetToggle(preset, rows));
        }
        Presets = presets;
    }

    public FirewallStatus? Status => _status;
    public string Summary { get => _summary; private set { _summary = value; Raise(); } }
    public string Pending { get => _pending; private set { _pending = value; Raise(); } }
    public bool HasChanges { get => _hasChanges; private set { _hasChanges = value; Raise(); Raise(nameof(CanApply)); } }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; Raise(); Raise(nameof(IsIdle)); Raise(nameof(CanApply)); Raise(nameof(CanRemoveAll)); } }
    public bool IsIdle => !_isBusy;
    public bool CanApply => !_isBusy && _hasChanges && _status?.Readable == true;
    public bool CanRemoveAll => !_isBusy && _status?.Readable == true && _status.HasAny;

    /// <summary>The rules the ticked rows ask for.</summary>
    public IReadOnlyList<BlockRule> Desired() => Rows.Where(r => r.IsChecked).SelectMany(r => r.Choice.Rules()).Distinct().ToList();

    /// <summary>
    /// Shows the firewall as it is now: ticks exactly the ports AgentReclaim blocks (anything not applied yet is
    /// discarded) and fills in each port's status.
    /// </summary>
    public void Load(FirewallStatus status, SocketSnapshot sockets, Func<int, string> processName)
    {
        _status = status;
        Raise(nameof(Status));
        Raise(nameof(CanRemoveAll));
        foreach (var old in Rows.Where(r => r.IsCustom).ToList()) Rows.Remove(old);

        var (listed, custom) = FirewallSelection.Map(Rows.Select(r => r.Choice).ToList(), status.Present());
        foreach (var (row, direction) in Rows.Zip(listed).ToList()) row.Reset(direction is not null, direction ?? row.Direction);
        foreach (var choice in custom)
        {
            var row = new FirewallRow("Custom", choice.Service, choice.Ports, choice.Protocols, choice.Direction, true);
            row.Reset(true, choice.Direction);
            Add(row);
        }

        foreach (var row in Rows) row.Status = FirewallSelection.Describe(row.Ports, row.Protocols, status, sockets, processName);
        foreach (var preset in Presets) preset.Refresh();
        Summary = SummaryText(status);
        UpdatePending();
    }

    /// <summary>Adds a custom port or range (ticked). Returns an error to show, or null.</summary>
    public string? AddCustom(string ports, int protocolIndex, int directionIndex)
    {
        if (!PortRange.TryParse(ports, out var range))
            return "Enter a port (1–65535) or a range like 8000-8010.";
        var protocols = protocolIndex switch { 1 => Protocols.Udp, 2 => Protocols.Both, _ => Protocols.Tcp };
        var direction = (Directions)Math.Clamp(directionIndex, 0, 2);
        // The same port and protocols already listed: tick that row with the direction asked for. Any other overlap
        // gets its own row (the rules are combined when applying).
        if (Rows.FirstOrDefault(r => r.Ports == range && r.Protocols == protocols) is { } existing)
        {
            existing.IsChecked = true;
            existing.Direction = direction;
            return existing.IsCustom ? null
                : $"Port {range} is already listed ({existing.Service}): it's ticked now, blocking {FirewallRow.DirectionNames[(int)direction].ToLowerInvariant()}.";
        }
        var row = new FirewallRow("Custom", "Custom port", range, protocols, direction, true);
        row.IsChecked = true;
        row.Status = "Not applied yet";
        Add(row);
        UpdatePending();
        return null;
    }

    /// <summary>Takes a custom row off the list (unticked, so applying removes its rules).</summary>
    public void RemoveCustom(FirewallRow row)
    {
        if (!row.IsCustom) return;
        row.IsChecked = false;
        Rows.Remove(row);
        UpdatePending();
    }

    public FirewallPlan? Plan(FirewallStatus fresh) => fresh.Readable ? FirewallPlan.Create(fresh, Desired()) : null;

    private void Add(FirewallRow row)
    {
        row.Changed += () =>
        {
            foreach (var preset in Presets ?? []) preset.Refresh();
            UpdatePending();
        };
        Rows.Add(row);
    }

    private void UpdatePending()
    {
        if (_status is not { Readable: true } status)
        {
            HasChanges = false;
            Pending = "";
            return;
        }
        var plan = FirewallPlan.Create(status, Desired());
        HasChanges = !plan.IsEmpty;
        Pending = plan.IsEmpty ? "No changes to apply." : $"To apply: {FirewallChange.Summarize(plan)}.";
        if (plan.Blocked.Count > 0)
            Pending += $" Skipped (another rule already has the name): {string.Join(", ", plan.Blocked.Select(r => r.Describe()))}.";
    }

    private static string SummaryText(FirewallStatus status)
    {
        if (!status.Readable) return status.Error ?? "Couldn't read Windows Firewall.";
        var profiles = WindowsFirewall.Profiles.Select(p =>
            $"{WindowsFirewall.ProfileName(p)}{((status.CurrentProfiles & p) != 0 ? " (in use)" : "")}: {(status.EnabledByProfile.GetValueOrDefault(p) ? "on" : "OFF")}");
        var text = $"Windows Firewall — {string.Join(" · ", profiles)}. AgentReclaim rules: {status.Present().Count}.";
        if (!status.OnForCurrentNetworks) text += " Windows Firewall is off for the network you're on, so no rule here has any effect until it's turned on.";
        if (status.Renamed().Count > 0) text += $" {status.Renamed().Count} rule(s) in the AgentReclaim group were renamed outside AgentReclaim: the next Apply removes them.";
        if (!status.Complete) text += " Some firewall rules couldn't be read, so AgentReclaim won't unblock anything until they can.";
        if (status.BypassRules.Count > 0) text += $" {status.BypassRules.Count} authenticated-bypass (IPsec) allow rule(s) can override these blocks for the users or machines they list.";
        if (status.PolicyOverride) text += " Group Policy manages this firewall: it may ignore local rules like these.";
        if (status.OtherFirewalls.Count > 0) text += $" Also installed: {string.Join(", ", status.OtherFirewalls)}, which may decide on its own.";
        return text;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentReclaim.Core.Integration;

/// <summary>
/// Adds or removes AgentReclaim's SessionEnd hook in ~/.claude/settings.json. Only ever run when the
/// user clicks the button; keeps a timestamped backup and leaves every other setting untouched.
/// </summary>
public static class ClaudeHookInstaller
{
    private const string Marker = "AgentReclaim";

    /// <summary>Tests point this at a temp file so they never touch the real Claude settings.</summary>
    public static string? SettingsPathOverride { get; set; }

    public static string SettingsPath => SettingsPathOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static string HookCommand(string exePath) => $"\"{exePath}\" hook session-end";

    public static bool IsInstalled() =>
        File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(Marker, StringComparison.OrdinalIgnoreCase)
        && Load()?["hooks"]?["SessionEnd"] is JsonArray groups
        && groups.Any(g => g?["hooks"] is JsonArray list && list.Any(IsOurHook));

    public static string Install(string exePath)
    {
        var root = Load() ?? new JsonObject();
        var hooks = root["hooks"] as JsonObject ?? (JsonObject)(root["hooks"] = new JsonObject());
        var sessionEnd = hooks["SessionEnd"] as JsonArray ?? (JsonArray)(hooks["SessionEnd"] = new JsonArray());

        RemoveOurHooks(sessionEnd);
        sessionEnd.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = HookCommand(exePath),
                ["timeout"] = 10,
            }),
        });

        return Save(root);
    }

    public static string Uninstall()
    {
        var root = Load();
        if (root?["hooks"]?["SessionEnd"] is not JsonArray sessionEnd) return SettingsPath;
        RemoveOurHooks(sessionEnd);
        if (sessionEnd.Count == 0) (root["hooks"] as JsonObject)?.Remove("SessionEnd");
        return Save(root);
    }

    /// <summary>
    /// Removes only AgentReclaim's own command entries. Sibling hooks and group settings stay; a group is
    /// dropped only if our hook was all it contained.
    /// </summary>
    private static void RemoveOurHooks(JsonArray sessionEnd)
    {
        foreach (var group in sessionEnd.ToList())
        {
            if (group?["hooks"] is not JsonArray list) continue;
            var ours = list.Where(IsOurHook).ToList();
            if (ours.Count == 0) continue;
            foreach (var hook in ours) list.Remove(hook);
            if (list.Count == 0) sessionEnd.Remove(group);
        }
    }

    /// <summary>Exactly our command: an AgentReclaim executable invoked with `hook session-end`.</summary>
    private static bool IsOurHook(JsonNode? hook)
    {
        if (hook?["command"] is not JsonValue value || !value.TryGetValue<string>(out var command)) return false;
        var (file, arguments) = Actions.Relauncher.Split(command);
        return Path.GetFileName(file).Equals("AgentReclaim.exe", StringComparison.OrdinalIgnoreCase)
               && arguments.Trim().Equals("hook session-end", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject? Load()
    {
        if (!File.Exists(SettingsPath)) return null;
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonNode.Parse(File.ReadAllText(SettingsPath), documentOptions: options) as JsonObject
               ?? throw new InvalidDataException($"{SettingsPath} is not a JSON object.");
    }

    private static string Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        FileBackup.Create(SettingsPath, "agentreclaim");
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return SettingsPath;
    }
}

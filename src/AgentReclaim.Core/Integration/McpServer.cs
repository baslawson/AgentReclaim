using System.Text.Json;
using System.Text.Json.Nodes;
using AgentReclaim.Core.Actions;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Collection;
using AgentReclaim.Core.Model;
using AgentReclaim.Core.Native;
using AgentReclaim.Core.Settings;

namespace AgentReclaim.Core.Integration;

/// <summary>
/// What stop_my_process needs: the current settings (read on every call, so the switch in the app
/// applies at once), who the calling agent is, and how to stop a subtree.
/// </summary>
public sealed class McpSelfScope(
    Func<AppSettings> settings,
    Func<CallerAgent?> identifyCaller,
    Func<ProcessNode, string, string, string, TerminationResult> stop,
    int selfPid)
{
    private CallerAgent? _caller;

    public AppSettings Settings => settings();

    /// <summary>Cached once found; a failed or empty lookup (e.g. a process listing that failed) is retried next time.</summary>
    public CallerAgent? Caller
    {
        get
        {
            if (_caller is not null) return _caller;
            try
            {
                return _caller = identifyCaller();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return null;
            }
        }
    }

    public int SelfPid => selfPid;

    public TerminationResult Stop(ProcessNode root, string agent, string attribution, string trigger) =>
        stop(root, agent, attribution, trigger);

    public static McpSelfScope Default() => new(
        () => AppSettings.Load(AppPaths.SettingsFile),
        () => AgentScope.Identify(Environment.ProcessId, ProcessCollector.ReadIdentities(), ReadCommandLine),
        (root, agent, attribution, trigger) =>
            new ProcessTerminator(new ActionLog(AppPaths.ActionLogFile)).StopSubtree(root, agent, attribution, trigger),
        Environment.ProcessId);

    private static string? ReadCommandLine(int pid)
    {
        var handle = NativeProcess.Open(pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            return NativeProcess.GetCommandLine(handle);
        }
        finally
        {
            Native.NativeMethods.CloseHandle(handle);
        }
    }
}

/// <summary>
/// `AgentReclaim.exe mcp`: an MCP server over stdio, so an agent can ask what is using the machine's
/// resources. Everything is read-only except stop_my_process, which is off until the user turns it on
/// and then only reaches the calling agent's own processes and leftovers (see <see cref="AgentScope"/>).
/// </summary>
public sealed class McpServer(TextReader input, TextWriter output, Func<AnalysisResult>? analyze = null, McpSelfScope? scope = null)
{
    private const string DefaultProtocolVersion = "2025-06-18";

    private AnalysisResult Analyze() => analyze is not null ? analyze() : AnalyzeNow();
    private readonly McpSelfScope _scope = scope ?? McpSelfScope.Default();

    public void Run()
    {
        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Everything about one request, including reading its fields, stays inside this boundary:
            // a bad request gets an error reply and the server keeps serving.
            JsonNode? id = null;
            try
            {
                JsonNode? request;
                try
                {
                    request = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    Write(Error(null, -32700, "Parse error"));
                    continue;
                }

                if (request is not JsonObject obj)
                {
                    Write(Error(null, -32600, "Invalid request: expected a JSON object"));
                    continue;
                }

                var hasId = obj.TryGetPropertyValue("id", out var rawId) && rawId is not null;
                if (hasId && !(rawId is JsonValue v && (v.TryGetValue<string>(out _) || v.TryGetValue<long>(out _))))
                {
                    Write(Error(null, -32600, "Invalid request: id must be a string or number"));
                    continue;
                }
                id = hasId ? rawId!.DeepClone() : null;

                var method = Text(obj["method"]);
                if (id is null) continue; // a notification (e.g. notifications/initialized): no reply
                if (method is null)
                {
                    Write(Error(id, -32600, "Invalid request: method must be a string"));
                    continue;
                }

                Write(method switch
                {
                    "initialize" => Result(id, new JsonObject
                    {
                        ["protocolVersion"] = Text(obj["params"]?["protocolVersion"]) ?? DefaultProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "agentreclaim", ["version"] = AppInfo.Version.ToString() },
                    }),
                    "ping" => Result(id, new JsonObject()),
                    "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
                    "tools/call" => Result(id, Call(obj["params"])),
                    _ => Error(id, -32601, $"Method not found: {method}"),
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            {
                Write(Error(id, -32602, $"Invalid params: {ex.Message}"));
            }
            catch (Exception ex)
            {
                Write(Error(id, -32603, ex.Message));
            }
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonArray Tools() =>
    [
        Tool("resource_overview",
            "System memory/CPU, how much Claude Code, Codex and Gemini CLI sessions use, leftovers from exited sessions, and findings such as piles of idle child processes or a growing agent.",
            new JsonObject()),
        Tool("list_session_processes",
            "Processes running under one live agent session (the biggest first). Without agent_pid, lists the sessions.",
            new JsonObject { ["agent_pid"] = new JsonObject { ["type"] = "integer", ["description"] = "PID of the agent (claude.exe, codex.exe, or node.exe running an npm-installed agent such as Gemini CLI)" } }),
        Tool("list_leftovers",
            "Process trees left behind by exited agent sessions, with memory, ports and command lines. Stopping them is done by the user in the AgentReclaim app.",
            new JsonObject()),
        Tool("list_my_processes",
            "Processes running under the agent that called this tool (e.g. dev servers, watchers, test runners it started) and its own leftovers, with memory, ports and whether stop_my_process may stop each one.",
            new JsonObject()),
        Tool("stop_my_process",
            "Stops one of your own processes (and its children) from list_my_processes, e.g. a dev server you started that is still holding a port. " +
            "Only works if the user turned it on in AgentReclaim (Settings → Integrations). Never stops anything outside your own process tree, another agent, or protected processes. Logged in AgentReclaim's activity log.",
            new JsonObject { ["instance_id"] = new JsonObject { ["type"] = "string", ["description"] = "instance_id from list_my_processes (pid plus start time, so a reused pid never matches)" } },
            readOnly: false, required: ["instance_id"]),
    ];

    private static JsonObject Tool(string name, string description, JsonObject properties, bool readOnly = true, string[]? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is not null) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
            ["annotations"] = readOnly
                ? new JsonObject { ["readOnlyHint"] = true }
                : new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = true, ["idempotentHint"] = false },
        };
    }

    private JsonObject Call(JsonNode? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var args = parameters?["arguments"];
        if (name is not ("resource_overview" or "list_session_processes" or "list_leftovers" or "list_my_processes" or "stop_my_process"))
            throw new ArgumentException($"Unknown tool: {name}");
        if (name == "stop_my_process") return StopMyProcess(args);

        var result = Analyze();
        object payload = name switch
        {
            "resource_overview" => Overview(result),
            "list_session_processes" => args?["agent_pid"]?.GetValue<int>() is { } pid ? SessionProcesses(result, pid) : Sessions(result),
            "list_leftovers" => result.Orphans.Select(Leftover).ToList(),
            _ => MyProcesses(result),
        };
        return Content(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject Content(string text, bool isError = false)
    {
        var content = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (isError) content["isError"] = true;
        return content;
    }

    private const string TurnedOff =
        "Stopping processes from MCP is turned off. The user can turn it on in AgentReclaim → Settings → Integrations " +
        "(\"Let agents stop their own processes\"). Until then, ask the user to stop it, or stop it the way you started it.";

    private object MyProcesses(AnalysisResult result)
    {
        if (_scope.Caller is not { } caller)
            return new { error = "Couldn't tell which agent started this MCP server, so there is nothing to list." };

        return new
        {
            agent = caller.Label,
            stopping_enabled = _scope.Settings.AllowAgentSelfStop,
            processes = AgentScope.OwnProcesses(result, caller, _scope.SelfPid).Take(200).Select(p => new
            {
                instance_id = InstanceId(p.Node.Process.Key),
                pid = p.Node.Process.Pid,
                parent_pid = p.Node.Process.ParentPid,
                name = p.Node.Process.Name,
                kind = p.IsLeftover ? "leftover" : "running",
                depth = p.Depth,
                memory_mb = p.Node.Process.PrivateBytes >> 20,
                cpu_percent = Math.Round(p.Node.Process.CpuPercent, 1),
                ports = p.Node.Process.ListeningPorts,
                can_stop = p.Check.Allowed,
                why_not = p.Check.BlockedReason,
                command_line = p.Node.Process.CommandLine,
            }).ToList(),
        };
    }

    private static string InstanceId(ProcessKey key) => $"{key.Pid}:{key.StartTicks}";

    private static ProcessKey? ParseInstanceId(string? text) =>
        text?.Split(':') is [var pid, var ticks]
        && int.TryParse(pid, out var p) && p > 0
        && long.TryParse(ticks, out var t) && t > 0
            ? new ProcessKey(p, t)
            : null;

    private JsonObject StopMyProcess(JsonNode? args)
    {
        // An exact instance, not a bare pid: if the listed process exited and its pid was reused, nothing matches.
        if (ParseInstanceId(Text(args?["instance_id"])) is not { } key)
            throw new ArgumentException("instance_id must be an instance_id from list_my_processes, like \"1234:133000000000000000\".");
        if (!_scope.Settings.AllowAgentSelfStop) return Content(TurnedOff, isError: true);
        if (_scope.Caller is not { } caller)
            return Content("Couldn't tell which agent started this MCP server, so nothing can be stopped from here.", isError: true);

        var (target, refusal) = AgentScope.Resolve(Analyze(), caller, _scope.SelfPid, key);
        if (target is null) return Content(refusal!, isError: true);
        if (!target.Check.Allowed) return Content($"Not stopped: {target.Check.BlockedReason}", isError: true);

        var attribution = target.Leftover?.Attribution.ToString() ?? "own process";
        var outcome = _scope.Stop(target.Node, AgentSignatures.DisplayName(caller.Kind), attribution, $"mcp: {caller.Label}");
        var text = outcome.Message;
        if (outcome.Success && target.Check.Warnings.Count > 0)
            text += " Note: " + string.Join(" ", target.Check.Warnings);
        return Content(text, isError: !outcome.Success);
    }

    private static object Overview(AnalysisResult r) => new
    {
        system = new
        {
            memory_used_mb = r.Snapshot.System.UsedMemoryBytes >> 20,
            memory_total_mb = r.Snapshot.System.TotalMemoryBytes >> 20,
            cpu_percent = Math.Round(r.Snapshot.System.CpuPercent, 1),
        },
        ai_tools_memory_mb = r.AgentPrivateBytes >> 20,
        sessions = Sessions(r),
        leftovers = new { count = r.Orphans.Count, memory_mb = r.OrphanPrivateBytes >> 20 },
        findings = r.Findings.Select(f => new { f.Title, f.Detail, memory_mb = f.Bytes >> 20 }),
    };

    private static object Sessions(AnalysisResult r) => r.Sessions.Select(s => new
    {
        agent = AgentSignatures.DisplayName(s.Kind),
        pid = s.Root.Process.Pid,
        processes = s.Root.TotalCount,
        memory_mb = s.Root.TotalPrivateBytes >> 20,
        cpu_percent = Math.Round(s.Root.TotalCpuPercent, 1),
    }).ToList();

    private static object SessionProcesses(AnalysisResult r, int pid)
    {
        var session = r.Sessions.FirstOrDefault(s => s.Root.Process.Pid == pid)
                      ?? throw new ArgumentException($"No live agent session with pid {pid}.");
        return session.Root.SelfAndDescendants()
            .Select(x => x.Node.Process)
            .OrderByDescending(p => p.PrivateBytes)
            .Take(50)
            .Select(p => new
            {
                pid = p.Pid,
                name = p.Name,
                memory_mb = p.PrivateBytes >> 20,
                cpu_percent = Math.Round(p.CpuPercent, 1),
                ports = p.ListeningPorts,
                command_line = p.CommandLine,
            }).ToList();
    }

    private static object Leftover(OrphanTree o) => new
    {
        pid = o.Root.Process.Pid,
        name = o.Root.Process.Name,
        agent = AgentSignatures.DisplayName(o.Kind),
        owner = o.FormerOwner,
        owner_confidence = o.Attribution == Attribution.Observed ? "seen" : "guessed",
        processes = o.Root.TotalCount,
        memory_mb = o.Root.TotalPrivateBytes >> 20,
        ports = o.Root.SelfAndDescendants().SelectMany(x => x.Node.Process.ListeningPorts).Distinct(),
        command_line = o.Root.Process.CommandLine,
    };

    // Kept for the server's lifetime, so what it observes (e.g. a server under a shell that then exits) still
    // counts on the next request even if the app hasn't recorded it. The app's lineage is merged in each time.
    private ProcessCollector? _collector;
    private LineageStore? _lineage;
    private Analyzer? _analyzer;

    private AnalysisResult AnalyzeNow()
    {
        if (_collector is null)
        {
            // Built in locals and published together, so a failure here is retried on the next request.
            var collector = new ProcessCollector();
            var lineage = new LineageStore(AppPaths.LineageFile, readOnly: true);
            var analyzer = new Analyzer(lineage);
            collector.Collect();
            Thread.Sleep(500); // second sample gives CPU percentages
            (_lineage, _analyzer, _collector) = (lineage, analyzer, collector);
        }
        else
        {
            _lineage!.MergeFromDisk();
        }
        return _analyzer!.Analyze(_collector.Collect());
    }

    private void Write(JsonObject message)
    {
        output.WriteLine(message.ToJsonString());
        output.Flush();
    }

    private static JsonObject Result(JsonNode id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

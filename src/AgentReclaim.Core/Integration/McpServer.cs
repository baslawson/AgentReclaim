using System.Text.Json;
using System.Text.Json.Nodes;
using AgentReclaim.Core.Analysis;
using AgentReclaim.Core.Collection;

namespace AgentReclaim.Core.Integration;

/// <summary>
/// `AgentReclaim.exe mcp`: a read-only MCP server over stdio, so an agent can ask what is using the
/// machine's resources. It can't stop or change anything; that stays with the user in the app.
/// </summary>
public sealed class McpServer(TextReader input, TextWriter output, Func<AnalysisResult>? analyze = null)
{
    private const string DefaultProtocolVersion = "2025-06-18";

    private readonly Func<AnalysisResult> _analyze = analyze ?? AnalyzeNow;

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
            "System memory/CPU, how much Claude Code and Codex sessions use, leftovers from exited sessions, and findings such as piles of idle child processes or a growing agent.",
            new JsonObject()),
        Tool("list_session_processes",
            "Processes running under one live agent session (the biggest first). Without agent_pid, lists the sessions.",
            new JsonObject { ["agent_pid"] = new JsonObject { ["type"] = "integer", ["description"] = "PID of claude.exe / codex.exe" } }),
        Tool("list_leftovers",
            "Process trees left behind by exited agent sessions, with memory, ports and command lines. Stopping them is done by the user in the AgentReclaim app.",
            new JsonObject()),
    ];

    private static JsonObject Tool(string name, string description, JsonObject properties) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
    };

    private JsonObject Call(JsonNode? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var args = parameters?["arguments"];
        var result = _analyze();

        object payload = name switch
        {
            "resource_overview" => Overview(result),
            "list_session_processes" => args?["agent_pid"]?.GetValue<int>() is { } pid ? SessionProcesses(result, pid) : Sessions(result),
            "list_leftovers" => result.Orphans.Select(Leftover).ToList(),
            _ => throw new ArgumentException($"Unknown tool: {name}"),
        };

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            }),
        };
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

    private static AnalysisResult AnalyzeNow()
    {
        var collector = new ProcessCollector();
        collector.Collect();
        Thread.Sleep(500); // second sample gives CPU percentages
        return new Analyzer(new LineageStore(AppPaths.LineageFile, readOnly: true)).Analyze(collector.Collect());
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

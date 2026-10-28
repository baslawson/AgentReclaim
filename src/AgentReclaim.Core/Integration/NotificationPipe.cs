using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AgentReclaim.Core.Integration;

/// <summary>AgentStartTicks identifies the exact agent instance (0 if unknown, which never matches).</summary>
public sealed record SessionEndedMessage(int AgentPid, string? SessionId, string? Cwd, string? Reason, long AgentStartTicks = 0);

/// <summary>
/// One-line JSON messages from the hook to the running app over a named pipe that only the current
/// user can open (PipeOptions.CurrentUserOnly).
/// </summary>
public static class NotificationPipe
{
    public static bool TrySend(SessionEndedMessage message, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false; // app not running: nothing to tell
        }
    }

    /// <summary>Accepts messages until cancelled. Malformed messages are ignored.</summary>
    public static async Task ServeAsync(Action<SessionEndedMessage> onMessage, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(AppPaths.PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancel);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var readLine = reader.ReadLineAsync(cancel).AsTask();
                if (await Task.WhenAny(readLine, Task.Delay(TimeSpan.FromSeconds(5), cancel)) != readLine) continue;

                if (await readLine is { Length: > 0 and < 4096 } line
                    && JsonSerializer.Deserialize<SessionEndedMessage>(line) is { AgentPid: > 0 } message)
                    onMessage(message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }
}

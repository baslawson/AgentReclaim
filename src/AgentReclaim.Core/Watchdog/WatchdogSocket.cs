using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using AgentReclaim.Core.Diagnostics;

namespace AgentReclaim.Core.Watchdog;

/// <summary>
/// Pushes alerts to agents over a WebSocket: ws://127.0.0.1:port/watchdog?session=id&amp;token=t. It listens on loopback
/// only and takes nothing from a client but the connection. A web page can reach loopback too, so anything with an Origin
/// header or an unexpected Host is refused, and the token keeps out other users on this PC.
/// </summary>
public sealed class WatchdogSocket(WatchdogHub hub, string token) : IDisposable
{
    public const int MaxClients = 32;
    private const int MaxRequestBytes = 8192;
    private static readonly TimeSpan HandshakeLimit = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private int _clients;

    public int Port { get; private set; }

    /// <summary>Throws SocketException if the port is taken.</summary>
    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_stop.IsCancellationRequested) return;
                Log.Warning("Watchdog: accept failed", ex);
                await Task.Delay(1000);
                continue;
            }

            if (Interlocked.Increment(ref _clients) > MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                client.Dispose();
                Log.Warning($"Watchdog: refused a connection, already at {MaxClients}.");
                continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await Serve(client);
                }
                catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    Log.Debug($"Watchdog: connection ended: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Log.Warning("Watchdog: connection failed", ex);
                }
                finally
                {
                    Interlocked.Decrement(ref _clients);
                    client.Dispose();
                }
            });
        }
    }

    private async Task Serve(TcpClient client)
    {
        var stream = client.GetStream();
        string? head;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            limit.CancelAfter(HandshakeLimit);
            head = await ReadHead(stream, limit.Token);
        }
        if (head is null) return;

        var (status, session, key) = Judge(head, token, Port);
        if (status != 101)
        {
            await Reply(stream, $"HTTP/1.1 {status} Refused\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await Reply(stream, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");

        using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, TimeSpan.FromSeconds(30));
        var (connection, reader) = hub.Connect(session!, DateTime.UtcNow);
        Log.Info($"Watchdog: session {session} connected.");

        using var ended = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        // Only to see the client close: nothing it sends is used.
        var reading = Task.Run(async () =>
        {
            var buffer = new byte[1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                    if ((await socket.ReceiveAsync(buffer, ended.Token)).MessageType == WebSocketMessageType.Close) break;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            ended.Cancel();
        });

        var unsent = new List<WatchdogAlert>();
        try
        {
            await SendAll(reader, (alert, token) => socket.SendAsync(Encoding.UTF8.GetBytes(alert.Text), WebSocketMessageType.Text, true, token),
                unsent, ended.Token);
        }
        finally
        {
            hub.Disconnect(session!, connection, unsent, DateTime.UtcNow);
            Log.Info($"Watchdog: session {session} disconnected.");
            // Cancelling a receive aborts the socket, so the goodbye goes first. The client's listener ending is its signal.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    // Only these two end it from this side. A listener told "replaced" isn't to think the app stopped.
                    var reason = _stop.IsCancellationRequested ? "stopping" : "replaced";
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, bye.Token);
                    await reading.WaitAsync(bye.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or TimeoutException)
                {
                }
            }
            await ended.CancelAsync();
            await reading;
        }
    }

    /// <summary>The request up to its blank line, read byte by byte so the WebSocket's own bytes stay in the stream.</summary>
    private static async Task<string?> ReadHead(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        while (bytes.Count < MaxRequestBytes)
        {
            if (await stream.ReadAsync(one, token) == 0) return null;
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^1] == '\n' && bytes[^2] == '\r' && bytes[^3] == '\n' && bytes[^4] == '\r')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        return null;
    }

    private static async Task Reply(NetworkStream stream, string text) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));

    /// <summary>Decides whether a handshake may go ahead. 101: yes, for this session.</summary>
    /// <summary>
    /// Sends until the connection ends. What couldn't be sent, including the one being sent when it ended, goes into
    /// <paramref name="unsent"/> for the next connection.
    /// </summary>
    internal static async Task SendAll(ChannelReader<WatchdogAlert> reader, Func<WatchdogAlert, CancellationToken, Task> send,
        List<WatchdogAlert> unsent, CancellationToken token)
    {
        try
        {
            while (await reader.WaitToReadAsync(token))
                while (reader.TryRead(out var alert))
                {
                    try
                    {
                        await send(alert, token);
                    }
                    catch
                    {
                        unsent.Add(alert);
                        throw;
                    }
                }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }
        finally
        {
            while (reader.TryRead(out var left)) unsent.Add(left);
        }
    }

    internal static (int Status, string? Session, string? Key) Judge(string head, string token, int port)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first is not ["GET", var target, _]) return (400, null, null);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (headers.ContainsKey("Origin")) return (403, null, null);
        if (headers.GetValueOrDefault("Host") is not { } host || (host != $"127.0.0.1:{port}" && host != $"localhost:{port}"))
            return (403, null, null);

        var question = target.IndexOf('?');
        if (question < 0 || target[..question] != "/watchdog") return (404, null, null);
        var query = target[(question + 1)..].Split('&')
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .GroupBy(part => part[0])
            .ToDictionary(g => g.Key, g => g.Last()[1]);

        var given = Encoding.UTF8.GetBytes(query.GetValueOrDefault("token") ?? "");
        if (!CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(token))) return (401, null, null);
        if (query.GetValueOrDefault("session") is not { } session || !WatchdogEngine.ValidSession(session)) return (400, null, null);

        if (!headers.GetValueOrDefault("Upgrade", "").Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.GetValueOrDefault("Connection", "").Contains("upgrade", StringComparison.OrdinalIgnoreCase)
            || headers.GetValueOrDefault("Sec-WebSocket-Version") != "13"
            || headers.GetValueOrDefault("Sec-WebSocket-Key") is not { Length: > 0 } key)
            return (400, null, null);

        return (101, session, key);
    }

    /// <summary>After <see cref="Dispose"/>: until each connection has handed back what it hadn't sent, or the limit.</summary>
    public void WaitForConnections(TimeSpan limit)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _clients) > 0 && waited.Elapsed < limit) Thread.Sleep(10);
    }

    public void Dispose()
    {
        _stop.Cancel();
        hub.CloseAll();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }
    }
}

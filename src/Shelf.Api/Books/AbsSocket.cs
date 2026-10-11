using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

// The live connection Audiobookshelf's apps keep with their server, over socket.io: enough of it, by WebSocket alone
// (as the apps ask), for them to show the server as connected, take bookmarks, and hear of progress made elsewhere.
public sealed class AbsSocket(IServiceScopeFactory scopes, IDataProtectionProvider protection)
{
    private const int PingMilliseconds = 25_000;
    private readonly ConcurrentDictionary<Guid, (int ReaderId, WebSocket Socket, SemaphoreSlim Sending)> _connections = new();

    public void Map(WebApplication app) =>
        app.Map("/socket.io", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                // Long polling is not offered; the apps connect by WebSocket.
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await RunAsync(socket, http.RequestAborted);
        }).AllowAnonymous().ExcludeFromDescription();

    private async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var sending = new SemaphoreSlim(1, 1);
        var sid = id.ToString("N")[..20];
        await SendAsync(socket, sending, "0" + JsonSerializer.Serialize(new { sid, upgrades = Array.Empty<string>(), pingInterval = PingMilliseconds, pingTimeout = 20_000, maxPayload = 1_000_000 }), cancellationToken);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pinging = PingAsync(socket, sending, stop.Token);
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage && message.Length < 1_000_000);

                await HandleAsync(id, socket, sending, sid, message.ToString(), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            _connections.TryRemove(id, out _);
            await stop.CancelAsync();
            await pinging.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    // Engine.IO packets: 2 ping, 3 pong, 4 message; socket.io messages: 40 connect, 41 disconnect, 42 an event.
    private async Task HandleAsync(Guid id, WebSocket socket, SemaphoreSlim sending, string sid, string packet, CancellationToken cancellationToken)
    {
        if (packet == "2")
        {
            await SendAsync(socket, sending, "3", cancellationToken);
        }
        else if (packet.StartsWith("40", StringComparison.Ordinal))
        {
            await SendAsync(socket, sending, "40" + JsonSerializer.Serialize(new { sid }), cancellationToken);
        }
        else if (packet.StartsWith("41", StringComparison.Ordinal))
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
        }
        else if (packet.StartsWith("42", StringComparison.Ordinal) && JsonNode.Parse(packet[2..]) is JsonArray { Count: >= 1 } call && call[0]?.GetValue<string>() == "auth")
        {
            var token = call.Count > 1 ? call[1]?.ToString() : null;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            if (await AbsApi.ReaderOfAccessAsync(db, protection, token, cancellationToken) is { } reader)
            {
                _connections[id] = (reader.Id, socket, sending);
                await EmitAsync(socket, sending, "init", new JsonObject { ["userId"] = AbsJson.UserId(reader.Id), ["username"] = reader.Name, ["librariesScanning"] = new JsonArray() }, cancellationToken);
            }
            else
            {
                await EmitAsync(socket, sending, "auth_failed", new JsonObject { ["message"] = "Invalid token" }, cancellationToken);
            }
        }
    }

    // An event to every app the reader has connected.
    public async Task SendAsync(int readerId, string name, JsonNode payload, CancellationToken cancellationToken = default)
    {
        foreach (var (_, socket, sending) in _connections.Values.Where(connection => connection.ReaderId == readerId))
        {
            try
            {
                await EmitAsync(socket, sending, name, payload.DeepClone(), cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    public int Connected(int readerId) => _connections.Values.Count(connection => connection.ReaderId == readerId);

    private static Task EmitAsync(WebSocket socket, SemaphoreSlim sending, string name, JsonNode payload, CancellationToken cancellationToken) =>
        SendAsync(socket, sending, "42" + new JsonArray(name, payload).ToJsonString(), cancellationToken);

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim sending, string text, CancellationToken cancellationToken)
    {
        await sending.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        finally
        {
            sending.Release();
        }
    }

    private static async Task PingAsync(WebSocket socket, SemaphoreSlim sending, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PingMilliseconds));
        while (await timer.WaitForNextTickAsync(cancellationToken) && socket.State == WebSocketState.Open)
        {
            await SendAsync(socket, sending, "2", cancellationToken);
        }
    }
}

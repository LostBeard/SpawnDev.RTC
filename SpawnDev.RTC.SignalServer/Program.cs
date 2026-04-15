using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("https://localhost:5571", "http://localhost:5572");

// CORS for cross-origin WebSocket connections (demo on different port)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

// Simple room-based signaling server for WebRTC
// Peers join a room, exchange SDP offers/answers and ICE candidates via WebSocket
var rooms = new ConcurrentDictionary<string, Room>();

app.Map("/signal/{roomId}", async (HttpContext context, string roomId) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connections only");
        return;
    }

    var ws = await context.WebSockets.AcceptWebSocketAsync();
    var room = rooms.GetOrAdd(roomId, _ => new Room());
    var peerId = Guid.NewGuid().ToString("N")[..8];
    var peer = new Peer(peerId, ws);

    // Notify existing peers about the new peer
    room.AddPeer(peer);
    await room.BroadcastAsync(peerId, new { type = "peer-joined", peerId });

    Console.WriteLine($"[{roomId}] Peer {peerId} joined ({room.PeerCount} peers)");

    var buffer = new byte[64 * 1024];
    try
    {
        // Send the peer their ID and the list of existing peers
        await peer.SendAsync(new { type = "welcome", peerId, peers = room.GetPeerIds(peerId) });

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var msg = JsonSerializer.Deserialize<JsonElement>(json);

            if (!msg.TryGetProperty("type", out var typeEl))
                continue;

            var msgType = typeEl.GetString();
            var targetId = msg.TryGetProperty("targetId", out var tid) ? tid.GetString() : null;

            // Relay the message to the target peer (or broadcast if no target)
            var relay = new Dictionary<string, object?>
            {
                ["type"] = msgType,
                ["fromId"] = peerId,
            };

            // Copy all properties except type and targetId
            foreach (var prop in msg.EnumerateObject())
            {
                if (prop.Name != "type" && prop.Name != "targetId")
                    relay[prop.Name] = prop.Value;
            }

            if (targetId != null)
            {
                await room.SendToPeerAsync(targetId, relay);
            }
            else
            {
                await room.BroadcastAsync(peerId, relay);
            }
        }
    }
    catch (WebSocketException) { }
    finally
    {
        room.RemovePeer(peerId);
        await room.BroadcastAsync(peerId, new { type = "peer-left", peerId });
        Console.WriteLine($"[{roomId}] Peer {peerId} left ({room.PeerCount} peers)");

        if (room.PeerCount == 0)
            rooms.TryRemove(roomId, out _);
    }
});

// Health check
app.MapGet("/", () => Results.Ok(new { service = "SpawnDev.RTC.SignalServer", rooms = rooms.Count }));

Console.WriteLine("SpawnDev.RTC SignalServer running on https://localhost:5571");
app.Run();

// --- Types ---

class Peer
{
    public string Id { get; }
    public WebSocket Socket { get; }
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Peer(string id, WebSocket socket)
    {
        Id = id;
        Socket = socket;
    }

    public async Task SendAsync(object message)
    {
        if (Socket.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync();
        try
        {
            await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

class Room
{
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    public int PeerCount => _peers.Count;

    public void AddPeer(Peer peer) => _peers[peer.Id] = peer;

    public void RemovePeer(string peerId) => _peers.TryRemove(peerId, out _);

    public string[] GetPeerIds(string excludeId)
    {
        return _peers.Keys.Where(id => id != excludeId).ToArray();
    }

    public async Task BroadcastAsync(string fromId, object message)
    {
        foreach (var peer in _peers.Values)
        {
            if (peer.Id != fromId)
            {
                try { await peer.SendAsync(message); }
                catch { }
            }
        }
    }

    public async Task SendToPeerAsync(string targetId, object message)
    {
        if (_peers.TryGetValue(targetId, out var peer))
        {
            try { await peer.SendAsync(message); }
            catch { }
        }
    }
}

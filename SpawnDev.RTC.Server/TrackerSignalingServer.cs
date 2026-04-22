using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SpawnDev.RTC.Signaling;

namespace SpawnDev.RTC.Server;

/// <summary>
/// Generic WebRTC signaling server speaking the WebTorrent tracker wire protocol.
/// Rooms are keyed by a 20-byte opaque value on the wire (the tracker never
/// interprets it - any application can use a SHA-1-of-room-name, a raw GUID,
/// or a torrent info_hash interchangeably).
/// </summary>
/// <remarks>
/// Mount via <c>app.UseRtcSignaling("/announce")</c> from
/// <c>SpawnDev.RTC.Server.Extensions.SignalingAppBuilderExtensions</c>.
///
/// Interop: a stock WebTorrent browser or .NET client pointed at this endpoint
/// behaves exactly as if it were pointed at a public WebTorrent tracker.
/// Any SpawnDev.RTC consumer using <see cref="SpawnDev.RTC.Signaling.TrackerSignalingClient"/>
/// and a <see cref="SpawnDev.RTC.Signaling.RoomKey"/> can meet peers here
/// with zero WebTorrent references.
/// </remarks>
public sealed class TrackerSignalingServer
{
    private readonly ConcurrentDictionary<string, SignalingRoomInfo> _rooms = new();
    private readonly TrackerServerOptions _options;

    private static readonly JsonSerializerOptions _readOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public TrackerSignalingServer(TrackerServerOptions? options = null)
    {
        _options = options ?? new TrackerServerOptions();
    }

    /// <summary>Active rooms keyed by 20-byte wire room key.</summary>
    public IReadOnlyDictionary<string, SignalingRoomInfo> Rooms => _rooms;

    /// <summary>Total peers across all rooms.</summary>
    public int TotalPeers => _rooms.Values.Sum(r => r.Peers.Count);

    /// <summary>
    /// Handle one WebSocket connection. Call from an ASP.NET Core endpoint that
    /// has already accepted the upgrade (e.g. through <see cref="Extensions.SignalingAppBuilderExtensions.UseRtcSignaling"/>).
    /// </summary>
    public async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        var peer = new SignalingPeer(ws, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        try
        {
            await ReceiveLoopAsync(peer);
        }
        finally
        {
            foreach (var room in _rooms.Values)
                room.Peers.TryRemove(peer.PeerId, out _);
            foreach (var kvp in _rooms.Where(r => r.Value.Peers.IsEmpty).ToArray())
                _rooms.TryRemove(kvp.Key, out _);
        }
    }

    private async Task ReceiveLoopAsync(SignalingPeer peer)
    {
        var buffer = new byte[16384];

        while (peer.WebSocket.State == WebSocketState.Open)
        {
            using var frame = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await peer.WebSocket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                frame.Write(buffer, 0, result.Count);
                if (frame.Length > _options.MaxMessageBytes)
                {
                    _options.Log?.Invoke($"[RTC.Server] dropped oversize frame from {peer.RemoteAddress}");
                    return;
                }
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            try
            {
                var json = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
                var msg = JsonSerializer.Deserialize<WireMessage>(json, _readOpts);
                if (msg == null) continue;

                switch (msg.Action)
                {
                    case "announce":
                        await HandleAnnounceAsync(peer, msg);
                        break;
                    case "offer":
                        await RelayOfferAsync(peer, msg);
                        break;
                    case "answer":
                        await RelayAnswerAsync(peer, msg);
                        break;
                }
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"[RTC.Server] frame parse error: {ex.Message}");
            }
        }
    }

    private async Task HandleAnnounceAsync(SignalingPeer peer, WireMessage msg)
    {
        if (string.IsNullOrEmpty(msg.InfoHash) || string.IsNullOrEmpty(msg.PeerId))
        {
            await SendTextAsync(peer, "{\"action\":\"announce\",\"failure reason\":\"missing info_hash or peer_id\"}");
            return;
        }
        if (msg.InfoHash.Length > 100) return;

        peer.PeerId = msg.PeerId;
        var room = _rooms.GetOrAdd(msg.InfoHash, key => new SignalingRoomInfo(key));
        room.Peers[peer.PeerId] = peer;

        if (msg.Event == "stopped")
        {
            room.Peers.TryRemove(peer.PeerId, out _);
            return;
        }
        if (msg.Event == "completed" || msg.Left == 0)
            peer.IsSeeder = true;

        var maxPeers = Math.Min(msg.NumWant ?? _options.MaxPeersPerAnnounce, _options.MaxPeersPerAnnounce);
        var otherPeers = room.Peers.Values
            .Where(p => p.PeerId != peer.PeerId)
            .Take(maxPeers)
            .Select(p => new PeerSummary { PeerId = p.PeerId })
            .ToArray();

        var response = BinaryJsonSerializer.Serialize(new AnnounceResponse
        {
            InfoHash = msg.InfoHash!,
            Interval = _options.AnnounceIntervalSeconds,
            Complete = room.SeederCount,
            Incomplete = room.LeechCount,
            Peers = otherPeers.Length > 0 ? otherPeers : null,
        });
        await SendTextAsync(peer, response);

        if (msg.Offers is JsonElement offersEl && offersEl.ValueKind == JsonValueKind.Array)
        {
            var candidates = room.Peers.Values
                .Where(p => p.PeerId != peer.PeerId && p.WebSocket.State == WebSocketState.Open)
                .OrderBy(_ => Random.Shared.Next())
                .ToArray();

            int idx = 0;
            foreach (var offer in offersEl.EnumerateArray())
            {
                if (idx >= candidates.Length) break;
                if (!offer.TryGetProperty("offer", out var sdp) || !offer.TryGetProperty("offer_id", out var offerId))
                {
                    idx++;
                    continue;
                }
                var forward = BinaryJsonSerializer.Serialize(new RelayMessage
                {
                    Action = "announce",
                    InfoHash = msg.InfoHash!,
                    PeerId = peer.PeerId,
                    Offer = sdp,
                    OfferId = offerId.GetString(),
                });
                await SendTextAsync(candidates[idx], forward);
                idx++;
            }
        }

        bool hasAnswer = msg.Answer is JsonElement ae && ae.ValueKind == JsonValueKind.Object;
        if (hasAnswer && !string.IsNullOrEmpty(msg.ToPeerId) && !string.IsNullOrEmpty(msg.OfferId))
        {
            if (room.Peers.TryGetValue(msg.ToPeerId, out var target) && target.WebSocket.State == WebSocketState.Open)
            {
                var forward = BinaryJsonSerializer.Serialize(new RelayMessage
                {
                    Action = "announce",
                    InfoHash = msg.InfoHash!,
                    PeerId = peer.PeerId,
                    Answer = (JsonElement)msg.Answer!,
                    OfferId = msg.OfferId,
                });
                await SendTextAsync(target, forward);
            }
        }
    }

    private async Task RelayOfferAsync(SignalingPeer peer, WireMessage msg)
    {
        if (string.IsNullOrEmpty(msg.InfoHash) || string.IsNullOrEmpty(msg.ToPeerId)) return;
        if (!_rooms.TryGetValue(msg.InfoHash, out var room)) return;
        if (!room.Peers.TryGetValue(msg.ToPeerId, out var target)) return;

        var forward = BinaryJsonSerializer.Serialize(new RelayMessage
        {
            Action = "offer",
            InfoHash = msg.InfoHash!,
            PeerId = peer.PeerId,
            Offer = msg.Offer,
            OfferId = msg.OfferId,
        });
        await SendTextAsync(target, forward);
    }

    private async Task RelayAnswerAsync(SignalingPeer peer, WireMessage msg)
    {
        if (string.IsNullOrEmpty(msg.InfoHash) || string.IsNullOrEmpty(msg.ToPeerId)) return;
        if (!_rooms.TryGetValue(msg.InfoHash, out var room)) return;
        if (!room.Peers.TryGetValue(msg.ToPeerId, out var target)) return;

        var forward = BinaryJsonSerializer.Serialize(new RelayMessage
        {
            Action = "answer",
            InfoHash = msg.InfoHash!,
            PeerId = peer.PeerId,
            Answer = msg.Answer,
            OfferId = msg.OfferId,
        });
        await SendTextAsync(target, forward);
    }

    private async Task SendTextAsync(SignalingPeer peer, string text)
    {
        if (peer.WebSocket.State != WebSocketState.Open) return;
        if (!await peer.SendLock.WaitAsync(_options.SendTimeoutMs / 2)) return;
        try
        {
            if (peer.WebSocket.State != WebSocketState.Open) return;
            using var cts = new CancellationTokenSource(_options.SendTimeoutMs);
            var bytes = Encoding.UTF8.GetBytes(text);
            await peer.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { peer.SendLock.Release(); }
    }
}

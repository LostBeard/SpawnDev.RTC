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

    // Explicit TypeInfoResolver so deserialization works under file-based `dotnet run script.cs`
    // hosts and AOT/trimmed publishes. Matches BinaryJsonSerializer's serializer options.
    private static readonly JsonSerializerOptions _readOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    // Diagnostic file logging gated on env var. When `RTC_TRACKER_DEBUG_LOG` is set
    // to a path, every WS lifecycle event + frame parse + dispatch + send writes a
    // line to that file. Used to root-cause the hub-vs-local divergence where same
    // DLL bytes behave differently across environments.
    private static readonly string? _debugLogPath = Environment.GetEnvironmentVariable("RTC_TRACKER_DEBUG_LOG");
    private static readonly object _debugLogLock = new();
    private static void Diag(string msg)
    {
        if (string.IsNullOrEmpty(_debugLogPath)) return;
        try
        {
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {msg}{Environment.NewLine}";
            lock (_debugLogLock)
            {
                File.AppendAllText(_debugLogPath, line);
            }
        }
        catch { /* never let logging break the service */ }
    }

    public TrackerSignalingServer(TrackerServerOptions? options = null)
    {
        _options = options ?? new TrackerServerOptions();
    }

    /// <summary>Active rooms keyed by 20-byte wire room key.</summary>
    public IReadOnlyDictionary<string, SignalingRoomInfo> Rooms => _rooms;

    /// <summary>Total peers across all rooms.</summary>
    public int TotalPeers => _rooms.Values.Sum(r => r.Peers.Count);

    /// <summary>
    /// True if a peer with the given <paramref name="peerId"/> is currently
    /// connected (announced in at least one room). Enables tracker-gated TURN:
    /// a <see cref="StunTurnServerOptions.ResolveHmacKey"/> delegate can check
    /// this before issuing a TURN allocation, so only clients that have first
    /// established a signaling session get relay access.
    /// </summary>
    public bool IsPeerConnected(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return false;
        foreach (var room in _rooms.Values)
            if (room.Peers.ContainsKey(peerId)) return true;
        return false;
    }

    /// <summary>
    /// Snapshot of every currently-connected peer id across all rooms. Useful
    /// for admin / monitoring endpoints; do not call on the hot path - the
    /// result is a fresh allocation each time. For single-peer lookups use
    /// <see cref="IsPeerConnected"/>.
    /// </summary>
    public IReadOnlyCollection<string> ConnectedPeerIds
    {
        get
        {
            var set = new HashSet<string>();
            foreach (var room in _rooms.Values)
                foreach (var peerId in room.Peers.Keys)
                    set.Add(peerId);
            return set;
        }
    }

    /// <summary>
    /// Handle one WebSocket connection. Call from an ASP.NET Core endpoint that
    /// has already accepted the upgrade (e.g. through <see cref="Extensions.SignalingAppBuilderExtensions.UseRtcSignaling"/>).
    /// </summary>
    public async Task HandleWebSocketAsync(HttpContext context)
    {
        var connId = context.Connection.Id;
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        Diag($"[{connId}] handler entry remote={remote} isWS={context.WebSockets.IsWebSocketRequest} origin='{context.Request.Headers.Origin}' ua='{context.Request.Headers.UserAgent}'");
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            Diag($"[{connId}] not a WS request, returning 400");
            return;
        }

        // Origin allowlist (basic abuse protection). Only runs when the consumer
        // has opted in by populating TrackerServerOptions.AllowedOrigins. Rejects
        // the upgrade with 403 if the client's Origin header does not match.
        //
        // An empty/missing Origin header bypasses the check: per the feature's
        // stated purpose (browser-origin abuse protection) and per RFC 6454 §7,
        // browsers always send Origin on WebSocket upgrades. A missing Origin
        // means a non-browser client (desktop C# using ClientWebSocket,
        // Node.js ws library without explicit Origin, curl, etc.) - those
        // cannot be abused from a hostile page and the allowlist does not apply.
        // The `AllowedOrigins` XML doc is explicit that "Origin is set by the
        // browser and can be spoofed by non-browser clients; this is not a strong
        // authentication mechanism." So an attacker who wants to bypass is trivial
        // either way; legitimate non-browser consumers should not be blocked.
        if (_options.AllowedOrigins is { Count: > 0 } allowList)
        {
            var originHeader = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(originHeader) && !IsOriginAllowed(originHeader, allowList))
            {
                _options.Log?.Invoke($"[RTC.Server] rejected upgrade - Origin '{originHeader}' not in allowlist");
                context.Response.StatusCode = 403;
                return;
            }
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        Diag($"[{connId}] WS accepted, entering receive loop");
        var peer = new SignalingPeer(ws, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        peer.ConnId = connId;

        try
        {
            await ReceiveLoopAsync(peer);
            Diag($"[{connId}] receive loop returned cleanly");
        }
        catch (WebSocketException wsex)
        {
            Diag($"[{connId}] WebSocketException: {wsex.Message} ({wsex.WebSocketErrorCode})");
            _options.Log?.Invoke($"[RTC.Server] WebSocket receive aborted from {peer.RemoteAddress}: {wsex.Message}");
        }
        catch (Microsoft.AspNetCore.Connections.ConnectionResetException)
        {
            Diag($"[{connId}] ConnectionResetException");
            _options.Log?.Invoke($"[RTC.Server] Connection reset by {peer.RemoteAddress}");
        }
        catch (IOException ioex)
        {
            Diag($"[{connId}] IOException: {ioex.Message}");
            _options.Log?.Invoke($"[RTC.Server] IO error on peer {peer.RemoteAddress}: {ioex.Message}");
        }
        catch (Exception ex)
        {
            Diag($"[{connId}] UNHANDLED Exception: {ex.GetType().FullName}: {ex.Message}");
            Diag($"[{connId}] stack: {ex.StackTrace}");
            throw;
        }
        finally
        {
            foreach (var room in _rooms.Values)
                room.Peers.TryRemove(peer.PeerId, out _);
            foreach (var kvp in _rooms.Where(r => r.Value.Peers.IsEmpty).ToArray())
                _rooms.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// Match an Origin header against the configured allowlist. Accepts exact
    /// matches (case-insensitive) and wildcard-subdomain entries of the form
    /// <c>https://*.example.com</c>. Exposed so consumers can apply the same
    /// semantics in their own middleware (e.g. for HTTP endpoints that mirror
    /// the signaling endpoint's abuse protection).
    /// </summary>
    public static bool IsOriginAllowed(string origin, IReadOnlyList<string> allowList)
    {
        if (string.IsNullOrEmpty(origin)) return false;

        foreach (var entry in allowList)
        {
            if (string.IsNullOrEmpty(entry)) continue;

            // Exact match.
            if (origin.Equals(entry, StringComparison.OrdinalIgnoreCase)) return true;

            // Wildcard-subdomain form: "scheme://*.example.com" matches
            // "scheme://sub.example.com" or "scheme://deep.sub.example.com".
            // Does NOT match bare "scheme://example.com".
            var starIdx = entry.IndexOf("://*.", StringComparison.Ordinal);
            if (starIdx > 0)
            {
                var scheme = entry.Substring(0, starIdx + 3); // "scheme://"
                var suffix = entry.Substring(starIdx + 4);    // ".example.com"
                if (origin.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) &&
                    origin.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    origin.Length > scheme.Length + suffix.Length)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private async Task ReceiveLoopAsync(SignalingPeer peer)
    {
        var buffer = new byte[16384];
        var connId = peer.ConnId;
        Diag($"[{connId}] receive loop start, state={peer.WebSocket.State}");

        while (peer.WebSocket.State == WebSocketState.Open)
        {
            using var frame = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                Diag($"[{connId}] awaiting ReceiveAsync...");
                result = await peer.WebSocket.ReceiveAsync(buffer, CancellationToken.None);
                Diag($"[{connId}] ReceiveAsync returned: count={result.Count} type={result.MessageType} eom={result.EndOfMessage}");
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Complete the close handshake so the client's CloseAsync
                    // returns cleanly instead of throwing a premature-EOF error.
                    try
                    {
                        using var cts = new CancellationTokenSource(_options.SendTimeoutMs);
                        await peer.WebSocket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure, "", cts.Token);
                    }
                    catch { /* client may have already torn down - don't mask cleanup */ }
                    return;
                }
                frame.Write(buffer, 0, result.Count);
                if (frame.Length > _options.MaxMessageBytes)
                {
                    _options.Log?.Invoke($"[RTC.Server] dropped oversize frame from {peer.RemoteAddress}");
                    return;
                }
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                Diag($"[{connId}] non-text frame, skipping (type={result.MessageType})");
                continue;
            }

            try
            {
                var json = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
                Diag($"[{connId}] frame complete, json bytes={json.Length}, first 120 chars: {json.Substring(0, Math.Min(120, json.Length))}");
                var msg = JsonSerializer.Deserialize<WireMessage>(json, _readOpts);
                if (msg == null)
                {
                    Diag($"[{connId}] WireMessage deserialize returned null");
                    continue;
                }
                Diag($"[{connId}] parsed action={msg.Action} infoHash.len={msg.InfoHash?.Length ?? -1} peerId.len={msg.PeerId?.Length ?? -1} numwant={msg.NumWant} offers.kind={msg.Offers?.ValueKind}");

                switch (msg.Action)
                {
                    case "announce":
                        Diag($"[{connId}] dispatching to HandleAnnounceAsync");
                        await HandleAnnounceAsync(peer, msg);
                        Diag($"[{connId}] HandleAnnounceAsync returned");
                        break;
                    case "offer":
                        await RelayOfferAsync(peer, msg);
                        break;
                    case "answer":
                        await RelayAnswerAsync(peer, msg);
                        break;
                    default:
                        Diag($"[{connId}] unknown action '{msg.Action}'");
                        break;
                }
            }
            catch (Exception ex)
            {
                Diag($"[{connId}] frame handler exception: {ex.GetType().FullName}: {ex.Message}");
                Diag($"[{connId}] stack: {ex.StackTrace}");
                _options.Log?.Invoke($"[RTC.Server] frame parse error: {ex.Message}");
            }
        }
        Diag($"[{connId}] receive loop exiting, state={peer.WebSocket.State}");
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

        // Answer-relay path: when an announce carries an answer + to_peer_id + offer_id,
        // it is a reply to a forwarded offer. JS bittorrent-tracker forwards the answer
        // to the targeted peer and returns NO response to the sender. Mirror that here -
        // sending an extra announce-response in this case is a divergence the parity
        // harness flagged 2026-04-27 (verify-tracker-parity.mjs scenario D).
        bool hasAnswer = msg.Answer is JsonElement ae && ae.ValueKind == JsonValueKind.Object;
        bool isAnswerRelay = hasAnswer && !string.IsNullOrEmpty(msg.ToPeerId) && !string.IsNullOrEmpty(msg.OfferId);
        if (isAnswerRelay)
        {
            if (room.Peers.TryGetValue(msg.ToPeerId!, out var target) && target.WebSocket.State == WebSocketState.Open)
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
            return;
        }

        // Mutate room state BEFORE building the response so the counts in the response
        // reflect post-stop state - matches JS reference (parity-harness scenario E).
        if (msg.Event == "stopped")
        {
            room.Peers.TryRemove(peer.PeerId, out _);
        }
        else
        {
            room.Peers[peer.PeerId] = peer;
            if (msg.Event == "completed" || msg.Left == 0)
                peer.IsSeeder = true;
        }

        // Match the bittorrent-tracker JS reference (webtorrent/bittorrent-tracker
        // server.js): the WebSocket-tracker announce response carries info_hash +
        // interval + complete + incomplete only. NO peer list. Peer-list semantics
        // are a TCP/UDP-tracker thing; on the WebRTC path peer discovery happens
        // exclusively via forwarded offer/answer below. Verified against bittorrent-tracker
        // npm package by `tracker-debug/verify-tracker-parity.mjs` 2026-04-27.
        var response = BinaryJsonSerializer.Serialize(new AnnounceResponse
        {
            InfoHash = msg.InfoHash!,
            Interval = _options.AnnounceIntervalSeconds,
            Complete = room.SeederCount,
            Incomplete = room.LeechCount,
            Peers = null,
        });
        Diag($"[{peer.ConnId}] announce response built ({response.Length} chars), sending...");
        await SendTextAsync(peer, response);
        Diag($"[{peer.ConnId}] announce response sent OK");

        // After a stopped event the peer is gone - no offer forwarding, no further work.
        if (msg.Event == "stopped") return;

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

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpawnDev.RTC.Signaling;

/// <summary>
/// <see cref="ISignalingClient"/> backed by a WebTorrent-protocol tracker. Directly
/// talks to any tracker that speaks the bittorrent-tracker WebSocket wire format
/// (public trackers like <c>wss://tracker.openwebtorrent.com</c> as well as
/// <c>SpawnDev.RTC.Server.TrackerSignalingServer</c>).
///
/// Port of <c>SpawnDev.WebTorrent.WebSocketTracker</c> with the SimplePeer coupling
/// removed: offer generation, offer handling, and answer routing all go through the
/// caller-supplied <see cref="ISignalingRoomHandler"/>. The protocol math (binary-string
/// encoding, numwant caps, exponential reconnect backoff, shared socket pool) is
/// preserved bit-for-bit so this client interoperates with the same tracker universe.
/// </summary>
public sealed class TrackerSignalingClient : ISignalingClient
{
    // ========================
    // WIRE CONSTANTS (match bittorrent-tracker)
    // ========================
    public const int ReconnectMinimum = 10_000;
    public const int ReconnectMaximum = 3_600_000;     // 1 hour
    public const int ReconnectVariance = 300_000;       // 5 min
    public const int OfferTimeout = 50_000;             // 50s
    public const int DefaultAnnounceInterval = 120_000; // 120s
    public const int MaxOffers = 10;                    // JS: MAX_ANNOUNCE_PEERS

    /// <summary>Enable verbose wire logging to <see cref="Console"/>. Off by default.</summary>
    public static bool VerboseLogging { get; set; }

    // ========================
    // SHARED SOCKET POOL
    // One WebSocket per (announceUrl, peerId) shared across all rooms.
    // ========================
    private static readonly ConcurrentDictionary<string, TrackerSignalingClient> _socketPool = new();

    /// <summary>Dispose and drop every pooled tracker connection. Call between tests that spin up fresh peers.</summary>
    public static void ClearPool()
    {
        foreach (var t in _socketPool.Values)
            _ = t.DisposeAsync();
        _socketPool.Clear();
    }

    /// <summary>
    /// Get or create a shared <see cref="TrackerSignalingClient"/> for the given tracker URL and peer id.
    /// Different peer ids get distinct connections; the same (url, peerId) pair returns the same client.
    /// </summary>
    public static TrackerSignalingClient GetOrCreate(string announceUrl, byte[] peerId)
    {
        if (announceUrl is null) throw new ArgumentNullException(nameof(announceUrl));
        if (peerId is null) throw new ArgumentNullException(nameof(peerId));
        if (peerId.Length != 20) throw new ArgumentException("Peer id must be exactly 20 bytes.", nameof(peerId));

        var key = announceUrl + ":" + Convert.ToHexString(peerId);
        while (true)
        {
            if (_socketPool.TryGetValue(key, out var existing))
            {
                if (!existing.Destroyed) return existing;
                _socketPool.TryRemove(new KeyValuePair<string, TrackerSignalingClient>(key, existing));
            }
            var created = new TrackerSignalingClient(announceUrl, peerId);
            if (_socketPool.TryAdd(key, created)) return created;
            _ = created.DisposeAsync();
        }
    }

    // ========================
    // PUBLIC STATE
    // ========================
    public string AnnounceUrl { get; }
    public byte[] LocalPeerId { get; }
    public bool IsConnected => _connected && !Destroyed;
    public bool Destroyed { get; private set; }
    public bool Reconnecting { get; private set; }
    public int Retries { get; private set; }

    // ========================
    // PRIVATE STATE
    // ========================
    private readonly string _peerIdBinary;
    private ClientWebSocket? _ws;
    private bool _connected;
    private string? _trackerId;
    private Timer? _reconnectTimer;
    private Timer? _announceTimer;
    private CancellationTokenSource? _readCts;

    // Queued announces for before the socket opens.
    private readonly List<(RoomKey room, AnnounceOptions opts)> _pendingAnnounces = new();

    // Subscribed handlers keyed by the wire-format info_hash (latin1 binary string).
    // ConcurrentDictionary - offer/answer callbacks run on the read loop thread while
    // consumers may Subscribe/Unsubscribe from any thread.
    private readonly ConcurrentDictionary<string, ISignalingRoomHandler> _handlers = new();

    // Wire-format info_hash → RoomKey, so we can surface strongly-typed keys in callbacks
    // without rehashing on every message.
    private readonly ConcurrentDictionary<string, RoomKey> _roomKeys = new();

    // Outstanding offers we've sent. offer-id hex → (room, timeout timer).
    // The room is needed so answer responses can be routed back to the correct handler.
    private readonly ConcurrentDictionary<string, (RoomKey room, Timer? timer)> _pendingOffers = new();

    // ========================
    // EVENTS
    // ========================
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnWarning;
    public event Action<RoomKey, SignalingSwarmStats>? OnSwarmStats;

    // ========================
    // CONSTRUCTOR
    // ========================
    public TrackerSignalingClient(string announceUrl, byte[] peerId)
    {
        if (announceUrl is null) throw new ArgumentNullException(nameof(announceUrl));
        if (peerId is null) throw new ArgumentNullException(nameof(peerId));
        if (peerId.Length != 20) throw new ArgumentException("Peer id must be exactly 20 bytes.", nameof(peerId));

        AnnounceUrl = announceUrl;
        LocalPeerId = (byte[])peerId.Clone();
        _peerIdBinary = ToBinaryString(LocalPeerId);
        _ = OpenSocketAsync();
    }

    // ========================
    // SUBSCRIBE / UNSUBSCRIBE
    // ========================
    public void Subscribe(RoomKey roomKey, ISignalingRoomHandler handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        var wire = roomKey.ToWireString();
        _handlers[wire] = handler;
        _roomKeys[wire] = roomKey;
    }

    public void Unsubscribe(RoomKey roomKey)
    {
        var wire = roomKey.ToWireString();
        _handlers.TryRemove(wire, out _);
        _roomKeys.TryRemove(wire, out _);
    }

    // ========================
    // ANNOUNCE
    // ========================
    public async Task AnnounceAsync(RoomKey roomKey, AnnounceOptions options, CancellationToken ct = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (Destroyed || Reconnecting)
        {
            if (VerboseLogging)
                Console.WriteLine($"[TrackerSignaling] Announce skipped: destroyed={Destroyed} reconnecting={Reconnecting}");
            return;
        }

        var infoHashBinary = roomKey.ToWireString();
        _roomKeys.TryAdd(infoHashBinary, roomKey);

        if (!_connected)
        {
            if (VerboseLogging)
                Console.WriteLine($"[TrackerSignaling] Announce queued, socket not ready ({AnnounceUrl})");
            lock (_pendingAnnounces) _pendingAnnounces.Add((roomKey, options));
            return;
        }

        if (VerboseLogging)
            Console.WriteLine($"[TrackerSignaling] Announce {AnnounceUrl} room={roomKey.ToHex()[..8]}... event={options.Event ?? "(periodic)"}");

        var msg = new TrackerAnnounceMessage
        {
            InfoHash = infoHashBinary,
            PeerId = _peerIdBinary,
            Uploaded = options.Uploaded,
            Downloaded = options.Downloaded,
            Left = options.Left,
            Event = !string.IsNullOrEmpty(options.Event) ? options.Event : null,
            TrackerId = _trackerId,
        };

        if (options.Event == "stopped")
        {
            msg.NumWant = 0;
            await SendAsync(msg, ct).ConfigureAwait(false);
        }
        else if (options.Event == "completed")
        {
            // Match JS: "completed" wants to discover new leechers but doesn't send offers.
            msg.NumWant = Math.Min(options.NumWant > 0 ? options.NumWant : 50, MaxOffers);
            await SendAsync(msg, ct).ConfigureAwait(false);
        }
        else
        {
            int numwant = Math.Min(options.NumWant, MaxOffers);
            msg.NumWant = numwant;

            if (_handlers.TryGetValue(infoHashBinary, out var handler) && numwant > 0)
            {
                var offers = await BuildOffersAsync(handler, numwant, roomKey, ct).ConfigureAwait(false);
                msg.Offers = offers.Count > 0 ? offers.ToArray() : null;
            }

            await SendAsync(msg, ct).ConfigureAwait(false);
        }
    }

    // ========================
    // OFFER GENERATION
    // ========================
    private async Task<List<object>> BuildOffersAsync(
        ISignalingRoomHandler handler, int count, RoomKey room, CancellationToken ct)
    {
        IReadOnlyList<SignalingOffer> offers;
        try
        {
            offers = await handler.CreateOffersAsync(count, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"Offer generation failed: {ex.Message}");
            return new List<object>();
        }

        var results = new List<object>(offers.Count);
        foreach (var offer in offers)
        {
            if (offer.OfferId is null || offer.OfferId.Length != 20)
            {
                OnWarning?.Invoke($"Handler produced invalid offer id length {offer.OfferId?.Length ?? -1}, expected 20.");
                continue;
            }
            if (string.IsNullOrEmpty(offer.OfferSdp))
            {
                OnWarning?.Invoke("Handler produced empty offer SDP.");
                continue;
            }

            var offerIdHex = Convert.ToHexString(offer.OfferId).ToLowerInvariant();
            var timer = new Timer(static (s) =>
            {
                var (dict, hex) = ((ConcurrentDictionary<string, (RoomKey, Timer?)>, string))s!;
                dict.TryRemove(hex, out _);
            }, (_pendingOffers, offerIdHex), OfferTimeout, Timeout.Infinite);

            _pendingOffers[offerIdHex] = (room, timer);

            results.Add(new
            {
                offer = new { type = "offer", sdp = offer.OfferSdp },
                offer_id = ToBinaryString(offer.OfferId),
            });
        }
        return results;
    }

    // ========================
    // SOCKET LIFECYCLE
    // ========================
    private async Task OpenSocketAsync()
    {
        Destroyed = false;
        try
        {
            if (VerboseLogging) Console.WriteLine($"[TrackerSignaling] Connecting to {AnnounceUrl}...");
            _ws = new ClientWebSocket();
            _readCts = new CancellationTokenSource();
            await _ws.ConnectAsync(new Uri(AnnounceUrl), CancellationToken.None).ConfigureAwait(false);
            _connected = true;
            if (VerboseLogging) Console.WriteLine($"[TrackerSignaling] Connected to {AnnounceUrl}");

            var wasReconnecting = Reconnecting;
            Reconnecting = false;
            Retries = 0;

            OnConnected?.Invoke();

            (RoomKey room, AnnounceOptions opts)[] pending;
            lock (_pendingAnnounces)
            {
                pending = _pendingAnnounces.ToArray();
                _pendingAnnounces.Clear();
            }
            if (pending.Length > 0 && VerboseLogging)
                Console.WriteLine($"[TrackerSignaling] Flushing {pending.Length} queued announces to {AnnounceUrl}");
            foreach (var (room, opts) in pending)
                _ = AnnounceAsync(room, opts);

            _ = ReadLoopAsync(_readCts.Token);
            _ = wasReconnecting; // reserved for future reconnect telemetry
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"WebSocket connect failed: {ex.Message}");
            StartReconnectTimer();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnSocketClose();
                    return;
                }

                using var ms = new MemoryStream();
                ms.Write(buffer, 0, result.Count);
                while (!result.EndOfMessage)
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    ms.Write(buffer, 0, result.Count);
                }

                var json = Encoding.UTF8.GetString(ms.ToArray());
                OnSocketData(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { OnSocketClose(); }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"WebSocket read error: {ex.Message}");
            OnSocketClose();
        }
    }

    private void OnSocketData(string json)
    {
        if (Destroyed) return;

        if (VerboseLogging)
        {
            var preview = json.Length > 200 ? json[..200] + "..." : json;
            Console.WriteLine($"[TrackerSignaling] RECV from {AnnounceUrl}: {preview}");
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch
        {
            OnWarning?.Invoke("Invalid tracker response JSON");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var actionProp) ? actionProp.GetString() : null;

            if (action == "announce")
                _ = OnAnnounceResponseAsync(root.Clone());
            else if (action == "scrape")
            {
                // Scrape responses not surfaced yet. Future work.
            }
            else if (root.TryGetProperty("offer", out _) || root.TryGetProperty("answer", out _))
                _ = OnAnnounceResponseAsync(root.Clone());
            else if (VerboseLogging)
                Console.WriteLine($"[TrackerSignaling] Unknown action from {AnnounceUrl}: {action ?? "(none)"}");
        }
    }

    private async Task OnAnnounceResponseAsync(JsonElement data)
    {
        string responseInfoHash = data.TryGetProperty("info_hash", out var ihProp) ? (ihProp.GetString() ?? "") : "";

        // Ignore echoes of our own announces.
        if (data.TryGetProperty("peer_id", out var pidProp))
        {
            var responsePeerId = pidProp.GetString() ?? "";
            if (responsePeerId == _peerIdBinary) return;
        }

        if (data.TryGetProperty("failure reason", out var failProp))
        {
            OnWarning?.Invoke(failProp.GetString() ?? "tracker failure");
            return;
        }

        if (data.TryGetProperty("warning message", out var warnProp))
            OnWarning?.Invoke(warnProp.GetString() ?? "");

        if (data.TryGetProperty("interval", out var intProp) && intProp.TryGetInt32(out var interval))
            SetAnnounceInterval(interval * 1000);
        else if (data.TryGetProperty("min interval", out var minIntProp) && minIntProp.TryGetInt32(out var minInterval))
            SetAnnounceInterval(minInterval * 1000);

        if (data.TryGetProperty("tracker id", out var tidProp))
            _trackerId = tidProp.GetString();

        if (data.TryGetProperty("complete", out _))
        {
            var stats = new SignalingSwarmStats(
                data.TryGetProperty("complete", out var cp) ? cp.GetInt32() : 0,
                data.TryGetProperty("incomplete", out var ip) ? ip.GetInt32() : 0);
            if (_roomKeys.TryGetValue(responseInfoHash, out var rk))
                OnSwarmStats?.Invoke(rk, stats);
        }

        // Incoming offer from a remote peer — ask handler for an answer and relay it.
        if (data.TryGetProperty("offer", out var offerProp) && data.TryGetProperty("peer_id", out var offerPeerId))
        {
            var remotePeerBinary = offerPeerId.GetString() ?? "";
            var remotePeerId = FromBinaryString(remotePeerBinary);
            var offerIdBinary = data.TryGetProperty("offer_id", out var oidProp) ? (oidProp.GetString() ?? "") : "";
            var offerIdBytes = FromBinaryString(offerIdBinary);
            var offerSdp = offerProp.TryGetProperty("sdp", out var sdpProp) ? (sdpProp.GetString() ?? "") : "";

            if (!_handlers.TryGetValue(responseInfoHash, out var handler))
            {
                if (VerboseLogging)
                    Console.WriteLine($"[TrackerSignaling] Offer for unsubscribed room {responseInfoHash.Length} bytes, dropping.");
                return;
            }

            string? answerSdp;
            try
            {
                answerSdp = await handler.HandleOfferAsync(remotePeerId, offerIdBytes, offerSdp, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnWarning?.Invoke($"HandleOfferAsync threw: {ex.Message}");
                return;
            }
            if (string.IsNullOrEmpty(answerSdp)) return;

            var answerMsg = new TrackerAnswerMessage
            {
                InfoHash = responseInfoHash,
                PeerId = _peerIdBinary,
                ToPeerId = remotePeerBinary,
                Answer = new { type = "answer", sdp = answerSdp },
                OfferId = offerIdBinary,
                TrackerId = _trackerId,
            };
            await SendAsync(answerMsg, CancellationToken.None).ConfigureAwait(false);
        }

        // Incoming answer to one of our offers — route to the originating handler.
        if (data.TryGetProperty("answer", out var answerProp) && data.TryGetProperty("offer_id", out var answerOidProp))
        {
            var offerIdBinary = answerOidProp.GetString() ?? "";
            var offerIdHex = BinaryStringToHex(offerIdBinary);

            if (!_pendingOffers.TryRemove(offerIdHex, out var entry))
            {
                if (VerboseLogging)
                    Console.WriteLine($"[TrackerSignaling] Answer for unknown offer_id={offerIdHex[..Math.Min(8, offerIdHex.Length)]}..., dropping.");
                return;
            }
            entry.timer?.Dispose();

            var wire = entry.room.ToWireString();
            if (!_handlers.TryGetValue(wire, out var handler)) return;

            var remotePeerBinary = data.TryGetProperty("peer_id", out var ansPeerProp) ? (ansPeerProp.GetString() ?? "") : "";
            var remotePeerId = FromBinaryString(remotePeerBinary);
            var offerIdBytes = FromBinaryString(offerIdBinary);
            var answerSdp = answerProp.TryGetProperty("sdp", out var aSdpProp) ? (aSdpProp.GetString() ?? "") : "";

            try
            {
                await handler.HandleAnswerAsync(remotePeerId, offerIdBytes, answerSdp, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnWarning?.Invoke($"HandleAnswerAsync threw: {ex.Message}");
            }
        }
    }

    private void OnSocketClose()
    {
        if (Destroyed) return;
        if (_connected)
        {
            _connected = false;
            OnDisconnected?.Invoke();
        }
        StartReconnectTimer();
    }

    // ========================
    // RECONNECT (exponential backoff, matches JS exactly)
    // ========================
    private void StartReconnectTimer()
    {
        Retries++;
        int ms = RandomNumberGenerator.GetInt32(ReconnectVariance) +
                 Math.Min((int)Math.Pow(2, Retries) * ReconnectMinimum, ReconnectMaximum);

        Reconnecting = true;
        _reconnectTimer?.Dispose();
        _reconnectTimer = new Timer(_ =>
        {
            _ = OpenSocketAsync();
        }, null, ms, Timeout.Infinite);
    }

    // ========================
    // SEND
    // ========================
    private async Task SendAsync(object msg, CancellationToken ct)
    {
        if (Destroyed || _ws?.State != WebSocketState.Open) return;

        var json = BinaryJsonSerializer.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(json);

        if (VerboseLogging)
        {
            var preview = json.Length > 200 ? json[..200] + "..." : json;
            Console.WriteLine($"[TrackerSignaling] SEND to {AnnounceUrl}: {preview}");
        }

        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    // ========================
    // ANNOUNCE INTERVAL
    // ========================
    private void SetAnnounceInterval(int ms)
    {
        if (ms <= 0) return;
        _announceTimer?.Dispose();
        _announceTimer = new Timer(_ => { /* reserved for future periodic re-announce */ }, null, ms, ms);
    }

    // ========================
    // BINARY STRING ENCODING (matches JS hex2bin/bin2hex exactly)
    // ========================
    /// <summary>Bytes → latin1 char-per-byte binary string. Matches JS <c>hex2bin</c>.</summary>
    public static string ToBinaryString(byte[] bytes)
        => new string(bytes.Select(b => (char)b).ToArray());

    /// <summary>Binary string → byte array.</summary>
    public static byte[] FromBinaryString(string binaryString)
    {
        var buf = new byte[binaryString.Length];
        for (int i = 0; i < binaryString.Length; i++) buf[i] = (byte)binaryString[i];
        return buf;
    }

    /// <summary>Binary string → lowercase hex. Matches JS <c>bin2hex</c>.</summary>
    public static string BinaryStringToHex(string binaryString)
        => Convert.ToHexString(FromBinaryString(binaryString)).ToLowerInvariant();

    // ========================
    // DISPOSE
    // ========================
    public async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;
        _connected = false;

        _reconnectTimer?.Dispose();
        _announceTimer?.Dispose();
        _readCts?.Cancel();

        foreach (var kvp in _pendingOffers)
            kvp.Value.timer?.Dispose();
        _pendingOffers.Clear();

        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        _ws?.Dispose();
        _ws = null;

        var poolKey = AnnounceUrl + ":" + Convert.ToHexString(LocalPeerId);
        _socketPool.TryRemove(new KeyValuePair<string, TrackerSignalingClient>(poolKey, this));
    }
}

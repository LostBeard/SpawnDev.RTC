using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnDev.RTC
{
    /// <summary>
    /// Lightweight WebTorrent tracker client for serverless WebRTC signaling.
    /// Connects to any public WebTorrent tracker (e.g., openwebtorrent) and uses
    /// the BitTorrent tracker protocol for peer discovery and SDP exchange.
    ///
    /// No server deployment needed - uses existing public tracker infrastructure.
    ///
    /// Usage:
    ///   var tracker = new RTCTrackerClient("wss://tracker.openwebtorrent.com", "my-room-name");
    ///   tracker.OnPeerConnection += (pc, peerId) => { /* peer connected */ };
    ///   await tracker.JoinAsync();
    /// </summary>
    public class RTCTrackerClient : IDisposable
    {
        private ClientWebSocket? _ws;
        private readonly string _trackerUrl;
        private readonly string _infoHashHex;
        private readonly string _infoHashBinary;
        private readonly string _peerId;
        private readonly RTCPeerConnectionConfig? _config;
        private readonly Dictionary<string, IRTCPeerConnection> _peers = new();
        private readonly Dictionary<string, IRTCPeerConnection> _pendingOffers = new(); // offerId -> pc
    private readonly Dictionary<string, IRTCDataChannel> _pendingChannels = new(); // offerId -> local data channel
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private int _numwant = 5;
        private string? _trackerId;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// This client's peer ID (20-char BitTorrent format).
        /// </summary>
        public string PeerId => _peerId;

        /// <summary>
        /// The info hash for this swarm (hex string).
        /// </summary>
        public string InfoHash => _infoHashHex;

        /// <summary>
        /// Number of peers to request from the tracker.
        /// </summary>
        public int NumWant { get => _numwant; set => _numwant = value; }

        /// <summary>
        /// Connected peer IDs.
        /// </summary>
        public IReadOnlyCollection<string> ConnectedPeers => _peers.Keys.ToList().AsReadOnly();

        // --- Events ---

        /// <summary>
        /// Fired when a new peer connection is established.
        /// </summary>
        public event Action<IRTCPeerConnection, string>? OnPeerConnection;

        /// <summary>
        /// Fired when a data channel is opened by a remote peer.
        /// </summary>
        public event Action<IRTCDataChannel, string>? OnDataChannel;

        /// <summary>
        /// Fired when a peer disconnects.
        /// </summary>
        public event Action<string>? OnPeerDisconnected;

        /// <summary>
        /// Fired when connected to the tracker.
        /// </summary>
        public event Action? OnConnected;

        /// <summary>
        /// Fired when disconnected from the tracker.
        /// </summary>
        public event Action? OnDisconnected;

        /// <summary>
        /// Optional: configure each new peer connection before offer/answer.
        /// </summary>
        public Func<IRTCPeerConnection, string, Task>? OnPeerConnectionCreated { get; set; }

        /// <summary>
        /// Creates a tracker client for the specified room name.
        /// </summary>
        /// <param name="trackerUrl">WebSocket tracker URL (e.g., wss://tracker.openwebtorrent.com)</param>
        /// <param name="roomName">Room name - hashed to an infohash for the swarm</param>
        /// <param name="config">Optional peer connection config</param>
        public RTCTrackerClient(string trackerUrl, string roomName, RTCPeerConnectionConfig? config = null)
        {
            _trackerUrl = trackerUrl;
            _config = config ?? new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            // Generate infohash from room name (SHA-1, 20 bytes)
            var hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(roomName.Trim().ToLowerInvariant()));
            _infoHashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            _infoHashBinary = Encoding.Latin1.GetString(hashBytes);

            // Generate peer ID in BitTorrent format: -SR0100-xxxxxxxxxxxx
            var random = new byte[12];
            RandomNumberGenerator.Fill(random);
            _peerId = "-SR0100-" + Convert.ToHexString(random)[..12];
        }

        /// <summary>
        /// Connect to the tracker and join the swarm.
        /// </summary>
        public async Task JoinAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ws = new ClientWebSocket();

            if (!OperatingSystem.IsBrowser())
            {
                _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            await _ws.ConnectAsync(new Uri(_trackerUrl), _cts.Token);
            OnConnected?.Invoke();

            // Start message loop
            _ = Task.Run(() => MessageLoop(_cts.Token), _cts.Token);

            // Send initial announce with offers
            await AnnounceWithOffers();
        }

        /// <summary>
        /// Leave the swarm and disconnect.
        /// </summary>
        public async Task LeaveAsync()
        {
            // Send stopped event
            if (_ws?.State == WebSocketState.Open)
            {
                try
                {
                    await SendAsync(new
                    {
                        action = "announce",
                        info_hash = _infoHashBinary,
                        peer_id = _peerId,
                        uploaded = 0,
                        downloaded = 0,
                        left = 0,
                        @event = "stopped",
                        numwant = 0,
                    });
                }
                catch { }
            }

            _cts?.Cancel();
            if (_ws?.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { }
            }

            foreach (var pc in _peers.Values)
            {
                pc.Close();
                pc.Dispose();
            }
            _peers.Clear();

            foreach (var pc in _pendingOffers.Values)
            {
                pc.Close();
                pc.Dispose();
            }
            _pendingOffers.Clear();

            OnDisconnected?.Invoke();
        }

        private async Task AnnounceWithOffers()
        {
            // Create peer connections and offers for numwant peers
            var offers = new List<object>();
            for (int i = 0; i < _numwant; i++)
            {
                var pc = RTCPeerConnectionFactory.Create(_config);
                // Create a data channel so the offer includes an application media line
                var localDc = pc.CreateDataChannel("data");

                if (OnPeerConnectionCreated != null)
                    await OnPeerConnectionCreated(pc, "");

                var offer = await pc.CreateOffer();
                await pc.SetLocalDescription(offer);

                // Generate offer ID (20 bytes as binary string)
                var offerIdBytes = new byte[20];
                RandomNumberGenerator.Fill(offerIdBytes);
                var offerId = Encoding.Latin1.GetString(offerIdBytes);

                _pendingOffers[offerId] = pc;
                _pendingChannels[offerId] = localDc;

                offers.Add(new
                {
                    offer = new { type = "offer", sdp = offer.Sdp },
                    offer_id = offerId,
                });
            }

            await SendAsync(new
            {
                action = "announce",
                info_hash = _infoHashBinary,
                peer_id = _peerId,
                uploaded = 0,
                downloaded = 0,
                left = 1,
                @event = "started",
                numwant = _numwant,
                offers,
            });
        }

        private async Task MessageLoop(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var msg = JsonSerializer.Deserialize<JsonElement>(json);

                    await HandleMessage(msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                OnDisconnected?.Invoke();
            }
        }

        private async Task HandleMessage(JsonElement msg)
        {
            if (!msg.TryGetProperty("action", out var actionEl)) return;
            var action = actionEl.GetString();
            if (action != "announce") return;

            // Store tracker ID if provided
            if (msg.TryGetProperty("trackerid", out var tid))
                _trackerId = tid.GetString();

            // Offer relay: another peer sent us an offer
            if (msg.TryGetProperty("offer", out var offerEl) && msg.TryGetProperty("offer_id", out var offerIdEl))
            {
                var remotePeerId = msg.GetProperty("peer_id").GetString()!;
                var offerId = offerIdEl.GetString()!;
                var offerSdp = offerEl.GetProperty("sdp").GetString()!;

                await HandleIncomingOffer(remotePeerId, offerId, offerSdp);
            }
            // Answer relay: a peer responded to our offer
            else if (msg.TryGetProperty("answer", out var answerEl) && msg.TryGetProperty("offer_id", out var ansOfferIdEl))
            {
                var remotePeerId = msg.GetProperty("peer_id").GetString()!;
                var offerId = ansOfferIdEl.GetString()!;
                var answerSdp = answerEl.GetProperty("sdp").GetString()!;

                await HandleIncomingAnswer(remotePeerId, offerId, answerSdp);
            }
        }

        private async Task HandleIncomingOffer(string remotePeerId, string offerId, string offerSdp)
        {
            if (_peers.ContainsKey(remotePeerId)) return; // Already connected

            var pc = RTCPeerConnectionFactory.Create(_config);
            if (OnPeerConnectionCreated != null)
                await OnPeerConnectionCreated(pc, remotePeerId);

            WireEvents(pc, remotePeerId);
            _peers[remotePeerId] = pc;

            await pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "offer", Sdp = offerSdp });
            var answer = await pc.CreateAnswer();
            await pc.SetLocalDescription(answer);

            // Send answer back through tracker
            var answerMsg = new Dictionary<string, object?>
            {
                ["action"] = "announce",
                ["info_hash"] = _infoHashBinary,
                ["peer_id"] = _peerId,
                ["to_peer_id"] = remotePeerId,
                ["answer"] = new { type = "answer", sdp = answer.Sdp },
                ["offer_id"] = offerId,
            };
            if (_trackerId != null) answerMsg["trackerid"] = _trackerId;

            await SendAsync(answerMsg);
            OnPeerConnection?.Invoke(pc, remotePeerId);
        }

        private async Task HandleIncomingAnswer(string remotePeerId, string offerId, string answerSdp)
        {
            if (!_pendingOffers.TryGetValue(offerId, out var pc)) return;
            _pendingOffers.Remove(offerId);

            WireEvents(pc, remotePeerId);
            _peers[remotePeerId] = pc;

            await pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "answer", Sdp = answerSdp });

            // Fire OnDataChannel for the local data channel we created in AnnounceWithOffers
            if (_pendingChannels.TryGetValue(offerId, out var localDc))
            {
                _pendingChannels.Remove(offerId);
                OnDataChannel?.Invoke(localDc, remotePeerId);
            }

            OnPeerConnection?.Invoke(pc, remotePeerId);
        }

        private void WireEvents(IRTCPeerConnection pc, string remotePeerId)
        {
            pc.OnDataChannel += channel =>
            {
                OnDataChannel?.Invoke(channel, remotePeerId);
            };

            pc.OnConnectionStateChange += state =>
            {
                if (state == "disconnected" || state == "failed" || state == "closed")
                {
                    _peers.Remove(remotePeerId);
                    OnPeerDisconnected?.Invoke(remotePeerId);
                }
            };
        }

        private async Task SendAsync(object message)
        {
            if (_ws?.State != WebSocketState.Open) return;
            var json = JsonSerializer.Serialize(message, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            foreach (var pc in _peers.Values)
            {
                try { pc.Close(); pc.Dispose(); } catch { }
            }
            foreach (var pc in _pendingOffers.Values)
            {
                try { pc.Close(); pc.Dispose(); } catch { }
            }
            _peers.Clear();
            _pendingOffers.Clear();
            _ws?.Dispose();
            _cts?.Dispose();
        }
    }
}

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SpawnDev.RTC
{
    /// <summary>
    /// Drop-in signaling client for SpawnDev.RTC.
    /// Connects to a WebSocket signal server, handles SDP offer/answer exchange
    /// and ICE candidate trickle automatically.
    ///
    /// Usage:
    ///   var signal = new RTCSignalClient("wss://server/signal/room-id");
    ///   signal.OnPeerConnection += (pc, peerId) => { /* new peer connected */ };
    ///   await signal.ConnectAsync();
    /// </summary>
    public class RTCSignalClient : IDisposable
    {
        private ClientWebSocket? _ws;
        private readonly string _signalUrl;
        private readonly RTCPeerConnectionConfig? _config;
        private readonly Dictionary<string, IRTCPeerConnection> _peers = new();
        private readonly Dictionary<string, List<RTCIceCandidateInit>> _pendingCandidates = new();
        private CancellationTokenSource? _cts;
        private bool _disposed;

        /// <summary>
        /// This client's peer ID (assigned by the signal server).
        /// </summary>
        public string? PeerId { get; private set; }

        /// <summary>
        /// Fired when a new peer connection is established.
        /// The IRTCPeerConnection is fully set up with data channels and media.
        /// </summary>
        public event Action<IRTCPeerConnection, string>? OnPeerConnection;

        /// <summary>
        /// Fired when a data channel opens on any peer connection.
        /// </summary>
        public event Action<IRTCDataChannel, string>? OnDataChannel;

        /// <summary>
        /// Fired when a peer disconnects.
        /// </summary>
        public event Action<string>? OnPeerDisconnected;

        /// <summary>
        /// Fired when connected to the signal server.
        /// </summary>
        public event Action? OnConnected;

        /// <summary>
        /// Fired when disconnected from the signal server.
        /// </summary>
        public event Action? OnDisconnected;

        /// <summary>
        /// Optional: factory function to configure each new peer connection.
        /// Called before the offer/answer exchange. Use this to add data channels
        /// or media tracks before negotiation.
        /// </summary>
        public Func<IRTCPeerConnection, string, Task>? OnPeerConnectionCreated { get; set; }

        public RTCSignalClient(string signalUrl, RTCPeerConnectionConfig? config = null)
        {
            _signalUrl = signalUrl;
            _config = config ?? new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = "stun:stun.l.google.com:19302" } }
            };
        }

        /// <summary>
        /// Connect to the signal server and start listening for peers.
        /// </summary>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ws = new ClientWebSocket();

            if (!OperatingSystem.IsBrowser())
            {
                _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            await _ws.ConnectAsync(new Uri(_signalUrl), _cts.Token);
            OnConnected?.Invoke();

            // Start message loop in background
            _ = Task.Run(() => MessageLoop(_cts.Token), _cts.Token);
        }

        /// <summary>
        /// Disconnect from the signal server and close all peer connections.
        /// </summary>
        public async Task DisconnectAsync()
        {
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
            OnDisconnected?.Invoke();
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
                    if (!msg.TryGetProperty("type", out var typeEl)) continue;

                    await HandleMessage(typeEl.GetString()!, msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                OnDisconnected?.Invoke();
            }
        }

        private async Task HandleMessage(string type, JsonElement msg)
        {
            switch (type)
            {
                case "welcome":
                    PeerId = msg.GetProperty("peerId").GetString()!;
                    var peers = msg.GetProperty("peers");
                    // Initiate connections to all existing peers
                    foreach (var peer in peers.EnumerateArray())
                    {
                        var remotePeerId = peer.GetString()!;
                        await InitiateConnection(remotePeerId);
                    }
                    break;

                case "peer-joined":
                    // New peer will initiate the connection to us
                    break;

                case "peer-left":
                    var leftId = msg.GetProperty("peerId").GetString()!;
                    if (_peers.TryGetValue(leftId, out var leftPc))
                    {
                        _peers.Remove(leftId);
                        leftPc.Close();
                        leftPc.Dispose();
                        OnPeerDisconnected?.Invoke(leftId);
                    }
                    break;

                case "offer":
                    var offerFromId = msg.GetProperty("fromId").GetString()!;
                    var offerSdp = msg.GetProperty("sdp").GetString()!;
                    await HandleOffer(offerFromId, offerSdp);
                    break;

                case "answer":
                    var answerFromId = msg.GetProperty("fromId").GetString()!;
                    var answerSdp = msg.GetProperty("sdp").GetString()!;
                    if (_peers.TryGetValue(answerFromId, out var answerPc))
                    {
                        await answerPc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "answer", Sdp = answerSdp });
                        // Apply any pending ICE candidates
                        if (_pendingCandidates.TryGetValue(answerFromId, out var pending))
                        {
                            foreach (var c in pending)
                                await answerPc.AddIceCandidate(c);
                            _pendingCandidates.Remove(answerFromId);
                        }
                    }
                    break;

                case "ice-candidate":
                    var iceFromId = msg.GetProperty("fromId").GetString()!;
                    var candidate = new RTCIceCandidateInit
                    {
                        Candidate = msg.GetProperty("candidate").GetString()!,
                        SdpMid = msg.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null,
                        SdpMLineIndex = msg.TryGetProperty("sdpMLineIndex", out var mli) ? mli.GetInt32() : null,
                    };
                    if (_peers.TryGetValue(iceFromId, out var icePc))
                    {
                        if (icePc.RemoteDescription != null)
                        {
                            await icePc.AddIceCandidate(candidate);
                        }
                        else
                        {
                            // Queue candidates until remote description is set
                            if (!_pendingCandidates.ContainsKey(iceFromId))
                                _pendingCandidates[iceFromId] = new List<RTCIceCandidateInit>();
                            _pendingCandidates[iceFromId].Add(candidate);
                        }
                    }
                    break;
            }
        }

        private async Task InitiateConnection(string remotePeerId)
        {
            var pc = RTCPeerConnectionFactory.Create(_config);
            _peers[remotePeerId] = pc;
            WireEvents(pc, remotePeerId);

            if (OnPeerConnectionCreated != null)
                await OnPeerConnectionCreated(pc, remotePeerId);

            var offer = await pc.CreateOffer();
            await pc.SetLocalDescription(offer);
            await SendAsync(new { type = "offer", targetId = remotePeerId, sdp = offer.Sdp });

            OnPeerConnection?.Invoke(pc, remotePeerId);
        }

        private async Task HandleOffer(string remotePeerId, string sdp)
        {
            var pc = RTCPeerConnectionFactory.Create(_config);
            _peers[remotePeerId] = pc;
            WireEvents(pc, remotePeerId);

            if (OnPeerConnectionCreated != null)
                await OnPeerConnectionCreated(pc, remotePeerId);

            await pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "offer", Sdp = sdp });

            // Apply any pending ICE candidates
            if (_pendingCandidates.TryGetValue(remotePeerId, out var pending))
            {
                foreach (var c in pending)
                    await pc.AddIceCandidate(c);
                _pendingCandidates.Remove(remotePeerId);
            }

            var answer = await pc.CreateAnswer();
            await pc.SetLocalDescription(answer);
            await SendAsync(new { type = "answer", targetId = remotePeerId, sdp = answer.Sdp });

            OnPeerConnection?.Invoke(pc, remotePeerId);
        }

        private void WireEvents(IRTCPeerConnection pc, string remotePeerId)
        {
            pc.OnIceCandidate += candidate =>
            {
                _ = SendAsync(new
                {
                    type = "ice-candidate",
                    targetId = remotePeerId,
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex,
                });
            };

            pc.OnDataChannel += channel =>
            {
                OnDataChannel?.Invoke(channel, remotePeerId);
            };
        }

        private async Task SendAsync(object message)
        {
            if (_ws?.State != WebSocketState.Open) return;
            var json = JsonSerializer.Serialize(message);
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
            _peers.Clear();
            _ws?.Dispose();
            _cts?.Dispose();
        }
    }
}

using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.JSObjects.WebRTC;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of IRTCPeerConnection.
    /// Wraps the native browser RTCPeerConnection via SpawnDev.BlazorJS.
    /// Uses BlazorJSRuntime.JS static accessor - no DI required.
    /// </summary>
    public class BrowserRTCPeerConnection : IRTCPeerConnection
    {
        /// <summary>
        /// Direct access to the underlying BlazorJS RTCPeerConnection JSObject.
        /// Use this for advanced JS interop (media streams, tracks, stats, etc.)
        /// without going through the abstraction.
        /// </summary>
        public RTCPeerConnection NativeConnection { get; }

        private bool _disposed;

        public string ConnectionState => NativeConnection.ConnectionState;
        public string IceConnectionState => NativeConnection.IceConnectionState;
        public string IceGatheringState => NativeConnection.IceGatheringState;
        public string SignalingState => NativeConnection.SignalingState;
        public bool? CanTrickleIceCandidates => NativeConnection.CanTrickleIceCandidates;

        public RTCSessionDescriptionInit? LocalDescription => MapDesc(NativeConnection.LocalDescription);
        public RTCSessionDescriptionInit? RemoteDescription => MapDesc(NativeConnection.RemoteDescription);
        public RTCSessionDescriptionInit? CurrentLocalDescription => MapDesc(NativeConnection.CurrentLocalDescription);
        public RTCSessionDescriptionInit? CurrentRemoteDescription => MapDesc(NativeConnection.CurrentRemoteDescription);
        public RTCSessionDescriptionInit? PendingLocalDescription => MapDesc(NativeConnection.PendingLocalDescription);
        public RTCSessionDescriptionInit? PendingRemoteDescription => MapDesc(NativeConnection.PendingRemoteDescription);

        private static RTCSessionDescriptionInit? MapDesc(RTCSessionDescription? desc)
            => desc == null ? null : new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };

        public event Action<RTCIceCandidateInit>? OnIceCandidate;
        public event Action<RTCIceCandidateError>? OnIceCandidateError;
        public event Action<IRTCDataChannel>? OnDataChannel;
        public event Action<RTCTrackEventInit>? OnTrack;
        public event Action<string>? OnConnectionStateChange;
        public event Action<string>? OnSignalingStateChange;
        public event Action<string>? OnIceConnectionStateChange;
        public event Action<string>? OnIceGatheringStateChange;
        public event Action? OnNegotiationNeeded;

        public BrowserRTCPeerConnection(RTCPeerConnectionConfig? config = null)
        {
            if (config != null)
            {
                var jsConfig = new RTCConfiguration
                {
                    IceServers = config.IceServers?.Select(s => new RTCIceServer
                    {
                        Urls = s.Urls.Length == 1 ? (SpawnDev.BlazorJS.Union<string, string[]>)s.Urls[0] : (SpawnDev.BlazorJS.Union<string, string[]>)s.Urls,
                        Username = s.Username,
                        Credential = s.Credential,
                    }).ToArray(),
                    BundlePolicy = config.BundlePolicy,
                    IceTransportPolicy = config.IceTransportPolicy,
                    IceCandidatePoolSize = config.IceCandidatePoolSize,
                    PeerIdentity = config.PeerIdentity,
                    RtcMuxPolicy = config.RtcpMuxPolicy,
                };
                NativeConnection = new RTCPeerConnection(jsConfig);
            }
            else
            {
                NativeConnection = new RTCPeerConnection();
            }
            NativeConnection.OnIceCandidate += HandleIceCandidate;
            NativeConnection.OnIceCandidateError += HandleIceCandidateError;
            NativeConnection.OnDataChannel += HandleDataChannel;
            NativeConnection.OnTrack += HandleTrack;
            NativeConnection.OnConnectionStateChange += HandleConnectionStateChange;
            NativeConnection.OnSignalingStateChange += HandleSignalingStateChange;
            NativeConnection.OnIceConnectionStateChange += HandleIceConnectionStateChange;
            NativeConnection.OnIceGatheringStateChange += HandleIceGatheringStateChange;
            NativeConnection.OnNegotiationNeeded += HandleNegotiationNeeded;

            // Connection-state polling fallback (2026-04-29). Chromium under
            // Playwright can fail to emit `connectionstatechange` when the
            // remote tab closes via `page.close()` - the JS event simply
            // never fires on the surviving side, leaving `connectionState`
            // permanently "connected" from the consumer's perspective even
            // though the data channel and ICE both went down. Without this
            // fallback, P2P consumers (`SpawnDev.WebTorrent.RtcPeer`,
            // `SpawnDev.ILGPU.P2P.P2PWebRtcBridge`) never see `OnClose` and
            // peers stay registered indefinitely.
            //
            // Diagnosed 2026-04-29 against `P2PSwarm.TwoTab_PeerDiscovery`:
            // worker tab closed via Playwright, coord.peerCount stayed at 1
            // for the full 90s test budget; bridge `wire.OnClose` never
            // fired (verified via diagnostic Console.WriteLine).
            //
            // The poll runs every 500 ms, comparing the freshly-read
            // `connectionState` against the last value we observed firing.
            // When they differ - whether via natural JS event or our poll -
            // we synthesise a state-change call. Idempotent because the
            // event consumers (RtcPeer) only act on terminal transitions.
            StartConnectionStatePoller();
        }

        private System.Threading.CancellationTokenSource? _pollCts;
        private string? _lastObservedConnectionState;

        private void StartConnectionStatePoller()
        {
            _pollCts = new System.Threading.CancellationTokenSource();
            var ct = _pollCts.Token;
            _lastObservedConnectionState = NativeConnection.ConnectionState;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        var current = NativeConnection.ConnectionState;
                        if (current != _lastObservedConnectionState)
                        {
                            _lastObservedConnectionState = current;
                            try { OnConnectionStateChange?.Invoke(current); }
                            catch { /* never let a consumer exception kill the poller */ }
                        }
                        // Stop polling once we hit a terminal state - the connection
                        // is gone; no further transitions are possible.
                        if (current == "failed" || current == "closed")
                            return;
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                    }
                }
            }, ct);
        }

        public IRTCDataChannel CreateDataChannel(string label, RTCDataChannelConfig? options = null)
        {
            RTCDataChannelOptions? jsOptions = null;
            if (options != null)
            {
                jsOptions = new RTCDataChannelOptions
                {
                    Ordered = options.Ordered,
                    MaxPacketLifeTime = options.MaxPacketLifeTime,
                    MaxRetransmits = options.MaxRetransmits,
                    Protocol = options.Protocol,
                    Negotiated = options.Negotiated,
                    Id = options.Id,
                };
            }
            var channel = NativeConnection.CreateDataChannel(label, jsOptions);
            return new BrowserRTCDataChannel(channel);
        }

        public async Task<RTCSessionDescriptionInit> CreateOffer()
        {
            var desc = await NativeConnection.CreateOffer();
            return new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };
        }

        public async Task<RTCSessionDescriptionInit> CreateOffer(RTCOfferOptions options)
        {
            var jsOptions = new SpawnDev.BlazorJS.JSObjects.WebRTC.RTCOfferOptions { IceRestart = options.IceRestart };
            var desc = await NativeConnection.CreateOffer(jsOptions);
            return new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };
        }

        public async Task<RTCSessionDescriptionInit> CreateAnswer()
        {
            var desc = await NativeConnection.CreateAnswer();
            return new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };
        }

        public async Task<RTCSessionDescriptionInit> CreateAnswer(RTCAnswerOptions options)
        {
            var desc = await NativeConnection.CreateAnswer();
            return new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };
        }

        public async Task SetLocalDescription(RTCSessionDescriptionInit description)
        {
            var jsDesc = new RTCSessionDescription { Type = description.Type, Sdp = description.Sdp };
            await NativeConnection.SetLocalDescription(jsDesc);
        }

        public async Task SetRemoteDescription(RTCSessionDescriptionInit description)
        {
            var jsDesc = new RTCSessionDescription { Type = description.Type, Sdp = description.Sdp };
            await NativeConnection.SetRemoteDescription(jsDesc);
        }

        public async Task AddIceCandidate(RTCIceCandidateInit candidate)
        {
            var jsCandidate = new RTCIceCandidate(new RTCIceCandidateInfo
            {
                Candidate = candidate.Candidate,
                SdpMid = candidate.SdpMid,
                SdpMLineIndex = candidate.SdpMLineIndex,
                UsernameFragment = candidate.UsernameFragment,
            });
            await NativeConnection.AddIceCandidate(jsCandidate);
            jsCandidate.Dispose();
        }

        public async Task SetLocalDescription()
        {
            await NativeConnection.SetLocalDescription();
        }

        public void RestartIce() => NativeConnection.RestartIce();

        public IRTCRtpTransceiver[] GetTransceivers()
        {
            return NativeConnection.GetTransceivers().Select(t => (IRTCRtpTransceiver)new BrowserRTCRtpTransceiver(t)).ToArray();
        }

        public IRTCRtpTransceiver AddTransceiver(string kind)
        {
            return new BrowserRTCRtpTransceiver(NativeConnection.AddTransceiver(kind));
        }

        public IRTCRtpTransceiver AddTransceiver(IRTCMediaStreamTrack track)
        {
            if (track is BrowserRTCMediaStreamTrack browserTrack)
                return new BrowserRTCRtpTransceiver(NativeConnection.AddTransceiver(browserTrack.NativeTrack));
            throw new ArgumentException("Track must be a BrowserRTCMediaStreamTrack in WASM.");
        }

        public IRTCRtpTransceiver AddTransceiver(string kind, RTCRtpTransceiverInit init)
        {
            var opts = ToNativeOptions(init);
            return new BrowserRTCRtpTransceiver(NativeConnection.AddTransceiver(kind, opts));
        }

        public IRTCRtpTransceiver AddTransceiver(IRTCMediaStreamTrack track, RTCRtpTransceiverInit init)
        {
            if (track is not BrowserRTCMediaStreamTrack browserTrack)
                throw new ArgumentException("Track must be a BrowserRTCMediaStreamTrack in WASM.");
            var opts = ToNativeOptions(init);
            return new BrowserRTCRtpTransceiver(NativeConnection.AddTransceiver(browserTrack.NativeTrack, opts));
        }

        private static SpawnDev.BlazorJS.JSObjects.WebRTC.RTCRtpTransceiverOptions ToNativeOptions(RTCRtpTransceiverInit init)
        {
            return new SpawnDev.BlazorJS.JSObjects.WebRTC.RTCRtpTransceiverOptions
            {
                Direction = init.Direction,
                SendEncodings = init.SendEncodings?.Select(e => new SpawnDev.BlazorJS.JSObjects.WebRTC.RTCMediaEncoding
                {
                    Rid = e.Rid,
                    Active = e.Active,
                    MaxBitrate = e.MaxBitrate is uint m ? (int?)m : null,
                    MaxFramerate = e.MaxFramerate is double f ? (int?)f : null,
                    ScaleResolutionDownBy = e.ScaleResolutionDownBy is double d ? (float?)d : null,
                    Priority = e.Priority,
                }).ToArray(),
            };
        }

        public IRTCRtpSender AddTrack(IRTCMediaStreamTrack track, params IRTCMediaStream[] streams)
        {
            var browserTrack = track as BrowserRTCMediaStreamTrack
                ?? throw new ArgumentException("Track must be a BrowserRTCMediaStreamTrack in WASM.");
            var jsStreams = streams
                .Cast<BrowserRTCMediaStream>()
                .Select(s => s.NativeStream)
                .ToArray();
            var sender = jsStreams.Length > 0
                ? NativeConnection.AddTrack(browserTrack.NativeTrack, jsStreams)
                : NativeConnection.AddTrack(browserTrack.NativeTrack);
            return new BrowserRtpSender(sender);
        }

        public void RemoveTrack(IRTCRtpSender sender)
        {
            if (sender is BrowserRtpSender browserSender)
            {
                NativeConnection.RemoveTrack(browserSender.NativeSender);
            }
        }

        public IRTCRtpSender[] GetSenders()
        {
            return NativeConnection.GetSenders().Select(s => (IRTCRtpSender)new BrowserRtpSender(s)).ToArray();
        }

        public IRTCRtpReceiver[] GetReceivers()
        {
            return NativeConnection.GetReceivers().Select(r => (IRTCRtpReceiver)new BrowserRtpReceiver(r)).ToArray();
        }

        public async Task<IRTCStatsReport> GetStats()
        {
            var report = await NativeConnection.GetStats();
            return new BrowserRTCStatsReport(report);
        }

        public void Close() => NativeConnection.Close();

        private void HandleIceCandidate(RTCPeerConnectionEvent e)
        {
            var jsCandidate = e.Candidate;
            if (jsCandidate == null) return;
            OnIceCandidate?.Invoke(new RTCIceCandidateInit
            {
                Candidate = jsCandidate.Candidate,
                SdpMid = jsCandidate.SdpMid,
                SdpMLineIndex = jsCandidate.SdpMLineIndex,
                UsernameFragment = jsCandidate.UsernameFragment,
            });
        }

        private void HandleDataChannel(RTCDataChannelEvent e)
        {
            var channel = e.Channel;
            OnDataChannel?.Invoke(new BrowserRTCDataChannel(channel));
        }

        private void HandleConnectionStateChange(Event e)
        {
            // Update poller's last-observed value so it doesn't double-fire.
            // Single-threaded Blazor WASM means this assignment is atomic
            // relative to the poller's read.
            _lastObservedConnectionState = ConnectionState;
            OnConnectionStateChange?.Invoke(ConnectionState);
        }

        private void HandleIceConnectionStateChange(Event e)
        {
            OnIceConnectionStateChange?.Invoke(IceConnectionState);
        }

        private void HandleIceGatheringStateChange(Event e)
        {
            OnIceGatheringStateChange?.Invoke(IceGatheringState);
        }

        private void HandleTrack(RTCTrackEvent e)
        {
            OnTrack?.Invoke(new RTCTrackEventInit
            {
                Track = new BrowserRTCMediaStreamTrack(e.Track),
                Receiver = new BrowserRtpReceiver(e.Receiver),
                Streams = e.Streams.Select(s => (IRTCMediaStream)new BrowserRTCMediaStream(s)).ToArray(),
            });
        }

        private void HandleSignalingStateChange(Event e)
        {
            OnSignalingStateChange?.Invoke(SignalingState);
        }

        private void HandleIceCandidateError(RTCPeerConnectionIceErrorEvent e)
        {
            OnIceCandidateError?.Invoke(new RTCIceCandidateError
            {
                Address = e.Address,
                Port = e.Port,
                ErrorCode = e.ErrorCode,
                ErrorText = e.ErrorText,
                Url = e.Url,
            });
        }

        private void HandleNegotiationNeeded(Event e)
        {
            OnNegotiationNeeded?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _pollCts?.Cancel(); } catch { }
            try { _pollCts?.Dispose(); } catch { }
            NativeConnection.OnIceCandidate -= HandleIceCandidate;
            NativeConnection.OnIceCandidateError -= HandleIceCandidateError;
            NativeConnection.OnDataChannel -= HandleDataChannel;
            NativeConnection.OnTrack -= HandleTrack;
            NativeConnection.OnConnectionStateChange -= HandleConnectionStateChange;
            NativeConnection.OnSignalingStateChange -= HandleSignalingStateChange;
            NativeConnection.OnIceConnectionStateChange -= HandleIceConnectionStateChange;
            NativeConnection.OnIceGatheringStateChange -= HandleIceGatheringStateChange;
            NativeConnection.OnNegotiationNeeded -= HandleNegotiationNeeded;
            NativeConnection.Close();
            NativeConnection.Dispose();
        }
    }
}

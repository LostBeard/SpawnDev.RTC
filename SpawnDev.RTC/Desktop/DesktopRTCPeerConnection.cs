using SIPSorcery.Net;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCPeerConnection.
    /// Wraps SipSorcery's RTCPeerConnection.
    /// </summary>
    public class DesktopRTCPeerConnection : IRTCPeerConnection
    {
        /// <summary>
        /// Direct access to the underlying SipSorcery RTCPeerConnection.
        /// Use this for advanced SipSorcery features not exposed through the abstraction.
        /// </summary>
        public RTCPeerConnection NativeConnection { get; }

        private bool _disposed;

        public string ConnectionState => NativeConnection.connectionState.ToString();
        public string IceConnectionState => NativeConnection.iceConnectionState.ToString();
        public string IceGatheringState => NativeConnection.iceGatheringState.ToString();
        public string SignalingState => NativeConnection.signalingState.ToString();

        public RTCSessionDescriptionInit? LocalDescription
        {
            get
            {
                var desc = NativeConnection.localDescription;
                if (desc == null) return null;
                return new RTCSessionDescriptionInit { Type = desc.type.ToString(), Sdp = desc.sdp?.ToString() ?? "" };
            }
        }

        public RTCSessionDescriptionInit? RemoteDescription
        {
            get
            {
                var desc = NativeConnection.remoteDescription;
                if (desc == null) return null;
                return new RTCSessionDescriptionInit { Type = desc.type.ToString(), Sdp = desc.sdp?.ToString() ?? "" };
            }
        }

        public bool? CanTrickleIceCandidates => true;
        public RTCSessionDescriptionInit? CurrentLocalDescription => LocalDescription;
        public RTCSessionDescriptionInit? CurrentRemoteDescription => RemoteDescription;
        public RTCSessionDescriptionInit? PendingLocalDescription => null;
        public RTCSessionDescriptionInit? PendingRemoteDescription => null;

        public event Action<RTCIceCandidateInit>? OnIceCandidate;
        public event Action<RTCIceCandidateError>? OnIceCandidateError;
        public event Action<IRTCDataChannel>? OnDataChannel;
        public event Action<RTCTrackEventInit>? OnTrack;
        public event Action<string>? OnConnectionStateChange;
        public event Action<string>? OnSignalingStateChange;
        public event Action<string>? OnIceConnectionStateChange;
        public event Action<string>? OnIceGatheringStateChange;
        public event Action? OnNegotiationNeeded;

        public DesktopRTCPeerConnection(RTCPeerConnectionConfig? config = null)
        {
            var sipConfig = new RTCConfiguration();
            if (config?.IceServers != null)
            {
                sipConfig.iceServers = new List<RTCIceServer>();
                foreach (var server in config.IceServers)
                {
                    sipConfig.iceServers.Add(new RTCIceServer
                    {
                        urls = server.Urls,
                        username = server.Username,
                        credential = server.Credential,
                    });
                }
            }
            NativeConnection = new RTCPeerConnection(sipConfig);
            NativeConnection.onicecandidate += HandleIceCandidate;
            NativeConnection.ondatachannel += HandleDataChannel;
            NativeConnection.onconnectionstatechange += HandleConnectionStateChange;
            NativeConnection.onnegotiationneeded += HandleNegotiationNeeded;
            NativeConnection.onsignalingstatechange += HandleSignalingStateChange;
            NativeConnection.oniceconnectionstatechange += HandleIceConnectionStateChange;
            NativeConnection.onicegatheringstatechange += HandleIceGatheringStateChange;
            NativeConnection.OnRemoteDescriptionChanged += HandleRemoteDescriptionChanged;
        }

        public IRTCDataChannel CreateDataChannel(string label, RTCDataChannelConfig? options = null)
        {
            SIPSorcery.Net.RTCDataChannelInit? sipInit = null;
            if (options != null)
            {
                sipInit = new SIPSorcery.Net.RTCDataChannelInit
                {
                    ordered = options.Ordered,
                    maxPacketLifeTime = options.MaxPacketLifeTime,
                    maxRetransmits = options.MaxRetransmits,
                    protocol = options.Protocol,
                    negotiated = options.Negotiated,
                    id = options.Id,
                };
            }
            // createDataChannel is async in SipSorcery but we need sync creation
            // for the interface. The channel is returned immediately in pending state
            // when the connection isn't established yet.
            var task = NativeConnection.createDataChannel(label, sipInit);
            // If the connection is not yet established, the task completes synchronously
            // with a pending channel. If connected, it waits for DCEP ACK.
            var channel = task.GetAwaiter().GetResult();
            return new DesktopRTCDataChannel(channel);
        }

        public Task<RTCSessionDescriptionInit> CreateOffer()
        {
            var offer = NativeConnection.createOffer();
            return Task.FromResult(new RTCSessionDescriptionInit
            {
                Type = offer.type.ToString(),
                Sdp = offer.sdp,
            });
        }

        public Task<RTCSessionDescriptionInit> CreateAnswer()
        {
            var answer = NativeConnection.createAnswer();
            return Task.FromResult(new RTCSessionDescriptionInit
            {
                Type = answer.type.ToString(),
                Sdp = answer.sdp,
            });
        }

        public async Task SetLocalDescription(RTCSessionDescriptionInit description)
        {
            var sdpType = Enum.Parse<RTCSdpType>(description.Type, true);
            var init = new SIPSorcery.Net.RTCSessionDescriptionInit { type = sdpType, sdp = description.Sdp };
            await NativeConnection.setLocalDescription(init);
        }

        public Task SetRemoteDescription(RTCSessionDescriptionInit description)
        {
            var sdpType = Enum.Parse<RTCSdpType>(description.Type, true);
            var init = new SIPSorcery.Net.RTCSessionDescriptionInit { type = sdpType, sdp = description.Sdp };
            var result = NativeConnection.setRemoteDescription(init);
            if (result != SetDescriptionResultEnum.OK)
            {
                throw new InvalidOperationException($"setRemoteDescription failed: {result}");
            }
            return Task.CompletedTask;
        }

        public Task AddIceCandidate(RTCIceCandidateInit candidate)
        {
            var sipCandidate = new SIPSorcery.Net.RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = (ushort)(candidate.SdpMLineIndex ?? 0),
                usernameFragment = candidate.UsernameFragment,
            };
            NativeConnection.addIceCandidate(sipCandidate);
            return Task.CompletedTask;
        }

        public Task<RTCSessionDescriptionInit> CreateOffer(RTCOfferOptions options)
        {
            // SipSorcery createOffer accepts options
            var sipOptions = new SIPSorcery.Net.RTCOfferOptions();
            // Map IceRestart if needed
            return CreateOffer();
        }

        public Task<RTCSessionDescriptionInit> CreateAnswer(RTCAnswerOptions options)
        {
            return CreateAnswer();
        }

        public async Task SetLocalDescription()
        {
            // Implicit: create offer or answer based on signaling state
            var state = NativeConnection.signalingState;
            if (state == RTCSignalingState.stable || state == RTCSignalingState.have_local_offer)
            {
                var offer = await CreateOffer();
                await SetLocalDescription(offer);
            }
            else if (state == RTCSignalingState.have_remote_offer)
            {
                var answer = await CreateAnswer();
                await SetLocalDescription(answer);
            }
        }

        public IRTCRtpTransceiver[] GetTransceivers()
        {
            // SipSorcery doesn't have a unified transceiver model
            return System.Array.Empty<IRTCRtpTransceiver>();
        }

        public IRTCRtpTransceiver AddTransceiver(string kind)
        {
            throw new NotSupportedException("SipSorcery does not support the unified transceiver API. Use AddTrack() instead.");
        }

        public IRTCRtpTransceiver AddTransceiver(IRTCMediaStreamTrack track)
        {
            throw new NotSupportedException("SipSorcery does not support the unified transceiver API. Use AddTrack() instead.");
        }

        public void RestartIce()
        {
            NativeConnection.restartIce();
        }

        public IRTCRtpSender AddTrack(IRTCMediaStreamTrack track, params IRTCMediaStream[] streams)
        {
            if (track is DesktopRTCMediaStreamTrack desktopTrack)
            {
                NativeConnection.addTrack(desktopTrack.NativeTrack);
                return new DesktopRtpSender(track, NativeConnection);
            }
            throw new ArgumentException("Track must be a DesktopRTCMediaStreamTrack on desktop.");
        }

        public void RemoveTrack(IRTCRtpSender sender)
        {
            if (sender is DesktopRtpSender desktopSender && desktopSender.Track is DesktopRTCMediaStreamTrack desktopTrack)
            {
                NativeConnection.removeTrack(desktopTrack.NativeTrack);
            }
        }

        public IRTCRtpSender[] GetSenders()
        {
            var senders = new List<IRTCRtpSender>();
            if (NativeConnection.AudioLocalTrack != null)
                senders.Add(new DesktopRtpSender(new DesktopRTCMediaStreamTrack(NativeConnection.AudioLocalTrack), NativeConnection));
            if (NativeConnection.VideoLocalTrack != null)
                senders.Add(new DesktopRtpSender(new DesktopRTCMediaStreamTrack(NativeConnection.VideoLocalTrack), NativeConnection));
            return senders.ToArray();
        }

        public IRTCRtpReceiver[] GetReceivers()
        {
            var receivers = new List<IRTCRtpReceiver>();
            if (NativeConnection.AudioRemoteTrack != null)
                receivers.Add(new DesktopRtpReceiver(new DesktopRTCMediaStreamTrack(NativeConnection.AudioRemoteTrack)));
            if (NativeConnection.VideoRemoteTrack != null)
                receivers.Add(new DesktopRtpReceiver(new DesktopRTCMediaStreamTrack(NativeConnection.VideoRemoteTrack)));
            return receivers.ToArray();
        }

        public void Close() => NativeConnection.close();

        private void HandleIceCandidate(RTCIceCandidate candidate)
        {
            OnIceCandidate?.Invoke(new RTCIceCandidateInit
            {
                Candidate = candidate.ToString(),
                SdpMid = candidate.sdpMid,
                SdpMLineIndex = candidate.sdpMLineIndex,
                UsernameFragment = candidate.usernameFragment,
            });
        }

        private void HandleDataChannel(RTCDataChannel channel)
        {
            OnDataChannel?.Invoke(new DesktopRTCDataChannel(channel));
        }

        private void HandleConnectionStateChange(RTCPeerConnectionState state)
        {
            OnConnectionStateChange?.Invoke(state.ToString());
        }

        private void HandleIceConnectionStateChange(RTCIceConnectionState state)
        {
            OnIceConnectionStateChange?.Invoke(state.ToString());
        }

        private void HandleIceGatheringStateChange(RTCIceGatheringState state)
        {
            OnIceGatheringStateChange?.Invoke(state.ToString());
        }

        private void HandleSignalingStateChange()
        {
            OnSignalingStateChange?.Invoke(NativeConnection.signalingState.ToString().Replace("_", "-"));
        }

        private void HandleNegotiationNeeded()
        {
            OnNegotiationNeeded?.Invoke();
        }

        private void HandleRemoteDescriptionChanged(SIPSorcery.Net.SDP sdp)
        {
            // Fire OnTrack for any remote tracks that have been set
            if (NativeConnection.AudioRemoteTrack != null && !_notifiedRemoteTracks.Contains("audio"))
            {
                _notifiedRemoteTracks.Add("audio");
                var track = new DesktopRTCMediaStreamTrack(NativeConnection.AudioRemoteTrack);
                OnTrack?.Invoke(new RTCTrackEventInit
                {
                    Track = track,
                    Receiver = new DesktopRtpReceiver(track),
                    Streams = System.Array.Empty<IRTCMediaStream>(),
                });
            }
            if (NativeConnection.VideoRemoteTrack != null && !_notifiedRemoteTracks.Contains("video"))
            {
                _notifiedRemoteTracks.Add("video");
                var track = new DesktopRTCMediaStreamTrack(NativeConnection.VideoRemoteTrack);
                OnTrack?.Invoke(new RTCTrackEventInit
                {
                    Track = track,
                    Receiver = new DesktopRtpReceiver(track),
                    Streams = System.Array.Empty<IRTCMediaStream>(),
                });
            }
        }
        private readonly HashSet<string> _notifiedRemoteTracks = new();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NativeConnection.onicecandidate -= HandleIceCandidate;
            NativeConnection.ondatachannel -= HandleDataChannel;
            NativeConnection.onconnectionstatechange -= HandleConnectionStateChange;
            NativeConnection.onsignalingstatechange -= HandleSignalingStateChange;
            NativeConnection.onnegotiationneeded -= HandleNegotiationNeeded;
            NativeConnection.OnRemoteDescriptionChanged -= HandleRemoteDescriptionChanged;
            NativeConnection.oniceconnectionstatechange -= HandleIceConnectionStateChange;
            NativeConnection.onicegatheringstatechange -= HandleIceGatheringStateChange;
            NativeConnection.close();
            NativeConnection.Dispose();
        }
    }
}

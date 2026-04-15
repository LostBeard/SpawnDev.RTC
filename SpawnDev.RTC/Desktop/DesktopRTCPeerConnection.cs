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

        public event Action<RTCIceCandidateInit>? OnIceCandidate;
        public event Action<IRTCDataChannel>? OnDataChannel;
        public event Action<string>? OnConnectionStateChange;
        public event Action<string>? OnIceConnectionStateChange;
        public event Action<string>? OnIceGatheringStateChange;

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
            NativeConnection.oniceconnectionstatechange += HandleIceConnectionStateChange;
            NativeConnection.onicegatheringstatechange += HandleIceGatheringStateChange;
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NativeConnection.onicecandidate -= HandleIceCandidate;
            NativeConnection.ondatachannel -= HandleDataChannel;
            NativeConnection.onconnectionstatechange -= HandleConnectionStateChange;
            NativeConnection.oniceconnectionstatechange -= HandleIceConnectionStateChange;
            NativeConnection.onicegatheringstatechange -= HandleIceGatheringStateChange;
            NativeConnection.close();
            NativeConnection.Dispose();
        }
    }
}

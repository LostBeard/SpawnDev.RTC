using SIPSorcery.Net;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCPeerConnection.
    /// Wraps SipSorcery's RTCPeerConnection.
    /// </summary>
    public class DesktopRTCPeerConnection : IRTCPeerConnection
    {
        private readonly RTCPeerConnection _pc;
        private bool _disposed;

        public string ConnectionState => _pc.connectionState.ToString();
        public string IceConnectionState => _pc.iceConnectionState.ToString();
        public string IceGatheringState => _pc.iceGatheringState.ToString();
        public string SignalingState => _pc.signalingState.ToString();

        public RTCSessionDescriptionInit? LocalDescription
        {
            get
            {
                var desc = _pc.localDescription;
                if (desc == null) return null;
                return new RTCSessionDescriptionInit { Type = desc.type.ToString(), Sdp = desc.sdp?.ToString() ?? "" };
            }
        }

        public RTCSessionDescriptionInit? RemoteDescription
        {
            get
            {
                var desc = _pc.remoteDescription;
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
            _pc = new RTCPeerConnection(sipConfig);
            _pc.onicecandidate += HandleIceCandidate;
            _pc.ondatachannel += HandleDataChannel;
            _pc.onconnectionstatechange += HandleConnectionStateChange;
            _pc.oniceconnectionstatechange += HandleIceConnectionStateChange;
            _pc.onicegatheringstatechange += HandleIceGatheringStateChange;
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
            var task = _pc.createDataChannel(label, sipInit);
            // If the connection is not yet established, the task completes synchronously
            // with a pending channel. If connected, it waits for DCEP ACK.
            var channel = task.GetAwaiter().GetResult();
            return new DesktopRTCDataChannel(channel);
        }

        public Task<RTCSessionDescriptionInit> CreateOffer()
        {
            var offer = _pc.createOffer();
            return Task.FromResult(new RTCSessionDescriptionInit
            {
                Type = offer.type.ToString(),
                Sdp = offer.sdp,
            });
        }

        public Task<RTCSessionDescriptionInit> CreateAnswer()
        {
            var answer = _pc.createAnswer();
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
            await _pc.setLocalDescription(init);
        }

        public Task SetRemoteDescription(RTCSessionDescriptionInit description)
        {
            var sdpType = Enum.Parse<RTCSdpType>(description.Type, true);
            var init = new SIPSorcery.Net.RTCSessionDescriptionInit { type = sdpType, sdp = description.Sdp };
            var result = _pc.setRemoteDescription(init);
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
            _pc.addIceCandidate(sipCandidate);
            return Task.CompletedTask;
        }

        public void Close() => _pc.close();

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
            _pc.onicecandidate -= HandleIceCandidate;
            _pc.ondatachannel -= HandleDataChannel;
            _pc.onconnectionstatechange -= HandleConnectionStateChange;
            _pc.oniceconnectionstatechange -= HandleIceConnectionStateChange;
            _pc.onicegatheringstatechange -= HandleIceGatheringStateChange;
            _pc.close();
            _pc.Dispose();
        }
    }
}

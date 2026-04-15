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
        private readonly RTCPeerConnection _pc;
        private bool _disposed;

        public string ConnectionState => _pc.ConnectionState;
        public string IceConnectionState => _pc.IceConnectionState;
        public string IceGatheringState => _pc.IceGatheringState;
        public string SignalingState => _pc.SignalingState;

        public RTCSessionDescriptionInit? LocalDescription
        {
            get
            {
                var desc = _pc.LocalDescription;
                return desc == null ? null : new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };
            }
        }

        public RTCSessionDescriptionInit? RemoteDescription
        {
            get
            {
                var desc = _pc.RemoteDescription;
                return desc == null ? null : new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };
            }
        }

        public event Action<RTCIceCandidateInit>? OnIceCandidate;
        public event Action<IRTCDataChannel>? OnDataChannel;
        public event Action<string>? OnConnectionStateChange;
        public event Action<string>? OnIceConnectionStateChange;
        public event Action<string>? OnIceGatheringStateChange;

        public BrowserRTCPeerConnection(RTCPeerConnectionConfig? config = null)
        {
            if (config != null)
            {
                var jsConfig = new RTCConfiguration
                {
                    IceServers = config.IceServers?.Select(s => new RTCIceServer
                    {
                        Urls = s.Urls,
                        Username = s.Username,
                        Credential = s.Credential,
                    }).ToArray()
                };
                _pc = new RTCPeerConnection(jsConfig);
            }
            else
            {
                _pc = new RTCPeerConnection();
            }
            _pc.OnIceCandidate += HandleIceCandidate;
            _pc.OnDataChannel += HandleDataChannel;
            _pc.OnConnectionStateChange += HandleConnectionStateChange;
            _pc.OnIceConnectionStateChange += HandleIceConnectionStateChange;
            _pc.OnIceGatheringStateChange += HandleIceGatheringStateChange;
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
            var channel = _pc.CreateDataChannel(label, jsOptions);
            return new BrowserRTCDataChannel(channel);
        }

        public async Task<RTCSessionDescriptionInit> CreateOffer()
        {
            var desc = await _pc.CreateOffer();
            return new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };
        }

        public async Task<RTCSessionDescriptionInit> CreateAnswer()
        {
            var desc = await _pc.CreateAnswer();
            return new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };
        }

        public async Task SetLocalDescription(RTCSessionDescriptionInit description)
        {
            var jsDesc = new RTCSessionDescription { Type = description.Type, Sdp = description.Sdp };
            await _pc.SetLocalDescription(jsDesc);
        }

        public async Task SetRemoteDescription(RTCSessionDescriptionInit description)
        {
            var jsDesc = new RTCSessionDescription { Type = description.Type, Sdp = description.Sdp };
            await _pc.SetRemoteDescription(jsDesc);
        }

        public async Task AddIceCandidate(RTCIceCandidateInit candidate)
        {
            await _pc.AddIceCandidate(candidate.Candidate);
        }

        public void Close() => _pc.Close();

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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pc.OnIceCandidate -= HandleIceCandidate;
            _pc.OnDataChannel -= HandleDataChannel;
            _pc.OnConnectionStateChange -= HandleConnectionStateChange;
            _pc.OnIceConnectionStateChange -= HandleIceConnectionStateChange;
            _pc.OnIceGatheringStateChange -= HandleIceGatheringStateChange;
            _pc.Close();
            _pc.Dispose();
        }
    }
}

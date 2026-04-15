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

        public RTCSessionDescriptionInit? LocalDescription
        {
            get
            {
                var desc = NativeConnection.LocalDescription;
                return desc == null ? null : new RTCSessionDescriptionInit { Type = desc.Type, Sdp = desc.Sdp };
            }
        }

        public RTCSessionDescriptionInit? RemoteDescription
        {
            get
            {
                var desc = NativeConnection.RemoteDescription;
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
                NativeConnection = new RTCPeerConnection(jsConfig);
            }
            else
            {
                NativeConnection = new RTCPeerConnection();
            }
            NativeConnection.OnIceCandidate += HandleIceCandidate;
            NativeConnection.OnDataChannel += HandleDataChannel;
            NativeConnection.OnConnectionStateChange += HandleConnectionStateChange;
            NativeConnection.OnIceConnectionStateChange += HandleIceConnectionStateChange;
            NativeConnection.OnIceGatheringStateChange += HandleIceGatheringStateChange;
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

        public async Task<RTCSessionDescriptionInit> CreateAnswer()
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
            NativeConnection.OnIceCandidate -= HandleIceCandidate;
            NativeConnection.OnDataChannel -= HandleDataChannel;
            NativeConnection.OnConnectionStateChange -= HandleConnectionStateChange;
            NativeConnection.OnIceConnectionStateChange -= HandleIceConnectionStateChange;
            NativeConnection.OnIceGatheringStateChange -= HandleIceGatheringStateChange;
            NativeConnection.Close();
            NativeConnection.Dispose();
        }
    }
}

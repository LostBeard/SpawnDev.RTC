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
                        Urls = s.Urls,
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
            // Browser CreateAnswer doesn't have meaningful options yet
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

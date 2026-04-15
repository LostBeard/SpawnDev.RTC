namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform peer connection interface.
    /// Mirrors the W3C RTCPeerConnection specification.
    /// Browser: wraps native RTCPeerConnection via SpawnDev.BlazorJS.
    /// Desktop: wraps SipSorcery RTCPeerConnection.
    /// </summary>
    public interface IRTCPeerConnection : IDisposable
    {
        // --- Connection state ---
        string ConnectionState { get; }
        string IceConnectionState { get; }
        string IceGatheringState { get; }
        string SignalingState { get; }
        bool? CanTrickleIceCandidates { get; }

        // --- SDP descriptions ---
        RTCSessionDescriptionInit? LocalDescription { get; }
        RTCSessionDescriptionInit? RemoteDescription { get; }
        RTCSessionDescriptionInit? CurrentLocalDescription { get; }
        RTCSessionDescriptionInit? CurrentRemoteDescription { get; }
        RTCSessionDescriptionInit? PendingLocalDescription { get; }
        RTCSessionDescriptionInit? PendingRemoteDescription { get; }

        // --- Data channels ---
        IRTCDataChannel CreateDataChannel(string label, RTCDataChannelConfig? options = null);

        // --- SDP negotiation ---
        Task<RTCSessionDescriptionInit> CreateOffer();
        Task<RTCSessionDescriptionInit> CreateOffer(RTCOfferOptions options);
        Task<RTCSessionDescriptionInit> CreateAnswer();
        Task<RTCSessionDescriptionInit> CreateAnswer(RTCAnswerOptions options);
        Task SetLocalDescription(RTCSessionDescriptionInit description);
        Task SetLocalDescription();
        Task SetRemoteDescription(RTCSessionDescriptionInit description);

        // --- ICE ---
        Task AddIceCandidate(RTCIceCandidateInit candidate);
        void RestartIce();

        // --- Media tracks ---
        IRTCRtpSender AddTrack(IRTCMediaStreamTrack track, params IRTCMediaStream[] streams);
        void RemoveTrack(IRTCRtpSender sender);
        IRTCRtpSender[] GetSenders();
        IRTCRtpReceiver[] GetReceivers();

        // --- Transceivers ---
        IRTCRtpTransceiver[] GetTransceivers();
        IRTCRtpTransceiver AddTransceiver(string kind);
        IRTCRtpTransceiver AddTransceiver(IRTCMediaStreamTrack track);

        // --- Statistics ---
        Task<IRTCStatsReport> GetStats();

        // --- Lifecycle ---
        void Close();

        // --- Events ---

        /// <summary>
        /// Fired when a new ICE candidate is available for signaling.
        /// </summary>
        event Action<RTCIceCandidateInit>? OnIceCandidate;

        /// <summary>
        /// Fired when an ICE candidate error occurs.
        /// </summary>
        event Action<RTCIceCandidateError>? OnIceCandidateError;

        /// <summary>
        /// Fired when a remote peer creates a data channel.
        /// </summary>
        event Action<IRTCDataChannel>? OnDataChannel;

        /// <summary>
        /// Fired when the remote peer adds a media track.
        /// </summary>
        event Action<RTCTrackEventInit>? OnTrack;

        /// <summary>
        /// Fired when the connection state changes.
        /// </summary>
        event Action<string>? OnConnectionStateChange;

        /// <summary>
        /// Fired when the signaling state changes.
        /// </summary>
        event Action<string>? OnSignalingStateChange;

        /// <summary>
        /// Fired when the ICE connection state changes.
        /// </summary>
        event Action<string>? OnIceConnectionStateChange;

        /// <summary>
        /// Fired when the ICE gathering state changes.
        /// </summary>
        event Action<string>? OnIceGatheringStateChange;

        /// <summary>
        /// Fired when renegotiation is needed.
        /// </summary>
        event Action? OnNegotiationNeeded;
    }

    /// <summary>
    /// Event data for the OnTrack event.
    /// </summary>
    public class RTCTrackEventInit
    {
        public IRTCRtpReceiver Receiver { get; set; } = default!;
        public IRTCMediaStreamTrack Track { get; set; } = default!;
        public IRTCMediaStream[] Streams { get; set; } = System.Array.Empty<IRTCMediaStream>();
        public IRTCRtpTransceiver? Transceiver { get; set; }
    }

    /// <summary>
    /// ICE candidate error information.
    /// </summary>
    public class RTCIceCandidateError
    {
        public string? Address { get; set; }
        public uint? Port { get; set; }
        public uint ErrorCode { get; set; }
        public string ErrorText { get; set; } = "";
        public string Url { get; set; } = "";
    }

    /// <summary>
    /// Options for CreateOffer.
    /// </summary>
    public class RTCOfferOptions
    {
        public bool? IceRestart { get; set; }
    }

    /// <summary>
    /// Options for CreateAnswer.
    /// </summary>
    public class RTCAnswerOptions
    {
    }

    /// <summary>
    /// Cross-platform RTP sender - controls sending a media track.
    /// </summary>
    public interface IRTCRtpSender
    {
        IRTCMediaStreamTrack? Track { get; }
        IRTCDTMFSender? DTMF { get; }
        Task ReplaceTrack(IRTCMediaStreamTrack? track);
        void SetStreams(params IRTCMediaStream[] streams);
    }

    /// <summary>
    /// Cross-platform RTP receiver - receives a media track from the remote peer.
    /// </summary>
    public interface IRTCRtpReceiver
    {
        IRTCMediaStreamTrack Track { get; }
    }
}

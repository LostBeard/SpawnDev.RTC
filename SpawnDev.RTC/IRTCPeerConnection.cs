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
        RTCSessionDescriptionInit? LocalDescription { get; }
        RTCSessionDescriptionInit? RemoteDescription { get; }

        // --- Data channels ---
        IRTCDataChannel CreateDataChannel(string label, RTCDataChannelConfig? options = null);

        // --- SDP negotiation ---
        Task<RTCSessionDescriptionInit> CreateOffer();
        Task<RTCSessionDescriptionInit> CreateAnswer();
        Task SetLocalDescription(RTCSessionDescriptionInit description);
        Task SetRemoteDescription(RTCSessionDescriptionInit description);

        // --- ICE ---
        Task AddIceCandidate(RTCIceCandidateInit candidate);
        void RestartIce();

        // --- Media tracks ---

        /// <summary>
        /// Adds a media track to the connection. Returns the sender used to control it.
        /// </summary>
        IRTCRtpSender AddTrack(IRTCMediaStreamTrack track, params IRTCMediaStream[] streams);

        /// <summary>
        /// Removes a track sender from the connection.
        /// </summary>
        void RemoveTrack(IRTCRtpSender sender);

        /// <summary>
        /// Returns all RTP senders associated with this connection.
        /// </summary>
        IRTCRtpSender[] GetSenders();

        /// <summary>
        /// Returns all RTP receivers associated with this connection.
        /// </summary>
        IRTCRtpReceiver[] GetReceivers();

        // --- Lifecycle ---
        void Close();

        // --- Events ---

        /// <summary>
        /// Fired when a new ICE candidate is available for signaling.
        /// </summary>
        event Action<RTCIceCandidateInit>? OnIceCandidate;

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
        /// Fired when the ICE connection state changes.
        /// </summary>
        event Action<string>? OnIceConnectionStateChange;

        /// <summary>
        /// Fired when the ICE gathering state changes.
        /// </summary>
        event Action<string>? OnIceGatheringStateChange;

        /// <summary>
        /// Fired when renegotiation is needed (e.g., after adding/removing tracks).
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
    }

    /// <summary>
    /// Cross-platform RTP sender - controls sending a media track.
    /// </summary>
    public interface IRTCRtpSender
    {
        IRTCMediaStreamTrack? Track { get; }
        Task ReplaceTrack(IRTCMediaStreamTrack? track);
    }

    /// <summary>
    /// Cross-platform RTP receiver - receives a media track from the remote peer.
    /// </summary>
    public interface IRTCRtpReceiver
    {
        IRTCMediaStreamTrack Track { get; }
    }
}

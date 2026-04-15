namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform peer connection interface.
    /// Browser: wraps native RTCPeerConnection via SpawnDev.BlazorJS.
    /// Desktop: wraps SipSorcery RTCPeerConnection.
    /// </summary>
    public interface IRTCPeerConnection : IDisposable
    {
        string ConnectionState { get; }
        string IceConnectionState { get; }
        string IceGatheringState { get; }
        string SignalingState { get; }
        RTCSessionDescriptionInit? LocalDescription { get; }
        RTCSessionDescriptionInit? RemoteDescription { get; }

        IRTCDataChannel CreateDataChannel(string label, RTCDataChannelConfig? options = null);
        Task<RTCSessionDescriptionInit> CreateOffer();
        Task<RTCSessionDescriptionInit> CreateAnswer();
        Task SetLocalDescription(RTCSessionDescriptionInit description);
        Task SetRemoteDescription(RTCSessionDescriptionInit description);
        Task AddIceCandidate(RTCIceCandidateInit candidate);
        void Close();

        /// <summary>
        /// Fired when a new ICE candidate is available for signaling.
        /// </summary>
        event Action<RTCIceCandidateInit>? OnIceCandidate;

        /// <summary>
        /// Fired when a remote peer creates a data channel.
        /// </summary>
        event Action<IRTCDataChannel>? OnDataChannel;

        /// <summary>
        /// Fired when the connection state changes.
        /// Values: "new", "connecting", "connected", "disconnected", "failed", "closed".
        /// </summary>
        event Action<string>? OnConnectionStateChange;

        /// <summary>
        /// Fired when the ICE connection state changes.
        /// </summary>
        event Action<string>? OnIceConnectionStateChange;

        /// <summary>
        /// Fired when the ICE gathering state changes.
        /// Values: "new", "gathering", "complete".
        /// </summary>
        event Action<string>? OnIceGatheringStateChange;
    }
}

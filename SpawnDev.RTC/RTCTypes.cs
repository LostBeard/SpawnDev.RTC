using System.Text.Json.Serialization;

namespace SpawnDev.RTC
{
    /// <summary>
    /// Configuration for creating an RTCPeerConnection.
    /// </summary>
    public class RTCPeerConnectionConfig
    {
        [JsonPropertyName("iceServers")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RTCIceServerConfig[]? IceServers { get; set; }

        /// <summary>
        /// "balanced", "max-compat", or "max-bundle".
        /// </summary>
        [JsonPropertyName("bundlePolicy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BundlePolicy { get; set; }

        /// <summary>
        /// "all" or "relay". When "relay", only TURN candidates are used.
        /// </summary>
        [JsonPropertyName("iceTransportPolicy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? IceTransportPolicy { get; set; }

        /// <summary>
        /// Number of ICE candidates to pre-gather.
        /// </summary>
        [JsonPropertyName("iceCandidatePoolSize")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ushort? IceCandidatePoolSize { get; set; }

        /// <summary>
        /// Target peer identity for authentication.
        /// </summary>
        [JsonPropertyName("peerIdentity")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PeerIdentity { get; set; }

        /// <summary>
        /// "negotiate" or "require".
        /// </summary>
        [JsonPropertyName("rtcpMuxPolicy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RtcpMuxPolicy { get; set; }
    }

    /// <summary>
    /// ICE server configuration (STUN/TURN).
    /// </summary>
    public class RTCIceServerConfig
    {
        [JsonPropertyName("urls")]
        public string Urls { get; set; } = "";

        [JsonPropertyName("username")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Username { get; set; }

        [JsonPropertyName("credential")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Credential { get; set; }
    }

    /// <summary>
    /// SDP session description (offer/answer).
    /// </summary>
    public class RTCSessionDescriptionInit
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("sdp")]
        public string Sdp { get; set; } = "";
    }

    /// <summary>
    /// ICE candidate information for signaling exchange.
    /// </summary>
    public class RTCIceCandidateInit
    {
        [JsonPropertyName("candidate")]
        public string Candidate { get; set; } = "";

        [JsonPropertyName("sdpMid")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SdpMid { get; set; }

        [JsonPropertyName("sdpMLineIndex")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? SdpMLineIndex { get; set; }

        [JsonPropertyName("usernameFragment")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UsernameFragment { get; set; }
    }

    /// <summary>
    /// Options for creating a data channel.
    /// </summary>
    public class RTCDataChannelConfig
    {
        [JsonPropertyName("ordered")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Ordered { get; set; }

        [JsonPropertyName("maxPacketLifeTime")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ushort? MaxPacketLifeTime { get; set; }

        [JsonPropertyName("maxRetransmits")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ushort? MaxRetransmits { get; set; }

        [JsonPropertyName("protocol")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Protocol { get; set; }

        [JsonPropertyName("negotiated")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Negotiated { get; set; }

        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ushort? Id { get; set; }
    }
}

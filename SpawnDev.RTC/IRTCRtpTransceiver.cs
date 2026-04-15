namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform RTP transceiver - unified sender/receiver pairing.
    /// Mirrors the W3C RTCRtpTransceiver specification.
    /// </summary>
    public interface IRTCRtpTransceiver
    {
        /// <summary>
        /// The media line ID (null until negotiation).
        /// </summary>
        string? Mid { get; }

        /// <summary>
        /// The desired direction: "sendrecv", "sendonly", "recvonly", "inactive", "stopped".
        /// </summary>
        string Direction { get; set; }

        /// <summary>
        /// The currently negotiated direction (null before negotiation).
        /// </summary>
        string? CurrentDirection { get; }

        /// <summary>
        /// The RTP sender associated with this transceiver.
        /// </summary>
        IRTCRtpSender Sender { get; }

        /// <summary>
        /// The RTP receiver associated with this transceiver.
        /// </summary>
        IRTCRtpReceiver Receiver { get; }

        /// <summary>
        /// Permanently stops the transceiver. The sender stops sending,
        /// the receiver stops receiving, and the media description is tagged as "stopped".
        /// </summary>
        void Stop();
    }

    /// <summary>
    /// Codec information for codec preference ordering.
    /// </summary>
    public class RTCRtpCodecInfo
    {
        public string MimeType { get; set; } = "";
        public int ClockRate { get; set; }
        public int? Channels { get; set; }
        public string? SdpFmtpLine { get; set; }
    }
}

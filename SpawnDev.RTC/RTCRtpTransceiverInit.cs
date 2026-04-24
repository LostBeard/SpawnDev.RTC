namespace SpawnDev.RTC;

/// <summary>
/// Options for <see cref="IRTCPeerConnection.AddTransceiver(string, RTCRtpTransceiverInit)"/>
/// and the <c>track</c> overload. Mirrors the W3C WebRTC
/// <c>RTCRtpTransceiverInit</c> dictionary so consumers can set the initial
/// direction + simulcast <see cref="SendEncodings"/> at transceiver-creation
/// time - RIDs can only be set here per RFC 8853, not later via
/// <see cref="IRTCRtpSender.SetParameters(RTCRtpSendParameters)"/>.
/// </summary>
public class RTCRtpTransceiverInit
{
    /// <summary>
    /// Initial transceiver direction. Valid: <c>"sendrecv"</c> (default),
    /// <c>"sendonly"</c>, <c>"recvonly"</c>, <c>"inactive"</c>.
    /// Null leaves the browser / SipSorcery default.
    /// </summary>
    public string? Direction { get; set; }

    /// <summary>
    /// Initial send-side encodings. For simulcast, pass one entry per quality
    /// layer with distinct <see cref="RTCRtpEncoding.Rid"/> values. The browser
    /// encodes this into the SDP offer as <c>a=simulcast:send ...</c> +
    /// <c>a=rid:...</c> lines. After the offer, RIDs are locked - downstream
    /// <see cref="IRTCRtpSender.SetParameters"/> calls can only tune bitrate /
    /// framerate / scale / active, not change RID set membership.
    /// <para>
    /// Desktop note: SipSorcery does not implement real simulcast (single
    /// encoding per track); this property is accepted but ignored on the
    /// desktop path. The SDP offer from the desktop stack will not contain
    /// simulcast attributes.
    /// </para>
    /// </summary>
    public RTCRtpEncoding[]? SendEncodings { get; set; }
}

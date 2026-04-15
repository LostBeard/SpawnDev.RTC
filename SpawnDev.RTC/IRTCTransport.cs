namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform DTLS transport information.
    /// </summary>
    public interface IRTCDtlsTransport
    {
        string State { get; }
        IRTCIceTransport IceTransport { get; }
        event Action<string>? OnStateChange;
        event Action<string>? OnError;
    }

    /// <summary>
    /// Cross-platform ICE transport information.
    /// </summary>
    public interface IRTCIceTransport
    {
        string Component { get; }
        string GatheringState { get; }
        string Role { get; }
        string State { get; }
    }

    /// <summary>
    /// Cross-platform SCTP transport for data channels.
    /// </summary>
    public interface IRTCSctpTransport
    {
        IRTCDtlsTransport Transport { get; }
        string State { get; }
        int MaxMessageSize { get; }
        int? MaxChannels { get; }
    }

    /// <summary>
    /// Cross-platform WebRTC certificate.
    /// </summary>
    public interface IRTCCertificate
    {
        DateTime Expires { get; }
    }
}

namespace SpawnDev.RTC.Desktop
{
    public class DesktopRTCDtlsTransport : IRTCDtlsTransport
    {
        private readonly SIPSorcery.Net.RTCPeerConnection _pc;
        public string State => _pc.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected ? "connected" : "new";
        public IRTCIceTransport IceTransport => new DesktopRTCIceTransport(_pc);
        public event Action<string>? OnStateChange;
        public event Action<string>? OnError;

        public DesktopRTCDtlsTransport(SIPSorcery.Net.RTCPeerConnection pc)
        {
            _pc = pc;
        }
    }

    public class DesktopRTCIceTransport : IRTCIceTransport
    {
        private readonly SIPSorcery.Net.RTCPeerConnection _pc;
        public string Component => "rtp";
        public string GatheringState => _pc.iceGatheringState.ToString();
        public string Role => "controlling";
        public string State => _pc.iceConnectionState.ToString();

        public DesktopRTCIceTransport(SIPSorcery.Net.RTCPeerConnection pc)
        {
            _pc = pc;
        }
    }

    public class DesktopRTCSctpTransport : IRTCSctpTransport
    {
        private readonly SIPSorcery.Net.RTCPeerConnection _pc;
        public IRTCDtlsTransport Transport => new DesktopRTCDtlsTransport(_pc);
        public string State => _pc.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected ? "connected" : "connecting";
        public int MaxMessageSize => 262144; // 256KB per W3C spec
        public int? MaxChannels => 65535;

        public DesktopRTCSctpTransport(SIPSorcery.Net.RTCPeerConnection pc)
        {
            _pc = pc;
        }
    }

    public class DesktopRTCCertificate : IRTCCertificate
    {
        public DateTime Expires { get; }

        public DesktopRTCCertificate(DateTime expires)
        {
            Expires = expires;
        }
    }
}

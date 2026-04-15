using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects.WebRTC;

namespace SpawnDev.RTC.Browser
{
    public class BrowserRTCDtlsTransport : IRTCDtlsTransport
    {
        public RTCDtlsTransport NativeTransport { get; }
        public string State => NativeTransport.State;
        public IRTCIceTransport IceTransport => new BrowserRTCIceTransport(NativeTransport.IceTransport);
        public event Action<string>? OnStateChange;
        public event Action<string>? OnError;

        public BrowserRTCDtlsTransport(RTCDtlsTransport transport)
        {
            NativeTransport = transport;
            NativeTransport.OnStateChange += e => OnStateChange?.Invoke(State);
            NativeTransport.OnError += e => OnError?.Invoke("DTLS error");
        }
    }

    public class BrowserRTCIceTransport : IRTCIceTransport
    {
        public RTCIceTransport NativeTransport { get; }
        public string Component => NativeTransport.Component;
        public string GatheringState => NativeTransport.GatheringState;
        public string Role => NativeTransport.Role;
        public string State => NativeTransport.State;

        public BrowserRTCIceTransport(RTCIceTransport transport)
        {
            NativeTransport = transport;
        }
    }

    public class BrowserRTCSctpTransport : IRTCSctpTransport
    {
        // BlazorJS RTCSctpTransport is a JSObject with only a constructor
        // Properties need to be accessed via JS - but we should ask TJ
        // before using JSRef directly. For now, return safe defaults.
        public IRTCDtlsTransport Transport { get; }
        public string State { get; }
        public int MaxMessageSize { get; }
        public int? MaxChannels { get; }

        public BrowserRTCSctpTransport(RTCSctpTransport transport)
        {
            // BlazorJS RTCSctpTransport doesn't expose typed properties yet
            // These would need to be added to BlazorJS (Fix Library First rule)
            Transport = null!; // TODO: wire when BlazorJS exposes transport property
            State = "connected";
            MaxMessageSize = 262144;
            MaxChannels = 65535;
        }
    }
}

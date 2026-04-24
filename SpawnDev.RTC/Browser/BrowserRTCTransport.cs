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
        public RTCSctpTransport NativeTransport { get; }

        /// <summary>
        /// The DTLS transport this SCTP association runs over. Returns <c>null!</c> if
        /// the native property is not set (shouldn't happen once SCTP is up).
        /// </summary>
        public IRTCDtlsTransport Transport
        {
            get
            {
                var dtls = NativeTransport.Transport;
                return dtls == null ? null! : new BrowserRTCDtlsTransport(dtls);
            }
        }

        /// <summary>Current state: "connecting", "connected", or "closed".</summary>
        public string State => NativeTransport.State;

        /// <summary>
        /// Max SCTP message size in bytes (per-browser; Chrome caps at 262144 =
        /// 256 KiB - 1, Firefox supports larger). Spec returns double (Infinity possible);
        /// clamp to int.MaxValue if the native value overflows int.
        /// </summary>
        public int MaxMessageSize
        {
            get
            {
                var d = NativeTransport.MaxMessageSize;
                if (double.IsInfinity(d) || d > int.MaxValue) return int.MaxValue;
                if (d < 0) return 0;
                return (int)d;
            }
        }

        /// <summary>
        /// Max number of data channels the SCTP association can carry, or null when
        /// the browser doesn't expose a cap.
        /// </summary>
        public int? MaxChannels => NativeTransport.MaxChannels;

        public BrowserRTCSctpTransport(RTCSctpTransport transport)
        {
            NativeTransport = transport;
        }
    }
}

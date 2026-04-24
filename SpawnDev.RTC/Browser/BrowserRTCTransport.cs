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
        /// The DTLS transport this SCTP association runs over. Reads the live
        /// <c>transport</c> property through the BlazorJS JSRef accessor and wraps it
        /// in <see cref="BrowserRTCDtlsTransport"/> on demand. Returns <c>null!</c> if
        /// the native property is not set (shouldn't happen in practice once SCTP is up).
        /// </summary>
        public IRTCDtlsTransport Transport
        {
            get
            {
                var dtls = NativeTransport.JSRef!.Get<SpawnDev.BlazorJS.JSObjects.WebRTC.RTCDtlsTransport?>("transport");
                return dtls == null ? null! : new BrowserRTCDtlsTransport(dtls);
            }
        }

        /// <summary>Current state: "connecting", "connected", or "closed".</summary>
        public string State => NativeTransport.JSRef!.Get<string>("state");

        /// <summary>
        /// Max SCTP message size in bytes (per-browser; Chrome caps at 262144 =
        /// 256 KiB, Firefox supports much larger). Read live from the native object.
        /// </summary>
        public int MaxMessageSize
        {
            get
            {
                // Spec says double (so infinity possible), but in practice browsers report
                // finite sizes. Clamp to int.MaxValue if the native value overflows int.
                var d = NativeTransport.JSRef!.Get<double>("maxMessageSize");
                if (double.IsInfinity(d) || d > int.MaxValue) return int.MaxValue;
                if (d < 0) return 0;
                return (int)d;
            }
        }

        /// <summary>
        /// Max number of data channels the SCTP association can carry, or null when
        /// the browser doesn't expose a cap.
        /// </summary>
        public int? MaxChannels => NativeTransport.JSRef!.Get<int?>("maxChannels");

        public BrowserRTCSctpTransport(RTCSctpTransport transport)
        {
            NativeTransport = transport;
        }
    }
}

using SIPSorcery.Net;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of <see cref="IRTCStatsReport"/>. Produces a
    /// best-effort snapshot from the SipSorcery <see cref="RTCPeerConnection"/> -
    /// peer-connection state and transport basics. Not as rich as browser stats
    /// (no <c>inbound-rtp</c> byte counters, no <c>candidate-pair</c> RTT, no
    /// <c>codec</c> entries) because SipSorcery does not expose those counters
    /// on the public API surface; what we can report, we do.
    /// </summary>
    public class DesktopRTCStatsReport : IRTCStatsReport
    {
        private readonly Dictionary<string, RTCStatsEntry> _byId;
        private readonly RTCStatsEntry[] _entries;

        public int Size => _entries.Length;

        public DesktopRTCStatsReport() : this(null) { }

        public DesktopRTCStatsReport(RTCPeerConnection? pc)
        {
            var list = new List<RTCStatsEntry>();
            if (pc != null)
            {
                var timestamp = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                list.Add(new RTCStatsEntry
                {
                    Id = "pc",
                    Type = "peer-connection",
                    Timestamp = timestamp,
                    Values = new Dictionary<string, object?>
                    {
                        ["connectionState"] = ToW3CState(pc.connectionState.ToString()),
                        ["signalingState"] = ToW3CSignalingState(pc.signalingState.ToString()),
                        ["iceGatheringState"] = ToW3CState(pc.iceGatheringState.ToString()),
                        ["iceConnectionState"] = ToW3CState(pc.iceConnectionState.ToString()),
                    },
                });

                list.Add(new RTCStatsEntry
                {
                    Id = "T0",
                    Type = "transport",
                    Timestamp = timestamp,
                    Values = new Dictionary<string, object?>
                    {
                        ["sessionId"] = pc.SessionID,
                        ["dtlsState"] = pc.IsDtlsNegotiationComplete ? "connected" : "new",
                        ["dtlsCertificateSignatureAlgorithm"] = pc.DtlsCertificateSignatureAlgorithm,
                    },
                });
            }
            _entries = list.ToArray();
            _byId = _entries.ToDictionary(e => e.Id);
        }

        public RTCStatsEntry[] Entries() => _entries;
        public RTCStatsEntry? Get(string id) => _byId.TryGetValue(id, out var e) ? e : null;
        public bool Has(string id) => _byId.ContainsKey(id);
        public string[] Keys() => _byId.Keys.ToArray();
        public void Dispose() { }

        // SipSorcery uses PascalCase + underscore separators for compound states
        // (e.g. "have_local_offer"). The W3C standard is lowercase + hyphen
        // ("have-local-offer"). Downstream code typically wants the W3C shape so
        // logs and comparisons line up with the browser side.
        private static string ToW3CState(string s)
            => s.ToLowerInvariant();

        private static string ToW3CSignalingState(string s)
            => s.ToLowerInvariant().Replace('_', '-');
    }
}

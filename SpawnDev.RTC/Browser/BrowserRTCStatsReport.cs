using System.Text.Json;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.JSObjects.WebRTC;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of <see cref="IRTCStatsReport"/>. Wraps the native
    /// browser <see cref="RTCStatsReport"/> JSObject. Each entry's
    /// <see cref="RTCStatsEntry.Values"/> dictionary is populated by JSON-serializing
    /// the underlying JS stats object via <see cref="JSON.Stringify(object)"/>, so
    /// consumers get the full surface (<c>bytesReceived</c>, <c>packetsLost</c>,
    /// <c>jitter</c>, <c>roundTripTime</c>, etc.) - not just id/type/timestamp.
    /// </summary>
    public class BrowserRTCStatsReport : IRTCStatsReport
    {
        private readonly RTCStatsReport _report;

        public int Size => _report.Size;

        public BrowserRTCStatsReport(RTCStatsReport report)
        {
            _report = report;
        }

        public RTCStatsEntry[] Entries()
        {
            var results = new List<RTCStatsEntry>();
            foreach (var (_, stat) in _report.Entries)
            {
                try { results.Add(BuildEntry(stat)); }
                finally { stat.Dispose(); }
            }
            return results.ToArray();
        }

        public RTCStatsEntry? Get(string id)
        {
            var stat = _report.Get(id);
            if (stat == null) return null;
            try { return BuildEntry(stat); }
            finally { stat.Dispose(); }
        }

        public bool Has(string id) => _report.Has(id);

        public string[] Keys()
        {
            try { return _report.Keys(); }
            catch { return Entries().Select(e => e.Id).ToArray(); }
        }

        public void Dispose() => _report.Dispose();

        private static RTCStatsEntry BuildEntry(RTCStats stat)
        {
            var entry = new RTCStatsEntry
            {
                Id = stat.Id,
                Type = stat.Type,
                Timestamp = stat.Timestamp,
            };
            // Pull every enumerable property off the JS stat object into Values.
            // JSON.Stringify is the typed BlazorJS helper that does the right thing
            // for arbitrary RTCStats subtypes (candidate-pair, inbound-rtp,
            // outbound-rtp, transport, codec, etc.) without us hard-coding each.
            try
            {
                var json = JSON.Stringify(stat);
                if (!string.IsNullOrEmpty(json) && json != "null")
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (parsed != null)
                    {
                        foreach (var kvp in parsed)
                            entry.Values[kvp.Key] = ToClr(kvp.Value);
                    }
                }
            }
            catch
            {
                // Best-effort: if stringification fails we still return id/type/timestamp.
            }
            return entry;
        }

        private static object? ToClr(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object or JsonValueKind.Array => el.GetRawText(),
            _ => el.GetRawText(),
        };
    }
}

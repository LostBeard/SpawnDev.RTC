using SpawnDev.BlazorJS.JSObjects.WebRTC;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of IRTCStatsReport.
    /// Wraps BlazorJS RTCStatsReport.
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
            return _report.Entries.Select(e =>
            {
                var entry = new RTCStatsEntry
                {
                    Id = e.Item2.Id,
                    Type = e.Item2.Type,
                    Timestamp = e.Item2.Timestamp,
                };
                return entry;
            }).ToArray();
        }

        public RTCStatsEntry? Get(string id)
        {
            var stat = _report.Get(id);
            if (stat == null) return null;
            return new RTCStatsEntry
            {
                Id = stat.Id,
                Type = stat.Type,
                Timestamp = stat.Timestamp,
            };
        }

        public bool Has(string id) => _report.Has(id);
        public string[] Keys()
        {
            try { return _report.Keys(); }
            catch { return Entries().Select(e => e.Id).ToArray(); }
        }

        public void Dispose() => _report.Dispose();
    }
}

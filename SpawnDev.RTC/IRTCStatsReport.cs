namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform WebRTC statistics report.
    /// Contains connection quality metrics, candidate pair info, and track stats.
    /// </summary>
    public interface IRTCStatsReport : IDisposable
    {
        int Size { get; }
        RTCStatsEntry[] Entries();
        RTCStatsEntry? Get(string id);
        bool Has(string id);
        string[] Keys();
    }

    /// <summary>
    /// A single entry in an RTCStatsReport.
    /// </summary>
    public class RTCStatsEntry
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public double Timestamp { get; set; }
        public Dictionary<string, object?> Values { get; set; } = new();
    }
}

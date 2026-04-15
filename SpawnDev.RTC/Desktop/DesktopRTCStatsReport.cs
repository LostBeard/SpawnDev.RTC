namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCStatsReport.
    /// SipSorcery doesn't have browser-compatible stats, so this returns basic info.
    /// </summary>
    public class DesktopRTCStatsReport : IRTCStatsReport
    {
        public int Size => 0;
        public RTCStatsEntry[] Entries() => System.Array.Empty<RTCStatsEntry>();
        public RTCStatsEntry? Get(string id) => null;
        public bool Has(string id) => false;
        public string[] Keys() => System.Array.Empty<string>();
        public void Dispose() { }
    }
}

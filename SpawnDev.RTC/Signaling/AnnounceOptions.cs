namespace SpawnDev.RTC.Signaling;

/// <summary>
/// Per-announce parameters. Field names mirror the WebTorrent tracker wire format so
/// the same options work against any public tracker as well as
/// <c>SpawnDev.RTC.Server.TrackerSignalingServer</c>.
/// </summary>
public sealed class AnnounceOptions
{
    /// <summary>Announce event: <c>null</c> (periodic update), <c>"started"</c>, <c>"stopped"</c>, or <c>"completed"</c>.</summary>
    public string? Event { get; set; }

    /// <summary>How many peers we want the tracker to put us in touch with. The tracker may return fewer.</summary>
    public int NumWant { get; set; } = 10;

    /// <summary>Bytes uploaded. Left at 0 for non-torrent use cases.</summary>
    public long Uploaded { get; set; }

    /// <summary>Bytes downloaded. Left at 0 for non-torrent use cases.</summary>
    public long Downloaded { get; set; }

    /// <summary>Bytes left to download. <c>null</c> means unspecified; pass <c>1</c> for non-torrent peers that always "want more".</summary>
    public long? Left { get; set; } = 1;
}

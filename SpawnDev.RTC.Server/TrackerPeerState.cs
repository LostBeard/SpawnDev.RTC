namespace SpawnDev.RTC.Server;

/// <summary>
/// BEP 21 status of one announced peer in one torrent room.
/// The generic signaling service continues to accept peers that omit left,
/// retaining prior status until a subsequent announce provides enough information.
/// </summary>
public readonly record struct TrackerPeerState(bool IsSeeder, bool IsPartialSeed)
{
    public static TrackerPeerState FromAnnounce(string? announceEvent, long? left,
        TrackerPeerState previous = default)
    {
        if (announceEvent == "paused")
            return new TrackerPeerState(IsSeeder: false, IsPartialSeed: true);

        // A paused peer resuming without 'paused' is an active downloader.
        if (left == 0 || (left is null && announceEvent == "completed"))
            return new TrackerPeerState(IsSeeder: true, IsPartialSeed: false);
        if (left > 0)
            return new TrackerPeerState(IsSeeder: false, IsPartialSeed: false);

        // Generic RTC room consumers sometimes omit left. Preserve their previous
        // complete/incomplete classification, but clear upload-only on normal events.
        return new TrackerPeerState(IsSeeder: previous.IsSeeder, IsPartialSeed: false);
    }
}

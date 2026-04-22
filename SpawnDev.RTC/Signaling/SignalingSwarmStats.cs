namespace SpawnDev.RTC.Signaling;

/// <summary>
/// Swarm statistics reported by the tracker for a room. Matches the
/// WebTorrent tracker protocol <c>complete</c> (seeders) / <c>incomplete</c> (leechers) pair.
/// For non-torrent signaling consumers, treat <c>Complete + Incomplete</c> as the peer
/// count for the room.
/// </summary>
public sealed record SignalingSwarmStats(int Complete, int Incomplete);

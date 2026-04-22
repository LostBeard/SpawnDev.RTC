using System.Collections.Concurrent;

namespace SpawnDev.RTC.Server;

/// <summary>
/// Per-room state on <see cref="TrackerSignalingServer"/>. One instance per distinct
/// 20-byte room key (WebTorrent info_hash) that has at least one connected peer.
/// Rooms auto-remove when the last peer disconnects.
/// </summary>
public sealed class SignalingRoomInfo
{
    /// <summary>
    /// Room key as the raw UTF-16 string the client announced with. The wire
    /// format is a 20-byte latin1 binary string - any byte value is valid.
    /// </summary>
    public string RoomKey { get; }

    /// <summary>
    /// Active peers in this room, keyed by peer id. Thread-safe.
    /// </summary>
    public ConcurrentDictionary<string, SignalingPeer> Peers { get; } = new();

    /// <summary>Current seeder count (peers with <see cref="SignalingPeer.IsSeeder"/>).</summary>
    public int SeederCount => Peers.Values.Count(p => p.IsSeeder);

    /// <summary>Current leech count (peers that have not reported completed).</summary>
    public int LeechCount => Peers.Values.Count(p => !p.IsSeeder);

    internal SignalingRoomInfo(string roomKey)
    {
        RoomKey = roomKey;
    }
}

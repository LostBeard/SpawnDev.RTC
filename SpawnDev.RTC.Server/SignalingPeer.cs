using System.Net.WebSockets;

namespace SpawnDev.RTC.Server;

/// <summary>
/// A single peer connected to <see cref="TrackerSignalingServer"/>. One instance
/// per WebSocket. Lives for the lifetime of that connection.
/// </summary>
public sealed class SignalingPeer
{
    internal WebSocket WebSocket { get; }
    internal SemaphoreSlim SendLock { get; } = new(1, 1);

    /// <summary>
    /// 20-byte peer identifier as the raw UTF-16 string the client announced with.
    /// Empty until the first announce. Unique per connection within a room.
    /// </summary>
    public string PeerId { get; internal set; } = "";

    /// <summary>Remote IP:port captured at connect time.</summary>
    public string RemoteAddress { get; }

    /// <summary>UTC timestamp when the WebSocket was accepted.</summary>
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Kestrel's HttpContext.Connection.Id for this WebSocket. Empty for
    /// peers constructed from non-HTTP test rigs. Used by the diagnostic
    /// log path so each line is prefixed with the same connection id that
    /// Kestrel uses in its own connection-lifecycle log lines.
    /// </summary>
    public string ConnId { get; internal set; } = "";

    /// <summary>
    /// True when the peer's most recent announce reported <c>left=0</c> or
    /// <c>event=completed</c>. Counted as a seeder in swarm stats.
    /// </summary>
    public bool IsSeeder { get; internal set; }

    internal SignalingPeer(WebSocket ws, string remoteAddress)
    {
        WebSocket = ws;
        RemoteAddress = remoteAddress;
    }
}

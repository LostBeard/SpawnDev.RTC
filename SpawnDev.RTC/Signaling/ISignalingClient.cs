namespace SpawnDev.RTC.Signaling;

/// <summary>
/// WebRTC signaling client: discovers peers in a <see cref="RoomKey"/> and relays offers,
/// answers, and swarm stats for them. Decoupled from any specific peer implementation so
/// the same interface can back plain <c>IRTCPeerConnection</c> consumers (SpawnDev.RTC),
/// SimplePeer-wrapped consumers (SpawnDev.WebTorrent), or any future peer abstraction.
///
/// A single client can host multiple rooms on one transport connection; subscribe a handler
/// for each room before calling <see cref="AnnounceAsync"/>. The handler owns peer-connection
/// lifecycle and SDP generation - the signaling client only shuttles opaque offers/answers.
/// </summary>
public interface ISignalingClient : IAsyncDisposable
{
    /// <summary>Announce URL the client is bound to (e.g. <c>wss://tracker.example.com</c>).</summary>
    string AnnounceUrl { get; }

    /// <summary>This client's 20-byte peer id. Stable for the lifetime of the client.</summary>
    byte[] LocalPeerId { get; }

    /// <summary>True when the underlying transport is connected and ready to announce.</summary>
    bool IsConnected { get; }

    /// <summary>Raised after the transport (re)connects and is ready to send announces.</summary>
    event Action? OnConnected;

    /// <summary>Raised when the transport disconnects. The client will attempt to reconnect.</summary>
    event Action? OnDisconnected;

    /// <summary>Raised on non-fatal warnings (tracker warning messages, transient errors).</summary>
    event Action<string>? OnWarning;

    /// <summary>Raised when the signaling source reports swarm stats for a subscribed room.</summary>
    event Action<RoomKey, SignalingSwarmStats>? OnSwarmStats;

    /// <summary>
    /// Subscribe a handler for a room. The handler is invoked for all signaling events
    /// associated with <paramref name="roomKey"/> (generate offers on announce, handle
    /// incoming offers, route incoming answers). Replaces any prior handler for the room.
    /// </summary>
    void Subscribe(RoomKey roomKey, ISignalingRoomHandler handler);

    /// <summary>
    /// Unsubscribe a room. The client will stop invoking its handler but will not
    /// send a <c>stopped</c> announce - call <see cref="AnnounceAsync"/> with
    /// <c>Event = "stopped"</c> first if you need the server-side swarm to be updated.
    /// </summary>
    void Unsubscribe(RoomKey roomKey);

    /// <summary>
    /// Announce ourselves in <paramref name="roomKey"/>. For normal and <c>started</c>
    /// events the client will ask the subscribed handler to generate offers. For
    /// <c>stopped</c> no offers are generated.
    /// </summary>
    Task AnnounceAsync(RoomKey roomKey, AnnounceOptions options, CancellationToken ct = default);
}

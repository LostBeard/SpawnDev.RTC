namespace SpawnDev.RTC.Server;

/// <summary>
/// Configuration for <see cref="TrackerSignalingServer"/>. Sensible defaults are
/// chosen for a public-facing tracker deployment; tune via DI or inline.
/// </summary>
public sealed class TrackerServerOptions
{
    /// <summary>
    /// Announce interval returned to clients (seconds). Clients re-announce at least
    /// this often. WebTorrent's torrent-swarm convention is 120 s, but for on-demand
    /// WebRTC signaling (peers connecting/reconnecting interactively) that's too sparse -
    /// a dropped/slow peer waits too long to be rediscovered. 30 s keeps peers fresh.
    /// </summary>
    public int AnnounceIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// A peer that has not re-announced within this many seconds is treated as gone and evicted from
    /// its room(s) on the next announce touching that room (its dead socket is aborted). Without this,
    /// a peer that drops silently (network blip, NAT timeout, killed process with no TCP RST) lingers
    /// as a "candidate" until its WebSocket eventually notices - and the tracker keeps relaying offers
    /// into that void, so connecting peers waste offers on ghosts (slow/failed connects that compound
    /// with random per-connection peer-ids). Mirrors the JS bittorrent-tracker's miss-based eviction.
    /// <para>Only needs to exceed <see cref="AnnounceIntervalSeconds"/> by enough to tolerate announce
    /// jitter - a healthy peer re-announces every interval, so the margin just covers a late one.
    /// Over-eviction is self-healing (a wrongly-evicted live peer re-announces and re-joins on its next
    /// interval), so being aggressive is cheap. Default = 2x the interval.</para>
    /// </summary>
    public int PeerTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of peers returned per announce response. Clients request
    /// via <c>numwant</c>; the server clamps to this ceiling.
    /// </summary>
    public int MaxPeersPerAnnounce { get; set; } = 50;

    /// <summary>
    /// Maximum WebSocket message size accepted from a peer (bytes). Messages above
    /// this limit are dropped without closing the connection.
    /// </summary>
    public int MaxMessageBytes { get; set; } = 1_000_000;

    /// <summary>
    /// Per-send timeout when forwarding a relay to a target peer (milliseconds).
    /// Protects against stuck consumers blocking the tracker.
    /// </summary>
    public int SendTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Optional logger. Receives non-fatal diagnostics (bad frames, relay failures).
    /// If null, the server swallows these silently.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Optional Origin-header allowlist for incoming WebSocket upgrade requests.
    /// When non-null and non-empty, connections whose <c>Origin</c> header does
    /// not match any entry are rejected with HTTP 403 before the WebSocket
    /// handshake completes. When null or empty, no Origin check is performed
    /// (backward-compatible default for non-public deployments).
    /// <para>
    /// Entries can be:
    /// <list type="bullet">
    ///   <item>Exact origin: <c>https://app.example.com</c> (case-insensitive match).</item>
    ///   <item>Wildcard subdomain: <c>https://*.example.com</c> matches any
    ///         subdomain of <c>example.com</c> on the same scheme. Does NOT
    ///         match bare <c>example.com</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Use on public-facing signaling endpoints to reject browsers running on
    /// unaffiliated sites (basic abuse protection). Note that Origin is set by
    /// the browser and can be spoofed by non-browser clients; this is not a
    /// strong authentication mechanism. For stronger protection combine with
    /// ephemeral TURN credentials (<see cref="StunTurnServerOptions.EphemeralCredentialSharedSecret"/>)
    /// and/or authenticated signaling tokens.
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? AllowedOrigins { get; set; }
}

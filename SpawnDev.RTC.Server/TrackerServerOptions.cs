namespace SpawnDev.RTC.Server;

/// <summary>
/// Configuration for <see cref="TrackerSignalingServer"/>. Sensible defaults are
/// chosen for a public-facing tracker deployment; tune via DI or inline.
/// </summary>
public sealed class TrackerServerOptions
{
    /// <summary>
    /// Announce interval returned to clients (seconds). Clients re-announce at least
    /// this often. WebTorrent tracker convention is 120 s.
    /// </summary>
    public int AnnounceIntervalSeconds { get; set; } = 120;

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

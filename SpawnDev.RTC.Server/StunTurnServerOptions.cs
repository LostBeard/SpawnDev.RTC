using System.Net;

namespace SpawnDev.RTC.Server;

/// <summary>
/// Configuration for the embedded STUN/TURN server. Thin wrapper over SipSorcery's
/// <c>SIPSorcery.Net.TurnServerConfig</c> with sensible defaults for the typical
/// consumer scenario: same ASP.NET Core host that runs <see cref="TrackerSignalingServer"/>
/// also serves STUN binding + TURN relay on UDP port 3478.
/// </summary>
/// <remarks>
/// TurnServer is RFC 5766 compliant and handles both STUN (binding requests, no relay)
/// and TURN (allocation, relay, channel binding). No separate STUN server needed.
///
/// <para><strong>Production posture:</strong> SipSorcery's TurnServer is documented by
/// its author as "intended for development, testing, and small-scale/embedded scenarios
/// - not for production use at scale (use coturn or similar for that)." It supports a
/// single static credential, no rate-limiting, no TLS for the control channel, and no
/// TCP relay. For self-hosted signaling + TURN on the same box for a small tenant
/// (team chat, agent swarm, dev environment) it's fine. For public-internet high-traffic
/// TURN, run coturn and leave this off.</para>
///
/// <para><strong>Security:</strong> The defaults <c>"turn-user"</c> / <c>"turn-pass"</c>
/// are intentionally weak so you notice to replace them. Read credentials from config /
/// env-vars, never hardcode.</para>
/// </remarks>
public sealed class StunTurnServerOptions
{
    /// <summary>
    /// Enable the STUN/TURN listener. Defaults to <c>false</c> - the STUN/TURN server
    /// is opt-in so existing consumers who only want the WebSocket signaling tracker
    /// don't unexpectedly open UDP port 3478.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Address to bind the STUN/TURN listener to. Defaults to <see cref="IPAddress.Any"/>
    /// (all interfaces). Use <see cref="IPAddress.Loopback"/> to restrict to local host.
    /// </summary>
    public IPAddress ListenAddress { get; set; } = IPAddress.Any;

    /// <summary>UDP (and optionally TCP) port to listen on. Standard TURN port is 3478.</summary>
    public int Port { get; set; } = 3478;

    /// <summary>Accept TCP control connections in addition to UDP. Default: true.</summary>
    public bool EnableTcp { get; set; } = true;

    /// <summary>Accept UDP control datagrams. Default: true.</summary>
    public bool EnableUdp { get; set; } = true;

    /// <summary>
    /// Public address to advertise in <c>XOR-RELAYED-ADDRESS</c> responses. Set this when
    /// the server is behind NAT (your router's external IP). Leave null to advertise
    /// <see cref="ListenAddress"/> - correct for direct-internet deployments and for
    /// LAN-only testing.
    /// </summary>
    public IPAddress? RelayAddress { get; set; } = null;

    /// <summary>Long-term credential username (RFC 5389 Section 10.2). Required for TURN.</summary>
    public string Username { get; set; } = "turn-user";

    /// <summary>Long-term credential password. Defaults are intentionally weak - replace via config.</summary>
    public string Password { get; set; } = "turn-pass";

    /// <summary>REALM value for TURN auth challenges. Defaults to "spawndev-rtc".</summary>
    public string Realm { get; set; } = "spawndev-rtc";

    /// <summary>
    /// Default allocation lifetime in seconds. TURN clients send Refresh requests to
    /// keep their allocations alive past this. 600 seconds (10 minutes) matches coturn's
    /// default and the typical WebRTC-ICE-idle-timeout range.
    /// </summary>
    public int DefaultLifetimeSeconds { get; set; } = 600;
}

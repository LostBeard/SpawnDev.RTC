using Microsoft.AspNetCore.HttpOverrides;
using SIPSorcery.Net;
using SpawnDev.RTC.Server;
using SpawnDev.RTC.Server.Extensions;
using System.Net;

// SpawnDev.RTC.ServerApp - minimal standalone host for the WebTorrent-compatible
// signaling tracker. Zero-config startup: runs on port 5590 over HTTP by default
// (override with ASPNETCORE_URLS for TLS or a reverse proxy). Docker image binds
// 0.0.0.0:8080.
//
// Optional features (all disabled by default, enable via env-var or appsettings):
//   RTC__AllowedOrigins                        Semicolon-separated Origin allowlist
//                                              (abuse protection for public signaling).
//   RTC__StunTurn__Enabled                     Run an embedded STUN/TURN server.
//   RTC__StunTurn__Port                        UDP port (default 3478).
//   RTC__StunTurn__ListenAddress               IP to bind (default 0.0.0.0).
//   RTC__StunTurn__RelayAddress                Public IP to advertise in XOR-RELAYED-ADDRESS
//                                              (set when behind NAT).
//   RTC__StunTurn__Realm                       TURN auth realm (default "spawndev-rtc").
//   RTC__StunTurn__Username                    Long-term credential username
//                                              (ignored when EphemeralCredentialSharedSecret is set).
//   RTC__StunTurn__Password                    Long-term credential password.
//   RTC__StunTurn__EphemeralCredentialSharedSecret  HMAC secret for RFC 8489 §9.2 ephemeral
//                                              credentials (Twilio/Cloudflare TURN REST API
//                                              pattern). When set, static username/password
//                                              are ignored.
//   RTC__StunTurn__TrackerGated                When true AND EphemeralCredentialSharedSecret
//                                              is set, only peers currently announced to the
//                                              signaling tracker can obtain a TURN allocation.
//   RTC__StunTurn__RelayPortRangeStart         Low bound of per-allocation relay ports (inclusive).
//   RTC__StunTurn__RelayPortRangeEnd           High bound (inclusive). Both must be set together.
//                                              Constrain to a forwardable range when the host is
//                                              behind NAT. Default 0 = OS ephemeral (not forwardable).

var builder = WebApplication.CreateBuilder(args);

// Default dev binding. ASPNETCORE_URLS overrides unconditionally, which is what
// container images and production deployments should use.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://0.0.0.0:5590");

// Trust X-Forwarded-* headers from a reverse proxy (nginx / haproxy / Caddy /
// Traefik / Cloudflare). Without this, ctx.Request.Scheme reflects the internal
// HTTP scheme and any logs/metrics that rely on it are wrong.
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownNetworks.Clear();
    opts.KnownProxies.Clear();
});

// Tracker tuning. Env vars (prefixed `RTC__`) or appsettings.json both work thanks
// to ASP.NET Core's default configuration builder.
var trackerOptions = new TrackerServerOptions
{
    AnnounceIntervalSeconds = builder.Configuration.GetValue("RTC:AnnounceIntervalSeconds", 120),
    MaxPeersPerAnnounce = builder.Configuration.GetValue("RTC:MaxPeersPerAnnounce", 50),
    MaxMessageBytes = builder.Configuration.GetValue("RTC:MaxMessageBytes", 1_000_000),
    SendTimeoutMs = builder.Configuration.GetValue("RTC:SendTimeoutMs", 10_000),
};

// Origin allowlist (semicolon-separated). When unset, no Origin check is performed.
var allowedOriginsRaw = builder.Configuration.GetValue<string?>("RTC:AllowedOrigins");
if (!string.IsNullOrWhiteSpace(allowedOriginsRaw))
{
    trackerOptions.AllowedOrigins = allowedOriginsRaw
        .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToArray();
}

var announcePath = builder.Configuration.GetValue("RTC:Path", "/announce")!;

var app = builder.Build();

app.UseForwardedHeaders();
app.UseWebSockets();
var tracker = app.UseRtcSignaling(announcePath, trackerOptions);

// Optional embedded STUN/TURN server. Constructed inline (rather than via
// AddRtcStunTurn) so the tracker-gated resolver can close over the tracker
// instance that was just created.
TurnServer? turnServer = null;
var turnEnabled = builder.Configuration.GetValue("RTC:StunTurn:Enabled", false);
if (turnEnabled)
{
    var turnListen = builder.Configuration.GetValue("RTC:StunTurn:ListenAddress", "0.0.0.0");
    var turnRelay = builder.Configuration.GetValue<string?>("RTC:StunTurn:RelayAddress");
    var turnRealm = builder.Configuration.GetValue("RTC:StunTurn:Realm", "spawndev-rtc");
    var turnConfig = new TurnServerConfig
    {
        ListenAddress = IPAddress.Parse(turnListen),
        Port = builder.Configuration.GetValue("RTC:StunTurn:Port", 3478),
        EnableTcp = builder.Configuration.GetValue("RTC:StunTurn:EnableTcp", true),
        EnableUdp = builder.Configuration.GetValue("RTC:StunTurn:EnableUdp", true),
        RelayAddress = !string.IsNullOrWhiteSpace(turnRelay)
            ? IPAddress.Parse(turnRelay)
            : IPAddress.Parse(turnListen),
        Username = builder.Configuration.GetValue("RTC:StunTurn:Username", "turn-user"),
        Password = builder.Configuration.GetValue("RTC:StunTurn:Password", "turn-pass"),
        Realm = turnRealm,
        DefaultLifetimeSeconds = builder.Configuration.GetValue("RTC:StunTurn:DefaultLifetimeSeconds", 600),
        RelayPortRangeStart = builder.Configuration.GetValue("RTC:StunTurn:RelayPortRangeStart", 0),
        RelayPortRangeEnd = builder.Configuration.GetValue("RTC:StunTurn:RelayPortRangeEnd", 0),
    };

    var sharedSecret = builder.Configuration.GetValue<string?>("RTC:StunTurn:EphemeralCredentialSharedSecret");
    var trackerGated = builder.Configuration.GetValue("RTC:StunTurn:TrackerGated", false);

    if (!string.IsNullOrEmpty(sharedSecret))
    {
        turnConfig.ResolveHmacKey = trackerGated
            ? EphemeralTurnCredentials.TrackerGatedResolver(sharedSecret, turnRealm, tracker)
            : username => EphemeralTurnCredentials.ResolveLongTermKey(sharedSecret, turnRealm, username);
    }
    else if (trackerGated)
    {
        // Tracker gating is only meaningful with ephemeral credentials. Fail loud
        // rather than silently running with static credentials and no gate.
        throw new InvalidOperationException(
            "RTC__StunTurn__TrackerGated=true requires RTC__StunTurn__EphemeralCredentialSharedSecret to also be set.");
    }

    turnServer = new TurnServer(turnConfig);
    turnServer.Start();
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        try { turnServer.Stop(); } catch { /* best-effort cleanup */ }
        turnServer.Dispose();
    });
}

var version = typeof(TrackerSignalingServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";

app.MapGet("/", () => new
{
    name = "SpawnDev.RTC.ServerApp",
    version,
    description = "WebRTC signaling server speaking the WebTorrent tracker wire protocol.",
    webtorrentCompatible = true,
    stunTurnEnabled = turnEnabled,
    originAllowlistEnabled = trackerOptions.AllowedOrigins is { Count: > 0 },
    endpoints = new
    {
        announce = $"{announcePath} (WebSocket)",
        stats = "/stats",
        health = "/health",
    },
    repository = "https://github.com/LostBeard/SpawnDev.RTC",
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    rooms = tracker.Rooms.Count,
    peers = tracker.TotalPeers,
}));

app.MapGet("/stats", () => new
{
    rooms = tracker.Rooms.Count,
    totalPeers = tracker.TotalPeers,
    roomDetails = tracker.Rooms.Select(r => new
    {
        // Room keys on the wire are 20 raw bytes; the server stores them as a
        // latin1 string so we have to round-trip to bytes to hex-stringify.
        roomKey = Convert.ToHexString(System.Text.Encoding.Latin1.GetBytes(r.Key)),
        peers = r.Value.Peers.Count,
        seeders = r.Value.SeederCount,
        leechers = r.Value.LeechCount,
    }),
});

Console.WriteLine($"SpawnDev.RTC.ServerApp {version} - WebTorrent-compatible signaling server");
Console.WriteLine($"  Announce: {announcePath} (WebSocket)");
Console.WriteLine($"  Health:   /health");
Console.WriteLine($"  Stats:    /stats");
if (trackerOptions.AllowedOrigins is { Count: > 0 } allowList)
    Console.WriteLine($"  Origin allowlist: {string.Join(", ", allowList)}");
if (turnEnabled && turnServer != null)
{
    var authMode = builder.Configuration.GetValue<string?>("RTC:StunTurn:EphemeralCredentialSharedSecret") is { Length: > 0 }
        ? (builder.Configuration.GetValue("RTC:StunTurn:TrackerGated", false) ? "ephemeral + tracker-gated" : "ephemeral")
        : "long-term";
    Console.WriteLine($"  STUN/TURN: UDP :{builder.Configuration.GetValue("RTC:StunTurn:Port", 3478)} (auth={authMode})");
    var rangeStart = builder.Configuration.GetValue("RTC:StunTurn:RelayPortRangeStart", 0);
    var rangeEnd = builder.Configuration.GetValue("RTC:StunTurn:RelayPortRangeEnd", 0);
    if (rangeStart > 0 && rangeEnd >= rangeStart)
        Console.WriteLine($"  Relay ports: UDP {rangeStart}-{rangeEnd} (forward this range at your NAT)");
    else
        Console.WriteLine("  Relay ports: OS ephemeral (set RelayPortRangeStart/End when behind NAT)");
}
Console.WriteLine($"  Source:   https://github.com/LostBeard/SpawnDev.RTC");

app.Run();

using Microsoft.AspNetCore.HttpOverrides;
using SpawnDev.RTC.Server;
using SpawnDev.RTC.Server.Extensions;

// SpawnDev.RTC.ServerApp - minimal standalone host for the WebTorrent-compatible
// signaling tracker. Zero-config startup: runs on port 5590 over HTTP by default
// (override with ASPNETCORE_URLS for TLS or a reverse proxy). Docker image binds
// 0.0.0.0:8080.

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

// Tracker tuning. Env vars (prefixed `RTC_`) or appsettings.json both work thanks
// to ASP.NET Core's default configuration builder.
var trackerOptions = new TrackerServerOptions
{
    AnnounceIntervalSeconds = builder.Configuration.GetValue("RTC:AnnounceIntervalSeconds", 120),
    MaxPeersPerAnnounce = builder.Configuration.GetValue("RTC:MaxPeersPerAnnounce", 50),
    MaxMessageBytes = builder.Configuration.GetValue("RTC:MaxMessageBytes", 1_000_000),
    SendTimeoutMs = builder.Configuration.GetValue("RTC:SendTimeoutMs", 10_000),
};

var announcePath = builder.Configuration.GetValue("RTC:Path", "/announce")!;

var app = builder.Build();

app.UseForwardedHeaders();
app.UseWebSockets();
var tracker = app.UseRtcSignaling(announcePath, trackerOptions);

var version = typeof(TrackerSignalingServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";

app.MapGet("/", () => new
{
    name = "SpawnDev.RTC.ServerApp",
    version,
    description = "WebRTC signaling server speaking the WebTorrent tracker wire protocol.",
    webtorrentCompatible = true,
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
Console.WriteLine($"  Source:   https://github.com/LostBeard/SpawnDev.RTC");

app.Run();

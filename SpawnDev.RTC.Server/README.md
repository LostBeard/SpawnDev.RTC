# SpawnDev.RTC.Server

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.RTC.Server.svg)](https://www.nuget.org/packages/SpawnDev.RTC.Server)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

WebRTC signaling **server** for [SpawnDev.RTC](https://github.com/LostBeard/SpawnDev.RTC) consumers. Hosts the WebTorrent tracker wire protocol so any ASP.NET Core app can run a room-based signaling endpoint with a single `app.UseRtcSignaling("/announce")` call. Bundles an embedded RFC 5766 STUN/TURN server that can run alongside on the same host.

For the **client** library see [SpawnDev.RTC](https://www.nuget.org/packages/SpawnDev.RTC).

## What this gives you

Three composable pieces, all opt-in:

| Piece | Method | Purpose |
|-------|--------|---------|
| **Signaling tracker** | `app.UseRtcSignaling("/announce")` | WebSocket endpoint that brokers WebRTC offer/answer between peers. Bit-compatible with the public WebTorrent tracker fleet — your clients (using SpawnDev.RTC, JS WebTorrent, libtorrent v2 with WSS, etc.) just point at your URL instead of `wss://tracker.openwebtorrent.com`. |
| **STUN/TURN server** | `builder.Services.AddRtcStunTurn(opts => ...)` | Full RFC 5766 TURN with classic long-term creds OR ephemeral HMAC creds (RFC 8489 §9.2 / TURN REST API pattern). Optionally tracker-gated so only currently-announced peers can allocate. Replaces coturn for SpawnDev-style deployments. |
| **Origin allowlist** | property on the signaling-server options | Browser-side abuse protection: only origins on the allowlist can open a signaling WebSocket. Non-browser clients (desktop C#, `curl`, Node.js `ws`) bypass the check automatically because they don't send an Origin header. |

## Install

```xml
<PackageReference Include="SpawnDev.RTC.Server" Version="1.0.5" />
```

Or run the standalone host directly: [SpawnDev.RTC.ServerApp](https://github.com/LostBeard/SpawnDev.RTC) — single executable, Docker image, env-var configurable. Both the library and the app share the same internals; pick whichever suits your deployment.

## Quickstart — embedded signaling

```csharp
using SpawnDev.RTC.Server.Extensions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();
app.UseRtcSignaling("/announce");   // your tracker is now live

app.Run();
```

Every WebTorrent-compatible client can now use `wss://your-host/announce` as a tracker URL. Peers in the same `infohash` room get paired automatically.

## Quickstart — embedded STUN/TURN

Add it during service registration, separate from signaling. Both can run in the same host process:

```csharp
using SpawnDev.RTC.Server.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRtcStunTurn(opts =>
{
    opts.Enabled = true;
    opts.ListenAddress = "0.0.0.0";
    opts.Port = 3478;                            // standard STUN/TURN port
    opts.Realm = "your-domain.example";

    // Long-term credentials (classic TURN auth)
    opts.LongTermUsername = "alice";
    opts.LongTermPassword = "alice-secret";

    // OR ephemeral REST API credentials (preferred for production)
    opts.EphemeralSharedSecret = "your-32-byte-shared-secret-here";
    opts.EphemeralLifetime = TimeSpan.FromHours(1);

    // OR tracker-gated ephemeral: only peers currently announced to your
    // tracker can allocate. Combine with the signaling layer above.
    opts.TrackerGated = true;

    opts.RelayPortRangeStart = 49152;            // for NAT port forwarding
    opts.RelayPortRangeEnd = 65535;
});

var app = builder.Build();

app.UseWebSockets();
app.UseRtcSignaling("/announce");

app.Run();
```

Or bind from `appsettings.json`:

```csharp
builder.Services.AddRtcStunTurn(builder.Configuration.GetSection("Turn"));
```

```json
{
  "Turn": {
    "Enabled": true,
    "Port": 3478,
    "Realm": "your-domain.example",
    "EphemeralSharedSecret": "your-32-byte-shared-secret-here"
  }
}
```

Full options + walkthrough at [`Docs/stun-turn-server.md`](https://github.com/LostBeard/SpawnDev.RTC/blob/master/SpawnDev.RTC/Docs/stun-turn-server.md) in the parent repo.

## Origin allowlist

Browsers send an `Origin` header on every WebSocket connection. Setting `AllowedOrigins` rejects browser connections from origins not on the list — useful for keeping a tracker private to your own apps without firewalling the port:

```csharp
app.UseRtcSignaling("/announce", opts =>
{
    opts.AllowedOrigins = new[] { "https://app.example.com", "https://staging.example.com" };
});
```

Desktop / CLI clients (`SpawnDev.RTC` desktop, JS WebTorrent on Node, `curl`) don't send Origin; the allowlist auto-bypasses them so non-browser clients keep working. Locked by `OriginAllowlist_E2E_MissingOriginBypassesList` in the RTC test suite.

## What's in the package

- **`TrackerSignalingServer`** — the signaling endpoint. Speaks the same wire protocol as `tracker.openwebtorrent.com` (JSON `announce` / `offer` / `answer` / `signal` messages over WebSocket). Per-room pairing keyed on info_hash.
- **`StunTurnServerHostedService`** — the embedded TURN server, registered as an `IHostedService` so the lifetime is managed by ASP.NET Core's host. Built on the bundled SipSorcery fork.
- **`EphemeralTurnCredentials`** — utility for issuing/validating ephemeral usernames + HMAC passwords. Pair with `TurnServerConfig.ResolveHmacKey` for tenant-aware credential rotation.
- **`UseRtcSignaling` / `AddRtcStunTurn`** extension methods on `IApplicationBuilder` / `IServiceCollection`.

## Production deployments

`hub.spawndev.com:44365` runs this exact stack. Real-world load: dozens of concurrent rooms, simultaneous TURN allocations, browser + desktop clients pairing through it daily.

For the standalone-executable + Docker route, see the [SpawnDev.RTC.ServerApp](https://github.com/LostBeard/SpawnDev.RTC) project — same library, env-var-driven configuration, single deployable artifact.

## Dependencies

- `SpawnDev.RTC` 1.1.6 (signaling client primitives, room key types)
- ASP.NET Core 10 (`Microsoft.AspNetCore.App` framework reference)
- SipSorcery fork (bundled inline via SpawnDev.RTC) — for the TURN server's underlying RFC 5766 implementation

## Documentation

| Topic | Doc |
|-------|-----|
| Signaling protocol overview | [`Docs/signaling-overview.md`](https://github.com/LostBeard/SpawnDev.RTC/blob/master/SpawnDev.RTC/Docs/signaling-overview.md) |
| Running a tracker (this library) | [`Docs/run-a-tracker.md`](https://github.com/LostBeard/SpawnDev.RTC/blob/master/SpawnDev.RTC/Docs/run-a-tracker.md) |
| STUN/TURN server reference | [`Docs/stun-turn-server.md`](https://github.com/LostBeard/SpawnDev.RTC/blob/master/SpawnDev.RTC/Docs/stun-turn-server.md) |

## License

MIT — see [LICENSE.txt](https://github.com/LostBeard/SpawnDev.RTC/blob/master/SpawnDev.RTC/LICENSE.txt) in the parent repository.

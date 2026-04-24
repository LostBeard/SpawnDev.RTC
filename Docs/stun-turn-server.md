# Embedded STUN/TURN server

`SpawnDev.RTC.Server` ships an embedded STUN/TURN server (RFC 5389 / RFC 5766) that runs as an ASP.NET Core `IHostedService` alongside the signaling tracker. One process, one box, both the room-signaling WebSocket and the NAT-traversal relay.

No coturn dependency. No separate container. Plug in, configure, done.

---

## When you need this

- **STUN (binding request)** lets peers discover their public reflexive address behind NAT. Required for the vast majority of WebRTC peer connections - two peers behind symmetric NATs will never meet without it.
- **TURN (allocation + relay)** relays peer traffic through your server when direct peer-to-peer ICE fails. Happens for ~10-20% of connections in the wild (symmetric-NAT pairs, corporate firewalls, mobile carrier CGNAT).

If you ship a WebRTC app to public users, you need both. For a hobby project on a LAN, you can often skip TURN.

## Quick start

```csharp
using SpawnDev.RTC.Server.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRtcStunTurn(opts =>
{
    opts.Enabled = true;
    opts.Port = 3478;                              // standard STUN/TURN port
    opts.RelayAddress = IPAddress.Parse("<your public IP>");
    opts.Username = builder.Configuration["Turn:Username"]!;
    opts.Password = builder.Configuration["Turn:Password"]!;
});

var app = builder.Build();
app.UseWebSockets();
app.UseRtcSignaling("/announce");
app.Run();
```

Or bind from config:

```csharp
builder.Services.AddRtcStunTurn(builder.Configuration.GetSection("Turn"));
```

`AddRtcStunTurn` registers `StunTurnServerHostedService` which starts with the host and stops with it. When `Enabled = false` (the default), the hosted service is registered but the listener is never opened - zero footprint for consumers who only want the signaling tracker.

---

## Auth modes

### Long-term credentials (default)

Single static username / password pair configured at startup. Matches the classic RFC 5389 §10.2 pattern.

```csharp
opts.Username = "turn-user";
opts.Password = "turn-pass";    // change the defaults before going public!
opts.Realm    = "spawndev-rtc";
```

Good for single-tenant deployments or personal use. Replace the weak defaults or the hosted service logs a warning on startup.

### Ephemeral credentials (TURN REST API)

Time-limited HMAC-SHA1 credentials generated per user, per session. The standard pattern used by Twilio, Cloudflare, coturn's `--use-auth-secret`, and nearly every public TURN-as-a-service offering (RFC 8489 §9.2).

Your app backend mints `(username, password)` pairs from a shared secret. The TURN server validates them without a user database - pure cryptographic check. Credentials expire automatically.

**Server config:**
```csharp
opts.EphemeralCredentialSharedSecret = "<32+ byte random secret>";
// Username/Password above are ignored when the shared secret is set.
```

**App backend minting credentials:**
```csharp
using SpawnDev.RTC.Server;  // EphemeralTurnCredentials lives here

var (username, password) = EphemeralTurnCredentials.Generate(
    sharedSecret: "<same secret>",
    userId: "alice",                 // audit / debug only - any string w/o ':'
    lifetime: TimeSpan.FromHours(1)  // typical 1-24h
);

// Hand these to the browser as RTCIceServer credentials:
//   { urls: "turn:turn.example.com:3478", username, credential: password }
```

The username encodes the expiry Unix timestamp + userId (`"<expiry>:<userId>"`) so the server can reject expired credentials at validation time without a database lookup.

### Tracker-gated ephemeral credentials

The same ephemeral-credential pattern, but the server also requires the `userId` segment to match a peer currently announced to the signaling tracker. A stolen credential alone is not enough - the attacker also needs to maintain a live WebSocket session with the tracker, at which point their peer id is visible to you via `TrackerSignalingServer.ConnectedPeerIds`.

```csharp
// Program.cs - tracker must be created BEFORE the TURN server so the
// resolver can close over its instance.
var tracker = app.UseRtcSignaling("/announce");

builder.Services.AddRtcStunTurn(opts =>
{
    opts.Enabled = true;
    opts.EphemeralCredentialSharedSecret = "<secret>";
    opts.ResolveHmacKey = EphemeralTurnCredentials.TrackerGatedResolver(
        "<secret>", opts.Realm, tracker);
});
```

Or use the `SpawnDev.RTC.ServerApp` / `SpawnDev.WebTorrent.ServerApp` turnkey binaries - set `RTC__StunTurn__TrackerGated=true` in env and it wires itself up.

### Period-rotating sub-secrets

For deployments that want to rotate the master TURN secret without disrupting in-flight sessions. The resolver derives a per-period sub-secret as `HMAC-SHA256(masterSecret, floor(expiryUnix / periodSeconds))`; both sides derive the same sub-secret from the expiry time in the username. If the master leaks, only credentials whose expiry falls in still-valid periods are at risk.

```csharp
opts.ResolveHmacKey = EphemeralTurnCredentials.PeriodRotatingResolver(
    masterSecret: "<master>",
    realm: opts.Realm,
    periodSeconds: 3600);    // 1-hour periods

// Clients mint via the companion issuer:
var (user, pass) = EphemeralTurnCredentials.GeneratePeriodic(
    masterSecret: "<master>",
    userId: "alice",
    lifetime: TimeSpan.FromMinutes(30),  // must fit inside one period
    periodSeconds: 3600);
```

Typical periods: 1-24 hours. Shorter = tighter blast radius on compromise, but credentials issued near a period boundary may already be close to invalid.

### Full-custom resolver

If none of the above fit your auth story, plug in an arbitrary delegate:

```csharp
opts.ResolveHmacKey = username =>
{
    // Look up username in your database / session cache / whatever.
    // Return the 20-byte MD5(username:realm:password) HMAC key, or null to reject.
    var user = _userService.GetByUsername(username);
    if (user == null || user.IsExpired) return null;
    return MD5.HashData(Encoding.UTF8.GetBytes(
        $"{username}:{opts.Realm}:{user.TurnPassword}"));
};
```

---

## NAT port forwarding

TURN relay needs external clients to reach both the control-channel port (3478) AND per-allocation relay ports (ephemeral by default). If your host is behind NAT, forward both.

```csharp
// Constrain relay sockets to a forwardable range.
opts.RelayPortRangeStart = 49200;
opts.RelayPortRangeEnd   = 49299;
```

Then forward UDP `49200-49299` from the router to the host, in addition to UDP/TCP `3478`. The server walks the range from a random start offset under concurrency, falls back to OS ephemeral only if every port in the range is in use.

For cloud / VPS deployments with a direct public IP and open firewall, leave `RelayPortRange*` at 0 (default = OS ephemeral).

---

## Origin allowlist for signaling

Pair the TURN server with an Origin-header allowlist on the WebSocket signaling endpoint. Basic abuse protection - rejects upgrade requests from browsers running on unaffiliated sites before the WebSocket handshake completes.

```csharp
var trackerOpts = new TrackerServerOptions
{
    AllowedOrigins = new[]
    {
        "https://hub.spawndev.com",     // exact match (case-insensitive)
        "https://*.spawndev.com",       // wildcard subdomain
    },
};
app.UseRtcSignaling("/announce", trackerOpts);
```

Returns HTTP 403 to any WebSocket upgrade with an Origin that doesn't match. Note that Origin is browser-enforced; non-browser clients can spoof it, so this is not strong authentication - combine with ephemeral TURN credentials and/or signed signaling tokens.

---

## Env-var shortcut (turnkey deployment)

Both `SpawnDev.RTC.ServerApp` and `SpawnDev.WebTorrent.ServerApp` read all of the above from environment variables. Dump-and-run deployment.

| Env var | Example | Notes |
|---|---|---|
| `RTC__AllowedOrigins` | `https://hub.example.com;https://*.example.com` | Semicolon or comma separated |
| `RTC__StunTurn__Enabled` | `true` | Default `false` (opt-in) |
| `RTC__StunTurn__Port` | `3478` | Standard TURN port |
| `RTC__StunTurn__ListenAddress` | `0.0.0.0` | Bind address |
| `RTC__StunTurn__RelayAddress` | `64.246.234.108` | **Your public IP** (advertised in XOR-RELAYED-ADDRESS) |
| `RTC__StunTurn__Realm` | `spawndev-rtc` | TURN auth realm |
| `RTC__StunTurn__Username` | `turn-user` | Long-term username (ignored when EphemeralCredentialSharedSecret is set) |
| `RTC__StunTurn__Password` | `<password>` | Long-term password |
| `RTC__StunTurn__EphemeralCredentialSharedSecret` | `<32 byte random>` | Enables RFC 8489 §9.2 ephemeral creds |
| `RTC__StunTurn__TrackerGated` | `true` | Requires EphemeralCredentialSharedSecret |
| `RTC__StunTurn__RelayPortRangeStart` | `49200` | NAT forwardable range start |
| `RTC__StunTurn__RelayPortRangeEnd` | `49299` | NAT forwardable range end |

## Post-deploy verification

`GET https://<host>:<port>/` returns a JSON health doc including the active STUN/TURN state:

```json
{
  "stunTurn": { "enabled": true, "port": 3478, "authMode": "ephemeral + tracker-gated" },
  "originAllowlistEnabled": true
}
```

For deeper verification, the `HubDeploymentSmokeTests` class in `SpawnDev.RTC.DemoConsole` runs end-to-end against a deployed hub:

- `Smoke_StunBindingRequest_HubRespondsOnUdp` - UDP 3478 reachable + STUN answering (no credentials needed).
- `Smoke_TrackerGatedTurn_DenyThenAllowThenDeny` - full 5-step cycle proving tracker-gate enforcement from an external client's perspective.

Set `HUB_TRACKER_WS`, `HUB_TURN_HOST`, `HUB_SHARED_SECRET` env vars then:

```
dotnet run --no-build -c Release -- HubDeploymentSmokeTests.Smoke_TrackerGatedTurn_DenyThenAllowThenDeny
```

## Production posture

The embedded TURN server is derived from the SpawnDev fork of SipSorcery's `TurnServer`. Its author documents it as "intended for development, testing, and small-scale/embedded scenarios - not for production use at scale (use coturn or similar for that)". Reasonable small deployments:

- Hobby projects, demos, personal apps (gets you off public STUN services like `stun.l.google.com` which Google rate-limits and can disappear at any time)
- Self-hosted signaling + TURN on the same box for a single tenant
- Agent swarms, dev environments, team chat rooms

For genuine public-internet scale (thousands of concurrent relays, multi-GB/s throughput), run coturn and point your clients at it via the browser's standard `RTCIceServer` config. The signaling tracker in `SpawnDev.RTC.Server` is independent and handles that case just fine.

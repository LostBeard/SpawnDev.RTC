# SpawnDev.RTC Embedded STUN/TURN Server

`SpawnDev.RTC.Server` ships an embedded STUN/TURN server (RFC 5389/5766/8489) wired into ASP.NET Core as an `IHostedService`. One process can run WebSocket signaling + STUN binding + TURN relay together, sharing the same UDP port 3478.

This document covers the protocol surface (STUN messages, TURN allocation lifecycle, credential models) and the SpawnDev-specific extensions (tracker-gated allocation, NAT port-range bounding, ephemeral REST API credentials).

## Table of Contents

1. [Why embedded](#1-why-embedded)
2. [STUN — the binding service](#2-stun--the-binding-service)
3. [TURN — the relay service](#3-turn--the-relay-service)
4. [Authentication models](#4-authentication-models)
5. [Tracker-gated TURN — SpawnDev extension](#5-tracker-gated-turn--spawndev-extension)
6. [NAT port-range bounding](#6-nat-port-range-bounding)
7. [Configuration reference](#7-configuration-reference)

---

## 1. Why embedded

The classic deployment for a WebRTC application is a separate `coturn` (or pion `turnsd`) process for STUN/TURN, alongside the application server. SpawnDev.RTC.Server bundles the whole thing into one ASP.NET Core process for three reasons:

1. **Single deployment unit.** One systemd service, one Docker image, one set of credentials.
2. **Tracker-gated TURN.** When the same process runs the WebSocket tracker, it can answer "is this peer currently signaling?" cheaply at allocation time — only currently-announced peers can mint TURN credentials, eliminating freeloading.
3. **Cost control.** TURN bandwidth is the most expensive part of running a WebRTC app at scale. Keeping it gated by application logic (not just shared-secret credentials) means an attacker who steals the credential still can't relay through the server unless they're also actively in a signaling session.

The embedded server is opt-in (`AddRtcStunTurn(opts => { opts.Enabled = true; ... })`). Apps that don't need TURN run only the WebSocket tracker.

---

## 2. STUN — the binding service

STUN (RFC 5389) gives a peer behind a NAT its public reflexive address. Used during ICE to discover `srflx` candidates.

### Wire — Binding Request

```
0                   1                   2                   3
0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+---+---+-------------------------------+-------------------------------+
| 0 | 0 |     0x0001 (Binding Request)  |     Message Length            |
+---+---+-------------------------------+-------------------------------+
|                         0x2112A442 (Magic Cookie)                       |
+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
|                                                                       |
|                       96-bit Transaction ID                           |
|                                                                       |
+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
|                          Optional Attributes...                       |
+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
```

- **Method** (low 12 bits of message-type): `0x001` = Binding
- **Class** (bits 4 and 8 of message-type): 00 = Request, 10 = Indication, 11 = Error Response, 01 = Success Response

### Wire — Binding Success Response

The server reflects the source IP+port back as `XOR-MAPPED-ADDRESS` (attribute type `0x0020`):

```
+---+---+-------------------------------+-------------------------------+
| 0 | 1 |     0x0001 (Binding Success)  |     Message Length            |
+---+---+-------------------------------+-------------------------------+
|                         Magic Cookie                                  |
|                       Transaction ID                                  |
+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
|     0x0020 (XOR-MAPPED-ADDRESS)    |     0x0008 (length)              |
| Reserved | Family | XOR-Port (16 bits) | XOR-IP (32 bits, IPv4)       |
+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+---+
```

The `XOR-MAPPED-ADDRESS` attribute contains the address XORed with the magic cookie + transaction ID — the obfuscation prevents NAT64 / DNS-rewriting middleboxes from corrupting the address.

### IPv4 vs IPv6 family

`Family` byte: `0x01` = IPv4, `0x02` = IPv6. SpawnDev.RTC supports both; the server fills the family based on the listener address.

### When STUN is used

Browser and SipSorcery ICE agents send Binding Requests to the configured STUN servers as part of candidate gathering. The server's response gives the client its public reflexive address, which becomes a `srflx` ICE candidate in the SDP.

---

## 3. TURN — the relay service

TURN (RFC 5766 + RFC 8489 update) gives a peer a relay address on the server. Peers behind symmetric NATs that can't establish direct ICE pairs use TURN as a fallback. All traffic flows client → TURN server → other peer (and back).

### Allocation lifecycle

```
client                                    TURN server
  │                                            │
  │── Allocate Request (0x0003) ─────────────→ │
  │     USERNAME, MESSAGE-INTEGRITY, NONCE     │
  │     REQUESTED-TRANSPORT (UDP)              │
  │                                            │
  │ ←─── 401 Unauthorized + REALM + NONCE ─────│  (initial pass — server says
  │                                            │   "compute MESSAGE-INTEGRITY
  │                                            │    using realm + nonce")
  │                                            │
  │── Allocate Request (0x0003) ─────────────→ │
  │     USERNAME, MESSAGE-INTEGRITY (HMAC),    │
  │     NONCE, REQUESTED-TRANSPORT             │
  │                                            │
  │ ←─── Allocate Success Response ────────────│  (XOR-RELAYED-ADDRESS = relay
  │      XOR-RELAYED-ADDRESS (relay IP:port)   │   IP+port on TURN server,
  │      LIFETIME (default 600 sec)            │   advertised to other peers as
  │      XOR-MAPPED-ADDRESS (client public)    │   `relay` ICE candidate)
  │                                            │
  │── Refresh Request (0x0004) ──────────────→ │  (every LIFETIME / 2)
  │ ←─── Refresh Success ──────────────────────│
  │                                            │
  │── CreatePermission (0x0008) ─────────────→ │  (allow specific peer IP)
  │     XOR-PEER-ADDRESS (peer 1)              │
  │     XOR-PEER-ADDRESS (peer 2)              │
  │ ←─── CreatePermission Success ─────────────│
  │                                            │
  │ ── Send Indication (0x0016) ─────────────→ │  (data wrapper)
  │     XOR-PEER-ADDRESS, DATA                 │
  │                                            │── relayed UDP datagram ──→ peer
  │                                            │
  │                                            │← UDP from peer ──────────
  │ ←─── Data Indication (0x0017) ─────────────│  (data from peer)
  │      XOR-PEER-ADDRESS, DATA                │
  │                                            │
  │── Refresh Request (LIFETIME=0) ──────────→ │  (graceful close)
  │ ←─── Refresh Success ──────────────────────│
```

### Channel binding (data optimization)

After the first `Send Indication` per peer, the client can send a `ChannelBind Request` (0x0009) to assign a 16-bit channel number to that peer. Subsequent data uses the 4-byte channel-data frame instead of the ~40-byte Send Indication, halving overhead. SpawnDev.RTC supports channel binding via the SipSorcery fork.

---

## 4. Authentication models

### Long-term credentials (RFC 5389 §10.2.1)

A static `(username, realm, password)` tuple shared between client and server. `MESSAGE-INTEGRITY` is HMAC-SHA1 keyed on `MD5(username:realm:password)`.

```csharp
// SpawnDev.RTC.Server config (long-term):
new TurnServerConfig
{
    Username = "alice",
    Password = "alice-static-password",
    Realm    = "spawndev-rtc",
}
```

Pros: simple, no key rotation needed. Cons: leaked credential = unlimited TURN usage until rotated.

### Ephemeral credentials (RFC 8489 §9.2 — TURN REST API pattern)

Twilio / Cloudflare / coturn `--use-auth-secret` style. The application backend mints a time-bound `(username, password)` pair from a shared secret without involving the TURN server. The TURN server validates the pair by recomputing the HMAC.

**Username encoding:**

```
username = "<unix-timestamp-of-expiry>:<arbitrary-extra-data>"
         e.g., "1735862400:peer-id-abc123"
```

The expiry timestamp is the Unix time when the credential becomes invalid. When the TURN server gets an Allocate request, it parses the timestamp from the username; if past expiry, it returns 401. Otherwise it computes:

```
password = base64(HMAC-SHA1(shared_secret, username))
```

If the supplied password matches, the credential is accepted.

**Signing on the application side:**

```csharp
public static (string username, string password) Mint(
    string sharedSecret, string realm, string extra, TimeSpan lifetime)
{
    var expiry = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
    var username = $"{expiry}:{extra}";
    using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(sharedSecret));
    var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
    var password = Convert.ToBase64String(hash);
    return (username, password);
}
```

The shared secret never leaves the backend. The frontend gets `(username, password, ttl)` over an authenticated channel and uses them directly in `RTCIceServer`.

### Credential decision tree

| Use case | Recommended | Why |
|---|---|---|
| Internal-only deployment, fixed clients | Long-term | Simpler, no minting endpoint required |
| Public-facing, untrusted clients | Ephemeral + tracker-gated | Per-session bounded, can't be replayed indefinitely |
| Mixed-trust (some users authenticated) | Ephemeral, scoped username | Username carries application-level identity |

SpawnDev.RTC's `EphemeralTurnCredentials` class implements both the minting (`Mint`) and the server-side resolver (`ResolveLongTermKey`).

---

## 5. Tracker-gated TURN — SpawnDev extension

The novel piece. `SpawnDev.RTC.Server` can be configured so the TURN server **only** issues allocations to peers currently announced to the WebSocket tracker.

```csharp
// SpawnDev.RTC.Server config (tracker-gated ephemeral):
var trackerOptions = new TrackerServerOptions { /* ... */ };
var tracker = app.UseRtcSignaling("/announce", trackerOptions);

var turnConfig = new TurnServerConfig
{
    Realm = "spawndev-rtc",
    ResolveHmacKey = EphemeralTurnCredentials.TrackerGatedResolver(
        sharedSecret: Environment.GetEnvironmentVariable("RTC__StunTurn__EphemeralCredentialSharedSecret"),
        realm: "spawndev-rtc",
        tracker: tracker
    ),
};
```

`TrackerGatedResolver` wraps the standard ephemeral-credential resolver with a peer-presence check. When an Allocate Request arrives:

1. Parse `username` for the embedded peer-id (per the SpawnDev convention `username = "<expiry>:<peer-id>"`)
2. Call `tracker.IsPeerConnected(peer-id)` — true iff the peer-id is currently in any room
3. If false, return 401 (unauthorized)
4. If true, validate the HMAC normally

Effect: an attacker who steals an ephemeral credential pair can't use it unless they're also actively in a signaling session under the same peer-id. Since the WebSocket tracker is harder to abuse anonymously than a TURN server, this dramatically narrows the attack surface.

The check itself is O(1) (HashSet lookup) so it adds no measurable latency.

---

## 6. NAT port-range bounding

By default, TURN allocates relay sockets on OS-ephemeral UDP ports (typically 32768-60999 on Linux). Behind a NAT this range is **unforwardable** — the upstream router would need to forward every port in the ephemeral range, which most consumer routers can't.

The fork's `TurnServerConfig.RelayPortRangeStart` / `RelayPortRangeEnd` constrain allocations to a configurable range:

```csharp
new TurnServerConfig
{
    RelayPortRangeStart = 49200,
    RelayPortRangeEnd   = 49299,
    // ... 100 simultaneous TURN allocations supported, all in [49200..49299]
}
```

The hub.spawndev.com deployment sets `49200-49299` and the upstream router forwards UDP `192.168.1.113:49200-49299`. New allocations bind within the range; if the range is exhausted, new Allocate Requests get 486 (Allocation Quota Reached).

Equivalent to coturn's `--min-port` / `--max-port` and pion's `RelayAddressGenerator` port-range. Required for any consumer-NAT deployment.

---

## 7. Configuration reference

### Environment variables (recommended for production)

```bash
# Enable embedded STUN/TURN
RTC__StunTurn__Enabled=true

# Listen address & port (UDP)
RTC__StunTurn__ListenAddress=0.0.0.0
RTC__StunTurn__Port=3478

# Public IP advertised in XOR-RELAYED-ADDRESS (must match the NAT external IP)
RTC__StunTurn__RelayAddress=64.246.234.108

# Realm for MESSAGE-INTEGRITY
RTC__StunTurn__Realm=spawndev-rtc

# Long-term credential (ignored when EphemeralCredentialSharedSecret is set)
RTC__StunTurn__Username=turn-user
RTC__StunTurn__Password=turn-pass

# Ephemeral credentials (TURN REST API pattern)
RTC__StunTurn__EphemeralCredentialSharedSecret=<32-byte-base64>

# Tracker-gating (requires EphemeralCredentialSharedSecret to also be set)
RTC__StunTurn__TrackerGated=true

# Allocation lifetime in seconds (default 600)
RTC__StunTurn__DefaultLifetimeSeconds=600

# Relay port range for NAT-forwarding deployments
RTC__StunTurn__RelayPortRangeStart=49200
RTC__StunTurn__RelayPortRangeEnd=49299

# Origin allowlist for the SIGNALING WebSocket (separate from TURN)
RTC__AllowedOrigins=https://yourapp.com;https://*.yourapp.com
```

### Programmatic equivalent

```csharp
builder.Services.AddRtcStunTurn(opts =>
{
    opts.Enabled = true;
    opts.ListenAddress = IPAddress.Any;
    opts.Port = 3478;
    opts.RelayAddress = IPAddress.Parse("64.246.234.108");
    opts.Realm = "spawndev-rtc";
    opts.EphemeralCredentialSharedSecret = "<...>";
    opts.TrackerGated = true;
    opts.RelayPortRangeStart = 49200;
    opts.RelayPortRangeEnd = 49299;
});
```

### Network requirements

| Port | Protocol | Direction | Purpose |
|---|---|---|---|
| 80/443 | TCP | Inbound | Web server (signaling WebSocket upgrade, optional REST) |
| 3478 | UDP | Inbound | STUN binding + TURN allocation control |
| 3478 | TCP | Inbound | TURN-over-TCP (some restrictive firewalls) |
| 49200-49299 (or your range) | UDP | Inbound | TURN relay sockets |

Outbound: the server makes no outbound connections except whatever the application itself initiates.

### Healthcheck

`GET /health` returns:

```json
{
  "status": "ok",
  "rooms": 5,
  "peers": 23
}
```

Plus the TURN server's own internal stats are available via SipSorcery's `TurnServer` API surface (allocation count, traffic counters).

---

## Implementation map

| RFC concept | SpawnDev.RTC.Server type | Notes |
|---|---|---|
| STUN binding | SipSorcery `StunServer` | Inside `TurnServer` (shared port) |
| TURN allocation | SipSorcery `TurnServer` | Wrapped as ASP.NET Core `IHostedService` via `AddRtcStunTurn` |
| Long-term credential resolver | `TurnServerConfig.ResolveHmacKey = username => ResolveLongTermKey(secret, realm, username)` | `EphemeralTurnCredentials.ResolveLongTermKey` |
| Ephemeral credential resolver | `EphemeralTurnCredentials.ResolveEphemeralKey(secret, realm)` | RFC 8489 §9.2 |
| Tracker-gated resolver | `EphemeralTurnCredentials.TrackerGatedResolver(secret, realm, tracker)` | SpawnDev extension |
| Relay port range | `TurnServerConfig.RelayPortRangeStart/End` | SipSorcery fork addition |
| Origin allowlist for signaling WS | `TrackerServerOptions.AllowedOrigins` | Separate from TURN auth |

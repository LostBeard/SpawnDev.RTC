# Plan: Full Tracker Parity — HTTP + UDP + WebSocket

**Goal:** SpawnDev.RTC.Server supports every transport the JS `bittorrent-tracker` reference does — HTTP (BEP 3), UDP (BEP 15), and WebSocket (WebTorrent extension). Any BitTorrent client (qBittorrent, Transmission, Deluge, libtorrent-based, JS WebTorrent, our own SpawnDev.WebTorrent) can announce to `hub.spawndev.com` and discover peers across all transports.

**Last updated:** 2026-04-27
**Current state:** WebSocket tracker shipped + parity-clean against JS reference (1.0.7-rc.1, six scenarios A-F PASS). HTTP and UDP server-side support absent (verified 2026-04-27 by source-grep + live `curl https://hub.spawndev.com:44365/announce?info_hash=...` returning HTTP 400). Closing this gap is the focus of this plan.

---

## Why this matters

- **qBittorrent / Transmission / Deluge / libtorrent / rqbit** speak HTTP (BEP 3) and UDP (BEP 15) trackers, NOT WebSocket. They cannot currently announce to `hub.spawndev.com` at all.
- **JS WebTorrent in Node.js hybrid mode** can announce via WebSocket OR HTTP — currently we only accept the WebSocket path.
- **The reference (`webtorrent/bittorrent-tracker` npm package)** ships all three. Anyone matching the reference is expected to too.
- **Public-tracker convention:** tracker.openwebtorrent.com et al are WebSocket-only because they only serve browser-WebRTC peers. We aim broader — the hub already runs an embedded STUN/TURN server, so it's a unified "WebRTC + BitTorrent + STUN/TURN + HuggingFace proxy" host. Adding HTTP + UDP completes the tracker side of that vision.
- **The BitTorrent peer-wire is unchanged.** This plan only adds tracker transports — once peers know about each other, peer-to-peer connections still go via WebRTC (browsers) or TCP/uTP (mainline). A cross-transport "hybrid" peer (Node.js WebTorrent or our SpawnDev.WebTorrent.ServerApp seeder) bridges the two worlds.

## Scope

In:
- HTTP tracker `GET /announce` (BEP 3) + `GET /scrape` (BEP 48)
- HTTP compact peer encoding (BEP 23 IPv4, BEP 7 IPv6)
- UDP tracker (BEP 15) — connect/announce/scrape/error actions
- Shared `SignalingRoomInfo` so peers across all three transports are visible to each other
- Per-transport peer tagging (so a WebSocket browser peer doesn't get TCP IP:port forwards for clients that can't reach those, and vice versa — the JS reference makes the right choice for hybrid clients in its bittorrent-tracker-server.js)
- Wire-up in `SpawnDev.RTC.ServerApp`, `SpawnDev.WebTorrent.ServerApp`, and the `hub.spawndev.com` `spawndev_hub.service` env vars
- Unit tests + integration tests against qBittorrent and JS reference
- Documentation in `Research/` (both repos)

Out:
- Changes to the BitTorrent peer-wire transport (already covered by SpawnDev.WebTorrent + qBittorrent interop tests)
- DHT (already covered by SpawnDev.WebTorrent's DhtDiscovery)
- BEP 41 (UDP tracker URL extensions) — uncommon, can wait
- Tracker authentication / passkey URLs — separate feature

## Architecture decisions

### Where the code lives

`SpawnDev.RTC.Server` already owns `TrackerSignalingServer` (WebSocket). HTTP and UDP join it. The package goes from "WebSocket signaling server" to "full BitTorrent tracker server with WebSocket signaling extension."

New types:
- `SpawnDev.RTC.Server.HttpTrackerHandler` — ASP.NET Core endpoint handler for `GET /announce` + `GET /scrape`
- `SpawnDev.RTC.Server.UdpTrackerServer` — `IHostedService` hosting a UDP listener
- `SpawnDev.RTC.Server.TrackerCoordinator` — central per-room state owner, exposed to all three handlers

Keep WebSocket-only consumers (multiplayer games, agent swarms, voice chat) opt-out: HTTP/UDP enable via a separate `AddBitTorrentTrackerExtensions(opts => { ... })` call. Default keep WS-only.

### Shared room state

`SignalingRoomInfo` extends to track peers per transport:

```csharp
public sealed class SignalingRoomInfo
{
    public string RoomKey { get; }   // 20-byte info_hash on the wire

    // WebSocket peers (current — keyed by 20-byte peer_id)
    public ConcurrentDictionary<string, SignalingPeer> WebSocketPeers { get; }

    // HTTP/UDP peers (new — keyed by IP:port since these clients use BitTorrent peer_id
    // for handshake but the tracker stores them by IP:port for compact-peer responses)
    public ConcurrentDictionary<IPEndPoint, BtTrackerPeer> BitTorrentPeers { get; }

    public int SeederCount { get; }
    public int LeechCount { get; }
}

public sealed class BtTrackerPeer
{
    public IPEndPoint Endpoint { get; }       // wire IP+port
    public byte[] PeerId { get; }             // 20 bytes
    public bool IsSeeder { get; }             // left == 0
    public DateTimeOffset LastAnnounceAt { get; }
    public TrackerTransport Transport { get; } // Http or Udp
}
```

A peer can be BOTH (a hybrid Node.js WebTorrent or SpawnDev.WebTorrent ServerApp instance), and the tracker tracks them separately per transport because the peer-id semantics differ (BitTorrent peer-id vs WebRTC peer-id are NOT the same — though clients that bridge use the same value).

### Cross-transport discovery

When peer A (HTTP) announces, the tracker returns a peer list. Per JS bittorrent-tracker:
- HTTP/UDP responses carry `peers` (compact 6-byte) + `peers6` (compact 18-byte) lists of OTHER HTTP/UDP peers in the same room
- HTTP/UDP responses do NOT include WebSocket-only peers (those clients can't reach a TCP IP:port; Brower-only peers are unreachable from qBittorrent)
- WebSocket responses still carry no `peers` field (per current reference) — peers exchange via offer/answer relay only. WebSocket peers can't reach TCP-only qBittorrent directly, and vice versa.
- A **hybrid peer** (a Node.js webtorrent-hybrid or our SpawnDev.WebTorrent.ServerApp) shows up in BOTH transports because it announces twice — once via WebSocket (with WebRTC offers) and once via HTTP/UDP (with TCP IP:port). Such a peer becomes the bridge — browser peers find it via offer relay, qBittorrent finds it via compact peer list. Both leech from it; or it leeches from both and re-seeds.

### Port story

| Service | Port | Protocol |
|---|---|---|
| HTTP tracker | Same as the WebSocket tracker (44365 currently for hub) | HTTPS, ASP.NET Core handles the multiplexing |
| UDP tracker | New, configurable. Default proposal: 6969 (matches JS bittorrent-tracker default) | UDP |
| Existing STUN/TURN | 3478 | UDP |
| Existing relay range | 49200-49299 | UDP |

So the hub deploy adds one new env var (`RTC__BtTracker__UdpPort=6969`) and one new firewall rule (UDP 6969 inbound).

### Bencode encoder

Reuse `SpawnDev.WebTorrent.Bencode.BencodeEncoder` for HTTP responses. Currently in the WebTorrent package — we either:

(a) Add a `ProjectReference` from RTC.Server to SpawnDev.WebTorrent (creates a coupling RTC.Server → WebTorrent that doesn't exist today)
(b) Move Bencode to a shared sub-package `SpawnDev.WebTorrent.Bencode` (small, neutral primitive — natural fit for a primitives package)
(c) Inline a minimal encoder/decoder in SpawnDev.RTC.Server (~100 lines)

**Decision: (c) for the first cut** — keep the SpawnDev.RTC.Server package boundary clean (it's the "tracker" library, doesn't depend on WebTorrent semantics). The encoder is 100 lines max; not worth a new package or a dep. We can promote to a shared package if a third consumer needs it.

---

## Phases

### Phase 1: Research + Spec

- [ ] 1.1 Read `webtorrent/bittorrent-tracker` source: `lib/server/index.js`, `lib/server/parse-http.js`, `lib/server/parse-udp.js`, `lib/server/swarm.js`. Document exact wire-format details and quirks.
- [ ] 1.2 Read BEP 3 (HTTP tracker), BEP 7 (IPv6), BEP 15 (UDP tracker), BEP 23 (compact peer), BEP 41 (UDP extensions, optional), BEP 48 (HTTP scrape). Note any ambiguities and how the JS reference resolves them.
- [ ] 1.3 Stand up local `bittorrent-tracker` JS server in a Node.js script, capture its HTTP and UDP wire bytes against a real qBittorrent client and record them as fixture data. (`tracker-debug/capture-http-udp.mjs` — new file.)

### Phase 2: Documentation

- [ ] 2.1 Add `Research/04-bep-tracker-server.md` to SpawnDev.WebTorrent — full HTTP server protocol from a SERVER perspective (mirror of existing client-side `04-tracker-protocols.md`).
- [ ] 2.2 Add `Research/04-bep-tracker-server-udp.md` — same for UDP.
- [ ] 2.3 Update `SpawnDev.RTC/Research/01-tracker-signaling.md` to reference the BEP-tracker docs and clarify that the SpawnDev.RTC.Server now serves all three transports.
- [ ] 2.4 Add `SpawnDev.RTC/Research/07-bep-tracker.md` — RTC-side cover of how the BitTorrent tracker pieces fit into the package.

### Phase 3: Code — HTTP tracker

- [ ] 3.1 `BencodeWriter.cs` in `SpawnDev.RTC.Server` — minimal encoder for byte-string + integer + list + dict. Header comment cites BEP 3.
- [ ] 3.2 `BtTrackerPeer.cs` — DTO for HTTP/UDP-tracker peer state (Endpoint, PeerId, IsSeeder, LastAnnounceAt, Transport).
- [ ] 3.3 `TrackerTransport.cs` — enum `WebSocket | Http | Udp`.
- [ ] 3.4 `SignalingRoomInfo.cs` — extend with `BitTorrentPeers` dict.
- [ ] 3.5 `HttpTrackerHandler.cs` — handles `GET /announce`:
  - Parse query string (info_hash + peer_id raw bytes via the `%xx` decode, NOT URI.UnescapeDataString — urlencoded binary).
  - Extract: info_hash, peer_id, port, uploaded, downloaded, left, event, compact, numwant, ip (override).
  - Mutate `SignalingRoomInfo.BitTorrentPeers` (add/update on `started`/no event, remove on `stopped`, mark seeder on `completed`/`left=0`).
  - Build bencoded response: `{interval, complete, incomplete, peers (compact bytes), peers6 (compact bytes)}`.
  - Failure-path response with `failure reason` for missing/invalid params.
- [ ] 3.6 `HttpTrackerHandler.HandleScrape` — handles `GET /scrape`:
  - Parse `info_hash` (one or many query-string values).
  - Build bencoded response: `{files: { <raw 20 bytes>: {complete, incomplete, downloaded} }}`.
- [ ] 3.7 `SignalingAppBuilderExtensions.UseBtTrackerHttp(string path = "/announce")` — wires the HTTP handler at the same path the WebSocket tracker uses (the upgrade-vs-not branches at the request boundary).
- [ ] 3.8 ASP.NET Core integration: when the request is a WebSocket upgrade, route to `TrackerSignalingServer.HandleWebSocketAsync`. When it's a regular GET, route to `HttpTrackerHandler.HandleAnnounce` (or `HandleScrape` if path is `/scrape`). Single endpoint, two execution paths.
- [ ] 3.9 Unit tests for `BencodeWriter` round-tripping against `BencodeDecoder`.
- [ ] 3.10 Unit tests for `HttpTrackerHandler` against captured fixture bytes.

### Phase 4: Code — UDP tracker

- [ ] 4.1 `UdpTrackerServer.cs` (`BackgroundService`) — listens on configurable UDP port, handles connect/announce/scrape/error.
- [ ] 4.2 Connection-id issuance + 1-min expiry tracking. Per BEP 15: `connection_id` = HMAC-SHA1 of (client IP, client port, secret, timestamp); recipient validates by recomputing.
- [ ] 4.3 Transaction-id matching on response.
- [ ] 4.4 Big-endian framing helpers (`ReadInt32BE`, `WriteInt64BE`, etc).
- [ ] 4.5 `UseBtTrackerUdp(int port = 6969)` extension — registers the hosted service.
- [ ] 4.6 Unit tests against captured BEP 15 fixture frames.

### Phase 5: Wire-up

- [ ] 5.1 `SpawnDev.RTC.ServerApp/Program.cs` — opt-in switches `RTC__BtTracker__Http__Enabled` and `RTC__BtTracker__Udp__Enabled` + port config. Wire `app.UseRtcSignaling()` to also run the HTTP handler when enabled.
- [ ] 5.2 `SpawnDev.WebTorrent.ServerApp/Program.cs` — same switches.
- [ ] 5.3 `SpawnDev.WebTorrent/deploy/spawndev_hub/spawndev_hub.service` — add the env vars, set `RTC__BtTracker__Http__Enabled=true` and `RTC__BtTracker__Udp__Port=6969`. Document the new firewall rule (UDP 6969 inbound).
- [ ] 5.4 `SpawnDev.RTC.Server.csproj` version bump (`1.0.8-rc.1` proposed).
- [ ] 5.5 `SpawnDev.WebTorrent.ServerApp.csproj` PackageReference bump to consume the new RTC.Server.
- [ ] 5.6 CHANGELOG.md entries for both packages.

### Phase 6: Integration tests

- [ ] 6.1 `interop_test/qbittorrent_tracker_announce.cs` — qBittorrent announces to a fresh local SpawnDev.RTC.ServerApp via HTTP, the tracker `/stats` endpoint shows the peer, qBittorrent receives the empty peer list from an empty room.
- [ ] 6.2 `interop_test/qbittorrent_swarm_via_tracker.cs` — Two qBittorrent instances, both announcing to local SpawnDev.RTC.ServerApp via HTTP, they discover each other, transfer 1 MiB via TCP, SHA-256 verify.
- [ ] 6.3 `interop_test/spawndev_qbittorrent_via_tracker.cs` — SpawnDev.WebTorrent C# announces via HTTP to local hub, qBittorrent announces via HTTP to local hub, SpawnDev.WebTorrent discovers qBittorrent via compact peer list, transfers 1 MiB.
- [ ] 6.4 `interop_test/qbittorrent_swarm_via_hub.cs` — same as 6.2 but against the live `hub.spawndev.com` (after deploy).
- [ ] 6.5 `interop_test/js_webtorrent_hybrid_swarm.cs` — Node.js webtorrent-hybrid (announces via both WebSocket AND HTTP), C# leech browser-style (WebSocket), C# leech mainline-style (HTTP), all via local SpawnDev.RTC.ServerApp. Demonstrates the hybrid bridge.
- [ ] 6.6 UDP-tracker version of 6.1 and 6.2 — qBittorrent over UDP.

### Phase 7: Live deploy + community announcement

- [ ] 7.1 Deploy fresh hub bits with HTTP + UDP enabled.
- [ ] 7.2 Re-run `verify-tracker-parity.mjs` to confirm WebSocket parity unchanged.
- [ ] 7.3 Run the qBittorrent-via-hub integration test.
- [ ] 7.4 Update `SpawnDev.WebTorrent` README + `SpawnDev.RTC` README — `hub.spawndev.com` now accepts all three tracker transports.
- [ ] 7.5 Update `Research/00-README.md` indexes in both repos.

---

## Test matrix

| Client | Transport | Direction | Status target |
|---|---|---|---|
| qBittorrent 5.1+ | HTTP | announce → discover → leech | PASS |
| qBittorrent 5.1+ | UDP | announce → discover → leech | PASS |
| Transmission | HTTP | announce → discover → leech | PASS (libtorrent-based, behaves the same as qBittorrent) |
| JS webtorrent (Node, WebSocket) | WS | already covered, no regression | PASS |
| JS webtorrent (Node, hybrid) | WS + HTTP | both transports announce | PASS (hybrid bridge) |
| SpawnDev.WebTorrent C# (HTTP path) | HTTP | announce → discover → leech | PASS |
| SpawnDev.WebTorrent C# (UDP path) | UDP | announce → discover → leech | PASS |
| SpawnDev.WebTorrent C# (WS path) | WS | already covered, no regression | PASS |
| Browser SpawnDev.WebTorrent (WS) | WS | already covered, no regression | PASS |
| libtorrent direct UDP | UDP | wire-format-correct | PASS (unit-test fixture, no real client needed for proof) |

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| HTTP request handler conflicts with the WebSocket upgrade middleware | Branch on `context.WebSockets.IsWebSocketRequest` at the endpoint root — same path, two paths through the dispatcher |
| URL-encoded binary fields (info_hash, peer_id) not parsed correctly | Implement `UrlDecodeBytes` matching the same byte handling as `HttpTracker.UrlEncodeBytes`; unit-test against fixture URLs |
| Bencode encoding bugs (key ordering, integer vs string distinction) | Reuse the exact patterns from `BencodeEncoder.cs` (already-tested in production for .torrent files); unit-test round-trip against `BencodeDecoder` |
| UDP port conflicts with STUN/TURN on 3478 | Default UDP tracker to 6969; document the port choice |
| Connection-id replay attacks on UDP | HMAC-SHA1 with rotating secret + timestamp + client IP/port (per BEP 15 §6 reference) |
| Peer-list flooding (DoS via many announces) | Per-IP announce-rate limiting, room peer cap (configurable, default 200) — same shape as the existing `MaxMessageBytes` cap on WebSocket |
| Cross-transport peer pollution (a browser peer's WebRTC offer ending up in a TCP peer list) | `BtTrackerPeer` and `SignalingPeer` are separate types; HTTP/UDP responses only emit `BitTorrentPeers`, never `WebSocketPeers`; same direction for WS responses |

## Performance considerations

- HTTP and UDP handlers must be allocation-light. The hot path is `parse → look up room → format response`. Use `ArrayPool<byte>` for response buffers, avoid LINQ in the hot path.
- UDP listener uses a single `UdpClient` with async receive loop. No per-request thread.
- `BitTorrentPeers` uses `ConcurrentDictionary<IPEndPoint, BtTrackerPeer>` for O(1) add/update/remove.
- Compact peer encoding: pack 6 (IPv4) or 18 (IPv6) bytes per peer into a stack `Span<byte>` then to the bencoded response. No per-peer allocation.

## Compatibility note: what hybrid peers unlock

Once HTTP + UDP land, the hub can host **truly mixed swarms**:

- A torrent for a 1 GiB ML model on `hub.spawndev.com`
- The hub's own SpawnDev.WebTorrent.ServerApp runs as a hybrid peer (announces via all three transports, supports both WebRTC and TCP)
- Browser users (SpawnDev.BlazorJS app) leech via WebRTC through WebSocket signaling
- qBittorrent users on the public internet leech via TCP through HTTP tracker
- Both groups receive the same bytes from the hybrid peer
- Browser↔qBittorrent direct peer connections aren't possible (TCP-only mainline can't reach WebRTC-only browsers), but everyone leeches from the hybrid peer in parallel

This is exactly the "WebTorrent for everyone" promise the JS WebTorrent ecosystem already realizes. SpawnDev's contribution is making the same available in C# / .NET.

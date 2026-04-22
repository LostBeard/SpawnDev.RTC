# Plan: Tracker Signaling Migration (WebTorrent Tracker Client + Server → SpawnDev.RTC)

**Owner:** Captain (TJ)
**Lead:** Riker (team-lead editor on RTC + WebTorrent + MultiMedia)
**Status:** Research / design. No code yet.
**Date drafted:** 2026-04-17
**Precondition:** Geordi's ILGPU.P2P bump against RTC 1.0.1-rc.1 / WebTorrent 3.0.1-rc.1 is green (expected 162/1/6 → 163/0/6). Work does not start until that lands.

---

## TL;DR

Move the WebTorrent tracker **client** and the WebTorrent tracker **server** out of `SpawnDev.WebTorrent` / `SpawnDev.WebTorrent.Server` and into `SpawnDev.RTC` / a new `SpawnDev.RTC.Server` project. The tracker protocol is already a generic WebRTC signaling channel - we are admitting that on the tin. WebTorrent becomes a thin adapter that maps torrent-specific announce fields to the generic signaler.

This lets any SpawnDev.RTC consumer - multiplayer games, collaboration tools, voice chat, distributed compute, agent swarms - ship with full serverless WebRTC signaling and **without taking a dependency on WebTorrent**. It also collapses two parallel tracker client implementations in this repo (`RTCTrackerClient.cs` minimal + `WebSocketTracker.cs` full port) down to one source of truth.

The strategic win is network-effect adoption: every app that runs a signaling server for its own needs adds capacity to the public WebTorrent tracker network as a side effect. We turn every SpawnDev.RTC consumer into a potential decentralized infrastructure operator.

---

## Goal

Ship a self-contained WebRTC signaling stack in SpawnDev.RTC that:

1. Speaks the WebTorrent tracker wire protocol exactly (20-byte info_hash as room key, offers/answers/ice_candidates arrays, `action:"announce"` / `interval` / etc).
2. Works against any public WebTorrent tracker (`wss://tracker.openwebtorrent.com`, etc.) without any WebTorrent code in the consumer.
3. Ships a production-ready signaling server (`SpawnDev.RTC.Server`) that any consumer can deploy in minutes, and that any WebTorrent client can also use as a public tracker.
4. Keeps WebTorrent's full functionality intact via a thin adapter over the generic signaler.

## Success criteria

- A SpawnDev.RTC consumer with **zero** WebTorrent references can:
  - Announce to `wss://tracker.openwebtorrent.com` using an arbitrary 20-byte room key
  - Receive offers from other peers in the same "room"
  - Produce answers + ICE candidates via the tracker
  - Establish RTCPeerConnection data channels with those peers
  - Run on both browser (Blazor WASM) and desktop (.NET)
- WebTorrent's 374-test suite stays green after the migration. No regressions in piece download, wire extensions, DHT, web seeds, streaming.
- `SpawnDev.RTC.Server` can be deployed as:
  - A self-contained single-file exe (< ~50 MB) with zero config for the "just run it" case
  - A Docker image
  - Hosts thousands of concurrent signaling connections on modest hardware (< 50 MB RAM baseline)
- A WebTorrent client pointed at a `SpawnDev.RTC.Server` instance downloads a torrent end-to-end successfully. (The server is WebTorrent-wire-compatible even though it lives in a non-torrent package.)

---

## Strategic framing: the tracker protocol is generic signaling in a torrent trenchcoat

The WebTorrent tracker protocol is, under the hood, a room-based WebRTC signaling relay:

- The client connects via WebSocket to a well-known URL (`wss://.../announce`).
- It announces itself to a "room" identified by a 20-byte key (ostensibly a BitTorrent v1 info_hash, but the server never validates what it points at - it is just a key).
- It sends a batch of `offer` SDPs along with the announce.
- The server routes offers to other peers in the same room, returns incoming offers and answers, and relays ICE candidates.
- Consumers establish peer-to-peer WebRTC data channels and from there the tracker is out of the loop.

Strip the word "torrent" and every piece of that is "generic room-based WebRTC signaling." That is exactly what every multiplayer game, every collaborative editor, every agent swarm, every voice-chat app needs. The fact that the wire format happens to be the WebTorrent tracker protocol is a **feature**, not a limitation - it means SpawnDev.RTC consumers get interop with the public WebTorrent tracker fleet for free.

**This plan admits that architecturally.** Tracker client + server become SpawnDev.RTC citizens. WebTorrent becomes a consumer that pipes in torrent-flavored announce fields.

---

## Current state (verified 2026-04-17 before drafting this plan)

### Tracker client code lives in two places today

**Full implementation** - `D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent\WebSocketTracker.cs`
- "Direct 1:1 port of bittorrent-tracker/lib/client/websocket-tracker.js"
- namespace `SpawnDev.WebTorrent`, class `WebSocketTracker : IAsyncDisposable`
- Shared socket pool (static dictionary keyed by `announceUrl + peerId` hex). Multiple swarms can share one WS connection to the same tracker.
- Constants: `ReconnectMinimum = 10_000`, `MaxOffers = 10`, `OfferTimeout = 50_000`, `DefaultAnnounceInterval = 120_000`.
- Factory: `public static WebSocketTracker GetOrCreate(string announceUrl, byte[] peerId, Func<bool, SimplePeer> createPeerFunc)` - note the `SimplePeer` factory coupling. This is what we have to generalize.
- `_pendingOffers` recently upgraded to `ConcurrentDictionary` (race between offer-gen lambda and timer) - fix is in WebTorrent 3.0.1-rc.1.
- Handles reconnect, offer-timeout cleanup, announce interval, `action:"announce"` vs server-initiated offer relay.

**Minimal parallel client** - `D:\users\tj\Projects\SpawnDev.RTC\SpawnDev.RTC\SpawnDev.RTC\RTCTrackerClient.cs`
- namespace `SpawnDev.RTC`, class `RTCTrackerClient : IDisposable`
- Built to unblock RTC consumers that just want signaling without pulling in WebTorrent.
- Uses `ClientWebSocket`, 20-byte peer ID, info hash, `OnPeerConnection` event.
- **This is the duplicate we are consolidating.** It is not bad code; it is just a second implementation of a protocol that only needs one. The migration kills it.

### Tracker server code lives in one place today

**Server** - `D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent.Server\TorrentTracker.cs`
- namespace `SpawnDev.WebTorrent.Server`, class `TorrentTracker`
- Docstring: "WebSocket-based BitTorrent tracker (BEP 15 over WebSocket). Manages peer swarms and facilitates WebRTC signaling for browser peers."
- `ConcurrentDictionary<string, TorrentSwarmInfo> _swarms`
- ASP.NET Core dependencies: `Microsoft.AspNetCore.Builder` / `Http` / `Routing`.
- Entry point: `public async Task HandleWebSocket(HttpContext context)`.
- Mounted by the consuming app via `app.Map("/announce", tracker.HandleWebSocket)`.
- Also contains supporting types: `TrackerOptions`, `TrackerPeer`, `TorrentSwarmInfo`.

**No `SpawnDev.RTC.Server` project exists yet.** The new solution needs one created.

### Consumers that will be affected

- **SpawnDev.WebTorrent** - `WebSocketTracker.cs` is used by the discovery layer. Migrates to a thin adapter in the same namespace.
- **SpawnDev.WebTorrent.Server** - `TorrentTracker.cs` moves out. What remains is the non-tracker server pieces (web seed server, HuggingFace proxy, server app assembly).
- **SpawnDev.WebTorrent.ServerApp** - Production deployment at `hub.spawndev.com`. Wires the tracker into ASP.NET Core. Needs one-line update to reference the new server package.
- **SpawnDev.ILGPU.P2P** - Consumes WebTorrent for distributed compute signaling. Benefits passively: the migration does not change its API surface but **does** let future P2P work switch to RTC signaling directly without going through WebTorrent.
- **Any future RTC consumer** - Suddenly has serverless signaling without any torrent dependency. This is the whole point.

### Why the duplication exists today

Not negligence - evolution. `RTCTrackerClient.cs` was added when RTC needed signaling and WebTorrent's full `WebSocketTracker` had too much torrent-specific coupling to import cleanly. The right move is to decouple `WebSocketTracker` from torrent concerns, not to maintain two parallel clients. This plan does that.

---

## Target state

### Project layout

```
SpawnDev.RTC/
    SpawnDev.RTC/                            # Existing library
        Signaling/                           # NEW folder
            ISignalingClient.cs              # Generic interface (replaces RTCTrackerClient concept)
            TrackerSignalingClient.cs        # Moved + generalized from WebSocketTracker.cs
            SignalingPeerEventArgs.cs
            RoomKey.cs                       # NEW helper (20-byte key + string/hash ctors)
            SignalingOffer.cs, SignalingAnswer.cs, SignalingIceCandidate.cs  # DTOs
        RTCTrackerClient.cs                  # DELETED (minimal impl superseded)
    SpawnDev.RTC.Server/                     # NEW project (NuGet package)
        TrackerSignalingServer.cs            # Moved + generalized from TorrentTracker.cs
        TrackerServerOptions.cs
        SignalingPeer.cs
        SignalingRoomInfo.cs
        Extensions/
            SignalingAppBuilderExtensions.cs # app.UseRtcSignaling("/announce", options)
    SpawnDev.RTC.ServerApp/                  # NEW project (self-contained exe) - optional phase
        Program.cs                           # Minimal host: Kestrel + RTC signaling + nothing else
        appsettings.json                     # Defaults for zero-config startup
    SpawnDev.RTC.Demo/                       # Existing
    ...
```

### WebTorrent becomes a consumer

```
SpawnDev.WebTorrent/
    SpawnDev.WebTorrent/
        Discovery/
            WebSocketTracker.cs              # NOW a thin adapter over TrackerSignalingClient
                                             # Maps torrent-specific announce fields (uploaded,
                                             # downloaded, left, event) to the generic signaler's
                                             # opaque payload. Produces SimplePeer instances.
    SpawnDev.WebTorrent.Server/
        WebSeedServer.cs                     # Stays
        HuggingFaceProxy.cs                  # Stays
        TorrentTracker.cs                    # DELETED (replaced by SpawnDev.RTC.Server)
```

### API design: ownership split

**SpawnDev.RTC owns:**
- WebSocket framing and reconnection
- Shared connection pool (one WS per `announceUrl + peerId`, many rooms multiplexed)
- `announce` / `offer` / `answer` / `ice_candidate` message shapes on the wire
- Room registration, peer roster, offer relay, answer relay, ICE relay
- The 20-byte room key as the primary addressing unit
- Timeout and cleanup policies (announce interval, offer timeout, reconnect delay)
- The public `ISignalingClient` interface and its DTOs
- `RoomKey` helper type

**SpawnDev.RTC.Server owns:**
- ASP.NET Core endpoint mapping (`app.UseRtcSignaling(...)`)
- Server-side room state, peer bookkeeping, rate limits
- Options (max peers per room, max rooms, announce interval, per-IP limits)
- Health check endpoint
- Metrics / stats surface

**SpawnDev.WebTorrent owns (via the adapter):**
- Torrent-specific announce fields (`uploaded`, `downloaded`, `left`, `event`, `numwant`, `compact`)
- `SimplePeer` production from signaling peer events
- Peer ID conventions specific to the BitTorrent protocol
- The choice of which trackers to announce to for a given torrent

**The adapter contract is one-way.** RTC has no idea WebTorrent exists. WebTorrent knows about RTC and plugs into `ISignalingClient`. This keeps RTC free of torrent debt and makes it trivially consumable by non-torrent apps.

### `ISignalingClient` sketch (interface only - no impl in this plan)

Conceptually the generic signaling client needs roughly this surface. Exact names pending Phase 1 design:

- Connect / disconnect lifecycle.
- Join a room by `RoomKey`. One client can be in many rooms simultaneously.
- Publish N offer SDPs on behalf of the local peer.
- Receive remote offers, publish answers, exchange ICE candidates.
- Events: `OnRemoteOffer`, `OnRemoteAnswer`, `OnRemoteIceCandidate`, `OnAnnounceAck`, `OnDisconnect`.
- Opaque payload passthrough on every announce so consumers like WebTorrent can attach protocol-specific metadata without RTC needing to know its shape.

### `RoomKey` helper

The 20-byte room key is the wire format because it matches WebTorrent info_hash and keeps us compatible with the public tracker fleet. We do not change the wire. But most non-torrent consumers will never want to hold a 20-byte array - they have a human-readable room name.

Provide ergonomic constructors:

- `RoomKey.FromBytes(byte[] key)` - raw (for torrent consumers who already have an info_hash).
- `RoomKey.FromString(string roomName)` - hashes `roomName` to 20 bytes (SHA-1 of UTF-8 is the pragmatic choice; collisions are not a security concern in signaling - rooms are cheap and operators can isolate trust boundaries).
- `RoomKey.FromGuid(Guid)` - convenience for app-generated room IDs.
- `RoomKey.Random()` - new room on the fly.
- Implements equality, `ToString()` as hex, round-trip with `TryParse`.

**Critical compatibility property:** any 20-byte key works. A WebTorrent client torrenting `abc123...` and a multiplayer game with `RoomKey.FromString("my-lobby-42")` are both valid rooms on the same tracker server. Operators do not care about the semantics - they care about the byte length.

---

## Phased migration

Each phase is independently shippable. Between phases, both WebTorrent and RTC stay green.

### Phase 0: Freeze and branch - 0.5 day

- [ ] Announce in DevComms so Data / Geordi / Tuvok know edits to RTC + WebTorrent are inbound.
- [ ] Confirm Geordi's ILGPU.P2P verification is green on RTC 1.0.1-rc.1 / WebTorrent 3.0.1-rc.1. Do not start migration until that is banked.
- [ ] Branch both repos. Migrate behind the branch; land as coordinated commits.

### Phase 1: Generic signaling client in SpawnDev.RTC - ~1 day

- [ ] Define `ISignalingClient` interface and DTOs (`SignalingOffer`, `SignalingAnswer`, `SignalingIceCandidate`, `SignalingPeerEventArgs`).
- [ ] Define `RoomKey` helper with all ctors listed above + tests.
- [ ] Port `WebSocketTracker.cs` → `Signaling/TrackerSignalingClient.cs` in RTC:
  - Remove `SimplePeer` coupling. The peer factory becomes pluggable (adapter supplies it; RTC does not know about SimplePeer).
  - Remove BitTorrent announce fields from the public surface; preserve them as pass-through in an opaque extension payload.
  - Preserve the shared socket pool - same pool semantics, pool key becomes `announceUrl + peerId` (unchanged).
  - Preserve all timing constants with sensible default values exposed via options.
  - Preserve the `_pendingOffers` ConcurrentDictionary fix (already in WebTorrent 3.0.1-rc.1).
- [ ] Unit tests in `SpawnDev.RTC.Demo.Shared` (so they run on browser + desktop):
  - Connect to public tracker.
  - Join room with a random `RoomKey`.
  - Two clients in same room exchange offer → answer → ICE → data channel open.
  - Verify no WebTorrent types are referenced anywhere in the test.
- [ ] Delete `RTCTrackerClient.cs` once the new client passes the same scenarios. (The minimal parallel client is now redundant.)

### Phase 2: Generic signaling server (`SpawnDev.RTC.Server`) - ~1 day

- [ ] Create `SpawnDev.RTC.Server` project. NuGet package, depends on `SpawnDev.RTC` and `Microsoft.AspNetCore.*`.
- [ ] Port `TorrentTracker.cs` → `TrackerSignalingServer.cs`:
  - Rename types: `TorrentTracker` → `TrackerSignalingServer`, `TorrentSwarmInfo` → `SignalingRoomInfo`, `TrackerPeer` → `SignalingPeer`, `TrackerOptions` → `TrackerServerOptions`.
  - The wire format stays bit-identical. Only the C# names change.
  - Preserve `ConcurrentDictionary<string, SignalingRoomInfo> _rooms` behavior.
  - Keep health check / metrics endpoints; rename if they leaked torrent terminology.
- [ ] Provide `app.UseRtcSignaling("/announce", options)` extension method so consumers wire it in one line.
- [ ] Unit tests in `SpawnDev.RTC.Demo.Shared`:
  - Start server in-process, have two clients meet via loopback.
  - Test room isolation (peers in room A do not see offers from room B).
  - Test reconnect, dropped peers, announce interval behavior.
  - Test peer count / room count limits.

### Phase 3: WebTorrent adapter - ~1 day

- [ ] In SpawnDev.WebTorrent, reduce `WebSocketTracker.cs` to a thin adapter (file stays, implementation shrinks):
  - Constructs an internal `ISignalingClient` from SpawnDev.RTC.
  - Maps torrent announce fields (`uploaded` / `downloaded` / `left` / `event` / `numwant`) into the opaque signaling payload going out; reverse on incoming.
  - Keeps the existing public API (`GetOrCreate(announceUrl, peerId, createPeerFunc)`, same events). **Source-compatible with current WebTorrent consumers.**
  - Produces `SimplePeer` instances from signaling peer events via the caller-supplied factory (unchanged contract).
- [ ] Regression: full WebTorrent Playwright + NUnit suite (374 tests) must stay green. No behavior drift.
- [ ] Delete `TorrentTracker.cs` from `SpawnDev.WebTorrent.Server`. `SpawnDev.WebTorrent.ServerApp` updates to consume `SpawnDev.RTC.Server` + `app.UseRtcSignaling("/announce", ...)` instead.

### Phase 4: SpawnDev.RTC.ServerApp (optional, high leverage) - ~0.5 day

- [ ] New project: `SpawnDev.RTC.ServerApp`. Minimal self-contained console host.
- [ ] Zero-config startup (listens on a default port, sensible defaults, ready to accept peers).
- [ ] Env-var and `appsettings.json` overrides for port, limits, bind address, TLS cert path.
- [ ] Publish single-file, self-contained exes for win-x64 / linux-x64 / osx-arm64 + linux-arm64.
- [ ] Dockerfile and published image (`spawndev/rtc-signaling`).
- [ ] Target footprint: baseline RAM under 50 MB, thousands of concurrent WebSocket connections on commodity hardware. Measured, not assumed.
- [ ] Include a `--help` line that advertises "WebTorrent-compatible" up front - operators who pattern-match on "tracker" should land on this.

### Phase 5: Deployment and soak - ~0.5 day

- [ ] Swap `hub.spawndev.com` from `SpawnDev.WebTorrent.ServerApp` to `SpawnDev.RTC.ServerApp` (or equivalent host that consumes the new server package). **Blue-green; keep the old deployment warm for rollback.**
- [ ] Soak: 24 hours of real traffic. Monitor peer count, memory, reconnect rate, announce latency.
- [ ] Verify WebTorrent clients in the wild still connect and swarm successfully - this is the compatibility claim under real load.
- [ ] Cut official releases: SpawnDev.RTC 1.1.0, SpawnDev.RTC.Server 1.0.0, SpawnDev.WebTorrent 3.1.0.

Total: ~4 days of focused work across two repos, plus a day of soak. Realistic calendar: a week.

---

## Adoption and deploy story - first-class, not a footnote

The technical migration is the easy part. **Adoption is the real deliverable.** This section exists because if we do the refactor perfectly but nobody runs a tracker, we have accomplished a code reorganization. The goal is a network.

### The pitch flip

Old pitch (implicit today): "help the WebTorrent ecosystem by running a tracker server." Dead on arrival. Operators need a selfish reason.

New pitch: "**run a WebRTC signaling server for your own app** - for your multiplayer game, your collaborative editor, your agent swarm, your voice chat. It is self-contained, zero-config, and happens to be WebTorrent-compatible so it also strengthens the public decentralized network as a side effect."

The WebTorrent compatibility is framed as a **bonus**, not the purpose. Operators running a signaling server for their own app get public-tracker-fleet membership for free. That is the flywheel.

### Deploy story must be trivial

Friction kills flywheels. The server must deploy in three commands or fewer for the common path:

```
# Docker
docker run -p 80:80 spawndev/rtc-signaling

# Single binary
./SpawnDev.RTC.ServerApp

# Systemd / Windows service
# (we ship example unit files)
```

Zero config for the "I just want signaling" case. `appsettings.json` + env vars for the "I want to customize" case. TLS via reverse proxy (nginx / Caddy / Traefik) documented with working examples. **Fronting with Cloudflare should work out of the box** - documented and tested, not just claimed.

### Independence angle (own it, do not hide it)

Not Google. Not Amazon. Not Microsoft. Every instance of `SpawnDev.RTC.Server` running in the wild is infrastructure that no megacorp controls. That matters to a specific audience - indie game devs, federated/self-hosted tool builders, research groups, sovereignty-minded operators, anyone who has watched Google/Amazon/Microsoft pull platform rugs. Name it in the README. TJ's existing stance on Big Tech (see `project_article_39trillion_2026_04_09.md`) aligns perfectly - this is a concrete alternative, not a complaint.

### Documentation targets

- `SpawnDev.RTC/Docs/signaling-overview.md` - what signaling is, why you want it, pointer to client usage.
- `SpawnDev.RTC.Server/Docs/run-a-tracker.md` - the deploy doc. Docker, bare metal, systemd, reverse proxy. Copy-paste working configs. TLS. Monitoring.
- `SpawnDev.RTC.Server/Docs/use-cases.md` - four or five concrete example architectures: multiplayer game lobby, collaborative document, voice chat room, agent swarm coordinator, distributed compute. Each one shows how `RoomKey.FromString(...)` gives you a room without any torrent awareness.
- README: one-page "run this command, get a working signaling server" for the casual reader. Deep docs one link away.

### Success measured in operators, not downloads

NuGet download counts do not prove adoption. What proves adoption is **third-party tracker instances in the wild**. Quiet metrics we can watch over the next 6-12 months:

- Public signaling/tracker server count on the WebTorrent tracker sphere - does it grow.
- GitHub stars on `SpawnDev.RTC.Server` vs `SpawnDev.WebTorrent.Server` trend.
- Dev.to / HN / X mentions of SpawnDev.RTC signaling in non-torrent contexts.
- Inbound issues asking for signaling features from users who are not torrenting anything.

These are signals, not KPIs. The point is: **watch the shape of adoption, not the raw install count.**

---

## Network-effect flywheel (why this is worth doing)

The strategic argument, compressed:

1. **Before:** SpawnDev.RTC consumers who want serverless signaling either (a) take a WebTorrent dependency they do not need, (b) build their own signaling, or (c) rely on public WebTorrent trackers that they do not control.
2. **After Phase 1-3:** consumers can do serverless signaling against any public tracker with zero WebTorrent code. One pain point gone.
3. **After Phase 4-5:** those same consumers can run their **own** signaling server in one command. Every SpawnDev.RTC consumer is now a potential tracker operator.
4. **Flywheel:** operators run a signaling server because they need it for their game / tool / swarm. That server also happens to be a public WebTorrent tracker. The public WebTorrent network grows as a side effect of apps pursuing their own goals.
5. **Compounding:** more trackers → better WebTorrent experience (more meet points, better survivability) → more WebTorrent adoption → more eyes on SpawnDev → more consumers of SpawnDev.RTC → more potential operators. Loop.

The rare property here is alignment. Selfish infrastructure operator behavior **strengthens** a public commons. That is the right shape of decentralized infrastructure.

---

## Compatibility guarantees

These are the invariants the migration must preserve:

1. **Wire format unchanged.** An RTC 1.1.0 signaling client can announce to `wss://tracker.openwebtorrent.com` and meet other peers exactly as a WebTorrent 3.0.x client does today.
2. **WebTorrent clients reach RTC servers.** A stock WebTorrent client (JS WebTorrent, SpawnDev.WebTorrent, any other BitTorrent-over-WebRTC client) pointed at a `SpawnDev.RTC.Server` instance behaves exactly as it does against any WebTorrent tracker.
3. **RTC clients reach RTC servers.** Non-torrent consumers using `RoomKey.FromString("my-room")` can connect via a `SpawnDev.RTC.Server` **and** via any third-party WebTorrent tracker that does not validate info_hash semantics (which is all of them in practice - none actually verify the swarm is a real torrent).
4. **WebTorrent 3.1.0 has no user-visible behavior change.** Same APIs, same events, same perf. The adapter is an implementation detail. Tests prove it.
5. **Parallel `RTCTrackerClient.cs` deletion is safe.** Confirmed no SpawnDev project depends on it externally - it was a transitional minimal client. (This needs to be verified one more time before deletion during Phase 1, not assumed.)

---

## Test migration strategy

- Port existing `WebSocketTracker` tests from WebTorrent into RTC's `SpawnDev.RTC.Demo.Shared`, stripping any torrent-specific assertions. These become the generic signaling client tests.
- Port existing `TorrentTracker` server tests into RTC.Server's test suite, similarly generalized.
- Add new RTC-side tests: `RoomKey` helpers, `FromString` → `FromBytes` round trips, multi-room on one connection, opaque payload passthrough.
- Add new RTC-side tests: no-WebTorrent-reference verification - a dedicated test project that has zero WebTorrent NuGet references and still completes a full signaling handshake.
- In WebTorrent, **keep** integration tests that drive a real announce against a loopback `SpawnDev.RTC.Server` to prove the adapter round-trips torrent announce fields correctly.
- Regression gates: full 374-test WebTorrent suite must stay green after Phase 3. Any red is a migration bug to fix, not a new-feature gap.

---

## Sequencing and dependencies

**Upstream precondition:** RTC 1.0.1-rc.1 / WebTorrent 3.0.1-rc.1 verified by Geordi on ILGPU.P2P (P2P WebGPU 162/1/6 → 163/0/6). Do not start Phase 1 until that is banked.

**One editor per project.** Per standing Rule 1 in active-agents.md: Riker is the editor for RTC + WebTorrent + MultiMedia. This work is all in Riker's lane. No cross-editor coordination problems.

**Blocking work on other agents?** None that I can see:
- Data - Lost Spawns + VoxelEngine + GameUI. Independent.
- Tuvok - research/planning/browser testing. Unaffected.
- Geordi - ILGPU + UnitTesting. ILGPU.P2P passively benefits (can switch to RTC signaling in a future major without requiring WebTorrent). Not blocked by this plan.

**Coordination touchpoints:**
- Phase 0 DevComms announcement so everyone knows RTC + WebTorrent are under edit.
- Phase 3 completion DevComms so ILGPU.P2P knows WebTorrent adapter stayed source-compatible and no consumer-side changes are required.
- Phase 5 deployment DevComms so the crew knows `hub.spawndev.com` is now serving from the new server package (and how to roll back if it regresses).

---

## Risks and open questions

### Risks

- **Scope creep.** The tempting version of this plan adds transports (WebTransport, TURN, pluggable non-tracker signaling). Resist. v1 ships the tracker protocol, nothing else. Other transports go in a follow-up plan.
- **WebTorrent behavior drift.** The adapter has to be bit-identical in behavior for the 374-test suite to stay green. If a test goes red, that is a real regression to fix, not a "close enough" to paper over. Rule 1 applies.
- **Performance parity.** Re-profile after the migration. Signaling is not hot but regressions here cost connection time on every torrent swarm and every RTC app. Measure before and after announce latency + peer-meet time.
- **Name collisions and discoverability.** `SpawnDev.RTC.Server` is a discoverable name. `TrackerSignalingServer` inside it is searchable. Do not let "tracker" leak out of the package in contexts where it confuses non-torrent operators.
- **ASP.NET Core dependency.** `SpawnDev.RTC` must stay clean of ASP.NET deps. All ASP.NET coupling lives in `SpawnDev.RTC.Server`. This is a hard boundary - if ASP.NET bleeds into the client-side package, we have broken Blazor WASM consumers. CI rule: build-break if `SpawnDev.RTC.csproj` transitively references `Microsoft.AspNetCore.*`.
- **Public tracker compatibility.** Real-world public trackers sometimes drift from spec. Test against at least 3 popular public trackers during Phase 1 (`openwebtorrent.com`, `btorrent.xyz`, `webtorrent.dev` or equivalent) to catch quirks early.

### Decisions locked in by Captain 2026-04-17

1. **`SpawnDev.RTC.ServerApp` - IN SCOPE for v1.** Phase 4 ships with the migration, not as a follow-up. Self-contained single-file exe + Docker image are part of the v1 deliverable. This is where the adoption-flywheel leverage lives.
2. **Package name: `SpawnDev.RTC.Server`.** Matches the `SpawnDev.WebTorrent.Server` pattern. Operators grep "RTC server" not "signaling server."
3. **`RoomKey.FromString` hash: SHA-1 of UTF-8.** Exact-fit wire format (20 bytes). Rooms are not a trust boundary - operators who care use `RoomKey.Random()`. No security concern.
4. **WebTorrent consumer source-compat: STRICT.** Consumers change a PackageReference version and nothing else. Zero code edits required downstream. Adapter internals may rename, public surface does not.
5. **`SpawnDev.WebTorrent.Server.TorrentTracker`: HARD DELETE in 3.1.0.** No `[Obsolete]` deprecation dance. Replaced by one line of `app.UseRtcSignaling(...)` in the new server package. Clean break.

---

## Acceptance checklist (use this to decide "done")

- [ ] `SpawnDev.RTC.Signaling` namespace ships an `ISignalingClient` and `RoomKey` with full tests on browser + desktop.
- [ ] `SpawnDev.RTC.Server` NuGet package exists, consumable in one-line `app.UseRtcSignaling(...)`, tested end-to-end.
- [ ] `RTCTrackerClient.cs` deleted, no references anywhere in the RTC or downstream solutions.
- [ ] WebTorrent 374-test suite still green. Playwright and NUnit.
- [ ] At least one SpawnDev.RTC consumer proves zero-WebTorrent signaling works (dedicated test project with no WebTorrent reference).
- [ ] `hub.spawndev.com` serving from the new server package, 24h soak clean, real-world WebTorrent clients still connecting.
- [ ] `Docs/run-a-tracker.md` lives, has working Docker / systemd / reverse-proxy examples.
- [ ] "WebTorrent-compatible" framed as a bonus on the README, not the headline.
- [ ] Rollback plan documented: revert hub.spawndev.com to the prior server app; both RTC 1.0.x and WebTorrent 3.0.x stay published so downstream consumers that lag behind still have a working path.

---

## Notes for the crew

**Data:** no action required, FYI only. This does not touch VoxelEngine, GameUI, Lost Spawns, SDF, or anything on your plate. If during this work I find a cross-project pattern worth adopting in your projects (e.g., `RoomKey.FromString` ergonomics) I will flag it in DevComms.

**Tuvok:** this plan would benefit from a second read before Phase 1 starts. Specifically: the `ISignalingClient` interface sketch and the `RoomKey` API surface - if you see anything that will bite us in 6 months, say so. Also: the adoption/deploy-story section is the most strategic part of the doc and I want sanity-check on the pitch framing before we commit to it in README copy.

**Geordi:** this affects your ILGPU.P2P lane in a good way. After Phase 3 completes, a future P2P major could drop WebTorrent entirely and signal through SpawnDev.RTC directly, if that simplifies your stack. Not asking for that decision now - just flagging the option opens up.

**Captain:** the four open questions above need your input before Phase 1 starts. Everything else is within Riker's authority under standing team-lead instructions. Let me know which way you want to go on each and I will execute.

🖖 Draft complete. Ready for review. No code touched. Work begins on your green light (and after Geordi's rc.1 verification banks).

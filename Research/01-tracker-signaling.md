# SpawnDev.RTC Tracker Signaling Protocol

The SpawnDev.RTC signaling layer reuses the WebTorrent WebSocket tracker wire protocol as a generic WebRTC signaling channel. This document is the complete behavioral spec — every message type, every field, every JSON shape, and every server-side decision rule the JS reference makes.

This is the same protocol described in [SpawnDev.WebTorrent's Research/04-tracker-protocols.md](https://github.com/LostBeard/SpawnDev.WebTorrent/blob/master/SpawnDev.WebTorrent/Research/04-tracker-protocols.md), but framed for SpawnDev.RTC consumers — multiplayer games, agent swarms, voice chat, distributed compute — who do not need to know about BitTorrent.

## Table of Contents

1. [Why this protocol](#1-why-this-protocol)
2. [Connection lifecycle](#2-connection-lifecycle)
3. [Wire format](#3-wire-format)
4. [Message catalog](#4-message-catalog)
5. [Server-side behavior — verified against JS reference](#5-server-side-behavior--verified-against-js-reference)
6. [Complete exchange — three-peer mesh](#6-complete-exchange--three-peer-mesh)
7. [Implementation map](#7-implementation-map)

---

## 1. Why this protocol

A WebRTC peer-to-peer connection requires a **signaling channel** to exchange SDP offers, SDP answers, and ICE candidates before the direct WebRTC link can be established. The signaling channel is application-defined — the WebRTC spec deliberately leaves it open. Implementations roll their own (Firebase, Socket.IO, custom WebSocket, etc.) at the cost of fragmentation.

SpawnDev.RTC adopts the WebTorrent **WebSocket tracker** as a ready-made signaling protocol because:

1. **Stable, deployed wire format.** The WebTorrent tracker has been running in production since ~2016 and has thousands of public deployments. The wire format is stable.
2. **Browser + desktop interop.** Browser WebRTC clients and desktop SipSorcery clients both already implement the protocol via the WebTorrent ecosystem.
3. **Public-tracker compatibility.** A SpawnDev.RTC application can use any public WebTorrent tracker (`openwebtorrent.com`, `tracker.webtorrent.dev`, etc.) for free signaling. Or run its own — `SpawnDev.RTC.Server` ships an ASP.NET-Core `app.UseRtcSignaling("/announce")` extension.
4. **Decoupled from BitTorrent.** The tracker treats `info_hash` as an opaque 20-byte room identifier. SpawnDev.RTC reframes it as `RoomKey` (see [05-room-key-and-peer-id.md](05-room-key-and-peer-id.md)) with no BitTorrent baggage. Existing public trackers don't know your room is a multiplayer game lobby — they just route offer/answer frames.

The protocol is JSON over WebSocket. No formal BEP — the JS reference (`webtorrent/bittorrent-tracker`) defines it.

---

## 2. Connection lifecycle

### URL format

```
wss://hub.spawndev.com:44365/announce
ws://localhost:5590/announce
wss://tracker.openwebtorrent.com   (no /announce path)
```

The `/announce` path is conventional but not required — the JS reference treats the WebSocket endpoint as the announce endpoint regardless of path. SpawnDev.RTC.Server's `app.UseRtcSignaling("/announce")` mounts at a configurable path; the default is `/announce`.

### Connect

A standard WebSocket upgrade. Headers that matter to the server:

- `Origin`: when the server has an `AllowedOrigins` allowlist configured, browser-initiated upgrades with a non-matching `Origin` get rejected with HTTP 403. Empty/missing `Origin` (non-browser clients — desktop C#, Node.js without explicit Origin override, curl) bypasses the check (RFC 6454: browsers always send Origin; missing means non-browser).
- No subprotocol negotiation.
- No special headers required from the client.

After the upgrade, the client sends `announce` messages and receives any of: announce response, forwarded offer, forwarded answer, scrape response, error.

### Heartbeat / re-announce

The protocol has no ping/pong frames at the application layer (TCP keep-alive is the only liveness signal). Clients should re-announce periodically — the announce response carries `interval` in seconds (default 120 in the JS reference), and the client should re-announce no more frequently than `interval`.

### Reconnection on failure

WebSocket close → exponential backoff. JS reference uses min 10s, max 60h, doubling on each failure. SpawnDev.RTC's `TrackerSignalingClient` follows the same shape.

### Multiple rooms per connection

A single WebSocket connection can multiplex multiple rooms by sending announces with different `info_hash` values. The server tracks per-room peer state; the client demultiplexes by `info_hash` on inbound frames.

---

## 3. Wire format

### JSON over WebSocket text frames

Every wire frame is a single JSON object encoded as UTF-8 in a WebSocket text frame. No length prefix, no framing — one WebSocket message = one JSON object.

### Binary string encoding for 20-byte fields

`info_hash`, `peer_id`, `to_peer_id`, and `offer_id` are 20-byte binary values. JSON has no native binary type. The reference encodes each byte as a single Unicode code point in latin1 (ISO 8859-1) — i.e., each byte's value is its code point, then UTF-8 encoded for transport.

Example: bytes `[0x12, 0x34, 0x56, 0x78, ...]` become the string `"4Vx..."` in JSON, which UTF-8-serializes to the byte sequence.

```csharp
// SpawnDev.RTC's BinaryJsonSerializer handles this — converts 20-byte arrays
// to/from latin1 strings transparently. Consumers don't write the encoding by
// hand.
var infoHashLatin1 = Convert.ToString(infoHashBytes, Encoding.Latin1);
```

The encoding has known issues (some byte sequences produce invalid UTF-8 surrogate pairs in strict implementations). There's an open WebTorrent proposal (`webtorrent/webtorrent#1676`) to switch to hex; for now, all production trackers and clients use the latin1 binary string. SpawnDev.RTC implements latin1 to maintain interop.

### Field naming

snake_case throughout (`info_hash`, `peer_id`, `to_peer_id`, `offer_id`). SpawnDev.RTC's wire DTOs use C# PascalCase property names with `JsonNamingPolicy.SnakeCaseLower`.

### Optional fields

Use **omission**, not `null`. The C# server uses `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on optional output fields so emitting `null` and not emitting the field produce the same wire bytes. JS clients typically distinguish "field absent" from "field present and null"; emitting `null` where the JS reference emits absence is a divergence.

---

## 4. Message catalog

Each subsection is a complete frame schema with every field, type, and JS-reference behavior.

### 4.1 Client → Server: `announce` (regular, with offers)

Sent by a peer that wants to be visible in the room and is prepared to accept incoming peer connections. Every offer in the array is one SDP offer ready to be relayed to one existing peer.

```jsonc
{
  "action":     "announce",
  "info_hash":  "<20-byte binary string - opaque room key>",
  "peer_id":    "<20-byte binary string - this peer's id>",
  "uploaded":   0,                 // BitTorrent inheritance, ignored by RTC
  "downloaded": 0,                 // BitTorrent inheritance, ignored by RTC
  "left":       1024,              // 0 = "completed/seeder", anything > 0 = "leecher"
  "event":      "started",         // optional: started | completed | stopped | (omitted)
  "numwant":    5,                 // informational; actual is min(offers.length, room peers - 1)
  "offers": [
    {
      "offer":    { "type": "offer", "sdp": "v=0\r\no=- 123 2 IN IP4 0.0.0.0\r\ns=-\r\n..." },
      "offer_id": "<20-byte binary string>"
    },
    // ... up to 5 in the JS reference, configurable in SpawnDev.RTC
  ]
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `action` | string | yes | Always `"announce"` for this message |
| `info_hash` | binary string | yes | 20 bytes — the room key |
| `peer_id` | binary string | yes | 20 bytes — this peer's id, MUST be stable across re-announces in the same session |
| `uploaded` | int | no | BitTorrent semantics, RTC ignores |
| `downloaded` | int | no | BitTorrent semantics, RTC ignores |
| `left` | int | no | 0 marks the peer as a seeder for `complete` count; non-zero leaves as leecher |
| `event` | string | no | `"started"` on first announce, `"completed"` to mark seeder, `"stopped"` to leave gracefully, omitted otherwise |
| `numwant` | int | no | Hint to server; actual forwarding is bounded by `min(offers.length, candidates)` |
| `offers` | array | no | One SDP offer per existing peer the announcer wants to meet |

### 4.2 Server → Client: `announce` (regular response)

Sent by the server in response to every regular announce (i.e., not an answer-relay; see §5).

```jsonc
{
  "action":     "announce",
  "info_hash":  "<echoed back>",
  "interval":   120,
  "complete":   <count of seeders in the room AFTER this announce was processed>,
  "incomplete": <count of leechers in the room AFTER this announce was processed>
  // NO `peers` field. NO `tracker_id` field by default.
}
```

The C# server uses an explicit `Peers = null` on its `AnnounceResponse` DTO, combined with `[JsonIgnore(WhenWritingNull)]`, to ensure the field is omitted entirely on the wire.

### 4.3 Server → Existing Peer: `announce` (forwarded offer)

When a peer announces with offers, the server picks existing peers from the room (excluding the announcer) in **random order** and forwards one offer per peer until either the offer array runs out or the candidate list runs out. The recipient of a forwarded offer sees:

```jsonc
{
  "action":    "announce",
  "info_hash": "<the room key>",
  "peer_id":   "<announcer's peer id, NOT recipient's>",
  "offer":     { "type": "offer", "sdp": "v=0\r\n..." },
  "offer_id":  "<from the announcer's offer item>"
}
```

The recipient is expected to:
1. Create a local `RTCPeerConnection`
2. `setRemoteDescription(offer)`
3. `createAnswer()` → `setLocalDescription(answer)`
4. Wait for ICE gathering to complete (the WebTorrent tracker has no trickle-ICE — full SDP must be sent)
5. Send back an answer-relay announce (§4.4) with `to_peer_id = peer_id` (announcer) and `offer_id` matching the forwarded offer

### 4.4 Client → Server: `announce` (answer-relay)

A reply to a previously-received forwarded offer. The wire is structured like an announce, but the server treats it specially (see §5.2).

```jsonc
{
  "action":     "announce",
  "info_hash":  "<the room key>",
  "peer_id":    "<the answering peer's id>",
  "answer":     { "type": "answer", "sdp": "v=0\r\n..." },
  "to_peer_id": "<the original offering peer's id, copied from the forwarded offer's peer_id>",
  "offer_id":   "<copied from the forwarded offer>"
}
```

### 4.5 Server → Original Offerer: `announce` (forwarded answer)

The server forwards the answer to the targeted peer:

```jsonc
{
  "action":    "announce",
  "info_hash": "<the room key>",
  "peer_id":   "<the answering peer's id>",
  "answer":    { "type": "answer", "sdp": "v=0\r\n..." },
  "offer_id":  "<the original offer_id>"
}
```

The original offerer matches `offer_id` to its pending local offer and `setRemoteDescription(answer)`. The WebRTC connection then completes via the established ICE candidates.

### 4.6 Client → Server: `scrape` (room counts query)

```jsonc
// Single room:
{ "action": "scrape", "info_hash": "<20-byte binary string>" }

// Multiple rooms:
{
  "action": "scrape",
  "info_hash": [ "<20 bytes>", "<20 bytes>", ... ]
}
```

### 4.7 Server → Client: `scrape` response

```jsonc
{
  "action": "scrape",
  "files": {
    "<hex-encoded info_hash>": {
      "complete":   15,
      "incomplete": 47,
      "downloaded": 1234     // BitTorrent inheritance, mostly ignored by RTC
    }
  }
}
```

**Note:** SpawnDev.RTC.Server's current `TrackerSignalingServer` does not yet implement scrape — incoming `action=scrape` frames hit the default branch and are silently ignored. Tracked as a follow-up.

### 4.8 Server → Client: `error`

```jsonc
{
  "action":         "announce",
  "info_hash":      "<echoed back>",
  "failure reason": "missing info_hash or peer_id"
}
```

The C# server emits this when `info_hash` or `peer_id` is missing or malformed. JS clients parse `failure reason` as a fatal-for-this-info-hash error.

---

## 5. Server-side behavior — verified against JS reference

The behaviors below were observed against the JS bittorrent-tracker reference (npm `bittorrent-tracker` package, run as a local server in `verify-tracker-parity.mjs`) and SpawnDev.RTC.Server matches each one.

### 5.1 Regular announce (non-answer, non-stop) → response sent

For any announce that is not an answer-relay (§5.2) and not stopped (§5.3), the server:
1. Adds the peer to the room (overwriting any prior socket binding for the same `peer_id`)
2. Marks `IsSeeder` if `event=completed` or `left=0`
3. Builds and sends an announce response (§4.2) to the announcer
4. If `offers` is present, forwards one offer per existing peer in the room (excluding the announcer) in randomized order

### 5.2 Answer-relay path → server sends NO response to the answer-sender

When an announce carries `answer + to_peer_id + offer_id`, the server:
1. **Does NOT add or update the room** (the announcer is already in the room from a prior announce)
2. Forwards the answer to `to_peer_id` (§4.5)
3. **Sends nothing to the answer-sender**

This is the JS reference behavior. A server that responds with an announce-counts frame in this case is producing an extra spurious frame the client doesn't expect. SpawnDev.RTC.Server matches the reference (verified by `verify-tracker-parity.mjs` scenario D).

### 5.3 `event=stopped` → response sent, with post-stop counts

When an announce carries `event=stopped`, the server:
1. Removes the peer from the room
2. **Sends an announce response** with the now-decremented counts (`incomplete` reflects post-removal)
3. Skips offer-forwarding regardless of whether offers were included

The room is removed entirely if it becomes empty.

Some early SpawnDev.RTC.Server builds returned early on `event=stopped` without sending any response — that was a divergence from the JS reference, fixed in 1.0.7-rc.1 (verified by `verify-tracker-parity.mjs` scenario E).

### 5.4 `event=completed` → mark as seeder, otherwise like a regular announce

`event=completed` is a one-shot transition signal; the peer remains in the room as a seeder. Subsequent announces from the same peer with `left=0` continue to count as seeder.

### 5.5 Forwarded-offer fan-out

```
candidates = room.peers - { announcer }   // existing peers except the announcer
candidates = shuffle(candidates)          // random order
forward_count = min(announce.offers.length, candidates.length)
for i in 0..forward_count:
    server.send(candidates[i], wrap_as_forwarded_offer(announce.offers[i], announce.peer_id))
```

`numwant` is informational only — the actual forwarding count is the min of the offer array length and the existing-peer count. The reference does not pad with empty offers, does not duplicate-assign, and does not respect `numwant` over the actual array length.

### 5.6 Reconnect with same `peer_id`

When a peer disconnects and reconnects (new WebSocket) and announces with the same `peer_id` for the same `info_hash`, the room overwrites the binding cleanly. The previous socket (if it somehow stayed alive — typically TCP RST has already torn it down) does not receive forwarded frames; the new socket replaces it.

### 5.7 Room isolation

A peer's announces only affect rooms keyed by the announce's `info_hash`. The same peer can be in multiple rooms simultaneously (different `info_hash` per announce); state is tracked independently per room. There is no cross-room offer leakage.

### 5.8 `IsPeerConnected(peerId)` server-side check

SpawnDev.RTC.Server exposes `tracker.IsPeerConnected(peerId)` to enable **tracker-gated TURN allocation** — only peers currently announced (in any room) can obtain a TURN credential. This is not part of the JS reference; it's a SpawnDev.RTC extension. See [04-stun-turn.md](04-stun-turn.md).

### 5.9 Frame size cap

SpawnDev.RTC.Server's default `MaxMessageBytes` is 1 MiB. Frames larger than this are dropped silently, and the connection may be closed. Configurable via `TrackerServerOptions.MaxMessageBytes`.

### 5.10 Connection-close cleanup

When the WebSocket closes (clean close or abrupt drop), the server removes the peer from every room it was in and removes any rooms that become empty. The cleanup happens in the `finally` block of the receive loop, so it runs on all close paths (close frame, connection reset, IO exception, unhandled exception).

---

## 6. Complete exchange — three-peer mesh

All three peers want to be in a 3-mesh (each peer connected to every other peer). The sequence:

```
peer-A                      tracker (server)                    peer-B                      peer-C
  |                                |                                |                            |
  |--- WS connect ---------------->|                                |                            |
  |--- announce ---------------->  |  (event=started, offers=[],    |                            |
  |    (numwant=2, no offers       |   numwant=2)                   |                            |
  |     since room is empty)       |                                |                            |
  |<- announce response -----------| (interval=120, c=0, i=1)       |                            |
  |                                |                                |                            |
  |                                |<--- WS connect -----------------|                           |
  |                                |<--- announce -------------------|  (event=started,           |
  |                                |     (numwant=2, offers=[O1,O2]) |   2 offers ready for       |
  |                                |                                 |   2 candidates: A only)    |
  |                                |---- announce response --------->|  (c=0, i=2)                |
  |<-- forwarded offer O1 ---------|                                 |                           |
  |    peer_id=B, offer_id=O1.id    |                                |                           |
  |                                |                                 |                           |
  |--- announce (answer-relay) --->|                                 |                           |
  |    answer=A1, to_peer_id=B,    |                                 |                           |
  |    offer_id=O1.id              |                                 |                           |
  |  (NO response from server)     |---- forwarded answer ---------->|                           |
  |                                |     peer_id=A, answer=A1,        |                          |
  |                                |     offer_id=O1.id               |                          |
  |                                |                                  |                          |
  |======== ICE / DTLS ============== Direct WebRTC P2P =================== A↔B established =====|
  |                                |                                  |                          |
  |                                |<--- WS connect ----------------------------------------------|
  |                                |<--- announce -----------------------------------------------|  (numwant=2,
  |                                |     (offers=[P1, P2])           |                          |   2 offers for
  |                                |                                 |                          |   2 candidates:
  |                                |                                 |                          |   A and B)
  |                                |---- announce response --------------------------------------> (c=0, i=3)
  |<-- forwarded offer P1 ---------|                                 |                          |
  |    peer_id=C, offer_id=P1.id   |                                 |                          |
  |                                |---- forwarded offer P2 -------->|                          |
  |                                |     peer_id=C, offer_id=P2.id   |                          |
  |--- announce (answer-relay) --->|                                 |                          |
  |    answer=A2, to_peer_id=C,    |                                 |                          |
  |    offer_id=P1.id              |                                 |                          |
  |                                |---- forwarded answer ----------------------------------------> A2
  |                                |                                 |                          |
  |                                |  <--- announce (answer-relay) ---|                          |
  |                                |       answer=B2, to_peer_id=C,  |                          |
  |                                |       offer_id=P2.id            |                          |
  |                                |---- forwarded answer ----------------------------------------> B2
  |                                |                                 |                          |
  |======== A↔C and B↔C WebRTC P2P established ==============================================|
```

After this sequence the tracker is no longer involved in the data path. All peer-to-peer communication flows over the established WebRTC data channels. The tracker is only consulted again when peers need to find new peers (re-announce on interval) or leave (event=stopped).

---

## 7. Implementation map

| Wire concept | Server (SpawnDev.RTC.Server) | Client (SpawnDev.RTC) |
|---|---|---|
| WebSocket endpoint | `app.UseRtcSignaling("/announce")` from `SignalingAppBuilderExtensions` | `TrackerSignalingClient.ConnectAsync(url)` |
| Connection acceptance | `TrackerSignalingServer.HandleWebSocketAsync` | `ClientWebSocket` (desktop) / browser native (browser) |
| Frame deserialization | `JsonSerializer.Deserialize<WireMessage>(json, _readOpts)` with `DefaultJsonTypeInfoResolver` for AOT/trim safety | Same |
| Frame serialization | `BinaryJsonSerializer.Serialize(...)` (latin1 binary string handling baked in) | Same |
| Announce processing | `TrackerSignalingServer.HandleAnnounceAsync` | `TrackerSignalingClient.AnnounceAsync(roomKey, peerId, offers, event)` |
| Offer-relay forwarding | Inline in `HandleAnnounceAsync` after sending response | n/a (server-side only) |
| Answer-relay forwarding | Inline in `HandleAnnounceAsync` short-circuit path | `TrackerSignalingClient.SendAnswerAsync(roomKey, toPeerId, offerId, answer)` |
| Origin allowlist | `TrackerServerOptions.AllowedOrigins` + `IsOriginAllowed()` | n/a |
| Frame-size cap | `TrackerServerOptions.MaxMessageBytes` (1 MiB default) | n/a |
| Send timeout | `TrackerServerOptions.SendTimeoutMs` | n/a |
| Tracker-gated TURN | `TrackerSignalingServer.IsPeerConnected(peerId)` | n/a (server-side check) |

The server does no application-level work beyond the wire — it's a stateless message router with per-room peer-id-to-socket bookkeeping. All actual signaling logic (offer generation, ICE waiting, answer matching, peer-connection lifecycle) lives in the client. See [02-rtc-peer-connection.md](02-rtc-peer-connection.md).

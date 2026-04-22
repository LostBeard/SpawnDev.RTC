# Tracker Comparison: RTCTrackerClient vs WebSocketTracker

**Date:** 2026-04-16
**Author:** Riker
**Context:** Captain asked for comparison between `RTCTrackerClient` (SpawnDev.RTC) and `WebSocketTracker` (SpawnDev.WebTorrent) to decide if the two should consolidate, now that SpawnDev.RTC is THE transport layer.

---

## Quick Stats

| | RTCTrackerClient | WebSocketTracker |
|---|---|---|
| Location | `SpawnDev.RTC/RTCTrackerClient.cs` | `SpawnDev.WebTorrent/WebSocketTracker.cs` |
| Lines | 388 | 613 |
| Status | Working (used by RTC demos + WPF chat) | Unknown (flagged "possibly broken" by Captain) |
| Purpose | Room-based WebRTC signaling (chat rooms) | Full BitTorrent tracker client |

## Wire-Level Compatibility

Both speak the **WebTorrent tracker protocol** (the same one public trackers like openwebtorrent.com serve). They send the same action/announce/offer/answer/offer_id payloads. A client using either one will get peers back from the same tracker.

Critical encoding detail: both treat the peer_id/info_hash fields as "binary strings" where each byte becomes a latin1 char. They match on the wire.

## Feature Matrix

| Feature | RTCTrackerClient | WebSocketTracker |
|---|---|---|
| **Core** | | |
| Speaks tracker protocol | Yes | Yes |
| Announce with offers | Yes | Yes |
| Incoming offer -> answer | Yes | Yes |
| Incoming answer -> connect | Yes | Yes |
| `stopped` event on leave | Yes | Yes |
| **Advanced BitTorrent** | | |
| Socket pooling (one WS per tracker URL, shared across torrents) | No | Yes (`_socketPool` keyed by URL+peerId) |
| Multi-torrent routing on shared socket | No | Yes (`Subscribe(infoHash, ...)` per-torrent handlers) |
| Reconnection with exponential backoff | No | Yes (JS-matching timing: min 10s, max 1hr, variance 5min) |
| Periodic re-announce | No | Yes (interval from tracker response) |
| Offer timeout + cleanup (50s) | No | Yes |
| Per-torrent peer factory | No | Yes |
| Per-torrent warning/update handlers | No | Yes |
| `completed` / `stopped` event semantics for seeders | Partial | Yes |
| Scrape support | No | No (stubbed in both) |
| **JSON / encoding** | | |
| JSON encoder | `UnsafeRelaxedJsonEscaping` | Custom `BinaryJsonSerializer` (handles C1 control chars) |
| Binary string convention | `Encoding.Latin1.GetString(bytes)` | `new string(bytes.Select(b => (char)b).ToArray())` |
| Filter self-messages | No | Yes (peer_id compare) |
| Failure reason / warning parsing | No | Yes |
| **Identity** | | |
| Peer ID prefix | `-SR0100-` (hardcoded) | Caller-supplied byte[] (`-WW0208-` in WebTorrent) |
| Info hash source | SHA-1 of room name | Caller-supplied byte[] |
| **Types** | | |
| Peer type | `IRTCPeerConnection` direct | `SimplePeer` abstraction |
| Data channel type | `IRTCDataChannel` direct | Routed via `SimplePeer.OnData` |

## Functional Overlap

Both do the same 80% - connect to a WS tracker, negotiate offer/answer, fire a "peer ready" event. The remaining 20% is where they diverge:

- **RTCTrackerClient** is a one-shot: connect, send N offers, handle responses, done. Good for 1:1 chat rooms.
- **WebSocketTracker** is a long-lived BitTorrent client: reconnects, re-announces, pools sockets across swarms, filters by infoHash, times out stale offers.

## Options

### Option A - Keep both (pragmatic)
- RTC retains `RTCTrackerClient` for its chat-room demos.
- WebTorrent retains `WebSocketTracker` for BitTorrent.
- Pro: no risk of regressing WebTorrent's JS-parity behaviors.
- Con: two tracker codebases. Drift risk. WebSocketTracker's "possibly broken" status still needs investigation separately.

### Option B - Promote WebSocketTracker to SpawnDev.RTC, retire RTCTrackerClient
- Move `WebSocketTracker` (with all its features) into SpawnDev.RTC.
- Delete `RTCTrackerClient`.
- Update RTC demos to use the full tracker (add room-name -> infoHash helper on top).
- Pro: one battle-tested tracker implementation. Alignment with "RTC is THE transport layer."
- Con: RTC now depends on `SimplePeer` abstraction (currently a WebTorrent concept). Either RTC grows a SimplePeer analogue or SimplePeer moves too.
- `BinaryJsonSerializer` and related helpers have to move too.
- WebTorrent becomes thinner: just consumes RTC's tracker.

### Option C - Promote RTCTrackerClient to SpawnDev.RTC, have WebTorrent consume it
- RTCTrackerClient stays in RTC as the one tracker.
- WebTorrent's `WebSocketTracker` features (pooling, reconnect, re-announce, multi-torrent routing, BinaryJsonSerializer) get **ported into** RTCTrackerClient.
- Pro: RTCTrackerClient's API is simpler; consolidating there keeps WebTorrent dependency narrow.
- Con: biggest amount of new code in RTC. Regression risk on WebTorrent tracker behavior unless the port is faithful.

### Option D - Extract shared base, keep both thin
- New: `WebTorrentTrackerProtocol` (or similar) in SpawnDev.RTC - handles wire protocol, socket, reconnect, offer/answer plumbing.
- `RTCTrackerClient` becomes a thin room-signaling wrapper over it.
- `WebSocketTracker` becomes a thin multi-torrent BitTorrent wrapper over it (or is retired entirely).
- Pro: cleanest architecture. Shared code is protocol-level only.
- Con: most work. Requires thoughtful API design.

## Recommendation

**Option A short-term, Option D long-term.**

Immediate (this week):
1. Run the Sintel e2e tests (task #3) to confirm whether WebSocketTracker is actually broken, and if so what's broken. "Possibly broken" is too vague to drive an architecture decision.
2. Keep both tracker implementations where they are.
3. Document the overlap (this file).

Medium-term (next 1-2 weeks, once WebTorrent e2e is green):
1. Extract a protocol layer into SpawnDev.RTC (Option D).
2. Retire the two specialized trackers as thin wrappers.
3. One place to fix tracker bugs.

**Do NOT consolidate blindly until we know what's actually broken in WebSocketTracker.** Consolidating a broken tracker into RTC would spread the breakage. Testing first is cheap (task #3). Consolidating after that is informed.

## Appendix: WebSocketTracker features that RTCTrackerClient would need if we consolidated via Option C

Anyone picking Option C should budget for porting these (line references approximate):

- Socket pool: `_socketPool` + `GetOrCreate()` (lines 26-55)
- Per-infoHash subscription model: `Subscribe()` / `Unsubscribe()` + four per-hash dictionaries (lines 83-138)
- Reconnection with exponential backoff: `StartReconnectTimer()` (lines 447-461)
- Periodic re-announce: `_announceTimer` + `SetAnnounceInterval()` (lines 540-544)
- Offer timeout lifecycle: timer-per-pending-offer in `GenerateOffersAsync()` (lines 500-504)
- Custom JSON serializer: `BinaryJsonSerializer` (separate file) handles C1 control chars that `UnsafeRelaxedJsonEscaping` escapes incorrectly for bittorrent-tracker
- Self-message filtering (lines 326-330)
- Failure reason / warning parsing (lines 333-349)
- Parallel offer generation (`Promise.all` style, lines 470-520)

Any consolidation that drops these features would regress WebTorrent's compatibility with public trackers.

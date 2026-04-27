# SpawnDev.RTC Protocol Research & Documentation

Comprehensive protocol documentation for the WebRTC signaling, peer-connection, data-channel, and STUN/TURN surfaces inside SpawnDev.RTC. This documentation serves two purposes:

1. **Internal reference** for the SpawnDev.RTC C# implementation across browser (Blazor WASM via SpawnDev.BlazorJS) and desktop (SipSorcery fork)
2. **Community documentation** — clear, complete protocol docs that the WebRTC + WebTorrent-tracker community currently lacks

This research is the SpawnDev.RTC counterpart to the [SpawnDev.WebTorrent Research/](https://github.com/LostBeard/SpawnDev.WebTorrent/tree/master/SpawnDev.WebTorrent/Research) protocol docs. WebTorrent owns the BitTorrent wire protocol and BEP details; RTC owns the WebRTC signaling, peer-connection, and TURN/STUN-server details. The two ecosystems share a tracker wire format (the WebSocket tracker is documented in both — RTC's coverage focuses on it as a generic signaling protocol decoupled from BitTorrent semantics).

## Documents

| # | Document | Description | Status |
|---|----------|-------------|--------|
| 00 | [00-README.md](00-README.md) | This index | Complete |
| 01 | [01-tracker-signaling.md](01-tracker-signaling.md) | WebSocket tracker wire protocol used as a generic WebRTC signaling channel (announce, offer-relay, answer-relay, stopped/completed events, scrape, frame-by-frame JS-reference behavior) | Complete |
| 02 | [02-rtc-peer-connection.md](02-rtc-peer-connection.md) | Cross-platform `RTCPeerConnection` abstraction — browser via SpawnDev.BlazorJS native interop, desktop via SipSorcery fork. ICE/DTLS/SRTP/SCTP layer-by-layer | Complete |
| 03 | [03-data-channels.md](03-data-channels.md) | DataChannel framing, BufferedAmount + threshold backpressure, SCTP cause codes (especially cause 12), `WireDataChannel` adapter semantics | Complete |
| 04 | [04-stun-turn.md](04-stun-turn.md) | Embedded STUN/TURN server protocol (RFC 5389/5766/8489), long-term and ephemeral (REST API pattern) credentials, tracker-gating, NAT port-range bounding, allocation lifecycle | Complete |
| 05 | [05-room-key-and-peer-id.md](05-room-key-and-peer-id.md) | The 20-byte RoomKey + PeerId conventions inherited from BitTorrent / WebTorrent. How RTC reuses them for generic rooms (multiplayer games, agent swarms, voice chat) without BitTorrent semantics | Complete |
| 06 | [06-sipsorcery-fork.md](06-sipsorcery-fork.md) | Why we fork SipSorcery and the per-modification rationale. References [SpawnDev.WebTorrent Research/09-sipsorcery-dtls-analysis.md](https://github.com/LostBeard/SpawnDev.WebTorrent/blob/master/SpawnDev.WebTorrent/Research/09-sipsorcery-dtls-analysis.md) for full DTLS/SRTP analysis | Complete |

## Sources

- IETF RFCs: 5245 (ICE), 5389 (STUN), 5766 (TURN), 8489 (STUN bis), 8445 (ICE bis), 8829 (JSEP), 8831 (DataChannels)
- W3C: [WebRTC 1.0](https://www.w3.org/TR/webrtc/), [WebRTC Stats](https://www.w3.org/TR/webrtc-stats/)
- JS reference: `webtorrent/bittorrent-tracker` server.js (WebSocket tracker)
- C# reference: SpawnDev.RTC source (`TrackerSignalingServer`, `RtcPeerConnection`, `WireDataChannel`)
- Desktop WebRTC reference: SpawnDev/SipSorcery fork (lives at `Src/sipsorcery/` as a git submodule)
- Tracker parity harnesses: `D:\users\tj\Projects\SpawnDev.WebTorrent\tracker-debug\` (Node.js, runs the JS reference and SpawnDev.RTC.Server head-to-head)
- Live deployment: `hub.spawndev.com` — production tracker + STUN/TURN under `SpawnDev.RTC.Server`

## Verification

The behaviors documented here are observed against real implementations, not paraphrased from specs:

- `tracker-debug/verify-tracker-parity.mjs` — runs six WebSocket-tracker scenarios (single peer / two peers / three peers / answer flow / stopped event / reconnect) head-to-head against `wss://hub.spawndev.com:44365/announce` AND a fresh local `bittorrent-tracker` JS reference. Diffs every captured frame and reports per-scenario divergences. Run before any change to `TrackerSignalingServer` or `WireMessage` DTOs.
- `PlaywrightMultiTest` (`D:\users\tj\Projects\SpawnDev.RTC\SpawnDev.RTC\PlaywrightMultiTest`) — Browser + desktop RTC tests including `Signaling.RoomIsolation`, `Signaling.CrossPlatform_BrowserDesktop`, `ServerApp.SmokeTest`, `DesktopTurnAuthTests`, `OriginAllowlistTests`. Run on every code change.

## Contributing

This documentation is maintained alongside the SpawnDev.RTC project. Discrepancies between docs and source are bugs in one or the other — open an issue or PR.

## License

This documentation is part of the SpawnDev.RTC project and is provided under the same license.

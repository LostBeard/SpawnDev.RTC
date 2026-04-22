# Changelog

## 1.1.0-rc.2 (2026-04-22)

### GetStats fix on both platforms

- `BrowserRTCStatsReport.Values` dict is now populated from the underlying JS `RTCStats` object via the typed `SpawnDev.BlazorJS.JSObjects.JSON.Stringify` helper. Consumers get the full W3C surface (`bytesReceived`, `roundTripTime`, `packetsLost`, `jitter`, `dataChannelsOpened/Closed`, etc.) rather than just `Id` / `Type` / `Timestamp`.
- Per-stat `RTCStats` JSObjects are now properly disposed (they leaked before).
- `DesktopRTCStatsReport` is no longer an empty stub. Returns two best-effort entries sourced from SipSorcery:
  - `peer-connection`: `connectionState`, `signalingState`, `iceGatheringState`, `iceConnectionState` (W3C shape: lowercase, hyphen-separated for signaling).
  - `transport`: `sessionId`, `dtlsState`, `dtlsCertificateSignatureAlgorithm`.
- SipSorcery's public API does not expose per-codec or per-candidate-pair counters, so those remain browser-only.

## 1.1.0-rc.1 (2026-04-22)

### `SpawnDev.RTC.Signaling` namespace (tracker-signaling migration Phase 1)

- New `ISignalingClient` + `ISignalingRoomHandler` interfaces with DTOs (`SignalingOffer`, `AnnounceOptions`, `SignalingSwarmStats`, `TrackerWireMessages`).
- `RoomKey` value type: SHA-1 of raw UTF-8 room name. **No normalization.** Any consumer that trims or lowercases silently joins a different swarm; xmldoc calls this out. Also supports `FromBytes`, `FromHex`, `Random`, `FromGuid`.
- `TrackerSignalingClient`: WebSocket tracker protocol client, binary-string latin1 framing is byte-compatible with plain JS WebTorrent peers. Shared socket pool (one connection per `announceUrl` + `peerId`). Reconnect with exponential backoff. Per-room offer/answer routing via `ISignalingRoomHandler`.
- `RtcPeerConnectionRoomHandler`: default room handler with an `IRTCPeerConnection` pool. Events `OnPeerConnection`, `OnDataChannel`, `OnPeerDisconnected`, `OnPeerConnectionCreated`.
- `BinaryJsonSerializer`: latin1-safe JSON helper for binary fields over WebSocket.

### Pairs with a new `SpawnDev.RTC.Server` NuGet package

- `TrackerSignalingServer` + `app.UseRtcSignaling("/announce")` extension let any ASP.NET Core app host a WebTorrent-compatible tracker in one line.
- Also available as a standalone executable + Docker image via `SpawnDev.RTC.ServerApp`.

### Legacy `RTCTrackerClient` removed

- Deleted in favour of `TrackerSignalingClient`. Consumers migrate by changing the type name and calling `Subscribe(room, handler)` + `AnnounceAsync(room, options)`.

### Consumers migrated

- `SpawnDev.RTC.Demo/Pages/ChatRoom.razor` (adopted `IAsyncDisposable`, preserves `OnPeerConnectionCreated` for `OnTrack` wiring).
- `SpawnDev.RTC.DemoConsole/ChatMode.cs`.
- `SpawnDev.RTC.WpfDemo/MainWindow.xaml.cs` (plus new `WpfVideoRenderer.cs` completing the MultiMedia integration from 1.0.1).
- `PlaywrightMultiTest`: 3 new `Signaling.*` integration tests (`Signaling.Embedded_TwoPeers`, `Signaling.Live_OpenWebTorrent`, `Signaling.CrossPlatform_BrowserDesktop`) plus `Signaling.RoomIsolation`.

### SipSorcery fork

- `NetServices` static constructor guarded against `PlatformNotSupportedException` on Blazor WASM (`NetworkChange.NetworkAddressChanged` subscription wrapped in try/catch). Ships the earlier 1.0.1-rc.1 fix.
- `WaitForIceGatheringToComplete` option on `RTCOfferOptions` / `RTCAnswerOptions` for synchronous SDP gathering on Desktop (required for non-trickle signaling scenarios).

### Tests

- Full RTC PlaywrightMultiTest suite: 253 / 0 / 0.
- `Signaling.*` filter: 8 / 8 (includes `Signaling.RoomIsolation` asserting peers in one room never appear in another room's announce response).

## 1.0.0 (2026-04-15)

Initial release. Built from scratch in a single day.

### Features
- Full W3C WebRTC API on browser (Blazor WASM) and desktop (.NET)
- Data channels: string, binary, zero-copy JS types (ArrayBuffer, TypedArray, Blob, DataView)
- Media streams: getUserMedia, getDisplayMedia, tracks, enable/disable, clone
- Transceivers: addTransceiver, direction control, getTransceivers
- ICE: trickle, restart, candidate gathering, STUN/TURN configuration
- SDP: offer/answer, implicit SetLocalDescription (perfect negotiation)
- Stats: GetStats with candidate-pair, transport stats
- DTMF: InsertDTMF, ToneBuffer
- Transport abstractions: DTLS, ICE, SCTP
- Configuration: BundlePolicy, IceTransportPolicy, IceCandidatePoolSize
- RTCTrackerClient: serverless signaling via WebTorrent tracker protocol
- RTCSignalClient: custom WebSocket signal server client
- Signal server: standalone + embedded in test infrastructure

### Demos
- Browser ChatRoom: video/audio/text conference with swarm signaling
- WPF desktop ChatRoom: text chat with peer list
- Console chat: text-only via tracker
- All demos serverless (openwebtorrent tracker, no server deployment)

### Testing
- 204 tests, 0 failures
- Pixel-level video verification (red, blue, split-screen spatial)
- SHA-256 data integrity verification
- Cross-platform desktop-to-browser via embedded signal server AND tracker
- Live openwebtorrent tracker integration test
- 5 simultaneous peer pairs stress test
- 256KB max payload, 100-message burst, 20 channel lifecycle

### SipSorcery Fork
- SRTP profiles restricted to 3 browser-compatible (AES-128-GCM, AES-256-GCM, AES128-CM-SHA1-80)
- TFMs trimmed to net48/net8.0/net9.0/net10.0

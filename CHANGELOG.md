# Changelog

## 1.1.3-rc.1 (2026-04-23)

### Upstream PR #1558 (SortMediaCapability) merged upstream

- The SipSorcery codec-priority inverted-ternary fix (at `RTPSession.cs:1221`) that shipped in SpawnDev.SIPSorcery 10.0.4-local.1 + our Phase 4a audio bridge work was [merged upstream](https://github.com/sipsorcery-org/sipsorcery/pull/1558) on 2026-04-23 by Aaron Clauson (merge commit `f3f32f9`). Future upstream SipSorcery releases carry the fix natively; our fork stays in sync.

### SCTP sender throughput fix (via SpawnDev.SIPSorcery 10.0.5-rc.1)

- Dep-bump to `SpawnDev.SIPSorcery 10.0.5-rc.1` which fixes the `SctpDataSender` producer-consumer lost-wakeup race. `_senderMre.Reset()` moved from AFTER the send work to the TOP of the `DoSend` loop so any `Set()` fired by SACK arrival during the send is preserved for the next `Wait(burstPeriod)` instead of being wiped.
- **Measured 60x speedup** on the new `SctpDataSenderUnitTest.Throughput_FastSackWake_ExceedsBurstCeiling` regression test (504 KB, synchronous SACK delivery over loopback): pre-fix 5613 ms / 89.8 KB/s → post-fix 94 ms / 5.4 MB/s.
- Unblocks SpawnDev.ILGPU.P2P's multi-MB tensor transfer path that was capped at ~77 KB/s on local WebRTC per Geordi's 2026-04-23 handoff. Downstream consumers pick up the fix transitively via `SpawnDev.WebTorrent 3.1.3-rc.1`.
- Zero source changes in SpawnDev.RTC itself — pure dep swap. Submodule pointer bump to `LostBeard/sipsorcery 2c4bf7714`.
- Added `Docs/audio-tracks.md` (Phase 4a walkthrough) and `Docs/sctp-tuning.md` (the SCTP fix documented for consumers).
- Upstream PR to sipsorcery-org pending after downstream verification.

## 1.1.2 (2026-04-22 stable) — shipped what the Unreleased block below described

### Phase 4a - SpawnDev.MultiMedia audio bridge + SipSorcery fork codec-priority fix

- New `DesktopRTCPeerConnection.AddTrack(IAudioTrack)` overload. A SpawnDev.MultiMedia `IAudioTrack` (e.g. WASAPI microphone capture) feeds straight into SipSorcery's RTP sender. Default path encodes Opus (WebRTC browser-native codec) via Concentus; explicit encoder overrides supported via `AddTrack(MultiMediaAudioSource)`.
- New `SpawnDev.RTC/Desktop/MultiMediaAudioSource.cs` (internal bridge class, also usable directly for advanced codec control). Handles Float32 -> PCM16 conversion, 20 ms framing, per-codec sample-rate/channel validation with clear NotSupportedException on mismatches (no silent-garbage audio).
- Conditional sibling-repo `ProjectReference` for `SpawnDev.MultiMedia` added to `SpawnDev.RTC.csproj` (mirrors the existing sipsorcery submodule pattern). External consumers fall back to a PackageReference once the MultiMedia package ships.
- SipSorcery fork fix: `src/SIPSorcery/net/RTP/RTPSession.cs:1221` had an inverted ternary in the `SortMediaCapability` priority-track selection. Symptom: two peers with identical multi-codec audio format lists negotiated *different* selected formats from the same offer/answer (offerer saw PCMU, answerer saw Opus). Fixed in the fork; same fix filed as an upstream PR: [sipsorcery-org/sipsorcery#1558](https://github.com/sipsorcery-org/sipsorcery/pull/1558).
- New end-to-end test `RTCTestBase.Phase4MediaTests.cs` - two `DesktopRTCPeerConnection` instances negotiate a 48 kHz stereo synthetic sine wave, assert `OnTrack(audio)` fires, SDP contains `m=audio` + `opus`, and pc2 receives >= 5 non-empty encoded Opus RTP frames within 20 s. Covers both signaling AND real RTP delivery.
- Full RTC PlaywrightMultiTest suite: **261/0/0** with the Phase 4a tests included, zero regressions on the previous 259.

### Phase 4b (upcoming)

- Windows MediaFoundation H.264 encoder via P/Invoke in SpawnDev.MultiMedia, then `AddTrack(IVideoTrack)` bridge on the same shape as the audio bridge. Scoped in `Plans/PLAN-SpawnDev-MultiMedia.md` Phase 4.

## 1.1.0-rc.4 (2026-04-22)

### Desktop GetStats + stronger test coverage

- `DesktopRTCStatsReport` now emits W3C-standard `dataChannelsOpened` / `dataChannelsClosed` on the `peer-connection` entry, derived from `SIPSorcery.Net.RTCPeerConnection.DataChannels`. Existing Desktop-specific extras (`connectionState`, `signalingState`, `iceGatheringState`, `iceConnectionState`) remain on the same entry for monitoring tools.
- `PeerConnection_GetStats` test strengthened: waits for DC open, asserts `peer-connection` + `transport` entries exist, asserts W3C `dataChannelsOpened >= 1` after opening a DC, and on Desktop asserts `connectionState == "connected"` post-handshake. Runs on both browser + desktop.
- New `ServerApp.SmokeTest`: launches `SpawnDev.RTC.ServerApp` as a subprocess on default port 5590, polls `/health` until ready, then verifies `/`, `/health`, `/stats` JSON shapes + a real `TrackerSignalingClient` announce round-trip. Kills the subprocess on teardown.
- Full RTC PlaywrightMultiTest suite: **253 / 0 / 0** at this ship (`1.1.0-rc.3` baseline of 252 plus the new ServerApp smoke test).

## 1.1.0-rc.3 (2026-04-22)

### Legacy removal

The custom `/signal/{roomId}` protocol from the pre-tracker-migration era is deleted. The WebTorrent-tracker-compatible signaling (`SpawnDev.RTC.Signaling.TrackerSignalingClient` + `SpawnDev.RTC.Server.TrackerSignalingServer`) replaces it in full.

- Removed: `SpawnDev.RTC/RTCSignalClient.cs` (the custom-protocol client).
- Removed: `SpawnDev.RTC.SignalServer/` project (the custom-protocol standalone server).
- Removed: `PlaywrightMultiTest/StaticFileServer` `/signal/{roomId}` endpoint + `SignalRoom` / `SignalPeer` support types.
- Removed: `PlaywrightMultiTest/ProjectRunner` `CrossPlatform.Desktop_Browser_DataChannel` test + `RunDesktopSignalPeer` helper - superseded by `Signaling.CrossPlatform_BrowserDesktop` which tests the same scenario through the tracker wire protocol.
- `_run-demo.bat` now launches `SpawnDev.RTC.ServerApp` (standalone tracker on `http://localhost:5590`) instead of the deleted SignalServer.

The codebase is < a month old, so no external consumer is relying on these - this is a clean cut, not a deprecation dance.

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
- RTCTrackerClient: serverless signaling via WebTorrent tracker protocol (removed in 1.1.0-rc.1; replaced by `SpawnDev.RTC.Signaling.TrackerSignalingClient`)
- RTCSignalClient: custom WebSocket signal server client (removed in 1.1.0-rc.3; replaced by `SpawnDev.RTC.Signaling.TrackerSignalingClient`)
- Signal server: standalone + embedded in test infrastructure (removed in 1.1.0-rc.3; replaced by `SpawnDev.RTC.Server` + `SpawnDev.RTC.ServerApp`)

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

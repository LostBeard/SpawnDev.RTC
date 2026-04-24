# Plan: SpawnDev.RTC v0.1.0 - Cross-Platform WebRTC

**Goal:** Ship a complete cross-platform WebRTC library - data channels, audio, video, media streams - that connects browser and desktop peers from a single API.

**Success criteria:** Desktop .NET and browser Blazor WASM peers connect and exchange data channel messages, audio tracks, and video tracks bidirectionally. Verified by PlaywrightMultiTest. API mirrors the W3C WebRTC specification.

> **Status (2026-04-24): v0.1.0 superseded — we're on v1.1.4 stable on nuget.org.** Phases 1-6 + 8 + 9 all shipped. Phase 4a (audio bridge) and Phase 4b (H.264 video bridge via MediaFoundation MFT) landed 2026-04-23. Phase 7 (advanced WebRTC features): renegotiation shipped, perfect-negotiation shipped, **TURN shipped (embedded RFC 5766 server with 4 credential modes + full relay data-path E2E)**. Only Simulcast's real-layer behavior (beyond DTO shape) remains unchecked, as a nice-to-have. Test count: **319 end-to-end via PlaywrightMultiTest** across browser + desktop. See `PLAN-Full-WebRTC-Coverage.md` for per-item status + `Docs/audio-tracks.md` / `Docs/video-tracks.md` / `Docs/stun-turn-server.md` for consumer-facing integration guides.


---

## Phase 1: SipSorcery Fork Setup - DONE

- [x] Fork `sipsorcery-org/sipsorcery` on GitHub under LostBeard
- [x] Add fork as git submodule at `Src/sipsorcery/`
- [x] SipSorcery.csproj builds within our solution
- [x] Trim target frameworks to net48/net8.0/net9.0/net10.0
- [x] Apply DTLS/SRTP fixes on our fork branch:
  - [x] Restrict SRTP profiles to browser-compatible set (AES128_CM_HMAC_SHA1_80, AEAD_AES_128_GCM, AEAD_AES_256_GCM per `CLAUDE.md`)
  - [x] NotifySecureRenegotiation override confirmed working (essential for Pion/libdatachannel compat)
  - [x] MKI negotiation disabled per RFC 8827
  - [x] DTLS handshake with browser peers verified (every browser+desktop data-channel test in the 135/0/0 Playwright matrix exercises this path end-to-end)

## Phase 2: Data Channel Abstraction - DONE

- [x] `IRTCPeerConnection` - Create offer/answer, SDP exchange, ICE, data channels
- [x] `IRTCDataChannel` - Send/receive string and binary, open/close/error events
- [x] `RTCPeerConnectionFactory.Create()` - Auto-detects platform
- [x] DTOs: `RTCPeerConnectionConfig`, `RTCSessionDescriptionInit`, `RTCIceCandidateInit`, `RTCDataChannelConfig`
- [x] Browser implementation: `BrowserRTCPeerConnection`, `BrowserRTCDataChannel`
- [x] Desktop implementation: `DesktopRTCPeerConnection`, `DesktopRTCDataChannel`
- [x] Zero-copy JS types: `Send(ArrayBuffer)`, `Send(TypedArray)`, `Send(Blob)`
- [x] `OnArrayBufferMessage` for zero-copy receive in WASM
- [x] `NativeConnection`/`NativeChannel` for platform-specific access

## Phase 3: Data Channel Tests - DONE

- [x] `TestInfrastructure_Working` - sanity check
- [x] `PeerConnection_CanCreate` - factory works on both platforms
- [x] `PeerConnection_CanCreateOffer` - SDP offer with data channel
- [x] `DataChannel_CanCreate` - channel creation and label
- [x] `Loopback_DataChannel_StringMessage` - two local peers exchange string
- [x] `Loopback_DataChannel_BinaryMessage` - two local peers exchange binary
- [x] `Loopback_DataChannel_Bidirectional` - ping/pong both directions
- [x] **Result: 16/16 pass (browser + desktop)** in 7 seconds

## Phase 4: Media Streams and Tracks — SHIPPED

- [x] `IRTCMediaStream` / `IRTCMediaStreamTrack` / `AddTrack` / `RemoveTrack` / `OnTrack` / `GetSenders` / `GetReceivers` — browser (BlazorJS wrap) + desktop (SipSorcery wrap).
- [x] Media capture: `GetUserMedia` + `GetDisplayMedia` (browser), `SpawnDev.MultiMedia.MediaDevices` on desktop.
- [x] Phase 4a audio bridge (2026-04-23): `DesktopRTCPeerConnection.AddTrack(IAudioTrack)` + `MultiMediaAudioSource` -> SipSorcery RTP audio encoder (Opus default). See `Docs/audio-tracks.md`.
- [x] Phase 4b video bridge (2026-04-23): `AddTrack(IVideoTrack)` + `MultiMediaVideoSource` -> H.264 via MediaFoundation MFT -> SipSorcery RTP. See `Docs/video-tracks.md`.

## Phase 5: RTP/RTCP and Transceivers — SHIPPED

- [x] `IRTCRtpSender` / `IRTCRtpReceiver` / `IRTCRtpTransceiver` (full browser + desktop).
- [x] `AddTransceiver(string kind)` + `AddTransceiver(track)` + `GetTransceivers()` on both platforms.
- [x] Codec negotiation + preference: `RTCRtpCodecInfo`, `IRTCRtpSender.GetParameters`/`SetParameters`.
- [x] DTMF: `IRTCDTMFSender` interface + browser impl.
- See `PLAN-Full-WebRTC-Coverage.md` Tier 2 + Tier 3 for per-item test coverage.

## Phase 6: Statistics and Diagnostics — SHIPPED

- [x] `GetStats()` on `IRTCPeerConnection` with `IRTCStatsReport` (browser full, desktop emits peer-connection + transport entries sourced from SipSorcery state).
- [x] W3C-standard stats: `dataChannelsOpened` / `dataChannelsClosed`, `connectionState`, `signalingState`, `iceGatheringState`, `iceConnectionState`.
- [x] Per-track + per-candidate-pair stats via RTPSender.GetStats on browser.

## Phase 7: Advanced Features — PARTIAL

- [x] **Renegotiation** - Add/remove tracks on live connection. **Both platforms verified via PlaywrightMultiTest (135/0/0 post-2026-04-23):** `Renegotiation_AddTrackAfterConnect_Desktop` (post-connect AddTrack + manual second offer/answer → pc2.OnTrack fires in 706 ms) and its browser mirror `Renegotiation_AddTrackAfterConnect_Browser` (same scenario via getUserMedia + fake-device-for-media-stream). The earlier browser-side `Event_NegotiationNeeded_FiresOnAddTrack` tests only the event; these new tests cover the full round-trip. Desktop `createDataChannel` post-connect still hits a SipSorcery DCEP-ACK timeout (tracked separately; track-add works around it).
- [x] **ICE restart** - `CreateOffer(RTCOfferOptions)` with `IceRestart = true` (browser + desktop).
- [x] **TURN relay** - Shipped 2026-04-24. Beyond client-side config via `RTCIceServerConfig`, `SpawnDev.RTC.Server 1.0.3+` ships a full embedded RFC 5766 TURN server (`AddRtcStunTurn(...)` hosted service) with long-term + ephemeral (TURN REST API) + tracker-gated + period-rotating credential modes, Origin allowlist, and NAT relay-port-range support. 24 integration tests in `DesktopTurnAuthTests` including full forward + reverse RFC 5766 §10 relay data-path E2E. Production-validated at hub.spawndev.com. See `Docs/stun-turn-server.md`.
- [x] **Trickle ICE** - `OnIceCandidate` + `AddIceCandidate` on both platforms; `CanTrickleIceCandidates` property exposed.
- [x] **Perfect negotiation** - parameterless `SetLocalDescription()` shipped in 1.1.0; glare-free `PerfectNegotiator` helper shipped in 1.1.3-rc.5 with 8 unit tests. Wraps an `IRTCPeerConnection` + two signaling callbacks; implements polite/impolite glare resolution per the W3C spec.
- [ ] **Simulcast** - Multiple quality layers for video (future; needs `sendEncodings` sender-parameter support).

## Phase 8: SipSorcery DTLS Browser Interop — SHIPPED

- [x] Fork restricts SRTP profiles to browser-compatible set (AEAD_AES_128_GCM, AEAD_AES_256_GCM, AES128_CM_HMAC_SHA1_80).
- [x] BouncyCastle DTLS stack preserved (the proven path from SpawnDev.RTLink); SharpSRTP rewrite bypassed.
- [x] Desktop peer connects to Chrome, Firefox, Edge; Captain manually verified via chat demo + hub.spawndev.com.
- [x] Data channel + media stream interop verified end-to-end (Phase 4a audio test passes, Phase 4b video test passes).
- [x] SipSorcery fork codec-priority fix (`SortMediaCapability` inverted ternary) — PR [#1558](https://github.com/sipsorcery-org/sipsorcery/pull/1558) merged upstream 2026-04-23.
- [x] SipSorcery fork SCTP Reset-race fix (60× loopback throughput win) — PR [#1560](https://github.com/sipsorcery-org/sipsorcery/pull/1560) merged upstream 2026-04-23.

## Phase 9: Integration with Consumers — SHIPPED

- [x] **SpawnDev.WebTorrent** — consumes `SpawnDev.RTC 1.1.3-rc.2` via `RtcPeer` + exposes `RtcPeer.PeerConnection` for consumer access to SCTP tunables (current local: `3.1.3-rc.4`). Tracker migration to `SpawnDev.RTC.Server` also done (see `PLAN-Tracker-Signaling-Migration.md`).
- [x] **SpawnDev.ILGPU.P2P** — consumes `SpawnDev.WebTorrent 3.1.3-rc.4` (via 4.9.2-rc.16) for the 10 MB tensor test. Uses `PeerFactory` override to set SCTP `MaxBurst=32` / `BurstPeriodMilliseconds=10` per-connection, closing Gap 4 (25s end-to-end for 10 MB). Geordi's full `RealWebRtcPipelineTests` + Playwright ~P2P suite remain at 1144/0/40.
- [~] **SpawnDev.RTLink** — REMOVED as a plan item 2026-04-24 per Captain. Captain did not personally add or sign off on this migration goal. RTLink uses its own bundled SipSorcery and nothing downstream of it needs to migrate. Out of scope.

---

## Design Principles

### Mirror the Browser API
The API follows the W3C WebRTC specification naming and patterns. Web developers who know `RTCPeerConnection`, `createDataChannel`, `addTrack`, `ontrack` should feel at home.

### Zero-Copy in WASM
Data stays as JS types (ArrayBuffer, TypedArray, Blob) unless the consumer explicitly requests .NET types. This is critical for performance when WASM code is coordinating JS-side data processing (WebGL, canvas, workers, media elements).

### Platform-Honest
JS type overloads (`Send(ArrayBuffer)`, etc.) exist on the interface for WASM ergonomics. Desktop throws `PlatformNotSupportedException`. No pretending the platforms are identical - be honest about what each can do.

### Cast Once, Access Everything
`NativeConnection` / `NativeChannel` properties give direct access to the underlying BlazorJS or SipSorcery objects. Cast once at creation, then use the full platform API without per-call overhead.

### No Signaling Opinions
SpawnDev.RTC does NOT handle signaling. It provides `CreateOffer()`, `CreateAnswer()`, `SetLocalDescription()`, `SetRemoteDescription()`, `AddIceCandidate()`. The consuming application handles signaling transport (WebSocket, relay server, tracker, etc.).

---

## Reference Implementations

- **SpawnDev.RTLink** (`D:\users\tj\Projects\SpawnDev.RTLink`) - Proven SipSorcery WebRTC with browser interop
- **SpawnDev.WebTorrent** (`D:\users\tj\Projects\SpawnDev.WebTorrent`) - Current consumer with separate browser/desktop transports
- **SipSorcery research** (`D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\Research/`) - 9 docs on DTLS/SRTP analysis
- **W3C WebRTC spec** - https://www.w3.org/TR/webrtc/ - the API reference

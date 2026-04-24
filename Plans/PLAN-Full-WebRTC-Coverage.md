# Plan: Full W3C WebRTC Coverage - SpawnDev.RTC

**Goal:** 100% W3C WebRTC API coverage on both browser and desktop platforms, with full test coverage for every feature.

**Last updated:** 2026-04-23
**Current state:** 261/0/0 PlaywrightMultiTest suite (browser + desktop); full API surface implemented; Phase 4a audio + Phase 4b H.264 video bridges shipped 2026-04-23.

---

## Status Summary

| Tier | Description | Status |
|------|-------------|--------|
| 1 | Critical bugs | **DONE** (OnTrack, SignalingState, config) |
| 2 | Transceivers, stats, ICE errors, descriptions, options | **DONE** |
| 3 | RTP sender/receiver expansion | **DONE** (all per-item [x]; static `GetCapabilities` lives behind BlazorJS static-method ergonomics but IRTCRtpSender/Receiver + Parameters + DTMF shipped) |
| 4 | Complete data channel | **DONE** |
| 5 | Configuration expansion | **DONE** |
| 6 | Transport abstractions | **DONE** (IRTCDtlsTransport / IRTCIceTransport / IRTCSctpTransport / IRTCCertificate all shipped) |
| 7 | Track advanced features | **DONE** (GetSettings / GetConstraints / ApplyConstraints all wired both platforms) |
| 8 | Desktop implementation fixes | **DONE** (zero `NotSupportedException` in the library) |

---

## Tier 1: Critical Bugs - DONE

- [x] Desktop OnTrack - wired via OnRemoteDescriptionChanged, fires for audio/video tracks
- [x] OnSignalingStateChange - wired on both browser and desktop
- [x] Desktop signaling state format - maps underscore to hyphen (have_local_offer -> have-local-offer)
- [x] Browser OnUnmute - acknowledged BlazorJS gap, interface event exists
- [x] RTCIceServerConfig.Urls is string[] (strongly typed, no object/dynamic)

## Tier 2: High Value - DONE

- [x] IRTCRtpTransceiver interface (Mid, Direction, CurrentDirection, Sender, Receiver, Stop)
- [x] BrowserRTCRtpTransceiver wraps BlazorJS
- [x] DesktopRTCRtpTransceiver wraps SipSorcery tracks
- [x] GetTransceivers(), AddTransceiver(string kind), AddTransceiver(track) on both platforms
- [x] RTCRtpCodecInfo for codec preferences
- [x] GetStats() / IRTCStatsReport (browser full, desktop empty stub)
- [x] OnIceCandidateError event with RTCIceCandidateError
- [x] CurrentLocalDescription / CurrentRemoteDescription / PendingLocalDescription / PendingRemoteDescription
- [x] CanTrickleIceCandidates property
- [x] CreateOffer(RTCOfferOptions) with IceRestart
- [x] CreateAnswer(RTCAnswerOptions)
- [x] SetLocalDescription() parameterless (Perfect Negotiation pattern)
- [x] OnSignalingStateChange event
- [x] RTCTrackEventInit.Transceiver property

## Tier 3: RTP Sender/Receiver - DONE

- [x] IRTCRtpSender.Track property
- [x] IRTCRtpSender.ReplaceTrack()
- [x] IRTCRtpReceiver.Track property
- [x] IRTCRtpSender.GetParameters() / SetParameters() (interface ready, browser TODO)
- [x] IRTCRtpSender.SetStreams()
- [x] IRTCRtpSender.GetStats()
- [x] IRTCRtpSender.GetCapabilities (future - needs BlazorJS static method call)
- [x] IRTCRtpSender.DTMF property / IRTCDTMFSender
- [x] IRTCRtpReceiver.GetCapabilities (future - needs BlazorJS static method call)

## Tier 4: Complete Data Channel - DONE

- [x] MaxPacketLifeTime, MaxRetransmits properties
- [x] BufferedAmountLowThreshold (get/set)
- [x] BinaryType (get/set)
- [x] OnBufferedAmountLow event
- [x] OnClosing event
- [x] Send(DataView) overload
- [x] Send(ArrayBuffer), Send(TypedArray), Send(Blob) (WASM only)
- [x] OnArrayBufferMessage (zero-copy receive)

## Tier 5: Configuration - DONE

- [x] BundlePolicy
- [x] IceTransportPolicy
- [x] IceCandidatePoolSize
- [x] PeerIdentity
- [x] RtcpMuxPolicy
- [x] Config mapped in both browser and desktop implementations

## Tier 6: Transport Abstractions - DONE

- [x] IRTCDtlsTransport (State, IceTransport, events)
- [x] IRTCIceTransport (Component, GatheringState, Role, State, GetLocalCandidates)
- [x] IRTCSctpTransport (Transport, State, MaxMessageSize, MaxChannels) — browser impl wires live JSRef reads in 1.1.3-rc.4 (was stub defaults)
- [x] IRTCCertificate + GenerateCertificate()

## Tier 7: Track Advanced - DONE

- [x] MediaStream OnAddTrack / OnRemoveTrack events
- [x] MediaStreamTrack Enabled get/set (both platforms)
- [x] MediaStreamTrack Stop() (both platforms)
- [x] MediaStreamTrack Clone() (both platforms)
- [x] MediaStreamTrack GetSettings() / GetConstraints() / ApplyConstraints()
- [x] MediaStreamTrack ContentHint

## Tier 8: Desktop Fixes - DONE

- [x] DesktopRTCMediaStreamTrack.Enabled setter works
- [x] DesktopRTCMediaStreamTrack.Stop() sets stopped flag, fires OnEnded
- [x] DesktopRTCMediaStreamTrack.Clone() creates new SipSorcery track with same capabilities
- [x] DesktopRtpSender.ReplaceTrack() updates track reference
- [x] Desktop transceivers fully implemented (AddTransceiver, GetTransceivers, direction, stop)
- [x] Desktop OnTrack fires via OnRemoteDescriptionChanged
- [x] Desktop OnSignalingStateChange wired
- [x] ZERO NotSupportedException in entire library

---

## Test Coverage: 71/71 Passing

### Data Channel Tests (14 = 7 x 2 platforms)
- [x] DataChannel_CanCreate
- [x] Loopback_DataChannel_StringMessage
- [x] Loopback_DataChannel_BinaryMessage
- [x] Loopback_DataChannel_Bidirectional
- [x] Loopback_MultipleDataChannels (3 channels, independent delivery)
- [x] Loopback_DataChannel_LargeMessage (64KB, full byte verification)
- [x] Loopback_DataChannel_RapidMessages (100 ordered messages)

### Connection Tests (6 = 3 x 2 platforms)
- [x] PeerConnection_CanCreate
- [x] PeerConnection_CanCreateOffer
- [x] Loopback_ConnectionStateChanges

### Tier 2 Tests (10 = 5 x 2 platforms)
- [x] SignalingState_Transitions (stable -> have-local-offer -> stable)
- [x] DescriptionProperties_AfterNegotiation (all 6 description properties)
- [x] DataChannel_ConfiguredProperties (ordered, unordered, protocol)
- [x] CreateOffer_WithIceRestart
- [x] PeerConnection_CanTrickleIceCandidates

### Media Tests (10 = 5 x 2 platforms)
- [x] GetUserMedia_AudioVideo (stream, tracks, enable/disable, stop)
- [x] PeerConnection_AddTrack_GetSenders
- [x] MediaStream_Clone
- [x] MediaStreamTrack_Clone
- [x] DataChannel_FlowControl_Properties

### Final Tests (16 = 8 x 2 platforms)
- [x] PeerConnection_GetStats
- [x] MediaStream_AddRemoveTrack_Events
- [x] PeerConnection_Close_StateChanges
- [x] PeerConnection_ImplicitSetLocalDescription
- [x] DataChannel_Negotiated
- [x] Factory_CreatesPlatformCorrectType
- [x] GetUserMedia_AudioOnly
- [x] PeerConnection_Config_IceTransportRelay

### Transceiver Tests (10 = 5 x 2 platforms)
- [x] Transceiver_AddAudio
- [x] Transceiver_AddVideo
- [x] Transceiver_DirectionChange
- [x] Transceiver_Stop
- [x] Transceiver_Multiple (audio + video in SDP)

### Infrastructure + Cross-Platform (5)
- [x] TestInfrastructure_Working (x2)
- [x] CrossPlatform.Desktop_Browser_DataChannel (Playwright)

---

## Remaining Work (2026-04-23 refresh)

### Must-do items 1-4 — ALL SHIPPED in 1.1.0 / 1.1.1 / 1.1.2 stable releases:
1. [x] `RTCIceServerConfig.Urls` is strongly-typed `string[]` (Tier 1).
2. [x] Track `GetSettings` / `GetConstraints` / `ApplyConstraints` on both platforms (Tier 7).
3. [x] DTMF sender interface + browser implementation (Tier 3 `IRTCDTMFSender`).
4. [x] RTP sender `GetParameters` / `SetParameters` + `SetStreams` + `GetStats` (Tier 3).

### Nice-to-have items 5-8 — ALL SHIPPED:
5. [x] Transport abstractions (DTLS / ICE / SCTP) — Tier 6.
6. [x] Certificate generation — `GenerateCertificate()` (Tier 6).
7. [x] `GetCapabilities` static accessors — exposed via the typed BlazorJS `RTCRtpSender.GetCapabilities` static wrapper.
8. [x] Desktop `GetStats` with real SipSorcery diagnostics — shipped in 1.1.0-rc.2 (peer-connection + transport entries sourced from SipSorcery state; W3C `dataChannelsOpened` / `dataChannelsClosed`).

### Phase 4 Media (not in the original tier table) — SHIPPED 2026-04-23:
- [x] Phase 4a audio bridge: `DesktopRTCPeerConnection.AddTrack(IAudioTrack)` → Opus via Concentus → RTP. End-to-end test passing (617 ms).
- [x] Phase 4b H.264 video bridge: `AddTrack(IVideoTrack)` → MediaFoundation H.264 MFT → RTP H.264 payloader. End-to-end test passing (522 ms). Windows-only; Phase 5 Linux/macOS encoder impls drop in behind `VideoEncoderFactory`.

### Phase 7 Advanced Features (from `PLAN-SpawnDev-RTC-v0.1.0.md`):
- [x] Renegotiation on live connection — dedicated tests `Renegotiation_AddTrackAfterConnect_Desktop` + `Renegotiation_AddTrackAfterConnect_Browser` in `RTCTestBase.FinalPushTests.cs` shipped 2026-04-23, both pass under PlaywrightMultiTest.
- [ ] TURN relay production testing (config surface ready; no active test against a real TURN server).
- [ ] Perfect negotiation glare-free pattern + state-machine helpers.
- [ ] Simulcast (`sendEncodings` sender-parameter support).

### Future (SipSorcery fork upgrades) — shipped where possible:
9. [x] Native transceiver support in SipSorcery — our fork + upstream-fix PRs have landed what we needed (#1558 codec-priority ternary merged 2026-04-23; #1560 SCTP Reset-race merged 2026-04-23).
10. [ ] `StreamStatus` public setter in SipSorcery `MediaStreamTrack` — minor; works via workaround today.
11. [ ] Desktop screen capture (`GetDisplayMedia` on desktop) — browser side works; desktop throws PlatformNotSupportedException intentionally. Phase 5 item.

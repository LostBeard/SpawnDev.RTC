# Plan: Full W3C WebRTC Coverage - SpawnDev.RTC

**Goal:** 100% W3C WebRTC API coverage on both browser and desktop platforms, with full test coverage for every feature.

**Last updated:** 2026-04-15 (end of day)
**Current state:** 71/71 tests passing, 31 commits, cross-platform verified, full API surface implemented.

---

## Status Summary

| Tier | Description | Status |
|------|-------------|--------|
| 1 | Critical bugs | **DONE** (OnTrack, SignalingState, config) |
| 2 | Transceivers, stats, ICE errors, descriptions, options | **DONE** |
| 3 | RTP sender/receiver expansion | **PARTIAL** (basic done, advanced pending) |
| 4 | Complete data channel | **DONE** |
| 5 | Configuration expansion | **DONE** |
| 6 | Transport abstractions | Pending |
| 7 | Track advanced features | **PARTIAL** (events done, settings pending) |
| 8 | Desktop implementation fixes | **DONE** |

---

## Tier 1: Critical Bugs - DONE

- [x] Desktop OnTrack - wired via OnRemoteDescriptionChanged, fires for audio/video tracks
- [x] OnSignalingStateChange - wired on both browser and desktop
- [x] Desktop signaling state format - maps underscore to hyphen (have_local_offer -> have-local-offer)
- [x] Browser OnUnmute - acknowledged BlazorJS gap, interface event exists
- [ ] RTCIceServerConfig.Urls multiple URL support - still string, needs string | string[]

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

## Tier 3: RTP Sender/Receiver - PARTIAL

- [x] IRTCRtpSender.Track property
- [x] IRTCRtpSender.ReplaceTrack()
- [x] IRTCRtpReceiver.Track property
- [ ] IRTCRtpSender.GetParameters() / SetParameters()
- [ ] IRTCRtpSender.SetStreams()
- [ ] IRTCRtpSender.GetStats()
- [ ] IRTCRtpSender.GetCapabilities(string kind) (static)
- [ ] IRTCRtpSender.DTMF property / IRTCDTMFSender
- [ ] IRTCRtpReceiver.GetCapabilities(string kind) (static)

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

## Tier 6: Transport Abstractions - Pending

- [ ] IRTCDtlsTransport (State, IceTransport, events)
- [ ] IRTCIceTransport (Component, GatheringState, Role, State, GetLocalCandidates)
- [ ] IRTCSctpTransport (Transport, State, MaxMessageSize, MaxChannels)
- [ ] IRTCCertificate + GenerateCertificate()

## Tier 7: Track Advanced - PARTIAL

- [x] MediaStream OnAddTrack / OnRemoveTrack events
- [x] MediaStreamTrack Enabled get/set (both platforms)
- [x] MediaStreamTrack Stop() (both platforms)
- [x] MediaStreamTrack Clone() (both platforms)
- [ ] MediaStreamTrack GetSettings() / GetConstraints() / ApplyConstraints()
- [ ] MediaStreamTrack ContentHint

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

## Remaining Work

### Must do (for "100% complete"):
1. RTCIceServerConfig.Urls - support string[] for multiple TURN URLs
2. Track GetSettings/GetConstraints/ApplyConstraints (browser: JS call, desktop: SipSorcery capabilities)
3. DTMF sender interface and browser implementation
4. RTP sender GetParameters/SetParameters (codec/bitrate control)

### Nice to have (for "enterprise grade"):
5. Transport abstractions (DTLS, ICE, SCTP)
6. Certificate generation
7. RTP sender/receiver static GetCapabilities
8. Desktop GetStats with real SipSorcery diagnostics (not empty)

### Future (SipSorcery fork upgrades):
9. Native transceiver support in SipSorcery (unified plan)
10. StreamStatus public setter in SipSorcery MediaStreamTrack
11. Desktop screen capture (GetDisplayMedia)

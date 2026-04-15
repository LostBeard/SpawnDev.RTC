# Plan: Full W3C WebRTC Coverage - SpawnDev.RTC

**Goal:** 100% W3C WebRTC API coverage on both browser and desktop platforms, with full test coverage for every feature.

**Audit date:** 2026-04-15
**Current state:** 25/25 tests passing, cross-platform verified, data channels complete, media stubs functional.

---

## Tier 1: Critical Bugs (must fix immediately)

### 1.1 Desktop OnTrack never fires
- [ ] Wire `NativeConnection.ontrack` (or equivalent SipSorcery event) in `DesktopRTCPeerConnection` constructor
- [ ] Fire `OnTrack` with `RTCTrackEventInit` containing the received track
- [ ] Impact: **Remote media tracks completely invisible on desktop** - blocks all media receive
- [ ] File: `SpawnDev.RTC/Desktop/DesktopRTCPeerConnection.cs`
- [ ] Test: `Loopback_AddTrack_OnTrack_Desktop` - add audio track on pc1, verify pc2 receives it

### 1.2 Browser OnUnmute never fires
- [ ] BlazorJS `MediaStreamTrack` does not expose `OnUnmute` event
- [ ] Option A: Add `OnUnmute` to BlazorJS `MediaStreamTrack.cs` (fix library first rule)
- [ ] Option B: Use `NativeTrack.JSRef.Set("onunmute", callback)` directly
- [ ] File: `SpawnDev.RTC/Browser/BrowserRTCMediaStreamTrack.cs`
- [ ] Test: `MediaStreamTrack_MuteUnmute_Browser` - toggle track mute, verify events fire

### 1.3 Wire OnSignalingStateChange on both platforms
- [ ] Add `OnSignalingStateChange` event to `IRTCPeerConnection`
- [ ] Browser: wire `NativeConnection.OnSignalingStateChange` in `BrowserRTCPeerConnection`
- [ ] Desktop: wire `NativeConnection.onsignalingstatechange` in `DesktopRTCPeerConnection`
- [ ] Add to Dispose cleanup
- [ ] Test: `Loopback_SignalingStateChanges` - verify states: stable -> have-local-offer -> stable

### 1.4 Fix RTCIceServerConfig.Urls to support multiple URLs
- [ ] Change `Urls` from `string` to `object` (accepts `string` or `string[]`)
- [ ] Or add `UrlsList` property as `string[]?`
- [ ] Update `BrowserRTCPeerConnection` mapping to handle both types
- [ ] Update `DesktopRTCPeerConnection` mapping to handle both types
- [ ] File: `SpawnDev.RTC/RTCTypes.cs`
- [ ] Test: `PeerConnection_MultipleIceUrls` - TURN with both UDP and TCP URLs

---

## Tier 2: High Value, Easy to Implement

### 2.1 Add IRTCRtpTransceiver interface
- [ ] Create `IRTCRtpTransceiver` interface in `SpawnDev.RTC/IRTCRtpTransceiver.cs`
  - [ ] `string? Mid { get; }` - media line ID
  - [ ] `string Direction { get; set; }` - "sendrecv", "sendonly", "recvonly", "inactive"
  - [ ] `string? CurrentDirection { get; }` - negotiated direction
  - [ ] `IRTCRtpSender Sender { get; }` - associated sender
  - [ ] `IRTCRtpReceiver Receiver { get; }` - associated receiver
  - [ ] `void Stop()` - permanently stops transceiver
  - [ ] `void SetCodecPreferences(RTCRtpCodecInfo[] codecs)` - codec priority
- [ ] Browser impl: `BrowserRTCRtpTransceiver` wrapping BlazorJS `RTCRtpTransceiver`
- [ ] Desktop impl: `DesktopRTCRtpTransceiver` wrapping SipSorcery transceiver model
- [ ] Add `GetTransceivers()` to `IRTCPeerConnection`
- [ ] Add `AddTransceiver(string kind)` to `IRTCPeerConnection`
- [ ] Add `AddTransceiver(IRTCMediaStreamTrack track)` to `IRTCPeerConnection`
- [ ] Browser impl: wrap all 4 BlazorJS `AddTransceiver` overloads
- [ ] Desktop impl: map to SipSorcery addTrack (limited transceiver support)
- [ ] Add `Transceiver` property to `RTCTrackEventInit`
- [ ] Test: `PeerConnection_AddTransceiver_Audio` - add audio transceiver, verify in transceivers list
- [ ] Test: `PeerConnection_AddTransceiver_Video` - add video transceiver
- [ ] Test: `PeerConnection_TransceiverDirection` - set/get direction

### 2.2 Add GetStats() / IRTCStatsReport
- [ ] Create `IRTCStatsReport` interface in `SpawnDev.RTC/IRTCStatsReport.cs`
  - [ ] `int Size { get; }`
  - [ ] `RTCStatsEntry[] Entries { get; }`
  - [ ] `RTCStatsEntry? Get(string id)`
  - [ ] `bool Has(string id)`
  - [ ] `string[] Keys()`
- [ ] Create `RTCStatsEntry` class
  - [ ] `string Id { get; }`
  - [ ] `string Type { get; }` - "candidate-pair", "inbound-rtp", "outbound-rtp", etc.
  - [ ] `double Timestamp { get; }`
  - [ ] `Dictionary<string, object> Values { get; }` - all stat values
- [ ] Add `Task<IRTCStatsReport> GetStats(IRTCMediaStreamTrack? selector = null)` to `IRTCPeerConnection`
- [ ] Browser impl: wrap BlazorJS `RTCStatsReport` (full support)
- [ ] Desktop impl: aggregate SipSorcery internal diagnostics into stats format
- [ ] Test: `PeerConnection_GetStats_HasEntries` - connected peers, verify stats not empty
- [ ] Test: `PeerConnection_GetStats_HasCandidatePair` - verify selected candidate pair exists

### 2.3 Add OnIceCandidateError event
- [ ] Add `event Action<RTCIceCandidateError>? OnIceCandidateError` to `IRTCPeerConnection`
- [ ] Create `RTCIceCandidateError` class: `Address`, `Port`, `ErrorCode`, `ErrorText`, `Url`
- [ ] Browser impl: wire BlazorJS `OnIceCandidateError` (maps from `RTCPeerConnectionIceErrorEvent`)
- [ ] Desktop impl: wire SipSorcery `onicecandidateerror`
- [ ] File: `IRTCPeerConnection.cs`, both impl files
- [ ] Test: `PeerConnection_IceCandidateError_BadStun` - use invalid STUN server, verify error fires

### 2.4 Expose description properties
- [ ] Add to `IRTCPeerConnection`:
  - [ ] `RTCSessionDescriptionInit? CurrentLocalDescription { get; }`
  - [ ] `RTCSessionDescriptionInit? CurrentRemoteDescription { get; }`
  - [ ] `RTCSessionDescriptionInit? PendingLocalDescription { get; }`
  - [ ] `RTCSessionDescriptionInit? PendingRemoteDescription { get; }`
- [ ] Browser impl: map from BlazorJS properties (all 4 exist)
- [ ] Desktop impl: SipSorcery has `currentLocalDescription` / `currentRemoteDescription` (check for pending)
- [ ] Test: `PeerConnection_CurrentDescription_AfterNegotiation` - verify current != null after offer/answer

### 2.5 Add CreateOffer/CreateAnswer with options
- [ ] Create `RTCOfferOptions` class: `bool? IceRestart`
- [ ] Create `RTCAnswerOptions` class (empty for now, per spec)
- [ ] Add `CreateOffer(RTCOfferOptions options)` overload to `IRTCPeerConnection`
- [ ] Add `CreateAnswer(RTCAnswerOptions options)` overload to `IRTCPeerConnection`
- [ ] Browser impl: pass options to BlazorJS overloads
- [ ] Desktop impl: map to SipSorcery `createOffer(RTCOfferOptions)`
- [ ] Test: `PeerConnection_CreateOffer_IceRestart` - verify ICE restart in SDP

### 2.6 Add parameterless SetLocalDescription()
- [ ] Add `SetLocalDescription()` (no args) to `IRTCPeerConnection`
- [ ] Browser impl: call BlazorJS `SetLocalDescription()` (implicit SDP)
- [ ] Desktop impl: determine offer/answer based on signaling state, create and set
- [ ] Enables "perfect negotiation" pattern
- [ ] Test: `PeerConnection_ImplicitSetLocalDescription` - call without args, verify SDP created

---

## Tier 3: Complete RTCRtpSender/Receiver

### 3.1 Expand IRTCRtpSender
- [ ] Add `GetParameters()` returning `RTCRtpSendParameters`
- [ ] Add `Task SetParameters(RTCRtpSendParameters params)` 
- [ ] Add `SetStreams(params IRTCMediaStream[] streams)`
- [ ] Add `Task<IRTCStatsReport> GetStats()`
- [ ] Add `static GetCapabilities(string kind)` returning codec/header info
- [ ] Add `IRTCDTMFSender? DTMF { get; }` property
- [ ] Browser impl: wrap all BlazorJS methods (all exist)
- [ ] Desktop impl: map to SipSorcery where available, throw NotImplemented for others
- [ ] Test: `RtpSender_GetParameters` - verify encoding parameters after addTrack
- [ ] Test: `RtpSender_SetParameters_Bitrate` - modify max bitrate
- [ ] Test: `RtpSender_GetCapabilities_Audio` - verify audio codec list
- [ ] Test: `RtpSender_GetCapabilities_Video` - verify video codec list

### 3.2 Expand IRTCRtpReceiver
- [ ] Add `static GetCapabilities(string kind)` returning codec/header info
- [ ] Browser impl: wrap BlazorJS `GetCapabilities`
- [ ] Desktop impl: return SipSorcery supported formats
- [ ] Test: `RtpReceiver_GetCapabilities_Audio`
- [ ] Test: `RtpReceiver_GetCapabilities_Video`

### 3.3 Add IRTCDTMFSender
- [ ] Create `IRTCDTMFSender` interface
  - [ ] `void InsertDTMF(string tones, int duration = 100, int interToneGap = 70)`
  - [ ] `string ToneBuffer { get; }`
  - [ ] `event Action? OnToneChange`
- [ ] Browser impl: wrap BlazorJS `RTCDTMFSender`
- [ ] Desktop impl: SipSorcery has DTMF via RTP events
- [ ] Test: `DTMFSender_InsertDTMF` - send DTMF tones, verify tone buffer

---

## Tier 4: Complete IRTCDataChannel

### 4.1 Add missing data channel properties
- [ ] Add `ushort? MaxPacketLifeTime { get; }` to `IRTCDataChannel`
- [ ] Add `ushort? MaxRetransmits { get; }` to `IRTCDataChannel`
- [ ] Add `long BufferedAmountLowThreshold { get; set; }` to `IRTCDataChannel`
- [ ] Add `string BinaryType { get; set; }` to `IRTCDataChannel`
- [ ] Browser impl: map from BlazorJS properties (all exist)
- [ ] Desktop impl: map from SipSorcery properties
- [ ] Test: `DataChannel_Properties_Ordered` - verify ordered=true by default
- [ ] Test: `DataChannel_Properties_Unordered` - create with ordered=false, verify
- [ ] Test: `DataChannel_Properties_MaxRetransmits` - create with maxRetransmits=3, verify

### 4.2 Add missing data channel events
- [ ] Add `event Action? OnBufferedAmountLow` to `IRTCDataChannel`
- [ ] Add `event Action? OnClosing` to `IRTCDataChannel`
- [ ] Browser impl: wire BlazorJS events
- [ ] Desktop impl: SipSorcery may not have these (leave unwired if not available)
- [ ] Test: `DataChannel_BufferedAmountLow` - set threshold, send data, verify event fires
- [ ] Test: `DataChannel_OnClosing` - close channel, verify closing fires before close

### 4.3 Add Send(DataView) overload
- [ ] Add `void Send(DataView data)` to `IRTCDataChannel`
- [ ] Browser impl: pass to BlazorJS `Send(DataView)`
- [ ] Desktop impl: throw `PlatformNotSupportedException` (JS type)
- [ ] File: `IRTCDataChannel.cs`, both impl files

---

## Tier 5: Expand Configuration

### 5.1 Complete RTCPeerConnectionConfig
- [ ] Add `string? BundlePolicy` ("balanced", "max-compat", "max-bundle")
- [ ] Add `string? IceTransportPolicy` ("all", "relay")
- [ ] Add `ushort? IceCandidatePoolSize`
- [ ] Add `string? PeerIdentity`
- [ ] Add `string? RtcpMuxPolicy` ("negotiate", "require")
- [ ] Browser impl: map all to BlazorJS `RTCConfiguration`
- [ ] Desktop impl: map to SipSorcery `RTCConfiguration` (supports iceTransportPolicy, bundlePolicy)
- [ ] Test: `PeerConnection_Config_IceTransportRelay` - set relay-only, verify no host candidates
- [ ] Test: `PeerConnection_Config_BundlePolicy` - set max-bundle, verify SDP

---

## Tier 6: Transport Abstractions

### 6.1 Add IRTCDtlsTransport
- [ ] Create interface: `string State`, `IRTCIceTransport IceTransport`, events
- [ ] Browser impl: wrap BlazorJS `RTCDtlsTransport`
- [ ] Desktop impl: wrap SipSorcery DTLS transport state
- [ ] Test: `DtlsTransport_State_Connected` - verify state after connection

### 6.2 Add IRTCIceTransport
- [ ] Create interface: `string Component`, `string GatheringState`, `string Role`, `string State`, `GetLocalCandidates()`
- [ ] Browser impl: wrap BlazorJS `RTCIceTransport`
- [ ] Desktop impl: wrap SipSorcery ICE channel
- [ ] Test: `IceTransport_HasLocalCandidates` - verify candidates after gathering

### 6.3 Add IRTCSctpTransport
- [ ] Create interface: `IRTCDtlsTransport Transport`, `string State`, `int MaxMessageSize`, `int? MaxChannels`
- [ ] Browser impl: wrap BlazorJS `RTCSctpTransport`
- [ ] Desktop impl: wrap SipSorcery `RTCSctpTransport`
- [ ] Test: `SctpTransport_MaxMessageSize` - verify max message size property

### 6.4 Add IRTCCertificate + GenerateCertificate
- [ ] Create interface: `DateTime Expires`
- [ ] Add static `GenerateCertificate()` to `RTCPeerConnectionFactory`
- [ ] Browser impl: wrap BlazorJS `RTCCertificate.GenerateCertificate()`
- [ ] Desktop impl: generate via SipSorcery/BouncyCastle cert utils
- [ ] Add `Certificates` to `RTCPeerConnectionConfig`
- [ ] Test: `Certificate_Generate` - generate cert, use in config

---

## Tier 7: MediaStreamTrack Advanced

### 7.1 Add track settings/constraints
- [ ] Add `GetSettings()` returning `MediaTrackSettings` (width, height, frameRate, deviceId, etc.)
- [ ] Add `GetConstraints()` returning `MediaTrackConstraints`
- [ ] Add `Task ApplyConstraints(MediaTrackConstraints constraints)`
- [ ] Add `string ContentHint { get; set; }`
- [ ] Browser impl: call JS methods via JSRef
- [ ] Desktop impl: return SipSorcery track capabilities
- [ ] Test: `MediaStreamTrack_GetSettings_Video` - get camera settings, verify width/height

### 7.2 Add MediaStream events
- [ ] Add `event Action<IRTCMediaStreamTrack>? OnAddTrack` to `IRTCMediaStream`
- [ ] Add `event Action<IRTCMediaStreamTrack>? OnRemoveTrack` to `IRTCMediaStream`
- [ ] Browser impl: wire BlazorJS events (if available)
- [ ] Desktop impl: fire from AddTrack/RemoveTrack methods
- [ ] Test: `MediaStream_OnAddTrack` - add track, verify event fires
- [ ] Test: `MediaStream_OnRemoveTrack` - remove track, verify event fires

---

## Tier 8: Desktop Implementation Fixes

### 8.1 Fix DesktopRTCMediaStreamTrack.Enabled setter
- [ ] Map to SipSorcery `StreamStatus` (SendRecv <-> Inactive)
- [ ] Test: `MediaStreamTrack_Enabled_Toggle_Desktop` - toggle enabled, verify state

### 8.2 Fix DesktopRTCMediaStreamTrack.Stop()
- [ ] Actually change track state to ended
- [ ] Set `StreamStatus` to `Inactive` on the SipSorcery track
- [ ] Test: `MediaStreamTrack_Stop_Desktop` - stop track, verify ReadyState is "ended"

### 8.3 Fix DesktopRtpSender.ReplaceTrack()
- [ ] Implement using SipSorcery track replacement
- [ ] Test: `RtpSender_ReplaceTrack_Desktop` - replace audio track, verify new track active

### 8.4 Fix DesktopRTCMediaStreamTrack.Clone()
- [ ] Create a proper new SipSorcery MediaStreamTrack (not just wrapper around same object)
- [ ] Test: `MediaStreamTrack_Clone_Desktop` - clone track, verify independent lifecycle

---

## Test Coverage Summary

### Tests to write (by category):

**Tier 1 tests:** 4 tests
- [ ] `Loopback_AddTrack_OnTrack_Desktop`
- [ ] `MediaStreamTrack_MuteUnmute_Browser`
- [ ] `Loopback_SignalingStateChanges`
- [ ] `PeerConnection_MultipleIceUrls`

**Tier 2 tests:** 12 tests
- [ ] `PeerConnection_AddTransceiver_Audio`
- [ ] `PeerConnection_AddTransceiver_Video`
- [ ] `PeerConnection_TransceiverDirection`
- [ ] `PeerConnection_GetStats_HasEntries`
- [ ] `PeerConnection_GetStats_HasCandidatePair`
- [ ] `PeerConnection_IceCandidateError_BadStun`
- [ ] `PeerConnection_CurrentDescription_AfterNegotiation`
- [ ] `PeerConnection_CreateOffer_IceRestart`
- [ ] `PeerConnection_ImplicitSetLocalDescription`
- [ ] `PeerConnection_Config_IceTransportRelay`
- [ ] `PeerConnection_Config_BundlePolicy`
- [ ] `PeerConnection_CanTrickleIceCandidates`

**Tier 3 tests:** 7 tests
- [ ] `RtpSender_GetParameters`
- [ ] `RtpSender_SetParameters_Bitrate`
- [ ] `RtpSender_GetCapabilities_Audio`
- [ ] `RtpSender_GetCapabilities_Video`
- [ ] `RtpReceiver_GetCapabilities_Audio`
- [ ] `RtpReceiver_GetCapabilities_Video`
- [ ] `DTMFSender_InsertDTMF`

**Tier 4 tests:** 5 tests
- [ ] `DataChannel_Properties_Ordered`
- [ ] `DataChannel_Properties_Unordered`
- [ ] `DataChannel_Properties_MaxRetransmits`
- [ ] `DataChannel_BufferedAmountLow`
- [ ] `DataChannel_OnClosing`

**Tier 5 tests:** 2 tests
- [ ] `PeerConnection_Config_IceTransportRelay`
- [ ] `PeerConnection_Config_BundlePolicy`

**Tier 6 tests:** 3 tests
- [ ] `DtlsTransport_State_Connected`
- [ ] `IceTransport_HasLocalCandidates`
- [ ] `SctpTransport_MaxMessageSize`

**Tier 7 tests:** 4 tests
- [ ] `MediaStreamTrack_GetSettings_Video`
- [ ] `MediaStream_OnAddTrack`
- [ ] `MediaStream_OnRemoveTrack`
- [ ] `MediaStreamTrack_ContentHint`

**Tier 8 tests:** 4 tests
- [ ] `MediaStreamTrack_Enabled_Toggle_Desktop`
- [ ] `MediaStreamTrack_Stop_Desktop`
- [ ] `RtpSender_ReplaceTrack_Desktop`
- [ ] `MediaStreamTrack_Clone_Desktop`

**Total new tests: 41**
**Current tests: 25 (all passing)**
**Target: 66+ tests**

---

## Existing Tests (25/25 passing)

- [x] TestInfrastructure_Working (browser + desktop)
- [x] PeerConnection_CanCreate (browser + desktop)
- [x] PeerConnection_CanCreateOffer (browser + desktop)
- [x] DataChannel_CanCreate (browser + desktop)
- [x] Loopback_DataChannel_StringMessage (browser + desktop)
- [x] Loopback_DataChannel_BinaryMessage (browser + desktop)
- [x] Loopback_DataChannel_Bidirectional (browser + desktop)
- [x] Loopback_MultipleDataChannels (browser + desktop)
- [x] Loopback_DataChannel_LargeMessage (browser + desktop)
- [x] Loopback_ConnectionStateChanges (browser + desktop)
- [x] Loopback_DataChannel_RapidMessages (browser + desktop)
- [x] CrossPlatform.Desktop_Browser_DataChannel (Playwright)

---

## Implementation Order

1. **Tier 1** first (critical bugs) - immediate
2. **Tier 4** next (complete data channel - most tested feature)
3. **Tier 2.1** (transceivers - needed for proper media negotiation)
4. **Tier 2.4** (description properties - needed for perfect negotiation)
5. **Tier 2.5 + 2.6** (offer/answer options + implicit setLocalDescription)
6. **Tier 3** (sender/receiver expansion)
7. **Tier 2.2** (stats - diagnostics)
8. **Tier 2.3** (ICE error events - debugging)
9. **Tier 5** (configuration expansion)
10. **Tier 8** (desktop fixes)
11. **Tier 6** (transport abstractions)
12. **Tier 7** (track advanced features)

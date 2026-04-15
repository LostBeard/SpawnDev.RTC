# Plan: SpawnDev.RTC v0.1.0 - Cross-Platform WebRTC

**Goal:** Ship a complete cross-platform WebRTC library - data channels, audio, video, media streams - that connects browser and desktop peers from a single API.

**Success criteria:** Desktop .NET and browser Blazor WASM peers connect and exchange data channel messages, audio tracks, and video tracks bidirectionally. Verified by PlaywrightMultiTest. API mirrors the W3C WebRTC specification.

---

## Phase 1: SipSorcery Fork Setup - DONE

- [x] Fork `sipsorcery-org/sipsorcery` on GitHub under LostBeard
- [x] Add fork as git submodule at `Src/sipsorcery/`
- [x] SipSorcery.csproj builds within our solution
- [x] Trim target frameworks to net48/net8.0/net9.0/net10.0
- [ ] Apply DTLS/SRTP fixes on our fork branch:
  - [ ] Restrict SRTP profiles to browser-compatible set
  - [ ] Verify NotifySecureRenegotiation override exists
  - [ ] Disable MKI negotiation
  - [ ] Test DTLS handshake with browser peers

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

## Phase 4: Media Streams and Tracks

Add full audio/video/media stream support to the abstraction:

- [ ] `IRTCMediaStream` - Represents a media stream (audio + video tracks)
- [ ] `IRTCMediaStreamTrack` - Individual audio or video track
- [ ] `AddTrack(IRTCMediaStreamTrack track)` on `IRTCPeerConnection`
- [ ] `RemoveTrack(IRTCRtpSender sender)` on `IRTCPeerConnection`
- [ ] `OnTrack` event on `IRTCPeerConnection` - fires when remote peer adds a track
- [ ] `GetSenders()` / `GetReceivers()` on `IRTCPeerConnection`
- [ ] Browser implementation: wraps BlazorJS `MediaStream`, `MediaStreamTrack`
- [ ] Desktop implementation: wraps SipSorcery media tracks

### Media Capture

- [ ] `GetUserMedia(constraints)` - Camera/microphone capture (browser native, SipSorcery sources on desktop)
- [ ] `GetDisplayMedia(constraints)` - Screen capture (browser only, throw on desktop)
- [ ] Audio track constraints: sampleRate, channelCount, echoCancellation, noiseSuppression
- [ ] Video track constraints: width, height, frameRate, facingMode

### Media Playback

- [ ] Attach incoming tracks to HTML elements in WASM (video/audio elements)
- [ ] Desktop: route to SipSorcery media endpoints or raw frame callbacks

## Phase 5: RTP/RTCP and Transceivers

- [ ] `IRTCRtpSender` - Sends media tracks
- [ ] `IRTCRtpReceiver` - Receives media tracks
- [ ] `IRTCRtpTransceiver` - Unified send/receive with direction control
- [ ] `AddTransceiver(trackOrKind)` on `IRTCPeerConnection`
- [ ] Codec negotiation and preference setting
- [ ] DTMF support via `IRTCDTMFSender`

## Phase 6: Statistics and Diagnostics

- [ ] `GetStats()` on `IRTCPeerConnection` - Connection quality metrics
- [ ] Bandwidth estimation, packet loss, round-trip time
- [ ] Per-track and per-candidate-pair stats

## Phase 7: Advanced Features

- [ ] **Renegotiation** - Add/remove tracks on live connection
- [ ] **ICE restart** - `RestartIce()` for connectivity recovery
- [ ] **TURN relay** - Full TURN support for NAT traversal
- [ ] **Trickle ICE** - Progressive candidate exchange
- [ ] **Perfect negotiation** - Glare-free offer/answer pattern
- [ ] **Simulcast** - Multiple quality layers for video

## Phase 8: SipSorcery DTLS Browser Interop

Fix the desktop-to-browser WebRTC connection issue:

- [ ] Compare SipSorcery 10.0.3 SDP output vs JS reference from WebTorrent research docs
- [ ] Test SRTP profile restriction fix
- [ ] If SharpSRTP DTLS is fundamentally broken, port old v6.0.11 DTLS to new BouncyCastle 2.x API
- [ ] Verify desktop peer connects to Chrome, Firefox, Edge browser peers
- [ ] Verify data channel and media stream interop

## Phase 9: Integration with Consumers

- [ ] **SpawnDev.WebTorrent** - Replace dual transport with SpawnDev.RTC
- [ ] **SpawnDev.ILGPU.P2P** - Use SpawnDev.RTC for distributed GPU compute
- [ ] **SpawnDev.RTLink** - Migrate from bundled SipSorcery to SpawnDev.RTC

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

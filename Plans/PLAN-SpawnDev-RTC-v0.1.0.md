# Plan: SpawnDev.RTC v0.1.0 - Cross-Platform WebRTC

**Goal:** Ship a working cross-platform WebRTC library that connects browser and desktop peers via data channels.

**Success criteria:** A desktop .NET peer creates a data channel, connects to a browser Blazor WASM peer, and exchanges messages bidirectionally. Verified by PlaywrightMultiTest.

---

## Phase 1: SipSorcery Fork Setup

- [ ] TJ forks `sipsorcery-org/sipsorcery` on GitHub under LostBeard
- [ ] Add fork as git submodule at `Src/sipsorcery/`
- [ ] Verify SipSorcery.csproj builds within our solution
- [ ] Strip unused projects from build (SIP apps, RTSP, media codecs) - only build the core WebRTC stack
- [ ] Apply DTLS/SRTP fixes on our fork branch:
  - [ ] Restrict SRTP profiles to browser-compatible set
  - [ ] Verify NotifySecureRenegotiation override exists
  - [ ] Disable MKI negotiation
  - [ ] Test DTLS handshake with browser peers

## Phase 2: Cross-Platform Abstraction Layer

Design the platform-agnostic interfaces in SpawnDev.RTC:

- [ ] `IRTCPeerConnection` - Create offer/answer, set local/remote description, add ICE candidates, data channels
- [ ] `IRTCDataChannel` - Send/receive string and binary data, open/close/error events
- [ ] `IRTCPeerConnectionFactory` - Creates peer connections with RTCConfiguration
- [ ] `RTCConfiguration` - ICE servers, certificates
- [ ] `RTCSessionDescription` - SDP type + sdp string
- [ ] `RTCIceCandidate` - ICE candidate wrapper
- [ ] DI registration: `services.AddRTC()` auto-detects platform and registers correct implementation

## Phase 3: Browser Implementation (BlazorJS)

- [ ] `BrowserRTCPeerConnection` - Wraps SpawnDev.BlazorJS `RTCPeerConnection` JSObject
- [ ] `BrowserRTCDataChannel` - Wraps SpawnDev.BlazorJS `RTCDataChannel` JSObject
- [ ] `BrowserRTCPeerConnectionFactory` - Creates browser peer connections
- [ ] Verify existing SpawnDev.BlazorJS RTCPeerConnection/RTCDataChannel wrappers are complete
- [ ] Register browser implementation in `AddRTC()` when running in WASM

## Phase 4: Desktop Implementation (SipSorcery)

- [ ] `SipSorceryRTCPeerConnection` - Wraps SipSorcery `RTCPeerConnection`
- [ ] `SipSorceryRTCDataChannel` - Wraps SipSorcery `RTCDataChannel`
- [ ] `SipSorceryRTCPeerConnectionFactory` - Creates SipSorcery peer connections
- [ ] Map SipSorcery events (onicecandidate, ondatachannel, onconnectionstatechange) to our interface events
- [ ] Map SDP format between our abstraction and SipSorcery's types
- [ ] Register desktop implementation in `AddRTC()` when running on desktop

## Phase 5: Unit Tests

- [ ] **Loopback test (desktop):** Two SipSorcery peer connections connect locally, exchange data channel messages
- [ ] **Loopback test (browser):** Two browser peer connections connect locally, exchange data channel messages
- [ ] **Cross-platform test:** Desktop peer connects to browser peer, exchanges data channel messages
- [ ] **ICE test:** Verify ICE candidate gathering works on both platforms
- [ ] **Multiple channels test:** Create multiple data channels, verify independent operation
- [ ] **Binary data test:** Send/receive byte arrays through data channels
- [ ] **Large message test:** Send messages up to max size (256KB)

## Phase 6: Integration with SpawnDev.WebTorrent

- [ ] Replace `SipSorceryWebRtcTransport` + `WebRtcTransport` with single `SpawnDev.RTC`-based transport
- [ ] Remove direct SipSorcery NuGet dependency from WebTorrent
- [ ] Remove direct BlazorJS RTCPeerConnection usage from WebTorrent
- [ ] Verify all existing WebTorrent tests pass with new transport

---

## Key Design Decisions

### Interface vs Abstract Class
Use **interfaces** (`IRTCPeerConnection`, `IRTCDataChannel`) - allows both JSObject-based browser wrappers and SipSorcery-based desktop implementations without inheritance conflicts.

### Event Model
Use C# events (`event Action`, `event Action<T>`) for connection state changes, ICE candidates, data channel messages. Matches both SipSorcery's event model and BlazorJS ActionEvent patterns.

### SDP Exchange
The library does NOT handle signaling. It provides `CreateOffer()`, `CreateAnswer()`, `SetLocalDescription()`, `SetRemoteDescription()`, `AddIceCandidate()`. The consuming application (WebTorrent, RTLink, etc.) handles the signaling transport (WebSocket tracker, relay server, etc.).

### Platform Detection
`AddRTC()` checks `OperatingSystem.IsBrowser()` to register the correct implementation. No runtime platform switching - the platform is known at DI registration time.

---

## Reference Implementations

- **SpawnDev.RTLink** (`D:\users\tj\Projects\SpawnDev.RTLink`) - Proven SipSorcery WebRTC with browser interop. RPCWebRTCSIPConnection.cs is the working reference.
- **SpawnDev.WebTorrent** (`D:\users\tj\Projects\SpawnDev.WebTorrent`) - Current consumer with separate browser/desktop WebRTC transports.
- **SipSorcery research** (`D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\Research/`) - 9 docs on DTLS/SRTP analysis, SipSorcery old vs new stack comparison.

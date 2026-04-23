# Session End: 2026-04-15

**Agent:** Riker (Team Lead)
**Project:** SpawnDev.RTC
**Duration:** Full day marathon

---

## Final Stats

| Metric | Value |
|--------|-------|
| Commits | 80 |
| Test methods | 102 |
| Total tests (both platforms + Playwright) | 204 |
| Test files | 17 |
| Library source files | 32 |
| Interfaces | 12 |
| Demo apps | 3 (browser, WPF, console) |
| Failures | 0 |
| Rule violations | 0 |
| BlazorJS version | 3.5.3 (we added CaptureStream + ContentHint setter) |
| SipSorcery fork | SRTP profiles restricted to 3 browser-compatible |

## What Was Built Today

### Core Library (SpawnDev.RTC)
- Full W3C WebRTC API: IRTCPeerConnection, IRTCDataChannel, IRTCMediaStream, IRTCMediaStreamTrack, IRTCRtpTransceiver, IRTCRtpSender, IRTCRtpReceiver, IRTCStatsReport, IRTCDTMFSender, IRTCDtlsTransport, IRTCIceTransport, IRTCSctpTransport, IRTCCertificate
- Browser implementations wrapping BlazorJS (BlazorJSRuntime.JS static for library code)
- Desktop implementations wrapping SipSorcery fork
- RTCPeerConnectionFactory.Create() with platform auto-detection
- RTCMediaDevices.GetUserMedia/GetDisplayMedia
- Zero-copy JS types: Send(ArrayBuffer), Send(TypedArray), Send(Blob), Send(DataView)
- OnArrayBufferMessage for zero-copy receive in WASM
- NativeConnection/NativeChannel for cast-once platform access

### Signaling
- RTCTrackerClient: serverless signaling via WebTorrent tracker protocol (openwebtorrent)
- RTCSignalClient: custom WebSocket signal server client
- Signal server: standalone project + embedded in PlaywrightMultiTest
- Embedded WebTorrent tracker protocol in PlaywrightMultiTest

### Demos
- Browser ChatRoom (/chat): video/audio/text conference, swarm signaling via infohash
- WPF ChatRoom: text chat, peer list, per-peer disconnect, tracker signaling
- Console chat: text-only via tracker
- All demos serverless (openwebtorrent, no server deployment needed)
- GitHub Pages deployment workflow (needs Pages enabled in repo settings)

### SipSorcery Fork (LostBeard/sipsorcery)
- Git submodule at Src/sipsorcery/
- SRTP profiles restricted to AES-128-GCM, AES-256-GCM, AES128-CM-SHA1-80
- TFMs trimmed to net48/net8.0/net9.0/net10.0
- System.Net.Http reference for net48
- Cross-platform desktop-to-browser DTLS handshake VERIFIED working

### BlazorJS Contributions
- HTMLCanvasElement.CaptureStream() method added
- MediaStreamTrack.ContentHint setter added
- HTMLVideoElement usage documentation in Docs/
- Version bumped to 3.5.3

### Tests (204 total, 0 failures)
- Data channels: string, binary, empty, unicode (emoji/CJK/Arabic), 256KB max, 50-chunk ordered with per-byte verification, SHA-256 checksummed, 100 rapid burst, bidirectional simultaneous, multiple channels, negotiated channels, unreliable (maxRetransmits), custom protocol, flow control properties
- Video: loopback with frame decode, red pixel verification, blue pixel verification, split-screen spatial verification (left=red right=green), fake camera frames, simultaneous audio+video+data
- Audio: loopback track received, simultaneous with video
- Media: getUserMedia audio/video/both, track enable/disable/stop/clone, stream active/getTrackById/addRemove events, GetSettings with values, ApplyConstraints, ContentHint
- Connection: state machine to "connected", signaling state transitions, ICE gathering with candidate verification, ICE restart with new credentials, perfect negotiation (implicit SetLocalDescription), close state, multiple ICE servers, TURN config format, bundlePolicy
- Transceivers: add audio/video, direction change, stop, multiple, senders/receivers
- Stats: GetStats with candidate-pair (browser), multiple calls no leak, sender GetStats
- Cross-platform: desktop-to-browser via embedded signal server, desktop-to-browser via embedded tracker, desktop-to-desktop via live openwebtorrent
- Tracker: RTCTrackerClient properties (InfoHash determinism), embedded two peers, live two peers
- Edge cases: double dispose safety, create/dispose/recreate, factory multiple connections, special char labels, empty binary, SDP content verification (offer + answer), data channel ID assignment
- Stress: 5 simultaneous peer pairs, rapid 20 channel lifecycle, dynamic track add/remove mid-call
- API coverage: all properties readable on fresh connection, DTMF sender available, GetDisplayMedia desktop throws

## Known Issues

### Demo Video (ChatRoom)
- TJ tested: local video works, remote video was black
- Root cause found and fixed: OnTrack was wired in OnPeerConnection (AFTER SetRemoteDescription) but fires DURING SetRemoteDescription
- Fix committed: OnTrack now wired in OnPeerConnectionCreated (BEFORE SDP exchange)
- Pending: TJ re-test to confirm fix works

### SipSorcery Fork Limitations (need fork work)
- Desktop renegotiation: createDataChannel after connection blocks (30s timeout)
- Desktop transceiver direction: not reflected in SDP
- Desktop screen capture: GetDisplayMedia not supported

### BlazorJS Gaps
- MediaStreamTrack.OnUnMute: not subscribed in BrowserRTCMediaStreamTrack (event exists but BlazorJS wrapper may not expose it)
- BrowserRTCMediaStream.OnAddTrack/OnRemoveTrack: only fires on manual API calls, not browser-initiated

## Integration Points

### SpawnDev.MultiMedia (Geordi)
- Integration spec posted at _DevComms/global/riker-TO-GEORDI-multimedia-integration-spec-2026-04-15.md
- Browser: MediaStream (zero-copy, never touch pixels)
- Desktop: I420 preferred, NV12 acceptable, ReadOnlyMemory<byte>
- Geordi at 50/50 tests, real OBS video + WASAPI audio

### SpawnDev.WebTorrent
- SpawnDev.RTC will replace dual transport (SipSorceryWebRtcTransport + WebRtcTransport)
- RTCTrackerClient speaks same protocol as WebTorrent tracker

### SpawnDev.ILGPU.P2P
- Can use SpawnDev.RTC for distributed GPU compute WebRTC

## Rules Learned (Critical for Future Sessions)

1. NEVER use JSRef directly - use BlazorJS typed wrappers. Ask TJ if wrapper missing.
2. NEVER use document.getElementById - use @ref ElementReference
3. new HTMLVideoElement(elementRef) is THE correct pattern
4. Library code: BlazorJSRuntime.JS static. Components: @inject BlazorJSRuntime JS
5. Dispose on JSObject only releases .NET interop handle, NOT the JS object
6. Array<T> from GetTracks() MUST be disposed (using var)
7. RTCSessionDescription is a POCO (not JSObject) - no disposal needed
8. Rule #2 is NOT optional - fix the library FIRST, never "for now"
9. OnTrack fires DURING SetRemoteDescription - wire BEFORE SDP exchange
10. ALWAYS read BlazorJS Docs/ before using any BlazorJS type
11. No object, no dynamic - strong typing only
12. No eval, no em dashes

## Git State
- Branch: master
- Latest commit: 7807dd7
- Clean working tree
- All pushed to origin
- SipSorcery submodule at commit 0687d01ad

## Resume Tomorrow
1. TJ re-test ChatRoom demo (remote video fix committed)
2. Check GitHub Pages deployment (submodule fix committed)
3. SpawnDev.MultiMedia integration when Geordi ready
4. Start WebTorrent migration to SpawnDev.RTC
5. Any remaining BlazorJS wrapper gaps (OnUnMute, native OnAddTrack/OnRemoveTrack)

# Changelog

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

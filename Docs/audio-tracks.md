# Audio Tracks (Phase 4a)

SpawnDev.RTC ships a cross-platform audio-track path: capture audio on either browser or desktop, and send it over WebRTC to any peer (browser or desktop) from the same C# code.

Core types:

- **`IAudioTrack`** (from SpawnDev.MultiMedia) — platform-abstract audio source. Produces raw PCM frames.
- **`MultiMediaAudioSource`** (in `SpawnDev.RTC.Desktop`) — adapter that wraps an `IAudioTrack` as a SipSorcery `IAudioSource` so desktop `DesktopRTCPeerConnection` can encode/send it.
- **`DesktopRTCPeerConnection.AddTrack`** — two overloads for audio: direct `IAudioTrack` (wraps automatically) or pre-wrapped `MultiMediaAudioSource`.

Browser consumes audio via native WebRTC (`getUserMedia` -> `RTCPeerConnection.addTrack`). Desktop is where the bridge lives.

## Minimal desktop example

Capture a 48 kHz stereo sine wave and send it to a peer:

```csharp
using SpawnDev.RTC.Desktop;
using SpawnDev.MultiMedia;

var pc = new DesktopRTCPeerConnection();

// Any IAudioTrack - file, microphone, synthesized, etc.
IAudioTrack sine = new SineWaveAudioTrack(frequencyHz: 440, sampleRate: 48000, channels: 2);

pc.AddTrack(sine);  // wraps internally in MultiMediaAudioSource

// ...normal offer/answer SDP exchange via the signaling layer of your choice...
```

The receiving peer's `RTCPeerConnection.OnTrack` fires with the remote audio `MediaStreamTrack`; the audio plays through the default audio output (or the caller can route it as needed).

## What the bridge does

The adapter at `SpawnDev.RTC.Desktop/MultiMediaAudioSource.cs` implements SipSorcery's `IAudioSource` interface:

1. Subscribes to the `IAudioTrack`'s frame event.
2. Pushes raw PCM frames into the SipSorcery encoder at the negotiated format (Opus 48 kHz stereo when both peers support it; falls back to PCMU 8 kHz when talking to a plain PSTN-profile peer).
3. Exposes `EncodedFrameCount` and `EncodedByteCount` diagnostic properties so integration tests can assert real RTP frames were emitted, not just that the connection opened.

Codec negotiation uses SipSorcery's `SortMediaCapability`. Both peers landing on the SAME selected format was broken in upstream SipSorcery 10.0.4 due to an inverted ternary at `RTPSession.cs:1221` (PR [sipsorcery-org/sipsorcery#1558](https://github.com/sipsorcery-org/sipsorcery/pull/1558)); the fix is in the `LostBeard/sipsorcery` fork and is shipped via SpawnDev.SIPSorcery ≥ 10.0.4-local.1.

## Cross-platform: browser ↔ desktop audio call

Two peers, one browser (Blazor WASM), one desktop (.NET), same codebase:

- **Browser side** uses `BrowserRTCPeerConnection` which wraps the native `RTCPeerConnection`. Call `getUserMedia` on a browser `MediaStream`, grab its audio track, and add to the peer connection.
- **Desktop side** uses `DesktopRTCPeerConnection` with `AddTrack(IAudioTrack)`. Any `IAudioTrack` works - `SineWaveAudioTrack` for testing, a file-backed one for playback, a platform microphone wrapper for live capture.

Both peers exchange SDP through a `TrackerSignalingClient` (or any `ISignalingClient` implementation). The audio is end-to-end Opus 48 kHz stereo when the negotiation converges on Opus (the default when both sides are modern WebRTC stacks).

## Tests

Two end-to-end tests in `SpawnDev.RTC.Demo.Shared/UnitTests/RTCTestBase.Phase4MediaTests.cs`:

1. Two desktop peers exchange a 48 kHz stereo sine wave, verify `OnTrack` fires with audio kind + Opus payload type in the SDP.
2. The receiving peer's encoded-frame counter reaches at least 5 non-empty RTP frames within 20 seconds, proving the bridge actually encodes and emits audio (not just that signaling succeeded).

Both run in the full RTC PlaywrightMultiTest suite and pass on desktop. Full RTC regression: 261/0/0.

## Phase 4b (video, not yet shipped)

`IVideoTrack` on the MultiMedia side exists, but the SIP-side encoder for H.264 (via Windows MediaFoundation P/Invoke) is the next concrete deliverable and is estimated at 2-3 weeks of focused work - the RTP H.264 payload format is the wildcard. Linux VAAPI and macOS VideoToolbox are separate per-OS follow-ups after that.

Until video lands, use Phase 4a audio alongside browser-native video (desktop ↔ browser calls can still carry video on the browser side) or substitute screen-share over a data channel.

## File reference

| Path | Role |
|---|---|
| `SpawnDev.RTC.Desktop/MultiMediaAudioSource.cs` | Adapter: `IAudioTrack` → SipSorcery `IAudioSource` |
| `SpawnDev.RTC.Desktop/DesktopRTCPeerConnection.cs` | `AddTrack(IAudioTrack)` + `AddTrack(MultiMediaAudioSource)` overloads |
| `SpawnDev.RTC.Demo.Shared/UnitTests/RTCTestBase.Phase4MediaTests.cs` | Two end-to-end tests |
| Upstream SipSorcery `RTPSession.cs:1221` (fixed in LostBeard/sipsorcery) | Codec-priority ternary bug, fixed for reliable Opus negotiation |

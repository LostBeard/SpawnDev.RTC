# Video Tracks (Phase 4b)

SpawnDev.RTC ships a cross-platform video-track path: capture video on either browser or desktop, H.264-encode on desktop (via the OS MediaFoundation encoder), and send over WebRTC to any peer (browser or desktop) from the same C# code.

Core types:

- **`IVideoTrack`** (from SpawnDev.MultiMedia) — platform-abstract video source. Produces `VideoFrame` events (NV12 preferred for zero-copy encoding).
- **`MultiMediaVideoSource`** (in `SpawnDev.RTC.Desktop`) — adapter wrapping an `IVideoTrack` as a SipSorcery `IVideoSource`. Runs each raw frame through `VideoEncoderFactory.CreateH264` (Windows: MediaFoundation H.264 MFT) and emits H.264 Annex-B NAL units for SipSorcery's RTP H.264 packetizer (RFC 6184).
- **`DesktopRTCPeerConnection.AddTrack`** — two video overloads: `AddTrack(IVideoTrack)` (auto-wraps) or `AddTrack(MultiMediaVideoSource)` (preconstructed for bitrate control).

Browser consumes video via native WebRTC (`getUserMedia` → `RTCPeerConnection.addTrack`). Desktop uses the bridge below.

## Minimal desktop example

Capture a webcam and send to a peer:

```csharp
using SpawnDev.RTC.Desktop;
using SpawnDev.MultiMedia;

var pc = new DesktopRTCPeerConnection();

// Any IVideoTrack - webcam, screen capture, synthetic, etc.
var stream = await MediaDevices.GetUserMedia(new MediaStreamConstraints
{
    Video = new MediaTrackConstraints
    {
        Width = 640,
        Height = 480,
        FrameRate = 30,
        PixelFormat = VideoPixelFormat.NV12, // zero-copy into MFT
    }
});
var videoTrack = (IVideoTrack)stream.GetVideoTracks()[0];

pc.AddTrack(videoTrack);  // wraps internally in MultiMediaVideoSource

// ...normal offer/answer SDP exchange via the signaling layer of your choice...
```

Receiver peer's `RTCPeerConnection.OnTrack` fires with the remote video `MediaStreamTrack`.

## What the bridge does

At `SpawnDev.RTC.Desktop/MultiMediaVideoSource.cs`, the bridge:

1. Subscribes to `IVideoTrack.OnFrame`.
2. Confirms the frame is NV12 (MediaFoundation's preferred input; other formats fall through to `SpawnDev.MultiMedia.PixelFormatConverter` upstream - the bridge deliberately stays thin and does not embed a conversion pipeline).
3. Pushes the NV12 bytes into `IVideoEncoder.Encode` (Windows → `WindowsH264Encoder` → `H264EncoderMFT`).
4. Receives H.264 Annex-B NAL units and fires `OnVideoSourceEncodedSample`, which SipSorcery's `RTPSession` packetizes per RFC 6184 (Single NAL / FU-A fragmentation) and sends as RTP to the remote peer.
5. Exposes `EncodedFrameCount` / `EncodedByteCount` diagnostics and a `BitrateBps` setter.

## Codec configuration

- **Codec:** H.264, baseline profile (`eAVEncH264VProfile_Base = 66`) — widest browser compatibility. Main/High profile are a simple `H264MFT.MF_MT_MPEG2_PROFILE` bump if/when a use case emerges.
- **Rate control:** CBR (`eAVEncCommonRateControlMode_CBR = 0`). Default bitrate 1.5 Mbps; override via `MultiMediaVideoSource.BitrateBps`.
- **Low latency:** `CODECAPI_AVLowLatencyMode = true`. No frame buffering / no B-frame reordering — encoder emits every frame as soon as it's done (sub-millisecond on hardware-accelerated MFTs like Intel Quick Sync / NVIDIA NVENC / AMD VCE).
- **Key frames:** Call `source.ForceKeyFrame()` to request an IDR. Today's implementation restarts the encoder to guarantee a fresh SPS+PPS+IDR on the next frame (cheap on hardware; millisecond-level). A cleaner `CODECAPI_AVEncVideoForceKeyFrame` path can replace this if the restart cost ever matters.
- **RTP clock:** 90 kHz (WebRTC video standard, all codecs).

## Tests

One end-to-end test in `SpawnDev.RTC.Demo.Shared/UnitTests/RTCTestBase.Phase4MediaTests.cs`:

- `Phase4b_Desktop_VideoBridge_EncodesAndNegotiatesH264`: two `DesktopRTCPeerConnection` instances exchange a synthetic 320×240 @ 30 fps NV12 pattern, assert `OnTrack(video)` fires, SDP contains `m=video` + `H264`, sender-side `MultiMediaVideoSource.EncodedFrameCount >= 5` and `EncodedByteCount >= 1000`. Completes in ~500 ms on a reasonable dev box.

Plus three lower-level MFT tests in `SpawnDev.MultiMedia.Demo.Shared/UnitTests/MultiMediaTestBase.H264Encoder.cs`:

- `H264Encoder_FirstOutput_ContainsSpsPpsIdr` — parses Annex-B start codes + NAL-type bytes, asserts types 7 (SPS) + 8 (PPS) + 5 (IDR) all present in the first non-empty output. 21 ms.
- `H264Encoder_MultipleFrames_ProduceIncreasingTimestamps` — 30-frame feed + Drain, asserts > 0 outputs + > 100 bytes. 33 ms.
- `H264Encoder_Dispose_DoesNotThrow` — encode + dispose + double-dispose. 18 ms.

Plus `VideoEncoderFactory_CreateH264_ReturnsWorkingEncoder` proves the `IVideoEncoder` abstraction surface byte-identically to the raw MFT wrapper.

## Platform status

- **Windows:** ✅ Phase 4b shipped (this doc). MediaFoundation H.264 Encoder MFT via P/Invoke. Zero external NuGet media deps.
- **Linux:** 🚧 Phase 5 — VAAPI (`libva` + `libva-drm`) or x264 fallback behind the same `VideoEncoderFactory.CreateH264` facade.
- **macOS:** 🚧 Phase 5 — VideoToolbox (`VTCompressionSession*`).
- **Browser:** ✅ Native WebRTC stack (no bridge needed; `RTCPeerConnection.addTrack` on a `MediaStreamTrack` returned by `getUserMedia` just works).

## File reference

| Path | Role |
|---|---|
| `SpawnDev.MultiMedia/Windows/H264MFTInterop.cs` | P/Invoke for MediaFoundation H.264 MFT + ICodecAPI + PROPVARIANT + MFT_OUTPUT_DATA_BUFFER |
| `SpawnDev.MultiMedia/Windows/H264EncoderMFT.cs` | Thin MFT wrapper: set output/input types, configure via ICodecAPI (low-latency + CBR), ProcessInput / ProcessOutput + Drain + Dispose |
| `SpawnDev.MultiMedia/Windows/WindowsH264Encoder.cs` | `IVideoEncoder` implementation on top of `H264EncoderMFT` |
| `SpawnDev.MultiMedia/IVideoEncoder.cs` | Platform-agnostic encoder interface + `VideoEncoderFactory.CreateH264` dispatch |
| `SpawnDev.RTC.Desktop/MultiMediaVideoSource.cs` | Bridge: `IVideoTrack` → `IVideoEncoder` → `IVideoSource` (SipSorcery) |
| `SpawnDev.RTC.Desktop/DesktopRTCPeerConnection.cs` | `AddTrack(IVideoTrack)` + `AddTrack(MultiMediaVideoSource)` overloads |
| SipSorcery `RtpVideoFramer.cs` + `MediaStream.cs` (fork) | RFC 6184 RTP H.264 packetization (Single NAL / FU-A / STAP-A) - unchanged from upstream |

## Related

- [`audio-tracks.md`](audio-tracks.md) — Phase 4a audio bridge (Opus via Concentus).
- [`sctp-tuning.md`](sctp-tuning.md) — Data-channel throughput fix shipped alongside Phase 4a/4b.
- `Plans/PLAN-H264-Encoder.md` (in SpawnDev.MultiMedia repo) — Original 6-step Phase 4b execution plan. All 4 implementation steps now shipped; WPF demo integration (step 5) and formal video codec negotiation docs (step 6) remain.

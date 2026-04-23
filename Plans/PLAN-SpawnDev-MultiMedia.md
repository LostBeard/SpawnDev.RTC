# Plan: SpawnDev.MultiMedia - Cross-Platform Media Capture and Playback

**Owner:** Captain (TJ)
**Lead:** Geordi
**Template:** Copy SpawnDev.RTC project layout exactly (PlaywrightMultiTest, Demo, DemoConsole, Demo.Shared, etc.)
**Repo:** LostBeard/SpawnDev.MultiMedia (new repo)

---

## What It Is

Cross-platform media capture and playback for .NET. Camera, microphone, speakers, screen capture, video display. One API, every platform. Same architecture as SpawnDev.RTC - platform detection, common interfaces, native implementations.

**SpawnDev.RTC consumes SpawnDev.MultiMedia** for WebRTC media. But SpawnDev.MultiMedia is standalone - any .NET app can use it for camera/mic access without WebRTC.

---

## Project Structure (Copy from SpawnDev.RTC)

```
SpawnDev.MultiMedia/
    SpawnDev.MultiMedia/                  # Core library (NuGet package)
        Browser/                     # BlazorJS implementations
        Windows/                     # Windows implementations (MediaFoundation, WASAPI)
        Linux/                       # Linux implementations (V4L2, PulseAudio)
        MacOS/                       # macOS implementations (AVFoundation, CoreAudio)
        SpawnDev.MultiMedia.csproj
    SpawnDev.MultiMedia.Demo/             # Blazor WASM demo (camera preview, mic level meter)
    SpawnDev.MultiMedia.Demo.Shared/      # Shared tests (run on browser + desktop)
    SpawnDev.MultiMedia.DemoConsole/      # Desktop test runner
    SpawnDev.MultiMedia.WpfDemo/          # WPF demo (camera preview, audio test)
    PlaywrightMultiTest/             # Copy from SpawnDev.RTC (update ports/names)
    SpawnDev.MultiMedia.slnx
```

**Port assignment:** 5580 (PlaywrightMultiTest)

---

## Core Interfaces

### IMediaDevices (static factory, like RTCMediaDevices)

```csharp
public static class MediaDevices
{
    // Camera + mic access
    static Task<IMediaStream> GetUserMedia(MediaStreamConstraints constraints);
    
    // Screen capture (browser only, throw on desktop initially)
    static Task<IMediaStream> GetDisplayMedia(MediaStreamConstraints? constraints = null);
    
    // Enumerate available devices
    static Task<MediaDeviceInfo[]> EnumerateDevices();
}
```

### IMediaStream

```csharp
public interface IMediaStream : IDisposable
{
    string Id { get; }
    bool Active { get; }
    IMediaStreamTrack[] GetTracks();
    IMediaStreamTrack[] GetAudioTracks();
    IMediaStreamTrack[] GetVideoTracks();
    IMediaStreamTrack? GetTrackById(string id);
    void AddTrack(IMediaStreamTrack track);
    void RemoveTrack(IMediaStreamTrack track);
    IMediaStream Clone();
    event Action<IMediaStreamTrack>? OnAddTrack;
    event Action<IMediaStreamTrack>? OnRemoveTrack;
}
```

### IMediaStreamTrack

```csharp
public interface IMediaStreamTrack : IDisposable
{
    string Id { get; }
    string Kind { get; }           // "audio" or "video"
    string Label { get; }          // Device name
    bool Enabled { get; set; }
    bool Muted { get; }
    string ReadyState { get; }     // "live" or "ended"
    string ContentHint { get; set; }
    
    MediaTrackSettings GetSettings();
    MediaTrackConstraints GetConstraints();
    Task ApplyConstraints(MediaTrackConstraints constraints);
    IMediaStreamTrack Clone();
    void Stop();
    
    event Action? OnEnded;
    event Action? OnMute;
    event Action? OnUnmute;
}
```

### IVideoTrack (extends IMediaStreamTrack)

```csharp
public interface IVideoTrack : IMediaStreamTrack
{
    int Width { get; }
    int Height { get; }
    double FrameRate { get; }
    
    // Raw frame access for desktop rendering
    // Browser: never fires (use video element instead)
    event Action<VideoFrame>? OnFrame;
}

public class VideoFrame : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public VideoPixelFormat Format { get; }    // RGBA, NV12, I420, BGRA
    public ReadOnlyMemory<byte> Data { get; }  // Raw pixel data
    public long Timestamp { get; }
}

public enum VideoPixelFormat
{
    RGBA,
    BGRA,
    NV12,
    I420,
    YUY2,
}
```

### IAudioTrack (extends IMediaStreamTrack)

```csharp
public interface IAudioTrack : IMediaStreamTrack
{
    int SampleRate { get; }
    int ChannelCount { get; }
    int BitsPerSample { get; }
    
    // Raw audio sample access for desktop playback/processing
    // Browser: never fires (use audio element instead)
    event Action<AudioFrame>? OnFrame;
}

public class AudioFrame
{
    public int SampleRate { get; }
    public int ChannelCount { get; }
    public int SamplesPerChannel { get; }
    public ReadOnlyMemory<byte> Data { get; }  // PCM samples
    public long Timestamp { get; }
}
```

### IAudioPlayer (desktop only)

```csharp
public interface IAudioPlayer : IDisposable
{
    void Play(IAudioTrack track);
    void Stop();
    float Volume { get; set; }      // 0.0 to 1.0
    bool Muted { get; set; }
}
```

### IVideoRenderer (desktop only)

```csharp
public interface IVideoRenderer : IDisposable
{
    void Attach(IVideoTrack track);
    void Detach();
    // Platform-specific: WPF gets ImageSource, WinForms gets Bitmap, etc.
}
```

### MediaDeviceInfo

```csharp
public class MediaDeviceInfo
{
    public string DeviceId { get; set; } = "";
    public string Kind { get; set; } = "";        // "audioinput", "audiooutput", "videoinput"
    public string Label { get; set; } = "";
    public string GroupId { get; set; } = "";
}
```

---

## Platform Implementations

### Browser (SpawnDev.MultiMedia/Browser/)

**Already mostly done in SpawnDev.RTC.** Move the browser media code here.

- `BrowserMediaDevices` - wraps `navigator.mediaDevices` via BlazorJS
- `BrowserMediaStream` - wraps BlazorJS `MediaStream`
- `BrowserMediaStreamTrack` - wraps BlazorJS `MediaStreamTrack`
- Video display: consumer attaches to HTML `<video>` element via BlazorJS
- Audio playback: consumer attaches to HTML `<audio>` element via BlazorJS
- Uses `BlazorJSRuntime.JS` static accessor (no DI)

**Dependencies:** SpawnDev.BlazorJS

### Windows (SpawnDev.MultiMedia/Windows/)

**No external NuGet dependencies.** Use Windows APIs via P/Invoke.

#### Video Capture: MediaFoundation

MediaFoundation is built into Windows 7+. Access via COM interop/P/Invoke.

Key APIs:
- `MFEnumDeviceSources()` - enumerate cameras
- `IMFMediaSource` - camera device
- `IMFSourceReader` - read video frames
- `IMFSample` / `IMFMediaBuffer` - raw frame data

Implementation:
```csharp
public class WindowsVideoCapture : IVideoTrack
{
    // P/Invoke MediaFoundation
    // MFCreateDeviceSource -> IMFMediaSource -> IMFSourceReader
    // ReadSample() in a loop -> fire OnFrame with VideoFrame
    // Supports width/height/frameRate constraints
}
```

Reference: The SIPSorceryMedia.Windows source code at
https://github.com/sipsorcery-org/SIPSorceryMedia.Windows
shows exactly how to do this. Study it, don't copy it (different license concerns).

#### Audio Capture: WASAPI

WASAPI (Windows Audio Session API) is built into Windows Vista+.

Key APIs:
- `IMMDeviceEnumerator` - enumerate audio devices
- `IMMDevice` - audio device
- `IAudioClient` - capture/playback session
- `IAudioCaptureClient` - read audio samples
- `IAudioRenderClient` - write audio samples for playback

Implementation:
```csharp
public class WindowsAudioCapture : IAudioTrack
{
    // COM interop for WASAPI
    // IAudioClient.Initialize() -> IAudioCaptureClient
    // GetBuffer() in a loop -> fire OnFrame with AudioFrame
    // Supports sampleRate, channelCount constraints
}

public class WindowsAudioPlayer : IAudioPlayer
{
    // IAudioClient.Initialize() -> IAudioRenderClient
    // GetBuffer() / ReleaseBuffer() for playback
}
```

#### Device Enumeration

```csharp
public class WindowsMediaDevices
{
    // MFEnumDeviceSources for video devices
    // IMMDeviceEnumerator for audio devices
    // Return MediaDeviceInfo[] with deviceId, label, kind
}
```

#### Video Display (WPF)

```csharp
public class WpfVideoRenderer : IVideoRenderer
{
    // Converts VideoFrame to WriteableBitmap
    // Updates WPF Image.Source on UI thread
    // Uses Dispatcher.Invoke for thread safety
    public WriteableBitmap Bitmap { get; }  // Bind to Image.Source
}
```

### Linux (SpawnDev.MultiMedia/Linux/) - Phase 2

- Video: V4L2 (Video4Linux2) via P/Invoke to `libv4l2`
- Audio: PulseAudio via P/Invoke to `libpulse`
- Device enumeration: `/dev/video*` for cameras, PulseAudio API for audio

### macOS (SpawnDev.MultiMedia/MacOS/) - Phase 3

- Video: AVFoundation via P/Invoke or ObjC interop
- Audio: CoreAudio via P/Invoke
- Device enumeration: AVCaptureDevice API

---

## Integration with SpawnDev.RTC

SpawnDev.RTC will consume SpawnDev.MultiMedia:

```csharp
// Before (current - creates tracks with codec capabilities but no capture)
var stream = await RTCMediaDevices.GetUserMedia(constraints);

// After (with SpawnDev.MultiMedia - real camera/mic capture)
var stream = await MediaDevices.GetUserMedia(constraints);
// stream.GetVideoTracks()[0] fires OnFrame with real camera frames

// Feed to SpawnDev.RTC peer connection
var sender = pc.AddTrack(stream.GetVideoTracks()[0]);
// SpawnDev.RTC wires the track's OnFrame to SipSorcery's RTP encoder
```

The key integration point: SpawnDev.RTC's `DesktopRTCPeerConnection.AddTrack()` 
needs to wire `IVideoTrack.OnFrame` events to SipSorcery's video source, 
and `IAudioTrack.OnFrame` to SipSorcery's audio source.

---

## Test Plan

### Unit Tests (Demo.Shared, run on browser + desktop)

```
Phase 1 - Device Enumeration:
- [ ] EnumerateDevices_ReturnsDevices
- [ ] EnumerateDevices_HasVideoInput
- [ ] EnumerateDevices_HasAudioInput
- [ ] EnumerateDevices_DeviceInfoProperties (deviceId, kind, label)

Phase 2 - Video Capture:
- [ ] GetUserMedia_VideoOnly (request video, verify track kind)
- [ ] GetUserMedia_VideoTrack_Properties (width, height, frameRate)
- [ ] GetUserMedia_VideoTrack_EnableDisable
- [ ] GetUserMedia_VideoTrack_Stop
- [ ] GetUserMedia_VideoTrack_Clone
- [ ] GetUserMedia_VideoTrack_GetSettings
- [ ] GetUserMedia_VideoTrack_ApplyConstraints (change resolution)
- [ ] GetUserMedia_VideoTrack_OnFrame (desktop: verify raw frames arrive)

Phase 3 - Audio Capture:
- [ ] GetUserMedia_AudioOnly
- [ ] GetUserMedia_AudioTrack_Properties (sampleRate, channelCount)
- [ ] GetUserMedia_AudioTrack_EnableDisable
- [ ] GetUserMedia_AudioTrack_Stop
- [ ] GetUserMedia_AudioTrack_OnFrame (desktop: verify raw samples arrive)

Phase 4 - Combined:
- [ ] GetUserMedia_AudioAndVideo
- [ ] MediaStream_GetTracks_Mixed
- [ ] MediaStream_Clone_WithTracks
- [ ] MediaStream_AddRemoveTrack

Phase 5 - Audio Playback (desktop only):
- [ ] AudioPlayer_PlayTrack
- [ ] AudioPlayer_Volume
- [ ] AudioPlayer_Mute

Phase 6 - Video Rendering (desktop only):
- [ ] VideoRenderer_AttachTrack
- [ ] VideoRenderer_FramesRendered
- [ ] VideoRenderer_Detach
```

### PlaywrightMultiTest

Copy SpawnDev.RTC's PlaywrightMultiTest setup:
- Fake camera/mic for browser (`--use-fake-device-for-media-stream`)
- Camera/mic permissions granted
- Non-headless Chromium
- Port 5580

### Demo Apps

**Blazor WASM Demo:**
- Camera preview page (getUserMedia -> video element)
- Mic level meter (audio samples -> visual bar)
- Device selector dropdown (enumerateDevices)
- Resolution/framerate controls

**WPF Demo:**
- Camera preview (VideoFrame -> WriteableBitmap -> Image)
- Mic level meter (AudioFrame -> volume calculation -> progress bar)
- Device selector dropdown
- Resolution/framerate controls
- Audio playback test (play captured mic through speakers)

---

## Implementation Order

### Phase 1: Skeleton + Browser (Week 1)
1. Copy SpawnDev.RTC project structure exactly
2. Rename everything (SpawnDev.RTC -> SpawnDev.MultiMedia)
3. Define all interfaces (IMediaStream, IMediaStreamTrack, IVideoTrack, IAudioTrack, etc.)
4. Browser implementations (move from SpawnDev.RTC, already working)
5. Device enumeration test
6. GetUserMedia test (browser)
7. PlaywrightMultiTest running with fake camera

### Phase 2: Windows Video Capture (Week 2)
1. MediaFoundation P/Invoke declarations
2. Camera enumeration via MFEnumDeviceSources
3. WindowsVideoCapture class (IMFSourceReader -> OnFrame)
4. VideoFrame with pixel format conversion
5. WPF demo with camera preview
6. Desktop tests passing

### Phase 3: Windows Audio Capture + Playback (Week 2-3)
1. WASAPI COM interop declarations
2. Audio device enumeration via IMMDeviceEnumerator
3. WindowsAudioCapture class (IAudioCaptureClient -> OnFrame)
4. WindowsAudioPlayer class (IAudioRenderClient)
5. WPF demo with mic level meter + playback
6. Desktop audio tests passing

### Phase 4: SpawnDev.RTC Integration

**Phase 4a (audio) - SHIPPED 2026-04-23** in SpawnDev.RTC commit `45e5f24`:
1. [x] SpawnDev.RTC references SpawnDev.MultiMedia via conditional sibling-repo ProjectReference (same pattern as the sipsorcery submodule).
2. [x] `DesktopRTCPeerConnection.AddTrack(IAudioTrack)` + `AddTrack(MultiMediaAudioSource)` overloads wire `IAudioTrack.OnFrame` into SipSorcery's RTP audio encoder (Opus via Concentus by default for browser interop; PCMU/PCMA/G722 available).
3. [x] `MultiMediaAudioSource` bridge: Float32 -> PCM16 conversion via `AudioFormatConverter`, 20 ms framing, strict sample-rate/channel-count validation with clear `NotSupportedException` on mismatches.
4. [x] `EncodedFrameCount` / `EncodedByteCount` diagnostic properties on the bridge so tests and consumers can distinguish "encoder never ran" from "RTP did not deliver."
5. [x] End-to-end test `RTCTestBase.Phase4MediaTests.cs` - two `DesktopRTCPeerConnection` instances negotiate a 48 kHz stereo synthetic sine wave, assert OnTrack fires, SDP contains m=audio + opus, and pc2 receives >= 5 non-empty encoded Opus RTP frames within 20 s. Zero regressions on the 259 pre-existing RTC tests (full suite 261/0/0).
6. [x] SipSorcery fork fix: inverted ternary in `SortMediaCapability` priority-track selection fixed in-fork and filed upstream as PR [sipsorcery-org/sipsorcery#1558](https://github.com/sipsorcery-org/sipsorcery/pull/1558).

**Phase 4b (video) - NOT YET STARTED** (planned):
1. Windows MediaFoundation H.264 encoder via P/Invoke in SpawnDev.MultiMedia.
2. `AddTrack(IVideoTrack)` overload on `DesktopRTCPeerConnection`, same pattern as the audio bridge.
3. Browser WebRTC already supports H.264 end-to-end (native stack); desktop<->browser video call test rounds it out.
4. Cross-platform video call test (browser camera -> desktop display + vice versa).

### Phase 5: Linux + macOS (future)

1. Linux: V4L2 video + PulseAudio. MF H.264 encoder replaced by VAAPI for hardware encoding where available.
2. macOS: AVFoundation video + CoreAudio. MF H.264 encoder replaced by VideoToolbox.
3. Same `AddTrack(IAudioTrack)` / `AddTrack(IVideoTrack)` API on all platforms.

---

## Key Principles

1. **No external NuGet media dependencies.** Windows APIs via P/Invoke. Browser via BlazorJS.
2. **Same pattern as SpawnDev.RTC.** Platform detection, common interfaces, `OperatingSystem.IsBrowser()` / `OperatingSystem.IsWindows()`.
3. **BlazorJSRuntime.JS static accessor** for browser implementations. No DI.
4. **NEVER use JSRef directly.** Use BlazorJS typed wrappers. If a wrapper is missing, add it to BlazorJS.
5. **Strong typing only.** No object, no dynamic. Strongly typed DTOs.
6. **Zero-copy where possible.** VideoFrame uses ReadOnlyMemory<byte>, not byte[] copies.
7. **Fix Library First.** If BlazorJS is missing a media wrapper, fix BlazorJS.

---

## Dependencies

- SpawnDev.BlazorJS (browser media wrappers)
- No NuGet media packages
- Windows: MediaFoundation (mfplat.dll, mf.dll) + WASAPI (ole32.dll) via P/Invoke
- Linux: libv4l2, libpulse via P/Invoke
- macOS: AVFoundation, CoreAudio frameworks via P/Invoke

---

## Reference Code

- SIPSorceryMedia.Windows source: https://github.com/sipsorcery-org/SIPSorceryMedia.Windows (study patterns, don't copy)
- NAudio source: https://github.com/naudio/NAudio (WASAPI patterns)
- .NET MediaFoundation samples: Windows SDK samples
- BlazorJS MediaStreamTrack: `D:\users\tj\Projects\SpawnDev.BlazorJS\SpawnDev.BlazorJS\SpawnDev.BlazorJS\JSObjects\MediaStreamTrack.cs`

---

## Notes for Geordi

- Copy SpawnDev.RTC's entire project layout as template
- Update all namespaces, project names, ports (5580)
- The browser implementations can be moved from SpawnDev.RTC.Browser (BrowserRTCMediaStream, BrowserRTCMediaStreamTrack)
- Start with device enumeration + video capture on Windows
- Test with PlaywrightMultiTest using fake camera (--use-fake-device-for-media-stream)
- The WPF demo is the primary desktop showcase
- Captain wants this to work on Windows, Linux, macOS, and browser
- No SIPSorceryMedia dependency at all

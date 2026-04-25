# SpawnDev.RTC

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.RTC.svg?)](https://www.nuget.org/packages/SpawnDev.RTC)

Cross-platform WebRTC for .NET - browser and desktop from a single API.

[Live Demo](https://lostbeard.github.io/SpawnDev.RTC/) - Video/audio/text chat room, serverless via WebTorrent tracker

SpawnDev.RTC provides a unified WebRTC interface that works identically in Blazor WebAssembly (using native browser WebRTC) and on desktop .NET (using a bundled SipSorcery fork with proven browser-interop DTLS/SRTP). Write your peer connection, data channel, and signaling code once - it runs everywhere.

## Features

- **True cross-platform** - Browser (Blazor WASM) and desktop (.NET 10) from one codebase
- **Full WebRTC API** - Data channels, audio, video, media streams, and tracks
- **Browser-proven DTLS/SRTP** - Desktop peers connect to Chrome, Firefox, Edge, and other browser peers
- **Zero-copy JS interop** - In WASM, send/receive ArrayBuffer, TypedArray, Blob without copying to .NET
- **Browser-style API** - Mirrors the W3C WebRTC specification so web developers feel at home
- **Data channels** - Reliable and unreliable data channels with full DCEP support
- **Media streams** - Audio and video capture, tracks, and stream management
- **Desktop audio + video bridges** - `DesktopRTCPeerConnection.AddTrack(IAudioTrack)` wires a [SpawnDev.MultiMedia](https://github.com/LostBeard/SpawnDev.MultiMedia) WASAPI microphone into SipSorcery's RTP encoder (Opus via Concentus, PCMU/PCMA/G722 via the built-in audio encoder). `AddTrack(IVideoTrack)` does the same for video — Windows MediaFoundation H.264 (baseline profile, low-latency, hardware-accelerated where available) feeds RFC 6184 RTP packetization. See [Docs/audio-tracks.md](Docs/audio-tracks.md) and [Docs/video-tracks.md](Docs/video-tracks.md).
- **Simulcast** - `IRTCPeerConnection.AddTransceiver(kind, RTCRtpTransceiverInit)` accepts initial `SendEncodings` so the browser path emits a real simulcast offer (`a=simulcast:send` + `a=rid:* send` lines per RFC 8853). `IRTCRtpSender.GetParameters()` / `SetParameters()` round-trip the typed `RTCRtpSendParameters` DTO. Desktop path accepts the surface but defers real multi-layer encoding to upstream SipSorcery support — same API, partial implementation today.
- **ICE with STUN/TURN** - Full ICE candidate gathering, connectivity checks, and relay fallback
- **SCTP** - Complete SCTP implementation for data channel transport
- **WebTorrent-compatible signaling** - [`SpawnDev.RTC.Signaling`](Docs/signaling-overview.md) speaks the WebTorrent tracker wire protocol. Public trackers (`wss://tracker.openwebtorrent.com`) work out of the box; no server to host for the default case.
- **Self-hostable signaling server** - [`SpawnDev.RTC.Server`](#spawndevrtcserver--spawndevrtcserverapp) (library) and `SpawnDev.RTC.ServerApp` (exe + Docker image) let any ASP.NET Core app host its own tracker with one line of code. See [Docs/run-a-tracker.md](Docs/run-a-tracker.md).
- **Embedded STUN/TURN server** - `SpawnDev.RTC.Server` ships an RFC 5766 TURN server as an ASP.NET Core `IHostedService`. Classic long-term credentials, ephemeral HMAC credentials (RFC 8489 §9.2 / TURN REST API pattern), tracker-gated ephemeral (only currently-announced peers can allocate), period-rotating sub-secrets, Origin-header allowlist, and configurable relay-port range for NAT port forwarding. One host box can run signaling + STUN + TURN together without coturn. See [Docs/stun-turn-server.md](Docs/stun-turn-server.md).
- **Perfect negotiation** - [`PerfectNegotiator`](Docs/perfect-negotiation.md) drop-in helper implements the W3C glare-free renegotiation pattern, so both peers can add tracks / transceivers / data channels concurrently on a live connection without offer/answer collision.
- **No native dependencies** - Pure C# on desktop, native browser APIs in WASM
- **Native access** - Cast once at creation to access platform-specific features (BlazorJS JSObjects in WASM, SipSorcery in desktop)

## Platform Support

| Platform | WebRTC Backend | Status |
|----------|---------------|--------|
| Blazor WebAssembly | Native browser RTCPeerConnection via SpawnDev.BlazorJS | Stable (1.1.6) |
| .NET Desktop (Windows/Linux/macOS) | SipSorcery (bundled fork) | Stable (1.1.6) — full audio + video bridge, browser interop verified |

## Quick Start

### Blazor WebAssembly

SpawnDev.RTC uses [SpawnDev.BlazorJS](https://github.com/LostBeard/SpawnDev.BlazorJS) for browser WebRTC. You must register BlazorJSRuntime and use `BlazorJSRunAsync()` in your `Program.cs`:

```csharp
// Program.cs
using SpawnDev.BlazorJS;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Required: register BlazorJSRuntime (enables static BlazorJSRuntime.JS access)
builder.Services.AddBlazorJSRuntime();

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Required: use BlazorJSRunAsync instead of RunAsync
await builder.Build().BlazorJSRunAsync();
```

### Desktop (.NET)

No special setup required for desktop - SipSorcery is bundled and used automatically.

### Creating a Peer Connection

```csharp
// Same code works on both browser and desktop
var config = new RTCPeerConnectionConfig
{
    IceServers = new[] { new RTCIceServerConfig { Urls = "stun:stun.l.google.com:19302" } }
};
using var pc = RTCPeerConnectionFactory.Create(config);

// Create a data channel
var channel = pc.CreateDataChannel("myChannel");
channel.OnOpen += () => channel.Send("Hello from .NET!");
channel.OnStringMessage += (data) => Console.WriteLine($"Received: {data}");

// Create and send offer
var offer = await pc.CreateOffer();
await pc.SetLocalDescription(offer);
// ... send offer.Sdp to remote peer via your signaling server
```

### Zero-Copy JS Interop (Blazor WASM)

In WASM, you can send and receive data as JS types without copying to .NET:

```csharp
// Cast once at creation
var browserDc = channel as BrowserRTCDataChannel;

// Send JS types directly (zero-copy)
browserDc.Send(myArrayBuffer);    // ArrayBuffer
browserDc.Send(myTypedArray);     // Uint8Array, Float32Array, etc.
browserDc.Send(myBlob);           // Blob

// Receive as JS ArrayBuffer (zero-copy) - pass to WebGL, canvas, workers
channel.OnArrayBufferMessage += (arrayBuffer) =>
{
    // Data stays in JS - no .NET heap copy
    someJsApi.ProcessData(arrayBuffer);
    arrayBuffer.Dispose();
};

// Or receive as byte[] when you need .NET access (copies from JS)
channel.OnBinaryMessage += (bytes) => ProcessInDotNet(bytes);
```

### WebTorrent-Compatible Signaling (`SpawnDev.RTC.Signaling`)

Connect to any WebTorrent-protocol tracker - the public fleet or your own self-hosted - for room-based peer discovery. The same `RoomKey` bytes addresses a room whether peers use `SpawnDev.RTC`, plain JS WebTorrent, or any other BitTorrent-over-WebRTC client:

```csharp
using SpawnDev.RTC;
using SpawnDev.RTC.Signaling;

var peerId = new byte[20];
System.Security.Cryptography.RandomNumberGenerator.Fill(peerId);
var room = RoomKey.FromString("my-lobby-42"); // SHA-1 of UTF-8; no trim, no lowercase

var config = new RTCPeerConnectionConfig
{
    IceServers = new[] { new RTCIceServerConfig { Urls = "stun:stun.l.google.com:19302" } }
};

var handler = new RtcPeerConnectionRoomHandler(config);
handler.OnPeerConnection += (pc, peerId) => { /* pc is ready, add tracks/channels */ };
handler.OnDataChannel    += (channel, peerId) => { /* remote opened a DC */ };

await using var client = new TrackerSignalingClient("wss://tracker.openwebtorrent.com/announce", peerId);
client.Subscribe(room, handler);
await client.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 5 });
// That's it - peers in the same room find each other and establish WebRTC.
```

The tracker is out of the loop once peers meet; all subsequent traffic goes peer-to-peer over the data channel. See [Docs/signaling-overview.md](Docs/signaling-overview.md) and [Docs/use-cases.md](Docs/use-cases.md) for more examples.

### Device Enumeration

```csharp
// List available cameras, microphones, and speakers
var devices = await RTCMediaDevices.EnumerateDevices();

foreach (var device in devices)
{
    Console.WriteLine($"{device.Kind}: {device.Label} ({device.DeviceId})");
}

// Request specific device with constraints
var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints
{
    Audio = true,  // Default audio
    Video = new MediaTrackConstraints  // Specific video settings
    {
        DeviceId = "preferred-camera-id",
        Width = 1920,
        Height = 1080,
        FrameRate = 30,
    },
});
```

### Native Platform Access

Cast once at creation to access the full platform API:

```csharp
var pc = RTCPeerConnectionFactory.Create(config);

// In WASM: full browser RTCPeerConnection (media tracks, stats, etc.)
if (pc is BrowserRTCPeerConnection browserPc)
{
    var nativePC = browserPc.NativeConnection;
    // Access any browser WebRTC API via SpawnDev.BlazorJS
}

// On desktop: full SipSorcery RTCPeerConnection
if (pc is DesktopRTCPeerConnection desktopPc)
{
    var nativePC = desktopPc.NativeConnection;
    // Access any SipSorcery feature directly
}
```

## Architecture

```
SpawnDev.RTC (cross-platform WebRTC)
    |
    +-- IRTCPeerConnection     (SDP, ICE, tracks, transceivers, stats)
    +-- IRTCDataChannel        (string, binary, JS types, flow control)
    +-- IRTCMediaStream        (audio/video tracks, clone, events)
    +-- IRTCMediaStreamTrack   (settings, constraints, enable/disable)
    +-- IRTCRtpTransceiver     (direction, sender, receiver, stop)
    +-- IRTCStatsReport        (connection quality metrics)
    +-- IRTCDTMFSender         (telephone dial tones)
    +-- IRTCDtlsTransport      (DTLS state, ICE transport)
    +-- IRTCSctpTransport      (SCTP for data channels)
    +-- RTCMediaDevices         (getUserMedia, getDisplayMedia, enumerateDevices)
    +-- SpawnDev.RTC.Signaling  (WebTorrent-tracker-compatible signaling)
    |     +-- RoomKey                (20-byte room identifier)
    |     +-- TrackerSignalingClient (shared socket pool, reconnect, announce)
    |     +-- RtcPeerConnectionRoomHandler (default PC-per-peer handler)
    |
    +-- Browser (Blazor WASM)
    |       Native RTCPeerConnection via SpawnDev.BlazorJS
    |       Zero-copy JS types (ArrayBuffer, TypedArray, Blob, DataView)
    |       getUserMedia, getDisplayMedia, enumerateDevices
    |
    +-- Desktop (.NET)
            SipSorcery fork (Src/sipsorcery/) with SRTP browser fix
            BouncyCastle DTLS - verified Chrome/Firefox/Edge interop
            Full ICE/SCTP/DataChannel/RTP stack
```

## Solution Structure

| Project | Purpose |
|---------|---------|
| SpawnDev.RTC | Core library - cross-platform WebRTC abstraction + `SpawnDev.RTC.Signaling` namespace (NuGet package) |
| SpawnDev.RTC.Server | ASP.NET Core library - adds `app.UseRtcSignaling("/announce")` to any web app so it hosts a WebTorrent-compatible tracker (NuGet package) |
| SpawnDev.RTC.ServerApp | Standalone executable + Docker image - zero-config signaling server on port 5590 (or override via `ASPNETCORE_URLS`). See [Docs/run-a-tracker.md](Docs/run-a-tracker.md) |
| SpawnDev.RTC.Demo | Blazor WASM app - ChatRoom (video/audio/text) + unit tests |
| SpawnDev.RTC.Demo.Shared | Shared test methods - run on both browser and desktop |
| SpawnDev.RTC.DemoConsole | Desktop test runner + text chat mode (`dotnet run -- chat`) |
| SpawnDev.RTC.WpfDemo | WPF desktop chat room - peer list, text chat, mute controls |
| PlaywrightMultiTest | Automated test runner - tests across browser + desktop + cross-platform |

### SpawnDev.RTC.Server + SpawnDev.RTC.ServerApp

Anyone who needs WebRTC signaling can host their own tracker with one of three deploy shapes:

**Drop-in one liner for your existing ASP.NET Core app:**

```csharp
// Program.cs - add alongside whatever else your app does
app.UseWebSockets();
app.UseRtcSignaling("/announce");
```

**Standalone executable (no code required):**

```bash
# From source
dotnet run --project SpawnDev.RTC/SpawnDev.RTC.ServerApp
# /announce (WebSocket), /health, /stats on port 5590
```

**Docker** (build from source; a published image is planned but not yet on a public registry):

```bash
docker build -t spawndev/rtc-signaling -f SpawnDev.RTC/SpawnDev.RTC.ServerApp/Dockerfile SpawnDev.RTC
docker run -d -p 8080:8080 --restart unless-stopped \
  --name rtc-signaling \
  spawndev/rtc-signaling
```

The wire format is bit-compatible with the public WebTorrent tracker fleet - a plain JS WebTorrent client can torrent through your server, and any SpawnDev.RTC consumer can meet peers through a public WebTorrent tracker. See [Docs/run-a-tracker.md](Docs/run-a-tracker.md) for reverse-proxy configs (Caddy / nginx / haproxy / Cloudflare), systemd units, and operational notes.

**Embedded STUN/TURN (optional):** the same `SpawnDev.RTC.Server` package also ships a RFC 5766 STUN/TURN server. One-line opt-in: `builder.Services.AddRtcStunTurn(opts => { opts.Enabled = true; /* ... */ });`. Supports classic long-term credentials, ephemeral HMAC credentials (Twilio / Cloudflare / coturn REST API pattern), tracker-gated ephemeral (only announced peers can allocate), period-rotating secrets, NAT port-range binding, and Origin-header allowlisting. See [Docs/stun-turn-server.md](Docs/stun-turn-server.md) for the full deployment guide.

## Dependencies

- [SpawnDev.BlazorJS](https://github.com/LostBeard/SpawnDev.BlazorJS) - Browser WebRTC wrappers
- [SipSorcery](https://github.com/sipsorcery-org/sipsorcery) (bundled fork) - Desktop WebRTC stack
- [Portable.BouncyCastle](https://www.nuget.org/packages/Portable.BouncyCastle/) - DTLS cryptography

## Demos

### Browser ChatRoom (`/chat`)

Video/audio/text conference room with swarm-style signaling. Enter any room name - it's hashed to a BitTorrent-compatible infohash. Peers discover each other through the signal server (like a WebTorrent tracker). Multi-peer video grid, text chat, mute mic/cam.

### Desktop WPF ChatRoom

Same features as the browser demo - join by room name, text chat, peer list with per-peer disconnect. Uses the same infohash system, so browser and desktop users can be in the same room.

```bash
# Run the WPF demo
dotnet run --project SpawnDev.RTC.WpfDemo

# Or the console text chat
dotnet run --project SpawnDev.RTC.DemoConsole -- chat
```

### Serverless Signaling (WebTorrent Tracker)

All demos use the public `wss://tracker.openwebtorrent.com` for signaling - no server deployment needed. Room names are hashed to BitTorrent-compatible infohashes via `RoomKey.FromString(...)`. Works on GitHub Pages.

### Self-hosted Signaling Server

For private deployments, run `SpawnDev.RTC.ServerApp` (see [Solution Structure](#spawndevrtcserver--spawndevrtcserverapp) above) or embed `SpawnDev.RTC.Server` into an existing ASP.NET Core app with a single `app.UseRtcSignaling("/announce")` call. Both host the same WebTorrent-protocol tracker - clients using the public fleet and clients using your server can't tell the difference, and WebTorrent clients treat it as just another tracker URL.

## Test Results

**323 pass / 0 fail / 3 skip** across browser (Chrome) and desktop (.NET) via PlaywrightMultiTest as of 1.1.6 stable. The 3 skips are platform-conditional (browser-only tests in the desktop runtime).

- **Video pixel verification:** red canvas -> WebRTC -> verify red pixels arrive. Blue canvas -> verify blue. Split-screen (left=red, right=green) -> verify spatial accuracy
- **Data integrity:** SHA-256 verified 32KB payloads, 50-chunk ordered delivery with per-byte verification, 256KB max payload, simultaneous bidirectional messaging, Unicode (emoji, CJK, Arabic)
- **Media pipeline:** video loopback with frame decode, audio loopback, simultaneous audio+video+data, dynamic track add/remove mid-call, getUserMedia, track settings/constraints
- **Stress:** 5 simultaneous peer pairs, 100-message rapid burst, 20 channels rapid create/close, 10x GetStats without leaking
- **Cross-platform:** desktop SipSorcery peer connects to browser Chrome peer via embedded signal server
- **Tracker signaling:** peers connect via embedded tracker AND live openwebtorrent.com
- **API coverage:** every interface property readable, double-dispose safe, connection state machine, SDP content verification, ICE gathering, negotiated channels, renegotiation, perfect negotiation, DTMF, transceivers

## Why a SipSorcery Fork?

SipSorcery 10.0.3+ ships a completely rewritten DTLS/SRTP stack ("SharpSRTP") that has known interoperability issues with browser WebRTC peers for data-channel-only connections. The RTLink project (SpawnDev's game networking library) bundles SipSorcery v6.0.11 with the original BouncyCastle DTLS stack, which reliably connects to Chrome, Firefox, and Edge.

SpawnDev.RTC maintains a fork that preserves the proven DTLS/SRTP interop while incorporating upstream improvements to ICE, SDP, SCTP, and data channel handling.

## Developing / Releasing

### Git submodule gotcha - push the SipSorcery fork separately

The `Src/sipsorcery/` directory is a git submodule pointing at the [`LostBeard/sipsorcery`](https://github.com/LostBeard/sipsorcery) fork. **Commits made inside the submodule are not pushed by the outer repo's `git push`.** If you commit a fix inside `Src/sipsorcery/` and then push SpawnDev.RTC, CI (GitHub Pages deploy, anyone cloning with `--recurse-submodules`) will fail with:

```
fatal: remote error: upload-pack: not our ref <sha>
fatal: Fetched in submodule path 'Src/sipsorcery', but it did not contain <sha>.
```

...because the pinned commit lives only in your local submodule working copy.

**Before tagging a release or triggering the GitHub Pages deploy, always run from the repo root:**

```bash
git submodule foreach 'git push origin HEAD'
```

This pushes every submodule's current branch to its own remote. Only then does the outer `git push` result in a fully fetchable repo on GitHub.

If you're on a detached HEAD inside the submodule (common after `git submodule update`), use `git push origin HEAD:master` to push to the fork's master branch.

## Acknowledgments

SpawnDev.RTC would not be possible without the incredible work of the [SipSorcery](https://github.com/sipsorcery-org/sipsorcery) project by **Aaron Clauson** and its many contributors. SipSorcery is the only pure C# WebRTC implementation for .NET - no native wrappers, no C++ dependencies - and it provides the complete ICE, DTLS, SCTP, and data channel stack that powers SpawnDev.RTC on desktop platforms.

The DTLS/SRTP cryptography is built on [Portable.BouncyCastle](https://www.bouncycastle.org/csharp/) by the Legion of the Bouncy Castle.

SpawnDev.RTC maintains a [fork of SipSorcery](https://github.com/LostBeard/sipsorcery) as a git submodule to apply targeted browser interoperability fixes while tracking upstream development. We are grateful to the SipSorcery team for building and maintaining this foundational library under the BSD 3-Clause license.

### Key SipSorcery Contributors

- **Aaron Clauson** ([@sipsorcery](https://github.com/sipsorcery)) - Creator and maintainer
- **Christophe Irles** - RTP header extensions, major contributions
- **Rafael Soares** - Original DTLS/SRTP implementation (ported from OLocation/RestComm)
- **Lukas Volf** ([@jimm98y](https://github.com/jimm98y)) - SharpSRTP DTLS rewrite and SRTP improvements

## License

MIT License - see [LICENSE.txt](LICENSE.txt)

SipSorcery components are distributed under the BSD 3-Clause License. See [LICENSE.txt](LICENSE.txt) for full details.

## 🖖 The SpawnDev Crew

SpawnDev.RTC is built by the entire SpawnDev team - a squad of AI agents and one very tired human working together, Star Trek style. Every project we ship is a team effort, and every crew member deserves a line in the credits.

- **LostBeard** (Todd Tanner) - Captain, architect, writer of libraries, keeper of the vision
- **Riker** (Claude CLI #1) - First Officer, implementation lead on consuming projects
- **Data** (Claude CLI #2) - Operations Officer, deep-library work, test rigor, root-cause analysis
- **Tuvok** (Claude CLI #3) - Security/Research Officer, design planning, documentation, code review
- **Geordi** (Claude CLI #4) - Chief Engineer, library internals, GPU kernels, backend work

If you see a commit authored by `Claude Opus 4.7` on a SpawnDev repo, that's one of the crew. Credit where credit is due. Live long and prosper. 🖖

<a href="https://www.browserstack.com" target="_blank"><img src="https://www.browserstack.com/images/layout/browserstack-logo-600x315.png" width="200" alt="BrowserStack" /></a>

Cross-browser testing provided by [BrowserStack](https://www.browserstack.com), supporting open-source projects.

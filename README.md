# SpawnDev.RTC

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.RTC.svg?)](https://www.nuget.org/packages/SpawnDev.RTC)

Cross-platform WebRTC for .NET - browser and desktop from a single API.

SpawnDev.RTC provides a unified WebRTC interface that works identically in Blazor WebAssembly (using native browser WebRTC) and on desktop .NET (using a bundled SipSorcery fork with proven browser-interop DTLS/SRTP). Write your peer connection, data channel, and signaling code once - it runs everywhere.

## Features

- **True cross-platform** - Browser (Blazor WASM) and desktop (.NET 10) from one codebase
- **Full WebRTC API** - Data channels, audio, video, media streams, and tracks
- **Browser-proven DTLS/SRTP** - Desktop peers connect to Chrome, Firefox, Edge, and other browser peers
- **Zero-copy JS interop** - In WASM, send/receive ArrayBuffer, TypedArray, Blob without copying to .NET
- **Browser-style API** - Mirrors the W3C WebRTC specification so web developers feel at home
- **Data channels** - Reliable and unreliable data channels with full DCEP support
- **Media streams** - Audio and video capture, tracks, and stream management
- **ICE with STUN/TURN** - Full ICE candidate gathering, connectivity checks, and relay fallback
- **SCTP** - Complete SCTP implementation for data channel transport
- **No native dependencies** - Pure C# on desktop, native browser APIs in WASM
- **Native access** - Cast once at creation to access platform-specific features (BlazorJS JSObjects in WASM, SipSorcery in desktop)

## Platform Support

| Platform | WebRTC Backend | Status |
|----------|---------------|--------|
| Blazor WebAssembly | Native browser RTCPeerConnection via SpawnDev.BlazorJS | Working |
| .NET Desktop (Windows/Linux/macOS) | SipSorcery (bundled fork) | In Development |

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
SpawnDev.RTC (cross-platform abstraction)
    |
    +-- Browser (Blazor WASM)
    |       Uses native RTCPeerConnection via SpawnDev.BlazorJS
    |
    +-- Desktop (.NET)
            Uses SipSorcery fork (Src/sipsorcery/)
            Portable.BouncyCastle DTLS - proven browser interop
            Full ICE/SCTP/DataChannel stack
```

## Solution Structure

| Project | Purpose |
|---------|---------|
| SpawnDev.RTC | Core library - cross-platform WebRTC abstraction (NuGet package) |
| SpawnDev.RTC.Demo | Blazor WASM test app for browser-side testing |
| SpawnDev.RTC.Demo.Shared | Shared test base - tests run on both browser and desktop |
| SpawnDev.RTC.DemoConsole | Desktop test runner |
| PlaywrightMultiTest | Automated browser + desktop test runner |

## Dependencies

- [SpawnDev.BlazorJS](https://github.com/LostBeard/SpawnDev.BlazorJS) - Browser WebRTC wrappers
- [SipSorcery](https://github.com/sipsorcery-org/sipsorcery) (bundled fork) - Desktop WebRTC stack
- [Portable.BouncyCastle](https://www.nuget.org/packages/Portable.BouncyCastle/) - DTLS cryptography

## Why a SipSorcery Fork?

SipSorcery 10.0.3+ ships a completely rewritten DTLS/SRTP stack ("SharpSRTP") that has known interoperability issues with browser WebRTC peers for data-channel-only connections. The RTLink project (SpawnDev's game networking library) bundles SipSorcery v6.0.11 with the original BouncyCastle DTLS stack, which reliably connects to Chrome, Firefox, and Edge.

SpawnDev.RTC maintains a fork that preserves the proven DTLS/SRTP interop while incorporating upstream improvements to ICE, SDP, SCTP, and data channel handling.

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

## Credits

Built by Todd Tanner ([@LostBeard](https://github.com/LostBeard)) and the SpawnDev team.

AI development assisted by Claude (Anthropic).

<a href="https://www.browserstack.com" target="_blank"><img src="https://www.browserstack.com/images/layout/browserstack-logo-600x315.png" width="200" alt="BrowserStack" /></a>

Cross-browser testing provided by [BrowserStack](https://www.browserstack.com), supporting open-source projects.

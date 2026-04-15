# SpawnDev.RTC

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.RTC.svg?)](https://www.nuget.org/packages/SpawnDev.RTC)

Cross-platform WebRTC for .NET - browser and desktop from a single API.

SpawnDev.RTC provides a unified WebRTC interface that works identically in Blazor WebAssembly (using native browser WebRTC) and on desktop .NET (using a bundled SipSorcery fork with proven browser-interop DTLS/SRTP). Write your peer connection, data channel, and signaling code once - it runs everywhere.

## Features

- **True cross-platform** - Browser (Blazor WASM) and desktop (.NET 10) from one codebase
- **Browser-proven DTLS/SRTP** - Desktop peers connect to Chrome, Firefox, Edge, and other browser peers
- **Data channels** - Reliable and unreliable data channels with full DCEP support
- **ICE with STUN/TURN** - Full ICE candidate gathering, connectivity checks, and relay fallback
- **SCTP** - Complete SCTP implementation for data channel transport
- **No native dependencies** - Pure C# on desktop, native browser APIs in WASM
- **SpawnDev.BlazorJS integration** - Typed C# wrappers for browser WebRTC APIs

## Platform Support

| Platform | WebRTC Backend | Status |
|----------|---------------|--------|
| Blazor WebAssembly | Native browser RTCPeerConnection via SpawnDev.BlazorJS | Working |
| .NET Desktop (Windows/Linux/macOS) | SipSorcery (bundled fork) | In Development |

## Quick Start

### Blazor WebAssembly

```csharp
// Program.cs
builder.Services.AddBlazorJSRuntime();
builder.Services.AddRTC(); // registers cross-platform RTC services
await builder.Build().BlazorJSRunAsync();
```

### Desktop (.NET)

```csharp
// Program.cs
var services = new ServiceCollection();
services.AddRTC(); // registers desktop RTC services
var sp = services.BuildServiceProvider();
```

### Creating a Peer Connection

```csharp
// Same code works on both browser and desktop
var rtc = sp.GetRequiredService<IRTCPeerConnectionFactory>();
var config = new RTCConfiguration
{
    IceServers = new[] { new RTCIceServer { Urls = "stun:stun.l.google.com:19302" } }
};
var pc = rtc.CreatePeerConnection(config);

// Create a data channel
var channel = pc.CreateDataChannel("myChannel");
channel.OnOpen += () => channel.Send("Hello from .NET!");
channel.OnMessage += (data) => Console.WriteLine($"Received: {data}");

// Create and send offer
var offer = await pc.CreateOffer();
await pc.SetLocalDescription(offer);
// ... send offer.Sdp to remote peer via your signaling server
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

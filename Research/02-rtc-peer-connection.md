# SpawnDev.RTC `RTCPeerConnection` — Cross-Platform Reference

This document describes the WebRTC `RTCPeerConnection` surface SpawnDev.RTC exposes from a single C# API across browser (Blazor WASM) and desktop (.NET 10), how each layer (ICE, DTLS, SCTP) maps to the underlying engine, and where browser and desktop diverge in practice.

## Table of Contents

1. [Cross-platform mapping](#1-cross-platform-mapping)
2. [Connection lifecycle](#2-connection-lifecycle)
3. [SDP offer / answer](#3-sdp-offer--answer)
4. [ICE](#4-ice)
5. [DTLS](#5-dtls)
6. [SCTP](#6-sctp)
7. [Differences browser vs desktop](#7-differences-browser-vs-desktop)
8. [Implementation map](#8-implementation-map)

---

## 1. Cross-platform mapping

| W3C `RTCPeerConnection` member | Browser (SpawnDev.BlazorJS) | Desktop (SipSorcery fork) |
|---|---|---|
| `new RTCPeerConnection(config)` | `JS.New("RTCPeerConnection", config)` returning typed `RTCPeerConnection` | `new SIPSorcery.Net.RTCPeerConnection(config)` |
| `createOffer()` | Native call → returns SDP string | SipSorcery's `CreateOffer()` → returns `RTCSessionDescriptionInit` |
| `createAnswer()` | Native | SipSorcery's `CreateAnswer()` |
| `setLocalDescription(sdp)` | Native | `SetLocalDescriptionAsync(sdp)` |
| `setRemoteDescription(sdp)` | Native | `SetRemoteDescriptionAsync(sdp)` |
| `addIceCandidate(candidate)` | Native | `AddIceCandidate(candidate)` |
| `createDataChannel(label, opts)` | Native → returns `RTCDataChannel` | `CreateDataChannelAsync(label, opts)` → `RTCDataChannel` |
| `iceConnectionState` | Native enum | SipSorcery enum |
| `connectionState` | Native enum | SipSorcery enum |
| `getStats()` | Native via `JSON.stringify(report)` (parsed in C#) | SipSorcery `GetStatsAsync()` returns synthesized stats |

The single C# API surface is `SpawnDev.RTC.RtcPeerConnection`. Internally it forwards to one of two implementations selected by platform detection at construction time. Consumers do not see the platform split.

```csharp
// Same code on both platforms:
var pc = new RtcPeerConnection(new RTCConfiguration { /* ICE servers etc */ });
pc.OnConnectionStateChange += (_, state) => Console.WriteLine($"state = {state}");
pc.OnDataChannel += (_, dc) => HandleDataChannel(dc);
var dc = await pc.CreateDataChannelAsync("messages", new RTCDataChannelInit { Ordered = true });
var offer = await pc.CreateOfferAsync();
await pc.SetLocalDescriptionAsync(offer);
// ... ship `offer.Sdp` over signaling, await answer, set remote description ...
```

---

## 2. Connection lifecycle

### State machine (W3C RTCPeerConnection)

```
new                                         (new RTCPeerConnection)
  ├─→ have-local-offer  (setLocalDescription called with offer)
  │     ├─→ stable      (setRemoteDescription called with answer)
  │     └─→ have-remote-pranswer
  │           └─→ stable (final answer)
  ├─→ have-remote-offer (setRemoteDescription called with offer)
  │     ├─→ have-local-pranswer
  │     │     └─→ stable
  │     └─→ stable      (setLocalDescription called with answer)
  └─→ closed            (close() called or fatal error)
```

Both platforms expose this state via the `signalingState` property. The values are identical strings (`"new"`, `"have-local-offer"`, `"have-remote-offer"`, `"have-local-pranswer"`, `"have-remote-pranswer"`, `"stable"`, `"closed"`).

### connectionState (rolled-up)

The W3C `connectionState` rolls up ICE + DTLS + the underlying transport into one bucket: `"new"` → `"connecting"` → `"connected"` → `"disconnected"` → `"failed"` → `"closed"`. SpawnDev.RTC fires `OnConnectionStateChange` on every transition.

### iceConnectionState (ICE-specific)

`"new"` → `"checking"` → `"connected"` (or `"completed"` if all candidate pairs nominated) → `"disconnected"` → `"failed"` → `"closed"`. Browser and desktop both fire transitions; the desktop SipSorcery fork emits `"completed"` only after the final ICE pair is nominated.

---

## 3. SDP offer / answer

### Offer generation (caller side)

A peer that initiates the connection generates an offer:

```csharp
var pc = new RtcPeerConnection(config);
var dc = await pc.CreateDataChannelAsync("data");  // create BEFORE the offer to negotiate the data-channel m-line
var offer = await pc.CreateOfferAsync();
await pc.SetLocalDescriptionAsync(offer);

// CRITICAL: wait for ICE gathering to complete before sending the SDP. The
// WebTorrent tracker has no trickle-ICE channel — full SDP with all candidates
// must go in a single offer. This was the root cause of the 1.1.2 chat-demo
// silent failure (peers exchanged candidate-less SDPs and never connected).
await pc.WaitForIceGatheringCompleteAsync();

var fullSdp = pc.LocalDescription.Sdp;  // includes all gathered candidates
SendOverSignaling(fullSdp);
```

`WaitForIceGatheringCompleteAsync()` is a SpawnDev.RTC helper that wraps the native `iceGatheringState === "complete"` event. Both platforms support it.

### Offer SDP shape (data-channel-only — typical for SpawnDev.RTC)

```sdp
v=0
o=- 1234567890 2 IN IP4 0.0.0.0
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic: WMS *
m=application 9 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 0.0.0.0
a=ice-ufrag:abc1
a=ice-pwd:0123456789abcdef0123456789abcdef
a=ice-options:trickle
a=fingerprint:sha-256 AB:CD:EF:...   ; DTLS cert fingerprint
a=setup:actpass                       ; offerer is flexible — answer picks active or passive
a=mid:0
a=sctp-port:5000
a=max-message-size:262144              ; 256 KB default; configurable up to ~1 GB
a=candidate:1 1 UDP 2113929471 192.168.1.5 49200 typ host
a=candidate:2 1 UDP 1694498815 64.246.234.108 49210 typ srflx raddr 192.168.1.5 rport 49200
a=candidate:3 1 UDP 41885439 64.246.234.108 49220 typ relay raddr 192.168.1.5 rport 49200
a=end-of-candidates
```

Notes:
- `m=application 9 UDP/DTLS/SCTP webrtc-datachannel` is the only m-line for data-channel-only connections (no media). SpawnDev.RTC uses data-channel-only by default; audio/video tracks add additional m-lines.
- `a=setup:actpass` on the offer; the answer picks `active` or `passive` (browser typically picks `active`, SipSorcery handles either).
- `a=ice-options:trickle` is set even though SpawnDev.RTC waits for full gathering — clients tolerant of trickle still see it as a hint.
- `a=fingerprint` is the DTLS certificate fingerprint. Browser uses ECDSA-P256 by default; SpawnDev.RTC's SipSorcery fork generates ECDSA-P256 with RSA fallback for browser interop.

### Answer SDP shape

Same structure, with `a=setup:active` (or `passive`) instead of `actpass`, and the answerer's own `ice-ufrag` / `ice-pwd` / `fingerprint` / candidates. The answerer's `m=application` line MUST mirror the offerer's port and direction conventions.

### Browser vs SipSorcery SDP differences

Documented divergences observed on the wire:

| Field | Browser | SipSorcery (fork) | Notes |
|---|---|---|---|
| `o=` line `sess-id` | Random 64-bit | Sequential | Both parseable per RFC 8866 |
| `a=group:BUNDLE` ordering | Strict m-line order | Same | OK |
| `a=msid-semantic` | `WMS *` | `WMS` (no `*`) | Both accepted by browser |
| `a=ice-options:trickle` | Always | Always | OK |
| `a=fingerprint` algorithm | sha-256 | sha-256 | Browser may emit sha-512 in newer versions; SipSorcery accepts both |
| `a=setup` on offer | `actpass` | `actpass` | Match |
| `a=setup` on answer | `active` or `passive` | `active` | Browser picks based on ICE role; SipSorcery always picks active when offering, passive when answering — observed in 5+ live runs |
| `a=sctp-port` | `5000` | `5000` | Match |
| `a=max-message-size` | `262144` | `262144` | Both default; SpawnDev.RTC raises to 1 MB on the SCTP layer |
| Trickle ICE candidates | `a=candidate:` lines incrementally | `a=candidate:` lines all at once after gathering complete | SpawnDev.RTC always waits for gathering complete; trickle is suppressed |

These differences are interop-tolerant — every browser + SipSorcery cross-test in `PlaywrightMultiTest.Signaling.CrossPlatform_BrowserDesktop` passes against current Chrome/Edge/Firefox.

---

## 4. ICE

### Configuration

```csharp
var config = new RTCConfiguration
{
    IceServers = new[]
    {
        new RTCIceServer { Urls = new[] { "stun:hub.spawndev.com:3478" } },
        new RTCIceServer
        {
            Urls = new[] { "turn:hub.spawndev.com:3478" },
            Username = "ephemeral-username",
            Credential = "ephemeral-credential",
        }
    },
    IceTransportPolicy = RTCIceTransportPolicy.All,        // "all" or "relay" (force TURN)
};
```

### Candidate types

| Type | Description | When generated | Latency / cost |
|---|---|---|---|
| `host` | Direct interface address (LAN IP) | Always | Free, lowest latency |
| `srflx` | Server-reflexive (NAT public IP) via STUN | When STUN server reachable | Free (after STUN), low latency |
| `prflx` | Peer-reflexive (discovered during connectivity check) | During candidate-pair checks | Free, low latency |
| `relay` | TURN-allocated relay address | When TURN credentials valid + direct paths fail | Costs TURN-server bandwidth, higher latency |

The browser and SipSorcery both gather all four types in parallel. The ICE state machine prefers in order: `host` → `srflx`/`prflx` → `relay`. A pair is "nominated" when both sides agree on it (RFC 8445 §8.1.1).

### Trickle ICE in WebTorrent-tracker context

WebRTC normally supports **trickle ICE** — candidates are exchanged incrementally as gathering progresses, reducing connection setup latency. The WebTorrent WebSocket tracker has no separate trickle channel — the SDP offer / answer must contain the full candidate list at send time. SpawnDev.RTC handles this with `WaitForIceGatheringCompleteAsync()` before serializing the SDP. See [SpawnDev.RTC 1.1.2 changelog](../CHANGELOG.md) for the bug this fix addresses.

### ICE candidate format on the wire

```
candidate:1 1 UDP 2113929471 192.168.1.5 49200 typ host
            │ │ │      │              │     │     │
            │ │ │      │              │     │     └─ candidate type
            │ │ │      │              │     └─ port
            │ │ │      │              └─ IP address
            │ │ │      └─ priority (32-bit, RFC 8445 §5.1.2)
            │ │ └─ transport (UDP only for SCTP-data-channel)
            │ └─ component ID (1 for RTP, 2 for RTCP — but BUNDLE+rtcp-mux means just 1)
            └─ foundation (opaque grouping ID)
```

Trailing fields appear for non-host candidates: `raddr <IP> rport <port>` indicate the related (server-reflexive base or relayed allocation source) address.

---

## 5. DTLS

DTLS (Datagram TLS, RFC 6347) is the transport-security layer used by all WebRTC data channels. Every byte exchanged after the ICE pair is nominated is encrypted under a DTLS session.

### Cipher suites

Both browser and SpawnDev.RTC support:
- `TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256` (preferred — fastest with hardware AES)
- `TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256` (fallback)

### Certificate

Each peer generates a self-signed cert at construction time (SpawnDev.RTC: ECDSA-P256 preferred, RSA-2048 fallback). The cert is **bound to the SDP fingerprint** — the receiving peer verifies the DTLS handshake's cert against `a=fingerprint:sha-256 AB:CD:...` from the SDP.

### SipSorcery fork DTLS implementation

SpawnDev.RTC's desktop side uses a SipSorcery fork that retains the proven BouncyCastle DTLS stack (v6.0.11) instead of the upstream's SharpSRTP rewrite (PR #1486, Dec 2025) which broke browser interop for data-channel-only connections. See [06-sipsorcery-fork.md](06-sipsorcery-fork.md) and the [SpawnDev.WebTorrent Research/09-sipsorcery-dtls-analysis.md](https://github.com/LostBeard/SpawnDev.WebTorrent/blob/master/SpawnDev.WebTorrent/Research/09-sipsorcery-dtls-analysis.md) for full analysis.

---

## 6. SCTP

SCTP (Stream Control Transmission Protocol, RFC 4960) sits above DTLS and provides multi-stream framing for WebRTC data channels (`UDP/DTLS/SCTP/webrtc-datachannel`).

### Streams

Each `RTCDataChannel` corresponds to one or two SCTP streams (one per direction). The number of streams per association is negotiated at SCTP init; defaults vary by implementation:
- Chrome / Firefox: 65535 streams per direction
- SipSorcery: 65535 streams per direction (fork tunable)

In practice, SpawnDev.RTC apps use 1–10 channels per peer-connection.

### Reliability and ordering

A data channel is configured at creation time with two flags:
- `ordered` (boolean): when true, SCTP delivers in-order; when false, out-of-order delivery allowed
- `maxRetransmits` (int) OR `maxPacketLifeTime` (ms): retransmit ceiling — when reached, the message is abandoned

Combinations:

| `ordered` | `maxRetransmits` | `maxPacketLifeTime` | Behavior |
|---|---|---|---|
| true | undefined | undefined | Reliable + ordered (TCP-like, default) |
| true | 0 | undefined | Reliable + ordered with no retransmits — strict at-most-once |
| true | N | undefined | Up to N retransmits per message, ordered |
| false | undefined | undefined | Reliable + unordered (best-effort ordering) |
| false | 0 | undefined | At-most-once unordered (UDP-like) |
| false | undefined | T ms | Lifetime-bounded unordered |

SpawnDev.RTC's default is `Ordered = true` (reliable, in-order).

### Backpressure (BufferedAmount)

Critical detail: `RTCDataChannel.send(data)` does **NOT** block or throw when the SCTP send buffer is full. It queues the message indefinitely on the local heap. If the application keeps queuing, the heap grows without bound, and eventually the remote sees the connection drop with `sctpCauseCode=12` (User-Initiated Abort — browser tearing down out of memory or hitting an internal cap).

The fix is **explicit backpressure**:

```csharp
const int MaxBufferedAmount = 64 * 1024;  // 64 KB

dc.BufferedAmountLowThreshold = MaxBufferedAmount;
dc.OnBufferedAmountLow += (_, _) => /* signal sender */;

async Task SendWithBackpressure(byte[] payload)
{
    while (dc.BufferedAmount > MaxBufferedAmount && !dc.IsClosed)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dc.OnBufferedAmountLow += (_, _) => tcs.TrySetResult(true);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    dc.Send(payload);
}
```

This is exactly what `SpawnDev.RTC.RtcPeer.Send` does — see [03-data-channels.md](03-data-channels.md) for the complete wire-level details, including the SCTP cause-12 production debug story.

### SCTP cause codes

When SCTP closes a stream or association due to error, it emits a cause code. Common values:

| Cause | Name | Meaning |
|---|---|---|
| 1 | Invalid Stream Identifier | Sent to a closed/non-existent stream |
| 2 | Missing Mandatory Parameter | Malformed init frame |
| 5 | Unresolvable Address | Endpoint address cannot be resolved |
| **12** | **User-Initiated Abort** | One side called close (or browser tearing down due to local error) |
| 13 | Protocol Violation | Unrecoverable protocol error |

Cause 12 is the most operationally relevant — see [03-data-channels.md](03-data-channels.md) for the SpawnDev.RTC fix that resolved a production cause-12 fault chain.

---

## 7. Differences browser vs desktop

Behavioral differences between SpawnDev.BlazorJS browser-native `RTCPeerConnection` and the SipSorcery-fork desktop `RTCPeerConnection`. SpawnDev.RTC's cross-platform abstraction smooths most of them, but consumers writing edge-case code should know:

| Behavior | Browser | Desktop (fork) | Reconciled in SpawnDev.RTC by |
|---|---|---|---|
| ICE gathering completion event | Fires immediately on `iceGatheringState === "complete"` | Fires when last gatherer finishes | Both wrapped under `WaitForIceGatheringCompleteAsync()` |
| `getStats()` return shape | Native `RTCStatsReport` with native types | Synthesized `Dictionary<string, RTCStats>` | Browser path uses `JSON.stringify` to flatten; desktop emits comparable keys; result is `IReadOnlyDictionary<string, RTCStats>` either way |
| Data channel default `id` | Browser auto-assigns (1, 3, 5, ...) | Fork auto-assigns starting at 1 | Caller passes explicit `Id` to override |
| `RTCDataChannel.Send(byte[])` semantics | Sync queue (cannot block) | Sync queue (cannot block) | Both wrapped by `RtcPeer.SendAsync` with backpressure |
| `BufferedAmountLowThreshold` default | 0 (event fires whenever buffer drains) | 0 (same) | Set explicitly to `MaxBufferedAmount` (64 KB) on creation |
| `OnDataChannel` event | Fires on remote-initiated channel | Fires on remote-initiated channel | OK |
| `connectionState` granularity | W3C 6-state | Same enum, same transitions | OK |
| Trickle ICE | Supported | Supported | Suppressed by `WaitForIceGatheringCompleteAsync` for tracker compat |
| Cert algorithm preference | ECDSA-P256 | ECDSA-P256 (fork preference) → RSA fallback | Browser interop verified |
| `getReceivers` / `getSenders` | Native (audio/video) | Fork supports for media | Data-channel-only apps don't use these |
| `addTrack(track, stream)` | Native | Fork supports via SipSorcery RTP | SpawnDev.MultiMedia provides cross-platform `IAudioTrack` / `IVideoTrack` for media apps |

---

## 8. Implementation map

| W3C concept | SpawnDev.RTC C# type | Notes |
|---|---|---|
| `RTCPeerConnection` | `SpawnDev.RTC.RtcPeerConnection` | Single API, dual implementation |
| `RTCDataChannel` | `SpawnDev.RTC.RtcDataChannel` | Wraps browser native or SipSorcery `RTCDataChannel` |
| `RTCConfiguration` | `SpawnDev.RTC.RTCConfiguration` | Mirror of W3C dictionary |
| `RTCIceServer` | `SpawnDev.RTC.RTCIceServer` | Mirror |
| `RTCSessionDescriptionInit` | `SpawnDev.RTC.RTCSessionDescriptionInit` | Mirror with `Type` + `Sdp` |
| `RTCIceCandidate` | `SpawnDev.RTC.RTCIceCandidate` | Mirror |
| Stats report | `IReadOnlyDictionary<string, RTCStats>` (typed subclasses for `inbound-rtp`, `outbound-rtp`, `peer-connection`, `transport`, `data-channel`, etc.) | Browser via JSON.stringify, desktop via SipSorcery state |
| Signaling state | `SpawnDev.RTC.RTCSignalingState` enum | String-typed values match W3C |
| Connection state | `SpawnDev.RTC.RTCPeerConnectionState` enum | Same |
| ICE gathering wait | `RtcPeerConnection.WaitForIceGatheringCompleteAsync()` | SpawnDev.RTC extension for tracker-compat |
| Backpressure-aware send | `RtcPeer.SendAsync(byte[])` | Awaits `OnBufferedAmountLow` when over `MaxBufferedAmount` |

The signaling-layer adapter (`TrackerSignalingClient`, `RtcPeerConnectionRoomHandler`) wires the `RTCPeerConnection` to the WebSocket tracker — see [01-tracker-signaling.md](01-tracker-signaling.md) for that protocol and [03-data-channels.md](03-data-channels.md) for how the data flows once connected.

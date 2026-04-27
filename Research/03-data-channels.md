# SpawnDev.RTC Data Channel Wire & Backpressure Reference

Once a WebRTC peer-connection is established, all communication flows over `RTCDataChannel`s. This document covers the wire-level details every SpawnDev.RTC consumer should know — message framing, binary encoding, backpressure (the most error-prone area), and SCTP cause-code interpretation.

## Table of Contents

1. [Channel creation and labels](#1-channel-creation-and-labels)
2. [Message framing](#2-message-framing)
3. [Binary vs text](#3-binary-vs-text)
4. [Backpressure — `BufferedAmount` and `OnBufferedAmountLow`](#4-backpressure--bufferedamount-and-onbufferedamountlow)
5. [SCTP cause codes — production debug](#5-sctp-cause-codes--production-debug)
6. [Max message size](#6-max-message-size)
7. [`WireDataChannel` adapter — SpawnDev.RTC + WebTorrent integration](#7-wiredatachannel-adapter--spawndevrtc--webtorrent-integration)

---

## 1. Channel creation and labels

A peer-connection can have multiple data channels. Each is identified by a label (UTF-8 string, no length cap) and an id (uint16, auto-assigned).

```csharp
var dc = await pc.CreateDataChannelAsync("messages", new RTCDataChannelInit
{
    Ordered = true,            // SCTP ordered delivery
    MaxRetransmits = null,     // null = reliable (no cap on retransmits)
    Negotiated = false,        // false = announce via DCEP; true = pre-negotiated id
    Id = null,                 // ignored when Negotiated=false
    Protocol = "",             // optional sub-protocol identifier (string, often empty)
});
```

### Labels are NOT cross-side identifiers

A common pitfall: assuming the label assigned at the offerer's `CreateDataChannel` call is the same as the label observed at the answerer's `OnDataChannel` event. Browsers preserve the label faithfully; SipSorcery's fork also preserves it. **However**, SpawnDev.RTC's `RtcPeer` had a 2026-04-27 bug where label-vs-peerId comparison was used to dedup peer-connections — and the labels turned out to be NOT cross-side stable in some race scenarios with simultaneous `CreateDataChannel` calls from both sides. The fix in `Torrent.OnHandshake` skips the destroy-on-duplicate path when labels are not directly comparable. See `D:\users\tj\Projects\SpawnDev.WebTorrent\SpawnDev.WebTorrent\SpawnDev.WebTorrent\Torrent.cs` for the `labelsComparable` guard.

The takeaway: do not use channel labels as cross-side dedup keys. Use the BitTorrent peer_id from the BEP-10 handshake (or whatever stable identifier the application establishes via a handshake message on the channel itself).

### DCEP — Data Channel Establishment Protocol (RFC 8832)

When a non-negotiated channel is created, the offerer sends a DCEP `OPEN` message in-band to the answerer, who replies with a DCEP `ACK`. This happens transparently — neither browser nor SipSorcery exposes DCEP to consumers. Application data sent before the ACK is buffered until the ACK arrives.

---

## 2. Message framing

### One `Send` = one application message

`RTCDataChannel.Send(data)` produces exactly one **message** on the wire, regardless of how SCTP fragments it under the hood. The receiving side's `OnMessage` event fires once per message with the complete data buffer.

```csharp
dc.Send(new byte[] { 1, 2, 3 });    // Receiver gets one OnMessage with [1, 2, 3]
dc.Send(new byte[] { 4, 5, 6 });    // Receiver gets one OnMessage with [4, 5, 6]
// Receiver does NOT get [1, 2, 3, 4, 5, 6] in a single event.
```

This is different from a TCP socket (where reads can deliver any prefix of the bytes sent). Data channels are **message-oriented** like UDP, but **reliable** (when ordered=true, retransmits=null).

### No length prefix, no message ID

The wire frame contains the application message bytes plus SCTP/DTLS overhead — no application-level framing is added. Consumers that need their own message ids, headers, or types build them into the application payload.

### Ordering across channels

SCTP delivers messages within a single channel in send-order (when ordered=true). It does NOT preserve cross-channel ordering. If your app needs cross-channel ordering, either use one channel for everything or build sequence numbers into payloads.

---

## 3. Binary vs text

### Binary type — set on the receiver side

```csharp
dc.BinaryType = RTCDataChannelBinaryType.ArrayBuffer;  // delivers byte[] / Uint8Array
// or
dc.BinaryType = RTCDataChannelBinaryType.Blob;         // browser-only, delivers Blob
```

SpawnDev.RTC defaults to `ArrayBuffer` on both platforms. The C# event `OnMessage` always delivers a `byte[]` regardless of binary type.

### Send paths

```csharp
dc.Send(string text);   // sends as text frame, UTF-8 encoded by SCTP
dc.Send(byte[] data);   // sends as binary frame
```

The receiver sees one `OnMessage` event per send. Text vs binary is preserved through the channel — `OnMessage` carries either a string or a byte[] depending on what the sender used.

In production SpawnDev.RTC + SpawnDev.WebTorrent code, all peer-wire and signaling traffic is binary. Text frames are reserved for the human-readable signaling layer (the WebSocket tracker — not the data channel).

---

## 4. Backpressure — `BufferedAmount` and `OnBufferedAmountLow`

This is the single most important thing to get right when writing a data-channel application that pushes more than ~tens of KB at a time.

### The trap

`RTCDataChannel.Send(data)` is **synchronous** and **non-blocking**. It does not throw on a full SCTP send buffer — it queues the message on the local heap and returns immediately. There is no built-in mechanism to make it backpressure-aware.

### What goes wrong

If your application keeps calling `Send` while the network is slower than the sender:

1. `BufferedAmount` (the queue size in bytes) grows without bound
2. Local memory pressure increases
3. Eventually one of three things happens:
   a. Browser reaches its internal cap (Chromium: ~1 GiB queued before discarding) and tears down the connection with `sctpCauseCode=12` (User-Initiated Abort)
   b. The local process runs out of heap and crashes
   c. The remote peer's incoming buffer fills, RWND drops to zero, and the sender stalls — but only AFTER local memory is committed

### The fix — explicit backpressure

```csharp
const int MaxBufferedAmount = 64 * 1024;  // 64 KB — fits comfortably under MTU * cwnd

// At channel creation:
dc.BufferedAmountLowThreshold = MaxBufferedAmount;

// On every send:
async Task SendAsync(byte[] payload)
{
    while (dc.BufferedAmount > MaxBufferedAmount && !destroyed && dc.ReadyState == "open")
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Capture the latest tcs so OnBufferedAmountLow signals it
        Interlocked.Exchange(ref _bufferedAmountLowTcs, tcs);

        // Recheck in case the buffer drained between the while condition and here
        if (dc.BufferedAmount <= MaxBufferedAmount) break;

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    if (dc.ReadyState == "open") dc.Send(payload);
}

dc.OnBufferedAmountLow += (_, _) =>
{
    var tcs = Interlocked.Exchange(ref _bufferedAmountLowTcs, null);
    tcs?.TrySetResult(true);
};
```

This is exactly what `SpawnDev.RTC.RtcPeer.Send` (and `WireDataChannel.Send`) implement.

### `BufferedAmountLowThreshold` semantics

`OnBufferedAmountLow` fires **once** when `BufferedAmount` transitions from above the threshold to at-or-below. To get repeated firings, the buffer must rise back above the threshold first.

The threshold of 64 KB is a SpawnDev.RTC convention chosen to:
- Stay comfortably under the typical SCTP cwnd at steady state (avoiding frequent stalls)
- Stay well below browser caps (avoid OOM-style aborts)
- Provide a useful breathing room for high-throughput tensor / piece data

Configurable via `RtcPeer.MaxBufferedAmount`.

### 30-second wait ceiling

The `await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30))` ceiling exists because if the remote stops draining (network outage, browser tab suspended), `OnBufferedAmountLow` never fires. The 30s ceiling lets the sender abandon the wait, return control, and let the next-layer error handling (heartbeat timeout, peer destroy) close the connection cleanly.

---

## 5. SCTP cause codes — production debug

When a data-channel close is observed, the close event carries a numeric `cause` (the SCTP cause code). Common values:

| Cause | Name | What you actually saw | What to do |
|---|---|---|---|
| 0 | Normal | Other side called `close()` cleanly | Nothing; cleanup |
| 1 | Invalid Stream Identifier | Send to closed/never-existed stream | Bug — should not reach here |
| 2 | Missing Mandatory Parameter | Malformed init | Bug at peer |
| 5 | Unresolvable Address | Endpoint address gone | Network issue, not your bug |
| 8 | Restart of Association | Endpoint restarted | Reconnect |
| **12** | **User-Initiated Abort** | One side called close() OR browser hit internal cap | See below |
| 13 | Protocol Violation | Unrecoverable protocol error | Bug |

### The cause-12 production saga

In April 2026, SpawnDev.RTC.WebTorrent's deployed P2P compute demo on GitHub Pages started seeing `sctpCauseCode=12` events when workers pushed back multi-MB tensor result buffers. Diagnosis:

1. Worker computes tensor, pushes back to coordinator via `dc.Send(resultBuffer)`
2. `dc.BufferedAmount` climbed past Chrome's internal cap (~1 GiB queued)
3. Chrome tore down the connection unilaterally, emitting `sctpCauseCode=12`
4. Coordinator saw `OnClose` with cause=12, application logic interpreted as "remote left the swarm"
5. Worker stayed alive but coordinator pruned it; chain reaction

The fix shipped in `SpawnDev.WebTorrent 3.2.2-rc.1` and `SpawnDev.ILGPU.P2P 4.9.2-rc.26`:

1. `RtcPeer.Send` becomes `async` and awaits `OnBufferedAmountLow` when `BufferedAmount > MaxBufferedAmount` (64 KB)
2. `WireDataChannel.BufferedAmountLowThreshold = MaxBufferedAmount`
3. `P2PWorker.HandleDispatchAsync` awaits buffer push-back BEFORE sending the `KernelResult`, so the worker can abort cleanly with `result.Success = false` if any push fails

After the fix: zero observed cause-12 events across 3 weeks of production. The Mandelbrot demo's blank-canvas symptom (caused by the worker timing out before its KernelResult arrived) disappeared simultaneously.

### Cause-12 from the OTHER direction

Occasionally a peer's WebRTC stack will emit cause 12 on its own when the local browser is shutting down (tab close, navigate away). This is normal cleanup and indistinguishable on the wire from the abort case. SpawnDev.RTC's higher-level `OnPeerLeft` event collapses both into a single "peer is gone" signal so consumer code doesn't need to differentiate.

---

## 6. Max message size

The `a=max-message-size` SDP attribute tells the peer how big a single application message it can send. Defaults:

| Implementation | Default | Notes |
|---|---|---|
| Chrome | 262144 (256 KB) | Hard cap on unmodified Chrome |
| Firefox | 1073741823 (~1 GiB) | Effectively unlimited |
| Safari | 65535 (64 KB) | Lowest of the major browsers |
| SipSorcery (fork) | 262144 (256 KB) | Configurable up to 1 GB |
| SpawnDev.RTC default | 262144 | Chrome-compatible |

To send messages larger than 256 KB safely across all browsers, the application must chunk the payload itself. SpawnDev.WebTorrent's piece exchange (BEP 3) chunks pieces at 16 KB boundaries by default, well under any browser's cap.

For high-throughput SpawnDev.ILGPU.P2P workloads, the application chunks tensor buffers into 64 KB strips before pushing — and applies the backpressure pattern (§4) so the chunked sequence doesn't accumulate in `BufferedAmount`.

---

## 7. `WireDataChannel` adapter — SpawnDev.RTC + WebTorrent integration

`WireDataChannel` is the SpawnDev.WebTorrent adapter that wraps an `RTCDataChannel` to look like a BitTorrent peer-wire transport. It plugs into `Wire.cs` (the BEP-3/BEP-10 framing layer) and lets BitTorrent peer logic operate over a WebRTC data channel as if it were a TCP socket.

Key responsibilities:

1. **Backpressure** — `BufferedAmountLowThreshold = RtcPeer.MaxBufferedAmount`; sends await `OnBufferedAmountLow` when over.
2. **Binary framing** — Wire treats sends as opaque byte arrays; the application layer (Wire.cs) handles BEP-3 length-prefixed framing on top.
3. **Close cascade** — on `OnClose`, `WireDataChannel` emits a single event that `Torrent.OnWire`'s `Wire.OnClose` handler turns into a `_transport.UnregisterPeer(peerId)` call. This was the 2026-04-26 P2P bridge-dropout bug; see `SpawnDev.ILGPU.P2P 4.9.2-rc.25` changelog.
4. **No application logic** — `WireDataChannel` does NOT understand the BitTorrent wire protocol. It just routes bytes between the data channel and the upper Wire layer.

The adapter lives in `SpawnDev.WebTorrent` and is the only point where SpawnDev.RTC's `RtcPeer` connects to BitTorrent semantics. SpawnDev.RTC alone (without WebTorrent) doesn't need it — apps using RTC for non-BitTorrent purposes (multiplayer game state, agent swarms) consume `RtcPeer` directly.

---

## Implementation map

| Concept | SpawnDev.RTC type | File |
|---|---|---|
| Data channel | `RtcDataChannel` | `SpawnDev.RTC/RtcDataChannel.cs` |
| Peer + send + backpressure | `RtcPeer` | `SpawnDev.WebTorrent/RtcPeer.cs` (since WebTorrent owns the BitTorrent integration) |
| WebTorrent wire adapter | `WireDataChannel` | `SpawnDev.WebTorrent/WireDataChannel.cs` |
| Default `MaxBufferedAmount` | 64 KB | `RtcPeer.cs` |
| Backpressure 30 s ceiling | `TimeSpan.FromSeconds(30)` | `RtcPeer.cs` `Send` method |

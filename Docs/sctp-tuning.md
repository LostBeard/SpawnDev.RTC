# SCTP Throughput Tuning (Desktop)

Desktop WebRTC data channels in SpawnDev.RTC are powered by our SipSorcery fork (`LostBeard/sipsorcery`, `SpawnDev.SIPSorcery` nuget package). The SCTP layer ships RFC 4960 §7.2.2 defaults:

| Knob | Default | Source |
|---|---|---|
| `MAX_BURST` | 4 chunks | RFC 4960 §7.2.2 |
| `BURST_PERIOD_MILLISECONDS` | 50 ms | Implementation choice (not in RFC) |
| `DEFAULT_SCTP_MTU` | 1300 B | SipSorcery default |

Steady-state ceiling with defaults: `MAX_BURST × MTU / BURST_PERIOD = 4 × 1300 / 50 ms ≈ 104 KB/s` per association, rate-limited by the periodic burst timer when SACKs don't wake the sender thread earlier than the timer.

## 2026-04-23 fix: SACK wake-up race

Versions prior to `SpawnDev.SIPSorcery 10.0.5-rc.1` had a producer-consumer lost-wakeup race in `SctpDataSender.DoSend`: `_senderMre.Reset()` was called AFTER the send work and BEFORE `_senderMre.Wait(burstPeriod)`. Any `_senderMre.Set()` fired by SACK arrival (on a different thread) in the window between the last chunk sent and the `Reset()` was wiped, and the sender thread blocked for the full 50 ms timeout even though more congestion-window capacity was available. On localhost loopback where SACKs round-trip in microseconds, the race window was hit almost every burst, capping actual throughput at the theoretical floor instead of blowing past it.

**Fix:** `_senderMre.Reset()` moved to the TOP of the `DoSend` loop so any `Set()` during send work is preserved for the next `Wait()`. Wait returns promptly on the SACK signal, and throughput becomes bounded by MRE wake latency (sub-millisecond) rather than the 50 ms burst period.

**Measured on the new regression test** `SctpDataSenderUnitTest.Throughput_FastSackWake_ExceedsBurstCeiling` (504 KB loopback, synchronous SACK delivery):

| State | Time | Throughput |
|---|---|---|
| Pre-fix | 5613 ms | 89.8 KB/s |
| Post-fix | 94 ms | 5.4 MB/s |

**60× speedup on the zero-RTT benchmark.** The lost-wakeup race eliminated the "SACK wiped by Reset before Wait blocks" stall, so Wait returns promptly on signal.

Fix lands in the fork (`SpawnDev.SIPSorcery ≥ 10.0.5-rc.1`) and in any SpawnDev.RTC build ≥ `1.1.3-rc.1`. **Merged upstream 2026-04-23** as [sipsorcery-org/sipsorcery#1560](https://github.com/sipsorcery-org/sipsorcery/pull/1560); Aaron Clauson merged within hours, no comment. Future upstream releases carry the fix natively.

### Honest caveat: real-world WebRTC loopback is still RTT-bound

Geordi (2026-04-23 evening, DevComms `geordi-to-riker-sctp-max-burst-ceiling-2026-04-23.md`) re-measured end-to-end throughput through a real `DesktopRTCPeerConnection` data channel — DTLS encrypt + UDP + DTLS decrypt + scheduler + return trip — and reported:

| Buffer | SendBufferAsync | OnBufferReceived | Effective throughput |
|---|---|---|---|
| 64 KB | 1 ms | 457 ms | 0.14 MB/s |
| 1 MB | 9 ms | 5,337 ms | 0.19 MB/s |
| 5 MB | 9 ms | 30,370 ms | 0.16 MB/s |
| 10 MB | 15 ms | 55,919 ms | 0.18 MB/s |

Constant ~0.15–0.19 MB/s regardless of buffer size → matches `MAX_BURST × MTU / observed_RTT = 4 × 1300 / ~28 ms ≈ 186 KB/s`. The Reset-race fix is real and correct; it just isn't the **dominant** bottleneck once a real-world DTLS/UDP SACK RTT is in the loop. The synthetic benchmark delivers SACKs in-thread with effectively zero RTT which is why it saw 5.4 MB/s — that's the upper bound of what the sender thread can produce when SACKs don't have to leave the host.

**The remaining bottleneck on loopback/LAN is `MAX_BURST = 4`** (RFC 4960 §7.2.2 default, conservative for WAN but wildly under-provisioned for sub-10ms-RTT links). See the next section for the per-association tunables that unlock this.

## Per-association tunables (2026-04-23, `SpawnDev.SIPSorcery ≥ 10.0.5-rc.2`)

`MAX_BURST` and `BURST_PERIOD_MILLISECONDS` are now per-`SctpAssociation` properties with RFC-compliant defaults (4 / 50 ms). Consumers that know their link characteristics can raise them for dramatic throughput improvement without affecting WAN-shaped links.

```csharp
var pc = new DesktopRTCPeerConnection(config);
// ... SCTP association comes up after the DTLS handshake; raise the knobs as soon as it's available:
var sctp = pc.NativeConnection.sctp;                   // SIPSorcery RTCSctpTransport
sctp.RTCSctpAssociation.MaxBurst = 32;                 // default 4; RFC 4960 §7.2.2
sctp.RTCSctpAssociation.BurstPeriodMilliseconds = 10;  // default 50
```

Expected on Geordi's measurement machine: `MAX_BURST = 32` at the same ~28 ms RTT → `32 × 1300 / 28 ms ≈ 1.5 MB/s`, ~8× improvement. 10 MB transfer drops from ~56 s to ~7 s. Tune based on your own RTT measurements; the RFC default is the safe choice whenever the link might traverse the public internet.

## Is the ceiling still reached?

With the Reset-race fix (baseline) + the `MAX_BURST` knob exposed, remaining ceilings are:

1. Chunk allocation / framing overhead (minor; dominated by the RTT term unless MaxBurst is set absurdly high)
2. SACK processing cost (CPU-bound on the receive side)
3. The 262144-byte SIPSorcery SCTP hard cap per message (unchanged)
4. Peer-side receive-window (`_receiverWindow`) back-pressure — if the remote advertises a small window, raising MaxBurst doesn't help

## History

- `d045833a3` - SctpDataSender Reset-race fix + regression test on `LostBeard/sipsorcery`
- `2c4bf7714` - Fork version bump to `10.0.5-rc.1` + release notes
- `ab9a0f3` - SpawnDev.RTC 1.1.3-rc.1 + submodule pointer bump
- `54e7238` - SpawnDev.WebTorrent 3.1.3-rc.1 dep bump (transitively picks up the fix)

Credit to Geordi for the file:line-level diagnostic (DevComms `geordi-to-riker-sctp-burst-handoff-2026-04-23.md`).

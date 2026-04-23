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

**60× speedup.** Loopback-limited by SCTP bookkeeping, not by the burst timer anymore.

Fix lands in the fork (`SpawnDev.SIPSorcery ≥ 10.0.5-rc.1`) and in any SpawnDev.RTC build ≥ `1.1.3-rc.1`. **Merged upstream 2026-04-23** as [sipsorcery-org/sipsorcery#1560](https://github.com/sipsorcery-org/sipsorcery/pull/1560); Aaron Clauson merged within hours, no comment. Future upstream releases carry the fix natively.

## Is the ceiling still reached?

After the fix, throughput over loopback is limited by:

1. Chunk allocation / framing overhead
2. SACK processing cost
3. Wake-event round-trip time
4. The 262144-byte SIPSorcery SCTP hard cap per message (unchanged)

For long-haul or lossy links, the `MAX_BURST × MTU / RTT` bound still governs, which is RFC-correct behavior. Consumers on high-bandwidth low-latency WAN links may still benefit from raising `MAX_BURST` and/or lowering `BURST_PERIOD`, but that's a per-deployment knob and not a library-side default change.

## Changing the knobs at runtime

`SctpDataSender._burstPeriodMilliseconds` is already `internal` and mutable after construction (used by upstream unit tests to drive tests faster). For production runtime tuning, open an issue / PR on the fork - we'd like to see motivating benchmark data before we bump the default.

## History

- `d045833a3` - SctpDataSender Reset-race fix + regression test on `LostBeard/sipsorcery`
- `2c4bf7714` - Fork version bump to `10.0.5-rc.1` + release notes
- `ab9a0f3` - SpawnDev.RTC 1.1.3-rc.1 + submodule pointer bump
- `54e7238` - SpawnDev.WebTorrent 3.1.3-rc.1 dep bump (transitively picks up the fix)

Credit to Geordi for the file:line-level diagnostic (DevComms `geordi-to-riker-sctp-burst-handoff-2026-04-23.md`).

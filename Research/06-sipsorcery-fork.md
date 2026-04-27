# SipSorcery Fork — Why and What We Change

SpawnDev.RTC's desktop WebRTC implementation is a fork of [SipSorcery](https://github.com/sipsorcery-org/sipsorcery), a mature open-source .NET WebRTC + SIP library. The fork lives at [LostBeard/sipsorcery](https://github.com/LostBeard/sipsorcery) on GitHub and is bundled as a git submodule at `Src/sipsorcery/` in the SpawnDev.RTC repo.

This document covers why we fork, what we change, and the path back to upstream. Full DTLS/SRTP analysis (including a detailed walk through the upstream rewrite that broke browser interop) lives in the SpawnDev.WebTorrent repo at [Research/09-sipsorcery-dtls-analysis.md](https://github.com/LostBeard/SpawnDev.WebTorrent/blob/master/SpawnDev.WebTorrent/Research/09-sipsorcery-dtls-analysis.md). This doc is the index — read 09 there for the deep dive.

## Table of Contents

1. [Why we fork](#1-why-we-fork)
2. [Modifications](#2-modifications)
3. [Submodule layout](#3-submodule-layout)
4. [Upstream sync workflow](#4-upstream-sync-workflow)
5. [Path to merging back](#5-path-to-merging-back)

---

## 1. Why we fork

SipSorcery 10.0.3+ (December 2025) shipped a completely rewritten DTLS/SRTP stack ("SharpSRTP", PR #1486) that **breaks browser WebRTC interop** for data-channel-only connections. SpawnDev.RTLink (the predecessor library that SpawnDev.RTC inherits from) had proven for two years that the **prior** BouncyCastle DTLS stack (v6.0.11) works reliably with Chrome/Firefox/Edge. The SharpSRTP rewrite regressed multiple cipher-suite negotiations and DTLS extensions.

We could:
1. Pin to SipSorcery 10.0.2 (the last working version before the rewrite)
2. Wait for the upstream rewrite to stabilize
3. Fork and keep the proven BouncyCastle DTLS while taking ICE/SDP/SCTP improvements from upstream

We chose 3 because:
- Pinning to 10.0.2 means losing months of legitimate upstream improvements (ICE candidate filtering, IPv6 fixes, SCTP tunables)
- Waiting is unbounded — the rewrite is invasive and the upstream maintainers indicated multiple-month-scale stabilization
- Forking lets us fix our own integration bugs (browser-interop edge cases) without waiting on upstream review

The fork pattern is identical to SpawnDev.ILGPU's ILGPU fork — same git submodule layout, same conditional ProjectReference / PackageReference scheme.

---

## 2. Modifications

The fork's commits are all listed in [Src/sipsorcery/UPSTREAM_BACKLOG.md](../Src/sipsorcery/UPSTREAM_BACKLOG.md) (lives in the submodule). Summary:

### DTLS / Crypto

1. **Restrict SRTP profiles to browser-compatible:**
   - `AES128_CM_HMAC_SHA1_80`
   - `AEAD_AES_128_GCM`
   - `AEAD_AES_256_GCM`
   
   Upstream's SharpSRTP rewrite negotiates additional profiles that some browser builds don't accept; restricting prevents the silent-fail path.

2. **Use BouncyCastle DTLS** (`Org.BouncyCastle.Crypto.Tls`) instead of SharpSRTP. BouncyCastle is well-tested, has a known-good cipher set, and produces DTLS handshakes browsers verify cleanly.

3. **Generate ECDSA-P256 certificates by default** (with RSA-2048 fallback when ECDSA isn't supported by the platform). Browsers prefer ECDSA-P256 for DTLS; matching the preference reduces handshake fingerprinting and improves interop.

4. **Disable MKI (Master Key Identifier)** per RFC 8827 — browsers don't send MKI on the wire, and SipSorcery's strict-mode validation rejected packets without it.

5. **`NotifySecureRenegotiation` override** — required for Pion / libdatachannel compatibility. Upstream's strict TLS-renegotiation policy rejects DTLS handshakes that don't use the secure-renegotiation extension exactly as RFC 5746 specifies; some non-browser implementations diverge slightly. The override accepts both flavors.

### TURN / STUN

6. **`TurnServerConfig.ResolveHmacKey`** — per-request HMAC-SHA1 key resolver delegate. Lets the application backend implement the RFC 8489 §9.2 ephemeral-credential pattern (Twilio / Cloudflare TURN REST API). Required for `EphemeralTurnCredentials` and `TrackerGatedResolver` in SpawnDev.RTC.Server.

7. **`TurnServerConfig.RelayPortRangeStart` / `RelayPortRangeEnd`** — bounded relay-socket port range for NAT port-forwarding deployments. Required for any consumer-NAT TURN deployment (most home routers can't forward the OS ephemeral range).

### SCTP

8. **`SctpAssociation.MaxBurst`** + **`BurstPeriodMilliseconds`** — expose RFC 4960 §7.2.2 burst-control knobs. Default values are conservative; high-throughput data-channel apps (SpawnDev.ILGPU.P2P tensor pipelines) tune them up for ~10x throughput.

9. **Producer-consumer lost-wakeup race fix in `SctpDataSender`** — ~60x speedup on fast-SACK paths. Upstream had a bug where the data-sender thread occasionally missed a wakeup when the SACK queue drained between checks, causing 100+ ms stalls under high throughput.

### ICE

10. **No active changes** — SpawnDev.RTC takes upstream ICE behavior unchanged. The ICE state machine in SipSorcery is stable and we have no edge-case bugs against it.

---

## 3. Submodule layout

The fork lives as a git submodule:

```
SpawnDev.RTC/
├── SpawnDev.RTC/                         # repo root
│   ├── SpawnDev.RTC/                     # the C# library
│   ├── SpawnDev.RTC.Server/              # the C# tracker + STUN/TURN server library
│   ├── Src/
│   │   └── sipsorcery/                   # ← git submodule, points at LostBeard/sipsorcery
│   │       ├── src/
│   │       │   ├── SIPSorcery/           # main DLL
│   │       │   └── SIPSorceryMedia.Abstractions/
│   │       └── UPSTREAM_BACKLOG.md       # PR-ready changes for upstream
│   └── ...
```

### Conditional ProjectReference / PackageReference

Both `SpawnDev.RTC.csproj` and `SpawnDev.RTC.Server.csproj` use a conditional reference pattern (same as ILGPU):

```xml
<!-- In-repo builds where the submodule sibling exists: ProjectReference for hot-iteration. -->
<ItemGroup Condition="Exists('$(MSBuildThisFileDirectory)..\Src\sipsorcery\src\SIPSorcery\SIPSorcery.csproj')">
    <ProjectReference Include="..\Src\sipsorcery\src\SIPSorcery\SIPSorcery.csproj" />
</ItemGroup>
<!-- External / standalone builds: PackageReference resolves the published fork from NuGet. -->
<ItemGroup Condition="!Exists('$(MSBuildThisFileDirectory)..\Src\sipsorcery\src\SIPSorcery\SIPSorcery.csproj')">
    <PackageReference Include="SpawnDev.SIPSorcery" Version="10.0.5" />
</ItemGroup>
```

The fork is published to nuget.org as `SpawnDev.SIPSorcery` (separate package id from upstream, so consumers can opt in).

For the SpawnDev.RTC nuget package itself: `SpawnDev.RTC 1.1.1+` bundles the fork DLLs (`SIPSorcery.dll` + `SIPSorceryMedia.Abstractions.dll`) inside `lib/net10.0/` using `PrivateAssets="All"` + `AddProjectReferencesToPackage` MSBuild target — meaning external consumers PackageReference-ing `SpawnDev.RTC` get the fork bundled, no separate dep. See `SpawnDev.RTC.csproj` for the bundling target.

---

## 4. Upstream sync workflow

### Pull upstream changes into the fork

```bash
cd Src/sipsorcery
git remote add upstream https://github.com/sipsorcery-org/sipsorcery.git
git fetch upstream
git merge upstream/main          # or rebase, depending on conflicts
# ... resolve conflicts (most likely in DTLS files, where our BouncyCastle branch diverges) ...
git push origin master            # push to LostBeard/sipsorcery master
cd ../..
git add Src/sipsorcery
git commit -m "Sync sipsorcery fork to upstream YYYY-MM-DD"
```

### Add a fork-specific change

```bash
cd Src/sipsorcery
git checkout -b feature/spawn-dev-foo
# ... edit, commit ...
git push origin feature/spawn-dev-foo
# Open PR against LostBeard/sipsorcery master, merge it
git checkout master && git pull
cd ../..
git add Src/sipsorcery
git commit -m "Bump sipsorcery fork: <change description>"
```

After committing the submodule pointer, also append the change to `Src/sipsorcery/UPSTREAM_BACKLOG.md` with rationale + PR-ready description. That's the queue of changes we'd send upstream once the SharpSRTP situation stabilizes.

---

## 5. Path to merging back

We'd happily retire the fork and consume upstream directly when:

1. Upstream's SharpSRTP rewrite stabilizes to the point that browser interop (Chrome/Firefox/Edge data-channel-only) round-trips cleanly without our cipher-suite restriction.
2. Upstream accepts the SCTP burst-tunable + lost-wakeup fix (item 8-9 above) — these are uncontroversial and benefit all SipSorcery consumers.
3. Upstream accepts the `ResolveHmacKey` + `RelayPortRange*` extensions (items 6-7) — also uncontroversial and broadly useful.

The DTLS rewrite (items 1-5) is the political blocker. We've kept upstream-mergeable patches separate from the fork-only changes in `UPSTREAM_BACKLOG.md` so the merge can be incremental.

---

## See also

- [SpawnDev.WebTorrent Research/09-sipsorcery-dtls-analysis.md](https://github.com/LostBeard/SpawnDev.WebTorrent/blob/master/SpawnDev.WebTorrent/Research/09-sipsorcery-dtls-analysis.md) — full DTLS-rewrite walk-through with code-level citations
- [Src/sipsorcery/UPSTREAM_BACKLOG.md](../Src/sipsorcery/UPSTREAM_BACKLOG.md) — list of fork commits with merge-readiness status
- [LostBeard/sipsorcery on GitHub](https://github.com/LostBeard/sipsorcery) — the fork repo
- [sipsorcery-org/sipsorcery](https://github.com/sipsorcery-org/sipsorcery) — upstream

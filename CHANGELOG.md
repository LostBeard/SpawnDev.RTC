# Changelog

## SpawnDev.RTC 1.1.8-rc.4 (2026-04-29)

### Opt-in `BrowserRTCPeerConnection.DiagnosticsEnabled` static flag

New static property `BrowserRTCPeerConnection.DiagnosticsEnabled` (default `false`). When set to `true`, the connection-state poller publishes per-tick state and synthesis events to JS globals for debugging:

- `__brrtc_pc_count` - number of pollers started in this app
- `__brrtc_pc_{N}` - latest tick state for poller N (`tick={n} conn={state} ice={state}`)
- `__brrtc_last_tick` - last tick globally
- `__brrtc_synth_{N}` - set when synthesis fires (`src=iceFailed|debounce tick={n} subs={N}`)

Off-default keeps zero JS-interop overhead in production. Enable via `SpawnDev.RTC.Browser.BrowserRTCPeerConnection.DiagnosticsEnabled = true;` in `Program.cs` when investigating wire-close issues.

Polling fallback functionality (rc.2 / rc.3) unchanged.

## SpawnDev.RTC.Server 1.0.7-rc.1 (2026-04-28)

### Two parity fixes against the bittorrent-tracker JS reference

Both surfaced by a new head-to-head harness `tracker-debug/verify-tracker-parity.mjs` that runs the same six WebTorrent-tracker scenarios against (a) the live `wss://hub.spawndev.com:44365/announce` and (b) a fresh local `bittorrent-tracker` npm reference, and diffs every captured frame.

**1. Answer-relay path.** When an announce frame carries `answer + to_peer_id + offer_id` (a reply to a forwarded offer), the JS reference forwards the answer to the targeted peer and returns NO announce response to the sender. The C# server was previously sending an extra announce-response frame in this case. `TrackerSignalingServer.HandleAnnounceAsync` now short-circuits to the answer-relay branch BEFORE the response-build step and exits without responding.

**2. Stopped event.** When an announce carries `event=stopped`, the JS reference removes the peer from the room AND sends an announce response with the updated counts (so `incomplete=0` etc. reflect post-stop state). The C# server was previously returning early without sending any response. State mutation now happens before the response-build step, the response is sent with post-stop counts, and the offer-forwarding branch is skipped.

Both fixes only affect the announce-response framing - the data-channel WebRTC flow itself is unchanged. Verified end-to-end against the live hub at `wss://hub.spawndev.com:44365/announce` after redeploy: all 6 scenarios PARITY OK against JS reference (single peer / two peers / three peers / answer flow / stopped event / reconnect).

Includes the `peers`-field-omission fix from 1.0.6.

## 1.1.6 (2026-04-24)

### Two tracker-signaling bug fixes

**1. `TypeInfoResolver` for AOT / trimmed / file-based hosts.**

`BinaryJsonSerializer` (client outbound) and `TrackerSignalingServer._readOpts` (server inbound) now explicitly set

```csharp
TypeInfoResolver = new DefaultJsonTypeInfoResolver()
```

Without this, the very first tracker announce threw

```
System.InvalidOperationException: Reflection-based serialization has been disabled for this application.
Either use the source generator APIs or explicitly configure the 'JsonSerializerOptions.TypeInfoResolver' property.
```

under any reflection-disabled host — specifically:

- **.NET 10 file-based `dotnet run script.cs`** hosts (new in .NET 10, disables reflection-based serialization by default).
- **Trimmed / AOT publishes** that haven't plugged in a source generator.

Impact was masked because `Torrent.StartDiscovery` fires the initial announce with `_ = _discovery.AnnounceAsync(...)` (fire-and-forget), so the exception was swallowed and the client silently never registered with any tracker. Regular `dotnet build` + `dotnet run --project` hosts weren't affected — reflection is enabled there.

Caught by the new `SpawnDev.WebTorrent/interop_test/js_webtorrent_liveswarm.cs` harness, which runs as a file-based `dotnet run` script and uses the full SpawnDev.WebTorrent → SpawnDev.RTC tracker-signaling stack to pair against a real Node.js `webtorrent@^2` + `@roamhq/wrtc` seeder. After the fix, that harness passes end-to-end: 1 MiB hybrid torrent transferred SHA-256 byte-identical from JS to C# over real WebRTC datachannel through the bundled WebSocket tracker.

Zero behavior change for reflection-enabled builds — setting `TypeInfoResolver` explicitly to `DefaultJsonTypeInfoResolver` is what the default was before; the value now just doesn't depend on the host's reflection-based default being enabled.

**2. Empty Origin bypasses the `AllowedOrigins` allowlist.**

`TrackerSignalingServer.HandleWebSocketAsync` now skips the allowlist check when the request's `Origin` header is empty (matches the case where it is missing entirely on the upgrade).

Per RFC 6454 §7, browser-initiated WebSocket upgrades always include an `Origin` header. A missing/empty `Origin` therefore means a non-browser client - desktop C# `ClientWebSocket`, Node.js `ws` without explicit Origin override, curl, etc. The allowlist is explicitly documented as browser-origin abuse protection, with the docstring noting that "Origin is set by the browser and can be spoofed by non-browser clients; this is not a strong authentication mechanism." Treating "no Origin" as "not a browser, allowlist does not apply" makes the gate match its stated purpose.

Caught when the 2026-04-24 hub.spawndev.com `RTC__AllowedOrigins=https://hub.spawndev.com;https://*.spawndev.com` deployment 403'd 5 SpawnDev.WebTorrent desktop integration tests on next sweep. Before the fix any deployment with a populated allowlist effectively blocked every legitimate non-browser consumer, including command-line tools and backend services.

New `OriginAllowlist_E2E_MissingOriginBypassesList` test in `DesktopTurnAuthTests`. The browser-Origin path - explicit Origin must match the allowlist - is unchanged and still covered by `OriginAllowlist_E2E_AcceptsListedRejectsOthers`.

PlaywrightMultiTest full sweep: **323 pass / 0 fail / 3 skip** in 2m 12s. Companion release: SpawnDev.RTC.Server 1.0.5.

## 1.1.5 (2026-04-24)

### Simulcast sendEncodings + TURN data-path E2E tests

Additive API surface closing two audit-punch-list gaps.

**`IRTCPeerConnection.AddTransceiver(string kind, RTCRtpTransceiverInit init)`** (and `(IRTCMediaStreamTrack, RTCRtpTransceiverInit)` overload) — initial-simulcast configuration. Per RFC 8853 / W3C WebRTC, RIDs must be set at transceiver creation time; `SetParameters` cannot change them afterwards (browsers throw `InvalidModificationError: Read-only field modified`). The new `RTCRtpTransceiverInit` DTO exposes `Direction` + `SendEncodings`, reusing the existing cross-platform `RTCRtpEncoding` shape. Browser path translates to native `RTCRtpTransceiverOptions` + `RTCMediaEncoding[]`; desktop path accepts the options but ignores `SendEncodings` (SipSorcery has no native simulcast). New `RtpSender_InitSimulcast_SdpOfferContainsSimulcastAndRidLines` verifies the browser's SDP offer contains `a=simulcast:send` + three `a=rid:* send` lines when 3 encodings are passed at `AddTransceiver` time.

**TURN RFC 5766 §10 data-path E2E**: two new tests in `DesktopTurnAuthTests` exercising the actual relay forwarding (not just Allocate auth):
- `TurnRelay_E2E_SendIndicationForwardsDataToRawPeer` - Client → TURN `SendIndication` → raw UDP peer receives exact bytes.
- `TurnRelay_E2E_DataIndicationDeliversPeerPayloadToClient` - Raw peer UDP → TURN relay socket → `DataIndication` → client extracts exact bytes.

PlaywrightMultiTest full sweep: **322 pass / 0 fail / 3 skip** in 2m 10s. Closes RTC `PLAN-Full-WebRTC-Coverage.md:184+186` and `PLAN-SpawnDev-RTC-v0.1.0.md:72+75` — all Phase 7 items now shipped.

## 1.1.4 (2026-04-24)

### Browser `RTCRtpSender.SetParameters` fix

Regression in 1.1.3: on the browser path, `IRTCRtpSender.SetParameters(GetParameters())` threw

```
TypeError: Failed to execute 'setParameters' on 'RTCRtpSender':
Failed to read the 'codecs' property from 'RTCRtpParameters':
Required member is undefined.
```

on every call - my cross-platform-DTO-to-BlazorJS-native translation only copied the mutable fields (TransactionId + Encodings) across, so `codecs` / `headerExtensions` / `rtcp` arrived at the browser as undefined. Per W3C WebRTC spec those members are required-present (even though read-only from the app's perspective) when passing a parameters object into `setParameters`.

Fix: `BrowserRtpSender` caches the native BlazorJS `RTCRtpSendParameters` from the most recent `GetParameters()` call, and on `SetParameters()` mutates that cached object in place (setting TransactionId + Encodings from the DTO) before passing it to the native API. Codecs / HeaderExtensions / Rtcp round-trip unchanged, matching the spec-intended "modify-then-write" flow.

Surfaced by `RtpSender_SetParameters_TransactionIdRoundTrip_Succeeds` + `RtpSender_EncodingShape_SimulcastLayeringRoundTrips` in the WASM test runtime. Both pass on 1.1.4. Desktop path unchanged (SipSorcery's single-encoding stub doesn't route through this translation).

No consumer-facing API change. Consumers of SpawnDev.RTC 1.1.3 via SpawnDev.WebTorrent 3.1.3 pick up the fix on next `dotnet restore` via the >= 1.1.3 transitive pin (no WebTorrent re-release required).

## 1.1.3-rc.14 (2026-04-24)

### Tracker WebSocket disconnect hygiene fix

`SpawnDev.RTC.Server 1.0.3-rc.4` catches `WebSocketException` / `ConnectionResetException` / `IOException` from the tracker's WebSocket receive loop when a peer drops TCP uncleanly. Without the catch, these bubbled up as "unhandled application exception" fail-level entries in Kestrel's log, which on public-internet deployments (mobile clients, lossy networks, browser tab closes) fill the journal with alarming-looking but functionally harmless stack traces. Now swallowed with a debug-level log to the consumer-configured logger; peer cleanup in the finally block is unchanged.

Pure log hygiene. No functional change. 24 tests still pass.

## 1.1.3-rc.13 (2026-04-24)

### Relay port range for NAT deployments

`SpawnDev.RTC.Server 1.0.3-rc.3` exposes the `SpawnDev.SIPSorcery 10.0.5-rc.4` fork's new `TurnServerConfig.RelayPortRangeStart/End` as `StunTurnServerOptions.RelayPortRangeStart/End` + env-var (`RTC__StunTurn__RelayPortRangeStart` / `RTC__StunTurn__RelayPortRangeEnd`). When set, per-allocation TURN relay sockets bind within the configured range instead of the OS ephemeral pool. Required when the TURN host is behind a consumer NAT that cannot forward the full ephemeral range (16,000+ ports). Matches coturn's `--min-port`/`--max-port` and pion's `RelayAddressGenerator` port-range.

`SpawnDev.WebTorrent.ServerApp` (hub.spawndev.com production) also picks up the env-vars for Origin allowlist, STUN/TURN enable, and ephemeral credentials - previously only `SpawnDev.RTC.ServerApp` had them wired. Hub can now run embedded STUN/TURN without a coturn dependency.

1 new integration test brings the total to 24 pass. No other code changes; this is the deployment-readiness bump.

## 1.1.3-rc.12 (2026-04-24)

### Tracker-gated TURN, period-rotating secrets, and Origin allowlist

Three production-readiness features on top of rc.11's ephemeral-credential plumbing. All in `SpawnDev.RTC.Server 1.0.3-rc.2`; the main `SpawnDev.RTC` package moves forward only because its transitive reference to `SpawnDev.RTC.Server` changes.

**Tracker-gated TURN.** `EphemeralTurnCredentials.TrackerGatedResolver(sharedSecret, realm, tracker)` composes a `ResolveHmacKey` delegate that rejects allocations unless the credential's `userId` segment matches a peer currently announced to the signaling tracker. Closes the open gap where a stolen credential was useful for TURN even if the underlying signaling session had been torn down - now TURN follows tracker presence. New `TrackerSignalingServer.IsPeerConnected(peerId)` + `ConnectedPeerIds` snapshot APIs support the same check in consumer middleware.

**Period-rotating sub-secrets.** `EphemeralTurnCredentials.PeriodRotatingResolver(masterSecret, realm, periodSeconds)` + the matching `GeneratePeriodic(...)` issuer derive per-period sub-secrets as `HMAC-SHA256(masterSecret, floor(expiryUnix / periodSeconds))`. Both sides compute the same sub-secret deterministically from the expiry timestamp, so there's no period-boundary race. If the master leaks, only credentials whose expiry falls inside still-valid periods are at risk. Typical period: 1-24h.

**Origin-header allowlist on signaling.** `TrackerServerOptions.AllowedOrigins` rejects WebSocket upgrade requests whose `Origin` doesn't match any entry. Supports exact match (case-insensitive) and wildcard-subdomain form (`https://*.example.com`). When unset or empty, no check is performed (backward compatible). Rejection is HTTP 403 before the WebSocket handshake completes. `TrackerSignalingServer.IsOriginAllowed` is exposed publicly for consumer middleware. The server also now completes the WebSocket Close handshake with `CloseOutputAsync` on client-initiated disconnect - avoids "premature-EOF" exceptions on `ClientWebSocket.CloseAsync` in consuming code.

**hub.spawndev.com deployment knobs.** `SpawnDev.RTC.ServerApp` picks up env-var config for Origin allowlist, STUN/TURN enable/port/addresses, long-term or ephemeral credentials, and tracker-gating. Dockerfile exposes UDP 3478 + documents the hub-style config (see `SpawnDev.RTC.ServerApp/Dockerfile`).

12 new tests added (23 total in `DesktopTurnAuthTests`). End-to-end proofs include real `ClientWebSocket` upgrades against a real `WebApplication.CreateBuilder()` host with a TURN server bound to a TCP/UDP port and announcing peers via the tracker wire protocol. No mocks.

## 1.1.3-rc.11 (2026-04-24)

### Ephemeral TURN credentials (TURN REST API pattern)

`SpawnDev.RTC.Server` now supports time-limited HMAC-SHA1 TURN credentials, the industry-standard pattern used by Twilio, Cloudflare, and coturn's `--use-auth-secret`. Your app backend mints a credential pair from a shared secret; the TURN server validates without needing a user database.

New in `SpawnDev.RTC.Server`:

- `EphemeralTurnCredentials.Generate(sharedSecret, userId, lifetime)` - produces a `(username, password)` pair where the username encodes the expiry Unix timestamp and the password is `Base64(HMAC-SHA1(secret, username))`.
- `EphemeralTurnCredentials.Validate(sharedSecret, username, password)` - backend-side HMAC + expiry check, constant-time compare.
- `EphemeralTurnCredentials.ResolveLongTermKey(sharedSecret, realm, username)` - server-side resolver that computes the SipSorcery MESSAGE-INTEGRITY HMAC key from an ephemeral username. Returns null (rejects with 401) on expired or malformed usernames.
- `StunTurnServerOptions.EphemeralCredentialSharedSecret` - convenience knob. When set, the hosted service auto-wires the resolver; static `Username`/`Password` are ignored.
- `StunTurnServerOptions.ResolveHmacKey` - full-control resolver delegate for tracker-gated credentials, period-rotating keys, REST-API user-database lookups, etc. Takes the incoming STUN USERNAME and returns the 20-byte MD5 key (or null to reject).

Underlying fork change: `SpawnDev.SIPSorcery 10.0.5-rc.3` adds `TurnServerConfig.ResolveHmacKey` - a per-request HMAC-SHA1 key resolver hook on `TurnServer`. Backward compatible: when unset, classic long-term credentials work unchanged. Candidate for upstream SIPSorcery PR once validated in production.

11 new unit tests in `DesktopTurnAuthTests` cover: helper round-trip, tamper rejection, wrong-secret rejection, expired credential rejection, server/client key equivalence, DI lifecycle (enabled/disabled), full TURN Allocate round-trip with both long-term and ephemeral credentials, and expired-credential Allocate rejection (401).

## 1.1.3-rc.10 (2026-04-24)

### Simulcast control surface on IRTCRtpSender

Closes the last open Phase 7 plan item. `IRTCRtpSender` now exposes `GetParameters()` + `SetParameters(RTCRtpSendParameters)` with a cross-platform DTO shape:

- `RTCRtpSendParameters` — TransactionId (opaque round-trip token), Encodings array, Codecs array.
- `RTCRtpEncoding` — per-layer: Rid, Active, MaxBitrate, MaxFramerate, ScaleResolutionDownBy, ScalabilityMode, Priority, NetworkPriority.

Browser impl threads the call through BlazorJS 3.5.6's new typed `RTCRtpSendParameters` (7 new WebRTC dictionary wrappers in BlazorJS this version). Desktop impl returns a single-encoding default with a deterministic transactionId counter; SipSorcery has no native simulcast so SetParameters is a no-op beyond the transactionId-mismatch guard. The typed API is consistent across platforms even though real multi-layer behavior only activates on browser. Moves RTC from "simulcast not supported" to "simulcast API shipped; desktop impl pending Phase 5 SipSorcery native support."

4 new unit tests — shape, transactionId round-trip, stale-transactionId rejection (desktop-only), 3-layer simulcast DTO round-trip.

## 1.1.3-rc.9 (2026-04-24)

### Embedded STUN/TURN server in SpawnDev.RTC.Server

One ASP.NET Core host can now run WebSocket signaling + STUN binding + TURN relay together. New in `SpawnDev.RTC.Server`:

- `StunTurnServerHostedService` — ASP.NET Core `IHostedService` that starts/stops with the host. Wraps SipSorcery's RFC 5766 `TurnServer` (which handles both STUN binding requests and TURN allocation/relay in one process - no separate STUN daemon needed).
- `StunTurnServerOptions` — listen address, port (3478 default), TCP/UDP toggles, relay address for NAT'd deployments, long-term credential (username/password/realm), allocation lifetime.
- `AddRtcStunTurn()` DI extension on `IServiceCollection` - register via `builder.Services.AddRtcStunTurn(opts => { opts.Enabled = true; ... })` or bind a config section (`builder.Services.AddRtcStunTurn(builder.Configuration.GetSection("Turn"))`).
- Opt-in: `Enabled = false` by default so existing consumers who only want the WebSocket signaling tracker don't unexpectedly open UDP port 3478. Default credentials (`turn-user`/`turn-pass`) are intentionally weak and log a warning so consumers notice to replace them.

Usage typical shape:

```csharp
builder.Services.AddRtcStunTurn(opts =>
{
    opts.Enabled = true;
    opts.Port = 3478;
    opts.Username = builder.Configuration["Turn:Username"]!;
    opts.Password = builder.Configuration["Turn:Password"]!;
});
var app = builder.Build();
app.UseWebSockets();
app.UseRtcSignaling("/announce"); // WebSocket signaling
// STUN/TURN hosted service started automatically by the AddRtcStunTurn registration.
```

**Production posture:** SipSorcery's TurnServer is documented by its author as suitable for development, testing, and small-scale / embedded scenarios - not public-internet high-traffic TURN. For that, run coturn on dedicated hardware and leave `Enabled = false`. For self-hosted signaling + TURN on the same box for a small tenant (team chat, agent swarm, dev environment), it's fine.

New unit test: `StunServer_Loopback_BindingRequest_ReturnsXorMappedAddress` fires a minimal RFC 5389 STUN Binding Request at a loopback TurnServer and parses the XOR-MAPPED-ADDRESS from the response. Proves the server responds correctly under the exact config the hosted service uses. Desktop-only (browser has no UDP).

## 1.1.3-rc.8 (2026-04-24)

### Docs hygiene

- `PackageReleaseNotes` in the csproj pruned from an unreadable multi-release blob (every version since 1.1.0 in one XML node) to the current-version summary. Full history now lives here. No code changes from rc.7.

## 1.1.3-rc.7 (2026-04-24)

### Consume BlazorJS 3.5.5 typed RTCSctpTransport

- `BrowserRTCSctpTransport` reads `NativeTransport.State` / `Transport` / `MaxMessageSize` / `MaxChannels` directly instead of the rc.4 `JSRef.Get<T>()` plumbing (Data shipped the typed properties on the underlying BlazorJS wrapper in 3.5.5). Zero behavior change, cleaner code.

## 1.1.3-rc.5 (2026-04-24)

### PerfectNegotiator helper

- First public release of `SpawnDev.RTC.PerfectNegotiator`. (See the rc.6 entry below for full details - rc.6 includes a race fix and is the recommended version; consumers should skip rc.5.)

## 1.1.3-rc.6 (2026-04-24)

### PerfectNegotiator: glare-free renegotiation helper + HasNegotiated race fix

- New `SpawnDev.RTC.PerfectNegotiator` (shipped rc.5). Drop-in helper implementing the W3C Perfect Negotiation pattern (`https://w3c.github.io/webrtc-pc/#perfect-negotiation-example`) over any `IRTCPeerConnection`. Construct with a `polite` role flag + two signaling callbacks (`sendDescription`, `sendCandidate`); the helper auto-subscribes to `OnNegotiationNeeded` + `OnIceCandidate` and exposes `HandleRemoteDescriptionAsync` / `HandleRemoteCandidateAsync` for the app to call on incoming signaling messages. Glare resolution per the spec: impolite side wins offer collisions, polite side rolls back its in-flight offer and takes the remote. Works on both Browser (Blazor WASM → native RTCPeerConnection) and Desktop (SipSorcery).
- rc.6 fixes a race in `HasNegotiated`: rc.5 set the flag after `await sendDescription(...)`, but a consumer awaiting that callback (e.g. a test TaskCompletionSource) resumed in the same synchronous step and observed `HasNegotiated=false`. rc.6 flips the flag right after `SetLocalDescription()` succeeds, before the send-await.
- Also: `AutoSendsOfferOnNegotiationNeeded` test skips on desktop. SipSorcery doesn't fire `onnegotiationneeded` for `CreateDataChannel` the way the browser does (same skip pattern as the existing `Event_NegotiationNeeded_FiresOnAddTrack` test); desktop renegotiation is covered by `Renegotiation_AddTrackAfterConnect_Desktop` which doesn't rely on the auto-fire event.
- 8 new unit tests × 2 platforms = 18 test executions, 18/0/0 pass in 4 s. Closes the Phase 7 "perfect negotiation glare-free pattern + state-machine helpers" plan item.
- New doc: `Docs/perfect-negotiation.md` - pattern overview, usage, what the helper does for you, when to use it, constraints, platform notes, references.

## 1.1.3-rc.4 (2026-04-24)

### BrowserRTCSctpTransport: live JSRef reads

- `BrowserRTCSctpTransport` (`Browser/BrowserRTCTransport.cs`) was hardcoding `State="connected"`, `MaxMessageSize=262144`, `MaxChannels=65535` and `Transport=null!` as stubs because we were waiting for typed properties on the underlying BlazorJS `RTCSctpTransport` wrapper. rc.4 reads the spec properties directly via `JSRef.Get<T>(name)`: `state` → string, `transport` → `RTCDtlsTransport?` wrapped in `BrowserRTCDtlsTransport` on demand, `maxMessageSize` → double (clamped to `int` range), `maxChannels` → `int?`. Callers now get the live browser-reported values (Chrome 262144 vs Firefox larger for max message size is no longer stub fiction).
- Zero BlazorJS changes needed - the `RTCSctpTransport` JSObject wrapper already exists. Still nice-to-have is typed properties on the wrapper itself so this file can drop the `JSRef.Get` plumbing; tracked in DevComms `riker-to-data-blazorjs-rtc-sctp-properties-2026-04-24.md`, low priority.

## 1.1.3-rc.1 (2026-04-23)

### Phase 4b H.264 video bridge shipped

- New `DesktopRTCPeerConnection.AddTrack(SpawnDev.MultiMedia.IVideoTrack)` + `AddTrack(MultiMediaVideoSource)` overloads. A SpawnDev.MultiMedia `IVideoTrack` (webcam, screen-share, synthetic) feeds straight into SipSorcery's RTP video sender with H.264 encoding on the path.
- New `SpawnDev.RTC/Desktop/MultiMediaVideoSource.cs` - bridge from `IVideoTrack` → `SpawnDev.MultiMedia.IVideoEncoder` (factory dispatches to Windows MediaFoundation H.264 MFT in Phase 4b) → SipSorcery `IVideoSource`. Emits H.264 Annex-B NAL units; SipSorcery's existing RTP H.264 packetizer (RFC 6184 Single NAL / FU-A) handles fragmentation.
- Codec: H.264 baseline profile, CBR, low-latency mode (`CODECAPI_AVLowLatencyMode`), 1.5 Mbps default bitrate (configurable via `MultiMediaVideoSource.BitrateBps`), 90 kHz RTP clock. SDP advertises `H264 / 90000` with `packetization-mode=1`.
- `MultiMediaVideoSource.ForceKeyFrame()` triggers an IDR (today by restarting the encoder — sub-millisecond on hardware-accelerated MFTs).
- End-to-end test `Phase4b_Desktop_VideoBridge_EncodesAndNegotiatesH264`: two DesktopRTCPeerConnection instances exchange a synthetic 320x240 @ 30 fps NV12 pattern, assert OnTrack(video) fires, SDP contains `m=video` + `H264`, sender encoded ≥ 5 frames / ≥ 1000 bytes. Completes in ~500 ms.
- Windows-only until Phase 5 lands Linux (VAAPI) and macOS (VideoToolbox) encoders behind the same `VideoEncoderFactory.CreateH264` facade. Browser uses native WebRTC.
- Docs: new `Docs/video-tracks.md` walkthrough covering the minimal example + encoder config + test layout + file reference map.

### Upstream PR #1558 (SortMediaCapability) merged upstream

- The SipSorcery codec-priority inverted-ternary fix (at `RTPSession.cs:1221`) that shipped in SpawnDev.SIPSorcery 10.0.4-local.1 + our Phase 4a audio bridge work was [merged upstream](https://github.com/sipsorcery-org/sipsorcery/pull/1558) on 2026-04-23 by Aaron Clauson (merge commit `f3f32f9`). Future upstream SipSorcery releases carry the fix natively; our fork stays in sync.

### SCTP sender throughput fix (via SpawnDev.SIPSorcery 10.0.5-rc.1)

- Dep-bump to `SpawnDev.SIPSorcery 10.0.5-rc.1` which fixes the `SctpDataSender` producer-consumer lost-wakeup race. `_senderMre.Reset()` moved from AFTER the send work to the TOP of the `DoSend` loop so any `Set()` fired by SACK arrival during the send is preserved for the next `Wait(burstPeriod)` instead of being wiped.
- **60x speedup on the synthetic zero-RTT benchmark** (`SctpDataSenderUnitTest.Throughput_FastSackWake_ExceedsBurstCeiling`, 504 KB with same-thread SACK delivery): pre-fix 5613 ms / 89.8 KB/s → post-fix 94 ms / 5.4 MB/s. That number IS the upper bound the sender thread can produce when SACKs don't have to leave the host.
- **Real-world WebRTC loopback is still RTT-bound at `MAX_BURST × MTU / RTT ≈ 186 KB/s`** (Geordi measured ~0.15–0.19 MB/s end-to-end through a real DesktopRTCPeerConnection regardless of buffer size). The Reset-race fix is correct but not the dominant bottleneck once a real DTLS/UDP SACK RTT is in the loop. See `Docs/sctp-tuning.md` for the full analysis + the separate per-association `MAX_BURST` / `BURST_PERIOD` tunables shipping in 1.1.3-rc.2 that unlock the remaining throughput.
- Zero source changes in SpawnDev.RTC itself — pure dep swap. Submodule pointer bump to `LostBeard/sipsorcery 2c4bf7714`.
- Added `Docs/audio-tracks.md` (Phase 4a walkthrough) and `Docs/sctp-tuning.md` (the SCTP fix documented for consumers).
- Upstream PR to sipsorcery-org pending after downstream verification.

## 1.1.2 (2026-04-22 stable) — shipped what the Unreleased block below described

### Phase 4a - SpawnDev.MultiMedia audio bridge + SipSorcery fork codec-priority fix

- New `DesktopRTCPeerConnection.AddTrack(IAudioTrack)` overload. A SpawnDev.MultiMedia `IAudioTrack` (e.g. WASAPI microphone capture) feeds straight into SipSorcery's RTP sender. Default path encodes Opus (WebRTC browser-native codec) via Concentus; explicit encoder overrides supported via `AddTrack(MultiMediaAudioSource)`.
- New `SpawnDev.RTC/Desktop/MultiMediaAudioSource.cs` (internal bridge class, also usable directly for advanced codec control). Handles Float32 -> PCM16 conversion, 20 ms framing, per-codec sample-rate/channel validation with clear NotSupportedException on mismatches (no silent-garbage audio).
- Conditional sibling-repo `ProjectReference` for `SpawnDev.MultiMedia` added to `SpawnDev.RTC.csproj` (mirrors the existing sipsorcery submodule pattern). External consumers fall back to a PackageReference once the MultiMedia package ships.
- SipSorcery fork fix: `src/SIPSorcery/net/RTP/RTPSession.cs:1221` had an inverted ternary in the `SortMediaCapability` priority-track selection. Symptom: two peers with identical multi-codec audio format lists negotiated *different* selected formats from the same offer/answer (offerer saw PCMU, answerer saw Opus). Fixed in the fork; same fix filed as an upstream PR: [sipsorcery-org/sipsorcery#1558](https://github.com/sipsorcery-org/sipsorcery/pull/1558).
- New end-to-end test `RTCTestBase.Phase4MediaTests.cs` - two `DesktopRTCPeerConnection` instances negotiate a 48 kHz stereo synthetic sine wave, assert `OnTrack(audio)` fires, SDP contains `m=audio` + `opus`, and pc2 receives >= 5 non-empty encoded Opus RTP frames within 20 s. Covers both signaling AND real RTP delivery.
- Full RTC PlaywrightMultiTest suite: **261/0/0** with the Phase 4a tests included, zero regressions on the previous 259.

### Phase 4b (upcoming)

- Windows MediaFoundation H.264 encoder via P/Invoke in SpawnDev.MultiMedia, then `AddTrack(IVideoTrack)` bridge on the same shape as the audio bridge. Scoped in `Plans/PLAN-SpawnDev-MultiMedia.md` Phase 4.

## 1.1.0-rc.4 (2026-04-22)

### Desktop GetStats + stronger test coverage

- `DesktopRTCStatsReport` now emits W3C-standard `dataChannelsOpened` / `dataChannelsClosed` on the `peer-connection` entry, derived from `SIPSorcery.Net.RTCPeerConnection.DataChannels`. Existing Desktop-specific extras (`connectionState`, `signalingState`, `iceGatheringState`, `iceConnectionState`) remain on the same entry for monitoring tools.
- `PeerConnection_GetStats` test strengthened: waits for DC open, asserts `peer-connection` + `transport` entries exist, asserts W3C `dataChannelsOpened >= 1` after opening a DC, and on Desktop asserts `connectionState == "connected"` post-handshake. Runs on both browser + desktop.
- New `ServerApp.SmokeTest`: launches `SpawnDev.RTC.ServerApp` as a subprocess on default port 5590, polls `/health` until ready, then verifies `/`, `/health`, `/stats` JSON shapes + a real `TrackerSignalingClient` announce round-trip. Kills the subprocess on teardown.
- Full RTC PlaywrightMultiTest suite: **253 / 0 / 0** at this ship (`1.1.0-rc.3` baseline of 252 plus the new ServerApp smoke test).

## 1.1.0-rc.3 (2026-04-22)

### Legacy removal

The custom `/signal/{roomId}` protocol from the pre-tracker-migration era is deleted. The WebTorrent-tracker-compatible signaling (`SpawnDev.RTC.Signaling.TrackerSignalingClient` + `SpawnDev.RTC.Server.TrackerSignalingServer`) replaces it in full.

- Removed: `SpawnDev.RTC/RTCSignalClient.cs` (the custom-protocol client).
- Removed: `SpawnDev.RTC.SignalServer/` project (the custom-protocol standalone server).
- Removed: `PlaywrightMultiTest/StaticFileServer` `/signal/{roomId}` endpoint + `SignalRoom` / `SignalPeer` support types.
- Removed: `PlaywrightMultiTest/ProjectRunner` `CrossPlatform.Desktop_Browser_DataChannel` test + `RunDesktopSignalPeer` helper - superseded by `Signaling.CrossPlatform_BrowserDesktop` which tests the same scenario through the tracker wire protocol.
- `_run-demo.bat` now launches `SpawnDev.RTC.ServerApp` (standalone tracker on `http://localhost:5590`) instead of the deleted SignalServer.

The codebase is < a month old, so no external consumer is relying on these - this is a clean cut, not a deprecation dance.

## 1.1.0-rc.2 (2026-04-22)

### GetStats fix on both platforms

- `BrowserRTCStatsReport.Values` dict is now populated from the underlying JS `RTCStats` object via the typed `SpawnDev.BlazorJS.JSObjects.JSON.Stringify` helper. Consumers get the full W3C surface (`bytesReceived`, `roundTripTime`, `packetsLost`, `jitter`, `dataChannelsOpened/Closed`, etc.) rather than just `Id` / `Type` / `Timestamp`.
- Per-stat `RTCStats` JSObjects are now properly disposed (they leaked before).
- `DesktopRTCStatsReport` is no longer an empty stub. Returns two best-effort entries sourced from SipSorcery:
  - `peer-connection`: `connectionState`, `signalingState`, `iceGatheringState`, `iceConnectionState` (W3C shape: lowercase, hyphen-separated for signaling).
  - `transport`: `sessionId`, `dtlsState`, `dtlsCertificateSignatureAlgorithm`.
- SipSorcery's public API does not expose per-codec or per-candidate-pair counters, so those remain browser-only.

## 1.1.0-rc.1 (2026-04-22)

### `SpawnDev.RTC.Signaling` namespace (tracker-signaling migration Phase 1)

- New `ISignalingClient` + `ISignalingRoomHandler` interfaces with DTOs (`SignalingOffer`, `AnnounceOptions`, `SignalingSwarmStats`, `TrackerWireMessages`).
- `RoomKey` value type: SHA-1 of raw UTF-8 room name. **No normalization.** Any consumer that trims or lowercases silently joins a different swarm; xmldoc calls this out. Also supports `FromBytes`, `FromHex`, `Random`, `FromGuid`.
- `TrackerSignalingClient`: WebSocket tracker protocol client, binary-string latin1 framing is byte-compatible with plain JS WebTorrent peers. Shared socket pool (one connection per `announceUrl` + `peerId`). Reconnect with exponential backoff. Per-room offer/answer routing via `ISignalingRoomHandler`.
- `RtcPeerConnectionRoomHandler`: default room handler with an `IRTCPeerConnection` pool. Events `OnPeerConnection`, `OnDataChannel`, `OnPeerDisconnected`, `OnPeerConnectionCreated`.
- `BinaryJsonSerializer`: latin1-safe JSON helper for binary fields over WebSocket.

### Pairs with a new `SpawnDev.RTC.Server` NuGet package

- `TrackerSignalingServer` + `app.UseRtcSignaling("/announce")` extension let any ASP.NET Core app host a WebTorrent-compatible tracker in one line.
- Also available as a standalone executable + Docker image via `SpawnDev.RTC.ServerApp`.

### Legacy `RTCTrackerClient` removed

- Deleted in favour of `TrackerSignalingClient`. Consumers migrate by changing the type name and calling `Subscribe(room, handler)` + `AnnounceAsync(room, options)`.

### Consumers migrated

- `SpawnDev.RTC.Demo/Pages/ChatRoom.razor` (adopted `IAsyncDisposable`, preserves `OnPeerConnectionCreated` for `OnTrack` wiring).
- `SpawnDev.RTC.DemoConsole/ChatMode.cs`.
- `SpawnDev.RTC.WpfDemo/MainWindow.xaml.cs` (plus new `WpfVideoRenderer.cs` completing the MultiMedia integration from 1.0.1).
- `PlaywrightMultiTest`: 3 new `Signaling.*` integration tests (`Signaling.Embedded_TwoPeers`, `Signaling.Live_OpenWebTorrent`, `Signaling.CrossPlatform_BrowserDesktop`) plus `Signaling.RoomIsolation`.

### SipSorcery fork

- `NetServices` static constructor guarded against `PlatformNotSupportedException` on Blazor WASM (`NetworkChange.NetworkAddressChanged` subscription wrapped in try/catch). Ships the earlier 1.0.1-rc.1 fix.
- `WaitForIceGatheringToComplete` option on `RTCOfferOptions` / `RTCAnswerOptions` for synchronous SDP gathering on Desktop (required for non-trickle signaling scenarios).

### Tests

- Full RTC PlaywrightMultiTest suite: 253 / 0 / 0.
- `Signaling.*` filter: 8 / 8 (includes `Signaling.RoomIsolation` asserting peers in one room never appear in another room's announce response).

## 1.0.0 (2026-04-15)

Initial release. Built from scratch in a single day.

### Features
- Full W3C WebRTC API on browser (Blazor WASM) and desktop (.NET)
- Data channels: string, binary, zero-copy JS types (ArrayBuffer, TypedArray, Blob, DataView)
- Media streams: getUserMedia, getDisplayMedia, tracks, enable/disable, clone
- Transceivers: addTransceiver, direction control, getTransceivers
- ICE: trickle, restart, candidate gathering, STUN/TURN configuration
- SDP: offer/answer, implicit SetLocalDescription (perfect negotiation)
- Stats: GetStats with candidate-pair, transport stats
- DTMF: InsertDTMF, ToneBuffer
- Transport abstractions: DTLS, ICE, SCTP
- Configuration: BundlePolicy, IceTransportPolicy, IceCandidatePoolSize
- RTCTrackerClient: serverless signaling via WebTorrent tracker protocol (removed in 1.1.0-rc.1; replaced by `SpawnDev.RTC.Signaling.TrackerSignalingClient`)
- RTCSignalClient: custom WebSocket signal server client (removed in 1.1.0-rc.3; replaced by `SpawnDev.RTC.Signaling.TrackerSignalingClient`)
- Signal server: standalone + embedded in test infrastructure (removed in 1.1.0-rc.3; replaced by `SpawnDev.RTC.Server` + `SpawnDev.RTC.ServerApp`)

### Demos
- Browser ChatRoom: video/audio/text conference with swarm signaling
- WPF desktop ChatRoom: text chat with peer list
- Console chat: text-only via tracker
- All demos serverless (openwebtorrent tracker, no server deployment)

### Testing
- 204 tests, 0 failures
- Pixel-level video verification (red, blue, split-screen spatial)
- SHA-256 data integrity verification
- Cross-platform desktop-to-browser via embedded signal server AND tracker
- Live openwebtorrent tracker integration test
- 5 simultaneous peer pairs stress test
- 256KB max payload, 100-message burst, 20 channel lifecycle

### SipSorcery Fork
- SRTP profiles restricted to 3 browser-compatible (AES-128-GCM, AES-256-GCM, AES128-CM-SHA1-80)
- TFMs trimmed to net48/net8.0/net9.0/net10.0

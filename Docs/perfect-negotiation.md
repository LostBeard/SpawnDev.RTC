# Perfect Negotiation (Glare-Free Renegotiation)

WebRTC lets either peer re-trigger SDP negotiation after the initial connection — adding a track, adding a data channel, stopping a transceiver — via the `onnegotiationneeded` event. On a live connection both peers can legitimately try to renegotiate at the same instant, which produces offer/answer collision ("glare"). The W3C specifies a deterministic resolution pattern called **Perfect Negotiation**; SpawnDev.RTC ships it as a drop-in helper in 1.1.3-rc.5+.

## The Pattern

One peer is designated **polite**, the other **impolite**. Roles can be assigned however you like (the inviter is typically impolite, the invitee polite; or flip a coin; or use a lex-compared peer id). On collision:

- The **impolite** peer wins — it keeps its in-flight offer, ignores the incoming one, and the other side waits.
- The **polite** peer loses — it rolls back its offer via `SetLocalDescription()` (which transitions back to `stable`), then applies the incoming offer normally.

With that rule plus the W3C-idiomatic parameterless `SetLocalDescription()` (implicitly creates offer or answer based on current signaling state), both peers end up in the same negotiated state regardless of which side's local state got there first.

## Usage

```csharp
using SpawnDev.RTC;

var pc = RTCPeerConnectionFactory.Create(config);

// Roles: impolite peer sends offers first; polite peer defers on collision.
var neg = new PerfectNegotiator(
    pc,
    polite: imPolite,
    sendDescription: desc => SignalingChannel.SendAsync(new { type = "desc", desc }),
    sendCandidate:   cand => SignalingChannel.SendAsync(new { type = "cand", cand }));

// Receive side - dispatch incoming signaling messages to the helper.
SignalingChannel.OnMessage += async msg =>
{
    if (msg.type == "desc") await neg.HandleRemoteDescriptionAsync(msg.desc);
    else if (msg.type == "cand") await neg.HandleRemoteCandidateAsync(msg.cand);
};

// That's it. Subsequent AddTrack / AddTransceiver / CreateDataChannel
// from either side triggers renegotiation automatically.
pc.AddTrack(audioTrack, audioStream);
// ... later, possibly at the same instant on the other side ...
pc.AddTrack(videoTrack, videoStream);
```

## What the Helper Does for You

On construction:

- Subscribes to `pc.OnNegotiationNeeded` → calls parameterless `SetLocalDescription()` → ships the result through your `sendDescription` callback. Sets `makingOffer = true` for the duration of the local offer operation so incoming remote offers during that window are detected as collisions.
- Subscribes to `pc.OnIceCandidate` → ships each candidate through your `sendCandidate` callback.

On `HandleRemoteDescriptionAsync`:

- If the incoming description is an offer and `(makingOffer || signalingState != "stable")`, it's a collision. The impolite side ignores the offer (`ignoreOffer = true`); the polite side proceeds.
- Applies `SetRemoteDescription(desc)` (rolling back its own offer implicitly if polite + collision).
- If the description was an offer, generates and ships the answer via parameterless `SetLocalDescription()`.

On `HandleRemoteCandidateAsync`:

- Applies the candidate via `pc.AddIceCandidate`. Swallows failures that occur only because the candidate's parent offer was ignored (see `ignoreOffer` above); rethrows anything else so the caller can log transport issues.

## When to Use It

Use Perfect Negotiation when EITHER of these is true:

1. Both peers can add media tracks / transceivers / data channels at arbitrary times after the initial connection.
2. Your signaling channel is full-duplex and either side can initiate a new offer.

If your protocol is strictly one-shot (peer A always offers, peer B always answers, no post-connect renegotiation), you do **not** need Perfect Negotiation — the built-in `RtcPeerConnectionRoomHandler` is simpler.

## Constraints

- The helper assumes the provided `sendDescription` and `sendCandidate` callbacks are reliable. If your transport can drop messages, the peers can de-sync; pair this helper with a reliable signaling channel (WebSocket, tracker, etc.).
- Roles must be deterministic. Both peers independently knowing "I am polite" (or "I am impolite") is what makes glare resolution converge. If both peers think they're polite or both think they're impolite, the pattern doesn't work.
- `polite` is immutable after construction. Re-create the helper if a role swap is needed (rare).

## Platform Notes

Works on Browser (Blazor WASM → native `RTCPeerConnection`) and Desktop (→ SipSorcery). Auto-fire of `onnegotiationneeded` from `CreateDataChannel` is browser-only on a fresh peer; SipSorcery's behavior there is less reliable, so the desktop auto-fire test is skipped (see `RTCTestBase.PerfectNegotiatorTests.cs`). Desktop renegotiation driven explicitly by AddTrack post-connect is covered by `Renegotiation_AddTrackAfterConnect_Desktop` and works end-to-end.

## References

- W3C WebRTC spec: <https://w3c.github.io/webrtc-pc/#perfect-negotiation-example>
- Jan-Ivar Bruaroey's blog post (original write-up): <https://blog.mozilla.org/webrtc/perfect-negotiation-in-webrtc/>
- Implementation: `SpawnDev.RTC/PerfectNegotiator.cs`
- Tests: `SpawnDev.RTC.Demo.Shared/UnitTests/RTCTestBase.PerfectNegotiatorTests.cs`

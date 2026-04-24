namespace SpawnDev.RTC;

/// <summary>
/// Implementation of the W3C "Perfect Negotiation" pattern for WebRTC. Wraps an
/// <see cref="IRTCPeerConnection"/> and a bidirectional signaling channel so that
/// both peers can call <c>AddTrack</c> / <c>AddTransceiver</c> / <c>CreateDataChannel</c>
/// concurrently without glare (offer/answer collision).
///
/// Reference: <c>https://w3c.github.io/webrtc-pc/#perfect-negotiation-example</c>.
///
/// Usage:
/// <code>
/// var neg = new PerfectNegotiator(
///     pc,
///     polite: isBobTheSecondPeer,
///     sendDescription: desc =&gt; signaling.Send(new { type="desc", desc }),
///     sendCandidate: cand =&gt; signaling.Send(new { type="cand", cand }));
///
/// // Receive side of signaling:
/// signaling.OnMessage += async msg =&gt;
/// {
///     if (msg.type == "desc") await neg.HandleRemoteDescriptionAsync(msg.desc);
///     else if (msg.type == "cand") await neg.HandleRemoteCandidateAsync(msg.cand);
/// };
/// </code>
///
/// With the helper wired up, subsequent <c>pc.AddTrack(...)</c> / <c>pc.AddTransceiver(...)</c>
/// / <c>pc.CreateDataChannel(...)</c> calls from EITHER peer trigger renegotiation
/// automatically, and simultaneous renegotiations resolve via the polite/impolite
/// role without data loss. The impolite side wins collisions; the polite side
/// rolls back its in-flight offer and applies the incoming one.
/// </summary>
public sealed class PerfectNegotiator : IDisposable
{
    private readonly IRTCPeerConnection _pc;
    private readonly bool _polite;
    private readonly Func<RTCSessionDescriptionInit, Task> _sendDescription;
    private readonly Func<RTCIceCandidateInit, Task> _sendCandidate;

    private volatile bool _makingOffer;
    private volatile bool _ignoreOffer;
    private volatile bool _isSettingRemoteAnswerPending;
    private bool _disposed;

    /// <summary>
    /// True when this side rolls back its offer on collision. Exactly one peer in
    /// a negotiating pair should be polite. Typical assignment: the peer that
    /// received the invitation is polite; the peer that initiated is impolite.
    /// </summary>
    public bool Polite => _polite;

    /// <summary>True once at least one successful renegotiation has run.</summary>
    public bool HasNegotiated { get; private set; }

    /// <summary>
    /// Construct a negotiator for <paramref name="pc"/>. Hooks
    /// <see cref="IRTCPeerConnection.OnNegotiationNeeded"/> and
    /// <see cref="IRTCPeerConnection.OnIceCandidate"/> immediately so the helper
    /// will auto-send offers and candidates as soon as they're produced. The two
    /// <see cref="Func{T, TResult}"/> delegates are how the helper talks to the
    /// signaling channel - the caller is responsible for delivering the descriptions
    /// and candidates to the remote side via whatever transport they use (tracker,
    /// websocket, etc.).
    /// </summary>
    public PerfectNegotiator(
        IRTCPeerConnection pc,
        bool polite,
        Func<RTCSessionDescriptionInit, Task> sendDescription,
        Func<RTCIceCandidateInit, Task> sendCandidate)
    {
        _pc = pc ?? throw new ArgumentNullException(nameof(pc));
        _polite = polite;
        _sendDescription = sendDescription ?? throw new ArgumentNullException(nameof(sendDescription));
        _sendCandidate = sendCandidate ?? throw new ArgumentNullException(nameof(sendCandidate));

        _pc.OnNegotiationNeeded += HandleNegotiationNeeded;
        _pc.OnIceCandidate += HandleIceCandidate;
    }

    private async void HandleNegotiationNeeded()
    {
        try
        {
            _makingOffer = true;
            // Parameterless SetLocalDescription() is the W3C Perfect Negotiation idiom -
            // it implicitly creates an offer or answer as appropriate for the current
            // signaling state. Supported on both browser + desktop as of 1.1.0.
            await _pc.SetLocalDescription();
            if (_pc.LocalDescription != null)
                await _sendDescription(_pc.LocalDescription);
            HasNegotiated = true;
        }
        catch (Exception)
        {
            // Don't crash the peer connection on signaling errors - renegotiation
            // can be retried on the next needsNegotiation event. If sendDescription
            // throws, it's the caller's responsibility to handle signaling-channel
            // failures out-of-band.
        }
        finally
        {
            _makingOffer = false;
        }
    }

    private async void HandleIceCandidate(RTCIceCandidateInit candidate)
    {
        try { await _sendCandidate(candidate); }
        catch { /* Signaling-layer failure - caller handles */ }
    }

    /// <summary>
    /// Apply a remote description received over the signaling channel. Implements
    /// the glare-resolution logic: on collision the polite side rolls back its
    /// in-flight offer and takes the remote one; the impolite side ignores the
    /// incoming offer (the polite side will send an answer once the rollback
    /// completes).
    /// </summary>
    public async Task HandleRemoteDescriptionAsync(RTCSessionDescriptionInit description)
    {
        if (description == null) throw new ArgumentNullException(nameof(description));

        // Determine collision: an incoming offer collides if we're mid-offer OR the
        // signaling state isn't "stable" (which can happen briefly between our own
        // SetLocalDescription and SetRemoteDescription calls).
        var readyForOffer = !_makingOffer
            && (_pc.SignalingState == "stable" || _isSettingRemoteAnswerPending);
        var offerCollision = description.Type == "offer" && !readyForOffer;
        _ignoreOffer = !_polite && offerCollision;
        if (_ignoreOffer) return;

        _isSettingRemoteAnswerPending = description.Type == "answer";
        await _pc.SetRemoteDescription(description);
        _isSettingRemoteAnswerPending = false;

        if (description.Type == "offer")
        {
            // Parameterless SetLocalDescription() implicitly creates the answer since
            // signaling state is now have-remote-offer.
            await _pc.SetLocalDescription();
            if (_pc.LocalDescription != null)
                await _sendDescription(_pc.LocalDescription);
            HasNegotiated = true;
        }
    }

    /// <summary>
    /// Apply a remote ICE candidate. Ignores errors that happen only because we
    /// chose to ignore the corresponding offer (see <see cref="HandleRemoteDescriptionAsync"/>).
    /// Any other candidate-add failure is rethrown so the caller can log it.
    /// </summary>
    public async Task HandleRemoteCandidateAsync(RTCIceCandidateInit candidate)
    {
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));
        try { await _pc.AddIceCandidate(candidate); }
        catch
        {
            if (!_ignoreOffer) throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pc.OnNegotiationNeeded -= HandleNegotiationNeeded; } catch { }
        try { _pc.OnIceCandidate -= HandleIceCandidate; } catch { }
    }
}

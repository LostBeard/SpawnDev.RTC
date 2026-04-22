namespace SpawnDev.RTC.Signaling;

/// <summary>
/// Consumer-supplied hook set for one signaling room. An <see cref="ISignalingClient"/>
/// calls into this handler to generate offers, respond to remote offers, and route
/// answers back to the peers that produced the original offer. Implementations own
/// the actual <c>RTCPeerConnection</c> instances and the offer-id → peer mapping.
/// </summary>
public interface ISignalingRoomHandler
{
    /// <summary>
    /// Produce up to <paramref name="count"/> offers for the next announce. Return fewer
    /// if some fail; the client sends whatever is returned. Implementations must remember
    /// the offer-id they generate so they can correlate a later
    /// <see cref="HandleAnswerAsync"/> back to the originating peer connection.
    /// </summary>
    Task<IReadOnlyList<SignalingOffer>> CreateOffersAsync(int count, CancellationToken ct);

    /// <summary>
    /// A remote peer has sent us an offer. Return the SDP of the answer to send back,
    /// or <c>null</c> to drop the offer. The client forwards the answer to the tracker
    /// with the original <paramref name="offerId"/> so the remote peer can correlate.
    /// </summary>
    Task<string?> HandleOfferAsync(byte[] remotePeerId, byte[] offerId, string offerSdp, CancellationToken ct);

    /// <summary>
    /// A remote peer has answered one of our offers identified by <paramref name="offerId"/>.
    /// Implementations look up the pending peer connection, apply the answer, and wire up
    /// any data/media channels the caller expects.
    /// </summary>
    Task HandleAnswerAsync(byte[] remotePeerId, byte[] offerId, string answerSdp, CancellationToken ct);
}

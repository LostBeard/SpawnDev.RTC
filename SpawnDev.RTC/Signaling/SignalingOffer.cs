namespace SpawnDev.RTC.Signaling;

/// <summary>
/// A locally generated offer to send with the next announce. <see cref="OfferId"/> must
/// be 20 random bytes and must be unique within the caller's pending-offer table - the
/// matching answer comes back carrying the same bytes.
/// </summary>
public sealed record SignalingOffer(byte[] OfferId, string OfferSdp);

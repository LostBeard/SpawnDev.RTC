using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SpawnDev.RTC.Signaling;

/// <summary>
/// Reusable <see cref="ISignalingRoomHandler"/> that owns a pool of <see cref="IRTCPeerConnection"/>
/// instances - one per remote peer discovered in the room. Handles the offer/answer dance on the
/// caller's behalf and surfaces peer and data-channel events via .NET events, so a consumer can
/// be connected with minimal code.
///
/// Typical wiring:
/// <code>
/// var client = new TrackerSignalingClient(trackerUrl, peerId);
/// var handler = new RtcPeerConnectionRoomHandler(config);
/// handler.OnDataChannel += (ch, remotePeerId) => { ... };
/// client.Subscribe(RoomKey.FromString("lobby"), handler);
/// await client.AnnounceAsync(RoomKey.FromString("lobby"), new AnnounceOptions { Event = "started" });
/// </code>
///
/// The handler expects at least one data channel per peer. Override <see cref="DataChannelLabel"/>
/// to change the label used on outbound offers, or pre-create channels on the peer connection in
/// <see cref="OnPeerConnectionCreated"/> for finer control.
/// </summary>
public class RtcPeerConnectionRoomHandler : ISignalingRoomHandler, IDisposable
{
    private readonly RTCPeerConnectionConfig? _config;

    // offer-id (20 raw bytes, hex-keyed) → (pc, local dc)
    private readonly ConcurrentDictionary<string, (IRTCPeerConnection pc, IRTCDataChannel? dc)> _pendingOffers = new();

    // remote peer id (hex) → the connection currently serving that peer
    private readonly ConcurrentDictionary<string, PeerEntry> _peers = new();

    // Serializes the glare tie-break. The offer/answer SDP work stays outside it.
    private readonly object _peersLock = new();

    /// <summary>
    /// One established connection to a remote peer, tagged with the offer id that produced it.
    /// The offer id is the glare tie-break key - see <see cref="TryInstallPeer"/>.
    /// </summary>
    private sealed class PeerEntry
    {
        public required IRTCPeerConnection Pc { get; init; }
        public required string OfferIdHex { get; init; }
    }

    private int _defaultOfferCount = 5;
    private bool _disposed;

    /// <summary>Data channel label used on offers this handler initiates. Defaults to <c>"data"</c>.</summary>
    public string DataChannelLabel { get; set; } = "data";

    /// <summary>Peer connection config (ICE servers, etc). Can be null for platform defaults.</summary>
    public RTCPeerConnectionConfig? Config => _config;

    /// <summary>
    /// Optional hook invoked right after an <see cref="IRTCPeerConnection"/> is created, before any
    /// offer/answer is generated. Caller can attach tracks, additional data channels, or event wiring.
    /// The <see cref="string"/> argument is the remote peer id (hex), or <see cref="string.Empty"/>
    /// for outbound offers whose remote peer id is not known yet.
    /// </summary>
    public Func<IRTCPeerConnection, string, Task>? OnPeerConnectionCreated { get; set; }

    /// <summary>Raised when a peer connection has a remote description set. Argument is remote peer id (hex).</summary>
    public event Action<IRTCPeerConnection, string>? OnPeerConnection;

    /// <summary>Raised for each data channel opened on a peer connection in this room (local or remote).</summary>
    public event Action<IRTCDataChannel, string>? OnDataChannel;

    /// <summary>Raised when a peer's connection state drops to <c>disconnected</c>, <c>failed</c>, or <c>closed</c>.</summary>
    public event Action<string>? OnPeerDisconnected;

    public RtcPeerConnectionRoomHandler(RTCPeerConnectionConfig? config = null)
    {
        _config = config;
    }

    /// <summary>Currently connected remote peer ids (hex).</summary>
    public IReadOnlyCollection<string> ConnectedPeers => _peers.Keys.ToArray();

    // ========================
    // ISignalingRoomHandler
    // ========================

    public virtual async Task<IReadOnlyList<SignalingOffer>> CreateOffersAsync(int count, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count <= 0) count = _defaultOfferCount;

        var offers = new List<SignalingOffer>(count);
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var pc = RTCPeerConnectionFactory.Create(_config);
            var dc = pc.CreateDataChannel(DataChannelLabel);

            if (OnPeerConnectionCreated != null)
                await OnPeerConnectionCreated(pc, string.Empty).ConfigureAwait(false);

            var offer = await pc.CreateOffer().ConfigureAwait(false);
            await pc.SetLocalDescription(offer).ConfigureAwait(false);

            // The WebTorrent tracker protocol does not support trickle-ICE - the full SDP
            // (with candidates) must be in the announce payload. On Browser and most Desktop
            // stacks, CreateOffer returns before ICE gathering completes, so we must wait.
            var fullSdp = await WaitForIceGatheringCompleteAsync(pc, offer.Sdp ?? "", ct).ConfigureAwait(false);

            var offerIdBytes = new byte[20];
            RandomNumberGenerator.Fill(offerIdBytes);
            var offerIdHex = Convert.ToHexString(offerIdBytes).ToLowerInvariant();

            _pendingOffers[offerIdHex] = (pc, dc);
            offers.Add(new SignalingOffer(offerIdBytes, fullSdp));
        }
        return offers;
    }

    public virtual async Task<string?> HandleOfferAsync(byte[] remotePeerId, byte[] offerId, string offerSdp, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var remoteHex = Convert.ToHexString(remotePeerId).ToLowerInvariant();
        var offerIdHex = Convert.ToHexString(offerId).ToLowerInvariant();

        // Cheap pre-check so we don't build an answer we are only going to throw away.
        // TryInstallPeer re-decides authoritatively under the lock.
        if (_peers.TryGetValue(remoteHex, out var seen) && !Wins(offerIdHex, seen.OfferIdHex)) return null;

        var pc = RTCPeerConnectionFactory.Create(_config);
        WirePeer(pc, remoteHex);

        if (OnPeerConnectionCreated != null)
            await OnPeerConnectionCreated(pc, remoteHex).ConfigureAwait(false);

        await pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "offer", Sdp = offerSdp }).ConfigureAwait(false);
        var answer = await pc.CreateAnswer().ConfigureAwait(false);
        await pc.SetLocalDescription(answer).ConfigureAwait(false);

        // Same reason as CreateOffersAsync - WebTorrent tracker protocol doesn't trickle,
        // so the answer SDP must have candidates embedded before we return it.
        var fullSdp = await WaitForIceGatheringCompleteAsync(pc, answer.Sdp ?? "", ct).ConfigureAwait(false);

        if (!TryInstallPeer(remoteHex, new PeerEntry { Pc = pc, OfferIdHex = offerIdHex }))
        {
            try { pc.Close(); } catch { }
            pc.Dispose();
            return null;
        }

        OnPeerConnection?.Invoke(pc, remoteHex);
        return fullSdp;
    }

    /// <summary>
    /// Waits for ICE gathering on <paramref name="pc"/> to reach <c>complete</c>, then returns
    /// the current local-description SDP (which will now include the gathered candidates).
    /// Capped at 5 seconds to avoid hanging on malfunctioning STUN - after the cap we return
    /// whatever SDP is available.
    /// </summary>
    private static async Task<string> WaitForIceGatheringCompleteAsync(IRTCPeerConnection pc, string fallbackSdp, CancellationToken ct)
    {
        // Already complete - return the current local description which has candidates.
        if (pc.IceGatheringState == "complete")
            return pc.LocalDescription?.Sdp ?? fallbackSdp;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string state) { if (state == "complete") tcs.TrySetResult(true); }
        pc.OnIceGatheringStateChange += Handler;
        try
        {
            // Re-check post-subscribe in case the state transitioned between the first check and now.
            if (pc.IceGatheringState == "complete") return pc.LocalDescription?.Sdp ?? fallbackSdp;

            await Task.WhenAny(tcs.Task, Task.Delay(5000, ct)).ConfigureAwait(false);
        }
        finally
        {
            pc.OnIceGatheringStateChange -= Handler;
        }
        return pc.LocalDescription?.Sdp ?? fallbackSdp;
    }

    public virtual async Task HandleAnswerAsync(byte[] remotePeerId, byte[] offerId, string answerSdp, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var offerIdHex = Convert.ToHexString(offerId).ToLowerInvariant();
        if (!_pendingOffers.TryRemove(offerIdHex, out var entry)) return;

        var pc = entry.pc;
        var localDc = entry.dc;
        var remoteHex = Convert.ToHexString(remotePeerId).ToLowerInvariant();

        // Cheap pre-check; TryInstallPeer re-decides authoritatively under the lock.
        if (_peers.TryGetValue(remoteHex, out var seen) && !Wins(offerIdHex, seen.OfferIdHex))
        {
            try { pc.Close(); } catch { }
            pc.Dispose();
            return;
        }

        WirePeer(pc, remoteHex);
        await pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "answer", Sdp = answerSdp }).ConfigureAwait(false);

        if (!TryInstallPeer(remoteHex, new PeerEntry { Pc = pc, OfferIdHex = offerIdHex }))
        {
            try { pc.Close(); } catch { }
            pc.Dispose();
            return;
        }

        OnPeerConnection?.Invoke(pc, remoteHex);

        if (localDc != null) OnDataChannel?.Invoke(localDc, remoteHex);
    }

    /// <summary>
    /// Glare tie-break. Two peers that announce at the same moment each receive the other's
    /// offer and each answers it, so both end up holding the half of a connection whose
    /// counterpart the other peer discarded - and nothing ever connects. Both peers see the
    /// same pair of offer ids, so both can pick the same winner with no extra signaling:
    /// the lowest offer id wins. Ids are 20 random bytes, so ties do not occur.
    /// </summary>
    private static bool Wins(string candidateOfferIdHex, string incumbentOfferIdHex)
        => string.CompareOrdinal(candidateOfferIdHex, incumbentOfferIdHex) < 0;

    /// <summary>
    /// Installs <paramref name="entry"/> as the connection for <paramref name="remoteHex"/>,
    /// applying <see cref="Wins"/> against any incumbent. Returns false if the incumbent wins,
    /// in which case the caller must close and dispose its connection. A displaced incumbent is
    /// closed here, after the swap, so <see cref="WirePeer"/>'s identity guard sees the winner.
    /// </summary>
    private bool TryInstallPeer(string remoteHex, PeerEntry entry)
    {
        IRTCPeerConnection? displaced = null;
        lock (_peersLock)
        {
            if (_peers.TryGetValue(remoteHex, out var incumbent))
            {
                if (!Wins(entry.OfferIdHex, incumbent.OfferIdHex)) return false;
                displaced = incumbent.Pc;
            }
            _peers[remoteHex] = entry;
        }
        if (displaced != null)
        {
            try { displaced.Close(); } catch { }
            displaced.Dispose();
        }
        return true;
    }

    // ========================
    // INTERNAL
    // ========================

    private void WirePeer(IRTCPeerConnection pc, string remoteHex)
    {
        pc.OnDataChannel += ch => OnDataChannel?.Invoke(ch, remoteHex);
        pc.OnConnectionStateChange += state =>
        {
            if (state == "disconnected" || state == "failed" || state == "closed")
            {
                bool removed;
                lock (_peersLock)
                {
                    // Only tear down the room entry if THIS connection is still the one serving
                    // the peer. A connection displaced by the glare tie-break is closed on
                    // purpose and must not evict the winner that replaced it.
                    removed = _peers.TryGetValue(remoteHex, out var current)
                              && ReferenceEquals(current.Pc, pc)
                              && _peers.TryRemove(remoteHex, out _);
                }
                if (removed) OnPeerDisconnected?.Invoke(remoteHex);
            }
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _pendingOffers)
        {
            try { kvp.Value.pc.Close(); } catch { }
            kvp.Value.pc.Dispose();
        }
        _pendingOffers.Clear();

        foreach (var kvp in _peers)
        {
            try { kvp.Value.Pc.Close(); } catch { }
            kvp.Value.Pc.Dispose();
        }
        _peers.Clear();
    }
}

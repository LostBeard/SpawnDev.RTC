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

    // remote peer id (hex) → pc
    private readonly ConcurrentDictionary<string, IRTCPeerConnection> _peers = new();

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

            var offerIdBytes = new byte[20];
            RandomNumberGenerator.Fill(offerIdBytes);
            var offerIdHex = Convert.ToHexString(offerIdBytes).ToLowerInvariant();

            _pendingOffers[offerIdHex] = (pc, dc);
            offers.Add(new SignalingOffer(offerIdBytes, offer.Sdp ?? ""));
        }
        return offers;
    }

    public virtual async Task<string?> HandleOfferAsync(byte[] remotePeerId, byte[] offerId, string offerSdp, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var remoteHex = Convert.ToHexString(remotePeerId).ToLowerInvariant();
        if (_peers.ContainsKey(remoteHex)) return null; // already paired

        var pc = RTCPeerConnectionFactory.Create(_config);
        WirePeer(pc, remoteHex);

        if (OnPeerConnectionCreated != null)
            await OnPeerConnectionCreated(pc, remoteHex).ConfigureAwait(false);

        await pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "offer", Sdp = offerSdp }).ConfigureAwait(false);
        var answer = await pc.CreateAnswer().ConfigureAwait(false);
        await pc.SetLocalDescription(answer).ConfigureAwait(false);

        _peers[remoteHex] = pc;
        OnPeerConnection?.Invoke(pc, remoteHex);
        return answer.Sdp;
    }

    public virtual async Task HandleAnswerAsync(byte[] remotePeerId, byte[] offerId, string answerSdp, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var offerIdHex = Convert.ToHexString(offerId).ToLowerInvariant();
        if (!_pendingOffers.TryRemove(offerIdHex, out var entry)) return;

        var pc = entry.pc;
        var localDc = entry.dc;
        var remoteHex = Convert.ToHexString(remotePeerId).ToLowerInvariant();

        WirePeer(pc, remoteHex);
        await pc.SetRemoteDescription(new RTCSessionDescriptionInit { Type = "answer", Sdp = answerSdp }).ConfigureAwait(false);

        _peers[remoteHex] = pc;
        OnPeerConnection?.Invoke(pc, remoteHex);

        if (localDc != null) OnDataChannel?.Invoke(localDc, remoteHex);
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
                if (_peers.TryRemove(remoteHex, out _))
                    OnPeerDisconnected?.Invoke(remoteHex);
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
            try { kvp.Value.Close(); } catch { }
            kvp.Value.Dispose();
        }
        _peers.Clear();
    }
}

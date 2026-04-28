using System.Text;
using SIPSorcery.Net;
using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCDataChannel.
    /// Wraps SipSorcery's RTCDataChannel.
    /// JS type overloads (ArrayBuffer, TypedArray, Blob) throw PlatformNotSupportedException.
    /// </summary>
    public class DesktopRTCDataChannel : IRTCDataChannel
    {
        /// <summary>
        /// Direct access to the underlying SipSorcery RTCDataChannel.
        /// </summary>
        public RTCDataChannel NativeChannel { get; }

        private bool _disposed;

        public string Label => NativeChannel.label;
        public string ReadyState => NativeChannel.readyState.ToString();
        public ushort? Id => NativeChannel.id;
        public bool Ordered => NativeChannel.ordered;
        public string Protocol => NativeChannel.protocol ?? "";
        public bool Negotiated => NativeChannel.negotiated;
        public long BufferedAmount => (long)NativeChannel.bufferedAmount;

        public ushort? MaxPacketLifeTime => NativeChannel.maxPacketLifeTime;
        public ushort? MaxRetransmits => NativeChannel.maxRetransmits;
        public long BufferedAmountLowThreshold
        {
            get => (long)NativeChannel.bufferedAmountLowThreshold;
            set
            {
                NativeChannel.bufferedAmountLowThreshold = (ulong)value;
                // SipSorcery doesn't natively raise the bufferedamountlow event, so we
                // emulate it: poll bufferedAmount and fire OnBufferedAmountLow when it
                // transitions from above-threshold to below-threshold (matches the
                // edge-triggered semantics of the spec). Without this, callers that
                // rely on the event (like SpawnDev.WebTorrent's RtcPeer.Send for SCTP
                // backpressure) sit on a 30-second WaitAsync timeout for every chunk
                // sent over the threshold, which collapses into the wire dying mid-
                // transfer (TimeoutException -> Peer.Destroy) on multi-MB tensor
                // pushes. Diagnosed 2026-04-29 via [RtcPeer-DIAG] logging in
                // SpawnDev.ILGPU.P2P's LargeBuffer_1MB test: the timeout fired with
                // BufferedAmount=0 (drained) and ReadyState=open (channel fine), so
                // the missing signal was the only thing wrong.
                StartBufferedAmountLowPoller();
            }
        }
        public string BinaryType
        {
            get => NativeChannel.binaryType ?? "arraybuffer";
            set => NativeChannel.binaryType = value;
        }

        public event Action? OnOpen;
        public event Action? OnClose;
        public event Action? OnClosing;  // SipSorcery doesn't have this - never fires
        public event Action? OnBufferedAmountLow;  // Fired by the emulated poller in StartBufferedAmountLowPoller.
        public event Action<string>? OnStringMessage;
        public event Action<byte[]>? OnBinaryMessage;
        public event Action<ArrayBuffer>? OnArrayBufferMessage;  // Never fires on desktop
        public event Action<string>? OnError;

        public DesktopRTCDataChannel(RTCDataChannel channel)
        {
            NativeChannel = channel;
            NativeChannel.onopen += HandleOpen;
            NativeChannel.onclose += HandleClose;
            NativeChannel.onmessage += HandleMessage;
            NativeChannel.onerror += HandleError;
        }

        // --- Send: universal ---
        public void Send(string data) => NativeChannel.send(data);
        public void Send(byte[] data) => NativeChannel.send(data);

        // --- Send: JS types (not supported on desktop) ---
        public void Send(ArrayBuffer data) =>
            throw new PlatformNotSupportedException("ArrayBuffer is only available in Blazor WASM. Use Send(byte[]) on desktop.");

        public void Send(TypedArray data) =>
            throw new PlatformNotSupportedException("TypedArray is only available in Blazor WASM. Use Send(byte[]) on desktop.");

        public void Send(Blob data) =>
            throw new PlatformNotSupportedException("Blob is only available in Blazor WASM. Use Send(byte[]) on desktop.");

        public void Send(DataView data) =>
            throw new PlatformNotSupportedException("DataView is only available in Blazor WASM. Use Send(byte[]) on desktop.");

        public void Close() => NativeChannel.close();

        private void HandleOpen()
        {
            OnOpen?.Invoke();
        }

        private void HandleClose()
        {
            OnClose?.Invoke();
        }

        private void HandleMessage(RTCDataChannel dc, DataChannelPayloadProtocols protocol, byte[] data)
        {
            switch (protocol)
            {
                case DataChannelPayloadProtocols.WebRTC_String:
                    OnStringMessage?.Invoke(Encoding.UTF8.GetString(data));
                    break;
                case DataChannelPayloadProtocols.WebRTC_String_Empty:
                    OnStringMessage?.Invoke("");
                    break;
                case DataChannelPayloadProtocols.WebRTC_Binary:
                    OnBinaryMessage?.Invoke(data);
                    break;
                case DataChannelPayloadProtocols.WebRTC_Binary_Empty:
                    OnBinaryMessage?.Invoke(System.Array.Empty<byte>());
                    break;
                default:
                    OnBinaryMessage?.Invoke(data);
                    break;
            }
            // OnArrayBufferMessage intentionally never fires on desktop
        }

        private void HandleError(string error)
        {
            OnError?.Invoke(error);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _bufferPollCts?.Cancel(); } catch { }
            try { _bufferPollCts?.Dispose(); } catch { }
            NativeChannel.onopen -= HandleOpen;
            NativeChannel.onclose -= HandleClose;
            NativeChannel.onmessage -= HandleMessage;
            NativeChannel.onerror -= HandleError;
        }

        // ===== Emulated OnBufferedAmountLow polling =====
        //
        // SipSorcery's RTCDataChannel does not expose a "bufferedamountlow" event
        // hook; bufferedAmount is updated as SCTP drains but no callback fires when
        // it crosses the threshold. We emulate the spec'd edge-triggered behavior
        // with a single polling task per channel: when bufferedAmount transitions
        // from above-threshold to at-or-below-threshold, OnBufferedAmountLow fires
        // exactly once. The poller exits when the channel is disposed or its
        // ReadyState leaves "open".

        private System.Threading.CancellationTokenSource? _bufferPollCts;
        private int _bufferPollerStarted; // 0 = not started, 1 = started

        private void StartBufferedAmountLowPoller()
        {
            // Once-and-done: only start the poller the first time the threshold is set.
            if (System.Threading.Interlocked.Exchange(ref _bufferPollerStarted, 1) != 0)
                return;

            _bufferPollCts = new System.Threading.CancellationTokenSource();
            var ct = _bufferPollCts.Token;
            _ = Task.Run(async () =>
            {
                bool wasAboveThreshold = false;
                while (!ct.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        if (NativeChannel.readyState != RTCDataChannelState.open)
                        {
                            await Task.Delay(50, ct).ConfigureAwait(false);
                            continue;
                        }
                        var threshold = (long)NativeChannel.bufferedAmountLowThreshold;
                        var current = (long)NativeChannel.bufferedAmount;
                        if (current > threshold)
                        {
                            wasAboveThreshold = true;
                        }
                        else if (wasAboveThreshold)
                        {
                            // Transitioned downward across the threshold. Fire ONCE.
                            wasAboveThreshold = false;
                            try { OnBufferedAmountLow?.Invoke(); }
                            catch { /* never let consumer exceptions bubble out of the poller */ }
                        }
                        await Task.Delay(20, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        // Defensive: keep polling even if a transient native call throws.
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                }
            }, ct);
        }
    }
}

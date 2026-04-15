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

        public event Action? OnOpen;
        public event Action? OnClose;
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
            NativeChannel.onopen -= HandleOpen;
            NativeChannel.onclose -= HandleClose;
            NativeChannel.onmessage -= HandleMessage;
            NativeChannel.onerror -= HandleError;
        }
    }
}

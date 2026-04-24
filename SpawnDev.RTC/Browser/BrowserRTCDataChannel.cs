using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.JSObjects.WebRTC;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of IRTCDataChannel.
    /// Wraps the native browser RTCDataChannel via SpawnDev.BlazorJS.
    /// Supports zero-copy JS types (ArrayBuffer, TypedArray, Blob).
    /// </summary>
    public class BrowserRTCDataChannel : IRTCDataChannel
    {
        /// <summary>
        /// Direct access to the underlying BlazorJS RTCDataChannel JSObject.
        /// Use this for advanced JS interop without going through the abstraction.
        /// </summary>
        public RTCDataChannel NativeChannel { get; }

        private bool _disposed;

        public string Label => NativeChannel.Label;
        public string ReadyState => NativeChannel.ReadyState;
        public ushort? Id => NativeChannel.Id;
        public bool Ordered => NativeChannel.Ordered;
        public string Protocol => NativeChannel.Protocol;
        public bool Negotiated => NativeChannel.Negotiated;
        public long BufferedAmount => NativeChannel.BufferedAmount;

        public ushort? MaxPacketLifeTime => NativeChannel.MaxPacketLifeTime;
        public ushort? MaxRetransmits => NativeChannel.MaxRetransmits;
        public long BufferedAmountLowThreshold
        {
            get => NativeChannel.BufferedAmountLowThreshold;
            set => NativeChannel.BufferedAmountLowThreshold = value;
        }
        public string BinaryType
        {
            get => NativeChannel.BinaryType;
            set => NativeChannel.BinaryType = value;
        }

        public event Action? OnOpen;
        public event Action? OnClose;
        public event Action? OnClosing;
        public event Action? OnBufferedAmountLow;
        public event Action<string>? OnStringMessage;
        public event Action<byte[]>? OnBinaryMessage;
        public event Action<ArrayBuffer>? OnArrayBufferMessage;
        public event Action<string>? OnError;

        public BrowserRTCDataChannel(RTCDataChannel channel)
        {
            NativeChannel = channel;
            NativeChannel.BinaryType = "arraybuffer";
            NativeChannel.OnOpen += HandleOpen;
            NativeChannel.OnClose += HandleClose;
            NativeChannel.OnClosing += HandleClosing;
            NativeChannel.OnBufferedAmountLow += HandleBufferedAmountLow;
            NativeChannel.OnMessage += HandleMessage;
            NativeChannel.OnError += HandleError;
        }

        // --- Send: universal ---
        public void Send(string data) => NativeChannel.Send(data);
        public void Send(byte[] data) => NativeChannel.Send(data);

        // --- Send: JS types (zero-copy in WASM) ---
        public void Send(ArrayBuffer data) => NativeChannel.Send(data);
        public void Send(TypedArray data) => NativeChannel.Send(data);
        public void Send(Blob data) => NativeChannel.Send(data);
        public void Send(DataView data) => NativeChannel.Send(data);

        public void Close() => NativeChannel.Close();

        private void HandleOpen(RTCDataChannelEvent e)
        {
            OnOpen?.Invoke();
        }

        private void HandleClose(Event e)
        {
            OnClose?.Invoke();
        }

        private void HandleClosing(Event e)
        {
            OnClosing?.Invoke();
        }

        private void HandleBufferedAmountLow(Event e)
        {
            OnBufferedAmountLow?.Invoke();
        }

        private void HandleMessage(MessageEvent e)
        {
            var dataType = e.JSRef!.TypeOf("data");
            if (dataType == "string")
            {
                var str = e.JSRef!.Get<string>("data");
                OnStringMessage?.Invoke(str);
            }
            else
            {
                // ArrayBuffer path - zero-copy first, then byte[] for convenience
                var arrayBuffer = e.JSRef!.Get<ArrayBuffer>("data");

                // Fire zero-copy event first (caller takes ownership)
                if (OnArrayBufferMessage != null)
                {
                    OnArrayBufferMessage.Invoke(arrayBuffer);
                }

                // Fire byte[] event if subscribed (copies data to .NET)
                if (OnBinaryMessage != null)
                {
                    var bytes = (byte[])arrayBuffer;
                    OnBinaryMessage.Invoke(bytes);
                }

                // Only dispose if nobody took ownership via OnArrayBufferMessage
                if (OnArrayBufferMessage == null)
                {
                    arrayBuffer.Dispose();
                }
            }
        }

        private void HandleError(RTCErrorEvent e)
        {
            // RTCErrorEvent wraps an RTCError in its .error property — the useful
            // diagnostics (message, errorDetail, sctpCauseCode, etc.) live THERE,
            // not on the event itself. Reading .message on the event always returns
            // nothing useful; this surfaces the real details so consumers can actually
            // diagnose data-channel failures instead of being told "Unknown error".
            // Spec: https://www.w3.org/TR/webrtc/#dom-rtcerror
            if (e.JSRef == null)
            {
                OnError?.Invoke("Unknown error (null RTCErrorEvent.JSRef)");
                return;
            }

            var name = e.JSRef.Get<string?>("error.name");
            var detail = e.JSRef.Get<string?>("error.errorDetail");
            var msg = e.JSRef.Get<string?>("error.message");
            var sctpCause = e.JSRef.Get<int?>("error.sctpCauseCode");
            var sdpLine = e.JSRef.Get<int?>("error.sdpLineNumber");
            var httpStatus = e.JSRef.Get<int?>("error.httpRequestStatusCode");
            var recvAlert = e.JSRef.Get<int?>("error.receivedAlert");
            var sentAlert = e.JSRef.Get<int?>("error.sentAlert");

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(detail)) parts.Add(detail!);
            if (!string.IsNullOrEmpty(msg)) parts.Add(msg!);
            if (!string.IsNullOrEmpty(name)) parts.Add($"name={name}");
            if (sctpCause.HasValue) parts.Add($"sctpCauseCode={sctpCause}");
            if (sdpLine.HasValue) parts.Add($"sdpLineNumber={sdpLine}");
            if (httpStatus.HasValue) parts.Add($"httpStatus={httpStatus}");
            if (recvAlert.HasValue) parts.Add($"receivedAlert={recvAlert}");
            if (sentAlert.HasValue) parts.Add($"sentAlert={sentAlert}");

            // Last-resort fallback so we never silently swallow the error.
            if (parts.Count == 0)
            {
                var topMsg = e.JSRef.Get<string?>("message");
                parts.Add(!string.IsNullOrEmpty(topMsg)
                    ? topMsg!
                    : "RTCErrorEvent with no message/errorDetail/name fields");
            }

            OnError?.Invoke(string.Join(" | ", parts));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NativeChannel.OnOpen -= HandleOpen;
            NativeChannel.OnClose -= HandleClose;
            NativeChannel.OnClosing -= HandleClosing;
            NativeChannel.OnBufferedAmountLow -= HandleBufferedAmountLow;
            NativeChannel.OnMessage -= HandleMessage;
            NativeChannel.OnError -= HandleError;
            NativeChannel.Dispose();
        }
    }
}

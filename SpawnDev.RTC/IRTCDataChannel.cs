using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform WebRTC data channel.
    /// API mirrors the W3C RTCDataChannel specification.
    /// </summary>
    public interface IRTCDataChannel : IDisposable
    {
        // --- Properties ---
        string Label { get; }
        string ReadyState { get; }
        ushort? Id { get; }
        bool Ordered { get; }
        string Protocol { get; }
        bool Negotiated { get; }
        long BufferedAmount { get; }
        ushort? MaxPacketLifeTime { get; }
        ushort? MaxRetransmits { get; }
        long BufferedAmountLowThreshold { get; set; }
        string BinaryType { get; set; }

        // --- Send: universal (all platforms) ---
        void Send(string data);
        void Send(byte[] data);

        // --- Send: WASM only (throws PlatformNotSupportedException on desktop) ---
        void Send(ArrayBuffer data);
        void Send(TypedArray data);
        void Send(Blob data);
        void Send(DataView data);

        void Close();

        // --- Events ---

        /// <summary>
        /// The data channel has been opened and communication is possible.
        /// </summary>
        event Action? OnOpen;

        /// <summary>
        /// The data channel has been closed.
        /// </summary>
        event Action? OnClose;

        /// <summary>
        /// The data channel is closing (before fully closed).
        /// </summary>
        event Action? OnClosing;

        /// <summary>
        /// The outgoing buffer has dropped below BufferedAmountLowThreshold.
        /// </summary>
        event Action? OnBufferedAmountLow;

        /// <summary>
        /// A string message was received. Works on all platforms.
        /// </summary>
        event Action<string>? OnStringMessage;

        /// <summary>
        /// A binary message was received as byte[]. Works on all platforms.
        /// In WASM, this copies data from JS to .NET. For zero-copy, use OnArrayBufferMessage.
        /// </summary>
        event Action<byte[]>? OnBinaryMessage;

        /// <summary>
        /// A binary message was received as ArrayBuffer. WASM only - zero-copy.
        /// On desktop, this event never fires.
        /// Caller is responsible for disposing the ArrayBuffer.
        /// </summary>
        event Action<ArrayBuffer>? OnArrayBufferMessage;

        /// <summary>
        /// An error occurred on the data channel.
        /// </summary>
        event Action<string>? OnError;
    }
}

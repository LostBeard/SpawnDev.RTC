namespace SpawnDev.RTC
{
    /// <summary>
    /// Cross-platform data channel interface.
    /// Browser: wraps native RTCDataChannel via SpawnDev.BlazorJS.
    /// Desktop: wraps SipSorcery RTCDataChannel.
    /// </summary>
    public interface IRTCDataChannel : IDisposable
    {
        string Label { get; }
        string ReadyState { get; }
        ushort? Id { get; }
        bool Ordered { get; }
        string Protocol { get; }
        bool Negotiated { get; }
        long BufferedAmount { get; }

        void Send(string data);
        void Send(byte[] data);
        void Close();

        /// <summary>
        /// Fired when the data channel is open and ready for communication.
        /// </summary>
        event Action? OnOpen;

        /// <summary>
        /// Fired when the data channel is closed.
        /// </summary>
        event Action? OnClose;

        /// <summary>
        /// Fired when a string message is received.
        /// </summary>
        event Action<string>? OnStringMessage;

        /// <summary>
        /// Fired when a binary message is received.
        /// </summary>
        event Action<byte[]>? OnBinaryMessage;

        /// <summary>
        /// Fired when an error occurs on the data channel.
        /// </summary>
        event Action<string>? OnError;
    }
}

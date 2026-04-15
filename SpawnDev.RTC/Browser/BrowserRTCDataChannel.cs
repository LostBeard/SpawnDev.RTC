using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.JSObjects.WebRTC;
using System.Text;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of IRTCDataChannel.
    /// Wraps the native browser RTCDataChannel via SpawnDev.BlazorJS.
    /// </summary>
    public class BrowserRTCDataChannel : IRTCDataChannel
    {
        private readonly RTCDataChannel _channel;
        private bool _disposed;

        public string Label => _channel.Label;
        public string ReadyState => _channel.ReadyState;
        public ushort? Id => _channel.Id;
        public bool Ordered => _channel.Ordered;
        public string Protocol => _channel.Protocol;
        public bool Negotiated => _channel.Negotiated;
        public long BufferedAmount => _channel.BufferedAmount;

        public event Action? OnOpen;
        public event Action? OnClose;
        public event Action<string>? OnStringMessage;
        public event Action<byte[]>? OnBinaryMessage;
        public event Action<string>? OnError;

        public BrowserRTCDataChannel(RTCDataChannel channel)
        {
            _channel = channel;
            _channel.BinaryType = "arraybuffer";
            _channel.OnOpen += HandleOpen;
            _channel.OnClose += HandleClose;
            _channel.OnMessage += HandleMessage;
            _channel.OnError += HandleError;
        }

        public void Send(string data) => _channel.Send(data);

        public void Send(byte[] data) => _channel.Send(data);

        public void Close() => _channel.Close();

        private void HandleOpen(RTCDataChannelEvent e)
        {
            OnOpen?.Invoke();
        }

        private void HandleClose(Event e)
        {
            OnClose?.Invoke();
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
                // ArrayBuffer - convert to byte[]
                using var arrayBuffer = e.JSRef!.Get<ArrayBuffer>("data");
                var bytes = (byte[])arrayBuffer;
                OnBinaryMessage?.Invoke(bytes);
            }
        }

        private void HandleError(RTCErrorEvent e)
        {
            var message = e.JSRef?.Get<string?>("message") ?? "Unknown error";
            OnError?.Invoke(message);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _channel.OnOpen -= HandleOpen;
            _channel.OnClose -= HandleClose;
            _channel.OnMessage -= HandleMessage;
            _channel.OnError -= HandleError;
            _channel.Dispose();
        }
    }
}

using System.Text;
using SIPSorcery.Net;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCDataChannel.
    /// Wraps SipSorcery's RTCDataChannel.
    /// </summary>
    public class DesktopRTCDataChannel : IRTCDataChannel
    {
        private readonly RTCDataChannel _channel;
        private bool _disposed;

        public string Label => _channel.label;
        public string ReadyState => _channel.readyState.ToString();
        public ushort? Id => _channel.id;
        public bool Ordered => _channel.ordered;
        public string Protocol => _channel.protocol ?? "";
        public bool Negotiated => _channel.negotiated;
        public long BufferedAmount => (long)_channel.bufferedAmount;

        public event Action? OnOpen;
        public event Action? OnClose;
        public event Action<string>? OnStringMessage;
        public event Action<byte[]>? OnBinaryMessage;
        public event Action<string>? OnError;

        public DesktopRTCDataChannel(RTCDataChannel channel)
        {
            _channel = channel;
            _channel.onopen += HandleOpen;
            _channel.onclose += HandleClose;
            _channel.onmessage += HandleMessage;
            _channel.onerror += HandleError;
        }

        public void Send(string data) => _channel.send(data);

        public void Send(byte[] data) => _channel.send(data);

        public void Close() => _channel.close();

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
                    OnBinaryMessage?.Invoke(Array.Empty<byte>());
                    break;
                default:
                    OnBinaryMessage?.Invoke(data);
                    break;
            }
        }

        private void HandleError(string error)
        {
            OnError?.Invoke(error);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _channel.onopen -= HandleOpen;
            _channel.onclose -= HandleClose;
            _channel.onmessage -= HandleMessage;
            _channel.onerror -= HandleError;
        }
    }
}

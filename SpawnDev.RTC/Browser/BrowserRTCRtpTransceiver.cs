using SpawnDev.BlazorJS.JSObjects.WebRTC;

namespace SpawnDev.RTC.Browser
{
    /// <summary>
    /// Browser implementation of IRTCRtpTransceiver.
    /// </summary>
    public class BrowserRTCRtpTransceiver : IRTCRtpTransceiver
    {
        public RTCRtpTransceiver NativeTransceiver { get; }

        public string? Mid => NativeTransceiver.Mid;
        public string Direction { get => NativeTransceiver.Direction; set => NativeTransceiver.Direction = value; }
        public string? CurrentDirection => NativeTransceiver.CurrentDirection;
        public IRTCRtpSender Sender => new BrowserRtpSender(NativeTransceiver.Sender);
        public IRTCRtpReceiver Receiver => new BrowserRtpReceiver(NativeTransceiver.Receiver);

        public BrowserRTCRtpTransceiver(RTCRtpTransceiver transceiver)
        {
            NativeTransceiver = transceiver;
        }

        public void Stop() => NativeTransceiver.Stop();
    }
}

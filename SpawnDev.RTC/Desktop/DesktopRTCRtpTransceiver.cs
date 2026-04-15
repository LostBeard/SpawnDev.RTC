using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCRtpTransceiver.
    /// Maps SipSorcery's addTrack model to the W3C transceiver API.
    /// SipSorcery doesn't have native transceivers, so this wraps a
    /// sender/receiver pair created from a MediaStreamTrack.
    /// </summary>
    public class DesktopRTCRtpTransceiver : IRTCRtpTransceiver
    {
        private readonly RTCPeerConnection _pc;
        private readonly MediaStreamTrack _nativeTrack;
        private string _direction = "sendrecv";
        private bool _stopped;

        public string? Mid => null; // SipSorcery doesn't assign MIDs in the same way
        public string Direction
        {
            get => _stopped ? "stopped" : _direction;
            set
            {
                if (_stopped) return;
                _direction = value;
                // SipSorcery StreamStatus is internal set - we track direction ourselves
                // The direction will be applied during SDP negotiation
            }
        }
        public string? CurrentDirection => _stopped ? "stopped" : _direction;
        public IRTCRtpSender Sender { get; }
        public IRTCRtpReceiver Receiver { get; }

        public DesktopRTCRtpTransceiver(RTCPeerConnection pc, MediaStreamTrack track)
        {
            _pc = pc;
            _nativeTrack = track;
            var wrappedTrack = new DesktopRTCMediaStreamTrack(track);
            Sender = new DesktopRtpSender(wrappedTrack, pc);
            Receiver = new DesktopRtpReceiver(wrappedTrack);
        }

        public void Stop()
        {
            _stopped = true;
        }
    }
}

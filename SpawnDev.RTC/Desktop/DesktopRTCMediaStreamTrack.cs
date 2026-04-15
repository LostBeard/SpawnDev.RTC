using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCMediaStreamTrack.
    /// Wraps SipSorcery's MediaStreamTrack.
    /// </summary>
    public class DesktopRTCMediaStreamTrack : IRTCMediaStreamTrack
    {
        public MediaStreamTrack NativeTrack { get; }
        private bool _disposed;
        private bool _stopped;
        private bool _enabled = true;

        public string Id => NativeTrack.Ssrc.ToString();
        public string Kind => NativeTrack.Kind == SDPMediaTypesEnum.audio ? "audio" : NativeTrack.Kind == SDPMediaTypesEnum.video ? "video" : NativeTrack.Kind.ToString().ToLowerInvariant();
        public string Label => $"{Kind} ({NativeTrack.Ssrc})";

        public bool Enabled
        {
            get => _enabled && !_stopped;
            set => _enabled = value;
        }

        public bool Muted => !_enabled || _stopped;
        public string ReadyState => _stopped ? "ended" : "live";

        public event Action? OnEnded;
        public event Action? OnMute;
        public event Action? OnUnmute;

        public DesktopRTCMediaStreamTrack(MediaStreamTrack track)
        {
            NativeTrack = track;
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _enabled = false;
            OnEnded?.Invoke();
        }

        public IRTCMediaStreamTrack Clone()
        {
            // Create a new SipSorcery track with the same capabilities
            var cloned = new MediaStreamTrack(
                NativeTrack.Kind,
                NativeTrack.IsRemote,
                new List<SDPAudioVideoMediaFormat>(NativeTrack.Capabilities),
                NativeTrack.StreamStatus
            );
            return new DesktopRTCMediaStreamTrack(cloned);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    /// <summary>
    /// Desktop implementation of IRTCRtpSender.
    /// </summary>
    public class DesktopRtpSender : IRTCRtpSender
    {
        public IRTCMediaStreamTrack? Track { get; private set; }
        public IRTCDTMFSender? DTMF => null; // SipSorcery DTMF is handled via RTP events, not per-sender
        private readonly SIPSorcery.Net.RTCPeerConnection? _pc;

        public DesktopRtpSender(IRTCMediaStreamTrack? track, SIPSorcery.Net.RTCPeerConnection? pc = null)
        {
            Track = track;
            _pc = pc;
        }

        public Task ReplaceTrack(IRTCMediaStreamTrack? track)
        {
            Track = track;
            return Task.CompletedTask;
        }

        public void SetStreams(params IRTCMediaStream[] streams)
        {
            // SipSorcery doesn't have per-sender stream association
            // Streams are managed at the track/session level
        }
    }

    /// <summary>
    /// Desktop implementation of IRTCRtpReceiver.
    /// </summary>
    public class DesktopRtpReceiver : IRTCRtpReceiver
    {
        public IRTCMediaStreamTrack Track { get; }

        public DesktopRtpReceiver(IRTCMediaStreamTrack track)
        {
            Track = track;
        }
    }
}

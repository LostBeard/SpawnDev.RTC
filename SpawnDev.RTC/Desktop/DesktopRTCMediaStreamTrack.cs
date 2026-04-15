using SIPSorcery.Net;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCMediaStreamTrack.
    /// Wraps SipSorcery's MediaStreamTrack.
    /// </summary>
    public class DesktopRTCMediaStreamTrack : IRTCMediaStreamTrack
    {
        /// <summary>
        /// Direct access to the underlying SipSorcery MediaStreamTrack.
        /// </summary>
        public MediaStreamTrack NativeTrack { get; }

        private bool _disposed;

        public string Id => NativeTrack.Ssrc.ToString();
        public string Kind => NativeTrack.Kind.ToString().ToLowerInvariant();
        public string Label => $"{NativeTrack.Kind} ({NativeTrack.Ssrc})";
        public bool Enabled { get => NativeTrack.StreamStatus != MediaStreamStatusEnum.Inactive; set { } }
        public bool Muted => NativeTrack.StreamStatus == MediaStreamStatusEnum.Inactive;
        public string ReadyState => NativeTrack.StreamStatus == MediaStreamStatusEnum.Inactive ? "ended" : "live";

        public event Action? OnEnded;
        public event Action? OnMute;
        public event Action? OnUnmute;

        public DesktopRTCMediaStreamTrack(MediaStreamTrack track)
        {
            NativeTrack = track;
        }

        public void Stop()
        {
            // SipSorcery doesn't have a direct Stop() on tracks
            OnEnded?.Invoke();
        }

        public IRTCMediaStreamTrack Clone()
        {
            // SipSorcery tracks can't be cloned - create a new wrapper
            return new DesktopRTCMediaStreamTrack(NativeTrack);
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
        public IRTCMediaStreamTrack? Track { get; }

        public DesktopRtpSender(IRTCMediaStreamTrack? track)
        {
            Track = track;
        }

        public Task ReplaceTrack(IRTCMediaStreamTrack? track)
        {
            throw new NotImplementedException("ReplaceTrack is not yet supported on desktop.");
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

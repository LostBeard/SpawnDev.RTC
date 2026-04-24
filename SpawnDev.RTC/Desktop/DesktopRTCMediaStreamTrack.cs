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

        private string _contentHint = "";
        private MediaTrackConstraints _constraints = new();

        public string ContentHint { get => _contentHint; set => _contentHint = value; }

        public RTCMediaTrackSettings GetSettings()
        {
            var settings = new RTCMediaTrackSettings();
            if (NativeTrack.Kind == SDPMediaTypesEnum.audio)
            {
                settings.SampleRate = 8000;
                settings.ChannelCount = 1;
            }
            return settings;
        }

        public MediaTrackConstraints GetConstraints() => _constraints;

        public Task ApplyConstraints(MediaTrackConstraints constraints)
        {
            _constraints = constraints;
            return Task.CompletedTask;
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

        public Task<IRTCStatsReport> GetStats()
        {
            return Task.FromResult<IRTCStatsReport>(new DesktopRTCStatsReport());
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

        // Desktop-side transactionId counter for the getParameters/setParameters
        // token dance. SipSorcery doesn't natively enforce this, but we produce a
        // monotonic token so consumers that round-trip it see consistent behavior
        // with the browser implementation.
        private static long _transactionCounter;
        private string _lastTransactionId = "";

        /// <summary>
        /// Returns minimal send parameters for the desktop backend. SipSorcery does
        /// NOT implement simulcast (single encoding per track), so <c>Encodings</c>
        /// is a single-entry array representing the current track. The browser
        /// simulcast knobs (maxBitrate, scaleResolutionDownBy, scalabilityMode)
        /// are all unset — applying them via <see cref="SetParameters"/> has no
        /// effect today; this is documented as a Phase 5 gap in the library plan.
        /// </summary>
        public RTCRtpSendParameters GetParameters()
        {
            _lastTransactionId = System.Threading.Interlocked.Increment(ref _transactionCounter).ToString("D");
            return new RTCRtpSendParameters
            {
                TransactionId = _lastTransactionId,
                Encodings = new[] { new RTCRtpEncoding { Active = true } },
                // SipSorcery's codec info could be populated from the PC's negotiated
                // SDP; leaving null for now — the in-scope simulcast consumer doesn't
                // need it, and desktop SetCodecPreferences is the proper codec API.
                Codecs = null,
            };
        }

        /// <summary>
        /// Desktop SetParameters is a no-op today. SipSorcery doesn't expose per-encoding
        /// simulcast control — the single RTP track handles everything. Validates the
        /// transactionId matches the most recent GetParameters (mirroring browser
        /// behavior) so consumers get consistent error reporting across platforms.
        /// </summary>
        public Task SetParameters(RTCRtpSendParameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (!string.IsNullOrEmpty(parameters.TransactionId)
                && !string.IsNullOrEmpty(_lastTransactionId)
                && parameters.TransactionId != _lastTransactionId)
            {
                throw new InvalidOperationException(
                    $"SetParameters transactionId mismatch: expected {_lastTransactionId}, got {parameters.TransactionId}. " +
                    "Call GetParameters first, modify the returned object, then pass it to SetParameters unchanged except for the fields you want to change.");
            }
            // SipSorcery doesn't natively support simulcast, so the encoding knobs
            // (maxBitrate, scaleResolutionDownBy, scalabilityMode) are accepted but
            // ignored today. Multi-encoding arrays that would turn on simulcast on
            // the browser side are a no-op here. See the class-level remark on
            // GetParameters for the longer context.
            return Task.CompletedTask;
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

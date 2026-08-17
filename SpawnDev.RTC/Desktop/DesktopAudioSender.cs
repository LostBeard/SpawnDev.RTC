using System;
using SpawnDev.RTC.Audio;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop <see cref="IRTCAudioSender"/>: feeds pushed PCM into a trackless
    /// <see cref="MultiMediaAudioSource"/> that Opus-encodes it and emits RTP through the SipSorcery
    /// peer connection. The source is added to the connection on construction, so its <see cref="Track"/>
    /// is already wired for transmission.
    /// <para>
    /// The encode runs at the source's selected format (Opus WebRTC = 48 kHz), updated by SDP
    /// negotiation. Pushed PCM must match that rate/channel layout - the bridge does not resample.
    /// </para>
    /// </summary>
    public sealed class DesktopAudioSender : IRTCAudioSender
    {
        private readonly MultiMediaAudioSource _source;
        private readonly IRTCMediaStreamTrack _track;
        private bool _disposed;

        public DesktopAudioSender(DesktopRTCPeerConnection pc, int sampleRate, int channels)
        {
            if (pc is null) throw new ArgumentNullException(nameof(pc));
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            _source = new MultiMediaAudioSource();
            var sender = pc.AddTrack(_source);
            _track = sender.Track ?? throw new InvalidOperationException("AddTrack did not yield a sendable track.");
        }

        /// <summary>Exists so tests can drive the source directly without a live peer connection.</summary>
        internal MultiMediaAudioSource Source => _source;

        public IRTCMediaStreamTrack Track => _track;

        public void PushPcm(AudioPcmFrame frame)
        {
            if (_disposed) return;
            _source.PushPcm(frame.Pcm);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }
}

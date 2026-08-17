using System;

namespace SpawnDev.RTC.Audio
{
    /// <summary>
    /// Pulls PCM out of a received audio track, so application code (a speech recognizer, a VAD, a
    /// recorder) can consume the remote microphone as samples rather than as an opaque
    /// <see cref="IRTCMediaStreamTrack"/>.
    /// <para>
    /// This is the receive counterpart the raw peer-connection model does not provide. Browser: runs the
    /// received track through a <c>MediaStreamTrackProcessor</c> and reads WebCodecs <c>AudioData</c>
    /// frames. Desktop: taps the SipSorcery RTP receive path and Opus-decodes to PCM. Either way the
    /// samples surface on <see cref="OnPcmFrame"/> at the track's own rate/channel layout.
    /// </para>
    /// </summary>
    public interface IRTCAudioReceiver : IDisposable
    {
        /// <summary>
        /// Raised for each decoded PCM frame as audio arrives. Handlers must not block - on the browser
        /// this fires on the frame-read loop, on desktop on the RTP receive path. The frame's
        /// <see cref="AudioPcmFrame.SampleRate"/> / <see cref="AudioPcmFrame.Channels"/> describe what
        /// the remote sent (WebRTC Opus is 48 kHz).
        /// </summary>
        event Action<AudioPcmFrame>? OnPcmFrame;

        /// <summary>Begins pulling frames. Idempotent.</summary>
        void Start();

        /// <summary>Stops pulling frames without disposing. Idempotent.</summary>
        void Stop();
    }
}

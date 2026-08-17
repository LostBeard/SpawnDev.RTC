using System;

namespace SpawnDev.RTC.Audio
{
    /// <summary>
    /// Cross-platform PCM audio helpers over <see cref="IRTCPeerConnection"/>. They hide the platform
    /// split the same way <see cref="RTCPeerConnectionFactory"/> does: browser uses WebCodecs Insertable
    /// Streams, desktop uses the SipSorcery audio codec path.
    /// </summary>
    public static class RTCAudioExtensions
    {
        /// <summary>
        /// Adds a send-only audio track fed by application PCM (e.g. TTS) and returns the
        /// <see cref="IRTCAudioSender"/> to push frames into. The track is already attached to
        /// <paramref name="pc"/>.
        /// </summary>
        public static IRTCAudioSender AddPcmAudioTrack(this IRTCPeerConnection pc, int sampleRate, int channels)
        {
            if (pc is null) throw new ArgumentNullException(nameof(pc));
            if (OperatingSystem.IsBrowser())
            {
                var sender = new Browser.BrowserAudioSender(sampleRate, channels);
                pc.AddTrack(sender.Track);
                return sender;
            }
            if (pc is Desktop.DesktopRTCPeerConnection desktop)
                return new Desktop.DesktopAudioSender(desktop, sampleRate, channels);
            throw new NotSupportedException($"AddPcmAudioTrack: unsupported peer connection type {pc.GetType().Name}.");
        }

        /// <summary>
        /// Wraps a received audio track (from <see cref="IRTCPeerConnection.OnTrack"/>) as an
        /// <see cref="IRTCAudioReceiver"/> that surfaces decoded PCM frames. Call
        /// <see cref="IRTCAudioReceiver.Start"/> to begin pulling. On desktop the received audio is read
        /// from the peer connection's RTP path, so <paramref name="receivedTrack"/> is not required there.
        /// </summary>
        public static IRTCAudioReceiver ReceivePcmAudio(this IRTCPeerConnection pc, IRTCMediaStreamTrack? receivedTrack = null)
        {
            if (pc is null) throw new ArgumentNullException(nameof(pc));
            if (OperatingSystem.IsBrowser())
            {
                if (receivedTrack is Browser.BrowserRTCMediaStreamTrack browserTrack)
                    return new Browser.BrowserAudioReceiver(browserTrack);
                throw new ArgumentException("On browser, ReceivePcmAudio requires the received BrowserRTCMediaStreamTrack.", nameof(receivedTrack));
            }
            if (pc is Desktop.DesktopRTCPeerConnection desktop)
                return new Desktop.DesktopAudioReceiver(desktop);
            throw new NotSupportedException($"ReceivePcmAudio: unsupported peer connection type {pc.GetType().Name}.");
        }
    }
}

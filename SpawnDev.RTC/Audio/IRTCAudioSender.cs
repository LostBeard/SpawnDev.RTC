using System;

namespace SpawnDev.RTC.Audio
{
    /// <summary>
    /// Sends application-produced PCM (e.g. TTS) to the remote peer as a live audio track.
    /// <para>
    /// This is the piece the raw <see cref="IRTCPeerConnection"/> track model does not provide: a way to
    /// push arbitrary PCM into a sendable <see cref="IRTCMediaStreamTrack"/>. Browser: writes WebCodecs
    /// <c>AudioData</c> frames to a <c>MediaStreamTrackGenerator</c>. Desktop: feeds a SipSorcery
    /// audio source that Opus-encodes and emits RTP. Add <see cref="Track"/> to the connection with
    /// <see cref="IRTCPeerConnection.AddTrack(IRTCMediaStreamTrack, IRTCMediaStream[])"/> (or let the
    /// <c>AddPcmAudioTrack</c> extension do it for you).
    /// </para>
    /// </summary>
    public interface IRTCAudioSender : IDisposable
    {
        /// <summary>The sendable audio track this source drives. Attach it to a peer connection.</summary>
        IRTCMediaStreamTrack Track { get; }

        /// <summary>
        /// Queues one PCM frame for transmission. The frame's rate/channels must match what the sender
        /// was created with (the bridge does not resample). Returns immediately; encoding and RTP happen
        /// on the platform's own media path.
        /// </summary>
        void PushPcm(AudioPcmFrame frame);
    }
}
